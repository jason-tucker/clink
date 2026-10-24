using System.IO;
using Clink.Audio;
using Clink.Encode;

namespace Clink.Engine;

/// <summary>Where a <see cref="ClipMuxWriter"/> puts its file: a clip (written whole by Finish) or a recording (written as it goes, see <see cref="Mp4Muxer.CreateProgressive"/>).</summary>
internal readonly record struct ClipOutput(string Path, string? FinalPath)
{
    /// <summary>A clip: <paramref name="path"/> appears when the writer is finished.</summary>
    public static ClipOutput FastStart(string path) => new(path, null);
    /// <summary>A recording: samples go to <paramref name="inProgressPath"/> (with a journal beside it); Finish moves it to <paramref name="finalPath"/> (same volume).</summary>
    public static ClipOutput Progressive(string inProgressPath, string finalPath) => new(inProgressPath, finalPath);
    public bool IsProgressive => FinalPath != null;
}

/// <param name="Codec">"h264" | "hevc" | "av1".</param>
/// <param name="Fps">The nominal frame rate the capture times are snapped to; 0 keeps them (made strictly increasing).</param>
/// <param name="SequenceHeader">Encoder-provided parameter sets (Annex B) or AV1 Sequence Header OBU; null when the samples carry them in-band.</param>
internal sealed record ClipVideoConfig(string Codec, int Width, int Height, uint Fps, byte[]? SequenceHeader = null);

/// <param name="AudioSamples">Frames written per audio track, in the order the tracks were given (0 for a track left out of the file).</param>
/// <param name="Duration100ns">The video's span, first sample to last sample's start.</param>
internal sealed record ClipMuxResult(string Path, long Duration100ns, long SizeBytes, string[] AudioTracks, int VideoSamples, int[] AudioSamples);

/// <summary>
/// Turns video access units and AAC frames, fed one at a time, into an MP4 file: snaps the video to the frame grid, converts Annex B to
/// length-prefixed NAL units (or AV1 temporal units to samples), holds the samples back until the parameter sets are known (then creates
/// the <see cref="Mp4Muxer"/>), and owns the muxer's lifetime. Single thread. The audio track set is fixed at creation. Samples may arrive
/// in any order between the tracks (the muxer indexes every sample), but each track's timestamps have to be increasing: this class does
/// not check (the muxer does not either).
/// </summary>
internal sealed class ClipMuxWriter : IDisposable
{
    private readonly ClipOutput _output;
    private readonly ClipVideoConfig _video;
    private readonly IReadOnlyList<AudioTrackDesc> _audio;
    private readonly long _origin;
    private readonly bool _aligned;
    private readonly bool _av1, _hevc;
    private readonly ParameterSets _ps = new();
    private readonly double _period;
    private byte[]? _seqObu;
    private long _prev = long.MinValue;
    private Mp4Muxer? _mux;
    // Samples waiting for the parameter sets, in arrival order (normally none: the first sample is a keyframe that carries them, or the encoder handed them over). track -1 is video.
    private List<(int track, long pts, bool key, byte[] data)>? _pending;
    private int _count;
    private readonly int[] _audioCount;
    private bool _finished;

    public ClipMuxWriter(ClipOutput output, ClipVideoConfig video, IReadOnlyList<AudioTrackDesc> audio, long origin100ns, bool aligned = true)
    {
        _output = output;
        _video = video;
        _audio = audio;
        _origin = origin100ns;
        _aligned = aligned;
        _av1 = video.Codec == "av1";
        _hevc = video.Codec == "hevc";
        _period = video.Fps > 0 ? 10_000_000.0 / video.Fps : 0;
        _audioCount = new int[audio.Count];
        var seq = video.SequenceHeader;
        if (_av1) { if (seq != null) _seqObu = Av1.FindSequenceHeader(seq); }
        else if (seq != null) Nal.ToLengthPrefixed(seq, _hevc, _ps);
    }

    /// <summary>At least one video sample has been added (even if the muxer waits for parameter sets).</summary>
    public bool HasVideo => _count > 0;

