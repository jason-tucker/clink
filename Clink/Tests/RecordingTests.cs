using System.Buffers.Binary;
using System.IO;
using System.Text;
using Clink.Audio;
using Clink.Encode;
using Clink.Engine;
using Clink.Native;

namespace Clink.Tests;

/// <summary>
/// The progressive mode of <see cref="Mp4Muxer"/> (the writer of manual recordings of any length): the same synthetic clip through
/// the fast-start path and the progressive one has to read back the same, and a recording that is never finished has to come back
/// from its journal with every sample that was whole and nothing else. The tests that read the files back through Media Foundation
/// run like <see cref="MixRenderTests"/> (a machine without its AAC encoder skips them, unless CLINK_TESTS_NO_SKIP=1).
/// </summary>
internal static class RecordingTests
{
    private const long Origin = 1_000_000_000;      // an arbitrary clock reading, as a recording has
    private static readonly string[] Names = { "Mix", "Game" };
    // Where the two audio tracks start, in samples from the origin: before it and after it. 1201 is not a whole number of milliseconds
    // (48 samples), so the movie counts in 1/48000 s, the second branch of the muxer's timescale choice.
    private static readonly int[] Starts = { -480, 1201 };

    private sealed class Input
    {
        public readonly VideoTrackDesc Video = new() { Width = 16, Height = 16 };
        public readonly List<AudioTrackDesc> Audio = new();
        public readonly List<AacFrame>[] Frames = { new(), new() };
        public readonly List<(long pts, bool key, byte[] data)> Samples = new();
    }

    /// <summary>A mid-gray 16x16 clip at 30 fps of <paramref name="videoFrames"/> frames and two audio tracks that start where <see cref="Starts"/> says; AAC frames of silence, or with <paramref name="realAudio"/> false, any bytes.</summary>
    private static Input MakeInput(int videoFrames, bool realAudio)
    {
        var input = new Input();
        var ps = new ParameterSets();
        byte[] first = Nal.ToLengthPrefixed(TimelineProbe.Idr, false, ps);
        input.Video.CodecConfig = Nal.BuildAvcC(ps.Sps!, ps.Pps!);
        for (int i = 0; i < videoFrames; i++)
        {
            // (The audio frames differ in size, so that the offsets of the video samples are not a simple multiple.)
            input.Samples.Add((Origin + (long)Math.Round(i * 10_000_000.0 / 30), i % 10 == 0, (byte[])first.Clone()));
        }
        double unit = 10_000_000.0 / 48000;
        int count = (int)(videoFrames / 30.0 * 48000 / AacEncoder.FrameSamples) + 2;
        for (int t = 0; t < 2; t++)
        {
            long start = Origin + (long)Math.Round(Starts[t] * unit);
            byte[] asc = { 0x11, 0x90 };
            int bitrate = 192000;
            if (realAudio)
            {
                Mf.EnsureStarted();
                using var enc = new AacEncoder(48000, 2, 24000);
                var silence = new byte[AacEncoder.FrameSamples * 4];
                int k = 0;
                for (int n = 0; n < count + 4; n++)
                    enc.Encode(silence, start, (_, data) => { if (k < count) input.Frames[t].Add(new AacFrame(start + (long)Math.Round(k++ * AacEncoder.FrameSamples * unit), data)); });
                asc = enc.AudioSpecificConfig;
                bitrate = enc.BytesPerSecond * 8;
            }
            else
            {
                for (int k = 0; k < count; k++)
                    input.Frames[t].Add(new AacFrame(start + (long)Math.Round(k * AacEncoder.FrameSamples * unit), new byte[20 + (k + t) % 5]));
            }
            input.Audio.Add(new AudioTrackDesc { Name = Names[t], AudioSpecificConfig = asc, Bitrate = bitrate, SampleRate = 48000, Channels = 2 });
        }
        return input;
    }

    /// <summary>Writes the first <paramref name="videoCount"/> video samples, each behind the audio frames due before it, as ClipWriter does (the rest of the audio after the last, when all of the video is written). Returns what was written, in order: track (0 = video) and size.</summary>
    private static List<(int track, int size)> Feed(Mp4Muxer mux, Input input, int videoCount, Action<int>? after = null)
    {
        var log = new List<(int, int)>();
        var ai = new int[2];
        for (int i = 0; i < videoCount; i++)
        {
            var v = input.Samples[i];
            for (int t = 0; t < 2; t++)
                while (ai[t] < input.Frames[t].Count && input.Frames[t][ai[t]].Pts <= v.pts)
                {
                    var f = input.Frames[t][ai[t]++];
                    mux.WriteAudio(t, f.Pts, f.Data);
                    log.Add((t + 1, f.Data.Length));
                }
            mux.WriteVideo(v.pts, v.pts, v.key, v.data);
            log.Add((0, v.data.Length));
            after?.Invoke(i);
        }
        if (videoCount == input.Samples.Count)
            for (int t = 0; t < 2; t++)
                while (ai[t] < input.Frames[t].Count)
                {
                    var f = input.Frames[t][ai[t]++];
                    mux.WriteAudio(t, f.Pts, f.Data);
                    log.Add((t + 1, f.Data.Length));
                }
        return log;
    }

