using System.IO;
using System.Security.Cryptography;
using Clink.Audio;
using Clink.Encode;
using Clink.Engine;

namespace Clink.Tests;

/// <summary>
/// <see cref="ClipWriter"/> and the incremental <see cref="ClipMuxWriter"/> under it. The golden tests write fixed synthetic clips
/// (no Media Foundation, no encoder, no randomness; the muxer stamps no clock) and compare the SHA-256 of the files with constants baked
/// in before the writer was split up: a refactor of the writer that changes a byte of a clip fails them.
/// </summary>
internal static class ClipWriterTests
{
    private const long Origin = 1_000_000_000;
    private const uint Fps = 30;
    private static readonly int[] Starts = { -480, 1201, 2400 };
    private static readonly string[] Names = { "Mix", "Game", "Mic" };

    // The SHA-256 of the clips below as the one-shot ClipWriter made them.
    private const string GoldenAligned = "00B13FBC7E347B2975116BF421A18D409ED464064E80CC9FB0E6F4651D7E75DF";
    private const string GoldenNotAligned = "7F114916FC6EF09EE2389149D6DA7E283AC447E0D0A4A6320F3151E64F329253";
    private const string GoldenNoFps = "D4D5405D3BA3AF9DA255B4FAFF67508CBB014786A7C4B2792C7E3C6A1505CBFD";

    /// <summary>An Annex B access unit: the keyframe of <see cref="TimelineProbe.Idr"/> (it carries the parameter sets), or a made-up non-IDR slice.</summary>
    private static byte[] Frame(int i, bool key)
    {
        if (key) return (byte[])TimelineProbe.Idr.Clone();
        var au = new List<byte> { 0, 0, 0, 1, 0x41, 0x9A };
        int n = 40 + (i * 37) % 90;
        for (int k = 0; k < n; k++) au.Add((byte)(0x10 + (i * 13 + k * 7) % 200));   // (no zero bytes: no start code can form)
        return au.ToArray();
    }

    /// <summary>96 frames at 30 fps in 3 GOPs of 32; the capture times jitter by up to 2 ms around the grid (so the snapping works).</summary>
    private static List<VideoSample> MakeVideo(int count = 96, bool duplicates = false)
    {
        var list = new List<VideoSample>();
        for (int i = 0; i < count; i++)
        {
            long pts = Origin + (long)Math.Round(i * 10_000_000.0 / Fps) + ((i * 7) % 5 - 2) * 10_000;
            if (duplicates && i % 9 == 4) pts = list[^1].Pts;   // a timestamp that does not move on
            list.Add(new VideoSample { Pts = pts, Keyframe = i % 32 == 0, Data = Frame(i, i % 32 == 0) });
        }
        return list;
    }

    private static List<AudioTrackDesc> MakeDescs(int tracks)
    {
        var l = new List<AudioTrackDesc>();
        for (int t = 0; t < tracks; t++)
            l.Add(new AudioTrackDesc { Name = Names[t], AudioSpecificConfig = new byte[] { 0x11, 0x90 }, Bitrate = 192000 + t * 1000, SampleRate = 48000, Channels = 2 });
        return l;
    }

    private static List<AacFrame> MakeFrames(int t, int count)
    {
        double unit = 10_000_000.0 / 48000;
        long start = Origin + (long)Math.Round(Starts[t] * unit);
        var l = new List<AacFrame>();
        for (int k = 0; k < count; k++)
        {
            var data = new byte[20 + (k * 3 + t) % 11];
            for (int j = 0; j < data.Length; j++) data[j] = (byte)(k * 5 + t * 31 + j);
            l.Add(new AacFrame(start + (long)Math.Round(k * AacEncoder.FrameSamples * unit), data));
        }
        return l;
    }

    /// <summary>Audio for <paramref name="tracks"/> tracks that runs a little past the 96 video frames (3.2 s = 150 frames of 1024 samples); the last track empty when <paramref name="emptyLast"/>.</summary>
    private static List<(AudioTrackDesc desc, List<AacFrame> frames)> MakeAudio(int tracks, bool emptyLast = false)
    {
        var descs = MakeDescs(tracks);
        var l = new List<(AudioTrackDesc, List<AacFrame>)>();
        for (int t = 0; t < tracks; t++) l.Add((descs[t], emptyLast && t == tracks - 1 ? new List<AacFrame>() : MakeFrames(t, 152 - t)));
        return l;
    }

