using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace Clink.Encode;

internal sealed class VideoTrackDesc
{
    /// <summary>"h264" | "hevc" | "av1" (AV1 is experimental).</summary>
    public string Codec = "h264";
    /// <summary>Alias of <see cref="Codec"/> == "hevc"; setting it only switches between h264 and hevc.</summary>
    public bool Hevc { get => Codec == "hevc"; set { if (value) Codec = "hevc"; else if (Codec == "hevc") Codec = "h264"; } }
    public bool Av1 => Codec == "av1";
    public int Width, Height;
    public byte[] CodecConfig = Array.Empty<byte>(); // avcC, hvcC or av1C payload
    public int Timescale = 90000;

    /// <summary>Sample entry fourcc: avc1 / hvc1 / av01.</summary>
    public string SampleEntry => Av1 ? "av01" : Hevc ? "hvc1" : "avc1";
    /// <summary>Decoder configuration box: avcC / hvcC / av1C.</summary>
    public string ConfigBox => Av1 ? "av1C" : Hevc ? "hvcC" : "avcC";
}

internal sealed class AudioTrackDesc
{
    public string Name = "Audio";
    public byte[] AudioSpecificConfig = Array.Empty<byte>();
    public int SampleRate = 48000;
    public int Channels = 2;
    public int Bitrate = 192000;
    public int SamplesPerFrame = 1024;
}

/// <summary>
/// Minimal ISO BMFF writer: one video track (avc1/hvc1, or av01 for experimental AV1) + any number of AAC audio tracks, with edit lists so
/// every track starts exactly at the clip origin. Samples go to a temp file first; Finish() writes a
/// fast-start file (ftyp, moov, mdat). <see cref="CreateProgressive"/> is the mode for recordings of any length (ftyp, mdat, moov,
/// written in place, with a crash journal).
/// </summary>
internal sealed class Mp4Muxer : IDisposable
{
    private sealed class Track
    {
        public bool IsVideo;
        public VideoTrackDesc? V;
        public AudioTrackDesc? A;
        public int Timescale;
        public readonly List<long> Dts = new();     // in track timescale, relative to first sample
        public readonly List<long> Pts = new();
        public readonly List<uint> Sizes = new();
        public readonly List<long> Offsets = new();  // offset within mdat payload
        public readonly List<int> KeyIndices = new();
        public long FirstPts100ns = long.MinValue;
        public long LastEnd100ns;
    }

    /// <summary>
    /// The compatible brand that says a file's audio edit lists are alignment-true: every track's first sample sits on the movie
    /// timeline to the sample, and the Mix and its Game / Mic / Voice sources are placed as they were recorded (the Mix was mixed
    /// from the sources at whole-sample positions, which the edit lists reproduce). Written by this muxer unless the caller says it
    /// cannot vouch for the timestamps it was given; read by <c>Trimmer.ReadClipTracks</c>. Files without it (v0.2.4 and earlier,
    /// and trims made by them, which lost the offsets) are checked by correlation before a source is added to or taken out of the Mix.
    /// </summary>
    public const string AlignedBrand = "clk1";

    private readonly string _finalPath;
    private readonly string _tempPath;
    private readonly FileStream _temp;
    private readonly Track _video;
    private readonly List<Track> _audio = new();
    private readonly long _origin100ns;
    private readonly bool _aligned;
    private readonly Dictionary<Track, long> _starts = new();   // audio track -> where its first sample sits on the movie timeline, in samples
    private long _payload;
    private bool _finished;

    // Progressive mode (CreateProgressive): the samples go straight into the in-progress file after ftyp and a 64-bit mdat header, a
    // journal beside it records every sample, and Finish only appends the moov. See CreateProgressive.
    private readonly bool _progressive;
    private readonly long _dataStart;           // file offset of the first sample: ftyp + the 16-byte mdat header
    private FileStream? _idx;
    private string? _idxPath;
    private string _actualFinalPath;
    private byte[] _journal = Array.Empty<byte>();
    private int _journalLength;
    private bool _faulted;                      // an add failed half way (progressive mode): the index may list a sample whose bytes are not in the file

    /// <summary>Test seam: called in progressive mode just before a sample's bytes are written.</summary>
    internal Action? BeforeSampleWrite;

    /// <param name="origin100ns">Timeline position that becomes t=0 of the movie.</param>
    /// <param name="aligned">The timestamps of the audio frames are true positions on the movie timeline (a recording; a trim that kept
    /// every track's offset): the file gets <see cref="AlignedBrand"/> and the Mix's sources are placed relative to it exactly.</param>
    public Mp4Muxer(string path, VideoTrackDesc video, IEnumerable<AudioTrackDesc> audio, long origin100ns, bool aligned = true)
        : this(path, path + ".tmp", video, audio, origin100ns, aligned, progressive: false, resume: false)
    {
    }

    private Mp4Muxer(string finalPath, string dataPath, VideoTrackDesc video, IEnumerable<AudioTrackDesc> audio, long origin100ns, bool aligned, bool progressive, bool resume)
    {
        _finalPath = finalPath;
        _actualFinalPath = finalPath;
        _tempPath = dataPath;
        _progressive = progressive;
        // A new recording never replaces a file: an earlier one that was not recovered is somebody's data.
        _temp = new FileStream(_tempPath, resume ? FileMode.Open : progressive ? FileMode.CreateNew : FileMode.Create, FileAccess.ReadWrite, progressive ? FileShare.Read : FileShare.None, 1 << 20);
        _video = new Track { IsVideo = true, V = video, Timescale = video.Timescale };
        foreach (var a in audio) _audio.Add(new Track { A = a, Timescale = a.SampleRate });
        _origin100ns = origin100ns;
        _aligned = aligned;
        if (progressive) _dataStart = BuildFtyp(video, aligned).Length + 16;
    }

    public int AudioTrackCount => _audio.Count;

    /// <summary>Names of the audio tracks the file holds, in file order (a track that got no frames is left out of it).</summary>
    public string[] WrittenAudioTracks => _audio.Where(a => a.Sizes.Count > 0).Select(a => a.A!.Name).ToArray();

    /// <summary>Where the finished file is (progressive mode, after <see cref="Finish"/>): the path asked for, or the first free "name (2)" form of it.</summary>
    public string FinalPath => _actualFinalPath;

    /// <summary>
    /// Raised by <see cref="WriteVideo"/> / <see cref="WriteAudio"/> (progressive mode) before a sample that would not fit the file's
    /// 32-bit durations is added: nothing has been changed, so the muxer can still be finished, and the file holds everything before it.
    /// </summary>
    internal sealed class DurationLimitException : InvalidOperationException
    {
        public DurationLimitException(string message) : base(message) { }
    }

    /// <summary>One second of ticks kept free below the 32-bit limit, for the duration of the last sample.</summary>
    private const int LimitMarginSeconds = 1;

