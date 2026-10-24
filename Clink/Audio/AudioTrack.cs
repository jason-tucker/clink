using Clink.Encode;

namespace Clink.Audio;

internal readonly record struct AacFrame(long Pts, byte[] Data);

/// <summary>
/// One recorded audio track: keeps a gap-free 48 kHz timeline (silence is inserted when the source delivers
/// nothing), encodes to AAC and holds the recent frames in memory for the replay buffer. A track can also be a
/// <b>mix</b> of other tracks (<see cref="MixOf"/>): it has no source of its own and is filled from the others'
/// recent PCM on the same timeline, so the mix is sample-aligned with the separate tracks.
/// Capture always runs; <see cref="Enabled"/> only decides whether the track is written into clips.
/// </summary>
internal sealed unsafe class AudioTrack : IDisposable
{
    public const int Rate = WasapiSource.SampleRate;
    private const int FrameSamples = AacEncoder.FrameSamples;
    private const int HistoryFrames = Rate * 3;   // 3 s of PCM kept for mixing

    public string Name { get; }
    public WasapiSource? Source { get; set; }
    /// <summary>Write this track into clips. Sources are captured regardless (the mix needs them).</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Tracks this one is mixed from (null for a normal captured track).</summary>
    public IReadOnlyList<AudioTrack>? MixOf { get; }
    public bool IsMix => MixOf != null;

    private readonly AacEncoder _enc = new(Rate, 2, 24000);
    private readonly float[] _acc = new float[FrameSamples * 2];
    private int _accFrames;
    private long _basePts;
    private long _samplePos;      // frames pushed since _basePts
    private bool _started;
    private readonly List<AacFrame> _frames = new();
    private readonly object _gate = new();
    private long _frameIndex;     // AAC frames emitted
    private readonly float[] _history = new float[HistoryFrames * 2];

    public AudioTrack(string name, IReadOnlyList<AudioTrack>? mixOf = null)
    {
        Name = name;
        MixOf = mixOf;
    }

    public byte[] AudioSpecificConfig => _enc.AudioSpecificConfig;
    public int Bitrate => _enc.BytesPerSecond * 8;
    public bool Started => _started;
    public long TimelineEnd => _started ? PtsAt(_samplePos) : 0;
    public long FrameCount { get { lock (_gate) return _frames.Count; } }

    private long PtsAt(long samplePos) => _basePts + samplePos * 10_000_000L / Rate;
    private long PosAt(long pts) => (long)Math.Round((pts - _basePts) * (double)Rate / 10_000_000.0);

    public void OnPacket(float* stereo, int frames, long qpc, bool silent)
    {
        if (IsMix) return;
        if (!_started) { _basePts = qpc; _samplePos = 0; _started = true; }
        long expected = PtsAt(_samplePos);
        long gap = qpc - expected;
        if (gap > 300_000) // > 30 ms late: fill the hole with silence
        {
            if (gap > 100_000_000) // > 10 s: machine slept or source was dead; restart the timeline
            {
                Log.Info($"Audio '{Name}': timeline jump {gap / 10_000} ms, resyncing");
                ResetTimeline(qpc);
            }
            else PushSilence(gap * Rate / 10_000_000);
        }
        else if (gap < -300_000) // overlapping: drop the already-covered part
        {
            long skip = (-gap) * Rate / 10_000_000;
            if (skip >= frames) return;
            stereo += skip * 2;
            frames -= (int)skip;
        }
        // Device clock drift against QPC (tens of ppm on USB/wireless mics): slew one sample per packet once more than 10 ms off,
        // so it never piles up into a 30 ms hole or skip. The 10 ms dead band absorbs timestamp jitter.
        else if (gap > 100_000) Push(stereo, 1);
        else if (gap < -100_000 && frames > 1) { stereo += 2; frames--; }
        Push(stereo, frames);
    }

    private void ResetTimeline(long pts)
    {
        _basePts = pts;
        _samplePos = 0;
        _accFrames = 0;
        _frameIndex = 0;
        Array.Clear(_history);
        lock (_gate) _frames.Clear();
    }

    /// <summary>Keeps the timeline moving while a source is silent/absent (called periodically). Not used for mixes.</summary>
    public void PadSilenceTo(long until)
    {
        if (IsMix) return;
        if (!_started) { _basePts = until; _samplePos = 0; _started = true; return; }
        long expected = PtsAt(_samplePos);
        long gap = until - expected;
        if (gap <= 0) return;
        if (gap > 100_000_000) { ResetTimeline(until); return; }
        PushSilence(gap * Rate / 10_000_000);
    }

    /// <summary>
    /// For a mix track: encodes whole AAC blocks up to <paramref name="until"/> from the source tracks' recent PCM.
    /// Call after the sources have been padded to the same instant.
    /// </summary>
    public void MixUpTo(long until)
    {
        if (MixOf == null) return;
        if (!_started)
        {
            // Start one block behind so the first block is fully covered by the sources' history.
            _basePts = until - FrameSamples * 10_000_000L / Rate;
            _samplePos = 0;
            _started = true;
        }
        if (until - _basePts > 100_000_000 && _samplePos == 0) { ResetTimeline(until - FrameSamples * 10_000_000L / Rate); }
        float* block = stackalloc float[FrameSamples * 2];
        float* tmp = stackalloc float[FrameSamples * 2];
        var blockSpan = new Span<float>(block, FrameSamples * 2);
        var tmpSpan = new Span<float>(tmp, FrameSamples * 2);
        int guard = 0;
        while (PtsAt(_samplePos + FrameSamples) <= until && guard++ < 2000)
        {
            long pts = PtsAt(_samplePos);
            blockSpan.Clear();
            foreach (var src in MixOf)
            {
                src.ReadPcm(pts, FrameSamples, tmpSpan);
                for (int i = 0; i < FrameSamples * 2; i++) block[i] += tmp[i];
            }
            SoftClip(blockSpan);
            Push(block, FrameSamples);
        }
    }