    private static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static void Golden(string name, string expected, Func<string, string[]> write, bool aligned, string[] names, long[] offsets)
    {
        using var dir = new TempDir();
        string path = dir.File(name + ".mp4");
        string[] written = write(path);
        Check.Equal(names, written, name + ": tracks written");
        var tracks = Trimmer.ReadClipTracks(path);
        Check.NotNull(tracks, name + ": reads back as a Clink file");
        Check.Equal(aligned, tracks!.Marked, name + ": Marked");
        Check.Equal(names, tracks.Audio.Select(a => a.Name).ToArray(), name + ": names");
        Check.Equal(offsets, tracks.Audio.Select(a => a.StartFrames).ToArray(), name + ": offsets");
        Check.False(File.Exists(path + ".tmp"), name + ": no temp file left");
        string actual = Sha(path);
        Check.True(actual == expected, $"{name}: SHA-256 is {actual}, not the golden {expected}");
    }

    [UnitTest]
    public static void Golden_aligned_clip_with_three_tracks_is_byte_for_byte_what_it_always_was()
        => Golden("aligned", GoldenAligned,
            p => ClipWriter.Write(p, MakeVideo(), "h264", 16, 16, null, MakeAudio(3), Origin, Fps, aligned: true),
            true, Names, new long[] { -480, 1201, 2400 });

    [UnitTest]
    public static void Golden_not_aligned_clip_with_an_empty_track_is_byte_for_byte_what_it_always_was()
        => Golden("notaligned", GoldenNotAligned,
            p => ClipWriter.Write(p, MakeVideo(), "h264", 16, 16, null, MakeAudio(3, emptyLast: true), Origin, Fps, aligned: false),
            false, Names[..2], new long[] { -480, 1201 });

    [UnitTest]
    public static void Golden_clip_without_a_frame_rate_keeps_the_captured_times()
        => Golden("nofps", GoldenNoFps,
            p => ClipWriter.Write(p, MakeVideo(duplicates: true), "h264", 16, 16, null, MakeAudio(2), Origin, 0, aligned: true),
            true, Names[..2], new long[] { -480, 1201 });

    // ---------------------------------------------------------------- the incremental writer

    private static ClipMuxWriter NewProgressive(TempDir dir, string name, int tracks, byte[]? sequenceHeader = null, bool aligned = true)
        => new(ClipOutput.Progressive(dir.File(name + ".mp4.rec"), dir.File(name + ".mp4")), new ClipVideoConfig("h264", 16, 16, Fps, sequenceHeader), MakeDescs(tracks), Origin, aligned);

    /// <summary>Feeds the writer the video with the audio of track 0 <paramref name="lateBy100ns"/> behind (each frame goes in after the first video sample that is that much later than it), the other tracks as ClipWriter does.</summary>
    private static ClipMuxResult FeedLate(ClipMuxWriter w, List<VideoSample> video, List<(AudioTrackDesc desc, List<AacFrame> frames)> audio, long lateBy100ns)
    {
        var ai = new int[audio.Count];
        foreach (var v in video)
        {
            long q = w.NextVideoPts(v.Pts);
            for (int t = 1; t < audio.Count; t++)
                while (ai[t] < audio[t].frames.Count && audio[t].frames[ai[t]].Pts <= q) w.AddAudio(t, audio[t].frames[ai[t]++]);
            w.AddVideo(v.Pts, v.Keyframe, v.Data);
            while (ai[0] < audio[0].frames.Count && audio[0].frames[ai[0]].Pts + lateBy100ns <= q) w.AddAudio(0, audio[0].frames[ai[0]++]);
        }
        for (int t = 0; t < audio.Count; t++)
            while (ai[t] < audio[t].frames.Count) w.AddAudio(t, audio[t].frames[ai[t]++]);
        return w.Finish();
    }

    [UnitTest]
    public static void A_progressive_writer_fed_with_late_audio_makes_the_clip_ClipWriter_makes_from_the_same_input()
    {
        using var dir = new TempDir();
        string fast = dir.File("fast.mp4");
        ClipWriter.Write(fast, MakeVideo(), "h264", 16, 16, null, MakeAudio(3), Origin, Fps, aligned: true);
        ClipMuxResult result;
        using (var w = NewProgressive(dir, "prog", 3))
        {
            Check.False(w.HasVideo);
            result = FeedLate(w, MakeVideo(), MakeAudio(3), 4_000_000);
            Check.True(w.HasVideo);
        }
        Check.Equal(dir.File("prog.mp4"), result.Path);
        Check.False(File.Exists(dir.File("prog.mp4.rec")) || File.Exists(dir.File("prog.mp4.rec.idx")), "the in-progress files are gone");
        Check.Equal(96, result.VideoSamples);
        Check.Equal(new[] { 152, 151, 150 }, result.AudioSamples);
        Check.Equal(Names, result.AudioTracks);
        Check.Equal(new FileInfo(result.Path).Length, result.SizeBytes);
        Check.True(result.Duration100ns > 31_000_000L && result.Duration100ns < 33_000_000L, $"the duration is about 3.2 s ({result.Duration100ns})");

        var ta = Trimmer.ReadClipTracks(fast);
        var tb = Trimmer.ReadClipTracks(result.Path);
        Check.NotNull(ta); Check.NotNull(tb);
        Check.Equal(ta!.Marked, tb!.Marked, "Marked");
        Check.Equal(ta.Audio.Select(x => x.Name).ToArray(), tb.Audio.Select(x => x.Name).ToArray(), "names");
        Check.Equal(ta.Audio.Select(x => x.StartOffset).ToArray(), tb.Audio.Select(x => x.StartOffset).ToArray(), "offsets");
        Check.Equal(ta.AudioEditBoxes.Length, tb.AudioEditBoxes.Length, "edit list boxes");
    }

