using System.Diagnostics;
using System.IO;
using Clink.Audio;
using Clink.Encode;
using Clink.Engine;
using Clink.Native;

namespace Clink.Tests;

/// <summary>
/// <see cref="RecordingSink"/> (the writer of manual recordings) driven directly by fake producer threads with synthetic frames: no GPU,
/// no encoder. The files it makes are checked the way the other muxer tests do; the one test with a real <see cref="AudioTrack"/> needs
/// Media Foundation's AAC encoder and skips without it (unless CLINK_TESTS_NO_SKIP=1).
/// </summary>
internal static class RecordingSinkTests
{
    private const long T0 = 1_000_000_000;
    private const long Period = 333_333;          // 30 fps
    private const long AudioDur = 213_333;        // 1024 samples at 48 kHz
    private static readonly string[] Names = { "Mix", "Game", "Mic" };

    private static byte[] Frame(int i, bool key)
    {
        if (key) return (byte[])TimelineProbe.Idr.Clone();
        var au = new List<byte> { 0, 0, 0, 1, 0x41, 0x9A };
        int n = 40 + (i * 37) % 90;
        for (int k = 0; k < n; k++) au.Add((byte)(0x10 + (i * 13 + k * 7) % 200));
        return au.ToArray();
    }

    private static List<AudioTrackDesc> Descs(int tracks)
        => Enumerable.Range(0, tracks).Select(t => new AudioTrackDesc { Name = Names[t], AudioSpecificConfig = new byte[] { 0x11, 0x90 }, Bitrate = 192000, SampleRate = 48000, Channels = 2 }).ToList();

    private sealed class Written
    {
        public readonly List<(int track, long pts, bool key)> Items = new();
        public void Add(int track, long pts, bool key) { lock (Items) Items.Add((track, pts, key)); }
        public List<(int track, long pts, bool key)> Snapshot() { lock (Items) return Items.ToList(); }
        public List<(long pts, bool key)> Video() => Snapshot().Where(x => x.track < 0).Select(x => (x.pts, x.key)).ToList();
        public List<long> Audio(int track) => Snapshot().Where(x => x.track == track).Select(x => x.pts).ToList();
    }

    private static RecordingSinkConfig Cfg(TempDir dir, Written log, int tracks = 2, string name = "take")
        => new()
        {
            InProgressPath = dir.File(name + ".mp4.rec"),
            FinalPath = dir.File(name + ".mp4"),
            Video = new ClipVideoConfig("h264", 16, 16, 30),
            Audio = Descs(tracks),
            Game = "Test Game",
            Clock = () => T0 + 3_600_000_000L,
            Written = log.Add,
        };

    private static long VideoPts(int i) => T0 + i * Period;
    private static long AudioPts(int k) => T0 + k * AudioDur;

    private static void FeedVideo(RecordingSink s, int from, int to, int gop = 30)
    {
        for (int i = from; i < to; i++) s.OnVideo(VideoPts(i), i % gop == 0, Frame(i, i % gop == 0));
    }

    private static void FeedAudio(RecordingSink s, int track, int from, int to)
    {
        for (int k = from; k < to; k++) s.OnAudio(track, new AacFrame(AudioPts(k), new byte[20 + (k + track) % 7]));
    }

    /// <summary>A sink with its writer thread running (the constructor does not start it).</summary>
    private static RecordingSink Sink(RecordingSinkConfig cfg)
    {
        var s = new RecordingSink(cfg);
        s.Start();
        return s;
    }

    /// <summary>The movie duration (mvhd) of a file, in seconds.</summary>
    private static double MovieSeconds(string path)
    {
        byte[] b = File.ReadAllBytes(path);
        int at = b.AsSpan().IndexOf("mvhd"u8);
        Check.True(at >= 0, "the file has a movie header");
        var s = b.AsSpan(at + 4);
        if (s[0] == 1) return System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(s.Slice(4 + 16 + 4)) / (double)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(s.Slice(4 + 16));
        return System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(s.Slice(4 + 8 + 4)) / (double)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(s.Slice(4 + 8));
    }

    private static RecordingResult Stopped(RecordingSink s, bool catchUp = false)
    {
        s.Stop(RecordingEndReason.UserStopped, catchUp);
        return Done(s);
    }

    private static RecordingResult Done(RecordingSink s)
    {
        Check.True(s.Completion.Wait(20000), "the recording finishes");
        return s.Completion.Result;
    }

    private static void ReadsBack(string? path, string[] names, string what)
    {
        Check.NotNull(path, what + ": a file");
        Check.True(File.Exists(path!), what + ": the file is there");
        Check.False(File.Exists(path + ".rec") || File.Exists(path + ".rec.idx"), what + ": no in-progress files left");
        var tracks = Trimmer.ReadClipTracks(path!);
        Check.NotNull(tracks, what + ": reads back as a Clink file");
        Check.Equal(names, tracks!.Audio.Select(a => a.Name).ToArray(), what + ": audio tracks");
    }