    /// <summary>The longest a track of this timescale can run, in 100 ns, before its mdhd (and the movie's) 32-bit duration would overflow: 13.25 h for the 90 kHz video track.</summary>
    public static long DurationLimit100ns(int timescale) => (uint.MaxValue - (long)timescale * LimitMarginSeconds) * 10_000_000L / timescale;

    /// <summary>The shortest of the limits of this file's tracks (the video's, in practice); a recording should be finished before it is reached. Progressive mode only enforces it.</summary>
    public long DurationLimit() => _audio.Aggregate(DurationLimit100ns(_video.Timescale), (m, a) => Math.Min(m, DurationLimit100ns(a.Timescale)));

    /// <summary>How much video has been written, in 100 ns (the last video sample's time from the first).</summary>
    public long VideoElapsed100ns => _video.FirstPts100ns == long.MinValue ? 0 : _video.LastEnd100ns - _video.FirstPts100ns;

    public void WriteVideo(long pts100ns, long dts100ns, bool keyframe, ReadOnlySpan<byte> lengthPrefixed)
    {
        ThrowIfFaulted();
        var t = _video;
        long first = t.FirstPts100ns == long.MinValue ? dts100ns : t.FirstPts100ns;
        if (_progressive) CheckLimit(t, Math.Max(dts100ns, pts100ns) - first);
        try
        {
            NoteVideo(pts100ns, dts100ns, keyframe);
            Append(t, lengthPrefixed, keyframe ? (byte)1 : (byte)0, pts100ns, dts100ns);
        }
        catch { _faulted = true; throw; }
    }

    /// <summary>After a failed write part of the sample may sit in the file buffer and land on the next flush, ahead of where the journal says the next sample is: nothing more may be written.</summary>
    private void ThrowIfFaulted()
    {
        if (_faulted) throw new InvalidOperationException("Mp4Muxer is faulted after a failed write; Dispose it and use Mp4Muxer.Recover");
    }

    private void NoteVideo(long pts100ns, long dts100ns, bool keyframe)
    {
        var t = _video;
        if (t.FirstPts100ns == long.MinValue) t.FirstPts100ns = dts100ns;
        long rel = dts100ns - t.FirstPts100ns;
        t.Dts.Add(ToScale(rel, t.Timescale));
        t.Pts.Add(ToScale(pts100ns - t.FirstPts100ns, t.Timescale));
        if (keyframe) t.KeyIndices.Add(t.Sizes.Count);
        t.LastEnd100ns = dts100ns;
    }

    public void WriteAudio(int track, long pts100ns, ReadOnlySpan<byte> aacFrame)
    {
        ThrowIfFaulted();
        var t = _audio[track];
        if (_progressive) CheckLimit(t, pts100ns - (t.FirstPts100ns == long.MinValue ? pts100ns : t.FirstPts100ns));
        try
        {
            NoteAudio(t, pts100ns);
            Append(t, aacFrame, 0, pts100ns, pts100ns);
        }
        catch { _faulted = true; throw; }
    }

    private static void NoteAudio(Track t, long pts100ns)
    {
        if (t.FirstPts100ns == long.MinValue) t.FirstPts100ns = pts100ns;
        long rel = pts100ns - t.FirstPts100ns;
        long scaled = ToScale(rel, t.Timescale);
        t.Dts.Add(scaled);
        t.Pts.Add(scaled);
        t.LastEnd100ns = pts100ns;
    }

    private static void CheckLimit(Track t, long rel100ns)
    {
        if (rel100ns > DurationLimit100ns(t.Timescale))
            throw new DurationLimitException($"The {(t.IsVideo ? "video" : t.A!.Name)} track would pass the longest duration the file format holds here ({DurationLimit100ns(t.Timescale) / 10_000_000 / 3600.0:0.00} h)");
    }

    private void Append(Track t, ReadOnlySpan<byte> data, byte flags, long pts100ns, long dts100ns)
    {
        t.Offsets.Add(_payload);
        t.Sizes.Add((uint)data.Length);
        BeforeSampleWrite?.Invoke();
        _temp.Write(data);
        if (_idx != null) Journal(t, flags, pts100ns, dts100ns, (uint)data.Length, _payload);
        _payload += data.Length;
    }

    /// <summary>The same index entry as <see cref="Append"/> makes, for a sample whose bytes are already in the file (recovery).</summary>
    private void AppendIndexOnly(Track t, uint size)
    {
        t.Offsets.Add(_payload);
        t.Sizes.Add(size);
        _payload += size;
    }

    // ---------------------------------------------------------------- progressive mode and its journal

    private const int JournalBuffer = 1 << 16;
    private const int RecordSize = 32;
    private static readonly byte[] JournalMagic = Encoding.ASCII.GetBytes("CLINKIDX");
    private const uint JournalVersion = 1;