    [UnitTest]
    public static void A_progressive_writer_fed_with_late_audio_reads_back_through_Media_Foundation_like_the_clip()
    {
        using var dir = new TempDir();
        MixRenderTests.OnMta(() =>
        {
            List<(AudioTrackDesc desc, List<AacFrame> frames)> Real()
            {
                Native.Mf.EnsureStarted();
                var l = new List<(AudioTrackDesc, List<AacFrame>)>();
                double unit = 10_000_000.0 / 48000;
                for (int t = 0; t < 2; t++)
                {
                    using var enc = new AacEncoder(48000, 2, 24000);
                    long start = Origin + (long)Math.Round(Starts[t] * unit);
                    var frames = new List<AacFrame>();
                    var silence = new byte[AacEncoder.FrameSamples * 4];
                    int k = 0;
                    for (int n = 0; n < 80; n++)
                        enc.Encode(silence, start, (_, data) => { if (k < 76) frames.Add(new AacFrame(start + (long)Math.Round(k++ * AacEncoder.FrameSamples * unit), data)); });
                    l.Add((new AudioTrackDesc { Name = Names[t], AudioSpecificConfig = enc.AudioSpecificConfig, Bitrate = enc.BytesPerSecond * 8, SampleRate = 48000, Channels = 2 }, frames));
                }
                return l;
            }
            var audio = Real();
            string fast = dir.File("fast.mp4");
            ClipWriter.Write(fast, MakeVideo(64), "h264", 16, 16, null, audio, Origin, Fps);
            ClipMuxResult result;
            using (var w = new ClipMuxWriter(ClipOutput.Progressive(dir.File("p.mp4.rec"), dir.File("p.mp4")), new ClipVideoConfig("h264", 16, 16, Fps), audio.Select(a => a.desc).ToList(), Origin))
                result = FeedLate(w, MakeVideo(64), audio, 4_000_000);
            var (da, sa) = MediaProbe.Probe(fast, hideEditLists: true);
            var (db, sb) = MediaProbe.Probe(result.Path, hideEditLists: true);
            Check.Equal(3, sb.Count);
            Check.Equal(da, db, "duration");
            Check.Equal(sa, sb, "streams (counts, first and last timestamps, deltas, keyframes, bytes)");
            Check.Equal(Trimmer.ReadClipTracks(fast)!.Audio.Select(x => x.StartOffset).ToArray(), Trimmer.ReadClipTracks(result.Path)!.Audio.Select(x => x.StartOffset).ToArray(), "offsets");
        });
    }

    [UnitTest]
    public static void Samples_before_the_parameter_sets_are_held_and_written_not_dropped()
    {
        using var dir = new TempDir();
        // Two non-IDR frames and an audio frame come first; the keyframe that carries SPS/PPS is the third video sample.
        List<VideoSample> Video()
        {
            var v = MakeVideo(40);
            v[0].Keyframe = false; v[0].Data = Frame(1, false);
            v[1].Keyframe = false; v[1].Data = Frame(2, false);
            v[2].Keyframe = true; v[2].Data = Frame(0, true);
            return v;
        }
        ClipMuxResult Run(string name, byte[]? seq)
        {
            using var w = NewProgressive(dir, name, 2, seq);
            return FeedLate(w, Video(), MakeAudio(2), 0);
        }
        var deferred = Run("deferred", null);
        Check.Equal(40, deferred.VideoSamples, "the frames before the keyframe are in the file");
        Check.Equal(152, deferred.AudioSamples[0], "and so is the audio that came before the parameter sets");
        // The same input with the parameter sets handed over up front gives the same file: the deferral changes nothing but when the muxer is created.
        var given = Run("given", TimelineProbe.Idr);
        Check.Equal(Sha(given.Path), Sha(deferred.Path), "deferred and up-front parameter sets make the same bytes");
        Check.True(Trimmer.ReadClipTracks(deferred.Path)!.Marked);
    }