    [UnitTest]
    public static void RecordingSink_first_video_sample_is_a_keyframe_when_the_producers_start_mid_GOP()
    {
        using var dir = new TempDir();
        var log = new Written();
        var s = Sink(Cfg(dir, log, 3));
        FeedAudio(s, 0, 0, 100); FeedAudio(s, 1, 0, 100); FeedAudio(s, 2, 0, 100);   // audio of the time before the first keyframe
        FeedVideo(s, 5, 75);                                                          // the keyframes are at 30 and 60
        FeedAudio(s, 0, 100, 110); FeedAudio(s, 1, 100, 110); FeedAudio(s, 2, 100, 110);
        var r = Stopped(s);
        var video = log.Video();
        Check.True(video.Count > 0 && video[0].key, "the first video sample is a keyframe");
        Check.Equal(VideoPts(30), video[0].pts, "and it is the first keyframe");
        Check.Equal(45, video.Count, "every sample from there");
        long origin = video[0].pts;
        foreach (int t in new[] { 0, 1, 2 })
        {
            var audio = log.Audio(t);
            Check.True(audio.Count > 0, "audio of track " + t);
            Check.True(audio.All(p => p + AudioDur > origin), "no audio from before the first keyframe (track " + t + ")");
        }
        Check.Equal(RecordingEndReason.UserStopped, r.Reason);
        Check.Null(r.Error);
        Check.Equal("Test Game", r.Game);
        Check.Equal(0L, r.DroppedVideoFrames);
        Check.Equal(Names, r.AudioTracks, "the audio tracks are the enabled set");
        ReadsBack(r.Path, Names, "recording");
        Check.Equal(dir.File("take.mp4"), r.Path);
        Check.True(r.SizeBytes == new FileInfo(r.Path!).Length && r.SizeBytes > 0);
        Check.True(r.DurationSeconds > 1.3 && r.DurationSeconds < 1.5, $"the duration is the video's span ({r.DurationSeconds})");
    }

    [UnitTest]
    public static void RecordingSink_timestamps_of_every_track_strictly_increase_in_the_output()
    {
        using var dir = new TempDir();
        var log = new Written();
        var s = Sink(Cfg(dir, log, 2));
        int[] order = { 0, 1, 2, 2, 1, 3, 4, 4, 0, 5, 6, 7, 7, 6, 8 };
        var video = new Thread(() => { foreach (int i in order) s.OnVideo(VideoPts(i), i == 0, Frame(i, i == 0)); });
        var audio = new Thread(() => { foreach (int k in order) { s.OnAudio(0, new AacFrame(AudioPts(k), new byte[24])); s.OnAudio(1, new AacFrame(AudioPts(k), new byte[25])); } });
        video.Start(); audio.Start(); video.Join(); audio.Join();
        var r = Stopped(s);
        var v = log.Video().Select(x => x.pts).ToList();
        Check.Equal(Enumerable.Range(0, 9).Select(VideoPts).ToList(), v, "video: each timestamp once, in order");
        foreach (int t in new[] { 0, 1 })
            Check.Equal(Enumerable.Range(0, 9).Select(AudioPts).ToList(), log.Audio(t), "audio track " + t);
        ReadsBack(r.Path, Names[..2], "recording");
    }

    [UnitTest]
    public static void RecordingSink_producers_are_not_slowed_down_by_a_slow_writer()
    {
        using var dir = new TempDir();
        var log = new Written();
        var cfg = Cfg(dir, log, 2);
        cfg.BeforeWrite = () => Thread.Sleep(3);
        var s = Sink(cfg);
        var ticks = new List<long>();
        var frames = Enumerable.Range(0, 100).Select(i => Frame(i, i % 30 == 0)).ToArray();
        for (int i = 0; i < 100; i++)
        {
            long t0 = Stopwatch.GetTimestamp();
            s.OnVideo(VideoPts(i), i % 30 == 0, frames[i]);
            ticks.Add(Stopwatch.GetTimestamp() - t0);
            t0 = Stopwatch.GetTimestamp();
            s.OnAudio(0, new AacFrame(AudioPts(i), new byte[24]));
            ticks.Add(Stopwatch.GetTimestamp() - t0);
        }
        long queued = s.QueueBytes;
        ticks.Sort();
        double median = ticks[ticks.Count / 2] * 1000.0 / Stopwatch.Frequency, p99 = ticks[(int)(ticks.Count * 0.99)] * 1000.0 / Stopwatch.Frequency, max = ticks[^1] * 1000.0 / Stopwatch.Frequency;
        Check.True(queued > 0, "the writer is behind (that is the test)");
        // (the writer sleeps 3 ms per sample: a call that waited for it would sit near that; a loaded machine only adds scheduling noise)
        Check.True(median < 1.0, $"a typical call takes under 1 ms (median {median:0.000} ms)");
        Check.True(p99 < 5.0, $"99% of the calls take under 5 ms (p99 {p99:0.000} ms)");
        _ = max;   // (reported only: a single scheduling hiccup on a loaded machine says nothing about the writer; median and p99 do)
        var r = Stopped(s);
        Check.Equal(100, log.Video().Count, "and nothing is lost");
        Check.Equal(0L, r.DroppedVideoFrames);
        Check.Equal(0L, s.QueueBytes, "the queue is empty at the end");
        ReadsBack(r.Path, Names[..1], "recording");
    }

    [UnitTest]
    public static void RecordingSink_over_budget_drops_video_until_the_next_keyframe_and_the_file_still_reads_back()
    {
        using var dir = new TempDir();
        var log = new Written();
        var cfg = Cfg(dir, log, 2);
        cfg.BudgetBytes = 5000;
        using var open = new ManualResetEventSlim(false);
        cfg.BeforeWrite = () => open.Wait(10000);
        var s = Sink(cfg);
        try
        {
            FeedAudio(s, 0, 0, 5);
            FeedVideo(s, 0, 90);                       // three GOPs into a queue that cannot drain
            Check.True(s.Status.Dropped > 0, "video was dropped");
            long droppedSoFar = s.DroppedVideoFrames;
            open.Set();
            var sw = Stopwatch.StartNew();
            while (s.QueueBytes > 0 && sw.ElapsedMilliseconds < 10000) Thread.Sleep(5);
            Check.Equal(0L, s.QueueBytes, "the queue drains");
            FeedVideo(s, 90, 120);                     // a keyframe at 90: the video resumes
            FeedAudio(s, 1, 0, 5);
        }
        finally { open.Set(); }
        var r = Stopped(s);
        var video = log.Video();
        Check.True(r.DroppedVideoFrames > 0, "dropped frames are counted");
        Check.Equal(VideoPts(119), video[^1].pts, "the video runs to its end");
        Check.True(video.Count < 120, "frames are missing");
        int jumps = 0;
        for (int i = 1; i < video.Count; i++)
        {
            if (video[i].pts - video[i - 1].pts > Period * 3 / 2)
            {
                jumps++;
                Check.True(video[i].key, "video resumes on a keyframe");
            }
        }
        Check.True(jumps >= 1, "there is a gap");
        Check.True(log.Audio(0).Count == 5 && log.Audio(1).Count == 5, "the audio is not dropped");
        Check.Equal(RecordingEndReason.UserStopped, r.Reason);
        ReadsBack(r.Path, Names[..2], "recording");
    }

