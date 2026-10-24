using System.IO;
using System.Windows.Threading;
using Clink.Engine;

namespace Clink.Services;

// Manual recording (start / stop): the controller's side of the engine's recording sink. See RecordingPolicy for the pure rules.
internal sealed partial class AppController
{
    // ------------------------------------------------------------------ manual recording (start / stop)

    /// <summary>Where manual recordings go: a subfolder of the clips folder (the clip scan and the archive look at the folder's own files only).</summary>
    internal string RecordingsFolder => Path.Combine(Settings.ClipsFolder, "Recordings");

    /// <summary>
    /// True for a file in <see cref="RecordingsFolder"/>: a recording is not a clip. "Share last clip" and "trim last" skip it (it is
    /// hours long, the trimmer loads its selection into memory). Pure path arithmetic, no disk access.
    /// </summary>
    internal bool IsRecordingFile(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        try
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? "";
            return string.Equals(dir.TrimEnd('\\'), Path.GetFullPath(RecordingsFolder).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>A recording, not a clip: flagged when added (the index is shared by all clips folders), else in the current Recordings folder.</summary>
    internal bool IsRecordingEntry(ClipEntry e) => e.IsRecording || IsRecordingFile(e.Path);

    /// <summary>Puts a finished recording in the library flagged as a recording; an entry that is there already (the exit path added it) is flagged and kept.</summary>
    private void AddRecording(string path, double duration, long size, string? game, string[] audioTracks)
    {
        if (Library.Find(path) is { } existing)
        {
            if (!existing.IsRecording) { existing.IsRecording = true; Library.Update(existing); }
            return;
        }
        Library.Add(path, duration, size, game, audioTracks, isRecording: true);
    }

    /// <summary>Technical restarts (display change, resume) are announced this long before StartEngine, plus the delay of the restart.</summary>
    private static readonly TimeSpan TechnicalRestartSlack = TimeSpan.FromSeconds(10);

    /// <summary>A display change / resume restart is on its way: no part starts on the old engine. Expires, so a restart that never comes cannot block starts forever.</summary>
    private bool AwaitingTechnicalRestart => DateTime.UtcNow < _technicalRestartUntil;

    private static readonly TimeSpan ContinueWindow = TimeSpan.FromSeconds(180);   // the engine's retry backoff is 30 / 60 / 120 s: two failed starts fit
    private static readonly TimeSpan StallPause = TimeSpan.FromSeconds(5);         // after a capture stall the next part waits this long
    private static readonly TimeSpan QueuedStartWindow = TimeSpan.FromSeconds(30);

    /// <summary>A start was asked for while the recorder was not ready (or by <c>--record</c> at launch) and is still waiting for it.</summary>
    private bool RecordingQueued => DateTime.UtcNow < _recordWhenReadyUntil;

    /// <summary>The tray item: "Stop recording" while a recording runs, is between two parts, or waits for the recorder.</summary>
    private string RecordItemText => Engine.IsRecording || _wantRecording || RecordingQueued ? "Stop recording" : "Start recording";

    /// <summary>The keys of the first "record" hotkey that is set, or null.</summary>
    private string? RecordHotkeyLabel =>
        Settings.Hotkeys.FirstOrDefault(h => string.Equals((h.Action ?? "").Trim(), "record", StringComparison.OrdinalIgnoreCase) && h.Keys.Vk > 0)?.Keys.ToString();

    /// <summary>
    /// The "record" hotkey, the tray item and <c>--record</c>: stops the recording that is going (or the wish for one, between two
    /// parts or while a start waits for the recorder), or starts one. UI thread.
    /// </summary>
    public void ToggleRecording()
    {
        if (_disposed || _exiting) return;
        if (_finishing) { Say("Finishing the recording…"); return; }
        if ((DateTime.UtcNow - _lastRecordToggleAt).TotalMilliseconds < 700) { Say("One moment…"); return; }
        if (Engine.IsRecording || _wantRecording || RecordingQueued) StopRecording(userRequested: true);
        else StartRecording();
    }

    private void StartRecording()
    {
        bool waiting = !IsPaused && GameOnly && _gamePid == 0;
        // The recorder is on its way (a start or a restart of the engine is in flight, the old engine still reports IsRunning): a
        // recording started now would be cut seconds later. Wait for it instead.
        if (!IsPaused && !waiting && !_restarting && !_stopping && (_starting || AwaitingTechnicalRestart))
        {
            _lastRecordToggleAt = DateTime.UtcNow;
            if (!RecordingQueued) _recordWhenReadyUntil = DateTime.UtcNow + QueuedStartWindow;
            Say("Recording starts when the recorder is ready");
            RaiseStateChanged();
            return;
        }
        if (_restarting || _stopping || !Engine.IsRunning)
        {
            string why = _restarting ? "Clink is restarting"
                : IsPaused ? "Recording is paused"
                : waiting ? "Not recording: waiting for a game"
                : Engine.Error is string err && !_starting ? "Recording failed: " + err
                : "Not recording yet";
            Say(why);
            if (!MainWindowInFront)
            {
                if (Settings.ClickSound) System.Media.SystemSounds.Exclamation.Play();
                NotifyProblem("Recording not started", waiting ? why + ". If your game is not detected, add it under Capture → Recording → Custom apps." : why);
            }
            return;
        }
        _lastRecordToggleAt = DateTime.UtcNow;
        Interlocked.Increment(ref _recordingGen);
        if (TryStartRecording() is { } problem)
        {
            _wantRecording = false;
            Say(problem.Text);
            if (Settings.ClickSound) System.Media.SystemSounds.Exclamation.Play();
            NotifyProblem(problem.Title, problem.Text);
            return;
        }
        _wantRecording = true;
        _autoPart = false;
        _emptyAutoParts = 0;
        _partRunGen = _engineRunGen;
        if (Settings.ClickSound) ClickSound.Play(Settings.ClickVolume);
        Say("Recording started");
        string stop = RecordHotkeyLabel is string keys ? $"Press {keys} to stop" : "Stop it from the tray menu";
        Toasts.ShowRecordStarted(CurrentGame != null ? $"{CurrentGame} · {stop}" : stop);
        RaiseStateChanged();
    }

    /// <summary>
    /// <c>--record</c> on a start that found no Clink running: the recorder is not up yet, so the start waits for it (up to 30 s)
    /// instead of failing. Paused, or waiting for a game: refused outright, with the reason (see <see cref="StartRecording"/>).
    /// Called during startup.
    /// </summary>
    internal void QueueRecordingAtLaunch()
    {
        _recordWhenReadyUntil = DateTime.UtcNow + QueuedStartWindow;
        _ui.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            if (!RecordingQueued) return;   // the engine came up first and has acted on it
            if (IsPaused || (GameOnly && _gamePid == 0)) { _recordWhenReadyUntil = DateTime.MinValue; StartRecording(); return; }
            if (Engine.IsRunning) { StartQueuedRecording(); return; }
            Say("Recording starts when the recorder is ready");
        });
    }