    private static void WriteFastStart(string path, Input input)
    {
        using var mux = new Mp4Muxer(path, input.Video, input.Audio, Origin);
        Feed(mux, input, input.Samples.Count);
        mux.Finish();
    }

    private static void WriteProgressive(string path, Input input)
    {
        using var mux = Mp4Muxer.CreateProgressive(path + ".rec", path, input.Video, input.Audio, Origin);
        Feed(mux, input, input.Samples.Count, i => { if (i % 5 == 4) mux.Flush(i % 10 == 9); });
        mux.Finish();
        Check.Equal(path, mux.FinalPath);
    }

    /// <summary>The two files read back the same: the tracks as Clink reads them (names, offsets, the alignment mark) and every stream as Media Foundation hands it out with the edit lists hidden.</summary>
    private static void SameResults(string a, string b, string what)
    {
        var ta = Trimmer.ReadClipTracks(a);
        var tb = Trimmer.ReadClipTracks(b);
        Check.NotNull(ta, what + ": the fast-start clip is a Clink file");
        Check.NotNull(tb, what + ": the progressive clip is a Clink file");
        Check.Equal(ta!.Marked, tb!.Marked, what + ": Marked");
        Check.True(tb.Marked, what + ": a recording says its edit lists are true");
        Check.Equal(ta.Audio.Select(x => x.Name).ToArray(), tb.Audio.Select(x => x.Name).ToArray(), what + ": names");
        Check.Equal(ta.Audio.Select(x => x.StartOffset).ToArray(), tb.Audio.Select(x => x.StartOffset).ToArray(), what + ": offsets");
        Check.Equal(ta.AudioEditBoxes.Length, tb.AudioEditBoxes.Length, what + ": one edit list box per audio track");
        var (da, sa) = MediaProbe.Probe(a, hideEditLists: true);
        var (db, sb) = MediaProbe.Probe(b, hideEditLists: true);
        SameMoov(a, b, what);
        Check.Equal(3, sb.Count, what + ": video and two audio tracks");
        Check.Equal(da, db, what + ": duration");
        Check.Equal(sa, sb, what + ": streams (counts, first and last timestamps, deltas, keyframes, bytes)");
    }

    // ---------------------------------------------------------------- a small box parser

    private readonly record struct BoxAt(string Type, int Box, int Body, int End);