    [UnitTest]
    public static void RecordingSink_ends_at_MaxDuration_with_a_readable_file()
    {
        using var dir = new TempDir();
        var log = new Written();
        var cfg = Cfg(dir, log, 2);
        cfg.MaxDuration = TimeSpan.FromSeconds(2);
        var s = Sink(cfg);
        FeedAudio(s, 0, 0, 5); FeedAudio(s, 1, 0, 5);
        FeedVideo(s, 0, 150);
        var r = Done(s);
        Check.Equal(RecordingEndReason.MaxDuration, r.Reason);
        Check.Null(r.Error, "a limit is not an error");
        var video = log.Video();
        Check.True(video.Count >= 55 && video.Count <= 61 && video[^1].pts < T0 + 20_000_000, $"two seconds of video ({video.Count} samples)");
        Check.True(r.DurationSeconds > 1.8 && r.DurationSeconds <= 2.0, $"duration {r.DurationSeconds}");
        ReadsBack(r.Path, Names[..2], "recording");
    }

    [UnitTest]
    public static void RecordingSink_ends_at_MaxBytes_with_a_readable_file()
    {
        using var dir = new TempDir();
        var log = new Written();
        var cfg = Cfg(dir, log, 1);
        cfg.MaxBytes = 6000;
        var s = Sink(cfg);
        FeedVideo(s, 0, 150);
        var r = Done(s);
        Check.Equal(RecordingEndReason.MaxSize, r.Reason);
        Check.True(log.Video().Count < 150 && log.Video().Count > 10);
        ReadsBack(r.Path, Array.Empty<string>(), "recording");
    }

    [UnitTest]
    public static void RecordingSink_ends_at_a_gap_in_the_video_and_keeps_what_came_before()
    {
        using var dir = new TempDir();
        var log = new Written();
        var s = Sink(Cfg(dir, log, 2));
        FeedAudio(s, 0, 0, 10); FeedAudio(s, 1, 0, 10);
        FeedVideo(s, 0, 60);
        s.OnVideo(VideoPts(59) + 60_000_000, true, Frame(0, true));   // 6 s later: the capture stalled
        FeedVideo(s, 60, 70);                                          // (the sink has given up on this)
        var r = Done(s);
        Check.Equal(RecordingEndReason.Discontinuity, r.Reason);
        Check.Equal(60, log.Video().Count, "the video before the gap is all there");
        ReadsBack(r.Path, Names[..2], "recording");
    }

    [UnitTest]
    public static void RecordingSink_ends_when_the_disk_runs_low()
    {
        using var dir = new TempDir();
        var log = new Written();
        var cfg = Cfg(dir, log, 1);
        long free = long.MaxValue;
        cfg.FreeSpace = () => Interlocked.Read(ref free);
        cfg.SpaceCheckInterval = TimeSpan.FromMilliseconds(20);
        var s = Sink(cfg);
        FeedVideo(s, 0, 40);
        Thread.Sleep(100);
        Interlocked.Exchange(ref free, 100L << 20);
        var r = Done(s);
        Check.Equal(RecordingEndReason.DiskFull, r.Reason);
        Check.Equal(40, log.Video().Count);
        ReadsBack(r.Path, Array.Empty<string>(), "recording");
    }

    [UnitTest]
    public static void RecordingSink_clean_stop_keeps_the_audio_up_to_the_stop_and_drops_later_video()
    {
        using var dir = new TempDir();
        var log = new Written();
        var cfg = Cfg(dir, log, 2);
        long end = T0 + 20_000_000;
        cfg.Clock = () => end;
        cfg.AudioCatchUp = TimeSpan.FromSeconds(30);   // (the audio below arrives; the timeout is not what ends this)
        var s = Sink(cfg);
        FeedVideo(s, 0, 60); FeedAudio(s, 0, 0, 90); FeedAudio(s, 1, 0, 90);
        s.Stop(RecordingEndReason.UserStopped);
        FeedVideo(s, 61, 90);                       // after the stop point
        Check.False(s.Completion.IsCompleted, "the sink waits for the audio to reach the stop");
        FeedAudio(s, 0, 90, 100); FeedAudio(s, 1, 90, 100);
        var r = Done(s);
        Check.Equal(RecordingEndReason.UserStopped, r.Reason);
        Check.True(log.Video().All(v => v.pts <= end), "no video after the stop");
        Check.Equal(60, log.Video().Count);
        foreach (int t in new[] { 0, 1 })
        {
            var audio = log.Audio(t);
            Check.True(audio.Any(p => p + AudioDur >= end), "audio up to the stop point (track " + t + ")");
            Check.True(audio.All(p => p < end), "and none after it (track " + t + ")");
        }
        ReadsBack(r.Path, Names[..2], "recording");
        // The movie lasts as long as its longest track: audio past the stop point would freeze the last video frame.
        double movie = MovieSeconds(r.Path!);
        Check.True(movie > 1.9 && movie < 2.0 + AudioDur / 1e7 + 0.01, $"the file ends with the video ({movie:0.000} s)");
    }

