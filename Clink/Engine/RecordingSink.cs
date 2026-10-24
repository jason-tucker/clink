using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using Clink.Audio;
using Clink.Encode;
using Clink.Native;

namespace Clink.Engine;

internal enum RecordingEndReason { UserStopped, EngineStopped, MaxDuration, MaxSize, DiskFull, WriteFailed, Overflow, Discontinuity, Empty, Recovered, Unrecoverable }

/// <param name="Path">The finished (or recovered) file; null when nothing usable was left or the file could not be recovered yet.</param>
/// <param name="Error">Null for a clean end; otherwise what went wrong (a limit is not an error).</param>
internal sealed record RecordingResult(string? Path, double DurationSeconds, long SizeBytes, string? Game, string[] AudioTracks, RecordingEndReason Reason, string? Error, long DroppedVideoFrames);

/// <param name="BytesWritten">Sample bytes handed to the file so far (without the container's own overhead).</param>
/// <param name="QueueBytes">Bytes waiting for the writer thread.</param>
internal sealed record RecordingStatus(string PartialPath, DateTime StartedUtc, double Seconds, long BytesWritten, long QueueBytes, long Dropped);

/// <param name="Folder">Where the recording lands.</param>
/// <param name="MaxDuration">Longest part (default 4 h).</param>
/// <param name="MaxBytes">Most sample bytes in one part (default: none).</param>
internal sealed record RecordingOptions(string Folder, TimeSpan? MaxDuration = null, long? MaxBytes = null);

/// <summary>What a <see cref="RecordingSink"/> records, and the seams the tests use.</summary>
internal sealed class RecordingSinkConfig
{
    public string InProgressPath = "", FinalPath = "";
    public ClipVideoConfig Video = null!;
    public IReadOnlyList<AudioTrackDesc> Audio = Array.Empty<AudioTrackDesc>();
    public double BitrateMbps = 25;
    public TimeSpan MaxDuration = TimeSpan.FromHours(4);
    public long? MaxBytes;
    public string? Game;
    /// <summary>Runs once on the writer thread when the sink stops taking samples, to detach it from its producers (the engine's taps).</summary>
    public Action<RecordingSink>? Detach;
    /// <summary>Runs on the writer thread with the result, just before <see cref="RecordingSink.Completion"/> completes. Must not wait for it.</summary>
    public Action<RecordingSink, RecordingResult>? Completed;
    /// <summary>Consulted when the first keyframe arrives and <see cref="Video"/> has no sequence header (the encoder may know its parameter sets only after its first output).</summary>
    public Func<byte[]?>? SequenceHeaderProvider;
    /// <summary>QPC in 100 ns (the clock of the sample timestamps).</summary>
    public Func<long> Clock = ComUtil.QpcNow100ns;
    /// <summary>Free bytes on the recording's volume (checked every ~5 s); null when unknown.</summary>
    public Func<long>? FreeSpace;
    public long MinFreeBytes = 1L << 30;
    /// <summary>Queue limit in bytes; 0 = max(64 MB, 15 s at <see cref="BitrateMbps"/>).</summary>
    public long BudgetBytes;
    public TimeSpan OverflowLimit = TimeSpan.FromSeconds(30);
    public TimeSpan GapLimit = TimeSpan.FromSeconds(5);
    public TimeSpan AudioCatchUp = TimeSpan.FromMilliseconds(800);
    /// <summary>Stop this long before the file format's duration limit.</summary>
    public TimeSpan LimitMargin = TimeSpan.FromSeconds(60);
    /// <summary>Test hook: runs on the writer thread before every sample is written.</summary>
    public Action? BeforeWrite;
    /// <summary>Test hook: runs on the writer thread after every sample is written (track, pts; -1 = video).</summary>
    public Action<int, long, bool>? Written;
    public TimeSpan SpaceCheckInterval = TimeSpan.FromSeconds(5);
}

/// <summary>
/// The long-running sink of a manual recording: producers (the encoder thread, the audio thread) copy samples into a queue and return;
/// one thread, "Clink.Recorder", owns the <see cref="ClipMuxWriter"/> (progressive mode) and does all the file work. Producers never
/// block, do I/O or wait for a lock. Each producer kind is a single thread at a time (the video producer holds the engine's tap gate, each
/// audio track's producer the audio gate).
/// </summary>
internal sealed class RecordingSink
{
    private readonly struct Item
    {
        public readonly int Track;      // -1 = video
        public readonly long Pts;
        public readonly bool Key;
        public readonly byte[] Buf;
        public readonly int Len;
        public readonly bool Pooled;
        public Item(int track, long pts, bool key, byte[] buf, int len, bool pooled) { Track = track; Pts = pts; Key = key; Buf = buf; Len = len; Pooled = pooled; }
    }

