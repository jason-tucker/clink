using Clink.Engine;
using Clink.Services;
using Clink.UI;

namespace Clink.Tests;

/// <summary>The rules of manual recording that need no window: when a recording continues as a new part, and the texts it shows.</summary>
internal static class RecordingPolicyTests
{
    private static readonly RecordingEndReason[] Continuing =
        { RecordingEndReason.EngineStopped, RecordingEndReason.MaxDuration, RecordingEndReason.Discontinuity };

    [UnitTest]
    public static void RecordingPolicy_continues_only_a_wanted_recording_that_ended_for_a_technical_reason()
    {
        foreach (var reason in Enum.GetValues<RecordingEndReason>())
            foreach (var cause in Enum.GetValues<EngineStopCause>())
                foreach (bool auto in new[] { false, true })
                {
                    bool technicalReason = Continuing.Contains(reason) || (reason == RecordingEndReason.Empty && auto);
                    bool technicalCause = cause is EngineStopCause.None or EngineStopCause.Technical;
                    Check.Equal(technicalReason && technicalCause, RecordingPolicy.ShouldStartNewPart(reason, cause, wantRecording: true, autoPart: auto), $"{reason} / {cause} / auto {auto} / wanted");
                    Check.False(RecordingPolicy.ShouldStartNewPart(reason, cause, wantRecording: false, autoPart: auto), $"{reason} / {cause} / auto {auto} / not wanted");
                }
    }

    [UnitTest]
    public static void RecordingPolicy_the_named_cases()
    {
        Check.True(RecordingPolicy.ShouldStartNewPart(RecordingEndReason.EngineStopped, EngineStopCause.Technical, true, false), "display change / resume");
        Check.True(RecordingPolicy.ShouldStartNewPart(RecordingEndReason.EngineStopped, EngineStopCause.None, true, false), "the engine failed by itself (GPU reset)");
        Check.True(RecordingPolicy.ShouldStartNewPart(RecordingEndReason.MaxDuration, EngineStopCause.None, true, false), "the 4 h limit");
        Check.True(RecordingPolicy.ShouldStartNewPart(RecordingEndReason.Discontinuity, EngineStopCause.None, true, true), "a capture stall");
        Check.True(RecordingPolicy.ShouldStartNewPart(RecordingEndReason.Empty, EngineStopCause.None, true, true), "an automatic part that recorded nothing goes on");
        Check.True(RecordingPolicy.ShouldStartNewPart(RecordingEndReason.Empty, EngineStopCause.Technical, true, true), "an automatic part cut by a restart goes on");
        Check.False(RecordingPolicy.ShouldStartNewPart(RecordingEndReason.Empty, EngineStopCause.None, true, false), "the user's own start recorded nothing: told");
        Check.False(RecordingPolicy.ShouldStartNewPart(RecordingEndReason.Empty, EngineStopCause.Pause, true, true), "an empty part never outlives a pause");
        Check.False(RecordingPolicy.ShouldStartNewPart(RecordingEndReason.EngineStopped, EngineStopCause.Pause, true, false), "paused");
        Check.False(RecordingPolicy.ShouldStartNewPart(RecordingEndReason.EngineStopped, EngineStopCause.GameEnded, true, false), "game-only: the game ended");
        Check.False(RecordingPolicy.ShouldStartNewPart(RecordingEndReason.EngineStopped, EngineStopCause.Shutdown, true, false), "Clink is going");
        Check.False(RecordingPolicy.ShouldStartNewPart(RecordingEndReason.UserStopped, EngineStopCause.None, true, true), "the user stopped");
        foreach (var r in new[] { RecordingEndReason.DiskFull, RecordingEndReason.WriteFailed, RecordingEndReason.Overflow, RecordingEndReason.Unrecoverable, RecordingEndReason.MaxSize })
            foreach (bool auto in new[] { false, true })
                Check.False(RecordingPolicy.ShouldStartNewPart(r, EngineStopCause.Technical, true, auto), r + " never continues");
    }
    [UnitTest]
    public static void RecordingPolicy_texts()
    {
        Check.Equal("00:00", RecordingPolicy.Elapsed(0));
        Check.Equal("00:12", RecordingPolicy.Elapsed(12.9));
        Check.Equal("12:34", RecordingPolicy.Elapsed(754));
        Check.Equal("1:02:03", RecordingPolicy.Elapsed(3723));
        Check.Equal("00:00", RecordingPolicy.Elapsed(-5));
        Check.Equal("Recording 00:12", RecordingPolicy.StatusLine(12, null));
        Check.Equal("Recording 00:12 · Overwatch", RecordingPolicy.StatusLine(12, "Overwatch"));
        Check.Equal("12:34 · 1.2 GB", RecordingPolicy.SavedSummary(754, 1_200_000_000));
        Check.Equal("Recovered 1 recording", RecordingPolicy.RecoveredTitle(1));
        Check.Equal("Recovered 3 recordings", RecordingPolicy.RecoveredTitle(3));
    }