    [UnitTest]
    public static void RecordingSink_clean_stop_without_audio_tracks_still_takes_the_last_in_flight_frames()
    {
        using var dir = new TempDir();
        var log = new Written();
        var cfg = Cfg(dir, log, 0);
        cfg.Clock = () => VideoPts(35);
        using var hold = new ManualResetEventSlim(false);
        cfg.BeforeWrite = () => hold.Wait(10000);   // the writer waits (its queue is not empty), so the late frames cannot miss the stop wait
        var s = Sink(cfg);
        FeedVideo(s, 0, 31);
        var sw = Stopwatch.StartNew();
        s.Stop(RecordingEndReason.UserStopped);
        FeedVideo(s, 31, 36);                       // the encoder's frames still on their way when the stop came
        hold.Set();
        var r = Done(s);
        Check.True(sw.ElapsedMilliseconds >= 90, $"it waits a moment ({sw.ElapsedMilliseconds} ms)");
        Check.Equal(36, log.Video().Count, "and has them");
        ReadsBack(r.Path, Array.Empty<string>(), "recording");
    }

    [UnitTest]
    public static void RecordingSink_clean_stop_does_not_wait_long_for_audio_that_never_comes()
    {
        using var dir = new TempDir();
        var log = new Written();
        var cfg = Cfg(dir, log, 2);
        cfg.Clock = () => T0 + 20_000_000;
        cfg.AudioCatchUp = TimeSpan.FromMilliseconds(300);
        var s = Sink(cfg);
        FeedVideo(s, 0, 30); FeedAudio(s, 0, 0, 20);
        var sw = Stopwatch.StartNew();
        s.Stop(RecordingEndReason.UserStopped);
        var r = Done(s);
        Check.True(sw.ElapsedMilliseconds >= 280 && sw.ElapsedMilliseconds < 10000, $"it gives up after the catch-up time ({sw.ElapsedMilliseconds} ms)");
        ReadsBack(r.Path, Names[..1], "recording");
    }

    [UnitTest]
    public static void RecordingSink_stopping_an_empty_recording_discards_it_and_leaves_no_files()
    {
        using var dir = new TempDir();
        var log = new Written();
        var r = Stopped(Sink(Cfg(dir, log, 2)));
        Check.Equal(RecordingEndReason.Empty, r.Reason);
        Check.Equal("Nothing recorded", r.Error);
        Check.Null(r.Path);
        Check.Equal(0, Directory.GetFileSystemEntries(dir.Path).Length, "nothing left in the folder");

        // Audio and non-key frames only: still nothing.
        var s = Sink(Cfg(dir, log, 2));
        FeedAudio(s, 0, 0, 20); FeedAudio(s, 1, 0, 20);
        FeedVideo(s, 1, 25);
        r = Stopped(s);
        Check.Equal(RecordingEndReason.Empty, r.Reason);
        Check.Null(r.Path);
        Check.Equal(0, Directory.GetFileSystemEntries(dir.Path).Length, "nothing left in the folder");
    }

    [UnitTest]
    public static void RecordingSink_a_failing_writer_ends_the_recording_with_WriteFailed_and_keeps_the_data()
    {
        using var dir = new TempDir();
        var log = new Written();
        var cfg = Cfg(dir, log, 1);
        int calls = 0;
        cfg.BeforeWrite = () => { if (++calls == 20) throw new IOException("the disk went away"); };
        var s = Sink(cfg);
        FeedVideo(s, 0, 60);
        var r = Done(s);
        Check.Equal(RecordingEndReason.WriteFailed, r.Reason);
        Check.Contains(r.Error, "the disk went away");
        Check.True(r.Path != null && File.Exists(r.Path) || File.Exists(dir.File("take.mp4.rec")), "the data is kept (recovered at once, or left for the next start)");
        Check.Equal(19, log.Video().Count);
    }

    [UnitTest]
    public static void RecordingSink_a_full_disk_error_is_reported_as_DiskFull()
    {
        using var dir = new TempDir();
        var log = new Written();
        var cfg = Cfg(dir, log, 1);
        int calls = 0;
        cfg.BeforeWrite = () => { if (++calls == 5) throw new IOException("There is not enough space on the disk.", unchecked((int)0x80070070)); };
        var s = Sink(cfg);
        FeedVideo(s, 0, 30);
        var r = Done(s);
        Check.Equal(RecordingEndReason.DiskFull, r.Reason);
        Check.NotNull(r.Error);
    }

    [UnitTest]
    public static void RecordingSink_status_counts_what_was_written()
    {
        using var dir = new TempDir();
        var log = new Written();
        var s = Sink(Cfg(dir, log, 1));
        FeedVideo(s, 0, 31);
        var sw = Stopwatch.StartNew();
        while (s.QueueBytes > 0 && sw.ElapsedMilliseconds < 5000) Thread.Sleep(5);
        sw.Restart();
        while (s.Status.Seconds < 0.99 && sw.ElapsedMilliseconds < 5000) Thread.Sleep(5);
        var st = s.Status;
        Check.Equal(dir.File("take.mp4.rec"), st.PartialPath);
        Check.True(st.BytesWritten > 1000 && st.Seconds >= 0.99 && st.Dropped == 0, $"{st}");
        Stopped(s);
    }