    /// <summary>Soft limiter: linear below 0.8, then a smooth knee that never exceeds 1.0 (keeps a loud mic + game from hard-clipping).</summary>
    private static void SoftClip(Span<float> s)
    {
        for (int i = 0; i < s.Length; i++) s[i] = SoftKnee(s[i]);
    }

    /// <summary>
    /// <see cref="SoftClip"/> for one sample: the curve the recorded Mix was limited with, which a Mix rebuilt from its sources (the
    /// trim editor's preview and export) must use too, so that its loud peaks come out as they did in the recording.
    /// </summary>
    public static float SoftKnee(float v)
    {
        float a = MathF.Abs(v);
        if (a <= 0.8f) return v;
        float over = (a - 0.8f) / 0.2f;
        float y = 0.8f + 0.2f * MathF.Tanh(over);
        return v < 0 ? -y : y;
    }

    /// <summary>Copies this track's timeline PCM for [pts, pts + frames) into <paramref name="dst"/> (zeros where nothing is known).</summary>
    public void ReadPcm(long pts, int frames, Span<float> dst)
    {
        dst.Slice(0, frames * 2).Clear();
        if (!_started || IsMix) return;
        long start = PosAt(pts);
        long oldest = Math.Max(0, _samplePos - HistoryFrames);
        for (int i = 0; i < frames;)
        {
            long pos = start + i;
            if (pos >= _samplePos) break;
            if (pos < oldest) { i += (int)Math.Min(frames - i, oldest - pos); continue; }
            int idx = (int)(pos % HistoryFrames);
            int run = (int)Math.Min(frames - i, Math.Min(HistoryFrames - idx, _samplePos - pos));
            _history.AsSpan(idx * 2, run * 2).CopyTo(dst.Slice(i * 2, run * 2));
            i += run;
        }
    }

    private void PushSilence(long frames)
    {
        float* zero = stackalloc float[512 * 2];
        new Span<float>(zero, 512 * 2).Clear();
        while (frames > 0)
        {
            int n = (int)Math.Min(512, frames);
            Push(zero, n);
            frames -= n;
        }
    }

    private void Push(float* stereo, int frames)
    {
        while (frames > 0)
        {
            int n = Math.Min(frames, FrameSamples - _accFrames);
            var src = new ReadOnlySpan<float>(stereo, n * 2);
            src.CopyTo(_acc.AsSpan(_accFrames * 2));
            if (!IsMix)
            {
                // Keep recent PCM for mixing (ring indexed by absolute timeline position).
                int idx = (int)(_samplePos % HistoryFrames);
                int first = Math.Min(n, HistoryFrames - idx);
                src.Slice(0, first * 2).CopyTo(_history.AsSpan(idx * 2));
                if (first < n) src.Slice(first * 2, (n - first) * 2).CopyTo(_history.AsSpan(0));
            }
            _accFrames += n;
            stereo += n * 2;
            frames -= n;
            _samplePos += n;
            if (_accFrames == FrameSamples) FlushFrame();
        }
    }

    private void FlushFrame()
    {
        long pts = PtsAt(_samplePos - FrameSamples);
        Span<byte> pcm = stackalloc byte[FrameSamples * 4];
        for (int i = 0; i < FrameSamples * 2; i++)
        {
            float v = _acc[i];
            int s = (int)MathF.Round(Math.Clamp(v, -1f, 1f) * 32767f);
            pcm[i * 2] = (byte)s; pcm[i * 2 + 1] = (byte)(s >> 8);
        }
        _accFrames = 0;
        _frameIndex++;
        _enc.Encode(pcm, pts, (encPts, data) =>
        {
            var frame = new AacFrame(encPts, data);
            lock (_gate) _frames.Add(frame);
            var tap = _tap;
            if (tap != null)
            {
                try { tap(frame); } catch (Exception ex) { Log.Warn($"Audio '{Name}' tap: {ex.Message}"); }
            }
        });
    }

    private Action<AacFrame>? _tap;

    /// <summary>
    /// Sees every new frame as it is encoded, on the audio thread (the engine's audio gate is held): it must only copy and enqueue. Set and
    /// cleared under the engine's audio gate, which is what <see cref="AttachTap"/> relies on.
    /// </summary>
    public Action<AacFrame>? FrameTap { get => Volatile.Read(ref _tap); set => Volatile.Write(ref _tap, value); }

    /// <summary>
    /// Returns the frames since <paramref name="from"/> and sets <see cref="FrameTap"/>, with no frame lost or repeated between the two:
    /// the caller holds the lock that serializes this track's producer (the engine's audio gate), so no frame is made in between.
    /// </summary>
    public List<AacFrame> AttachTap(long from, Action<AacFrame> tap)
    {
        var frames = Snapshot(from, long.MaxValue);
        FrameTap = tap;
        return frames;
    }

    public void Trim(long olderThan)
    {
        lock (_gate)
        {
            int n = 0;
            while (n < _frames.Count && _frames[n].Pts < olderThan) n++;
            if (n > 0) _frames.RemoveRange(0, n);
        }
    }

    public List<AacFrame> Snapshot(long from, long to)
    {
        lock (_gate)
        {
            long frameDur = FrameSamples * 10_000_000L / Rate;
            return _frames.Where(f => f.Pts + frameDur > from && f.Pts <= to).ToList();
        }
    }

    public void Dispose()
    {
        Source?.Dispose();
        _enc.Dispose();
    }
}
