using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Clink.Audio;
using Clink.Engine;
using Clink.Native;
using Clink.UI;

namespace Clink.Services;

/// <summary>What Clink is doing right now, for the window, the tray and the status dot.</summary>
internal enum RecordingState { Starting, Recording, WaitingForGame, Paused, Error }

/// <summary>
/// How a Share ended (<see cref="AppController.LastShareOutcome"/>): a link copied or uploaded; the share window opened because there is
/// no Copium token (nothing failed); turned down for Copium's limits; failed (the upload, or no window over the game); or, rarely,
/// the share window itself could not be opened (<see cref="AppController.ShareError"/> says so; no upload was tried).
/// </summary>
internal enum ShareOutcome { None, LinkCopied, Uploaded, FileShareOpened, Refused, Failed, WindowFailed }

/// <summary>Owns the engine, settings, clip library, hotkeys, game detection, toasts, status dot, archive, tray icon and the single-instance pipe.</summary>
internal sealed partial class AppController : IDisposable
{
    /// <summary>
    /// Appended to the mutex and pipe names so a "--dev" instance runs next to the real one without ever seeing its commands:
    /// "" for a normal run, ".dev.&lt;hash of the data folder&gt;" with <c>--dev</c> (<see cref="DevOptions.InstanceSuffix"/>). Set before
    /// <see cref="AcquireSingleInstance"/>, never changed afterwards.
    /// </summary>
    public static string InstanceSuffix { get; set; } = "";
    /// <summary>Running as <c>--dev &lt;folder&gt;</c>: own data folder, mutex and pipe; no updater, no Run key, hotkeys off unless <see cref="DevHotkeys"/>.</summary>
    public static bool IsDev => InstanceSuffix.Length > 0;
    /// <summary><c>--dev-hotkeys</c>: a dev instance binds its hotkeys too (its hook then swallows those combos system-wide while it runs).</summary>
    public static bool DevHotkeys { get; set; }
    /// <summary>Hidden <c>--dev-accept-import</c> (test aid): a dev instance imports the other copy's settings the moment one is offered, as if the tray item had been clicked and confirmed.</summary>
    public static bool DevAcceptImport { get; set; }
    /// <summary><c>--dev-token</c>: a dev instance keeps the Copium token its settings hold (otherwise it has none).</summary>
    public static bool DevToken { get; set; }
    /// <summary><c>--dev-record</c>: a dev instance records. Without it, it starts paused: recording next to the user's game is not what a test run is for.</summary>
    public static bool DevRecord { get; set; }
    /// <summary>A dev instance that was not asked to record (<c>--dev-record</c>) starts paused.</summary>
    internal static bool StartsPaused => IsDev && !DevRecord;
    public static string PipeName => "Clink.Control" + InstanceSuffix;
    internal static string MutexName => "Clink.SingleInstance.6F1B" + InstanceSuffix;

    /// <summary>The switches that make a restarted process this same dev instance (see <see cref="DevOptions.Switches"/>); none for a normal run.</summary>
    internal static List<string> DevSwitches() =>
        IsDev ? DevOptions.Switches(Settings.Dir, DevHotkeys, DevRecord, DevToken, SettingsImport.PackagesRoot) : new List<string>();
    private static readonly TimeSpan GameExitGrace = TimeSpan.FromSeconds(5);   // keep recording this long after the game's process is gone

    public static AppController Instance { get; private set; } = null!;

    public Settings Settings { get; }
    public ClipLibrary Library { get; } = new();
    public Engine.Engine Engine { get; } = new();
    public Updater Updater { get; }
    public Toasts Toasts { get; }
    public ArchiveService Archive { get; }
    public List<MonitorInfo> Monitors { get; private set; } = new();
    public bool IsPaused { get; private set; }
    public string LastNotice { get; private set; } = "";
    /// <summary>Why the last <see cref="ShareAsync"/> refused to upload (Copium's free limits), for the clip row and the toast to show instead of "Upload failed"; null when it was not refused.</summary>
    public string? ShareRefusal { get; private set; }
    /// <summary>Why the last <see cref="ShareAsync"/> failed (no token, Copium's answer, the network), for the clip row, the toast and the trim editor to show; null when it did not fail.</summary>
    public string? ShareError { get; private set; }
    /// <summary>
    /// How the last <see cref="ShareAsync"/> ended, set on every path of it. The callers tell "the share window opened" (no Copium
    /// token: the user has the file to drag, nothing failed, so no error line) from a failure by it; read it right after the call returns.
    /// </summary>
    public ShareOutcome LastShareOutcome { get; private set; }
    /// <summary>What <see cref="ShareError"/> says when <see cref="LastShareOutcome"/> is <see cref="ShareOutcome.WindowFailed"/>: not an upload failure, so the row and the toast show it as it is.</summary>
    public const string ShareWindowFailedText = "Could not open the share window";
    /// <summary>A Copium upload token is saved: Share uploads. Without one it opens the share window (the file to drag) instead.</summary>
    public bool HasCopiumToken => !string.IsNullOrWhiteSpace(Settings.CopiumToken);
    /// <summary>Version of a downloaded update waiting to be installed, or null.</summary>
    public Version? PendingVersion => Updater.PendingVersion;

    /// <summary>Name of the detected game / custom app (process name), or null while none runs. Set by the 1 s detection poll in both recording modes.</summary>
    public string? CurrentGame { get; private set; }
    /// <summary>"fullscreen" | "library" | "custom": which signal found <see cref="CurrentGame"/>.</summary>
    public string? CurrentGameSource { get; private set; }
    /// <summary>Settings.RecordingMode == "game": the engine runs only while a game or custom app is running.</summary>
    public bool GameOnly => string.Equals(Settings.RecordingMode, "game", StringComparison.OrdinalIgnoreCase);

    public RecordingState State
    {
        get
        {
            if (IsPaused) return RecordingState.Paused;
            if (Engine.IsRunning) return RecordingState.Recording;
            if (GameOnly && _gamePid == 0) return RecordingState.WaitingForGame;
            if (Engine.Error != null) return RecordingState.Error;
            return RecordingState.Starting;
        }
    }

    /// <summary>A manual recording (start / stop) is going: the status dot is red and the status line counts its time. Not the replay buffer.</summary>
    public bool IsManualRecording => !IsPaused && (Engine.IsRecording || _wantRecording);

    /// <summary>One line for the UI header: "Recording · Overwatch", "Recording 00:12 · Overwatch" (manual), "Waiting for a game…", "Paused", "Error", "Starting…".</summary>
    public string StatusText => State switch
    {
        RecordingState.Paused => "Paused",
        _ when _wantRecording && !Engine.IsRecording => "Recording (resuming…)",   // between two parts of a recording
        RecordingState.Recording when IsManualRecording && Engine.Recording is { } rec => RecordingPolicy.StatusLine(rec.Seconds, CurrentGame),
        RecordingState.Recording => CurrentGame != null ? $"Recording · {CurrentGame}" : "Recording",
        RecordingState.WaitingForGame => "Waiting for a game…",
        RecordingState.Error => "Error",
        _ => "Starting…",
    };

    public event Action? StateChanged;
    public event Action<string>? Notice;
    public event Action<ClipEntry>? ClipSaved;
    public event Action? ShowRequested;
    /// <summary>"Clink.exe --trim-last" (or the pipe command "trim"): open the trim editor on the newest clip.</summary>
    public event Action? TrimLastRequested;
    /// <summary>A toast's Trim button: open the trim editor on this clip. When nobody listens the toast opens the editor itself.</summary>
    public event Action<ClipEntry>? TrimRequested;
    /// <summary>An update became ready to install (or the pending state changed otherwise). Raised on the UI thread.</summary>
    public event Action? UpdateStateChanged;

    private readonly Dispatcher _ui;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _engineGate = new(1, 1);   // Engine.Start / Stop are not thread-safe against each other
    private System.Windows.Forms.NotifyIcon? _tray;
    private System.Drawing.Icon? _trayIcon;     // the tray's icon at the taskbar's small-icon size (owned here, unlike SystemIcons)
    private System.Windows.Forms.ToolStripMenuItem? _statusItem, _clipItem, _pauseItem, _updateItem, _importItem;
    private DispatcherTimer? _timer;
    private StatusDotWindow? _dot;
    private static Mutex? s_mutex;
    private int _saving;
    private Task<ClipEntry?>? _saveTask;    // the save in flight (UI thread): its clip once in the library, null if it failed
    private DateTime _clipRefusedAt = DateTime.MinValue;   // a clip turned down before any save started (not recording, --clip at startup)
    // Copium uploads in flight (UI thread); Announced: started by ShareLast, which reports a failure itself.
    private readonly Dictionary<ClipEntry, (Task<bool> Task, bool Announced)> _uploads = new(ReferenceEqualityComparer.Instance);
    private DateTime _lastClipAt = DateTime.MinValue;
    private int _startGen;
    private bool _starting;
    private bool _stopping;                 // a StopEngine is in flight: Engine.IsRunning reads true until its teardown has finished
    private DateTime _lastStartAt = DateTime.MinValue;
    private int _startFailures;             // engine starts that failed in a row (Tick's retry backoff, one balloon per streak)
    private bool _disposed;
    private uint _gamePid;
    private DateTime? _gameStopAt;          // game-only mode: stop the engine at this time unless a game comes back
    private string? _announceGame;          // show the "Recording started" toast for this game once the engine is up
    private int _detecting;
    private string _trayText = "";
    private string? _hotkeyNotice;          // said once at start when the main hotkey was claimed for Alt+C or left unset (MigrateHotkeys)
    private bool _importOffered, _importAccepted;   // another copy's settings were offered in a balloon (once per start); --dev-accept-import has acted
    private bool _wantRecording;            // the user started a manual recording and has not stopped it (UI thread): a technical end continues it as a new part
    private EngineStopCause _stopCause;     // why the controller stopped the engine under a recording (UI thread); consumed by OnRecordingEnded
    private bool _settingsRestartPending;   // a settings change wanted an engine restart while recording: applied when the recording ends
    private bool _exiting;                  // Quit / session end: StopRecordingForExit has run
    private DateTime _lastRecordToggleAt = DateTime.MinValue;
    private int _recordingGen;              // bumped by every start / stop request: a part-continuation loop of an older wish gives up
    private int _engineRunGen;              // bumped each time a start of the engine completes with the engine running: a part waits for a newer run than the one it ended with
    private int _partRunGen;                // _engineRunGen when the recording that is going (or ended last) started
    private DateTime _technicalRestartUntil = DateTime.MinValue; // a display change / resume is on its way to restarting the engine until this time (UI thread): no part starts on the old engine
    private DateTime _recordWhenReadyUntil = DateTime.MinValue;   // a start asked for while the recorder was not ready (or --record at launch) is acted on until this time
    private bool _autoPart;                 // the recording that is going (or ended last) was started by the controller as a continuation, not by the user
    private int _emptyAutoParts;            // automatic parts in a row that recorded nothing
    private bool _finishing;                // the user stopped the recording and its file is being finished
    private readonly object _recoverGate = new();   // one RecoverRecordings pass at a time (a folder change during the startup pass)
    private System.Windows.Forms.ToolStripMenuItem? _recordItem;
    private static readonly TimeSpan ExitStopLimit = TimeSpan.FromSeconds(4);

