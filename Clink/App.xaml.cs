using System.IO;
using System.Windows;
using System.Windows.Threading;
using Windows.Win32;
using Clink.Services;

namespace Clink;

public partial class App : System.Windows.Application
{
    private AppController? _controller;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        string[] args = e.Args;
        if (args.Length > 0 && args[0].Equals("--apply-update", StringComparison.OrdinalIgnoreCase))
        {
            // Clink.exe --apply-update <targetExePath> <pid>: we are the freshly downloaded exe replacing an installed one.
            // No log init of the normal path and no single-instance mutex here; the old process is still shutting down.
            int code;
            try { code = Updater.RunApplyUpdate(args); }
            catch (Exception ex) { Log.Error("[update] apply crashed", ex); code = 1; }
            Shutdown(code);
            return;
        }

        AppDomain.CurrentDomain.UnhandledException += (_, a) => Log.Error("Unhandled", (a.ExceptionObject as Exception) ?? new Exception(a.ExceptionObject?.ToString()));
        DispatcherUnhandledException += (_, a) => { Log.Error("UI exception", a.Exception); a.Handled = true; };
        TaskScheduler.UnobservedTaskException += (_, a) => { Log.Error("Task exception", a.Exception); a.SetObserved(); };

        if (args.Length > 0 && args[0].Equals("--unittest", StringComparison.OrdinalIgnoreCase))
        {
            // Clink.exe --unittest [nameFilter]: the in-exe tests (Clink/Tests); exit code 0 = every test passed. They are pure: a temp
            // folder and Settings.DirOverride stand in for %APPDATA%\Clink, and the single-instance mutex and pipe are never touched,
            // so this is safe next to a running Clink. They run on this (the UI) thread, one after the other.
            Log.Init(Path.Combine(Path.GetTempPath(), "clink-unittest"), Log.CliFile);
            AttachParentConsole();
            Tests.EditListEmulation.StartFromEnvironment(args);   // CLINK_EMULATE_ELST: only here, in --trim and in --probe (it checks the command line too)
            int failed;
            try { failed = Tests.TestRunner.Run(args); }
            catch (Exception ex) { Log.Error("[unittest] the runner crashed", ex); Console.WriteLine(ex); failed = 1; }
            Shutdown(failed == 0 ? 0 : 1);
            return;
        }

        // The command-line modes log to clink-cli.log: the running Clink holds clink.log open.
        if (args.Length > 0 && args[0].Equals("--selftest", StringComparison.OrdinalIgnoreCase))
        {
            Log.Init(Settings.Dir, Log.CliFile);
            AttachParentConsole();
            int code = 1;
            var t = new Thread(() => { code = SelfTest.Run(args); }) { IsBackground = false };
            t.SetApartmentState(ApartmentState.MTA);
            t.Start();
            t.Join();
            Shutdown(code);
            return;
        }
        if (args.Length > 1 && args[0].Equals("--probe", StringComparison.OrdinalIgnoreCase))
        {
            Log.Init(Settings.Dir, Log.CliFile);
            AttachParentConsole();
            Tests.EditListEmulation.StartFromEnvironment(args);
            int code = 0;
            var t = new Thread(() =>
            {
                try
                {
                    string report = Engine.MediaProbe.Report(args[1]);
                    Console.WriteLine(report);
                    Log.Info("[probe]\n" + report);
                    if (args.Skip(2).Any(a => a.Equals("--levels", StringComparison.OrdinalIgnoreCase)))
                    {
                        // Clink.exe --probe <file> --levels: how loud each audio track really is (RMS / peak of the decoded audio)
                        string levels = Engine.MediaProbe.Levels(args[1]);
                        Console.WriteLine(levels);
                        Log.Info("[probe]\n" + levels);
                    }
                    if (args.Length > 2 && !args[2].StartsWith("--", StringComparison.Ordinal))
                    {
                        long at = args.Length > 3 && double.TryParse(args[3], out double sec) ? (long)(sec * 10_000_000) : 0;
                        var frame = Engine.MediaProbe.DecodeFrame(args[1], at);
                        if (frame != null) { Engine.MediaProbe.SavePng(args[2], frame.Value.w, frame.Value.h, frame.Value.bgra); Log.Info($"[probe] frame saved to {args[2]}"); Console.WriteLine("frame saved to " + args[2]); }
                        else { Log.Warn("[probe] no frame decoded"); code = 4; }
                    }
                }
                catch (Exception ex) { Log.Error("[probe] failed", ex); Console.WriteLine(ex); code = 5; }
            });
            t.SetApartmentState(ApartmentState.MTA);
            t.Start();
            t.Join();
            Shutdown(code);
            return;
        }
        if (args.Length > 1 && args[0].Equals("--trim", StringComparison.OrdinalIgnoreCase))
        {
            // Clink.exe --trim <in> <out> <startSec> <endSec> [--gain Name=volume]... [--drop Name]...  (see Engine/TrimCli.cs)
            Log.Init(Settings.Dir, Log.CliFile);
            AttachParentConsole();
            Tests.EditListEmulation.StartFromEnvironment(args);
            int code = 0;
            var t = new Thread(() =>
            {
                try { Engine.TrimCli.Run(args); Console.WriteLine("ok"); }
                catch (Exception ex) { Log.Error("[trim] failed", ex); Console.WriteLine(ex); code = 5; }
            });
            t.SetApartmentState(ApartmentState.MTA);
            t.Start();
            t.Join();
            Shutdown(code);
            return;
        }

