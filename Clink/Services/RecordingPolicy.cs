using Clink.Engine;

namespace Clink.Services;

/// <summary>Why the controller stopped (or restarted) the engine while a manual recording was going: the restart policy reads it.</summary>
internal enum EngineStopCause
{
    /// <summary>Nothing the controller did: the recording ended by itself (a limit, a stall) or the engine failed on its own (a GPU reset).</summary>
    None,
    /// <summary>A restart the user did not ask for: display change, resume from sleep.</summary>
    Technical,
    /// <summary>The user paused recording.</summary>
    Pause,
    /// <summary>Game-only mode: the game ended and the recorder was stopped.</summary>
    GameEnded,
    /// <summary>Clink is going away (quit, session end, a restart for an import).</summary>
    Shutdown,
}

/// <summary>The pure rules of manual recording: when a recording continues in a new file, and the words the toasts and the status use.</summary>
internal static class RecordingPolicy
{
    /// <summary>
    /// A recording that ended starts a new part (a new file) only when the user still wants to record and the end was not theirs:
    /// a limit (MaxDuration), a capture stall (Discontinuity) or an engine restart that was not asked for (EngineStopped with a
    /// technical cause, or none: the engine failed by itself and the controller restarts it). Never after a pause, a game exit,
    /// shutdown, and never after a stop or a failure of the recording itself. An Empty part (the engine went away before a frame)
    /// continues too, but only when the controller started that part itself (<paramref name="autoPart"/>): the user's own start that
    /// recorded nothing is reported instead.
    /// </summary>
    public static bool ShouldStartNewPart(RecordingEndReason reason, EngineStopCause cause, bool wantRecording, bool autoPart)
    {
        if (!wantRecording) return false;
        if (cause is not (EngineStopCause.None or EngineStopCause.Technical)) return false;
        return reason switch
        {
            RecordingEndReason.EngineStopped or RecordingEndReason.MaxDuration or RecordingEndReason.Discontinuity => true,
            RecordingEndReason.Empty => autoPart,
            _ => false,
        };
    }

    /// <summary>"00:12", "12:34", "1:02:03": the time a recording has run, for the status and the tray.</summary>
    public static string Elapsed(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, Math.Floor(seconds)));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");
    }

    /// <summary>"Recording 00:12", with the game when one is detected.</summary>
    public static string StatusLine(double seconds, string? game) =>
        game != null ? $"Recording {Elapsed(seconds)} · {game}" : $"Recording {Elapsed(seconds)}";

    /// <summary>The subtitle of the "Recording saved" toast: "12:34 · 1.2 GB".</summary>
    public static string SavedSummary(double seconds, long bytes) => $"{Elapsed(seconds)} · {Capabilities.FormatBytes(bytes)}";

    /// <summary>"Recovered 1 recording" / "Recovered 3 recordings": the one toast after a start that found unfinished recordings.</summary>
    public static string RecoveredTitle(int count) => count == 1 ? "Recovered 1 recording" : $"Recovered {count} recordings";

    /// <summary>
    /// The problem a finished recording reports, as (title, text), or null when it ended well (saved, or empty: see the caller).
    /// A failure that still left a file says it was recovered (a full disk finishes the file cleanly: it says saved).
    /// </summary>
    public static (string Title, string Text)? Problem(RecordingResult r)
    {
        string kept = r.Path == null ? "" : r.Reason == RecordingEndReason.DiskFull ? " The recording was saved." : " The recording was recovered and saved.";
        string why = r.Reason switch
        {
            RecordingEndReason.DiskFull => "The disk is nearly full.",
            RecordingEndReason.WriteFailed => "Writing the file failed" + (string.IsNullOrEmpty(r.Error) ? "." : ": " + r.Error),
            RecordingEndReason.Overflow => "The disk could not keep up with the recording.",
            RecordingEndReason.Unrecoverable => "The recording could not be saved" + (string.IsNullOrEmpty(r.Error) ? "." : ": " + r.Error),
            _ => "",
        };
        if (why.Length == 0) return null;
        return ("Recording stopped", why + kept);
    }
}