    public AppController(Settings settings, Updater updater)
    {
        Settings = settings;
        _ui = Dispatcher.CurrentDispatcher;
        Instance = this;
        Updater = updater;
        Toasts = new Toasts(settings, () => SelectedMonitor);
        Archive = new ArchiveService(settings, Library);
        Updater.UpdateAvailable += info => _ui.BeginInvoke(() => OnUpdateAvailable(info));
        Updater.UpdateReady += version => _ui.BeginInvoke(() => OnUpdateReady(version));
    }

    // ------------------------------------------------------------------ single instance

    /// <summary>Returns true if this process should run; otherwise the command was forwarded to the running instance.</summary>
    public static bool AcquireSingleInstance(string[] args)
    {
        bool createdNew;
        try { s_mutex = new Mutex(true, MutexName, out createdNew); }
        catch (UnauthorizedAccessException) { createdNew = false; }   // an elevated Clink owns it: it is running all the same
        if (createdNew) return true;
        SendCommand(CommandFromArgs(args) ?? "show");
        return false;
    }

    /// <summary>The pipe command a command line asks for: "clip" (--clip), "trim" (--trim-last), "share" (--share-last), or null.</summary>
    public static string? CommandFromArgs(string[] args) =>
        args.Any(a => a.Equals("--clip", StringComparison.OrdinalIgnoreCase)) ? "clip"
        : args.Any(a => a.Equals("--trim-last", StringComparison.OrdinalIgnoreCase)) ? "trim"
        : args.Any(a => a.Equals("--share-last", StringComparison.OrdinalIgnoreCase)) ? "share"
        : args.Any(a => a.Equals("--record", StringComparison.OrdinalIgnoreCase)) ? "record"   // toggles a manual recording (to a running Clink; a start finds the recorder not up yet and says so)
        : null;

    /// <summary>
    /// Gives up the single-instance mutex so a replacement process can take it (a restart, an update, or this process on its way
    /// out): released first, then closed. A mutex that is only closed stays owned by the thread that took it until that thread ends,
    /// and a process waiting for it would find it abandoned, or wait for nothing.
    /// </summary>
    public static void ReleaseSingleInstance()
    {
        var mutex = s_mutex;
        s_mutex = null;
        if (mutex == null) return;
        try { mutex.ReleaseMutex(); } catch (Exception) { }   // not ours to release (another thread owns it, an elevated Clink holds the real one)
        try { mutex.Dispose(); } catch (Exception) { }
    }

    // ------------------------------------------------------------------ a Clink that restarts itself

    private const string AfterPidSwitch = "--after-pid";

    /// <summary>The pid after the hidden <c>--after-pid</c> switch: the Clink that started this one by restarting itself. Null when there is none, or it is not a number.</summary>
    internal static int? AfterPid(IEnumerable<string> args)
    {
        var list = args.ToList();
        for (int i = 0; i + 1 < list.Count; i++)
            if (list[i].Equals(AfterPidSwitch, StringComparison.OrdinalIgnoreCase))
                return int.TryParse(list[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out int pid) && pid > 0 ? pid : null;
        return null;
    }

    /// <summary>
    /// Waits up to <paramref name="limit"/> for process <paramref name="pid"/> to be gone. True when it is (or never was there, or is
    /// this process, or cannot be waited for: do not hold the start up for that), false when it was still running at the end.
    /// A restarted Clink does this before it takes the mutex and opens the log, so it never runs next to the one it replaces,
    /// which may still be writing the files the restart changed. Never throws.
    /// </summary>
    internal static bool WaitForExit(int pid, TimeSpan limit)
    {
        if (pid == Environment.ProcessId) return true;
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.WaitForExit(limit);
        }
        catch (Exception) { return true; }   // no such process any more, or no right to wait for it
    }

    /// <summary>The hidden switch a restart gives the new Clink: wait for this process to be gone first (see <see cref="WaitForExit"/>).</summary>
    internal static string[] AfterPidArguments() => new[] { AfterPidSwitch, Environment.ProcessId.ToString(CultureInfo.InvariantCulture) };