    /// <summary>The recorder is up: starts the recording that was asked for while it was not (see <see cref="StartRecording"/>).</summary>
    private void StartQueuedRecording()
    {
        if (!RecordingQueued) return;
        _recordWhenReadyUntil = DateTime.MinValue;
        if (_disposed || _exiting || Engine.IsRecording || _wantRecording) return;
        StartRecording();
    }

    /// <summary>Starts one recording file now. Null when it started, else the (title, text) of why not (nothing has changed then).</summary>
    private (string Title, string Text)? TryStartRecording()
    {
        try
        {
            Engine.StartRecording(new RecordingOptions(RecordingsFolder), CurrentGame);
            return null;
        }
        catch (IOException ex)
        {
            Log.Warn("Start recording: " + ex.Message);
            return ("Recording not started", ex.Message.StartsWith("Not enough free", StringComparison.Ordinal) ? "Not enough free space to record. " + ex.Message : "Could not write the recording: " + ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Warn("Start recording: " + ex.Message);
            return ("Recording not started", "Could not write the recording: " + ex.Message);
        }
        catch (Exception ex)   // InvalidOperationException: the engine is not running / already recording
        {
            Log.Warn("Start recording: " + ex.Message);
            return ("Recording not started", ex.Message);
        }
    }

    /// <summary>
    /// Ends the recording, or the wish for one when no file is open (between two parts, or a start that still waits for the recorder).
    /// Never waits for the file on the UI thread: the end is reported by <see cref="OnRecordingEnded"/>.
    /// </summary>
    private async void StopRecording(bool userRequested)
    {
        _lastRecordToggleAt = DateTime.UtcNow;
        Interlocked.Increment(ref _recordingGen);   // a part-continuation loop of the wish that ends here gives up
        bool queued = RecordingQueued;
        _wantRecording = false;   // before the stop is asked for: the end must not continue as a new part
        _recordWhenReadyUntil = DateTime.MinValue;
        if (userRequested && Settings.ClickSound) ClickSound.Play(Settings.ClickVolume);
        if (!Engine.IsRecording)
        {
            Say(queued ? "Recording cancelled" : "Recording stopped");
            RaiseStateChanged();
            return;
        }
        _finishing = true;   // (cleared by OnRecordingEnded)
        try { await Engine.StopRecordingAsync(); }
        catch (InvalidOperationException) { _finishing = false; /* it has ended already */ }
        catch (Exception ex) { _finishing = false; Log.Warn("Stop recording: " + ex.Message); }
    }

    /// <summary>The user no longer wants a recording (a pause, the game's end, a restart of Clink): the one that is going will not continue as a new part.</summary>
    private void EndRecordingWish(EngineStopCause cause)
    {
        _wantRecording = false;
        _recordWhenReadyUntil = DateTime.MinValue;
        if (Engine.IsRecording) _stopCause = cause;
    }

    /// <summary>The engine is about to be restarted for a technical reason (display change, resume from sleep): the recording that ends with it continues.</summary>
    private void MarkTechnicalRestart()
    {
        if (Engine.IsRecording) _stopCause = EngineStopCause.Technical;
    }

    /// <summary>
    /// A technical restart is announced (display change, resume), before its <paramref name="delay"/>: the recording that ends meanwhile
    /// is marked, and no next part starts until <see cref="StartEngine"/> has been called (it clears the deadline) and the new run is
    /// up, or the deadline (delay + 10 s) has passed.
    /// </summary>
    private void BeginTechnicalRestart(TimeSpan delay)
    {
        _technicalRestartUntil = DateTime.UtcNow + delay + TechnicalRestartSlack;
        MarkTechnicalRestart();
    }

    /// <summary>
    /// The settings window's debounced restart: applied now, or, while a recording runs, when it ends (a restart would cut the file
    /// into parts). UI thread.
    /// </summary>
    public void RequestEngineRestart()
    {
        if (Engine.IsRecording)
        {
            if (_settingsRestartPending) return;
            _settingsRestartPending = true;
            Say("Settings apply when the recording stops");
            Toasts.ShowNotice("Settings apply when the recording stops", "The recorder restarts with them afterwards");
            return;
        }
        StartEngine();
    }

    /// <summary>
    /// A recording ended (any reason). Runs on the UI thread: the engine raises its RecordingStopped event on the
    /// recorder's thread and Start() marshals it here with BeginInvoke. Nothing in it waits for the engine.
    /// </summary>
    private void OnRecordingEnded(RecordingResult r)
    {
        var cause = _stopCause;
        _stopCause = EngineStopCause.None;
        _finishing = false;
        // The file the recording really became (it can end in "(2)"); null when nothing was kept. (Already in the library when the
        // exit path added it: adding again would reset its date.)
        if (r.Path != null)
        {
            try { AddRecording(r.Path, r.DurationSeconds, r.SizeBytes, r.Game, r.AudioTracks); }
            catch (Exception ex) { Log.Warn("recording to library: " + ex.Message); }
        }
        if (_disposed || _exiting) { _wantRecording = false; return; }

        bool again = RecordingPolicy.ShouldStartNewPart(r.Reason, cause, _wantRecording, _autoPart);
        if (r.Reason == RecordingEndReason.Empty) { if (again && ++_emptyAutoParts >= 3) again = false; }   // the recorder gives no video: stop trying
        else _emptyAutoParts = 0;
        if (!again) _wantRecording = false;

        string summary = RecordingPolicy.SavedSummary(r.DurationSeconds, r.SizeBytes);
        if (RecordingPolicy.Problem(r) is { } problem)
        {
            Say($"{problem.Title}: {problem.Text}");
            if (Settings.ClickSound) System.Media.SystemSounds.Exclamation.Play();
            NotifyProblem(problem.Title, problem.Text);
        }
        else if (again)
        {
            // (a part that recorded nothing is not worth a word: the next one is on its way)
            if (r.Reason != RecordingEndReason.Empty && r.Path != null) Say($"Recording part saved ({summary}), continuing in a new file");
        }
        else if (r.Reason == RecordingEndReason.Empty || r.Path == null)
        {
            Say("Nothing recorded");
            Toasts.ShowNotice("Nothing recorded", "The recording had no video, so no file was kept");
        }
        else
        {
            Say($"Recording saved ({summary})");
            Toasts.ShowRecordSaved(r.Game != null ? $"{r.Game} · {summary}" : summary);
        }
        // Recordings are not handed to the archive (it works on the clips folder's own files, which these are not, and they can be
        // hours long) and ClipSaved is for the clip toast / list highlight: the library's Changed event refreshes the list.

        bool settingsRestart = _settingsRestartPending;
        if (settingsRestart)
        {
            StartEngine();   // the settings that waited for the recording (a no-op while paused: resume applies them)
            _settingsRestartPending = false;   // (not left set when StartEngine returned early: it would restart the next recording)
        }
        if (again)
        {
            bool needNewEngine = cause == EngineStopCause.Technical || r.Reason is RecordingEndReason.EngineStopped or RecordingEndReason.Empty || settingsRestart;
            ContinueRecordingAsync(Volatile.Read(ref _recordingGen), _partRunGen, needNewEngine, r.Reason == RecordingEndReason.Discontinuity ? StallPause : TimeSpan.Zero);
        }
        RaiseStateChanged();
    }

    /// <summary>
    /// A recording ended for a technical reason and the user still wants it: starts the next part once the engine is running again
    /// (it is being restarted, or the Tick retry will do it). <paramref name="needNewEngine"/>: the engine went down with the part
    /// (or will be restarted), so the next part waits for a run newer than <paramref name="runGenAtEnd"/> (the old engine can still
    /// report IsRunning for a moment). <paramref name="minDelay"/>: not before this long (after a capture stall). Gives up when the
    /// wish changes, or after 3 minutes.
    /// </summary>
    private async void ContinueRecordingAsync(int gen, int runGenAtEnd, bool needNewEngine, TimeSpan minDelay = default)
    {
        try
        {
            var deadline = DateTime.UtcNow + ContinueWindow;
            var notBefore = DateTime.UtcNow + minDelay;
            bool busyRetried = false;
            while (true)
            {
                if (!_wantRecording || gen != Volatile.Read(ref _recordingGen) || _disposed || _exiting || IsPaused) return;
                if (Engine.IsRunning && !_stopping && !_starting && !_restarting && !AwaitingTechnicalRestart
                    && DateTime.UtcNow >= notBefore && (!needNewEngine || _engineRunGen > runGenAtEnd))
                {
                    if (Engine.IsRecording)
                    {
                        // (not expected: the end that brought us here has cleared it) finish it before the next part
                        try { await Engine.StopRecordingAsync(); } catch (Exception) { }
                        continue;
                    }
                    try
                    {
                        Engine.StartRecording(new RecordingOptions(RecordingsFolder), CurrentGame);
                        _autoPart = true;
                        _partRunGen = _engineRunGen;
                        Say("Recording continues in a new file");
                        RaiseStateChanged();
                        return;
                    }
                    catch (InvalidOperationException ex) when (ex.Message.Contains("Already recording", StringComparison.Ordinal))
                    {
                        if (busyRetried) return;   // somebody else's recording is on: it is the one that goes on
                        busyRetried = true;
                    }
                    catch (InvalidOperationException) { /* the engine went down again: wait for the next start */ }
                    catch (Exception ex)
                    {
                        Log.Warn("Next recording part: " + ex.Message);
                        _wantRecording = false;
                        Say("Recording stopped: " + ex.Message);
                        NotifyProblem("Recording stopped", "The recording could not continue: " + ex.Message);
                        return;
                    }
                }
                if (DateTime.UtcNow > deadline)
                {
                    _wantRecording = false;
                    Say("Recording stopped: the recorder did not come back");
                    NotifyProblem("Recording stopped", "The recorder did not come back after the restart, so the recording could not continue");
                    return;
                }
                await Task.Delay(500);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Continue recording", ex);
            _wantRecording = false;
        }
    }

    /// <summary>
    /// Finishes the recordings an earlier run left behind (off the UI thread), then puts them in the library and says so once.
    /// A recording that is being written right now (another instance) is skipped by the engine. Startup, and when the clips folder changes.
    /// </summary>
    internal void RecoverRecordingsAsync()
    {
        string folder = RecordingsFolder;
        Task.Run(() =>
        {
            IReadOnlyList<RecordingResult> found;
            try { lock (_recoverGate) found = Clink.Engine.Engine.RecoverRecordings(folder); }
            catch (Exception ex) { Log.Warn("Recover recordings: " + ex.Message); return; }
            if (found.Count > 0) _ui.BeginInvoke(() => OnRecordingsRecovered(found));
        });
    }

    private void OnRecordingsRecovered(IReadOnlyList<RecordingResult> found)
    {
        int n = 0;
        foreach (var r in found)
        {
            if (r.Reason == RecordingEndReason.Recovered && r.Path != null)
            {
                try { AddRecording(r.Path, r.DurationSeconds, r.SizeBytes, r.Game, r.AudioTracks); n++; }
                catch (Exception ex) { Log.Warn("recovered recording to library: " + ex.Message); }
            }
            else Log.Warn("Recovery: " + (r.Error ?? r.Reason.ToString()));   // logged only: it would be said again at every start
        }
        if (n == 0 || _disposed) return;
        string title = RecordingPolicy.RecoveredTitle(n);
        Say(title);
        Toasts.ShowNotice(title, "Clink closed while recording; what was written is saved in the Recordings folder");
    }

    /// <summary>
    /// Clink is going (Quit, the app's exit, the session ending): ends a recording and waits for its file for up to 4 s, so the common
    /// case does not depend on the 15 s wait of <see cref="Dispose"/>. The wait is safe on the UI thread: the end is raised on the
    /// recorder's thread and marshalled with BeginInvoke. <paramref name="final"/>: nothing more is recorded or shown afterwards.
    /// </summary>
    public void StopRecordingForExit(bool final = true)
    {
        _wantRecording = false;
        _recordWhenReadyUntil = DateTime.MinValue;
        Interlocked.Increment(ref _recordingGen);
        if (final) _exiting = true;
        if (!Engine.IsRecording) return;
        try
        {
            var task = Engine.StopRecordingAsync();
            if (task.Wait(ExitStopLimit))
            {
                if (task.Result.Path is string path)
                    AddRecording(path, task.Result.DurationSeconds, task.Result.SizeBytes, task.Result.Game, task.Result.AudioTracks);
            }
            else Log.Warn("Exit: the recording was still being finished after 4 s");
        }
        catch (Exception ex) { Log.Warn("Exit: stopping the recording: " + ex.Message); }
    }

    /// <summary>
    /// At the end of <see cref="Dispose"/>, after the engine has been disposed (which waits for a recording to finish): a recording
    /// that finished only then (an exit that did not go through <see cref="StopRecordingForExit"/>, or one that waited out its 4 s)
    /// is put in the library if it is not there. Nothing here throws.
    /// </summary>
    private void AddFinishedRecordingOnExit()
    {
        try
        {
            var task = Engine.StopRecordingAsync();   // after Dispose: the finished recording's task, or a faulted one
            if (!task.IsCompletedSuccessfully) return;
            var r = task.Result;
            if (r.Path is string path && File.Exists(path))
                AddRecording(path, r.DurationSeconds, r.SizeBytes, r.Game, r.AudioTracks);
        }
        catch (Exception ex) { Log.Warn("Exit: the finished recording: " + ex.Message); }
    }
}