    [UnitTest]
    public static void RecordingSink_ends_with_Overflow_when_the_writer_stays_stalled()
    {
        using var dir = new TempDir();
        var log = new Written();
        var cfg = Cfg(dir, log, 1);
        cfg.BudgetBytes = 2000;
        cfg.OverflowLimit = TimeSpan.FromMilliseconds(50);
        using var open = new ManualResetEventSlim(false);
        cfg.BeforeWrite = () => open.Wait(20000);
        var s = Sink(cfg);
        try
        {
            Stopwatch? sinceDrop = null;
            for (int i = 0; i < 2000 && !s.IsFinished; i++)
            {
                s.OnVideo(VideoPts(i), i % 30 == 0, Frame(i, i % 30 == 0));
                if (s.DroppedVideoFrames > 0)
                {
                    sinceDrop ??= Stopwatch.StartNew();
                    if (sinceDrop.ElapsedMilliseconds > 400) break;   // (far past the 50 ms limit)
                    Thread.Sleep(2);
                }
            }
            Check.True(s.DroppedVideoFrames > 0, "video was dropped");
        }
        finally { open.Set(); }
        var r = Done(s);
        Check.Equal(RecordingEndReason.Overflow, r.Reason);
        Check.True(r.DroppedVideoFrames > 0);
        ReadsBack(r.Path, Array.Empty<string>(), "what was queued is still finished");
    }

    [UnitTest]
    public static void RecordingSink_an_engine_stop_with_a_backlog_writes_everything_queued_and_does_not_wait_for_audio()
    {
        using var dir = new TempDir();
        var log = new Written();
        var cfg = Cfg(dir, log, 2);
        cfg.BeforeWrite = () => Thread.Sleep(2);
        cfg.AudioCatchUp = TimeSpan.FromSeconds(30);
        var s = Sink(cfg);
        FeedVideo(s, 0, 100);
        s.Stop(RecordingEndReason.EngineStopped, catchUp: false);   // (no audio ever arrives: a catch-up would wait)
        var sw = Stopwatch.StartNew();
        var r = Done(s);
        Check.True(sw.ElapsedMilliseconds < 15000, "no catch-up wait");
        Check.Equal(RecordingEndReason.EngineStopped, r.Reason);
        Check.Equal(100, log.Video().Count, "the backlog is written");
        ReadsBack(r.Path, Array.Empty<string>(), "recording");
    }

    [UnitTest]
    public static void RecordingSink_concurrent_stops_share_one_result()
    {
        using var dir = new TempDir();
        var log = new Written();
        var s = Sink(Cfg(dir, log, 1));
        FeedAudio(s, 0, 0, 5);
        FeedVideo(s, 0, 40);
        var tasks = new Task<RecordingResult>[8];
        using var start = new ManualResetEventSlim(false);
        var threads = Enumerable.Range(0, tasks.Length).Select(n => new Thread(() =>
        {
            start.Wait();
            s.Stop(n % 2 == 0 ? RecordingEndReason.UserStopped : RecordingEndReason.EngineStopped, catchUp: false);
            tasks[n] = s.Completion;
        })).ToList();
        threads.ForEach(t => t.Start());
        start.Set();
        threads.ForEach(t => t.Join());
        Check.True(tasks.All(t => ReferenceEquals(t, tasks[0])), "one task");
        var r = Done(s);
        Check.True(ReferenceEquals(r, tasks[0].Result), "one result");
        Check.True(r.Reason is RecordingEndReason.UserStopped or RecordingEndReason.EngineStopped, "the first stop's reason wins: " + r.Reason);
        Check.Equal(40, log.Video().Count);
        ReadsBack(r.Path, Names[..1], "recording");
    }

    [UnitTest]
    public static void RecordingSink_keeps_at_most_1500_audio_frames_while_waiting_for_a_keyframe()
    {
        using var dir = new TempDir();
        var log = new Written();

        // No keyframe ever: the audio is dropped, nothing breaks, nothing is left.
        var s = Sink(Cfg(dir, log, 1));
        FeedAudio(s, 0, 0, 1700);
        FeedVideo(s, 1, 25);
        var r = Stopped(s);
        Check.Equal(RecordingEndReason.Empty, r.Reason);
        Check.Null(r.Path);
        Check.Equal(0, log.Audio(0).Count, "audio without video is not written");
        Check.Equal(0, Directory.GetFileSystemEntries(dir.Path).Length, "nothing left");

        // The keyframe comes late: only the newest 1500 frames waited for it.
        var log2 = new Written();
        s = Sink(Cfg(dir, log2, 1));
        FeedAudio(s, 0, 0, 1700);
        FeedVideo(s, 5, 40);                       // the keyframe is at 30
        r = Stopped(s);
        var video = log2.Video();
        Check.True(video.Count > 0 && video[0].key, "the first video sample is a keyframe");
        Check.Equal(1500, log2.Audio(0).Count, "the audio that waited is capped");
        Check.Equal(AudioPts(200), log2.Audio(0)[0], "the oldest went");
        ReadsBack(r.Path, Names[..1], "recording");
    }

    [UnitTest]
    public static void RecordingSink_gives_up_when_the_encoder_never_provides_parameter_sets()
    {
        using var dir = new TempDir();
        var log = new Written();
        var s = Sink(Cfg(dir, log, 1));
        byte[] bare = { 0, 0, 0, 1, 0x65, 0x88, 0x84, 0x00, 0x10 };     // an IDR slice, no SPS / PPS
        s.OnVideo(VideoPts(0), true, bare);
        for (int i = 1; i < 400; i++) s.OnVideo(VideoPts(i), false, Frame(i, false));
        var r = Done(s);
        Check.Equal(RecordingEndReason.WriteFailed, r.Reason);
        Check.Contains(r.Error, "parameter sets");
        Check.Null(r.Path);
        Check.True(r.Error!.Contains("parameter sets"), r.Error);
        Check.Equal(0, Directory.GetFileSystemEntries(dir.Path).Length, "no file was ever made");
        Check.Equal(0L, s.QueueBytes, "the queue is released");

        // A clip is bounded by its ring and keeps waiting.
        using var w = new ClipMuxWriter(ClipOutput.FastStart(dir.File("clip.mp4")), new ClipVideoConfig("h264", 16, 16, 30), Descs(1), T0);
        w.AddVideo(VideoPts(0), true, bare);
        for (int i = 1; i < 400; i++) w.AddVideo(VideoPts(i), false, Frame(i, false));
    }