    private const int ReasonNone = -1;
    private const int MaxPendingAudio = 1500;
    private const long NoAudioStopWaitMs = 100;
    private static readonly long AudioFrameDuration = AacEncoder.FrameSamples * 10_000_000L / 48000;

    private readonly RecordingSinkConfig _cfg;
    private readonly ConcurrentQueue<Item> _queue = new();
    private readonly ManualResetEventSlim _signal = new(false);
    private readonly TaskCompletionSource<RecordingResult> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _thread;
    private readonly long _budget;
    private readonly long[] _lastAudio;
    private readonly DateTime _startedUtc = DateTime.UtcNow;

    // Producer side (see the class comment for who runs these).
    private long _lastVideoSeen = long.MinValue;
    private bool _gotKey, _dropToKey;
    private long _overflowSince;     // Stopwatch timestamp of the first drop of this overflow, 0 = none
    private volatile bool _closed;
    private long _endPts = long.MaxValue;   // video after this is dropped (clean stop)

    // Shared
    private long _queueBytes, _written, _dropped, _elapsed100ns;
    private int _reason = ReasonNone;       // first reason wins: set by Stop / a producer's end request
    private volatile bool _stopRequested, _endNow, _catchUp, _aborted;
    private long _stopTimestamp;
    private int _started;
    private string? _externalError;

    // Writer thread
    private ClipMuxWriter? _w;
    private long _origin;
    private List<Item>? _pendingAudio = new();

    public RecordingSink(RecordingSinkConfig cfg)
    {
        _cfg = cfg;
        _budget = cfg.BudgetBytes > 0 ? cfg.BudgetBytes : Math.Max(64L << 20, (long)(15 * cfg.BitrateMbps * 1_000_000 / 8));
        _lastAudio = new long[cfg.Audio.Count];
        Array.Fill(_lastAudio, long.MinValue);
        _thread = new Thread(Run) { IsBackground = true, Name = "Clink.Recorder", Priority = ThreadPriority.BelowNormal };
    }

