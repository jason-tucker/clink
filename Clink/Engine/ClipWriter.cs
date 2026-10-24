using Clink.Audio;
using Clink.Encode;

namespace Clink.Engine;

/// <param name="AudioTracks">Names of the audio tracks in the file, in file order (what the library and the trim editor label them with).</param>
internal sealed record ClipResult(string Path, double DurationSeconds, long SizeBytes, string? Game, string[] AudioTracks);

/// <summary>
/// Turns a replay-buffer snapshot into an MP4 file. Samples are streamed: each one is converted and handed to the muxer in
/// turn and its snapshot bytes are released right after, so a long clip (the replay length goes up to an hour) costs one
/// copy of itself in managed memory, not the snapshot plus a converted twin of it.
/// </summary>
internal static class ClipWriter
{
    /// <summary>H.264 / HEVC only (kept for callers that still think in terms of a bool).</summary>
    public static string[] Write(string path, IEnumerable<VideoSample> video, bool hevc, int width, int height, byte[]? sequenceHeader,
        List<(AudioTrackDesc desc, List<AacFrame> frames)> audio, long origin100ns, uint fps, bool aligned = true)
        => Write(path, video, hevc ? "hevc" : "h264", width, height, sequenceHeader, audio, origin100ns, fps, aligned);

    /// <param name="video">Video samples in order, starting on a keyframe. Each sample's <see cref="VideoSample.Data"/> is released once written.</param>
    /// <param name="codec">"h264" | "hevc" | "av1" (experimental). Video samples are Annex B access units for H.264/HEVC, temporal units for AV1.</param>
    /// <param name="sequenceHeader">Encoder-provided parameter sets (Annex B) or AV1 Sequence Header OBU; may be null when the samples carry them in-band.</param>
    /// <param name="aligned">The audio frames' timestamps are true positions on the clip's timeline (a recording; a trim that kept each
    /// track's offset): the file says so (<see cref="Mp4Muxer.AlignedBrand"/>) and the Mix's sources are placed exactly against it. False for
    /// a trim of a file whose own offsets could not be trusted.</param>
    /// <returns>Names of the audio tracks written, in file order (a track with no frames is left out of the file).</returns>
    public static string[] Write(string path, IEnumerable<VideoSample> video, string codec, int width, int height, byte[]? sequenceHeader,
        List<(AudioTrackDesc desc, List<AacFrame> frames)> audio, long origin100ns, uint fps, bool aligned = true)
    {
        using var writer = new ClipMuxWriter(ClipOutput.FastStart(path), new ClipVideoConfig(codec, width, height, fps, sequenceHeader),
            audio.Select(a => a.desc).ToList(), origin100ns, aligned);
        var ai = new int[audio.Count];
        foreach (var v in video)
        {
            // The audio frames due up to this video sample (on the frame grid), then the sample.
            long q = writer.NextVideoPts(v.Pts);
            for (int t = 0; t < audio.Count; t++)
            {
                var frames = audio[t].frames;
                while (ai[t] < frames.Count && frames[ai[t]].Pts <= q) writer.AddAudio(t, frames[ai[t]++]);
            }
            writer.AddVideo(v.Pts, v.Keyframe, v.Data);
            v.Data = Array.Empty<byte>();   // the snapshot's copy is done with
        }
        if (!writer.HasVideo) throw new InvalidOperationException("Nothing recorded yet, wait a moment and try again.");
        for (int t = 0; t < audio.Count; t++)
        {
            var frames = audio[t].frames;
            while (ai[t] < frames.Count) writer.AddAudio(t, frames[ai[t]++]);
        }
        return writer.Finish().AudioTracks;
    }
}