    [UnitTest]
    public static void RecordingSink_takes_the_parameter_sets_from_the_provider_when_the_config_has_none()
    {
        using var dir = new TempDir();
        var log = new Written();
        var cfg = Cfg(dir, log, 1);
        cfg.SequenceHeaderProvider = () => (byte[])TimelineProbe.Idr.Clone();
        var s = Sink(cfg);
        byte[] bare = { 0, 0, 0, 1, 0x65, 0x88, 0x84, 0x00, 0x10 };
        s.OnVideo(VideoPts(0), true, bare);
        for (int i = 1; i < 40; i++) s.OnVideo(VideoPts(i), false, Frame(i, false));
        var r = Stopped(s);
        Check.Equal(RecordingEndReason.UserStopped, r.Reason);
        Check.Null(r.Error);
        Check.Equal(40, log.Video().Count);
        ReadsBack(r.Path, Array.Empty<string>(), "recording");
    }

    [UnitTest]
    public static void RecordingSink_callbacks_get_the_sink_even_when_it_ends_before_the_owner_has_stored_it()
    {
        using var dir = new TempDir();
        var log = new Written();
        var cfg = Cfg(dir, log, 1);
        RecordingSink? detached = null, completed = null;
        cfg.Detach = x => detached = x;
        cfg.Completed = (x, r) => completed = x;
        var s = new RecordingSink(cfg);                 // not started: nothing can end it yet
        s.Stop(RecordingEndReason.UserStopped, catchUp: false);
        Check.False(s.IsFinished, "it waits for Start");
        s.Start();
        s.Start();                                      // (once)
        Done(s);
        Check.True(ReferenceEquals(detached, s) && ReferenceEquals(completed, s), "both callbacks got the sink");
    }

    [UnitTest]
    public static void RecordingSink_Abort_leaves_no_file_and_no_result_path()
    {
        using var dir = new TempDir();
        var log = new Written();
        var s = Sink(Cfg(dir, log, 1));
        FeedVideo(s, 0, 40);
        var sw = Stopwatch.StartNew();
        while (log.Video().Count < 40 && sw.ElapsedMilliseconds < 10000) Thread.Sleep(5);
        Check.True(File.Exists(dir.File("take.mp4.rec")), "the file exists by now");
        s.Abort();
        var r = Done(s);
        Check.Null(r.Path);
        Check.False(File.Exists(dir.File("take.mp4")), "no finished file");
        Check.Equal(0, Directory.GetFileSystemEntries(dir.Path).Length, "nothing left");

        // Aborted before it ever ran.
        var s2 = new RecordingSink(Cfg(dir, new Written(), 1));
        FeedVideo(s2, 0, 10);
        s2.Abort();
        Done(s2);
        Check.Equal(0, Directory.GetFileSystemEntries(dir.Path).Length, "nothing left");
        Check.Equal(0L, s2.QueueBytes);
    }

    // ---------------------------------------------------------------- the engine's taps

    [UnitTest]
    public static void VideoFanOut_attaching_to_a_running_ring_leaves_no_gap_and_no_duplicate()
    {
        using var dir = new TempDir();
        var log = new Written();
        using var ring = new RingStore(RingStore.MinBytes);
        var fan = new VideoFanOut();
        const int total = 2400;
        int pushed = 0;
        using var go = new ManualResetEventSlim(false);
        var pusher = new Thread(() =>
        {
            for (int i = 0; i < total; i++)
            {
                if (i == 100) go.Wait(10000);                 // the attach happens here, with plenty of the stream still to come
                fan.Push(ring, VideoPts(i), i % 30 == 0, Frame(i, i % 30 == 0));
                Volatile.Write(ref pushed, i + 1);
                long until = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 20_000;   // 50 us: the pusher is still running while the attach happens
                while (Stopwatch.GetTimestamp() < until) Thread.SpinWait(10);
            }
        });
        pusher.Start();
        while (Volatile.Read(ref pushed) < 100) Thread.Yield();
        var s = Sink(Cfg(dir, log, 1));
        go.Set();
        long? origin = fan.Attach(ring, s);
        int pushedAtAttach = Volatile.Read(ref pushed);       // (at least what was in the ring then)
        Check.True(pushedAtAttach < total, $"the attach was mid-stream ({pushedAtAttach} of {total} pushed)");
        pusher.Join();
        Check.True(fan.Detach(s));
        Check.False(fan.Detach(s), "detaching twice");
        var r = Stopped(s);
        var video = log.Video();
        Check.NotNull(origin);
        Check.True(video[0].key && video[0].pts == origin, "starts on the snapshot's keyframe");
        Check.True((video[0].pts - T0) % (30 * Period) == 0, "the keyframe is the last one at the attach");
        for (int i = 1; i < video.Count; i++) Check.Equal(video[i - 1].pts + Period, video[i].pts, $"sample {i} follows the one before");
        Check.Equal(VideoPts(total - 1), video[^1].pts, "and it runs up to the last sample pushed");
        Check.True(video.Count(v => v.pts >= VideoPts(pushedAtAttach)) > 0, "live frames reached the sink after the attach");
        Check.NotNull(r.Path);
    }