        try { RunNormal(args); }
        catch (Exception ex)
        {
            // An exception here would otherwise be swallowed by the DispatcherUnhandledException handler and leave a headless
            // process holding the single-instance mutex: every later start would forward to it and exit.
            if (Log.Path == null) Log.Init(Settings.Dir);
            Log.Error("Startup failed", ex);
            try { _controller?.Dispose(); } catch { }
            AppController.ReleaseSingleInstance();
            try { MessageBox.Show("Clink could not start: " + ex.Message + "\n\nDetails: " + (Log.Path ?? Settings.Dir), "Clink", MessageBoxButton.OK, MessageBoxImage.Error); } catch { }
            Shutdown(1);
        }
    }

    private void RunNormal(string[] args)
    {
        // "--dev [folder] [--dev-hotkeys]": a second Clink for testing next to the real one, with its own data folder, mutex and
        // pipe. Decided before the single-instance check, so it can never forward a command to (or wait on) the real Clink.
        // A mistake in a --dev switch is an error, not a normal start: a normal start would take the real Clink's mutex or forward to it.
        DevOptions? dev;
        try { dev = DevOptions.Parse(args); }
        catch (ArgumentException ex)
        {
            AttachParentConsole();
            Console.WriteLine("Clink: " + ex.Message);
            Shutdown(2);
            return;
        }
        if (dev != null)
        {
            Settings.DirOverride = dev.Folder;
            AppController.InstanceSuffix = dev.InstanceSuffix;
            AppController.DevHotkeys = dev.BindHotkeys;
            AppController.DevAcceptImport = dev.AcceptImport;
            AppController.DevToken = dev.AllowToken;
            AppController.DevRecord = dev.Record;
            SettingsImport.PackagesRoot = dev.PackagesRoot;   // a --dev instance looks for other copies' settings only where this says (tests)
            SettingsImport.ForeignGuard = s => dev.Isolate(s, freshStore: false);   // and compares them as they would be once loaded here
        }

        // A Clink that another one restarted into (the hidden --after-pid <pid>) waits for that one to be gone before it takes the mutex
        // and opens the log, at most 10 s: the older one may still be writing the files the restart has just changed.
        int? previous = AppController.AfterPid(args);
        bool previousGone = previous is not int previousPid || AppController.WaitForExit(previousPid, TimeSpan.FromSeconds(10));

        // Normal run: one instance; "--clip" / "--show" talk to it. A second instance only forwards its command and exits,
        // without touching the log (the running instance holds it).
        if (!AppController.AcquireSingleInstance(args)) { Shutdown(0); return; }
        Log.Init(Settings.Dir);
        if (previous != null) Log.Info($"restart: the Clink with pid {previous} {(previousGone ? "has ended" : "was still running after 10 s")}");

        // Is this Clink running inside a packaged app (the Claude desktop app), so that the AppData it reads and writes is that app's
        // private copy? A --dev instance keeps everything in its own folder: nothing to find out.
        if (dev == null) AppDataRedirect.Detect();

        var settings = Settings.Load();
        bool freshStore = !settings.LoadedFromFile;
        // A clip merge that an earlier import could not do is tried again at every start, before the clip index is read, whether or
        // not the settings still differ from any other copy's.
        SettingsImport.RetryPendingClipMerge();
        // A first start of a real Clink takes over the newest settings another copy saved inside a packaged app's private AppData
        // (before anything has read them, so no window or timer can save over them). Not from inside such an app, and not in a
        // --dev instance, which looks only where --packages-root says. An existing store is only ever offered a copy (AppController).
        if (SettingsImport.CanTakeOverAtStart(freshStore, dev != null) && SettingsImport.ImportNewest() is { } imported)
        {
            settings = Settings.Load();
            settings.ImportedFrom = imported;
        }
        if (dev != null)
        {
            // Whatever the dev folder's settings file says (a copied fixture, one taken over from another copy, a real Clink's), this
            // instance stays away from the real clips, archive, updates and account: on every start, and after an import.
            var isolated = dev.Isolate(settings, freshStore);
            if (isolated.Count > 0) Log.Info("dev: kept away from the real world: " + string.Join("; ", isolated));
            if (settings.ImportedFrom != null) settings.Save();   // an imported file may not need migrating, which would leave it as it came
        }
        var updater = new Updater(settings);
        updater.LogSource();

        // A newer exe was downloaded on an earlier run: hand over to it and let it replace us ("--no-update" skips this).
        // Only an exe that knows its update repository does this: the updates folder is shared by every Clink exe on the
        // machine, and a local/debug build must never be overwritten by a release download it did not make. A --dev instance
        // never installs anything.
        bool noUpdate = dev != null || args.Any(a => a.Equals("--no-update", StringComparison.OrdinalIgnoreCase));
        if (updater.Repo != null && settings.AutoUpdate && !noUpdate && updater.TryGetPendingUpdate(out string pendingExe, out Version pendingVersion))
        {
            Log.Info($"Installing downloaded update v{pendingVersion} from {pendingExe}");
            try
            {
                // The restarted exe gets this command line back, so an update at login (--minimized from the Run key) stays in the tray.
                Updater.LaunchApply(pendingExe, Environment.ProcessPath!, args.Where(a => !a.Equals("--no-update", StringComparison.OrdinalIgnoreCase)));
                AppController.ReleaseSingleInstance();
                Shutdown(0);
                return;
            }
            catch (Exception ex)
            {
                Log.Error("Could not start the update, continuing with this version", ex);
                updater.ReportInstallFailure(ex, pendingVersion, pendingExe);
            }
        }

        // The theme and accent the user chose, before any window exists: a window takes its brushes from the palette when it loads.
        UI.ThemeManager.Apply(settings.Theme, settings.Accent);

        _controller = new AppController(settings, updater);
        // Windows signing out / shutting down: finish a manual recording (up to ~4 s) before the process is ended, so its file is
        // complete. (Not final: another program may still cancel the session end.)
        SessionEnding += (_, _) => _controller?.StopRecordingForExit(final: false);
        var main = new MainWindow();
        if (dev != null) main.Title = "Clink (dev)";
        MainWindow = main;
        _controller.Start();
        // "--clip" / "--share-last" / "--trim-last" while Clink was not running: start in the tray and run the command,
        // instead of opening the settings window over the game and dropping the command.
        string? cmd = AppController.CommandFromArgs(args);
        bool show = args.Any(a => a.Equals("--show", StringComparison.OrdinalIgnoreCase));
        bool minimized = !show && (settings.StartMinimized || cmd != null || args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase)));
        if (!minimized) main.Show();
        Log.Info($"Clink v{updater.VersionDisplay} started" + (dev != null ? $" (--dev: data in {dev.Folder}, hotkeys {(dev.BindHotkeys ? "on" : "off")})" : ""));
        if (cmd != null) _controller.RunStartupCommand(cmd);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _controller?.StopRecordingForExit();   // the common exit finishes the file here, not in Dispose's 15 s wait
        _controller?.Dispose();
        base.OnExit(e);
    }

    private static void AttachParentConsole()
    {
        try
        {
            if (PInvoke.AttachConsole(PInvoke.ATTACH_PARENT_PROCESS))
            {
                var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
                Console.SetOut(stdout);
                Console.SetError(stdout);
                Console.WriteLine();
            }
        }
        catch { }
    }
}