    /// <summary>
    /// A muxer for a recording of any length. The samples are written as they arrive to <paramref name="inProgressPath"/>, behind ftyp
    /// (the same brands as a clip's) and an mdat box with the 64-bit header (16 bytes, size patched at the end). A sidecar journal
    /// <paramref name="inProgressPath"/> + ".idx" (<see cref="JournalPath"/>) holds what it takes to build the moov (a header, written
    /// now) and one 32-byte record per sample, so a recording that is never finished is rebuilt by <see cref="Recover"/>. The caller
    /// calls <see cref="Flush"/> now and then (every couple of seconds). <see cref="Finish"/> patches the mdat size, appends the moov
    /// (built once; stco, or co64 when an offset needs it) and renames the file to <paramref name="finalPath"/> (a free "name (2)" form
    /// of it when taken, see <see cref="FinalPath"/>): its cost does not depend on the size of the file. <see cref="Dispose"/> never
    /// deletes the in-progress file or the journal. Same box layout as a clip: the readers cannot tell the two apart.
    /// </summary>
    public static Mp4Muxer CreateProgressive(string inProgressPath, string finalPath, VideoTrackDesc video, IEnumerable<AudioTrackDesc> audio, long origin100ns, bool aligned = true)
    {
        // File.Move across volumes quietly becomes a copy of the whole recording: the point of the mode is that Finish costs nothing.
        string rootA = Path.GetPathRoot(Path.GetFullPath(inProgressPath)) ?? "", rootB = Path.GetPathRoot(Path.GetFullPath(finalPath)) ?? "";
        if (!string.Equals(rootA, rootB, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"The in-progress file ({rootA}) and the final file ({rootB}) must be on the same volume");
        var audioList = audio.ToList();
        // CreateNew throws (and creates nothing) when an earlier recording is still there; after that, only what this call made is removed on failure.
        var m = new Mp4Muxer(finalPath, inProgressPath, video, audioList, origin100ns, aligned, progressive: true, resume: false);
        try
        {
            m.WriteFileHead(0);   // the mdat size is patched in when Finish knows it
            m._journal = new byte[JournalBuffer];
            string idxPath = JournalPath(inProgressPath);
            m._idx = new FileStream(idxPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1);   // buffered by the muxer itself
            m._idxPath = idxPath;
            m.WriteJournalHeader(video, audioList, origin100ns, aligned);
            m.Flush(false);
            return m;
        }
        catch
        {
            bool madeIdx = m._idx != null;
            m._finished = true;   // Dispose must not write anything more
            m.Dispose();
            try { File.Delete(inProgressPath); } catch { }
            if (madeIdx) try { File.Delete(JournalPath(inProgressPath)); } catch { }
            throw;
        }
    }

    /// <summary>Writes ftyp and the 16-byte mdat header (with <paramref name="mdatSize"/> as its 64-bit size) at the start of the data file. Leaves the position behind them.</summary>
    private void WriteFileHead(ulong mdatSize)
    {
        byte[] ftyp = BuildFtyp(_video.V!, _aligned);
        Span<byte> hdr = stackalloc byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(hdr, 1);
        Encoding.ASCII.GetBytes("mdat", hdr.Slice(4));
        BinaryPrimitives.WriteUInt64BigEndian(hdr.Slice(8), mdatSize);
        _temp.Position = 0;
        _temp.Write(ftyp);
        _temp.Write(hdr);
    }

    /// <summary>
    /// Progressive mode: gives the recording up. Closes both files and deletes them (best effort). Nothing is left for <see cref="Recover"/>.
    /// </summary>
    public void Discard()
    {
        if (!_progressive || _finished) return;
        _finished = true;
        try { _temp.Dispose(); } catch { }
        try { _idx?.Dispose(); } catch { }
        _idx = null;
        try { File.Delete(_tempPath); } catch { }
        if (_idxPath != null) try { File.Delete(_idxPath); } catch { }
    }

    /// <summary>Where the journal of an in-progress file is.</summary>
    public static string JournalPath(string inProgressPath) => inProgressPath + ".idx";

    private void WriteJournalHeader(VideoTrackDesc v, List<AudioTrackDesc> audio, long origin100ns, bool aligned)
    {
        var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(aligned);
            w.Write(_dataStart);   // Recover refuses a journal whose ftyp is not the one this build writes
            w.Write(origin100ns);
            w.Write(v.Codec); w.Write(v.Width); w.Write(v.Height); w.Write(v.Timescale);
            w.Write(v.CodecConfig.Length); w.Write(v.CodecConfig);
            w.Write(audio.Count);
            foreach (var a in audio)
            {
                w.Write(a.Name); w.Write(a.SampleRate); w.Write(a.Channels); w.Write(a.Bitrate); w.Write(a.SamplesPerFrame);
                w.Write(a.AudioSpecificConfig.Length); w.Write(a.AudioSpecificConfig);
            }
        }
        byte[] body = ms.ToArray();
        var head = new byte[JournalMagic.Length + 8];
        JournalMagic.CopyTo(head, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(8), JournalVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(12), (uint)body.Length);
        _idx!.Write(head);
        _idx.Write(body);
        Span<byte> sum = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(sum, Fnv(body));
        _idx.Write(sum);
    }

    private static uint Fnv(ReadOnlySpan<byte> bytes)
    {
        uint h = 2166136261;
        foreach (byte b in bytes) h = (h ^ b) * 16777619;
        return h;
    }

    /// <summary>The 16-bit check of a record: of everything but the check itself. A torn or zero-filled record does not pass it.</summary>
    private static ushort RecordCheck(ReadOnlySpan<byte> r)
    {
        uint h = Fnv(r.Slice(4));
        h = (h ^ r[0]) * 16777619;
        h = (h ^ r[1]) * 16777619;
        return (ushort)((h ^ (h >> 16)) ^ 0x5A5A);
    }

    private void Journal(Track t, byte flags, long pts100ns, long dts100ns, uint size, long offset)
    {
        if (_journalLength + RecordSize > _journal.Length) DrainJournal(dataDurable: true);
        var r = _journal.AsSpan(_journalLength, RecordSize);
        r[0] = (byte)(t.IsVideo ? 0 : 1 + _audio.IndexOf(t));
        r[1] = flags;
        BinaryPrimitives.WriteInt64LittleEndian(r.Slice(4), pts100ns);
        BinaryPrimitives.WriteInt64LittleEndian(r.Slice(12), dts100ns);
        BinaryPrimitives.WriteUInt32LittleEndian(r.Slice(20), size);
        BinaryPrimitives.WriteInt64LittleEndian(r.Slice(24), offset);
        BinaryPrimitives.WriteUInt16LittleEndian(r.Slice(2), RecordCheck(r));
        _journalLength += RecordSize;
    }

    /// <summary>Hands the buffered records to the file; the samples they describe go first, so that the journal never runs ahead of the data in what the OS holds.
    /// A non-durable drain protects against a crash of the process only; only a durable flush (<see cref="Flush"/> with true) protects against a power cut
    /// (NTFS may persist the length and the journal pages but not the data pages, which then read back as zeros).</summary>
    private void DrainJournal(bool dataDurable = false)
    {
        if (_idx == null || _journalLength == 0) return;
        _temp.Flush(dataDurable);   // (a full buffer drains durably: the journal must never name bytes the disk does not hold)
        _idx.Write(_journal, 0, _journalLength);
        _journalLength = 0;
    }

    /// <summary>
    /// Progressive mode: pushes the samples and then their journal records out. With <paramref name="durable"/> both are forced to the
    /// disk (FlushFileBuffers), which is what a crash or a power cut cannot take back; without it they are only handed to the OS.
    /// A few seconds apart is the idea. Does nothing for a clip's muxer.
    /// </summary>
    public void Flush(bool durable)
    {
        if (!_progressive || _finished) return;
        _temp.Flush(durable);
        DrainJournal();
        _idx?.Flush(durable);
    }

    private static long ToScale(long v100ns, int timescale) => (long)Math.Round(v100ns * (double)timescale / 10_000_000.0);

    /// <summary>A time in the movie's units (truncated, as the milliseconds always were).</summary>
    private static long ToMovie(long v100ns, int movieTimescale) => v100ns * movieTimescale / 10_000_000;

    /// <summary>The audio rate, as the movie timescale of a file whose tracks start at places that are not whole milliseconds.</summary>
    private const int FineTimescale = 48000;