    /// <summary>
    /// Starts the writer thread (once). Separate from the constructor so the owner can finish wiring the sink (store it, attach it to its
    /// producers) before anything can end it: the callbacks of <see cref="RecordingSinkConfig"/> receive the sink itself. Samples offered
    /// before this are queued.
    /// </summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        try { _thread.Start(); }
        catch { Volatile.Write(ref _started, 0); throw; }   // (so that Abort can still complete the task)
    }

    /// <summary>
    /// Gives the recording up: no file is finished (what was written is deleted) and the completion carries <see cref="RecordingEndReason.Empty"/>.
    /// For an attempt that failed to start. Any thread; does not wait.
    /// </summary>
    public void Abort()
    {
        _aborted = true;
        Stop(RecordingEndReason.EngineStopped, catchUp: false);
        Start();   // (the thread does the cleanup, also when the sink never ran)
    }

    /// <summary>A producer failed (it threw): ends the recording at once; what is queued is still written and finished.</summary>
    public void Fail(string error)
    {
        _externalError ??= error;
        RequestEnd(RecordingEndReason.WriteFailed);
    }

    public Task<RecordingResult> Completion => _done.Task;
    public string PartialPath => _cfg.InProgressPath;
    public long DroppedVideoFrames => Interlocked.Read(ref _dropped);
    public long QueueBytes => Interlocked.Read(ref _queueBytes);
    public long BytesWritten => Interlocked.Read(ref _written);
    public bool IsFinished => _done.Task.IsCompleted;

    public RecordingStatus Status => new(_cfg.InProgressPath, _startedUtc, Interlocked.Read(ref _elapsed100ns) / 10_000_000.0,
        Interlocked.Read(ref _written), Interlocked.Read(ref _queueBytes), Interlocked.Read(ref _dropped));

    // ------------------------------------------------------------------ producers

    /// <summary>Video from the live encoder: copies the span (valid only during the call) and queues it. Never blocks.</summary>
    public void OnVideo(long pts, bool keyframe, ReadOnlySpan<byte> data)
    {
        if (!AdmitVideo(pts, keyframe, data.Length)) return;
        var buf = ArrayPool<byte>.Shared.Rent(data.Length);
        data.CopyTo(buf);
        Enqueue(new Item(-1, pts, keyframe, buf, data.Length, true));
    }

    /// <summary>Video the sink may keep (the start snapshot): <paramref name="data"/> is queued as it is.</summary>
    public void OnVideoOwned(long pts, bool keyframe, byte[] data)
    {
        if (!AdmitVideo(pts, keyframe, data.Length)) return;
        Enqueue(new Item(-1, pts, keyframe, data, data.Length, false));
    }

    /// <summary>One AAC frame of track <paramref name="track"/> (the index in the descriptor list). The frame's bytes are shared, not copied (they are never changed).</summary>
    public void OnAudio(int track, AacFrame frame)
    {
        if (_closed || (uint)track >= (uint)_lastAudio.Length) return;
        if (frame.Pts <= Volatile.Read(ref _lastAudio[track])) return;   // timestamps of a track must increase
        if (_stopRequested && frame.Pts >= Volatile.Read(ref _endPts))
        {
            // Past the stop point: not part of the file (it would outlast the video), but it tells the stop that this track has caught up.
            Volatile.Write(ref _lastAudio[track], frame.Pts);
            return;
        }
        Enqueue(new Item(track, frame.Pts, false, frame.Data, frame.Data.Length, false));
        Volatile.Write(ref _lastAudio[track], frame.Pts);   // after the enqueue: "caught up" (a clean stop) then implies the frame is in the queue
    }

    private void Enqueue(in Item it)
    {
        Interlocked.Add(ref _queueBytes, it.Len);
        _queue.Enqueue(it);
        if (!_signal.IsSet) _signal.Set();
    }

    private bool AdmitVideo(long pts, bool key, int len)
    {
        if (_closed || pts > Volatile.Read(ref _endPts)) return false;
        if (_lastVideoSeen != long.MinValue)
        {
            if (pts <= _lastVideoSeen) return false;                              // per-track monotonic guard
            if (pts - _lastVideoSeen > _cfg.GapLimit.Ticks) { RequestEnd(RecordingEndReason.Discontinuity); return false; }   // the capture stalled: the file would have a hole
        }
        _lastVideoSeen = pts;
        if (!_gotKey)
        {
            if (!key) return false;   // a recording starts on a keyframe
            _gotKey = true;
        }
        long queued = Interlocked.Read(ref _queueBytes);
        if (queued + len > _budget)
        {
            // The writer cannot keep up: drop video up to the next keyframe (audio stays), and give up when it lasts.
            _dropToKey = true;
            return Drop();
        }
        if (_dropToKey)
        {
            if (!key) return Drop();
            _dropToKey = false;
            _overflowSince = 0;
        }
        return true;
    }

    private bool Drop()
    {
        Interlocked.Increment(ref _dropped);
        long now = Stopwatch.GetTimestamp();
        if (_overflowSince == 0) _overflowSince = now;
        else if ((now - _overflowSince) * 10_000_000L / Stopwatch.Frequency > _cfg.OverflowLimit.Ticks) RequestEnd(RecordingEndReason.Overflow);
        return false;
    }

    /// <summary>A producer's request to end now (what is queued is still written).</summary>
    private void RequestEnd(RecordingEndReason reason)
    {
        Interlocked.CompareExchange(ref _reason, (int)reason, ReasonNone);
        _closed = true;
        _endNow = true;
        _signal.Set();
    }

    // ------------------------------------------------------------------ control

    /// <summary>
    /// Ends the recording (any thread, does not wait). With <paramref name="catchUp"/> the end is "now": video after it is dropped and audio is
    /// taken until every track has a frame at or past it (at most <see cref="RecordingSinkConfig.AudioCatchUp"/>); without, what is queued is all there is.
    /// </summary>
    public void Stop(RecordingEndReason reason, bool catchUp = true)
    {
        if (_stopRequested) return;
        Interlocked.CompareExchange(ref _reason, (int)reason, ReasonNone);
        _catchUp = catchUp;
        if (catchUp) Volatile.Write(ref _endPts, _cfg.Clock());
        else _closed = true;
        _stopTimestamp = Stopwatch.GetTimestamp();
        _stopRequested = true;
        _signal.Set();
    }

    // ------------------------------------------------------------------ writer thread

    private void Run()
    {
        RecordingResult result;
        try { result = RunCore(); }
        catch (Exception ex)
        {
            Log.Error("Recorder", ex);
            result = new RecordingResult(null, 0, 0, _cfg.Game, Array.Empty<string>(), RecordingEndReason.WriteFailed, ex.Message, DroppedVideoFrames);
        }
        _closed = true;
        try { _cfg.Completed?.Invoke(this, result); } catch (Exception ex) { Log.Warn("Recording completed handler: " + ex.Message); }
        _done.TrySetResult(result);
    }

    private static bool IsDiskFull(Exception ex) => ex is IOException && ((ex.HResult & 0xFFFF) is 0x70 or 0x27);

    /// <summary>A writer exception to a reason; <paramref name="broken"/> when the writer cannot be used any more (Dispose, then Recover).</summary>
    private static RecordingEndReason Classify(Exception ex, out bool broken)
    {
        if (ex is LimitReached limit) { broken = false; return limit.Reason; }
        if (ex is Mp4Muxer.DurationLimitException) { broken = false; return RecordingEndReason.MaxDuration; }
        broken = true;
        return IsDiskFull(ex) ? RecordingEndReason.DiskFull : RecordingEndReason.WriteFailed;
    }

    private RecordingResult RunCore()
    {
        RecordingEndReason? ended = null;   // the writer's own reason to end (a limit, a failure)
        string? error = null;
        bool broken = false;
        var watch = Stopwatch.StartNew();
        // lastFlush starts a second in the past: the first flush follows the writer's creation at once (the pre-roll reaches the disk
        // with it, and a journal that is still empty beside a big data file would be unrecoverable).
        long lastFlush = -1000, lastDurable = 0, lastSpace = -(long)_cfg.SpaceCheckInterval.TotalMilliseconds;

        void Guard(Func<RecordingEndReason?> step)
        {
            try { ended ??= step(); }
            catch (Exception ex)
            {
                ended = Classify(ex, out broken);
                if (broken) error = ex.Message;
                Log.Warn($"Recording: {ended} ({ex.GetType().Name}: {ex.Message})");
            }
        }

        while (true)
        {
            _signal.Reset();
            if (_aborted) break;
            Guard(() => { DrainWriting(); return null; });
            if (ended == null)
            {
                Guard(() =>
                {
                    long ms = watch.ElapsedMilliseconds;
                    if (_w != null && ms - lastFlush >= 1000)
                    {
                        bool durable = ms - lastDurable >= 5000;
                        _w.Flush(durable);
                        lastFlush = ms;
                        if (durable) lastDurable = ms;
                    }
                    if (_cfg.FreeSpace != null && ms - lastSpace >= (long)_cfg.SpaceCheckInterval.TotalMilliseconds)
                    {
                        lastSpace = ms;
                        if (_cfg.FreeSpace() < _cfg.MinFreeBytes) { error = "The disk is nearly full"; return RecordingEndReason.DiskFull; }
                    }
                    return null;
                });
            }
            if (ended != null) break;
            if (_endNow && _queue.IsEmpty) break;
            if (_stopRequested && _queue.IsEmpty)
            {
                long waited = (Stopwatch.GetTimestamp() - _stopTimestamp) * 1000 / Stopwatch.Frequency;
                // Without audio tracks there is nothing to wait for but the encoder's last in-flight frames (a few frames, ~100 ms).
                if (!_catchUp || (AudioCaughtUp() && (_lastAudio.Length > 0 || waited >= NoAudioStopWaitMs)) || waited >= (long)_cfg.AudioCatchUp.TotalMilliseconds) break;
            }
            _signal.Wait(50);
        }

        try { _cfg.Detach?.Invoke(this); } catch (Exception ex) { Log.Warn("Recording detach: " + ex.Message); }
        _closed = true;
        if (!broken && ended == null && !_aborted)
        {
            // What a producer queued while the sink was detaching (nothing new comes after that).
            Guard(() => { DrainWriting(); return null; });
        }
        RecordingEndReason? external = _reason == ReasonNone ? null : (RecordingEndReason)_reason;
        RecordingEndReason reason = broken ? ended!.Value : (external ?? ended ?? RecordingEndReason.UserStopped);
        ReleaseQueue();
        error ??= _externalError;

        long dropped = DroppedVideoFrames;
        var w = _w;
        _w = null;
        if (_aborted)
        {
            try { w?.Discard(); } catch (Exception ex) { Log.Warn("Recording discard: " + ex.Message); }
            return Empty(RecordingEndReason.Empty, "The recording did not start", dropped);
        }
        if (w == null || !w.HasVideo)
        {
            try { w?.Discard(); } catch (Exception ex) { Log.Warn("Recording discard: " + ex.Message); }
            return Empty(reason, error, dropped);
        }
        if (!broken)
        {
            try
            {
                var r = w.Finish();
                w.Dispose();
                return new RecordingResult(r.Path, r.Duration100ns / 10_000_000.0, r.SizeBytes, _cfg.Game, r.AudioTracks, reason, error, dropped);
            }
            catch (Exception ex)
            {
                // Finish does not run twice: Dispose, then Recover.
                reason = Classify(ex, out _) == RecordingEndReason.DiskFull ? RecordingEndReason.DiskFull : RecordingEndReason.WriteFailed;
                error = ex.Message;
                Log.Warn($"Recording finish failed: {ex.Message}");
            }
        }
        try { w.Dispose(); } catch { }
        return Salvage(reason, error, dropped);
    }

    private RecordingResult Empty(RecordingEndReason reason, string? error, long dropped)
    {
        if (reason is RecordingEndReason.UserStopped or RecordingEndReason.EngineStopped) reason = RecordingEndReason.Empty;
        return new RecordingResult(null, 0, 0, _cfg.Game, Array.Empty<string>(), reason, error ?? "Nothing recorded", dropped);
    }

    /// <summary>The writer failed: the recoverable files get their moov now if that works (otherwise the next start recovers them).</summary>
    private RecordingResult Salvage(RecordingEndReason reason, string? error, long dropped)
    {
        string rec = _cfg.InProgressPath;
        try
        {
            var r = Mp4Muxer.Recover(rec, Mp4Muxer.JournalPath(rec), _cfg.FinalPath);
            if (r != null)
                return new RecordingResult(r.Path, r.Duration100ns / 10_000_000.0, r.SizeBytes, _cfg.Game, r.AudioTracks, reason, (error ?? reason.ToString()) + " (the recording was recovered)", dropped);
        }
        catch (Exception ex) { Log.Warn("Recording recover: " + ex.Message); }
        bool kept = File.Exists(rec);
        return new RecordingResult(null, Interlocked.Read(ref _elapsed100ns) / 10_000_000.0, 0, _cfg.Game, Array.Empty<string>(), reason,
            (error ?? reason.ToString()) + (kept ? "; the partial recording is kept and is recovered at the next start" : ""), dropped);
    }

    private bool AudioCaughtUp()
    {
        long end = Volatile.Read(ref _endPts);
        for (int i = 0; i < _lastAudio.Length; i++)
            if (Volatile.Read(ref _lastAudio[i]) < end) return false;
        return true;
    }

    /// <summary>Writes every queued sample; stops early (leaving the rest queued) when a limit is reached.</summary>
    private void DrainWriting()
    {
        while (_queue.TryDequeue(out var it))
        {
            try { Write(it); }
            finally { Release(it); }
        }
    }

    private void Release(in Item it)
    {
        Interlocked.Add(ref _queueBytes, -it.Len);
        if (it.Pooled) ArrayPool<byte>.Shared.Return(it.Buf);
    }

    private void ReleaseQueue()
    {
        while (_queue.TryDequeue(out var it)) Release(it);
    }

    private void Write(in Item it)
    {
        if (it.Track < 0)
        {
            if (_w == null)
            {
                _origin = it.Pts;
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_cfg.FinalPath))!);
                var video = _cfg.Video;
                if (video.SequenceHeader == null && _cfg.SequenceHeaderProvider?.Invoke() is { } header) video = video with { SequenceHeader = header };
                _w = new ClipMuxWriter(ClipOutput.Progressive(_cfg.InProgressPath, _cfg.FinalPath), video, _cfg.Audio, _origin, aligned: true);
                var pending = _pendingAudio;
                _pendingAudio = null;
                if (pending != null)
                    foreach (var a in pending)
                        if (a.Pts + AudioFrameDuration > _origin) { _cfg.BeforeWrite?.Invoke(); _w.AddAudio(a.Track, new AacFrame(a.Pts, a.Buf)); Interlocked.Add(ref _written, a.Len); _cfg.Written?.Invoke(a.Track, a.Pts, false); }
            }
            long elapsed = it.Pts - _origin;
            if (elapsed >= _cfg.MaxDuration.Ticks || elapsed >= _w.DurationLimit100ns - _cfg.LimitMargin.Ticks) throw new LimitReached(RecordingEndReason.MaxDuration);
            if (_cfg.MaxBytes is long max && Interlocked.Read(ref _written) >= max) throw new LimitReached(RecordingEndReason.MaxSize);
            _cfg.BeforeWrite?.Invoke();
            _w.AddVideo(it.Pts, it.Key, new ReadOnlySpan<byte>(it.Buf, 0, it.Len));
            Interlocked.Add(ref _written, it.Len);
            Interlocked.Exchange(ref _elapsed100ns, elapsed);
            _cfg.Written?.Invoke(-1, it.Pts, it.Key);
        }
        else if (_w == null)
        {
            // No video yet: the frames wait for the origin (the oldest go when it takes long).
            var list = _pendingAudio!;
            if (list.Count >= MaxPendingAudio) list.RemoveAt(0);
            list.Add(it);
        }
        else if (it.Pts + AudioFrameDuration > _origin)
        {
            _cfg.BeforeWrite?.Invoke();
            _w.AddAudio(it.Track, new AacFrame(it.Pts, it.Buf));
            Interlocked.Add(ref _written, it.Len);
            _cfg.Written?.Invoke(it.Track, it.Pts, false);
        }
    }

    /// <summary>A limit of the recording's own (duration, size): the file is finished, not failed.</summary>
    private sealed class LimitReached : Exception
    {
        public readonly RecordingEndReason Reason;
        public LimitReached(RecordingEndReason reason) { Reason = reason; }
    }
}