    [UnitTest]
    public static void VideoFanOut_without_a_keyframe_in_the_ring_the_recording_starts_at_the_first_live_one()
    {
        using var dir = new TempDir();
        var log = new Written();
        using var ring = new RingStore(RingStore.MinBytes);
        var fan = new VideoFanOut();
        for (int i = 1; i < 10; i++) fan.Push(ring, VideoPts(i), false, Frame(i, false));
        var s = Sink(Cfg(dir, log, 1));
        Check.Null(fan.Attach(ring, s), "no keyframe to start from");
        Check.Throws<InvalidOperationException>(() => fan.Attach(ring, s), "already attached");
        for (int i = 10; i < 70; i++) fan.Push(ring, VideoPts(i), i % 30 == 0, Frame(i, i % 30 == 0));
        Stopped(s);
        var video = log.Video();
        Check.True(video[0].key && video[0].pts == VideoPts(30), "the first live keyframe");
        Check.Equal(40, video.Count);
    }

    [UnitTest]
    public static unsafe void AudioTrack_attaching_a_tap_loses_and_repeats_no_frame()
    {
        MixRenderTests.OnMta(() =>
        {
            using var track = new AudioTrack("T");
            var gate = new object();
            var tapped = new List<AacFrame>();
            List<AacFrame>? head = null;
            int sent = 0;
            const int packets = 700;
            var feeder = new Thread(() =>
            {
                var silence = new float[480 * 2];
                fixed (float* p = silence)
                {
                    for (int n = 0; n < packets; n++)
                    {
                        lock (gate) track.OnPacket(p, 480, T0 + n * 100_000L, true);
                        Volatile.Write(ref sent, n + 1);
                        if (n % 20 == 0) Thread.Sleep(1);
                    }
                }
            });
            feeder.Start();
            while (Volatile.Read(ref sent) < 300) Thread.Sleep(1);
            lock (gate) head = track.AttachTap(0, tapped.Add);   // what the engine does under its audio gate
            feeder.Join();
            var all = track.Snapshot(0, long.MaxValue);
            Check.True(head!.Count > 0 && tapped.Count > 0, $"frames before ({head.Count}) and after ({tapped.Count}) the attach");
            Check.Equal(all.Select(f => f.Pts).ToArray(), head.Concat(tapped).Select(f => f.Pts).ToArray(), "snapshot + tapped frames are the whole timeline");
            Check.True(all.Zip(all.Skip(1), (a, b) => b.Pts > a.Pts).All(x => x), "increasing");
            track.FrameTap = null;
        });
    }

    // ---------------------------------------------------------------- recovery at startup

    [UnitTest]
    public static void RecoverRecordings_finishes_abandoned_recordings_and_clears_the_leftovers()
    {
        using var dir = new TempDir();
        Check.Equal(0, Engine.Engine.RecoverRecordings(dir.File("no such folder")).Count, "a missing folder");
        Check.Equal(0, Engine.Engine.RecoverRecordings(dir.Path).Count, "an empty folder");

        string rec = dir.File("Game 2025-01-01 10-00-00.mp4.rec"), final = dir.File("Game 2025-01-01 10-00-00.mp4");
        using (var w = new ClipMuxWriter(ClipOutput.Progressive(rec, final), new ClipVideoConfig("h264", 16, 16, 30), Descs(2), T0))
        {
            for (int i = 0; i < 20; i++) w.AddVideo(VideoPts(i), i % 10 == 0, Frame(i, i % 10 == 0));
            for (int k = 0; k < 8; k++) { w.AddAudio(0, new AacFrame(AudioPts(k), new byte[22])); w.AddAudio(1, new AacFrame(AudioPts(k), new byte[23])); }
            w.Flush(true);
        }   // (disposed, never finished: what a crash leaves)
        Check.True(File.Exists(rec) && File.Exists(rec + ".idx"), "an abandoned recording");

        string orphan = dir.File("Lost.mp4.rec.idx");
        File.WriteAllBytes(orphan, JournalStub(100));
        string headOnly = dir.File("Head.mp4.rec");
        File.WriteAllBytes(headOnly, FtypStub(48));
        File.SetLastWriteTimeUtc(headOnly, DateTime.UtcNow.AddHours(-1));
        string young = dir.File("Young.mp4.rec");
        File.WriteAllBytes(young, FtypStub(48));                       // (being created right now, perhaps)
        // Files that only look like leftovers by their names are not ours to delete.
        string foreignIdx = dir.File("Notes.mp4.rec.idx");
        File.WriteAllBytes(foreignIdx, new byte[100]);
        string foreignRec = dir.File("Other.mp4.rec");
        File.WriteAllBytes(foreignRec, new byte[48]);
        File.SetLastWriteTimeUtc(foreignRec, DateTime.UtcNow.AddHours(-1));
        string bad = dir.File("Bad.mp4.rec");
        File.WriteAllBytes(bad, new byte[3000]);
        File.WriteAllBytes(bad + ".idx", Enumerable.Repeat((byte)7, 500).ToArray());

        var results = Engine.Engine.RecoverRecordings(dir.Path);
        Check.Equal(2, results.Count, "one recovered, one reported");
        var ok = results.Single(r => r.Path != null);
        Check.Equal(final, ok.Path);
        Check.Equal(RecordingEndReason.Recovered, ok.Reason);
        Check.Equal(Names[..2], ok.AudioTracks);
        Check.True(ok.DurationSeconds > 0.5 && File.Exists(final) && new FileInfo(final).Length == ok.SizeBytes);
        Check.NotNull(Trimmer.ReadClipTracks(final), "the recovered file reads back");
        Check.False(File.Exists(rec) || File.Exists(rec + ".idx"), "its in-progress files are gone");
        Check.False(File.Exists(orphan), "an orphan journal is deleted");
        Check.False(File.Exists(headOnly), "a header-only recording is deleted");
        Check.True(File.Exists(young), "a young header-only file stays");
        Check.True(File.Exists(foreignIdx), "a foreign .rec.idx stays (wrong magic)");
        Check.True(File.Exists(foreignRec), "a foreign .rec stays (no ftyp head)");
        var failed = results.Single(r => r.Path == null);
        Check.NotNull(failed.Error);
        Check.Equal(RecordingEndReason.Unrecoverable, failed.Reason);
        Check.True(File.Exists(bad) && File.Exists(bad + ".idx"), "what cannot be recovered is kept");

        // Run again: nothing new to recover, the unrecoverable pair is reported again.
        Check.Equal(1, Engine.Engine.RecoverRecordings(dir.Path).Count);
    }