    [UnitTest]
    public static void Finish_without_video_or_without_parameter_sets_throws_and_makes_no_file()
    {
        using var dir = new TempDir();
        using (var w = NewProgressive(dir, "none", 2))
        {
            w.AddAudio(0, MakeFrames(0, 1)[0]);
            Check.False(w.HasVideo);
            Check.Contains(Check.Throws<InvalidOperationException>(() => w.Finish()).Message, "Nothing recorded");
        }
        using (var w = NewProgressive(dir, "noparams", 2))
        {
            w.AddVideo(Origin, false, Frame(1, false));
            Check.True(w.HasVideo);
            Check.Contains(Check.Throws<InvalidOperationException>(() => w.Finish()).Message, "SPS/PPS");
        }
        Check.Equal(0, Directory.GetFileSystemEntries(dir.Path).Length, "no file was made");
        Check.Throws<InvalidOperationException>(() => ClipWriter.Write(dir.File("clip.mp4"), new List<VideoSample>(), "h264", 16, 16, null, MakeAudio(2), Origin, Fps), "ClipWriter with no video");
        Check.Equal(0, Directory.GetFileSystemEntries(dir.Path).Length, "and no temp file");
    }

    [UnitTest]
    public static void Discard_removes_a_progressive_recording_and_Dispose_keeps_it_for_recovery()
    {
        using var dir = new TempDir();
        string rec = dir.File("d.mp4.rec"), idx = Mp4Muxer.JournalPath(rec);
        var video = MakeVideo(20);
        var audio = MakeAudio(2);
        using (var w = NewProgressive(dir, "d", 2))
        {
            foreach (var v in video) w.AddVideo(v.Pts, v.Keyframe, v.Data);
            w.AddAudio(0, audio[0].frames[0]);
            w.Flush(false);
            Check.True(File.Exists(rec) && File.Exists(idx), "the recording is on disk");
            w.Discard();
            Check.False(File.Exists(rec) || File.Exists(idx) || File.Exists(dir.File("d.mp4")), "Discard took it away");
            Check.Throws<ObjectDisposedException>(() => w.AddVideo(Origin, true, Frame(0, true)), "nothing is added to a discarded writer");
            w.Discard();   // and it does not mind being asked again
        }

        // Dispose without Finish: the recording stays and Recover finishes it, with every sample that was added.
        string rec2 = dir.File("e.mp4.rec"), idx2 = Mp4Muxer.JournalPath(rec2);
        using (var w = NewProgressive(dir, "e", 2))
        {
            for (int i = 0; i < video.Count; i++) { }
            foreach (var v in MakeVideo(20)) w.AddVideo(v.Pts, v.Keyframe, v.Data);
            for (int k = 0; k < 10; k++) { w.AddAudio(0, audio[0].frames[k]); w.AddAudio(1, audio[1].frames[k]); }
            w.Flush(durable: true);
        }
        Check.True(File.Exists(rec2) && File.Exists(idx2), "Dispose keeps the in-progress file and its journal");
        Check.False(File.Exists(dir.File("e.mp4")), "and it is not a clip yet");
        var r = Mp4Muxer.Recover(rec2, idx2, dir.File("e.mp4"));
        Check.NotNull(r, "recovered");
        Check.Equal(20, r!.VideoSamples);
        Check.Equal(Names[..2], r.AudioTracks);
        Check.True(Trimmer.ReadClipTracks(dir.File("e.mp4"))!.Marked);
        Check.False(File.Exists(rec2) || File.Exists(idx2), "the leftovers are gone");
    }

    [UnitTest]
    public static void A_clip_writer_without_a_finish_leaves_nothing_and_flush_is_a_no_op()
    {
        using var dir = new TempDir();
        using (var w = new ClipMuxWriter(ClipOutput.FastStart(dir.File("c.mp4")), new ClipVideoConfig("h264", 16, 16, Fps), MakeDescs(1), Origin))
        {
            foreach (var v in MakeVideo(5)) { w.AddVideo(v.Pts, v.Keyframe, v.Data); w.Flush(true); }
            Check.True(w.VideoElapsed100ns > 0);
            Check.True(w.DurationLimit100ns > 10L * 3600 * 10_000_000, "about 13 h");
        }
        Check.Equal(0, Directory.GetFileSystemEntries(dir.Path).Length, "the temp file is gone, and no clip was made");
    }
}