    /// <summary>How much video the muxer holds, in 100 ns (0 until it exists).</summary>
    public long VideoElapsed100ns => _mux?.VideoElapsed100ns ?? 0;

    /// <summary>The longest this file can run before its 32-bit durations overflow (the shortest of its tracks' limits).</summary>
    public long DurationLimit100ns => _mux?.DurationLimit()
        ?? _audio.Aggregate(Mp4Muxer.DurationLimit100ns(new VideoTrackDesc().Timescale), (m, a) => Math.Min(m, Mp4Muxer.DurationLimit100ns(a.SampleRate)));

    /// <summary>Where <see cref="AddVideo"/> will put a sample captured at <paramref name="pts100ns"/> (the frame grid), without adding it: audio frames due up to this time go in before the sample.</summary>
    public long NextVideoPts(long pts100ns) => Snap(pts100ns);

    private long Snap(long pts)
    {
        // Snap capture timestamps to the nominal frame grid so the file is clean constant-frame-rate
        // (the pacer jitters by a millisecond or two); keep timestamps strictly increasing (see SnapToGrid).
        if (_video.Fps > 0) return SnapToGrid(pts, _prev, _origin, _period);
        return pts <= _prev ? _prev + 1 : pts;
    }

    /// <param name="annexB">An Annex B access unit (H.264 / HEVC) or an AV1 temporal unit. Converted before this returns: the caller may reuse the span.</param>
    public void AddVideo(long pts100ns, bool keyframe, ReadOnlySpan<byte> annexB)
    {
        ObjectDisposedException.ThrowIf(_finished, this);
        long q = Snap(pts100ns);
        _prev = q;
        byte[] data;
        if (_av1)
        {
            data = Av1.ToSample(annexB);
            _seqObu ??= Av1.FindSequenceHeader(annexB);
        }
        else data = Nal.ToLengthPrefixed(annexB, _hevc, _ps);
        _count++;

        if (_mux == null)
        {
            bool ready = _av1 ? _seqObu != null : _ps.Complete(_hevc);
            if (!ready)
            {
                _pendingFirstVideo ??= q;
                _pendingBytes += data.Length;
                (_pending ??= new()).Add((-1, q, keyframe, data));
                CheckPendingCap(q);
                return;
            }
            _mux = CreateMuxer();
            if (_pending != null)
            {
                foreach (var p in _pending)
                {
                    if (p.track < 0) _mux.WriteVideo(p.pts, p.pts, p.key, p.data);
                    else _mux.WriteAudio(p.track, p.pts, p.data);
                }
                _pending = null;
                _pendingBytes = 0;
            }
        }
        _mux.WriteVideo(q, q, keyframe, data);
    }

    // A recording has no end in sight: samples held back for parameter sets that never come would fill the memory. A clip is bounded by its ring.
    private const long PendingMaxSpan100ns = 10 * 10_000_000L;
    private const long PendingMaxBytes = 64L << 20;
    private long? _pendingFirstVideo;
    private long _pendingBytes;

    private void CheckPendingCap(long? videoPts)
    {
        if (!_output.IsProgressive) return;
        if (_pendingBytes > PendingMaxBytes || (_pendingFirstVideo is long first && videoPts is long last && last - first > PendingMaxSpan100ns))
            throw new InvalidOperationException($"Encoder did not provide parameter sets within {PendingMaxSpan100ns / 10_000_000} s of video or {PendingMaxBytes >> 20} MB");
    }

    public void AddAudio(int track, in AacFrame frame)
    {
        ObjectDisposedException.ThrowIf(_finished, this);
        if ((uint)track >= (uint)_audio.Count) throw new ArgumentOutOfRangeException(nameof(track));
        _audioCount[track]++;
        if (_mux == null)
        {
            // Deferred frames keep a reference to frame.Data: safe, audio frames are never pooled or reused.
            _pendingBytes += frame.Data.Length;
            (_pending ??= new()).Add((track, frame.Pts, false, frame.Data));
            CheckPendingCap(null);
        }
        else _mux.WriteAudio(track, frame.Pts, frame.Data);
    }