    private static byte[] JournalStub(int length)
    {
        var b = new byte[length];
        "CLINKIDX"u8.CopyTo(b);
        return b;
    }

    private static byte[] FtypStub(int length)
    {
        var b = new byte[length];
        b[3] = 0x18;
        "ftyp"u8.CopyTo(b.AsSpan(4));
        return b;
    }

    /// <summary>The pair a recording leaves when it is killed before its first flush: the file head, and the journal's header and no sample.</summary>
    private static (string rec, string idx) EmptyPair(TempDir dir, string name)
    {
        string rec = dir.File(name + ".mp4.rec"), final = dir.File(name + ".mp4");
        using (var w = new ClipMuxWriter(ClipOutput.Progressive(rec, final), new ClipVideoConfig("h264", 16, 16, 30), Descs(1), T0))
        {
            for (int i = 0; i < 5; i++) w.AddVideo(VideoPts(i), i == 0, Frame(i, i == 0));
            w.Flush(true);
        }
        string idx = Mp4Muxer.JournalPath(rec);
        Check.Equal(Mp4Muxer.JournalState.HasSamples, Mp4Muxer.InspectJournal(idx, out long dataStart));
        byte[] j = File.ReadAllBytes(idx);
        int header = 16 + (int)BitConverter.ToUInt32(j, 12) + 4;
        File.WriteAllBytes(idx, j[..header]);
        using (var f = new FileStream(rec, FileMode.Open, FileAccess.Write)) f.SetLength(dataStart);
        Check.Equal(Mp4Muxer.JournalState.Empty, Mp4Muxer.InspectJournal(idx, out _), "a header and no sample");
        foreach (string p in new[] { rec, idx }) File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddHours(-1));
        return (rec, idx);
    }

    [UnitTest]
    public static void RecoverRecordings_deletes_a_recording_that_died_before_its_first_flush()
    {
        using var dir = new TempDir();
        var (rec, idx) = EmptyPair(dir, "Dead");
        var (youngRec, youngIdx) = EmptyPair(dir, "Young");
        File.SetLastWriteTimeUtc(youngIdx, DateTime.UtcNow);                       // (being made right now, perhaps)
        var (bigRec, bigIdx) = EmptyPair(dir, "Big");
        using (var f = new FileStream(bigRec, FileMode.Append, FileAccess.Write)) f.Write(new byte[20000]);   // more than a head: not ours to judge
        File.SetLastWriteTimeUtc(bigRec, DateTime.UtcNow.AddHours(-1));

        var results = Engine.Engine.RecoverRecordings(dir.Path);
        Check.False(File.Exists(rec) || File.Exists(idx), "the empty pair is deleted");
        Check.True(File.Exists(youngRec) && File.Exists(youngIdx), "a young one is left alone");
        Check.True(File.Exists(bigRec) && File.Exists(bigIdx), "a data file with more than a head is kept");
        var r = results.Single();
        Check.Equal(RecordingEndReason.Unrecoverable, r.Reason);
        Check.Null(r.Path);
        Check.Contains(r.Error, "Big");
    }

    [UnitTest]
    public static void RecoverRecordings_skips_a_recording_that_is_live()
    {
        using var dir = new TempDir();
        string rec = dir.File("Live.mp4.rec"), final = dir.File("Live.mp4");
        var w = new ClipMuxWriter(ClipOutput.Progressive(rec, final), new ClipVideoConfig("h264", 16, 16, 30), Descs(1), T0);
        try
        {
            for (int i = 0; i < 20; i++) w.AddVideo(VideoPts(i), i % 10 == 0, Frame(i, i % 10 == 0));
            w.Flush(true);
            long size = new FileInfo(rec).Length;
            var results = Engine.Engine.RecoverRecordings(dir.Path);
            Check.Equal(0, results.Count, "a live recording is neither recovered nor reported");
            Check.True(File.Exists(rec) && File.Exists(rec + ".idx"), "its files are untouched");
            Check.False(File.Exists(final), "and it is not finished");
            Check.Equal(size, new FileInfo(rec).Length, "same size");

            // The same when its files look old: the open handles protect it, not the clock.
            foreach (string p in new[] { rec, rec + ".idx" }) File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddHours(-1));
            Check.Equal(0, Engine.Engine.RecoverRecordings(dir.Path).Count);
            Check.True(File.Exists(rec) && File.Exists(rec + ".idx"), "still there");
        }
        finally { w.Discard(); }
        Check.False(File.Exists(rec) || File.Exists(rec + ".idx"), "discarded");
    }

    [UnitTest]
    public static void Engine_that_is_not_running_refuses_to_record()
    {
        using var dir = new TempDir();
        using var engine = new Engine.Engine();
        Check.Throws<InvalidOperationException>(() => engine.StartRecording(new RecordingOptions(dir.Path)), "no recording on an engine that is not running (the same check refuses one that is shutting down: _stop / IsRunning under the recording lock)");
        Check.False(engine.IsRecording);
        Check.Equal(0, Directory.GetFileSystemEntries(dir.Path).Length, "and leaves nothing behind");
        Check.True(engine.StopRecordingAsync().IsFaulted, "there is nothing to stop");
    }
}