    /// <summary>The command line without any <c>--after-pid &lt;pid&gt;</c>: a restart of a restarted Clink waits for itself, not for the one before.</summary>
    private static IEnumerable<string> WithoutAfterPid(IEnumerable<string> args)
    {
        var list = args.ToList();
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].Equals(AfterPidSwitch, StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < list.Count && int.TryParse(list[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out _)) i++;
                continue;
            }
            yield return list[i];
        }
    }

    public static void SendCommand(string cmd)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(2000);
            using var w = new StreamWriter(client);
            w.WriteLine(cmd);
            w.Flush();
        }
        catch (Exception ex) { Log.Warn("pipe send: " + ex.Message); }
    }

    private async Task PipeServerAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(ct);
                using var r = new StreamReader(server);
                string? line = await r.ReadLineAsync(ct);
                if (line != null) _ = _ui.BeginInvoke(() => RunCommand(line));
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log.Warn("pipe server: " + ex.Message); await Task.Delay(500, ct).ContinueWith(_ => { }); }
        }
    }

    /// <summary>A control command from the pipe ("clip", "show", "trim", "share", "pause", "resume"). UI thread.</summary>
    internal void RunCommand(string cmd)
    {
        switch (cmd.Trim().ToLowerInvariant())
        {
            case "clip": ClipNow(); break;
            case "show": ShowRequested?.Invoke(); break;
            case "trim": TrimLastRequested?.Invoke(); break;
            case "share": ShareLast(); break;
            case "record": ToggleRecording(); break;
            case "pause": if (!IsPaused) TogglePause(); break;
            case "resume": if (IsPaused) TogglePause(); break;
            default: Log.Warn("pipe: unknown command " + (cmd.Length > 40 ? cmd[..40] + "…" : cmd)); break;
        }
    }

    /// <summary>
    /// "--clip" / "--share-last" / "--trim-last" on a start that found no Clink running: runs it once the app is up (at idle,
    /// after startup). A clip has nothing to save yet (nothing was recording), so that one says so instead of failing silently.
    /// Called during startup, before the pipe has delivered anything.
    /// </summary>
    internal void RunStartupCommand(string cmd)
    {
        if (cmd == "record") { QueueRecordingAtLaunch(); return; }
        if (cmd != "clip") { _ui.BeginInvoke(DispatcherPriority.ApplicationIdle, () => RunCommand(cmd)); return; }
        // Marked now, not at idle: a --share-last sent right behind this --clip can reach the pipe while Clink is still starting.
        _clipRefusedAt = DateTime.UtcNow;
        _ui.BeginInvoke(DispatcherPriority.ApplicationIdle, () => ShowBalloon("Clink started", "It was not running, so there was nothing to save yet. "
            + (GameOnly ? "It records while a game runs." : "It is recording now.")));
    }

    // ------------------------------------------------------------------ lifecycle

    /// <summary>
    /// A --dev instance does not record unless asked to (--dev-record): it would capture, encode and put toasts over the game the
    /// person running the test is playing. Paused is a state the tray, the window and the hotkeys already know how to leave. The
    /// first thing <see cref="Start"/> does, before anything is hooked, drawn or started.
    /// </summary>
    internal void ApplyStartPause()
    {
        if (!StartsPaused) return;
        IsPaused = true;
        Log.Info("dev: paused, nothing is recorded (--dev-record records)");
    }

    public void Start()
    {
        ApplyStartPause();
        Monitors = Native.Monitors.Enumerate();
        Library.Load(Settings.ClipsFolder);
        MigrateHotkeys();
        Hotkeys.Install(ActiveHotkeys, a => _ui.BeginInvoke(() => OnHotkey(a)));
        // Hardware encoder enumeration loads vendor DLLs and can take a while on some machines: warm it on a worker so the
        // window's codec list and the first engine start find the answer ready instead of probing on the UI thread.
        Task.Run(() => { try { _ = Capabilities.Codecs; } catch (Exception ex) { Log.Warn("codec probe: " + ex.Message); } });
        Engine.StateChanged += () => _ui.BeginInvoke(RaiseStateChanged);
        // Once, for the one engine this controller owns. Raised on the recorder's thread: only ever BeginInvoke here (an Invoke
        // would deadlock against a UI thread that waits for the recording to finish: StopRecordingForExit, Engine.Dispose).
        Engine.RecordingStopped += result => { try { _ui.BeginInvoke(() => OnRecordingEnded(result)); } catch (Exception ex) { Log.Warn("recording end: " + ex.Message); } };
        RecoverRecordingsAsync();
        _ = PipeServerAsync(_cts.Token);
        CreateTray();
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Tick(), _ui);
        _timer.Start();
        SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        if (Settings.RunAtStartup) SetRunAtStartup(true); // keep the Run entry pointing at wherever the exe lives now
        if (GameOnly) { Log.Info("Recording mode: only while a game or custom app runs"); PollGame(); }
        else StartEngine();
        Archive.Start();
        ApplyStatusDot();
        if (!IsDev) Updater.StartBackgroundChecks();   // a --dev instance never contacts GitHub
        // A downloaded update was skipped at startup (install failed earlier, read-only folder, elevated): tell the user, once per start.
        if (Updater.InstallProblem is string problem) { Say(problem); ShowBalloon("Clink update not installed", problem); }
        // A hand edit of settings.json that does not parse, or has values that do not make sense (those keep their defaults).
        if (Settings.LoadProblem is string unread) { Say(unread); ShowBalloon(Settings.FileWasSkipped ? "Clink settings not loaded" : "Clink settings partly read", unread); }
        // The footer line of MigrateHotkeys is easy to miss when Clink starts in the tray; this is a hotkey it just claimed or left unset.
        if (_hotkeyNotice is string hotkeyNotice) ShowBalloon("Clink hotkey", hotkeyNotice);
        // Where the settings live. A Clink started from inside a packaged app (the Claude desktop app) sees that app's private copy of
        // AppData, so says so; a real one took another copy's settings over on a first start (App.RunNormal), or looks for a copy
        // worth offering (the tray item, below).
        if (AppDataRedirect.Notice is string redirected) Say(redirected);
        if (Settings.ImportedFrom is { } taken) Say($"Imported the settings Clink saved while started from inside {taken.FriendlyName} ({taken.SavedLocalText})");
        // Settings were imported (now, or by the Clink that restarted into this one) but the clip lists could not be merged: said once,
        // and every later start tries the merge again.
        if (SettingsImport.TakeClipsNotice() is string clipsNotice) { Say(clipsNotice); ShowBalloon("Clink", clipsNotice); }
        SettingsImport.PendingChanged += OnImportPending;
        if (CanOfferImport) _ = SettingsImport.Scan(Settings);
        RaiseStateChanged();
    }

    /// <summary>
    /// Another copy's settings are looked for by a real Clink with settings of its own (a fresh one has already taken the newest copy
    /// over) that is not itself inside a packaged app; a <c>--dev</c> instance looks only where <c>--packages-root</c> says, so a
    /// test never offers (or accepts) the settings of the person whose machine it runs on (<see cref="SettingsImport.CanOffer"/>).
    /// </summary>
    internal bool CanOfferImport => SettingsImport.CanOffer(Settings, IsDev);

    /// <summary>Once a second on the UI thread: game detection, the game-only start/stop rules, and a StateChanged for the stats line.</summary>
    private void Tick()
    {
        PollGame();
        var now = DateTime.UtcNow;
        if (GameOnly)
        {
            // The mode may have been switched while the engine was running with no game: give it the same grace as a game exit.
            if (_gamePid == 0 && Engine.IsRunning && !_stopping && _gameStopAt == null && !IsPaused) _gameStopAt = now + GameExitGrace;
            if (_gameStopAt is DateTime t && now >= t)
            {
                _gameStopAt = null;
                if (_gamePid == 0 && Engine.IsRunning) { Log.Info("No game running, stopping the recorder"); EndRecordingWish(EngineStopCause.GameEnded); StopEngine(); }
            }
        }
        // A recorder that should be running but is not: the mode was just switched, a start failed (encoder sessions taken by
        // another recorder, GPU or monitor still settling after resume / a display change) or it failed mid-run. Retry: 10 s
        // after a start that stopped without an error, 30 s / 30 s / 1 min / 2 min while starts keep failing; after a GPU reset
        // (driver crash / TDR, Engine.DeviceLost) a third of that, so recording resumes on a fresh device as soon as the driver
        // is back. StartEngine raises one notice per failure streak (NotifyProblem).
        if (_recordWhenReadyUntil != DateTime.MinValue && now >= _recordWhenReadyUntil)
        {
            // A start asked for while the recorder was not ready expired unfired (every other path resets it to MinValue).
            _recordWhenReadyUntil = DateTime.MinValue;
            Say("Recording not started: the recorder did not get ready");
            NotifyProblem("Recording not started", "The recorder did not get ready");
        }
        bool wanted = !IsPaused && !_restarting && (!GameOnly || _gamePid != 0);
        int failedWait = Engine.DeviceLost ? 10 : 30;
        var backoff = TimeSpan.FromSeconds(Engine.Error == null ? 10 : failedWait << Math.Clamp(_startFailures - 1, 0, 2));
        if (wanted && !Engine.IsRunning && !_starting && now - _lastStartAt > backoff)
        {
            if (Engine.DeviceLost) Say("The GPU was reset, restarting capture");
            if (Engine.Error != null) Log.Info("Retrying the recorder after: " + Engine.Error);
            StartEngine();
        }
        RaiseStateChanged();
    }

    // ------------------------------------------------------------------ game detection (docs/SPEC-v2.md §5)

    /// <summary>Kicks off one detection pass on a worker thread (skipped while the previous one is still running).</summary>
    private void PollGame()
    {
        var mon = SelectedMonitor;
        if (mon == null) return;
        if (Interlocked.Exchange(ref _detecting, 1) == 1) return;
        uint pid = _gamePid;
        var custom = Settings.CustomApps?.ToArray() ?? Array.Empty<string>();
        Task.Run(() =>
        {
            bool alive = false;
            GameProcessTracker.GameResult? found = null;
            try
            {
                alive = pid != 0 && GameProcessTracker.IsAlive(pid);
                if (!alive) found = GameProcessTracker.DetectGame(mon, custom);
            }
            catch (Exception ex) { Log.Warn("game poll: " + ex.Message); }
            _ui.BeginInvoke(() => { _detecting = 0; OnGamePoll(pid, alive, found); });
        });
    }

    private void OnGamePoll(uint polledPid, bool alive, GameProcessTracker.GameResult? found)
    {
        if (polledPid != _gamePid) return;   // state moved on while we were looking
        if (alive) { _gameStopAt = null; return; }   // a game is "gone" when its process exits, not when it loses focus
        if (_gamePid != 0)
        {
            Log.Info($"Game exited: {CurrentGame}");
            GameProcessTracker.Forget(_gamePid);   // its pid may be handed to an unrelated process within seconds
            _gamePid = 0; CurrentGame = null; CurrentGameSource = null; _announceGame = null;
            if (GameOnly) _gameStopAt = DateTime.UtcNow + GameExitGrace;
            RaiseStateChanged();
        }
        if (found is { } g)
        {
            _gamePid = g.Pid; CurrentGame = g.Name; CurrentGameSource = g.Source; _gameStopAt = null;
            Log.Info($"Game detected: {g.Name} pid={g.Pid} ({g.Source})");
            if (!IsPaused)
            {
                if (Engine.IsRunning && !_stopping) Toasts.ShowRecordingStarted(g.Name);     // always mode, or still in the grace period
                else
                {
                    // The toast follows once the engine is up: started here (game-only; this also overrides a grace-period stop
                    // that is still tearing down), or by Start()/Tick in always mode.
                    _announceGame = g.Name;
                    if (GameOnly) StartEngine();
                }
            }
            RaiseStateChanged();
        }
        // One game identity, published wherever it is used: the engine's Game audio track follows it, not only the focused
        // window (SPEC-v2 §5), and names the clip with it when it has no game of its own; a hotkey fires with extra modifiers
        // held (crouch + ScrollLock) only while it is in front.
        Engine.SetDetectedGame(_gamePid, CurrentGame);
        Hotkeys.GamePid = _gamePid;
    }

    // ------------------------------------------------------------------ updates

    private static bool MainWindowVisible => System.Windows.Application.Current?.MainWindow?.IsVisible == true;

    /// <summary>
    /// The user has the window in front of them: shown, not minimized, focused. Not <see cref="MainWindowVisible"/>, which a
    /// window minimized to the taskbar, or open behind a borderless game after an alt-tab, still is.
    /// </summary>
    private static bool MainWindowInFront =>
        System.Windows.Application.Current?.MainWindow is { IsVisible: true, IsActive: true, WindowState: not System.Windows.WindowState.Minimized };

    /// <summary>
    /// Why something asked for from the hotkey, the tray or the command line (no UI of their own) did not happen, shown once
    /// where the user is looking; the caller has already put it in the footer. Nothing more while the window is in front; a
    /// Clink toast (wrapped, at least 8 s) while the detected game is in front, since Windows holds tray balloons back over a
    /// game; a tray balloon otherwise. <paramref name="overGame"/>: the caller found a full-screen application in front that Clink
    /// did not detect as a game (<see cref="UserNotificationState"/>), which holds balloons back just the same.
    /// </summary>
    private void NotifyProblem(string title, string text, bool overGame = false)
    {
        if (MainWindowInFront) return;
        if (overGame || Hotkeys.GameInFront()) Toasts.ShowProblem(title, text);
        else ShowBalloon(title, text);
    }

    private void OnUpdateAvailable(UpdateInfo info)
    {
        // With auto-update off this only happens from a manual check, and the version click opens the release page itself.
        Log.Info($"update: v{info.Version} ({info.Tag}) available{(Settings.AutoUpdate ? "" : " (auto-update is off)")}");
    }

    private void OnUpdateReady(Version version)
    {
        if (_updateItem != null) { _updateItem.Text = $"Update to v{version}"; _updateItem.Visible = true; }
        string? problem = Updater.InstallProblemFor(Environment.ProcessPath ?? "");
        string msg = problem == null
            ? $"v{version} downloaded - installs on next start, or click 'Update to v{version}'"
            : $"v{version} downloaded, but {problem}. Install it by hand from {Updater.UpdatesDir}";
        Say(msg);
        if (!MainWindowVisible) ShowBalloon("Clink update ready", msg);
        UpdateStateChanged?.Invoke();
    }

    /// <summary>Installs the downloaded update now: hands over to the new exe and quits.</summary>
    public void UpdateNow()
    {
        if (IsDev) { Say(Updater.DevModeNote); return; }   // the updates folder is the real Clink's, and the new process would not be this dev instance
        string? pending = Updater.PendingPath;
        if (pending == null || !File.Exists(pending)) { Say("No update downloaded yet"); return; }
        string? problem = Updater.InstallProblemFor(Environment.ProcessPath ?? "");
        if (problem != null) { Say($"Cannot install v{Updater.PendingVersion}: {problem}. Install it by hand from {Updater.UpdatesDir}"); return; }
        try
        {
            Log.Info($"update: installing v{Updater.PendingVersion} from {pending}");
            // Come back the way we are now: visible if the window is open, otherwise in the tray (and as the same --dev instance, if
            // it is one), and only once this process is gone.
            Updater.LaunchApply(pending, Environment.ProcessPath!, UpdateRestartArguments(MainWindowVisible));
        }
        catch (Exception ex)
        {
            Log.Error("update: launch failed", ex);
            Say("Update failed to start: " + ex.Message);
            return;
        }
        Quit();
    }

    /// <summary>
    /// What the Clink an update installs is started with: the window shown or in the tray as it is now, the same --dev instance if
    /// this is one, and <c>--after-pid</c> with this process, so it takes the mutex and the log only once this one has ended.
    /// </summary>
    internal static List<string> UpdateRestartArguments(bool windowVisible)
    {
        var args = new List<string> { windowVisible ? "--show" : "--minimized" };
        args.AddRange(DevSwitches());
        args.AddRange(AfterPidArguments());
        return args;
    }

    // ------------------------------------------------------------------ settings saved by another copy of Clink

    /// <summary>
    /// <see cref="SettingsImport"/> found (or lost) a copy of the settings, saved inside a packaged app's private AppData, that differs
    /// from these: the tray menu gets (or loses) "Import settings saved … (Claude copy)…", and the first time per start one balloon
    /// says so. UI thread.
    /// </summary>
    private void OnImportPending()
    {
        if (!_ui.CheckAccess()) { _ui.BeginInvoke(OnImportPending); return; }
        var copy = SettingsImport.Pending;
        if (_importItem != null)
        {
            _importItem.Visible = copy != null;
            if (copy != null) _importItem.Text = $"Import settings saved {copy.SavedLocalText} ({copy.FriendlyName} copy)…";
        }
        if (copy == null) return;
        if (!_importOffered)
        {
            _importOffered = true;
            ShowBalloon("Clink", "Settings from another copy of Clink found: right-click the tray icon to import");
        }
        if (IsDev && DevAcceptImport && !_importAccepted)
        {
            _importAccepted = true;
            Log.Info("import: --dev-accept-import, importing now");
            ImportForeignSettings(copy);
        }
    }

    /// <summary>The tray item: says what the other copy would change and asks. Yes imports and restarts, No turns that copy down for good, Cancel leaves it for later.</summary>
    private void AskToImport()
    {
        var copy = SettingsImport.Pending;
        if (copy == null || _importInProgress) return;
        string what = copy.Differences.Count > 0 ? string.Join(", ", copy.Differences) : "a few settings";
        string text = $"Clink also has settings saved {copy.SavedLocalText} by a copy of Clink that was started from inside {copy.FriendlyName}.\n\n"
            + $"They differ from yours in: {what}.\n\n"
            + "Import them? Your current settings are kept as settings.pre-import.json (or the next free settings.pre-import.N.json), and Clink restarts.\n\n"
            + "Yes: import and restart.\nNo: keep mine and stop asking about this copy.\nCancel: decide later.";
        var owner = System.Windows.Application.Current?.MainWindow is { IsVisible: true } w ? w : null;
        var answer = owner != null
            ? MessageBox.Show(owner, text, "Clink", MessageBoxButton.YesNoCancel, MessageBoxImage.Question)
            : MessageBox.Show(text, "Clink", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Yes) ImportForeignSettings(copy);
        else if (answer == MessageBoxResult.No) SettingsImport.Decline(Settings, copy);
    }

    private static readonly TimeSpan RecorderStopLimit = TimeSpan.FromSeconds(10);   // how long a restart waits for a recorder that is in the middle of starting

    private bool _importInProgress;   // an import has begun and has not failed: a second one is refused, the tray item is off
    private bool _restarting;         // a restart is under way: no clip is saved and the recorder is not started again

    /// <summary>An import is under way (it ends with this process, or with a message saying why not): the tray item is off and a second import is refused.</summary>
    internal bool ImportInProgress => _importInProgress;

    /// <summary>Starts the process a restart hands over to. The unit tests replace it with one that starts nothing.</summary>
    internal Action<ProcessStartInfo> StartProcess { get; set; } = psi => Process.Start(psi)?.Dispose();

    /// <summary>What ends this process once its replacement is running (null: <see cref="Quit"/>). The unit tests replace it.</summary>
    internal Action? ExitForRestart { get; set; }

    /// <summary>The command line this process was started with, as a restart starts the next one from it (null: the real one). The unit tests replace it.</summary>
    internal IReadOnlyList<string>? CommandLine { get; set; }

    /// <summary>
    /// Takes over the settings (and the clip links) another copy of Clink saved in a packaged app's private AppData and restarts
    /// Clink to use them. In this order: a second import is refused from now on; saving a clip and starting the recorder are off;
    /// settings and clip index saves are frozen, so nothing this process still has pending (the window's timer, the updater, a clip
    /// being saved or shared) can put the old files back; the recorder and the archive are stopped; the import is made
    /// (<see cref="SettingsImport.Import"/>; what it replaces is kept beside it); and only then the new Clink is started, which waits
    /// for this process to be gone (<see cref="RestartClink"/>). If the import fails nothing is changed and Clink carries on. If the
    /// new Clink cannot be started either, saves are thawed and the recorder and archive go on, and the settings this Clink is running
    /// with are written back over the imported ones (so what is saved is what runs, not a trap for the next save) and the import is
    /// offered again; the user is told. UI thread (any other is marshalled).
    /// </summary>
    public void ImportForeignSettings(ForeignSettings copy)
    {
        if (!_ui.CheckAccess()) { _ui.BeginInvoke(() => ImportForeignSettings(copy)); return; }
        if (_importInProgress)
        {
            Log.Info("import: one is under way already: this request is refused");
            Say("An import is already under way");
            return;
        }
        _importInProgress = true;
        if (_importItem != null) _importItem.Enabled = false;
        bool wasRecording = Engine.IsRunning;
        ImportResult result;
        try
        {
            StopForRestart();
            result = SettingsImport.Import(copy);
        }
        catch (Exception ex)
        {
            Log.Error("import: failed", ex);
            ContinueAfterFailedRestart(wasRecording);
            string why = "Could not import those settings: " + ex.Message;
            Say(why);
            ShowBalloon("Clink", why);
            return;
        }
        Say(result.ClipsNotice ?? $"Imported the settings saved {copy.SavedLocalText}: restarting Clink");
        if (RestartClink(out string? error)) return;

        ContinueAfterFailedRestart(wasRecording);
        SettingsImport.ForgetPendingClipMerge();
        Settings.Save();
        SettingsImport.Offer(copy);   // at once: whoever called (the App tab's banner) looks at Pending as soon as this returns
        string text = $"Clink could not restart itself ({error}), so the import was not applied: your settings are as they were";
        Say(text);
        ShowBalloon("Clink", text);
    }

    /// <summary>
    /// Everything a restart must not have going on behind it: no clip is saved and the recorder is not started again (until
    /// <see cref="ContinueAfterFailedRestart"/>), settings and clip index saves are frozen, the recorder is stopped, and so is the
    /// archive (it moves clips and updates the index). A clip that is being saved at this moment keeps its file, which the next start
    /// finds in the clips folder, but its index entry is not written. Nothing here throws.
    /// </summary>
    private void StopForRestart()
    {
        _restarting = true;
        EndRecordingWish(EngineStopCause.Shutdown);
        Settings.FreezeSaves();
        Library.FreezeSaves();
        Interlocked.Increment(ref _startGen);   // a recorder start that has not begun is cancelled
        _starting = false;
        try
        {
            if (_engineGate.Wait(RecorderStopLimit)) { try { Engine.Stop(); } finally { _engineGate.Release(); } }
            else Log.Warn("restart: the recorder was still starting; it is left to the shutdown");
        }
        catch (Exception ex) { Log.Warn("restart: could not stop the recorder: " + ex.Message); }
        try { Archive.Stop(); } catch (Exception ex) { Log.Warn("restart: could not stop the archive: " + ex.Message); }
    }

    /// <summary>The restart did not happen: saves are thawed, the archive runs again, the recorder too if it was recording, the tray item is back.</summary>
    private void ContinueAfterFailedRestart(bool restartRecorder)
    {
        Settings.ThawSaves();
        Library.ThawSaves();
        _restarting = false;
        try { Archive.Start(); } catch (Exception ex) { Log.Warn("restart: could not start the archive again: " + ex.Message); }
        if (restartRecorder) StartEngine();
        _importInProgress = false;
        if (_importItem != null) _importItem.Enabled = true;
        RaiseStateChanged();
    }

    /// <summary>
    /// Starts Clink again (<c>--show</c> and the same options, so a <c>--dev</c> instance comes back as that instance, and
    /// <c>--after-pid</c> with this process) and ends this one. The new Clink waits for this process to be gone (at most 10 s) before it
    /// takes the single-instance mutex or opens the log, so the two never run side by side; this one gives the mutex up as soon as the
    /// new one is started, so that if this process hangs on the way out the new Clink still gets it when its wait is over. False, and
    /// <paramref name="error"/> says why, when the new process could not be started: nothing has been given up then.
    /// </summary>
    internal bool RestartClink(out string? error)
    {
        error = null;
        var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        foreach (string a in RestartArguments(CommandLine ?? Environment.GetCommandLineArgs().Skip(1))) psi.ArgumentList.Add(a);
        foreach (string a in AfterPidArguments()) psi.ArgumentList.Add(a);
        try { StartProcess(psi); }
        catch (Exception ex)
        {
            Log.Error("restart: could not start Clink again", ex);
            error = ex.Message;
            return false;
        }
        Log.Info("restart: the new Clink is started (" + string.Join(' ', psi.ArgumentList) + "); this one ends");
        ReleaseSingleInstance();
        (ExitForRestart ?? Quit)();
        return true;
    }

    /// <summary>
    /// The command line of the restarted Clink: this one's, without what only makes sense once (a command, --minimized, --no-update,
    /// the <c>--after-pid</c> this one was given), with <c>--show</c>; and, for a <c>--dev</c> instance, its dev switches written out
    /// the same way every time (<see cref="DevSwitches"/>, whichever form it was started with, without the one-start
    /// <c>--dev-accept-import</c>). A restart that lost <c>--dev</c> would be a normal start: it takes the real Clink's mutex, or
    /// forwards a command to it. <see cref="RestartClink"/> adds its own <c>--after-pid</c>.
    /// </summary>
    internal static List<string> RestartArguments(IEnumerable<string> args)
    {
        var once = new[] { "--clip", "--trim-last", "--share-last", "--record", "--minimized", "--no-update" };
        var kept = WithoutAfterPid(DevOptions.WithoutSwitches(args)).Where(a => !once.Contains(a, StringComparer.OrdinalIgnoreCase)).ToList();
        if (!kept.Contains("--show", StringComparer.OrdinalIgnoreCase)) kept.Add("--show");
        kept.AddRange(DevSwitches());
        return kept;
    }

    private void OnDisplayChanged(object? sender, EventArgs e)
    {
        _ui.BeginInvoke(async () =>
        {
            BeginTechnicalRestart(TimeSpan.FromMilliseconds(1500));   // at once, not after the delay: a recording that ends in between must not start its next part on the old engine
            await Task.Delay(1500);
            RefreshTrayIcon();   // a DPI change moves the tray's and the taskbar buttons' icon sizes
            AppIcons.RefreshWindows();
            Monitors = Native.Monitors.Enumerate();
            Say("Display changed, restarting capture");
            MarkTechnicalRestart();
            StartEngine();
            ApplyStatusDot();
        });
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        // Monitor handles can change across sleep (a monitor that did not wake up, a dock): restart on fresh ones.
        if (e.Mode == PowerModes.Suspend) _ui.BeginInvoke(MarkTechnicalRestart);
        if (e.Mode == PowerModes.Resume) _ui.BeginInvoke(async () => { BeginTechnicalRestart(TimeSpan.FromMilliseconds(3000)); await Task.Delay(3000); Monitors = Native.Monitors.Enumerate(); MarkTechnicalRestart(); StartEngine(); });
    }

    public MonitorInfo? SelectedMonitor =>
        Monitors.FirstOrDefault(m => string.Equals(m.DeviceName, Settings.MonitorDevice, StringComparison.OrdinalIgnoreCase))
        ?? Monitors.FirstOrDefault(m => m.IsPrimary) ?? Monitors.FirstOrDefault();

    /// <summary>Audio tracks in a clip: the mix plus each enabled part (the size arithmetic in <see cref="Capabilities"/> needs the count).</summary>
    public int AudioTrackCount => 1 + (Settings.TrackGame ? 1 : 0) + (Settings.TrackMic ? 1 : 0) + (Settings.TrackVoice ? 1 : 0) + (Settings.TrackDesktop ? 1 : 0);

    /// <summary>
    /// The engine configuration from the settings. The bitrate is what the Capture tab shows: the manual value, or the one a
    /// size target picks for the replay length. <see cref="EngineConfig.Codec"/> is the user's choice as written; it is
    /// resolved against the GPU's encoders (<see cref="Capabilities.EffectiveCodec"/>) by <see cref="StartEngine"/> off the UI thread.
    /// </summary>
    public EngineConfig BuildConfig()
    {
        var mon = SelectedMonitor ?? throw new InvalidOperationException("No monitor found");
        (uint w, uint h) = OutputSize(mon, Settings.Resolution);
        int secs = Math.Clamp(Settings.BufferSeconds, 5, 3600);
        var (mbps, _) = Capabilities.ResolveBitrate(Settings, AudioTrackCount, secs);
        return new EngineConfig
        {
            Monitor = mon,
            OutWidth = w,
            OutHeight = h,
            Fps = (uint)Math.Clamp(Settings.Fps, 15, 120),
            BitrateMbps = Math.Clamp(mbps, Capabilities.MinTargetMbps, Capabilities.MaxTargetMbps),
            // Any case or spacing ("HEVC", "h.265") is accepted, like the combos do; AV1 goes through as "av1".
            Codec = EngineConfig.NormalizeCodec(Settings.Codec),
            GopSeconds = Math.Clamp(Settings.KeyframeSeconds, 0.25f, 5f),
            BufferSeconds = secs,
            CaptureCursor = Settings.CaptureCursor,
            TrackDesktop = Settings.TrackDesktop, TrackGame = Settings.TrackGame, TrackMic = Settings.TrackMic, TrackVoice = Settings.TrackVoice,
            AllDeviceId = Settings.AllDeviceId,
            MicDeviceId = Settings.MicDeviceId,
            GameProcess = GameProcessTracker.NormalizeName(Settings.GameProcess),
            VoiceApp = GameProcessTracker.NormalizeName(Settings.VoiceApp),
            HdrKnee = Settings.HdrKnee, HdrPeak = Settings.HdrPeak,
            ClipsFolder = Settings.ClipsFolder,
        };
    }

    public static (uint w, uint h) OutputSize(MonitorInfo mon, string resolution)
    {
        int targetH = resolution.Trim().ToLowerInvariant() switch { "1440p" => 1440, "1080p" => 1080, "720p" => 720, _ => 0 };
        double w = mon.Width, h = mon.Height;
        // The preset names the short side: a portrait 1080×1920 monitor is already 1080p, and its 720p is 720×1280.
        double shortSide = Math.Min(w, h);
        if (targetH > 0 && targetH < shortSide) { double k = targetH / shortSide; w *= k; h *= k; }
        return ((uint)Math.Round(w / 2) * 2, (uint)Math.Round(h / 2) * 2);
    }

    // ------------------------------------------------------------------ engine

    /// <summary>
    /// (Re)starts the engine with the current settings. In game-only mode with no game running this stops it instead, so the
    /// UI can call it after any settings change (including the recording mode) without caring which mode is on.
    /// </summary>
    public void StartEngine()
    {
        _technicalRestartUntil = DateTime.MinValue;   // the restart that was waited for is here (or will not come: paused, restarting)
        if (IsPaused || _restarting) return;   // a restart is under way: the recorder was stopped for it and stays stopped
        if (GameOnly && _gamePid == 0)
        {
            if (Engine.IsRunning) { EndRecordingWish(EngineStopCause.GameEnded); StopEngine(); }
            RaiseStateChanged();
            return;
        }
        int gen = Interlocked.Increment(ref _startGen);
        _settingsRestartPending = false;   // this start reads the settings as they are now
        _lastStartAt = DateTime.UtcNow;   // also when the config fails: Tick retries on its backoff, not every second
        EngineConfig cfg;
        try { cfg = BuildConfig(); }
        catch (Exception ex) { Say("Cannot start: " + ex.Message); if (_stopping) StopEngine(); return; }   // the new generation cancelled that stop: re-issue it
        _stopping = false;
        _starting = true;
        Task.Run(() =>
        {
            _engineGate.Wait();
            try
            {
                if (gen != _startGen) return;
                // A codec this GPU cannot encode (or H.264 above 4096 px) falls back to one it can (the Capture tab says so); the
                // probe runs here, not on the UI thread.
                try
                {
                    string effective = Capabilities.EffectiveCodec(cfg.Codec, cfg.OutWidth, cfg.OutHeight);
                    if (effective != cfg.Codec)
                    {
                        Log.Info(Capabilities.IsSupported(cfg.Codec)
                            ? $"H.264 cannot encode {cfg.OutWidth}x{cfg.OutHeight}, recording {effective}"
                            : $"Codec {cfg.Codec} has no hardware encoder here, recording {effective}");
                        cfg.Codec = effective;
                    }
                }
                catch (Exception ex) { Log.Warn("codec check: " + ex.Message); }
                try { Engine.Start(cfg); }
                catch (Exception ex) { Log.Error("Engine start", ex); }
            }
            finally { _engineGate.Release(); }
            _ui.BeginInvoke(() =>
            {
                if (gen == _startGen)
                {
                    // Only the newest start reports: a superseded one would show an older start's error.
                    _starting = false;
                    if (Engine.IsRunning)
                    {
                        _startFailures = 0;
                        _engineRunGen++;
                        StartQueuedRecording();
                    }
                    else if (Engine.Error != null)
                    {
                        Say("Recording failed: " + Engine.Error);
                        // The window is usually in the tray while a game runs, so the footer alone is invisible. Once per
                        // streak: Tick keeps retrying.
                        if (_startFailures++ == 0) NotifyProblem("Clink is not recording", Engine.Error);
                    }
                }
                if (Engine.IsRunning && _announceGame is string game) { _announceGame = null; Toasts.ShowRecordingStarted(game); }
                RaiseStateChanged();
            });
        });
    }

    private void StopEngine()
    {
        int gen = Interlocked.Increment(ref _startGen);   // a start that has not run yet is cancelled; a later start cancels this stop
        _starting = false;
        _startFailures = 0;
        _stopping = true;
        Task.Run(() =>
        {
            bool stopped = false;
            _engineGate.Wait();
            try { if (gen == _startGen) { Engine.Stop(); stopped = true; } }
            catch (Exception ex) { Log.Warn("Engine stop: " + ex.Message); }
            finally { _engineGate.Release(); }
            // The recorder is idle now: give the last clips' buffers back to the OS. They are gen2 / large-object garbage that
            // an idle tray app may not collect for hours (a ~20 ms pause here costs nothing).
            if (stopped) { try { GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true); } catch { } }
            _ui.BeginInvoke(() =>
            {
                if (gen == _startGen) _stopping = false;
                RaiseStateChanged();
            });
        });
    }

    public void TogglePause()
    {
        IsPaused = !IsPaused;
        if (IsPaused) { EndRecordingWish(EngineStopCause.Pause); StopEngine(); Say("Recording paused"); }
        else
        {
            Say(GameOnly && _gamePid == 0 ? "Recording resumed: waiting for a game" : "Recording resumed");
            _announceGame = CurrentGame;   // "Recording started · Game" once the engine is back up, when a game is running
            StartEngine();
        }
        if (_pauseItem != null) _pauseItem.Text = IsPaused ? "Resume recording" : "Pause recording";
        RaiseStateChanged();
    }

    public void Say(string msg)
    {
        LastNotice = msg;
        Log.Info("notice: " + msg);
        Notice?.Invoke(msg);
    }

    /// <summary>Updates the tray tooltip / menu and the status dot, then raises <see cref="StateChanged"/>. UI thread.</summary>
    private void RaiseStateChanged()
    {
        UpdateTray();
        if (_recordItem != null) _recordItem.Text = RecordItemText;
        if (_dot != null && Settings.StatusDot) { try { _dot.Apply(State, IsManualRecording); } catch { } }
        StateChanged?.Invoke();
    }

    // ------------------------------------------------------------------ hotkeys (docs/SPEC-v2.md §6)

    /// <summary>
    /// Settings.Load() has already put the hotkey list in shape (<c>Hotkeys[0]</c> is the main hotkey, see
    /// <see cref="Settings.EnsureHotkeyModel"/>) and noted what changed: a settings file of an older schema, a first start or a list
    /// that had to be fixed is saved once here, in the current layout. When no hotkey saved the full replay, the user is told once
    /// which key the main hotkey got, or that it still needs one.
    /// </summary>
    private void MigrateHotkeys()
    {
        if (Settings.Migrated) { Settings.Save(); Log.Info("settings: migrated to schema 3"); }
        if (Settings.MainHotkeyAssigned) Say(_hotkeyNotice = "Alt+C now saves the full replay. Change it under Capture → Hotkeys");
        else if (Settings.MainHotkeyNeedsSetup) Say(_hotkeyNotice = "Set your main hotkey under Capture → Hotkeys: it saves the full replay");
    }

    /// <summary>
    /// The bindings the keyboard hook gets: <see cref="Settings.Hotkeys"/>, except in a <c>--dev</c> instance, which binds nothing
    /// unless started with <c>--dev-hotkeys</c>, so it never swallows a combo the real Clink (or a game) is waiting for. The hook is
    /// installed either way, which is all the hotkey editor's key capture needs.
    /// </summary>
    private IReadOnlyList<HotkeyAction> ActiveHotkeys => IsDev && !DevHotkeys ? Array.Empty<HotkeyAction>() : Settings.Hotkeys;

    /// <summary>Re-reads <see cref="Settings.Hotkeys"/> after the list changed in the UI (an unset main hotkey and no other row means no hotkeys).</summary>
    public void ReloadHotkeys()
    {
        Hotkeys.Rebind(ActiveHotkeys);
        UpdateTrayHotkey();
    }

    /// <summary>The keys that save a clip, for labels ("Press Alt+C to save a clip"): the main hotkey's, else the first additional "clip" hotkey that is set; null when neither is (the main may be unset by choice).</summary>
    public string? ClipHotkeyLabel => ClipHotkeyLabelFor(Settings.Hotkeys);

    /// <summary><see cref="ClipHotkeyLabel"/> for a hotkey list: <c>[0]</c> is the main hotkey; any "clip" row after it with keys is the fallback.</summary>
    internal static string? ClipHotkeyLabelFor(IReadOnlyList<HotkeyAction> hotkeys)
    {
        if (hotkeys.Count > 0 && hotkeys[0].Keys.Vk > 0) return hotkeys[0].Keys.ToString();
        return hotkeys.Skip(1).FirstOrDefault(h => Hotkeys.IsClip(h.Action) && h.Keys.Vk > 0)?.Keys.ToString();
    }

    /// <summary>"Clip now (F9)" for the tray menu, or just "Clip now" without a binding.</summary>
    private string ClipTrayText => ClipHotkeyLabel is string l ? $"Clip now ({l})" : "Clip now";

    private void OnHotkey(HotkeyAction a)
    {
        switch ((a.Action ?? "clip").Trim().ToLowerInvariant())
        {
            case "pause":
                TogglePause();
                // In a game nothing else shows it: a press by mistake would stop recording without a sign. A resume with a game
                // running gets its "Recording started" toast once the engine is up.
                if (!MainWindowInFront && Settings.ToastRecording.Enabled && (IsPaused || CurrentGame == null))
                    Toasts.ShowNotice(IsPaused ? "Recording paused" : "Recording resumed", IsPaused ? $"Press {a.Keys} to resume" : GameOnly ? "Waiting for a game" : "Replay buffer restarted", paused: IsPaused);
                break;
            case "share-last": case "share": ShareLast(); break;
            case "record": ToggleRecording(); break;
            default: ClipNow(a.Seconds > 0 ? a.Seconds : null); break;
        }
    }

    // ------------------------------------------------------------------ clips

    /// <param name="seconds">Replay length to save; null = the replay-length slider (<see cref="Settings.BufferSeconds"/>).</param>
    public async void ClipNow(int? seconds = null)
    {
        if (_restarting || !Engine.IsRunning)
        {
            bool waiting = !IsPaused && GameOnly && _gamePid == 0;
            // A recorder that failed to start says why: "yet" would promise a start that may never come.
            string why = _restarting ? "Clink is restarting"
                : IsPaused ? "Recording is paused"
                : waiting ? "Not recording: waiting for a game"
                : Engine.Error is string err && !_starting ? "Recording failed: " + err
                : "Not recording yet";
            _clipRefusedAt = DateTime.UtcNow;   // a share-last right behind this press must not share the clip before it
            Say(why);
            // The hotkey, the tray item and --clip have no UI of their own and the window is usually in the tray, minimized or
            // behind the game while one runs: a footer line alone would make a missed clip look saved, so say it where the user
            // notices unless the window is in front of them (with an error sound when the click sound is on: no click played).
            if (!MainWindowInFront)
            {
                if (Settings.ClickSound) System.Media.SystemSounds.Exclamation.Play();
                NotifyProblem("Clip not saved", waiting ? why + ". If your game is not detected, add it under Capture → Recording → Custom apps." : why);
            }
            return;
        }
        if ((DateTime.UtcNow - _lastClipAt).TotalMilliseconds < 700) return;
        if (Interlocked.Exchange(ref _saving, 1) == 1) return;
        _lastClipAt = DateTime.UtcNow;
        _clipRefusedAt = DateTime.MinValue;
        var saved = new TaskCompletionSource<ClipEntry?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _saveTask = saved.Task;
        if (Settings.ClickSound) ClickSound.Play(Settings.ClickVolume);
        try
        {
            int? length = seconds is int s && s > 0 ? Math.Clamp(s, 1, 3600) : null;
            // The engine names the clip after its own game; the app's detection fills in when it has none (device Game track, custom app).
            var result = await Engine.SaveClipAsync(length, CurrentGame);
            // The names of the tracks the file really has, in file order (the trim editor labels and keeps tracks by them).
            var entry = Library.Add(result.Path, result.DurationSeconds, result.SizeBytes, result.Game, result.AudioTracks);
            saved.TrySetResult(entry);
            Say($"Clip saved ({result.DurationSeconds:0}s)");
            Toasts.ShowClipSaved(entry);
            Archive.NotifyClipSaved();
            ClipSaved?.Invoke(entry);
            // The save copied the clip into managed buffers that are all garbage now: a background GC lets the next save reuse
            // that space instead of growing the process by another clip.
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: false);
        }
        catch (Exception ex)
        {
            Log.Error("Save clip", ex);
            Say("Clip failed: " + ex.Message);
            // The click has already played: without this a failed save looks like a saved clip.
            NotifyProblem("Clip not saved", ex.Message);
        }
        finally { saved.TrySetResult(null); _saving = 0; }
    }

    /// <summary>
    /// Shares the newest clip (the "share last clip" hotkey / tray item): uploads it, or copies its link if it already has one. With
    /// no Copium token it opens the share window for the file (see <see cref="ShareAsync"/>), except over the detected game, where it
    /// only says to open Clink.
    /// </summary>
    public async void ShareLast()
    {
        // "Clip, then share" (a Stream Deck multi-action, --clip then --share-last, two quick presses) means the clip being saved
        // right now: the library only gets it once the save is done, so wait for that save, however long it takes (a long replay
        // on a slow disk), and share what it saved, never the clip before it. A clip turned down before its save started (not
        // recording, or --clip starting Clink) shares nothing either: the two presses of a multi-action arrive well inside 3 s,
        // and a share-last pressed on its own still shares the newest clip.
        ClipEntry? entry;
        if (Volatile.Read(ref _saving) == 1 && _saveTask is { } saving)
        {
            entry = await saving;
            // ClipNow has already said why the clip was not saved (footer, and a toast / balloon away from the window).
            if (entry == null) { Log.Info("share-last: the clip being saved failed; nothing shared"); return; }
        }
        else if (DateTime.UtcNow - _clipRefusedAt < TimeSpan.FromSeconds(3))
        {
            // Same: ClipNow / the startup balloon has said why; the footer keeps that reason.
            Log.Info("share-last: the clip just pressed was not saved; nothing shared");
            return;
        }
        else entry = Library.Entries.FirstOrDefault(e => !IsRecordingEntry(e) && (e.IsShared || e.IsArchived || e.Exists));   // (a recording is hours long: not what "share last clip" means)
        bool ok = false, joined = false, windowOpened = false;
        if (entry == null) Say("No clips to share yet");
        else
        {
            // A second press during the same upload gets the same result: the press that started it reports a failure, once.
            joined = _uploads.TryGetValue(entry, out var running) && running.Announced;
            ok = await ShareAsync(entry, announce: true);
            windowOpened = LastShareOutcome == ShareOutcome.FileShareOpened;   // no Copium token: the share window is the answer, not a problem
        }
        // The hotkey, the tray item and --share-last have no UI of their own, and the window is usually in the tray, minimized or
        // behind the game: a refusal (or a failure) that only reached the footer would be invisible (NotifyProblem: a toast over
        // the game, a tray balloon otherwise).
        if (!ok && !joined && !windowOpened && LastNotice.Length > 0) NotifyProblem("Clip not uploaded", LastNotice, overGame: UserNotificationState.FullScreenInFront());
    }

    /// <summary>
    /// Why a clip may not go to Copium with this account, or null when it fits: "Over Copium's free limit (200 MB / 10 min)"
    /// without Copium+, "Over Copium Plus's limit (2 GB / 60 min)" with it (<see cref="Capabilities.CopiumLimits"/>). Copium would
    /// refuse it after a long upload; Share says so up front. A clip exactly at a limit passes. Pure.
    /// </summary>
    internal static string? ShareRefusalFor(long size, double duration, bool plus)
    {
        var (maxBytes, maxSeconds, label) = Capabilities.CopiumLimits(plus);
        return size > maxBytes || duration > maxSeconds ? "Over " + label : null;
    }

    /// <summary>The footer line for a clip <see cref="ShareRefusalFor"/> turned down: what the account takes, how big the clip is, what to do about it.</summary>
    internal static string ShareRefusalNotice(long size, double duration, bool plus)
    {
        var (maxBytes, maxSeconds, _) = Capabilities.CopiumLimits(plus);
        // Rounded up: a full 10:00 replay saves a moment over 10 min (it starts on a keyframe) and must not read "10:00".
        int secs = (int)Math.Ceiling(Math.Max(0, duration));
        string length = duration > 0 ? $" / {secs / 60}:{secs % 60:00}" : "";   // folder-scanned clips have no duration
        string limits = $"{Capabilities.FormatBytes(maxBytes)} / {maxSeconds / 60} min";
        string clip = $"this clip is {Capabilities.FormatBytes(size)}{length}";
        return plus
            ? $"Copium Plus accepts up to {limits}; {clip} - trim it first"
            : $"Copium free accepts up to {limits}; {clip} - trim it, or tick 'I have Copium+' if you do";
    }

    /// <summary>
    /// Shares a clip: copies its link when it has one, otherwise uploads it to Copium. With no Copium token there is nothing to
    /// upload to, so the share window opens with the file to drag into Discord, a browser or a folder instead
    /// (<see cref="LastShareOutcome"/> is then <see cref="ShareOutcome.FileShareOpened"/>, and the result is false without a
    /// <see cref="ShareError"/>). Returns true only when a link was copied or the clip was uploaded.
    /// </summary>
    /// <param name="announce">From the hotkey / tray / --share-last, which have no progress UI of their own: unless the window is in front, a toast says the upload started; and no window opens over a game.</param>
    public async Task<bool> ShareAsync(ClipEntry entry, IProgress<double>? progress = null, bool announce = false)
    {
        ShareRefusal = null;
        ShareError = null;
        LastShareOutcome = ShareOutcome.None;
        if (!string.IsNullOrEmpty(entry.CopiumLink))
        {
            CopyToClipboard(entry.CopiumLink);
            Say("Link copied");
            Toasts.ShowShared(entry, entry.CopiumLink);
            LastShareOutcome = ShareOutcome.LinkCopied;
            return true;
        }
        string? token = Settings.CopiumToken;
        if (string.IsNullOrWhiteSpace(token))
        {
            // Nothing to upload to: hand over the file instead. The hotkey path over the game opens no window (it would land on the
            // picture): it says where to go, and the caller shows the line over the game. "Over the game" is the detected game, and
            // also a full-screen application the shell reports (a Direct3D game Clink did not detect, another full-screen program).
            if (announce && (Hotkeys.GameInFront() || UserNotificationState.FullScreenInFront()))
            {
                ShareError = "No Copium token: open Clink to share the file";
                Say(ShareError);
                LastShareOutcome = ShareOutcome.Failed;
                return false;
            }
            if (!OpenShareWindow(entry))
            {
                ShareError = ShareWindowFailedText;
                Say(ShareError);
                LastShareOutcome = ShareOutcome.WindowFailed;
                return false;
            }
            Say("Drag the clip where you want to share it");
            LastShareOutcome = ShareOutcome.FileShareOpened;
            return false;
        }
        // Copium refuses clips over the account's limits (200 MB / 10 min free, 2 GB / 60 min with Copium+): say so now instead of uploading for a while and failing.
        if (ShareRefusalFor(entry.Size, entry.Duration, Settings.CopiumPlus) is string refusal)
        {
            ShareRefusal = refusal;
            Say(ShareRefusalNotice(entry.Size, entry.Duration, Settings.CopiumPlus));
            LastShareOutcome = ShareOutcome.Refused;
            return false;
        }
        // One upload per clip: the hotkey, the tray, the toast and the clip list can all ask for the same clip while it is going
        // up (nothing marks it shared until it is done). A second ask waits for the running upload and gets its result.
        if (_uploads.TryGetValue(entry, out var running))
        {
            Say("Already uploading this clip…");
            if (announce && !MainWindowInFront) Toasts.ShowUploading(entry, "Already uploading to Copium…");
            bool joinedOk = await running.Task;
            LastShareOutcome = joinedOk ? ShareOutcome.Uploaded : ShareOutcome.Failed;
            return joinedOk;
        }
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _uploads[entry] = (done.Task, announce);
        bool ok = false;
        try
        {
            Say("Uploading to Copium…");
            if (announce && !MainWindowInFront) Toasts.ShowUploading(entry);
            string title = (entry.Game ?? "Clip") + " " + entry.Created.ToString("yyyy-MM-dd HH:mm");
            string path = entry.Path;
            // Off the UI thread: opening an archived clip on an unreachable share blocks in the FileStream constructor for ~20 s.
            // The callers' Progress<T> were made on the UI thread and still report there.
            var up = await Task.Run(() => CopiumClient.UploadAsync(token, path, title, progress, _cts.Token));
            entry.CopiumLink = up.Link;
            entry.CopiumPageUrl = up.PageUrl;
            entry.CopiumId = up.Id;
            Library.Update(entry);
            CopyToClipboard(up.Link);
            Say("Uploaded, link copied");
            Toasts.ShowShared(entry, up.Link);
            ok = true;
        }
        catch (Exception ex)
        {
            Log.Error("Copium upload", ex);
            ShareRefusal = null;   // another clip's Share may have been refused while this one was going up
            ShareError = ex.Message;
            Say("Upload failed: " + ex.Message);
        }
        finally
        {
            _uploads.Remove(entry);
            LastShareOutcome = ok ? ShareOutcome.Uploaded : ShareOutcome.Failed;   // before the waiting asks continue, which read it
            done.SetResult(ok);
        }
        return ok;
    }

    private readonly List<ShareWindow> _shareWindows = new();   // the open share windows (UI thread), one per clip

    /// <summary>
    /// Opens the share window for a clip: its file to drag into Discord, a browser upload box or a folder, Copy file, Open folder.
    /// An open window for the same clip is brought forward instead of a second one. It has no owner (unlike the trim editor): the
    /// user minimizes or hides the main window to get at the drop target, and an owned window would go with it. It floats on top
    /// by itself and opens centred on the monitor under the pointer, except over a game (<see cref="OverGame"/>): there it opens
    /// without taking the focus, at the toasts' position on the captured monitor, and goes behind the game when that has the focus.
    /// UI thread; from another it runs there. False when the window could not be opened (logged).
    /// </summary>
    public bool OpenShareWindow(ClipEntry entry)
    {
        if (!_ui.CheckAccess()) return _ui.Invoke(() => OpenShareWindow(entry));
        try
        {
            bool overGame = OverGame;
            var open = _shareWindows.FirstOrDefault(w => string.Equals(w.Entry.Path, entry.Path, StringComparison.OrdinalIgnoreCase));
            if (open != null) { open.BringForward(activate: !overGame); return true; }
            var window = new ShareWindow(entry);
            _shareWindows.Add(window);
            window.Closed += (_, _) => _shareWindows.Remove(window);
            try
            {
                if (overGame) window.ShowOverGame(SelectedMonitor, Settings.ToastPosition);
                else window.Show();
            }
            catch { _shareWindows.Remove(window); throw; }
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("share window", ex);
            return false;
        }
    }

    /// <summary>
    /// The user is over a game that Clink detected: the game has the focus, or a toast over it was just clicked (the click made the
    /// toast the active window, so the game is no longer in front; and the toast asks without <c>announce</c>, so the hotkey path's
    /// "no window over the game" never applies to it). A window that opens then must neither take the focus from the game nor sit in
    /// the middle of its picture. With no game running (the main window, the trim editor, the desktop) it is false.
    /// </summary>
    private bool OverGame => CurrentGame != null && (Hotkeys.GameInFront() || ToastIsActive);

    private static bool ToastIsActive => System.Windows.Application.Current?.Windows.OfType<ToastWindow>().Any(t => t.IsActive) == true;

    /// <summary>The share window's "Set up Copium…": brings the main window forward and says where the token goes.</summary>
    public void ShowCopiumSetup()
    {
        Say("Add your token under Clips → Copium");
        ShowRequested?.Invoke();
    }

    /// <summary>Asks the window to open the trim editor on a clip. False when no window has subscribed to <see cref="TrimRequested"/>.</summary>
    public bool RequestTrim(ClipEntry entry)
    {
        var h = TrimRequested;
        if (h == null) return false;
        _ui.BeginInvoke(() => h(entry));
        return true;
    }

    public void CopyToClipboard(string text)
    {
        try { _ui.Invoke(() => Clipboard.SetDataObject(text, true)); }
        catch (Exception ex) { Log.Warn("clipboard: " + ex.Message); }
    }

    public void OpenFolder(string? file = null)
    {
        try
        {
            if (file != null && File.Exists(file)) Process.Start("explorer.exe", $"/select,\"{file}\"");
            else { Directory.CreateDirectory(Settings.ClipsFolder); Process.Start("explorer.exe", $"\"{Settings.ClipsFolder}\""); }
        }
        catch (Exception ex) { Say("Could not open folder: " + ex.Message); }
    }

    public void SaveSettings() => Settings.Save();

    public void SetRunAtStartup(bool on)
    {
        if (IsDev) return;   // a --dev instance must never touch the real Run entry (it would point the real startup at the dev build)
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (key == null) return;
            if (on) key.SetValue("Clink", $"\"{Environment.ProcessPath}\" --minimized");
            else key.DeleteValue("Clink", false);
        }
        catch (Exception ex) { Say("Startup setting failed: " + ex.Message); }
    }

    // ------------------------------------------------------------------ status dot (docs/SPEC-v2.md §7)

    /// <summary>Shows or hides the status dot per <see cref="Settings.StatusDot"/> / <see cref="Settings.StatusDotPosition"/> on the captured monitor. UI thread.</summary>
    public void ApplyStatusDot()
    {
        if (!_ui.CheckAccess()) { _ui.BeginInvoke(ApplyStatusDot); return; }
        try
        {
            if (!Settings.StatusDot) { _dot?.Hide(); return; }
            _dot ??= new StatusDotWindow();
            _dot.Apply(State, IsManualRecording);
            _dot.Place(SelectedMonitor, Settings.StatusDotPosition);
        }
        catch (Exception ex) { Log.Warn("status dot: " + ex.Message); }
    }

    // ------------------------------------------------------------------ tray

    /// <summary>The tray icon's name: "Clink", or "Clink (dev)" for a --dev instance, so the two icons can be told apart.</summary>
    private static string TrayName => IsDev ? "Clink (dev)" : "Clink";

    private void CreateTray()
    {
        try
        {
            var menu = new System.Windows.Forms.ContextMenuStrip();
            _statusItem = new System.Windows.Forms.ToolStripMenuItem("Starting…") { Enabled = false };
            menu.Items.Add(_statusItem);
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            _clipItem = new System.Windows.Forms.ToolStripMenuItem(ClipTrayText, null, (_, _) => _ui.BeginInvoke(() => ClipNow()));
            menu.Items.Add(_clipItem);
            _recordItem = new System.Windows.Forms.ToolStripMenuItem(RecordItemText, null, (_, _) => _ui.BeginInvoke(ToggleRecording));
            menu.Items.Add(_recordItem);
            menu.Items.Add("Share last clip", null, (_, _) => _ui.BeginInvoke(ShareLast));
            menu.Items.Add("Show Clink", null, (_, _) => ShowRequested?.Invoke());
            _pauseItem = new System.Windows.Forms.ToolStripMenuItem(IsPaused ? "Resume recording" : "Pause recording", null, (_, _) => TogglePause());
            menu.Items.Add(_pauseItem);
            menu.Items.Add("Open clips folder", null, (_, _) => OpenFolder());
            _updateItem = new System.Windows.Forms.ToolStripMenuItem("Update Clink", null, (_, _) => UpdateNow()) { Visible = false };
            menu.Items.Add(_updateItem);
            _importItem = new System.Windows.Forms.ToolStripMenuItem("Import settings…", null, (_, _) => _ui.BeginInvoke(AskToImport)) { Visible = false };
            menu.Items.Add(_importItem);
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add("Quit", null, (_, _) => Quit());
            _trayIcon = LoadTrayIcon();
            _tray = new System.Windows.Forms.NotifyIcon
            {
                Text = TrayName,
                Visible = true,
                ContextMenuStrip = menu,
                Icon = _trayIcon ?? System.Drawing.SystemIcons.Application,
            };
            _tray.MouseClick += (_, e) => { if (e.Button == System.Windows.Forms.MouseButtons.Left) ShowRequested?.Invoke(); };
        }
        catch (Exception ex) { Log.Warn("tray: " + ex.Message); }
    }

    /// <summary>
    /// The exe's icon frame that matches the tray's small-icon size at the taskbar's DPI (16 / 20 / 24 / 32 px at 100 / 125 / 150 / 200 %),
    /// so the shell shows a frame that was drawn for that size instead of shrinking the 32 px one (which is what
    /// Icon.ExtractAssociatedIcon returns). The caller owns the result; null if no icon could be read from the exe.
    /// </summary>
    private static System.Drawing.Icon? LoadTrayIcon()
    {
        string? exe = Environment.ProcessPath;
        if (exe == null) return null;
        try
        {
            int size = AppIcons.SmallIconSize();
            if (size > 0)
            {
                var icon = System.Drawing.Icon.ExtractIcon(exe, 0, size);
                if (icon != null) return icon;
            }
        }
        catch (Exception ex) { Log.Warn("tray icon size: " + ex.Message); }
        try { return System.Drawing.Icon.ExtractAssociatedIcon(exe); }
        catch (Exception ex) { Log.Warn("tray icon: " + ex.Message); return null; }
    }

    /// <summary>Swaps in the icon frame for the current DPI (after a display / scaling change) and frees the old one.</summary>
    private void RefreshTrayIcon()
    {
        if (_tray == null || _disposed) return;
        try
        {
            var icon = LoadTrayIcon();
            if (icon == null) return;
            if (_trayIcon != null && _trayIcon.Width == icon.Width) { icon.Dispose(); return; }   // same size: leave the icon alone
            var old = _trayIcon;
            _tray.Icon = icon;
            _trayIcon = icon;
            old?.Dispose();
        }
        catch (Exception ex) { Log.Warn("tray icon refresh: " + ex.Message); }
    }

    private void UpdateTray()
    {
        if (_tray == null) return;
        string status = StatusText;
        if (State == RecordingState.Error && Engine.Error != null) status = "Error: " + Engine.Error;
        string text = TrayName + " · " + status;
        if (text.Length > 120) text = text[..119] + "…";   // NotifyIcon.Text throws above 127 characters
        if (text != _trayText)
        {
            _trayText = text;
            try { _tray.Text = text; } catch { }
            if (_statusItem != null) _statusItem.Text = status;
        }
    }

    public void UpdateTrayHotkey() { if (_clipItem != null) _clipItem.Text = ClipTrayText; }

    public void ShowBalloon(string title, string text)
    {
        if (text.Length > 250) text = text[..249] + "…";   // Shell_NotifyIcon cuts balloon text at 256 characters
        try { _tray?.ShowBalloonTip(3000, title, text, System.Windows.Forms.ToolTipIcon.None); } catch { }
    }

    public void Quit()
    {
        _ui.BeginInvoke(() =>
        {
            // A throw while cleaning up must not leave a process with no tray icon running on.
            try { StopRecordingForExit(); Dispose(); }
            finally { System.Windows.Application.Current.Shutdown(); }
        });
    }

    public void Dispose()
    {
        if (_disposed) return;   // Quit disposes, then App.OnExit does again
        _disposed = true;
        Log.Info("Quitting");
        _cts.Cancel();
        _timer?.Stop();
        SettingsImport.PendingChanged -= OnImportPending;
        SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        try { Hotkeys.Uninstall(); } catch { }
        try { Archive.Stop(); } catch { }
        try { Toasts.CloseAll(); } catch { }
        try { _dot?.Close(); _dot = null; } catch { }
        try { Updater.Dispose(); } catch { }
        try { Engine.Dispose(); } catch { }
        AddFinishedRecordingOnExit();   // the recording Dispose finished (the 15 s wait): the exit paths that skipped StopRecordingForExit did not add it
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
        try { _trayIcon?.Dispose(); _trayIcon = null; } catch { }
        ReleaseSingleInstance();
    }
}