    private static List<BoxAt> Boxes(byte[] d, int start, int end)
    {
        var list = new List<BoxAt>();
        int at = start;
        while (at + 8 <= end)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(at));
            string type = Encoding.ASCII.GetString(d, at + 4, 4);
            int head = 8;
            if (size == 1) { size = (long)BinaryPrimitives.ReadUInt64BigEndian(d.AsSpan(at + 8)); head = 16; }
            else if (size == 0) size = end - at;
            if (size < head || at + size > end) break;
            list.Add(new BoxAt(type, at, at + head, (int)(at + size)));
            at += (int)size;
        }
        return list;
    }

    private static readonly HashSet<string> Containers = new() { "moov", "trak", "mdia", "minf", "stbl", "edts" };

    private static IEnumerable<BoxAt> Walk(byte[] d, int start, int end)
    {
        foreach (var b in Boxes(d, start, end))
        {
            yield return b;
            if (Containers.Contains(b.Type)) foreach (var c in Walk(d, b.Body, b.End)) yield return c;
        }
    }

    /// <summary>The whole moov box of a file, with every stco / co64 entry made relative to the start of the mdat payload.</summary>
    private static byte[] RebasedMoov(string path)
    {
        byte[] d = File.ReadAllBytes(path);
        var top = Boxes(d, 0, d.Length);
        long payloadStart = top.Single(b => b.Type == "mdat").Body;
        var m = top.Single(b => b.Type == "moov");
        byte[] moov = d[m.Box..m.End];
        foreach (var b in Walk(moov, 8, moov.Length))
        {
            if (b.Type == "stco")
            {
                uint n = BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(b.Body + 4));
                for (int i = 0; i < n; i++)
                {
                    var s = moov.AsSpan(b.Body + 8 + 4 * i);
                    BinaryPrimitives.WriteUInt32BigEndian(s, (uint)(BinaryPrimitives.ReadUInt32BigEndian(s) - payloadStart));
                }
            }
            else if (b.Type == "co64")
            {
                uint n = BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(b.Body + 4));
                for (int i = 0; i < n; i++)
                {
                    var s = moov.AsSpan(b.Body + 8 + 8 * i);
                    BinaryPrimitives.WriteUInt64BigEndian(s, (ulong)((long)BinaryPrimitives.ReadUInt64BigEndian(s) - payloadStart));
                }
            }
        }
        return moov;
    }

    /// <summary>The moov boxes of two files are the same bytes, the chunk offsets apart (which differ by where the mdat is).</summary>
    private static void SameMoov(string fast, string prog, string what)
    {
        byte[] a = RebasedMoov(fast), b = RebasedMoov(prog);
        Check.Equal(a.Length, b.Length, what + ": moov size");
        Check.True(a.AsSpan().SequenceEqual(b), what + ": the moov boxes are byte for byte the same (tkhd, elst, mvhd, stbl)");
    }

    /// <summary>The movie duration and the media duration of every track, as the file's mvhd and mdhd boxes hold them (version 0: 32 bits).</summary>
    private static (uint movie, uint movieScale, uint[] media) Durations(string path)
    {
        byte[] d = File.ReadAllBytes(path);
        var moov = Boxes(d, 0, d.Length).Single(b => b.Type == "moov");
        var all = Walk(d, moov.Box, moov.End).ToList();
        var mvhd = all.Single(b => b.Type == "mvhd");
        Check.Equal(0, (int)d[mvhd.Body], "mvhd version 0");
        uint movie = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(mvhd.Body + 16)), scale = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(mvhd.Body + 12));
        var media = all.Where(b => b.Type == "mdhd").Select(b => BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(b.Body + 16))).ToArray();
        return (movie, scale, media);
    }

    private static void CopyOpen(string from, string to)
    {
        using var a = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var b = new FileStream(to, FileMode.Create, FileAccess.Write, FileShare.None);
        a.CopyTo(b);
    }

    private static bool SameBytes(string a, string b) => File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));

    [UnitTest]
    public static void Progressive_and_fast_start_files_read_back_the_same()
    {
        using var dir = new TempDir();
        MixRenderTests.OnMta(() =>
        {
            var input = MakeInput(45, realAudio: true);
            string fast = dir.File("fast.mp4"), prog = dir.File("prog.mp4");
            WriteFastStart(fast, input);
            WriteProgressive(prog, input);
            Check.False(File.Exists(prog + ".rec"), "the in-progress file is the final one now");
            Check.False(File.Exists(prog + ".rec.idx"), "and the journal is gone");
            SameResults(fast, prog, "plain");
        });
    }

    [UnitTest]
    public static void Progressive_and_fast_start_files_read_back_the_same_under_edit_list_emulation()
    {
        using var dir = new TempDir();
        MixRenderTests.OnMta(() =>
        {
            var input = MakeInput(45, realAudio: true);
            string fast = dir.File("fast.mp4"), prog = dir.File("prog.mp4");
            WriteFastStart(fast, input);
            WriteProgressive(prog, input);
            using (EditListEmulation.Apply()) SameResults(fast, prog, "emulating");
            using (EditListEmulation.Apply(hideEditLists: false)) SameResults(fast, prog, "emulating, not hiding");
        });
    }

    [UnitTest]
    public static void A_recording_that_was_never_finished_comes_back_with_every_whole_sample_and_nothing_else()
    {
        using var dir = new TempDir();
        MixRenderTests.OnMta(() =>
        {
            var input = MakeInput(30, realAudio: true);
            string rec = dir.File("take.mp4.rec"), idx = Mp4Muxer.JournalPath(rec), final = dir.File("take.mp4");
            List<(int track, int size)> log;
            string recA = dir.File("a.mp4.rec"), idxA = Mp4Muxer.JournalPath(recA), finalA = dir.File("a.mp4");
            string recB = dir.File("b.mp4.rec"), idxB = Mp4Muxer.JournalPath(recB), finalB = dir.File("b.mp4");
            int flushed = 0;
            using (var mux = Mp4Muxer.CreateProgressive(rec, final, input.Video, input.Audio, Origin))
            {
                log = Feed(mux, input, 30, i => { if (i == 19) mux.Flush(durable: true); });
                // What the flush at the 20th video sample made durable: the log up to and including that sample.
                int videos = 0;
                flushed = log.TakeWhile(s => { bool take = videos < 20; if (s.track == 0) videos++; return take; }).Count();

                // The process dies here, in the middle of the recording: no Finish, and what is only in the muxer's buffers is lost.
                // The muxer is still open (it holds the files, shared for reading), so what is on disk now is copied, and the copies are
                // damaged the way a power cut does it.
                CopyOpen(rec, recA); CopyOpen(idx, idxA);
                CopyOpen(rec, recB); CopyOpen(idx, idxB);
                mux.Discard();
            }
            Check.False(File.Exists(rec) || File.Exists(idx), "Discard took the live recording away");
            Check.True(flushed > 20 && flushed < log.Count, $"the flush came in the middle of the recording ({flushed} of {log.Count})");
            var durable = log.Take(flushed).ToList();
            Check.True(durable[^1].track == 0 && durable[^1].size >= 2 && durable[^2].size >= 2, "the samples the cuts land in are big enough to cut in the middle");

            // A: every sample is in the data file, but the last journal record is torn (13 bytes of it are missing) and junk follows.
            using (var fs = new FileStream(idxA, FileMode.Open, FileAccess.Write)) { fs.SetLength(fs.Length - 13); fs.Seek(0, SeekOrigin.End); fs.Write(Enumerable.Range(1, 40).Select(x => (byte)(x * 7)).ToArray()); }
            // B: the journal is whole (and junk, a record and more, follows it), but the data file stops in the middle of the last sample.
            using (var fs = new FileStream(recB, FileMode.Open, FileAccess.Write)) fs.SetLength(fs.Length - durable[^1].size / 2 - 1);
            using (var fs = new FileStream(idxB, FileMode.Open, FileAccess.Write)) { fs.Seek(0, SeekOrigin.End); fs.Write(Enumerable.Range(1, 40).Select(x => (byte)(x * 11)).ToArray()); }
            var whole = durable.Take(durable.Count - 1).ToList();

            foreach (var (r, i, f, what) in new[] { (recA, idxA, finalA, "torn journal record"), (recB, idxB, finalB, "torn sample") })
            {
                var result = Mp4Muxer.Recover(r, i, f);
                Check.NotNull(result, what + ": recovered");
                Check.Equal(f, result!.Path);
                Check.Equal(whole.Count(s => s.track == 0), result.VideoSamples, what + ": video samples");
                Check.Equal(Names, result.AudioTracks);
                Check.True(File.Exists(f) && !File.Exists(r) && !File.Exists(i), what + ": the clip is in place and the leftovers are gone");
                Check.Equal(new FileInfo(f).Length, result.SizeBytes);

                Check.True(Trimmer.ReadClipTracks(f)!.Marked, what + ": the recovered file is a marked Clink file");
                Check.Equal(Names, Trimmer.ReadAudioTrackNames(f)!);
                var (duration, streams) = MediaProbe.Probe(f, hideEditLists: true);
                Check.Equal(3, streams.Count);
                // Media Foundation numbers the streams as it likes: the video one is found by its type, the audio ones are compared as a set.
                Check.Equal((long)whole.Count(s => s.track == 0), streams.Single(s => s.IsVideo).Count, what + ": video samples read back");
                // (Media Foundation puts the parameter sets in front of every keyframe it hands out: the video bytes are not the ones written.)
                var expected = new[] { 1, 2 }.Select(t => (whole.Count(s => s.track == t), whole.Where(s => s.track == t).Sum(s => (long)s.size))).OrderBy(x => x).ToList();
                var actual = streams.Where(s => !s.IsVideo).Select(s => ((int)s.Count, s.Bytes)).OrderBy(x => x).ToList();
                Check.Equal(expected, actual, what + ": audio samples and bytes read back");
                Check.True(Math.Abs(duration - result.Duration100ns) < 100_000, $"{what}: the duration ({duration}) is the one reported ({result.Duration100ns})");
            }
        });
    }

    [UnitTest]
    public static void An_abandoned_recording_keeps_its_data_and_its_journal()
    {
        using var dir = new TempDir();
        var input = MakeInput(10, realAudio: false);
        string rec = dir.File("take.mp4.rec"), idx = Mp4Muxer.JournalPath(rec), final = dir.File("take.mp4");
        using (var mux = Mp4Muxer.CreateProgressive(rec, final, input.Video, input.Audio, Origin)) Feed(mux, input, 10);   // no Finish
        Check.True(File.Exists(rec) && File.Exists(idx), "an abandoned recording keeps its data and its journal");
        Check.False(File.Exists(final), "and is not a clip yet");
        var result = Mp4Muxer.Recover(rec, idx, final);
        Check.NotNull(result, "and it comes back");
        Check.Equal(10, result!.VideoSamples);
    }

    [UnitTest]
    public static void Progressive_moov_is_the_fast_start_moov_with_the_offsets_apart()
    {
        using var dir = new TempDir();
        var input = MakeInput(45, realAudio: false);
        string fast = dir.File("fast.mp4"), prog = dir.File("prog.mp4");
        WriteFastStart(fast, input);
        WriteProgressive(prog, input);
        SameMoov(fast, prog, "synthetic");
        Check.Equal(48000u, Durations(prog).movieScale, "a track that starts 1201 samples late puts the movie on the audio's timescale");
        Check.True(Trimmer.ReadClipTracks(prog)!.Marked);
    }

    [UnitTest]
    public static void A_recording_recovers_to_the_file_Finish_makes_even_when_Finish_got_as_far_as_the_moov_or_the_head_is_gone()
    {
        using var dir = new TempDir();
        var input = MakeInput(45, realAudio: false);
        string rec = dir.File("take.mp4.rec"), idx = Mp4Muxer.JournalPath(rec), final = dir.File("take.mp4");
        string[] r = new[] { "a", "b", "c", "d" }.Select(x => dir.File(x + ".mp4.rec")).ToArray();
        using (var mux = Mp4Muxer.CreateProgressive(rec, final, input.Video, input.Audio, Origin))
        {
            Feed(mux, input, 45, i => { if (i % 7 == 6) mux.Flush(i % 14 == 13); });
            mux.Flush(durable: true);
            foreach (var x in r) { CopyOpen(rec, x); CopyOpen(idx, Mp4Muxer.JournalPath(x)); }
            mux.Finish();
        }
        Check.True(File.Exists(final) && !File.Exists(idx));

        // a: Recover alone makes the file Finish made, byte for byte.
        var a = Mp4Muxer.Recover(r[0], Mp4Muxer.JournalPath(r[0]), dir.File("ra.mp4"));
        Check.NotNull(a, "recovered");
        Check.True(SameBytes(final, dir.File("ra.mp4")), "Recover and Finish make the same file");
        Check.Equal(45, a!.VideoSamples);
        SameMoov(final, dir.File("ra.mp4"), "recovered");
        Check.Equal(Trimmer.ReadClipTracks(final)!.Audio.Select(x => x.StartOffset).ToArray(), Trimmer.ReadClipTracks(dir.File("ra.mp4"))!.Audio.Select(x => x.StartOffset).ToArray(), "offsets");

        // b: a Finish that failed after the moov was appended (the journal is still there): the moov is cut off and the file made again.
        File.Copy(final, r[1], overwrite: true);
        var b = Mp4Muxer.Recover(r[1], Mp4Muxer.JournalPath(r[1]), dir.File("rb.mp4"));
        Check.NotNull(b, "recovered over an old moov");
        Check.True(SameBytes(final, dir.File("rb.mp4")), "and the file is the same again");

        // c: the head of the file (ftyp, mdat header) is zeros, as after a power cut that lost them.
        using (var fs = new FileStream(r[2], FileMode.Open, FileAccess.Write))
        {
            byte[] f = File.ReadAllBytes(final);
            int head = (int)BinaryPrimitives.ReadUInt32BigEndian(f) + 16;
            fs.Write(new byte[head]);
        }
        var c = Mp4Muxer.Recover(r[2], Mp4Muxer.JournalPath(r[2]), dir.File("rc.mp4"));
        Check.NotNull(c, "recovered with the head gone");
        Check.True(SameBytes(final, dir.File("rc.mp4")), "and the head is written again");

        // d: a journal made for another head (the check of the header is mended so that only the head is wrong).
        string jd = Mp4Muxer.JournalPath(r[3]);
        byte[] j = File.ReadAllBytes(jd);
        int bodyLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(j.AsSpan(12));
        BinaryPrimitives.WriteInt64LittleEndian(j.AsSpan(16 + 1), BinaryPrimitives.ReadInt64LittleEndian(j.AsSpan(16 + 1)) + 4);   // the data start follows the 'aligned' byte
        uint h = 2166136261;
        foreach (byte x in j.AsSpan(16, bodyLength)) h = (h ^ x) * 16777619;
        BinaryPrimitives.WriteUInt32LittleEndian(j.AsSpan(16 + bodyLength), h);
        File.WriteAllBytes(jd, j);
        long before = new FileInfo(r[3]).Length;
        Check.Null(Mp4Muxer.Recover(r[3], jd, dir.File("rd.mp4")), "a journal of another layout is not used");
        Check.True(File.Exists(r[3]) && File.Exists(jd) && !File.Exists(dir.File("rd.mp4")) && new FileInfo(r[3]).Length == before, "and both files stay");
    }

    [UnitTest]
    public static void A_sample_far_out_in_a_big_recording_gets_a_64_bit_offset()
    {
        using var dir = new TempDir();
        var input = MakeInput(20, realAudio: false);
        string rec = dir.File("big.mp4.rec"), final = dir.File("big.mp4");
        using var mux = Mp4Muxer.CreateProgressive(rec, final, input.Video, input.Audio, Origin);
        Feed(mux, input, 20);
        long start = 5L << 30;
        byte[] moov = mux.BuildProgressiveMoov(start, out bool use64, out _);
        Check.True(use64, "past 4 GiB");
        Check.True(Boxes(moov, 8, moov.Length).Any(b => b.Type == "trak"));
        var samples = new List<(long at, long size)>();
        int tracks = 0;
        foreach (var trak in Boxes(moov, 8, moov.Length).Where(b => b.Type == "trak"))
        {
            tracks++;
            var inside = Walk(moov, trak.Body, trak.End).ToList();
            Check.False(inside.Any(b => b.Type == "stco"), "no 32-bit table");
            var co = inside.Single(b => b.Type == "co64");
            var sz = inside.Single(b => b.Type == "stsz");
            uint n = BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(co.Body + 4));
            Check.Equal(n, BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(sz.Body + 8)), "one size per offset");
            for (int i = 0; i < n; i++)
                samples.Add(((long)BinaryPrimitives.ReadUInt64BigEndian(moov.AsSpan(co.Body + 8 + 8 * i)), BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(sz.Body + 12 + 4 * i))));
        }
        Check.Equal(3, tracks);
        // Put together, the samples fill the data from `start` on, one after the other.
        long expect = start;
        foreach (var (at, size) in samples.OrderBy(s => s.at)) { Check.Equal(expect, at, "contiguous absolute offsets"); expect += size; }

        moov = mux.BuildProgressiveMoov(1000, out use64, out _);
        Check.False(use64, "a small file keeps the 32-bit table");
        Check.True(Walk(moov, 0, moov.Length).Any(b => b.Type == "stco") && !Walk(moov, 0, moov.Length).Any(b => b.Type == "co64"));
    }

    [UnitTest]
    public static void A_recording_with_nothing_in_it_cannot_be_finished_and_is_discarded_cleanly()
    {
        using var dir = new TempDir();
        var input = MakeInput(10, realAudio: false);
        string rec = dir.File("none.mp4.rec"), idx = Mp4Muxer.JournalPath(rec), final = dir.File("none.mp4");
        var mux = Mp4Muxer.CreateProgressive(rec, final, input.Video, input.Audio, Origin);
        var a = input.Frames[0][0];
        mux.WriteAudio(0, a.Pts, a.Data);   // audio alone is not a recording
        long length = new FileInfo(rec).Length;
        var ex = Check.Throws<InvalidOperationException>(() => mux.Finish(), "nothing to finish");
        Check.Contains(ex.Message, "Nothing recorded");
        Check.True(File.Exists(rec) && File.Exists(idx) && !File.Exists(final), "no file was touched or made");
        Check.Equal(length, new FileInfo(rec).Length, "the in-progress file was not written to");
        mux.Discard();
        Check.False(File.Exists(rec) || File.Exists(idx) || File.Exists(final), "Discard removes both files");
        mux.Discard();   // and does not mind being asked again
        mux.Dispose();
    }

    [UnitTest]
    public static void A_new_recording_never_replaces_an_earlier_one_that_was_not_recovered()
    {
        using var dir = new TempDir();
        var input = MakeInput(10, realAudio: false);
        string rec = dir.File("take.mp4.rec"), idx = Mp4Muxer.JournalPath(rec), final = dir.File("take.mp4");
        using (var mux = Mp4Muxer.CreateProgressive(rec, final, input.Video, input.Audio, Origin)) Feed(mux, input, 10);   // abandoned
        byte[] data = File.ReadAllBytes(rec), journal = File.ReadAllBytes(idx);
        Check.Throws<IOException>(() => Mp4Muxer.CreateProgressive(rec, final, input.Video, input.Audio, Origin), "the data file is there");
        Check.True(File.ReadAllBytes(rec).AsSpan().SequenceEqual(data) && File.ReadAllBytes(idx).AsSpan().SequenceEqual(journal), "both files are as they were");

        // Only the journal is left over: it is kept, and the data file this call made is taken away again.
        File.Delete(rec);
        Check.Throws<IOException>(() => Mp4Muxer.CreateProgressive(rec, final, input.Video, input.Audio, Origin), "the journal is there");
        Check.True(File.ReadAllBytes(idx).AsSpan().SequenceEqual(journal), "the journal is as it was");
        Check.False(File.Exists(rec), "and the data file made by the failed call is gone");
    }

    [UnitTest]
    public static void The_in_progress_file_and_the_final_file_have_to_be_on_one_volume()
    {
        var input = MakeInput(2, realAudio: false);
        // Nothing is touched: the paths are only compared.
        Check.Throws<ArgumentException>(() => Mp4Muxer.CreateProgressive(@"C:\clink-unittest-none\a.mp4.rec", @"D:\clink-unittest-none\a.mp4", input.Video, input.Audio, Origin));
        Check.Throws<ArgumentException>(() => Mp4Muxer.CreateProgressive(@"C:\clink-unittest-none\a.mp4.rec", @"\\server\share\a.mp4", input.Video, input.Audio, Origin));
        Check.False(Directory.Exists(@"C:\clink-unittest-none"));
    }

    [UnitTest]
    public static void A_sample_that_could_not_be_written_stops_the_recording_from_being_finished_but_not_from_being_recovered()
    {
        using var dir = new TempDir();
        var input = MakeInput(10, realAudio: false);
        string rec = dir.File("take.mp4.rec"), idx = Mp4Muxer.JournalPath(rec), final = dir.File("take.mp4");
        using (var mux = Mp4Muxer.CreateProgressive(rec, final, input.Video, input.Audio, Origin))
        {
            for (int i = 0; i < 3; i++) mux.WriteVideo(input.Samples[i].pts, input.Samples[i].pts, input.Samples[i].key, input.Samples[i].data);
            mux.Flush(durable: false);
            mux.BeforeSampleWrite = () => throw new IOException("disk full");
            var s = input.Samples[3];
            Check.Throws<IOException>(() => mux.WriteVideo(s.pts, s.pts, s.key, s.data));
            mux.BeforeSampleWrite = null;
            var ex = Check.Throws<InvalidOperationException>(() => mux.Finish(), "the index lists a sample that is not in the file");
            Check.Contains(ex.Message, "Recover");
            Check.True(File.Exists(rec) && !File.Exists(final), "nothing was made");
        }
        var result = Mp4Muxer.Recover(rec, idx, final);
        Check.NotNull(result);
        Check.Equal(3, result!.VideoSamples, "the three whole samples");
    }

    [UnitTest]
    public static void A_journal_with_no_whole_video_sample_recovers_nothing_and_deletes_nothing()
    {
        using var dir = new TempDir();
        var input = MakeInput(10, realAudio: false);
        string rec = dir.File("take.mp4.rec"), idx = Mp4Muxer.JournalPath(rec), final = dir.File("take.mp4");
        using (var mux = Mp4Muxer.CreateProgressive(rec, final, input.Video, input.Audio, Origin))
        {
            var a = input.Frames[0][0];
            mux.WriteAudio(0, a.Pts, a.Data);   // audio alone is not a recording
        }
        Check.Null(Mp4Muxer.Recover(rec, idx, final), "audio only");
        Check.True(File.Exists(rec) && File.Exists(idx) && !File.Exists(final), "files untouched");

        File.WriteAllBytes(idx, new byte[] { 1, 2, 3 });
        Check.Null(Mp4Muxer.Recover(rec, idx, final), "not a journal");
        Check.True(File.Exists(rec) && File.Exists(idx) && !File.Exists(final), "files untouched, again");
    }

    [UnitTest]
    public static void Finishing_a_recording_never_replaces_a_file_and_leaves_no_journal()
    {
        using var dir = new TempDir();
        var input = MakeInput(10, realAudio: false);
        string final = dir.File("take.mp4");
        File.WriteAllText(final, "someone else's");
        using var mux = Mp4Muxer.CreateProgressive(final + ".rec", final, input.Video, input.Audio, Origin);
        Feed(mux, input, 10);
        mux.Finish();
        Check.Equal(dir.File("take (2).mp4"), mux.FinalPath);
        Check.Equal("someone else's", File.ReadAllText(final));
        Check.True(Trimmer.ReadClipTracks(mux.FinalPath)!.Marked);
        Check.False(File.Exists(final + ".rec") || File.Exists(final + ".rec.idx"));
        mux.Dispose();
        Check.True(File.Exists(mux.FinalPath), "disposing a finished recording keeps it");
    }

    [UnitTest]
    public static void A_track_cannot_pass_the_longest_duration_the_file_holds()
    {
        using var dir = new TempDir();
        var input = MakeInput(2, realAudio: false);
        long limit = Mp4Muxer.DurationLimit100ns(90000);
        Check.Near(13.25, limit / 10_000_000.0 / 3600, .01, "about 13 h 15 min for the 90 kHz video track");
        string final = dir.File("long.mp4");
        using var mux = Mp4Muxer.CreateProgressive(final + ".rec", final, input.Video, input.Audio, Origin);
        Check.True(mux.DurationLimit() <= limit);
        var s = input.Samples[0];
        mux.WriteVideo(s.pts, s.pts, true, s.data);
        mux.WriteVideo(s.pts + limit, s.pts + limit, true, s.data);   // exactly at the limit still fits
        Check.Equal(limit, mux.VideoElapsed100ns);
        Check.Throws<Mp4Muxer.DurationLimitException>(() => mux.WriteVideo(s.pts + limit + 1_000_000, s.pts + limit + 1_000_000, true, s.data), "past the limit");
        var frame = input.Frames[0][0];
        mux.WriteAudio(0, frame.Pts, frame.Data);
        long thirtyHours = 30L * 3600 * 10_000_000;   // an audio track holds 24.8 h at 48 kHz
        Check.Throws<Mp4Muxer.DurationLimitException>(() => mux.WriteAudio(0, frame.Pts + thirtyHours, frame.Data), "an audio frame that far");
        mux.Finish();   // nothing was added by the refused sample: the file is whole
        var tracks = Trimmer.ReadClipTracks(mux.FinalPath);
        Check.NotNull(tracks, "the file up to the limit reads back");

        // The two frames are 13.25 h apart, so the estimate of the last frame's duration (the median gap) is as long: it is held to a
        // second, and what the file says (read from its boxes, 32 bits) is the limit plus that second, which still fits.
        long expectedVideo = (long)Math.Round((limit + 10_000_000L) * 90000 / 10_000_000.0);
        Check.True(expectedVideo <= uint.MaxValue, "the duration that should be written fits");
        var (movie, movieScale, media) = Durations(mux.FinalPath);
        Check.Equal(expectedVideo, (long)media[0], "the video track's mdhd duration is the limit and a second (not wrapped)");
        Check.Equal((limit + 10_000_000L) * movieScale / 10_000_000, (long)movie, "and the movie's");
    }

    [UnitTest]
    public static void Writes_after_a_failed_write_are_refused_and_the_recovered_samples_are_the_ones_written()
    {
        using var dir = new TempDir();
        var input = MakeInput(2, realAudio: false);
        string rec = dir.File("f.mp4.rec"), idx = Mp4Muxer.JournalPath(rec), final = dir.File("f.mp4");
        const int Size = 300_000;
        byte[] S(int i) { var b = new byte[Size]; Array.Fill(b, (byte)(i + 1)); return b; }
        int failedAt = -1;
        using (var mux = Mp4Muxer.CreateProgressive(rec, final, input.Video, input.Audio, Origin))
        {
            long dataStart = new FileInfo(rec).Length;
            using (var lk = new FileStream(rec, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                lk.Lock(dataStart + 500_000, 10);   // ahead of the write position: the write that reaches it fails (0x80070021)
                for (int i = 0; i < 4 && failedAt < 0; i++)
                {
                    try { mux.WriteVideo(Origin + i * 333_333, Origin + i * 333_333, true, S(i)); }
                    catch (IOException) { failedAt = i; }
                }
                lk.Unlock(dataStart + 500_000, 10);
            }
            Check.True(failedAt > 0, "a write failed, after some whole samples");
            // Part of the failed sample may sit in the file buffer: nothing more may follow it, or the journal would name the wrong bytes.
            Check.Throws<InvalidOperationException>(() => mux.WriteVideo(Origin + 9 * 333_333, Origin + 9 * 333_333, true, S(9)), "video after the fault");
            Check.Throws<InvalidOperationException>(() => mux.WriteAudio(0, Origin, new byte[20]), "audio after the fault");
            mux.Flush(false);   // (flushing stays allowed: Dispose needs it)
        }
        var r = Mp4Muxer.Recover(rec, idx, final);
        Check.NotNull(r);
        Check.Equal(failedAt, r!.VideoSamples, "the samples before the fault");
        byte[] d = File.ReadAllBytes(final);
        var moov = Boxes(d, 0, d.Length).Single(b => b.Type == "moov");
        var all = Walk(d, moov.Box, moov.End).ToList();
        var co = all.First(b => b.Type == "stco"); var sz = all.First(b => b.Type == "stsz");
        Check.Equal(failedAt, (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(co.Body + 4)), "chunk count");
        for (int i = 0; i < failedAt; i++)
        {
            int at = (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(co.Body + 8 + 4 * i));
            uint size = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(sz.Body + 12 + 4 * i));
            Check.Equal(Size, (int)size, $"size of sample {i}");
            Check.True(d.AsSpan(at, Size).SequenceEqual(S(i)), $"the bytes of sample {i} are what was written");
        }
    }

    [UnitTest]
    public static void Recovering_again_after_a_recovery_that_failed_to_move_the_file_gives_the_same_file()
    {
        using var dir = new TempDir();
        var input = MakeInput(30, realAudio: false);
        string rec = dir.File("take.mp4.rec"), idx = Mp4Muxer.JournalPath(rec), final = dir.File("take.mp4");
        string r1 = dir.File("b1.mp4.rec"), r2 = dir.File("b2.mp4.rec");
        List<(int track, int size)> log;
        using (var mux = Mp4Muxer.CreateProgressive(rec, final, input.Video, input.Audio, Origin))
        {
            log = Feed(mux, input, 30);
            mux.Flush(durable: true);
            CopyOpen(rec, r1); CopyOpen(idx, Mp4Muxer.JournalPath(r1));
            CopyOpen(rec, r2); CopyOpen(idx, Mp4Muxer.JournalPath(r2));
            mux.Discard();
        }
        // The last sample is torn in both copies.
        foreach (var x in new[] { r1, r2 }) using (var fs = new FileStream(x, FileMode.Open, FileAccess.Write)) fs.SetLength(fs.Length - log[^1].size / 2 - 1);
        var a = Mp4Muxer.Recover(r1, Mp4Muxer.JournalPath(r1), dir.File("o1.mp4"));
        // (a final directory that is not there: the file is cut and has its moov by the time the move fails)
        Check.Throws<DirectoryNotFoundException>(() => Mp4Muxer.Recover(r2, Mp4Muxer.JournalPath(r2), Path.Combine(dir.File("nodir"), "o2.mp4")));
        Check.True(File.Exists(r2) && File.Exists(Mp4Muxer.JournalPath(r2)), "both files stay");
        var b = Mp4Muxer.Recover(r2, Mp4Muxer.JournalPath(r2), dir.File("o2.mp4"));
        Check.NotNull(a); Check.NotNull(b);
        Check.Equal(a!.VideoSamples, b!.VideoSamples, "video samples");
        Check.Equal(a.Duration100ns, b.Duration100ns, "duration");
        Check.Equal(a.SizeBytes, b.SizeBytes, "size");
        Check.True(RebasedMoov(dir.File("o1.mp4")).AsSpan().SequenceEqual(RebasedMoov(dir.File("o2.mp4"))), "the same moov");
        Check.True(SameBytes(dir.File("o1.mp4"), dir.File("o2.mp4")), "the same file, byte for byte (the torn sample and the old moov are not data)");
    }

    [UnitTest]
    public static void A_finish_whose_move_fails_keeps_both_files_for_recover_and_discard_removes_them()
    {
        using var dir = new TempDir();
        var input = MakeInput(10, realAudio: false);
        string rec = dir.File("take.mp4.rec"), idx = Mp4Muxer.JournalPath(rec), final = dir.File("take.mp4");
        int attempts = Mp4Muxer.MoveAttempts, pause = Mp4Muxer.MovePauseMs;
        Mp4Muxer.MoveAttempts = 3; Mp4Muxer.MovePauseMs = 10;
        try
        {
            using var mux = Mp4Muxer.CreateProgressive(rec, final, input.Video, input.Audio, Origin);
            Feed(mux, input, 10);
            Mp4Muxer.RecoveryResult? r;
            using (var holder = new FileStream(rec, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))   // no FileShare.Delete: the move is refused
            {
                Check.Throws<IOException>(() => mux.Finish(), "the file is held");
                Check.True(File.Exists(rec) && File.Exists(idx) && !File.Exists(final), "nothing moved, nothing deleted");
            }
            r = Mp4Muxer.Recover(rec, idx, final);
            Check.NotNull(r, "recoverable once the handle is released");
            Check.Equal(10, r!.VideoSamples);
            Check.False(File.Exists(rec) || File.Exists(idx));

            // And a recording given up after a failed finish leaves nothing.
            string rec2 = dir.File("two.mp4.rec"), final2 = dir.File("two.mp4");
            using var mux2 = Mp4Muxer.CreateProgressive(rec2, final2, input.Video, input.Audio, Origin);
            Feed(mux2, input, 10);
            using (var holder = new FileStream(rec2, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                Check.Throws<IOException>(() => mux2.Finish());
            mux2.Discard();
            Check.False(File.Exists(rec2) || File.Exists(Mp4Muxer.JournalPath(rec2)) || File.Exists(final2), "discarded");
        }
        finally { Mp4Muxer.MoveAttempts = attempts; Mp4Muxer.MovePauseMs = pause; }
    }
}