    private Mp4Muxer CreateMuxer()
    {
        var desc = new VideoTrackDesc
        {
            Codec = _video.Codec,
            Width = _video.Width,
            Height = _video.Height,
            CodecConfig = _av1 ? Av1.BuildAv1C(_seqObu!) : _hevc ? Nal.BuildHvcC(_ps.Vps!, _ps.Sps!, _ps.Pps!) : Nal.BuildAvcC(_ps.Sps!, _ps.Pps!),
        };
        return _output.IsProgressive
            ? Mp4Muxer.CreateProgressive(_output.Path, _output.FinalPath!, desc, _audio, _origin, _aligned)
            : new Mp4Muxer(_output.Path, desc, _audio, _origin, _aligned);
    }

    /// <summary>Progressive output: hands what the muxer holds to the OS, and with <paramref name="durable"/> to the disk (see <see cref="Mp4Muxer.Flush"/>). Nothing to do for a clip, or before the muxer exists.</summary>
    public void Flush(bool durable)
    {
        if (_output.IsProgressive && !_finished) _mux?.Flush(durable);
    }

    /// <summary>Writes the file. Throws when there is no video yet ("Nothing recorded yet" / no parameter sets); an exception from the muxer leaves the writer to be disposed (progressive: then <see cref="Mp4Muxer.Recover"/>).</summary>
    public ClipMuxResult Finish()
    {
        ObjectDisposedException.ThrowIf(_finished, this);
        if (_mux == null)
        {
            if (_count == 0) throw new InvalidOperationException("Nothing recorded yet, wait a moment and try again.");
            throw new InvalidOperationException(_av1 ? "AV1 encoder did not provide a Sequence Header OBU (no keyframe yet?)" : "Encoder did not provide SPS/PPS (no keyframe yet?)");
        }
        _mux.Finish();
        _finished = true;
        string path = _output.IsProgressive ? _mux.FinalPath : _output.Path;
        long size = new FileInfo(path).Length;
        var written = _mux.WrittenAudioTracks;
        var counts = (int[])_audioCount.Clone();
        return new ClipMuxResult(path, _mux.VideoElapsed100ns, size, written, _count, counts);
    }

    /// <summary>Gives the file up. Progressive: both files are deleted. Does nothing once <see cref="Finish"/> has succeeded.</summary>
    public void Discard()
    {
        if (_finished) return;
        _mux?.Discard();
        Dispose();
    }

    /// <summary>Releases the files. A finished file stays; a progressive one that was not finished or discarded stays too, with its journal, for <see cref="Mp4Muxer.Recover"/>; a clip's temporary file goes.</summary>
    public void Dispose()
    {
        _finished = true;
        _mux?.Dispose();
        _mux = null;
        _pending = null;
    }

    /// <summary>
    /// Puts a frame on the nearest slot of the exact frame grid (origin + round(k * period)), after <paramref name="prev"/>.
    /// When that slot is taken, the frame moves to the next free slot if that is at most 3/4 of a period after its real time:
    /// that is jitter around a half-slot boundary (the pacer steps by a whole number of 100 ns units, so over a long clip its
    /// frames drift against the exact grid, and its catch-up reset can land on any phase), and staying on the upper slot keeps
    /// the file one stts run. A free slot further ahead than that means a catch-up burst after a late wake: the frame keeps its
    /// real time, so the frames after it land on their own slots instead of all running late against the audio. Either way a
    /// frame ends up at most 3/4 of a period from when it was captured.
    /// </summary>
    private static long SnapToGrid(long pts, long prev, long origin100ns, double period)
    {
        long k = (long)Math.Round((pts - origin100ns) / period);
        long q = origin100ns + (long)Math.Round(k * period);
        if (q > prev) return q;
        k = (long)Math.Floor((prev - origin100ns) / period) + 1;   // first slot after prev
        long next = origin100ns + (long)Math.Round(k * period);
        if (next <= prev) next = origin100ns + (long)Math.Round((k + 1) * period);
        return next - pts <= 0.75 * period ? next : Math.Max(prev + 1, pts);
    }
}