/// <summary>
/// Where the encoder's output goes: into the replay ring and, while a recording runs, into its sink. Both happen under one gate, so a
/// recording can start from the ring's last keyframe and attach in the same critical section: its first live sample is the one right after
/// the snapshot's last (no gap, no duplicate), and it needs no forced keyframe. The gate is held for a copy and an enqueue.
/// </summary>
internal sealed class VideoFanOut
{
    private readonly object _gate = new();
    private RecordingSink? _sink;

    public bool HasSink { get { lock (_gate) return _sink != null; } }

    /// <summary>The encoder thread: ring first, then the recorder.</summary>
    public void Push(RingStore ring, long pts, bool keyframe, ReadOnlySpan<byte> data)
    {
        lock (_gate)
        {
            ring.Push(pts, keyframe, data);
            var sink = _sink;
            if (sink == null) return;
            try { sink.OnVideo(pts, keyframe, data); }
            catch (Exception ex)
            {
                // Never let the recorder's trouble reach the encoder's output loop (it would skip releasing the output buffer): drop the recording, keep the ring.
                _sink = null;
                Log.Error("Recording video tap", ex);
                try { sink.Fail("The recorder failed: " + ex.Message); } catch { }
            }
        }
    }

    /// <summary>Feeds <paramref name="sink"/> the ring from the last keyframe and attaches it for the samples that follow. Returns the snapshot's first pts (the recording's origin), or null when the ring has no keyframe yet (the sink then starts at the first live one).</summary>
    public long? Attach(RingStore ring, RecordingSink sink)
    {
        lock (_gate)
        {
            if (_sink != null) throw new InvalidOperationException("Already recording");
            var snap = ring.Snapshot(long.MaxValue, long.MaxValue);   // everything in the ring is at or before now: the last keyframe on
            foreach (var s in snap) sink.OnVideoOwned(s.Pts, s.Keyframe, s.Data);
            _sink = sink;
            return snap.Count > 0 ? snap[0].Pts : null;
        }
    }

    /// <summary>Detaches <paramref name="sink"/> (false when it was not attached).</summary>
    public bool Detach(RecordingSink sink)
    {
        lock (_gate)
        {
            if (_sink != sink) return false;
            _sink = null;
            return true;
        }
    }
}