    [UnitTest]
    public static void RecordingPolicy_problem_texts_say_when_the_recording_was_recovered()
    {
        RecordingResult R(string? path, RecordingEndReason reason, string? error = null) =>
            new(path, 10, 1000, null, Array.Empty<string>(), reason, error, 0);

        Check.True(RecordingPolicy.Problem(R("a.mp4", RecordingEndReason.UserStopped)) == null, "a stop is no problem");
        Check.True(RecordingPolicy.Problem(R(null, RecordingEndReason.Empty)) == null, "empty is told apart by the caller");
        Check.True(RecordingPolicy.Problem(R("a.mp4", RecordingEndReason.MaxDuration)) == null, "a limit is no problem");

        var full = RecordingPolicy.Problem(R("a.mp4", RecordingEndReason.DiskFull, "The disk is nearly full"));
        Check.NotNull(full);
        Check.Contains(full!.Value.Text, "disk is nearly full");
        Check.Contains(full.Value.Text, "saved", "a file was kept");
        Check.False(full.Value.Text.Contains("recovered"), "a full disk finishes the file cleanly: nothing was recovered");
        var failed = RecordingPolicy.Problem(R("a.mp4", RecordingEndReason.WriteFailed, "x"));
        Check.Contains(failed!.Value.Text, "recovered", "a failed write that left a file was recovered");

        var lost = RecordingPolicy.Problem(R(null, RecordingEndReason.WriteFailed, "device not ready"));
        Check.NotNull(lost);
        Check.Contains(lost!.Value.Text, "device not ready");
        Check.False(lost.Value.Text.Contains("recovered"), "no file, nothing recovered");
        Check.NotNull(RecordingPolicy.Problem(R(null, RecordingEndReason.Overflow)));
        Check.NotNull(RecordingPolicy.Problem(R(null, RecordingEndReason.Unrecoverable, "x")));
    }

    [UnitTest]
    public static void RecordingPolicy_status_dot_is_red_for_a_manual_recording_and_green_for_the_buffer()
    {
        Check.Equal("Good", StatusDotWindow.KeyFor(RecordingState.Recording, false));
        Check.Equal("Danger", StatusDotWindow.KeyFor(RecordingState.Recording, true));
        Check.Equal("Muted", StatusDotWindow.KeyFor(RecordingState.Paused, true), "paused wins: no recording is going");
        Check.Equal("Danger", StatusDotWindow.KeyFor(RecordingState.Error, false));
        Check.Equal("Warn", StatusDotWindow.KeyFor(RecordingState.WaitingForGame, false));
    }

    [UnitTest]
    public static void RecordingPolicy_the_record_verb_is_a_command_line_command()
    {
        Check.Equal("record", AppController.CommandFromArgs(new[] { "--record" }));
        Check.Equal("clip", AppController.CommandFromArgs(new[] { "--clip" }));
        Check.False(AppController.RestartArguments(new[] { "--record", "--show" }).Contains("--record"), "a restart does not toggle again");
    }

    [UnitTest]
    public static void ClipEntry_IsRecording_round_trips_and_an_older_index_loads_as_false()
    {
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "clink-rec-" + Guid.NewGuid().ToString("N"));
        string clips = System.IO.Path.Combine(dir, "clips");
        System.IO.Directory.CreateDirectory(clips);
        string? oldOverride = ClipLibrary.IndexPathOverride;
        try
        {
            ClipLibrary.IndexPathOverride = System.IO.Path.Combine(dir, "clips.json");
            string rec = System.IO.Path.Combine(dir, "other", "rec.mp4");   // outside the clips folder, like a recording from an old folder
            string old = System.IO.Path.Combine(clips, "old.mp4");
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(rec)!);
            System.IO.File.WriteAllText(rec, "x");
            System.IO.File.WriteAllText(old, "x");

            var lib = new ClipLibrary();
            lib.Add(rec, 10, 1, null, Array.Empty<string>(), isRecording: true);
            lib.Add(old, 10, 1, null, Array.Empty<string>());
            var reloaded = new ClipLibrary();
            reloaded.Load(clips);
            Check.True(reloaded.Find(rec)!.IsRecording, "the flag survives a reload");
            Check.False(reloaded.Find(old)!.IsRecording, "a clip is not a recording");

            // An index written before the field existed.
            string json = "[{\"Path\":" + System.Text.Json.JsonSerializer.Serialize(old) + ",\"Created\":\"2026-01-01T00:00:00\",\"Duration\":1,\"Size\":1}]";
            System.IO.File.WriteAllText(ClipLibrary.IndexPathOverride, json);
            var legacy = new ClipLibrary();
            legacy.Load(clips);
            Check.False(legacy.Find(old)!.IsRecording, "a legacy entry without the field loads as false");
        }
        finally
        {
            ClipLibrary.IndexPathOverride = oldOverride;
            try { System.IO.Directory.Delete(dir, true); } catch { }
        }
    }
}