    public void Finish()
    {
        if (_finished) return;
        if (_progressive)
        {
            if (_faulted) throw new InvalidOperationException("A sample could not be written: the recording is not whole and cannot be finished here. Recover it from its journal (Mp4Muxer.Recover).");
            FinishProgressive();
            return;
        }
        _temp.Flush();

        var tracks = new List<Track> { _video };
        tracks.AddRange(_audio.Where(a => a.Sizes.Count > 0));
        PlaceAudioTracks(tracks);

        // The movie counts in milliseconds, which is also what an empty edit (a track that starts after the origin) counts in. A track
        // that starts a number of samples after the origin that is not a whole number of milliseconds would be off by up to one, so a
        // file with such a track counts in 1/48000 s instead and the empty edit is exactly as long as the track starts late. Any
        // other file is written as ever. (The rest of a millisecond cannot be made up with a media time after the empty edit: Media
        // Foundation's MPEG-4 source on Windows 11 then drops that many samples from the start of the track, where it ignores the
        // media time of a single edit. Clink's own readers do not depend on any of that: they read a clip with its audio edit lists
        // hidden from Media Foundation, see ClipSource. Other players see the edit lists, so they are written as exactly as they can be.)
        int movieTimescale = MovieTimescale();

        // Fix the last sample duration of each track (needed for stts) and compute durations.
        long movieDuration = MovieDuration(tracks, movieTimescale);

        byte[] ftyp = BuildFtyp(_video.V!, _aligned);
        // moov size depends on chunk offsets which depend on moov size: compute with a placeholder first, then rebuild with
        // real offsets. 32-bit offsets (stco) only when the whole file, moov included (several MB for a long clip), stays
        // under 4 GiB; co64 otherwise.
        byte[] moov = BuildMoov(tracks, movieTimescale, movieDuration, 0, false);
        bool use64 = ftyp.Length + moov.Length + 8L + _payload > uint.MaxValue;
        if (use64) moov = BuildMoov(tracks, movieTimescale, movieDuration, 0, true);
        long mdatHeaderSize = use64 ? 16 : 8;
        long mdatPayloadStart = ftyp.Length + moov.Length + mdatHeaderSize;
        moov = BuildMoov(tracks, movieTimescale, movieDuration, mdatPayloadStart, use64);
        if (ftyp.Length + moov.Length + mdatHeaderSize != mdatPayloadStart) throw new InvalidOperationException("moov size changed");

        // Written under a temporary name and renamed once complete, so a failure (disk full, access denied, the name taken)
        // never leaves a half-written clip under the real name.
        string part = _finalPath + ".part";
        try
        {
            using (var outFs = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            {
                outFs.Write(ftyp);
                outFs.Write(moov);
                Span<byte> hdr = stackalloc byte[16];
                if (use64)
                {
                    BinaryPrimitives.WriteUInt32BigEndian(hdr, 1);
                    Encoding.ASCII.GetBytes("mdat", hdr.Slice(4));
                    BinaryPrimitives.WriteUInt64BigEndian(hdr.Slice(8), (ulong)(_payload + 16));
                    outFs.Write(hdr);
                }
                else
                {
                    BinaryPrimitives.WriteUInt32BigEndian(hdr, (uint)(_payload + 8));
                    Encoding.ASCII.GetBytes("mdat", hdr.Slice(4));
                    outFs.Write(hdr.Slice(0, 8));
                }
                _temp.Position = 0;
                _temp.CopyTo(outFs);
            }
            File.Move(part, _finalPath, overwrite: false);
        }
        catch
        {
            try { File.Delete(part); } catch { }
            throw;
        }
        _finished = true;
        _temp.Dispose();
        try { File.Delete(_tempPath); } catch { }
    }

    /// <summary>The movie's timescale (call after <see cref="PlaceAudioTracks"/>); see the long comment in <see cref="Finish"/>.</summary>
    private int MovieTimescale() => _audio.Any(a => _starts.TryGetValue(a, out long s) && s > 0 && a.Timescale == FineTimescale && s % (FineTimescale / 1000) != 0) ? FineTimescale : 1000;

    private long MovieDuration(List<Track> tracks, int movieTimescale)
    {
        long movieDuration = 0;
        foreach (var t in tracks) movieDuration = Math.Max(movieDuration, ToMovie(TrackEnd100ns(t) - _origin100ns, movieTimescale));
        return movieDuration;
    }

    /// <summary>The moov of a progressive recording whose first sample is at <paramref name="dataStart"/> (stco, or co64 when a sample starts past 4 GiB). Internal for the tests, which place it far out.</summary>
    internal byte[] BuildProgressiveMoov(long dataStart, out bool use64, out long duration100ns)
    {
        var tracks = new List<Track> { _video };
        tracks.AddRange(_audio.Where(a => a.Sizes.Count > 0));
        PlaceAudioTracks(tracks);
        int movieTimescale = MovieTimescale();
        long movieDuration = MovieDuration(tracks, movieTimescale);
        duration100ns = Math.Max(0, tracks.Max(t => TrackEnd100ns(t) - _origin100ns));
        long lastOffset = tracks.Where(t => t.Offsets.Count > 0).Select(t => dataStart + t.Offsets[^1]).DefaultIfEmpty(dataStart).Max();
        use64 = lastOffset > uint.MaxValue;
        return BuildMoov(tracks, movieTimescale, movieDuration, dataStart, use64);
    }

    /// <summary>
    /// Progressive mode: rewrites the file's head and the mdat size, appends the moov (built once, the layout of <see cref="Finish"/>'s
    /// clips: the sample offsets are known already) and moves the file to its final name. Nothing is copied. The journal goes when the
    /// file is in place; a failure on the way leaves the in-progress file (the data and the journal are not touched, though the moov
    /// may be on the end of it) and the journal for <see cref="Recover"/>.
    /// </summary>
    /// <returns>The length of the movie, in 100 ns.</returns>
    private long FinishProgressive()
    {
        if (_video.Sizes.Count == 0) throw new InvalidOperationException("Nothing recorded");   // before any file is touched
        DrainJournal();
        _idx?.Flush(true);   // the journal must be whole on the disk before the data file is changed: a short one would make Recover cut a complete file back
        byte[] moov = BuildProgressiveMoov(_dataStart, out _, out long duration100ns);

        long end = _dataStart + _payload;
        _temp.Flush();
        _temp.SetLength(end);                      // nothing after the last sample (a moov from an earlier try, junk)
        WriteFileHead((ulong)(_payload + 16));     // all of it: after a power cut the first bytes may be zeros
        _temp.Position = end;
        _temp.Write(moov);
        _temp.Flush(true);
        _temp.Dispose();
        _idx?.Dispose();
        _idx = null;
        _actualFinalPath = MoveUnique(_tempPath, _finalPath);
        _finished = true;
        try { if (_idxPath != null) File.Delete(_idxPath); } catch { }
        return duration100ns;
    }

    /// <summary>A sharing violation or access denied: an indexer or a virus scanner that has just got hold of a big file lets go within seconds. Fields so that a test can shrink the wait.</summary>
    internal static int MoveAttempts = 10, MovePauseMs = 300;

    /// <summary>Moves <paramref name="from"/> to <paramref name="to"/>, or to "name (2).ext" and so on when that is taken; never replaces a file. Returns where it went.</summary>
    private static string MoveUnique(string from, string to)
    {
        string dir = Path.GetDirectoryName(to) ?? "", name = Path.GetFileNameWithoutExtension(to), ext = Path.GetExtension(to);
        string target = to;
        int names = 2, tries = 0;
        while (true)
        {
            try
            {
                File.Move(from, target, overwrite: false);
                return target;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // UnauthorizedAccessException counts as a transient "busy" too: an ACL denial, or a directory with the target's name, waits the attempts out and throws without walking to "(2)".
                bool busy = ex is UnauthorizedAccessException || ex.HResult == unchecked((int)0x80070020);
                if (busy && ++tries < MoveAttempts) { Thread.Sleep(MovePauseMs); continue; }
                if (!busy && ex is IOException && File.Exists(target) && names < 1000) { target = Path.Combine(dir, $"{name} ({names++}){ext}"); continue; }
                throw;
            }
        }
    }

    /// <summary>What <see cref="Recover"/> made of an unfinished recording.</summary>
    internal sealed record RecoveryResult(string Path, long Duration100ns, long SizeBytes, string[] AudioTracks, int VideoSamples);

    /// <summary>
    /// Finishes a progressive recording that never was: replays the journal at <paramref name="idxPath"/>, keeps the samples that are
    /// whole in both the journal and <paramref name="inProgressPath"/> (a torn last record, a sample the data file stops inside, and
    /// everything after, are dropped), cuts the data file after the last of them, appends the moov and moves the file to
    /// <paramref name="finalPath"/> (a free "name (2)" form when taken). The journal is deleted once the file is in place.
    /// </summary>
    /// <returns>The recovered file, or null when nothing is recoverable (no complete video sample, a journal that is not one, or one
    /// written for a different file head than this build writes): both files are then left exactly as they were. An error on the way
    /// out (I/O) can leave the in-progress file changed (its head rewritten, a moov on its end) but whole and still recoverable, the
    /// journal untouched; Recover can be run again.</returns>
    public static RecoveryResult? Recover(string inProgressPath, string idxPath, string finalPath)
    {
        if (!File.Exists(inProgressPath) || !File.Exists(idxPath)) return null;
        Mp4Muxer? mux = null;
        try
        {
            using (var idx = new FileStream(idxPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16))
            {
                var made = ReadJournalHeader(idx);
                if (made == null) return null;
                var (video, audio, origin, aligned, dataStart) = made.Value;
                mux = new Mp4Muxer(finalPath, inProgressPath, video, audio, origin, aligned, progressive: true, resume: true);
                mux._idxPath = idxPath;
                if (dataStart != mux._dataStart) return null;   // another layout of the head than this build's
                long dataLength = mux._temp.Length;
                // A failed earlier run may have appended a moov after the last whole sample: the mdat size it wrote bounds the samples.
                if (dataLength >= dataStart && dataStart >= 16)
                {
                    Span<byte> mh = stackalloc byte[16];
                    mux._temp.Position = dataStart - 16;
                    if (mux._temp.ReadAtLeast(mh, 16, throwOnEndOfStream: false) == 16 && BinaryPrimitives.ReadUInt32BigEndian(mh) == 1 && mh.Slice(4, 4).SequenceEqual("mdat"u8))
                    {
                        ulong large = BinaryPrimitives.ReadUInt64BigEndian(mh.Slice(8));
                        if (large != 0 && large <= long.MaxValue - (ulong)(dataStart - 16)) dataLength = Math.Min(dataLength, dataStart - 16 + (long)large);
                    }
                }
                Span<byte> r = stackalloc byte[RecordSize];
                while (idx.ReadAtLeast(r, RecordSize, throwOnEndOfStream: false) == RecordSize)
                {
                    if (BinaryPrimitives.ReadUInt16LittleEndian(r.Slice(2)) != RecordCheck(r)) break;
                    int which = r[0];
                    uint size = BinaryPrimitives.ReadUInt32LittleEndian(r.Slice(20));
                    long offset = BinaryPrimitives.ReadInt64LittleEndian(r.Slice(24));
                    if (which > mux._audio.Count || offset != mux._payload || mux._dataStart + offset + size > dataLength) break;
                    long pts = BinaryPrimitives.ReadInt64LittleEndian(r.Slice(4)), dts = BinaryPrimitives.ReadInt64LittleEndian(r.Slice(12));
                    if (which == 0)
                    {
                        mux.NoteVideo(pts, dts, (r[1] & 1) != 0);
                        mux.AppendIndexOnly(mux._video, size);
                    }
                    else
                    {
                        var t = mux._audio[which - 1];
                        NoteAudio(t, pts);
                        mux.AppendIndexOnly(t, size);
                    }
                }
            }
            if (mux._video.Sizes.Count == 0) return null;
            string[] names = mux.WrittenAudioTracks;
            int videoSamples = mux._video.Sizes.Count;
            long duration = mux.FinishProgressive();
            return new RecoveryResult(mux._actualFinalPath, duration, new FileInfo(mux._actualFinalPath).Length, names, videoSamples);
        }
        finally { mux?.Dispose(); }
    }

    /// <summary>What a journal file holds, see <see cref="InspectJournal"/>.</summary>
    internal enum JournalState { NotAJournal, Empty, HasSamples }

    /// <summary>
    /// Looks into the journal at <paramref name="idxPath"/> without changing anything. <see cref="JournalState.Empty"/>: a valid header and not one
    /// whole sample record (a recording that died in its first second, before the first flush). <paramref name="dataStart"/> is where the
    /// samples begin in the data file (the head's size; 0 when not a journal). I/O errors (the file is in use) propagate.
    /// </summary>
    public static JournalState InspectJournal(string idxPath, out long dataStart)
    {
        dataStart = 0;
        using var idx = new FileStream(idxPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 12);
        var made = ReadJournalHeader(idx);
        if (made == null) return JournalState.NotAJournal;
        dataStart = made.Value.dataStart;
        Span<byte> r = stackalloc byte[RecordSize];
        if (idx.ReadAtLeast(r, RecordSize, throwOnEndOfStream: false) < RecordSize || BinaryPrimitives.ReadUInt16LittleEndian(r.Slice(2)) != RecordCheck(r)) return JournalState.Empty;
        return JournalState.HasSamples;
    }

    /// <summary>The file starts with the journal's magic ("CLINKIDX"): it is one of ours (even if its header is torn).</summary>
    public static bool HasJournalMagic(string idxPath)
    {
        using var f = new FileStream(idxPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 12);
        Span<byte> head = stackalloc byte[8];
        return f.ReadAtLeast(head, 8, throwOnEndOfStream: false) == 8 && head.SequenceEqual(JournalMagic);
    }

    private static (VideoTrackDesc video, List<AudioTrackDesc> audio, long origin, bool aligned, long dataStart)? ReadJournalHeader(Stream s)
    {
        try { return ReadJournalHeaderCore(s); }
        catch (Exception ex) when (ex is EndOfStreamException or InvalidDataException or ArgumentException or DecoderFallbackException)
        {
            return null;   // a journal that is not one (the files stay)
        }
    }

    private static (VideoTrackDesc video, List<AudioTrackDesc> audio, long origin, bool aligned, long dataStart)? ReadJournalHeaderCore(Stream s)
    {
        Span<byte> head = stackalloc byte[16];
        if (s.ReadAtLeast(head, 16, throwOnEndOfStream: false) < 16 || !head.Slice(0, 8).SequenceEqual(JournalMagic)) return null;
        if (BinaryPrimitives.ReadUInt32LittleEndian(head.Slice(8)) != JournalVersion) return null;
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(head.Slice(12));
        if (length > 1 << 24) return null;
        var body = new byte[length];
        Span<byte> sum = stackalloc byte[4];
        if (s.ReadAtLeast(body, body.Length, throwOnEndOfStream: false) < body.Length || s.ReadAtLeast(sum, 4, throwOnEndOfStream: false) < 4) return null;
        if (BinaryPrimitives.ReadUInt32LittleEndian(sum) != Fnv(body)) return null;
        using var r = new BinaryReader(new MemoryStream(body), Encoding.UTF8);
        bool aligned = r.ReadBoolean();
        long dataStart = r.ReadInt64();
        long origin = r.ReadInt64();
        var v = new VideoTrackDesc { Codec = r.ReadString(), Width = r.ReadInt32(), Height = r.ReadInt32(), Timescale = r.ReadInt32() };
        v.CodecConfig = ReadBlock(r);
        int n = r.ReadInt32();
        var audio = new List<AudioTrackDesc>();
        for (int i = 0; i < n; i++)
        {
            var a = new AudioTrackDesc { Name = r.ReadString(), SampleRate = r.ReadInt32(), Channels = r.ReadInt32(), Bitrate = r.ReadInt32(), SamplesPerFrame = r.ReadInt32() };
            a.AudioSpecificConfig = ReadBlock(r);
            audio.Add(a);
        }
        return (v, audio, origin, aligned, dataStart);
    }

    /// <summary>A length-prefixed block of the journal header; a length that does not fit what is left is a journal that is not one.</summary>
    private static byte[] ReadBlock(BinaryReader r)
    {
        int count = r.ReadInt32();
        if (count < 0 || count > r.BaseStream.Length - r.BaseStream.Position) throw new InvalidDataException("journal block");
        return r.ReadBytes(count);
    }

    private long TrackEnd100ns(Track t)
    {
        if (t.Sizes.Count == 0) return _origin100ns;
        long lastDur = t.IsVideo ? LastFrameDuration100ns(t) : t.A!.SamplesPerFrame * 10_000_000L / t.A.SampleRate;
        return t.LastEnd100ns + lastDur;
    }

    /// <summary>
    /// The estimated duration of a video track's last sample. In progressive mode it is held to the margin <see cref="CheckLimit"/>
    /// keeps free (a recording whose last two frames are hours apart would otherwise write a duration that wraps); a clip is as ever.
    /// </summary>
    private long LastFrameDuration100ns(Track t)
    {
        long d = FrameDuration100ns(t);
        return _progressive ? Math.Min(d, LimitMarginSeconds * 10_000_000L) : d;
    }

    private static long FrameDuration100ns(Track t)
    {
        if (t.Dts.Count < 2) return 166_667;
        // median delta
        var deltas = new List<long>();
        for (int i = 1; i < Math.Min(t.Dts.Count, 200); i++) deltas.Add(t.Dts[i] - t.Dts[i - 1]);
        deltas.Sort();
        long d = deltas[deltas.Count / 2];
        return d * 10_000_000L / t.Timescale;
    }

    // ---------------------------------------------------------------- boxes

    private static byte[] BuildFtyp(VideoTrackDesc v, bool aligned)
    {
        var b = new Box("ftyp");
        b.Fourcc("isom"); b.U32(0x200);
        b.Fourcc("isom"); b.Fourcc("iso2"); b.Fourcc(v.SampleEntry); b.Fourcc("mp41");
        if (aligned) b.Fourcc(AlignedBrand);
        return b.ToArray();
    }

    /// <summary>
    /// Decides where each audio track's first sample goes on the movie timeline, in whole samples (<see cref="_starts"/>: negative = the
    /// first samples are before the movie origin and skipped, positive = the track starts after it). Each track on its own is the
    /// position of its first frame rounded to a sample. The Mix, though, was mixed from its sources at whole-sample positions, so with
    /// an aligned file the sources are put at the Mix's position minus the whole-sample lag it was mixed at: rounding each track by
    /// itself would put a source one sample off the Mix a quarter of the time, enough to leave the high end of a source in the Mix
    /// when it is taken out again.
    /// </summary>
    private void PlaceAudioTracks(List<Track> tracks)
    {
        var audio = tracks.Where(t => !t.IsVideo).ToList();
        if (audio.Count == 0) return;
        var real = audio.Select(t => (t.FirstPts100ns - _origin100ns) * (double)t.Timescale / 10_000_000.0).ToArray();
        var start = real.Select(r => (long)Math.Round(r, MidpointRounding.AwayFromZero)).ToArray();
        if (_aligned)
        {
            var roles = Engine.MixPlan.Roles(audio.Select(t => t.A!.Name).ToArray(), audio.Count);
            if (roles[0] == Engine.TrackRole.Mix)
            {
                for (int i = 1; i < roles.Length; i++)
                    if (roles[i] is Engine.TrackRole.Game or Engine.TrackRole.Mic or Engine.TrackRole.Voice)
                        start[i] = start[0] - (long)Math.Round(real[0] - real[i], MidpointRounding.AwayFromZero);
            }
        }
        for (int i = 0; i < audio.Count; i++) _starts[audio[i]] = start[i];
    }

    private byte[] BuildMoov(List<Track> tracks, int movieTimescale, long movieDuration, long mdatPayloadStart, bool use64)
    {
        var moov = new Box("moov");
        // mvhd
        var mvhd = new Box("mvhd", version: 0, flags: 0);
        mvhd.U32(0); mvhd.U32(0); // creation/modification
        mvhd.U32((uint)movieTimescale);
        mvhd.U32((uint)movieDuration);
        mvhd.U32(0x00010000); // rate 1.0
        mvhd.U16(0x0100);     // volume
        mvhd.U16(0); mvhd.U32(0); mvhd.U32(0);
        WriteMatrix(mvhd);
        for (int i = 0; i < 6; i++) mvhd.U32(0);
        mvhd.U32((uint)(tracks.Count + 1)); // next track id
        moov.Child(mvhd);

        uint trackId = 1;
        bool firstAudio = true;
        foreach (var t in tracks)
        {
            // Only the first audio track (normally Mix) is enabled; the others are disabled alternates of it, as ffmpeg writes
            // them. Players that honour the flags (QuickTime, Safari, iOS) would otherwise play every audio track at once.
            bool enabled = t.IsVideo || firstAudio;
            if (!t.IsVideo) firstAudio = false;
            moov.Child(BuildTrak(t, trackId++, enabled, movieTimescale, mdatPayloadStart, use64));
        }
        return moov.ToArray();
    }

    private Box BuildTrak(Track t, uint trackId, bool enabled, int movieTimescale, long mdatPayloadStart, bool use64)
    {
        var trak = new Box("trak");

        long firstPts100ns = t.FirstPts100ns;
        long trackEnd100ns = TrackEnd100ns(t);
        long mediaDuration = ToScale(trackEnd100ns - firstPts100ns, t.Timescale);

        // tkhd
        var tkhd = new Box("tkhd", 0, enabled ? 0x3u : 0x2u); // (enabled +) in movie
        tkhd.U32(0); tkhd.U32(0);
        tkhd.U32(trackId);
        tkhd.U32(0);
        tkhd.U32((uint)Math.Max(0, ToMovie(trackEnd100ns - _origin100ns, movieTimescale))); // duration in movie timescale
        tkhd.U32(0); tkhd.U32(0);
        tkhd.U16(0); tkhd.U16((ushort)(t.IsVideo ? 0 : 1)); // layer, alternate_group (the audio tracks are alternatives)
        tkhd.U16((ushort)(t.IsVideo ? 0 : 0x0100)); tkhd.U16(0);
        WriteMatrix(tkhd);
        tkhd.U32(t.IsVideo ? (uint)t.V!.Width << 16 : 0);
        tkhd.U32(t.IsVideo ? (uint)t.V!.Height << 16 : 0);
        trak.Child(tkhd);

        // edts/elst: align the track's first sample to the movie origin.
        var elst = new Box("elst", 0, 0);
        long startOffset100ns = firstPts100ns - _origin100ns; // >0: track starts late (empty edit); <0: skip into media
        if (!t.IsVideo && t.Timescale == FineTimescale && _starts.TryGetValue(t, out long startSamples))
        {
            // Audio, to the sample. A track that starts before the origin skips that many samples of its media (one edit, whose media
            // time Windows 11's Media Foundation ignores and Windows Server 2025's applies: Clink's readers hide the edit list from it
            // and put the track where its offset says, see ClipSource). One that starts after it gets an empty edit exactly as long as it
            // starts late, then its media from the start: a media time of 0, never the rest of a millisecond (see Finish).
            if (startSamples > 0)
            {
                elst.U32(2);
                elst.U32((uint)(startSamples * movieTimescale / FineTimescale)); elst.U32(0xFFFFFFFF); elst.U16(1); elst.U16(0); // empty edit
                elst.U32((uint)Math.Max(0, ToMovie(trackEnd100ns - firstPts100ns, movieTimescale))); elst.U32(0); elst.U16(1); elst.U16(0);
            }
            else
            {
                elst.U32(1);
                elst.U32((uint)Math.Max(0, ToMovie(trackEnd100ns - _origin100ns, movieTimescale))); elst.U32((uint)(-startSamples)); elst.U16(1); elst.U16(0);
            }
        }
        else if (startOffset100ns > 0)
        {
            elst.U32(2);
            elst.U32((uint)ToMovie(startOffset100ns, movieTimescale)); elst.U32(0xFFFFFFFF); elst.U16(1); elst.U16(0); // empty edit
            elst.U32((uint)Math.Max(0, ToMovie(trackEnd100ns - firstPts100ns, movieTimescale))); elst.U32(0); elst.U16(1); elst.U16(0);
        }
        else
        {
            long mediaTime = ToScale(-startOffset100ns, t.Timescale);
            elst.U32(1);
            elst.U32((uint)Math.Max(0, ToMovie(trackEnd100ns - _origin100ns, movieTimescale))); elst.U32((uint)mediaTime); elst.U16(1); elst.U16(0);
        }
        var edts = new Box("edts"); edts.Child(elst); trak.Child(edts);

        // mdia
        var mdia = new Box("mdia");
        var mdhd = new Box("mdhd", 0, 0);
        mdhd.U32(0); mdhd.U32(0);
        mdhd.U32((uint)t.Timescale);
        mdhd.U32((uint)mediaDuration);
        mdhd.U16(0x55C4); // 'und'
        mdhd.U16(0);
        mdia.Child(mdhd);

        var hdlr = new Box("hdlr", 0, 0);
        hdlr.U32(0);
        hdlr.Fourcc(t.IsVideo ? "vide" : "soun");
        hdlr.U32(0); hdlr.U32(0); hdlr.U32(0);
        hdlr.Utf8Z(t.IsVideo ? "Video" : t.A!.Name);
        mdia.Child(hdlr);

        var minf = new Box("minf");
        if (t.IsVideo)
        {
            var vmhd = new Box("vmhd", 0, 1); vmhd.U16(0); vmhd.U16(0); vmhd.U16(0); vmhd.U16(0); minf.Child(vmhd);
        }
        else
        {
            var smhd = new Box("smhd", 0, 0); smhd.U16(0); smhd.U16(0); minf.Child(smhd);
        }
        var dinf = new Box("dinf");
        var dref = new Box("dref", 0, 0); dref.U32(1);
        var url = new Box("url ", 0, 1); dref.Child(url);
        dinf.Child(dref); minf.Child(dinf);

        minf.Child(BuildStbl(t, mdatPayloadStart, use64));
        mdia.Child(minf);
        trak.Child(mdia);
        return trak;
    }

    private Box BuildStbl(Track t, long mdatPayloadStart, bool use64)
    {
        var stbl = new Box("stbl");

        // stsd
        var stsd = new Box("stsd", 0, 0); stsd.U32(1);
        if (t.IsVideo)
        {
            var v = t.V!;
            var entry = new Box(v.SampleEntry);         // avc1 / hvc1 / av01 VisualSampleEntry
            entry.Zero(6); entry.U16(1);                // reserved, data_reference_index
            entry.U16(0); entry.U16(0); entry.Zero(12); // pre_defined/reserved
            entry.U16((ushort)v.Width); entry.U16((ushort)v.Height);
            entry.U32(0x00480000); entry.U32(0x00480000);
            entry.U32(0);
            entry.U16(1);                               // frame_count
            var name = new byte[32]; var nm = Encoding.ASCII.GetBytes("Clink"); name[0] = (byte)nm.Length; nm.CopyTo(name, 1); entry.Bytes(name);
            entry.U16(0x0018); entry.I16(-1);
            var cfg = new Box(v.ConfigBox); cfg.Bytes(v.CodecConfig); entry.Child(cfg);
            var colr = new Box("colr"); colr.Fourcc("nclx"); colr.U16(1); colr.U16(1); colr.U16(1); colr.U8(0); entry.Child(colr);
            var pasp = new Box("pasp"); pasp.U32(1); pasp.U32(1); entry.Child(pasp);
            stsd.Child(entry);
        }
        else
        {
            var a = t.A!;
            var entry = new Box("mp4a");
            entry.Zero(6); entry.U16(1);
            entry.U32(0); entry.U32(0);                 // reserved
            entry.U16((ushort)a.Channels); entry.U16(16);
            entry.U16(0); entry.U16(0);
            entry.U32((uint)a.SampleRate << 16);
            entry.Child(BuildEsds(a));
            stsd.Child(entry);
        }
        stbl.Child(stsd);

        // stts (run-length of deltas)
        int n = t.Sizes.Count;
        var stts = new Box("stts", 0, 0);
        var runs = new List<(long delta, uint count)>();
        if (n > 0)
        {
            long defaultDelta = t.IsVideo ? ToScale(LastFrameDuration100ns(t), t.Timescale) : t.A!.SamplesPerFrame;
            for (int i = 0; i < n; i++)
            {
                long delta = i + 1 < n ? t.Dts[i + 1] - t.Dts[i] : defaultDelta;
                if (delta <= 0) delta = 1;
                if (runs.Count > 0 && runs[^1].delta == delta) runs[^1] = (delta, runs[^1].count + 1);
                else runs.Add((delta, 1));
            }
        }
        stts.U32((uint)runs.Count);
        foreach (var (delta, count) in runs) { stts.U32(count); stts.U32((uint)delta); }
        stbl.Child(stts);

        if (t.IsVideo)
        {
            var stss = new Box("stss", 0, 0);
            stss.U32((uint)t.KeyIndices.Count);
            foreach (var k in t.KeyIndices) stss.U32((uint)(k + 1));
            stbl.Child(stss);

            bool needCtts = false;
            for (int i = 0; i < n; i++) if (t.Pts[i] != t.Dts[i]) { needCtts = true; break; }
            if (needCtts)
            {
                var ctts = new Box("ctts", 1, 0);
                var cruns = new List<(long off, uint count)>();
                for (int i = 0; i < n; i++)
                {
                    long off = t.Pts[i] - t.Dts[i];
                    if (cruns.Count > 0 && cruns[^1].off == off) cruns[^1] = (off, cruns[^1].count + 1);
                    else cruns.Add((off, 1));
                }
                ctts.U32((uint)cruns.Count);
                foreach (var (off, count) in cruns) { ctts.U32(count); ctts.I32((int)off); }
                stbl.Child(ctts);
            }
        }

        // stsc: one sample per chunk
        var stsc = new Box("stsc", 0, 0);
        stsc.U32(1); stsc.U32(1); stsc.U32(1); stsc.U32(1);
        stbl.Child(stsc);

        var stsz = new Box("stsz", 0, 0);
        stsz.U32(0); stsz.U32((uint)n);
        foreach (var s in t.Sizes) stsz.U32(s);
        stbl.Child(stsz);

        if (use64)
        {
            var co64 = new Box("co64", 0, 0);
            co64.U32((uint)n);
            foreach (var o in t.Offsets) co64.U64((ulong)(mdatPayloadStart + o));
            stbl.Child(co64);
        }
        else
        {
            var stco = new Box("stco", 0, 0);
            stco.U32((uint)n);
            foreach (var o in t.Offsets) stco.U32((uint)(mdatPayloadStart + o));
            stbl.Child(stco);
        }
        return stbl;
    }

    private static Box BuildEsds(AudioTrackDesc a)
    {
        var esds = new Box("esds", 0, 0);
        byte[] asc = a.AudioSpecificConfig;
        // DecoderSpecificInfo
        var dsi = new List<byte> { 0x05 }; dsi.AddRange(DescLen(asc.Length)); dsi.AddRange(asc);
        // DecoderConfigDescriptor
        var dcd = new List<byte> { 0x04 };
        var dcdBody = new List<byte> { 0x40, 0x15, 0, 0x06, 0x00 }; // objectType AAC, streamType audio, bufferSize 1536
        dcdBody.AddRange(BE32((uint)Math.Max(a.Bitrate, 128000)));
        dcdBody.AddRange(BE32((uint)a.Bitrate));
        dcdBody.AddRange(dsi);
        dcd.AddRange(DescLen(dcdBody.Count)); dcd.AddRange(dcdBody);
        // SLConfigDescriptor
        var sl = new byte[] { 0x06, 0x01, 0x02 };
        // ES_Descriptor
        var esBody = new List<byte> { 0, 1, 0 }; // ES_ID = 1, flags
        esBody.AddRange(dcd); esBody.AddRange(sl);
        var es = new List<byte> { 0x03 }; es.AddRange(DescLen(esBody.Count)); es.AddRange(esBody);
        esds.Bytes(es.ToArray());
        return esds;

        static IEnumerable<byte> DescLen(int len) => new[] { (byte)(0x80 | ((len >> 21) & 0x7F)), (byte)(0x80 | ((len >> 14) & 0x7F)), (byte)(0x80 | ((len >> 7) & 0x7F)), (byte)(len & 0x7F) };
        static IEnumerable<byte> BE32(uint v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };
    }

    private static void WriteMatrix(Box b)
    {
        b.U32(0x00010000); b.U32(0); b.U32(0);
        b.U32(0); b.U32(0x00010000); b.U32(0);
        b.U32(0); b.U32(0); b.U32(0x40000000);
    }

    public void Dispose()
    {
        if (_progressive)
        {
            // An unfinished recording is kept, with its journal, for Recover; what the muxer still holds in memory goes to them first.
            if (!_finished) try { Flush(false); } catch { }
            try { _temp.Dispose(); } catch { }
            try { _idx?.Dispose(); } catch { }
            return;
        }
        try { _temp.Dispose(); } catch { }
        try { File.Delete(_tempPath); } catch { }
    }

    /// <summary>Big-endian box builder.</summary>
    private sealed class Box
    {
        private readonly MemoryStream _ms = new();
        private readonly string _type;
        public Box(string type) { _type = type; }
        public Box(string type, byte version, uint flags) : this(type) { U8(version); U8((byte)(flags >> 16)); U8((byte)(flags >> 8)); U8((byte)flags); }
        public void U8(byte v) => _ms.WriteByte(v);
        public void U16(ushort v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, v); _ms.Write(b); }
        public void I16(short v) => U16((ushort)v);
        public void U32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); _ms.Write(b); }
        public void I32(int v) => U32((uint)v);
        public void U64(ulong v) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteUInt64BigEndian(b, v); _ms.Write(b); }
        public void Fourcc(string s) => _ms.Write(Encoding.ASCII.GetBytes(s));
        public void Bytes(byte[] b) => _ms.Write(b);
        public void Zero(int n) { for (int i = 0; i < n; i++) _ms.WriteByte(0); }
        public void Utf8Z(string s) { _ms.Write(Encoding.UTF8.GetBytes(s)); _ms.WriteByte(0); }
        public void Child(Box b) => _ms.Write(b.ToArray());
        public byte[] ToArray()
        {
            var body = _ms.ToArray();
            var o = new byte[8 + body.Length];
            BinaryPrimitives.WriteUInt32BigEndian(o, (uint)o.Length);
            Encoding.ASCII.GetBytes(_type, o.AsSpan(4, 4));
            body.CopyTo(o, 8);
            return o;
        }
    }
}
