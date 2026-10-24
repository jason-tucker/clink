using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Clink.Audio;
using Clink.Native;
using Clink.Services;
using Clink.UI;

namespace Clink;

/// <summary>A combo entry: label + value; <see cref="Header"/> rows are section titles, <see cref="Disabled"/> rows are greyed (a codec this GPU lacks).</summary>
internal sealed record Choice(string Label, object Value, bool Header = false, bool Disabled = false) { public override string ToString() => Label; }

/// <summary>
/// The main window (docs/SPEC-v2.md §1, §10): resizable, settings tabs and the clips pane, which sits in a right column when the
/// window is ≥ 900 px wide and becomes the last tab, Library, when it is narrower. Capture holds, top to bottom, Capturing (monitor,
/// quality, size target, replay length with the one size/RAM readout under its slider, cursor) · Audio tracks (under a "set up
/// your audio" banner that goes away for good once the pointer has rested on the group and moved off, or an option in it
/// changes) · Recording · Hotkeys (the main hotkey, then shorter ones); Clips holds Copium and Storage; Alerts holds Toasts and
/// Status dot; App holds Appearance (theme and accent), Updates (with "run at startup") and Settings (where they are kept, and
/// the banners about a private copy of AppData and another copy's settings). Every control writes its setting on change and,
/// where the engine is affected, restarts it after a short debounce (<see cref="ScheduleRestart"/>). Every colour is a
/// DynamicResource or a resource reference, so the window follows a theme change while it is open.
/// </summary>
public partial class MainWindow : Window
{
    private const double WideWidth = 900;

    private readonly AppController _app = AppController.Instance;
    private readonly ObservableCollection<string> _customApps = new();
    private readonly DispatcherTimer _restartTimer, _saveTimer, _bannerTimer;
    private readonly Choice[] _bitratePresets = new[] { 4, 6, 8, 12, 16, 20, 25, 30, 40, 50, 60, 80, 100 }.Select(b => new Choice($"{b} Mbps", b)).ToArray();
    private bool _loading = true;
    private bool _filling;                              // a combo is being (re)populated: its SelectionChanged is not a user choice
    private bool _pickingRunningApp;                    // the "Add running app…" list was opened: a selection now is a pick, not a key on the closed box
    private bool _pickingSizeTarget;                    // the same for the size-target list: only a pick from it ticks "I have Copium+" for Copium Plus
    private readonly CopiumPlusTick _copiumPlusTick = new();
    private bool _hintShown;
    private bool _checkingUpdate;
    private bool _wide, _layoutApplied;
    private List<MonitorInfo>? _shownMonitors;          // the monitor list the map and label were built from (AppController replaces it on a display change)
    private DateTime _noticeUntil = DateTime.MinValue;   // keep a Say() notice visible this long before the stats line takes the footer back

    public MainWindow()
    {
        InitializeComponent();
        AppIcons.Attach(this);   // the taskbar button's own icon size, not WPF's default 32 px frame shrunk to it
        _restartTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _restartTimer.Tick += (_, _) => { _restartTimer.Stop(); _app.RequestEngineRestart(); };   // postponed while a recording runs
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); if (!_loading) _app.Settings.Save(); };
        _bannerTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _bannerTimer.Tick += (_, _) => { _bannerTimer.Stop(); MarkAudioSetupSeen(); };
        CustomAppsList.ItemsSource = _customApps;
        PreviewMouseWheel += ScrollPastClosedCombo;

        var s = _app.Settings;
        RestoreWindowBounds(s);
        Loaded += (_, _) => { ApplyLayout(); Populate(); };
        Closing += (_, e) => { e.Cancel = true; HideToTray(); };
        LocationChanged += (_, _) => { if (!_loading && IsVisible && WindowState == WindowState.Normal) { s.WindowX = Left; s.WindowY = Top; ScheduleSave(); } };
        SizeChanged += (_, _) =>
        {
            ApplyLayout();
            if (!_loading && IsVisible && WindowState == WindowState.Normal) { s.WindowWidth = ActualWidth; s.WindowHeight = ActualHeight; ScheduleSave(); }
        };

        _app.StateChanged += RefreshStatus;
        // Long notices (a Share refusal, an update problem) stay up long enough to read; the footer's tooltip has the full text.
        _app.Notice += msg => Dispatcher.BeginInvoke(() => { FooterText.Text = msg; _noticeUntil = DateTime.UtcNow.AddSeconds(Math.Clamp(msg.Length / 15.0, 4, 10)); });
        _app.ClipSaved += _ => Dispatcher.BeginInvoke(() => Clips.Refresh());
        _app.ShowRequested += () => Dispatcher.BeginInvoke(ShowFromTray);
        _app.TrimLastRequested += () => Dispatcher.BeginInvoke(async () =>
        {
            // File.Exists on an archived entry is a share round trip that can stall for the SMB timeout: look on a worker.
            var entries = _app.Library.Entries;
            ClipEntry? newest = null;
            // (not a recording: hours long, and the trimmer loads its selection into memory; the path test comes first, it costs nothing)
            try { newest = await Task.Run(() => entries.FirstOrDefault(e => !_app.IsRecordingEntry(e) && e.Exists)); }
            catch (Exception ex) { Log.Warn("trim last: " + ex.Message); }
            if (newest != null) OpenTrim(newest); else _app.Say("No clips to trim yet");
        });
        _app.TrimRequested += entry => Dispatcher.BeginInvoke(() => OpenTrim(entry));
        _app.Library.Changed += () => Dispatcher.BeginInvoke(() => Clips.Refresh());
        _app.UpdateStateChanged += () => Dispatcher.BeginInvoke(RefreshUpdateState);
        _app.Archive.Changed += () => Dispatcher.BeginInvoke(RefreshArchiveStatus);
        Clips.TrimRequested += OpenTrim;
        Clips.PlayRequested += entry => OpenClip(entry, trim: false);
        HotkeyEditor.Changed += HotkeysChanged;
        ReplaySlider.ValueChanged += v => { if (!_loading) UpdateEstimates(v); };
        ReplaySlider.ValueCommitted += ReplayLengthCommitted;
    }

    // ------------------------------------------------------------------ window bounds / layout

    /// <summary>
    /// A focused, closed ComboBox turns each wheel notch into the next / previous item and swallows it, so once a combo has been
    /// used (it keeps focus after a pick) the Capture tab stops scrolling under the pointer and the setting silently changes
    /// instead; most of them restart the engine and drop the replay. The pane scrolls instead. An open dropdown is its own
    /// popup and scrolls its list as usual; keyboard focus stays where it is (arrow keys still step a focused combo).
    /// </summary>
    private void ScrollPastClosedCombo(object sender, MouseWheelEventArgs e)
    {
        for (var d = e.OriginalSource as DependencyObject; d != null; d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
        {
            if (d is not ComboBox combo) continue;
            if (combo.IsDropDownOpen) return;
            e.Handled = true;
            for (DependencyObject? p = combo; p != null; p = VisualTreeHelper.GetParent(p))
                if (p is ScrollViewer sv) { sv.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = MouseWheelEvent }); break; }
            return;
        }
    }

    private void RestoreWindowBounds(Settings s)
    {
        if (s.WindowWidth is double ww && ww >= MinWidth) Width = ww;
        if (s.WindowHeight is double wh && wh >= MinHeight) Height = wh;
        var (areas, area) = WorkAreas();   // area: the monitor under the mouse pointer, where CenterScreen opens the window
        if (s.WindowX is double wx && s.WindowY is double wy && double.IsFinite(wx) && double.IsFinite(wy))
        {
            // Only where the header (the one place this borderless window can be dragged from) lands on a monitor's work area:
            // the virtual-screen box has corners no monitor covers, and a monitor may have gone away since. Of the monitors it
            // reaches, the one holding most of it counts (the AllScreens order means nothing).
            var header = new Rect(wx, wy, Width, 44);
            double most = 0;
            foreach (var wa in areas)
            {
                var hit = Rect.Intersect(header, wa);
                if (hit.IsEmpty || hit.Width < 120 || hit.Height < 22 || hit.Width * hit.Height <= most) continue;
                most = hit.Width * hit.Height;
                area = wa;
            }
            if (most > 0)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = wx; Top = wy;
            }
        }
        // Never bigger than that work area (a 1080p laptop at 150 % has ~690 DIP of height and MinHeight is 700), so all of it
        // can be dragged onto there.
        if (area.Width < 1 || area.Height < 1) return;
        MinWidth = Math.Min(MinWidth, area.Width);
        MinHeight = Math.Min(MinHeight, area.Height);
        Width = Math.Min(Width, area.Width);
        Height = Math.Min(Height, area.Height);
        if (WindowStartupLocation == WindowStartupLocation.Manual)
        {
            // Moved only as far as it takes to keep the header reachable on that work area: all of its height and 120 DIP of its
            // width (also after a narrower Width). A window left reaching onto the next monitor or past the bottom stays put.
            double grab = Math.Min(120, Width);
            Left = Math.Max(area.Left + grab - Width, Math.Min(Left, area.Right - grab));
            Top = Math.Max(area.Top, Math.Min(Top, area.Bottom - 44));
        }
    }

    /// <summary>
    /// Every monitor's work area, and the one under the mouse pointer (CenterScreen's), in the DIPs Left / Top use before the
    /// window exists (pixels at the system DPI).
    /// </summary>
    private static (List<Rect> all, Rect atPointer) WorkAreas()
    {
        var all = new List<Rect>();
        Rect atPointer = SystemParameters.WorkArea;
        try
        {
            double scale = System.Windows.Forms.Screen.PrimaryScreen is { } primary && SystemParameters.PrimaryScreenWidth > 0
                ? primary.Bounds.Width / SystemParameters.PrimaryScreenWidth : 1;
            Rect Dip(System.Drawing.Rectangle r) => new(r.X / scale, r.Y / scale, r.Width / scale, r.Height / scale);
            foreach (var sc in System.Windows.Forms.Screen.AllScreens) all.Add(Dip(sc.WorkingArea));
            atPointer = Dip(System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position).WorkingArea);
        }
        catch (Exception ex) { Log.Warn("work areas: " + ex.Message); }
        return (all, atPointer);
    }

    /// <summary>Wide: settings tabs left, the clips pane in a right column. Narrow: the clips pane is the Library tab, after the settings tabs.</summary>
    private void ApplyLayout()
    {
        bool wide = ActualWidth >= WideWidth;
        if (_layoutApplied && wide == _wide) return;
        _wide = wide; _layoutApplied = true;
        if (wide)
        {
            ClipsTab.Content = null;
            if (ReferenceEquals(Tabs.SelectedItem, ClipsTab)) Tabs.SelectedIndex = 0;
            ClipsTab.Visibility = Visibility.Collapsed;
            RightHost.Child = Clips;
            Clips.Margin = new Thickness(0);
            RightHost.Visibility = Visibility.Visible;
            SplitCol.Width = new GridLength(16);
            RightCol.Width = new GridLength(1.15, GridUnitType.Star);
        }
        else
        {
            RightHost.Child = null;
            RightHost.Visibility = Visibility.Collapsed;
            SplitCol.Width = new GridLength(0);
            RightCol.Width = new GridLength(0);
            Clips.Margin = new Thickness(0, 8, 0, 0);
            ClipsTab.Content = Clips;
            ClipsTab.Visibility = Visibility.Visible;
        }
    }

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    // ------------------------------------------------------------------ populate

    private void Populate()
    {
        var s = _app.Settings;
        _shownMonitors = _app.Monitors;
        MonitorMap.SetMonitors(_app.Monitors, _app.SelectedMonitor?.DeviceName);
        MonitorMap.Selected += m => { s.MonitorDevice = m.DeviceName; s.Save(); UpdateMonitorLabel(); FillCodecs(); UpdateEstimates(); _app.ApplyStatusDot(); ScheduleRestart(); };
        UpdateMonitorLabel();

        Fill(ResolutionCombo, new[] { new Choice("Native", "native"), new Choice("1440p", "1440p"), new Choice("1080p", "1080p"), new Choice("720p", "720p") }, s.Resolution);
        Fill(FpsCombo, new[] { new Choice("30", 30), new Choice("60", 60), new Choice("120", 120) }, s.Fps);
        Fill(BitrateCombo, _bitratePresets, s.BitrateMbps);
        FillCodecs();
        Fill(SizeTargetCombo, Capabilities.SizeTargets.Select(t => new Choice(t.Name, t.Id)).ToArray(), s.SizeTarget);
        Fill(ModeCombo, new[] { new Choice("Only while a game or custom app is running", "game"), new Choice("Always (whenever Clink runs)", "always") }, s.RecordingMode);
        _customApps.Clear();
        foreach (var a in (s.CustomApps ?? new()).Select(GameProcessTracker.NormalizeName).Where(a => a.Length > 0 && !IsAuto(a)).Distinct(StringComparer.OrdinalIgnoreCase)) _customApps.Add(a);
        FillRunningApps();

        HotkeyEditor.Load(s.Hotkeys, s.BufferSeconds);
        ReplaySlider.Value = Math.Clamp(s.BufferSeconds, ReplayLengthSlider.Minimum, ReplayLengthSlider.Maximum);
        ClickSoundCheck.IsChecked = s.ClickSound;
        CursorCheck.IsChecked = s.CaptureCursor;
        StartupCheck.IsChecked = s.RunAtStartup;
        TrackDesktopCheck.IsChecked = s.TrackDesktop;
        TrackGameCheck.IsChecked = s.TrackGame;
        TrackMicCheck.IsChecked = s.TrackMic;
        TrackVoiceCheck.IsChecked = s.TrackVoice;

        FillDesktopCombo(s.AllDeviceId);
        FillGameCombo(s.GameProcess);
        FillDeviceCombo(MicCombo, true, s.MicDeviceId);
        FillVoiceCombo(s.VoiceApp);
        UpdateAudioEnabled();
        AudioSetupBanner.Visibility = s.AudioSetupSeen ? Visibility.Collapsed : Visibility.Visible;

        if (!string.IsNullOrEmpty(s.CopiumToken)) { TokenBox.Password = s.CopiumToken; CopiumStatus.Text = "Token saved"; CopiumStatus.SetResourceReference(TextBlock.ForegroundProperty, "Good"); }
        TokenSaveButton.IsEnabled = false;
        CopiumPlusCheck.IsChecked = s.CopiumPlus;

        ClipsFolderBox.Text = s.ClipsFolder;
        ArchiveCheck.IsChecked = s.ArchiveEnabled;
        ArchivePathBox.Text = s.ArchivePath ?? "";
        ArchiveDaysBox.Text = s.ArchiveAfterDays.ToString(CultureInfo.InvariantCulture);
        ArchiveGbBox.Text = s.ArchiveWhenLocalExceedsGB.ToString("0.##", CultureInfo.InvariantCulture);
        UpdateArchiveEnabled();
        RefreshArchiveStatus();

        Fill(ToastPositionCombo, new[]
        {
            new Choice("Top left", "top-left"), new Choice("Top", "top"), new Choice("Top right", "top-right"),
            new Choice("Left", "left"), new Choice("Right", "right"),
            new Choice("Bottom left", "bottom-left"), new Choice("Bottom", "bottom"), new Choice("Bottom right", "bottom-right"),
        }, s.ToastPosition);
        ToastClipCheck.IsChecked = s.ToastClip.Enabled; ToastClipSlider.Value = Math.Clamp(s.ToastClip.Seconds, 1, 30);
        ToastSharedCheck.IsChecked = s.ToastShared.Enabled; ToastSharedSlider.Value = Math.Clamp(s.ToastShared.Seconds, 1, 30);
        ToastRecordingCheck.IsChecked = s.ToastRecording.Enabled; ToastRecordingSlider.Value = Math.Clamp(s.ToastRecording.Seconds, 1, 30);
        UpdateToastLabels();
        DotCheck.IsChecked = s.StatusDot;
        Fill(DotPositionCombo, new[] { new Choice("Top left", "top-left"), new Choice("Top right", "top-right"), new Choice("Bottom left", "bottom-left"), new Choice("Bottom right", "bottom-right") }, s.StatusDotPosition);
        DotPositionCombo.IsEnabled = s.StatusDot;

        AutoUpdateCheck.IsChecked = s.AutoUpdate;
        VersionText.Text = "v" + _app.Updater.VersionDisplay;
        UpdateVersionText.Text = "Clink v" + _app.Updater.VersionDisplay;
        VersionText.ToolTip = _app.Updater.Repo is string repo
            ? $"Updates come from github.com/{repo}{(_app.Updater.RepoOverridden ? " (UpdateRepo in settings.json)" : "")} · click to check now"
            : NoUpdatesText;
        RefreshUpdateState();
        SettingsDirText.Text = "Saved in " + Settings.Dir;
        RefreshSettingsBanners();
        SettingsImport.PendingChanged -= OnImportPendingChanged;
        SettingsImport.PendingChanged += OnImportPendingChanged;
        BuildAppearanceChoices();

        UpdateEstimates();
        Clips.Refresh();
        RefreshStatus();
        _loading = false;
    }

    private static void Fill(ComboBox combo, Choice[] choices, object current)
    {
        combo.ItemsSource = choices;
        combo.SelectedItem = choices.FirstOrDefault(c => !c.Header && Same(c.Value, current)) ?? choices.FirstOrDefault(c => !c.Header && !c.Disabled) ?? choices[0];
    }

    // string values (device ids, process names) compare case-insensitively so a hand-edited settings.json still matches
    private static bool Same(object a, object b) => a is string sa && b is string sb ? string.Equals(sa, sb, StringComparison.OrdinalIgnoreCase) : Equals(a, b);

    private static T? Selected<T>(ComboBox combo) => combo.SelectedItem is Choice c && c.Value is T v ? v : default;

    private void Refill(ComboBox combo, List<Choice> items, object current)
    {
        bool was = _filling;
        _filling = true;
        try { Fill(combo, items.ToArray(), current); }
        finally { _filling = was; }
    }

    // ------------------------------------------------------------------ capture: codecs, size estimates & targets (§3, §4)

    private void FillCodecs()
    {
        var items = Capabilities.Codecs
            .Select(c => new Choice(c.Supported ? c.Name + (c.Experimental ? " (experimental)" : "") : $"{c.Name} — {c.Reason}", c.Id, Disabled: !c.Supported))
            .ToList();
        Refill(CodecCombo, items, _app.Settings.Codec);
        UpdateCodecDescription();
    }

    private void UpdateCodecDescription()
    {
        string id = Selected<string>(CodecCombo) ?? _app.Settings.Codec;
        var info = Capabilities.Codec(id);
        if (info == null) { CodecDescription.Text = ""; return; }
        if (!Capabilities.Codecs.Any(c => c.Supported))
        {
            CodecDescription.Text = "No hardware video encoder found on this GPU – update the GPU driver; recording is not possible.";
            return;
        }
        var mon = _app.SelectedMonitor;
        var (w, h) = mon != null ? AppController.OutputSize(mon, _app.Settings.Resolution) : (0u, 0u);
        var fallback = Capabilities.Codec(Capabilities.EffectiveCodec(id, w, h));
        if (info.Supported && fallback != null && fallback.Id != info.Id)
            CodecDescription.Text = $"{info.Name} cannot encode {w}×{h}; recording uses {fallback.Name} instead. {info.Name} – {info.Description}";
        else if (info.Supported)
            CodecDescription.Text = $"{info.Name} – {info.Description}{(info.EncoderName != null ? $"  ·  {info.EncoderName.Replace(" Encoder MFT", "")}" : "")}";   // "NVIDIA H.264", as in the footer
        else
            CodecDescription.Text = $"{info.Name} is {info.Reason}; recording uses {fallback?.Name ?? "H.264"} instead. {info.Name} – {info.Description}";
    }

    private int AudioTrackCount() => _app.AudioTrackCount;   // the mix is always track 1; the same count the engine's bitrate is resolved with

    /// <summary>
    /// The readout under the replay-length slider in the Capturing card (§10, the only place size and RAM are shown): per-minute / per-clip sizes
    /// (<see cref="EstimateText"/>), the buffer's RAM (<see cref="RamText"/>, Danger over half of it) and the size-target note
    /// (<see cref="SizeNote"/>, Warn when no bitrate can make the clip fit); plus the bitrate combo's tooltip and the slider's RAM
    /// limit. Called on every resolution / FPS / bitrate / codec / size-target / audio-track / length change.
    /// </summary>
    private void UpdateEstimates(int? previewSeconds = null)
    {
        var s = _app.Settings;
        int seconds = Math.Clamp(previewSeconds ?? s.BufferSeconds, 1, 3600);
        int tracks = AudioTrackCount();
        var (mbps, note) = Capabilities.ResolveBitrate(s, tracks, seconds);
        bool targeted = Capabilities.SizeTargetBytes(s.SizeTarget) > 0;

        bool was = _filling;
        _filling = true;
        try
        {
            // The chosen bitrate stays editable; a size target only lowers the encode bitrate when a clip would not fit.
            if (!ReferenceEquals(BitrateCombo.ItemsSource, _bitratePresets)) Fill(BitrateCombo, _bitratePresets, s.BitrateMbps);
            BitrateCombo.IsEnabled = true;
            BitrateCombo.ToolTip = targeted && mbps < s.BitrateMbps
                ? $"Encoding at {mbps:0.#} Mbps so a {Capabilities.FormatDuration(Capabilities.FitSeconds(s.SizeTarget, seconds))} clip fits the size target; set the target to None to use the full {s.BitrateMbps} Mbps"
                : null;
        }
        finally { _filling = was; }

        SizeNote.Text = note ?? "";
        SizeNote.Visibility = note != null ? Visibility.Visible : Visibility.Collapsed;
        // Warn colour when no bitrate can make the clip fit, or when the replay reaches the target's length limit (Copium's 10 min,
        // Copium Plus's 60: no bitrate fixes that either).
        SizeNote.SetResourceReference(TextBlock.ForegroundProperty, Capabilities.IsWarningNote(note) ? "Warn" : "Muted");
        EstimateText.Text = Capabilities.DescribeEstimate(mbps, tracks, seconds);

        var (ramText, tooMuch) = Capabilities.DescribeRing(mbps, seconds);
        RamText.Text = ramText;
        RamText.SetResourceReference(TextBlock.ForegroundProperty, tooMuch ? "Danger" : "Muted");
        // Longer than the machine's RAM can hold at all cannot be allocated: the slider refuses it (the engine clamps to half anyway).
        long ram = Capabilities.PhysicalRamBytes();
        ReplaySlider.Limit = ram > 0 ? Math.Max(ReplayLengthSlider.Minimum, Capabilities.RingSecondsFor(mbps, ram)) : ReplayLengthSlider.Maximum;
    }

    private void SizeTargetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _filling) return;
        var s = _app.Settings;
        s.SizeTarget = Selected<string>(SizeTargetCombo) ?? SettingsDefaults.SizeTarget;
        // Picking the Copium Plus target says the account has Copium+, so Share checks Plus's limits: "I have Copium+" is ticked for
        // the user, but only for a pick from the open list. Stepping onto it with the arrow keys on the closed box (on the way to
        // another target) must not change the account setting: SizeTargetCombo_FocusWithinChanged ticks it if the user stops there.
        if (_copiumPlusTick.Selected(s.SizeTarget, _pickingSizeTarget, s.CopiumPlus)) TickCopiumPlus();
        s.Save();
        UpdateEstimates();
        ScheduleRestart();
    }

    private void SizeTargetCombo_DropDownOpened(object sender, EventArgs e) => _pickingSizeTarget = true;

    // Cleared after the events of the close: Enter in the list closes it first and selects the highlighted target after that.
    private void SizeTargetCombo_DropDownClosed(object sender, EventArgs e) => Dispatcher.BeginInvoke(() => _pickingSizeTarget = false);

    // The box lost the keyboard focus (to another control, another tab, another window): a user who stopped on Copium Plus with the
    // keys meant it.
    private void SizeTargetCombo_FocusWithinChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not false || _loading) return;
        var s = _app.Settings;
        if (!_copiumPlusTick.FocusLeft(s.SizeTarget, s.CopiumPlus)) return;
        TickCopiumPlus();
        s.Save();
    }

    /// <summary>Ticks "I have Copium+" for the Copium Plus size target, without CopiumPlusChanged running for it, and says so in the footer. The caller saves.</summary>
    private void TickCopiumPlus()
    {
        _app.Settings.CopiumPlus = true;
        bool was = _filling;
        _filling = true;
        try { CopiumPlusCheck.IsChecked = true; }
        finally { _filling = was; }
        _app.Say("Copium Plus size target: ticked I have Copium+");
    }

    // ------------------------------------------------------------------ capture: recording mode & custom apps (§5)

    private void ModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _filling) return;
        _app.Settings.RecordingMode = Selected<string>(ModeCombo) ?? SettingsDefaults.RecordingMode;
        _app.Settings.Save();
        RefreshStatus();
        // The mode is not part of the engine config: a running engine keeps its replay buffer (with no game, game-only mode stops
        // it after the usual 5 s grace); a stopped one is started now if the new mode wants it.
        if (!_app.Engine.IsRunning) ScheduleRestart();
    }

    private void FillRunningApps()
    {
        var items = new List<Choice> { new("Add running app…", "", true) };
        var apps = RunningApps().Where(c => !_customApps.Contains(c.Name, StringComparer.OrdinalIgnoreCase)).ToList();
        items.AddRange(apps.Select(c => new Choice(AppLabel(c), c.Name)));
        if (apps.Count == 0) items.Add(new Choice("No other windowed apps running", "", true));
        bool was = _filling;
        _filling = true;
        try { RunningAppsCombo.ItemsSource = items; RunningAppsCombo.SelectedIndex = 0; }
        finally { _filling = was; }
    }

    private void RunningAppsCombo_DropDownOpened(object sender, EventArgs e) { _pickingRunningApp = true; FillRunningApps(); }

    // Cleared after the events of the close: Enter in the list closes it first and selects the highlighted app after that.
    private void RunningAppsCombo_DropDownClosed(object sender, EventArgs e) => Dispatcher.BeginInvoke(() => _pickingRunningApp = false);

    private void RunningAppsCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _filling) return;
        // Only a pick from the open list adds an app: arrow / letter keys on the closed box step through it without asking.
        if (_pickingRunningApp && Selected<string>(RunningAppsCombo) is { Length: > 0 } name) AddCustomApp(name);
        Dispatcher.BeginInvoke(FillRunningApps);   // back to the placeholder once the selection event has finished
    }

    private void CustomAppBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        AddApp_Click(sender, e);
        e.Handled = true;
    }

    private void AddApp_Click(object sender, RoutedEventArgs e)
    {
        string name = CustomAppBox.Text;
        if (string.IsNullOrWhiteSpace(name)) return;
        AddCustomApp(name);
        CustomAppBox.Text = "";
    }

    private void AddCustomApp(string raw)
    {
        string name = GameProcessTracker.NormalizeName(Path.GetFileName(raw.Trim().Trim('"')));
        if (name.Length == 0 || IsAuto(name)) return;
        if (!_customApps.Contains(name, StringComparer.OrdinalIgnoreCase)) _customApps.Add(name);
        SaveCustomApps();
    }

    private void RemoveApp_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is string name) { _customApps.Remove(name); SaveCustomApps(); }
    }

    private void SaveCustomApps()
    {
        if (_loading) return;
        _app.Settings.CustomApps = _customApps.ToList();
        _app.Settings.Save();   // the 1 s detection poll picks the list up on its next pass
    }

    // ------------------------------------------------------------------ audio sources (unchanged from v1)

    private static bool IsAuto(string? name) => GameProcessTracker.IsAuto(name);

    /// <summary>
    /// All audio / Mic: the default endpoint or any active one. A saved device that is unplugged or disabled right now stays
    /// selected (by name when Windows still knows it) rather than silently showing "Default", so the picker tells the truth
    /// about what the engine is configured with and the id survives until the user picks something else.
    /// </summary>
    private void FillDeviceCombo(ComboBox combo, bool capture, string? current)
    {
        var items = new List<Choice> { new(capture ? "Default microphone" : "Default output", "") };
        List<AudioDeviceInfo> devices;
        try { devices = WasapiSource.ListDevices(capture, includeInactive: true); } catch { devices = new(); }
        items.AddRange(devices.Where(d => d.Problem == null).Select(d => new Choice(d.Name, d.Id)));
        string id = current ?? "";
        if (id.Length > 0 && !items.Any(i => Same(i.Value, id)))
        {
            var known = devices.FirstOrDefault(d => Same(d.Id, id));
            items.Add(new Choice(known != null ? $"{known.Name} ({known.Problem})" : "Saved device (not connected)", id));
        }
        Refill(combo, items, id);
    }

    private static List<GameProcessTracker.Candidate> RunningApps()
    {
        try { return GameProcessTracker.ListCandidates(); } catch { return new(); }
    }

    private static string AppLabel(GameProcessTracker.Candidate c)
    {
        string title = c.Title.Trim();
        if (title.Length > 48) title = title[..47] + "…";
        return title.Length == 0 ? c.Name : $"{c.Name} — {title}";
    }

    /// <summary>Game track: auto-detect or a running app; a configured name that is not running stays selectable so it is not lost.</summary>
    private void FillGameCombo(string current)
    {
        current = GameProcessTracker.NormalizeName(current);   // "auto" in any case, ".exe" dropped: matches the way the engine reads it
        // Short enough to fit the closed box at the window's width, like the Voice picker's; the tooltip explains the detection.
        var items = new List<Choice> { new("Auto (fullscreen app)", "auto") };
        var apps = RunningApps();
        if (apps.Count > 0) items.Add(new Choice("Running apps", "", true));
        items.AddRange(apps.Select(c => new Choice(AppLabel(c), c.Name)));
        AddDeviceChoices(items, current);
        if (!IsAuto(current) && !IsDeviceValue(current) && !items.Any(i => Same(i.Value, current))) items.Add(new Choice($"{current} (not running)", current));
        Refill(GameCombo, items, current);
    }

    /// <summary>Voice track: auto (first known voice app found), one of the known names, or any other running app.</summary>
    private void FillVoiceCombo(string current)
    {
        current = GameProcessTracker.NormalizeName(current);
        // Short enough to fit the closed box at the window's width; the full list is in the combo's tooltip.
        var items = new List<Choice> { new("Auto (Discord, TeamSpeak, …)", "auto") };
        items.AddRange(GameProcessTracker.VoiceAppNames.Select(n => new Choice(n, n)));
        var others = RunningApps().Where(c => !items.Any(i => Same(i.Value, c.Name))).ToList();
        if (others.Count > 0) items.Add(new Choice("Other running apps", "", true));
        items.AddRange(others.Select(c => new Choice(AppLabel(c), c.Name)));
        AddDeviceChoices(items, current);
        if (!IsAuto(current) && !IsDeviceValue(current) && !items.Any(i => Same(i.Value, current))) items.Add(new Choice($"{current} (not running)", current));
        Refill(VoiceCombo, items, current);
    }

    // The device and process lists are refreshed every time a dropdown opens (headsets get plugged in, games get launched while
    // the window sits in the tray); the current selection is always kept (see FillDeviceCombo / FillGameCombo).
    private void AllDeviceCombo_DropDownOpened(object sender, EventArgs e) => FillDesktopCombo(Selected<string>(AllDeviceCombo) ?? _app.Settings.AllDeviceId);

    /// <summary>
    /// Desktop track: the default output's loopback, any output's loopback ("out:&lt;id&gt;") or a capture device ("in:&lt;id&gt;":
    /// a Wave Link mix, a virtual cable), like the Game and Voice pickers. A bare endpoint id from an older settings file is
    /// an output loopback and keeps working; a saved device that is not connected stays selected rather than silently
    /// becoming "Default output".
    /// </summary>
    private void FillDesktopCombo(string? current)
    {
        string id = current ?? "";
        var items = new List<Choice> { new("Default output (what you hear)", "") };
        AddDeviceChoices(items, id);
        if (id.Length > 0 && !items.Any(i => Same(i.Value, id)))
        {
            List<AudioDeviceInfo> outputs;
            try { outputs = WasapiSource.ListDevices(false, includeInactive: true); } catch { outputs = new(); }
            var known = outputs.FirstOrDefault(d => Same(d.Id, id));   // v1 value: a bare render endpoint id
            items.Add(new Choice(known != null ? "Output: " + known.Name + (known.Problem != null ? $" ({known.Problem})" : "") : "Saved device (not connected)", id));
        }
        Refill(AllDeviceCombo, items, id);
    }

    private void GameCombo_DropDownOpened(object sender, EventArgs e) => FillGameCombo(Selected<string>(GameCombo) ?? _app.Settings.GameProcess);

    private void MicCombo_DropDownOpened(object sender, EventArgs e) => FillDeviceCombo(MicCombo, true, Selected<string>(MicCombo) ?? _app.Settings.MicDeviceId);

    private void VoiceCombo_DropDownOpened(object sender, EventArgs e) => FillVoiceCombo(Selected<string>(VoiceCombo) ?? _app.Settings.VoiceApp);

    private void UpdateAudioEnabled()
    {
        // Game, mic and voice are always captured for the mix, so their pickers stay live; only the optional Desktop track's does not.
        if (AllDeviceCombo == null || TrackDesktopCheck == null) return;
        AllDeviceCombo.IsEnabled = TrackDesktopCheck.IsChecked == true;
    }

    private static bool IsDeviceValue(string? v) =>
        v != null && (v.StartsWith("in:", StringComparison.OrdinalIgnoreCase) || v.StartsWith("out:", StringComparison.OrdinalIgnoreCase));

    /// <summary>Audio devices as alternative sources for the game / voice tracks: capture endpoints (virtual inputs such as Wave Link mixes) and output loopbacks.</summary>
    private void AddDeviceChoices(List<Choice> items, string current)
    {
        List<AudioDeviceInfo> inputs, outputs;
        try { inputs = WasapiSource.ListDevices(true); } catch { inputs = new(); }
        try { outputs = WasapiSource.ListDevices(false); } catch { outputs = new(); }
        if (inputs.Count > 0)
        {
            items.Add(new Choice("Audio inputs", "", true));
            items.AddRange(inputs.Select(d => new Choice("Input: " + d.Name, "in:" + d.Id)));
        }
        if (outputs.Count > 0)
        {
            items.Add(new Choice("Outputs (whatever plays on them)", "", true));
            items.AddRange(outputs.Select(d => new Choice("Output: " + d.Name, "out:" + d.Id)));
        }
        if (IsDeviceValue(current) && !items.Any(i => Same(i.Value, current)))
            items.Add(new Choice("Saved device (not connected)", current));
    }

    private void UpdateMonitorLabel()
    {
        var m = _app.SelectedMonitor;
        if (m == null) { MonitorLabel.Text = "No monitor"; return; }
        MonitorLabel.Text = $"{(string.IsNullOrEmpty(m.FriendlyName) ? m.ShortName : m.FriendlyName)}  ·  {m.Width}×{m.Height}{(m.IsPrimary ? "  ·  primary" : "")}";
        MonitorDetail.Text = m.IsHdr
            ? $"HDR on · tone-mapped to SDR (SDR white {m.SdrWhiteNits:0} nits) · {m.AdapterName}"
            : $"SDR · {m.AdapterName}";
    }

    // ------------------------------------------------------------------ status

    private void RefreshStatus()
    {
        if (!IsLoaded) return;
        if (_shownMonitors != null && !ReferenceEquals(_shownMonitors, _app.Monitors))
        {
            // A display change (monitor plugged in or out, resolution, HDR switched): AppController re-enumerated and restarted
            // the engine; the map, the label / HDR line, the codecs (adapter) and the estimates follow. Nothing here saves or restarts.
            _shownMonitors = _app.Monitors;
            MonitorMap.SetMonitors(_app.Monitors, _app.SelectedMonitor?.DeviceName);
            UpdateMonitorLabel();
            FillCodecs();
            UpdateEstimates();
        }
        var eng = _app.Engine;
        var state = _app.State;
        StatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, StatusDotWindow.KeyFor(state, _app.IsManualRecording));
        StatusText.Text = _app.StatusText;

        bool noticeShowing = DateTime.UtcNow < _noticeUntil;
        if (eng.IsRunning)
        {
            var st = eng.GetStats();
            // The game first: at the default width the line is cut at its end, and the stats are the part that can go.
            string game = st.Game != null ? $"{st.Game} · " : "";
            if (!noticeShowing) FooterText.Text = $"{game}{st.Encoder.Replace(" Encoder MFT", "")} · buffer {st.BufferedSeconds:0}s · {st.Dropped} dropped";
            // Each entry says what the track is actually capturing ("● Game (Overwatch)", "● Game (everything except Discord)",
            // "● Mic (USB Microphone)") or why it is not ("○ Voice (waiting for app)"), so a pick is confirmed here and the
            // fallback is not mistaken for the real thing. Non-breaking spaces inside an entry: the line wraps between tracks only.
            AudioStatus.Text = st.Audio.Count == 0 ? "No audio tracks" :
                string.Join("   ", st.Audio.Select(a => $"{(a.Running ? "●" : "○")} {a.Name}{(a.Detail != null ? $" ({a.Detail})" : "")}".Replace(' ', ' ')));
        }
        else
        {
            if (!noticeShowing)
                FooterText.Text = state switch
                {
                    RecordingState.Error => eng.Error ?? "Error",
                    RecordingState.WaitingForGame => "Not recording until a game or custom app runs (Capture → Recording)",
                    RecordingState.Paused => "Recording paused",
                    _ => eng.Status,
                };
            // Never leave the previous run's "● Mix   ● Game" line under a Paused / Error / Starting header: with the pickers
            // right above it, that reads as "my new selection is active".
            AudioStatus.Text = state switch
            {
                RecordingState.Paused => "Paused",
                RecordingState.WaitingForGame => "Starts with the game",
                RecordingState.Error => "Not recording",
                _ => eng.Status,
            };
        }

        string? game2 = _app.CurrentGame;
        string source = _app.CurrentGameSource switch { "fullscreen" => "fullscreen window", "library" => "game library folder", "custom" => "custom app", _ => "" };
        ModeStatus.Text = _app.GameOnly
            ? game2 != null ? $"Detected: {game2}{(source.Length > 0 ? $" ({source})" : "")}" : "Waiting for a game… detected by a fullscreen window on the captured monitor, a known game-library folder (Steam, Epic, Riot, Battle.net, Ubisoft, EA, GOG, Xbox, Rockstar) or a custom app below."
            : game2 != null ? $"Recording whenever Clink runs · game: {game2}" : "Recording whenever Clink runs.";
    }

    // ------------------------------------------------------------------ app tab: appearance, settings folder

    /// <summary>
    /// The App tab's Appearance card: a pill per theme and a swatch per accent, made from ThemeManager's lists so the ids and labels
    /// live in one place. Built once, by <see cref="Populate"/>; after that only the checked ones are brought in line with the settings.
    /// </summary>
    private void BuildAppearanceChoices()
    {
        if (ThemeChoices.Children.Count == 0)
        {
            foreach (var theme in ThemeManager.Themes)
            {
                var pill = new RadioButton
                {
                    Content = theme.Label, Tag = theme.Id, GroupName = "AppTheme", Style = (Style)FindResource("PillRadio"),
                    Padding = new Thickness(16, 5, 16, 5), FontSize = 12.5, Margin = new Thickness(0, 0, 8, 0),
                };
                pill.Checked += ThemeChoice_Checked;
                ThemeChoices.Children.Add(pill);
            }
            foreach (var accent in ThemeManager.Accents)
            {
                var swatch = new RadioButton
                {
                    Tag = accent.Id, GroupName = "AppAccent", Style = (Style)FindResource("SwatchRadio"), Margin = new Thickness(0, 0, 8, 0),
                    ToolTip = accent.Label + (accent.Id == SettingsDefaults.Accent ? " (default)" : ""),
                };
                System.Windows.Automation.AutomationProperties.SetName(swatch, accent.Label + " accent");
                swatch.Checked += AccentChoice_Checked;
                AccentChoices.Children.Add(swatch);
            }
            ThemeManager.ThemeChanged -= RefreshAppearance;
            ThemeManager.ThemeChanged += RefreshAppearance;
        }
        RefreshAppearance();
    }

    /// <summary>The checked pill and swatch follow the settings; each swatch shows its accent as it looks in the theme in force.</summary>
    private void RefreshAppearance()
    {
        bool was = _filling;
        _filling = true;   // checking a radio here is not a choice
        try
        {
            string theme = ThemeManager.NormalizeTheme(_app.Settings.Theme), accent = ThemeManager.NormalizeAccent(_app.Settings.Accent);
            foreach (var pill in ThemeChoices.Children.OfType<RadioButton>()) pill.IsChecked = (string?)pill.Tag == theme;
            foreach (var swatch in AccentChoices.Children.OfType<RadioButton>())
            {
                swatch.IsChecked = (string?)swatch.Tag == accent;
                var fill = new SolidColorBrush(ThemeManager.AccentColor((string?)swatch.Tag, theme));
                fill.Freeze();
                swatch.Background = fill;
            }
        }
        finally { _filling = was; }
    }

    private void ThemeChoice_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading || _filling || sender is not RadioButton { Tag: string id }) return;
        var s = _app.Settings;
        if (string.Equals(s.Theme, id, StringComparison.OrdinalIgnoreCase)) return;
        s.Theme = id;
        s.Save();
        ThemeManager.Apply(s.Theme, s.Accent);
    }

    private void AccentChoice_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading || _filling || sender is not RadioButton { Tag: string id }) return;
        var s = _app.Settings;
        if (string.Equals(s.Accent, id, StringComparison.OrdinalIgnoreCase)) return;
        s.Accent = id;
        s.Save();
        ThemeManager.Apply(s.Theme, s.Accent);
    }

    private void SettingsFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(Settings.Dir);
            Process.Start(new ProcessStartInfo(Settings.Dir) { UseShellExecute = true });
        }
        catch (Exception ex) { _app.Say("Could not open the folder: " + ex.Message); }
    }

    /// <summary>
    /// The SETTINGS card's banners, from <see cref="AppDataRedirect"/> (Clink runs inside a packaged app whose private copy of
    /// AppData it sees) and <see cref="SettingsImport.Pending"/> (another copy saved settings that differ from these). Shown on
    /// start and whenever <see cref="SettingsImport.PendingChanged"/> says Pending changed.
    /// </summary>
    private void RefreshSettingsBanners()
    {
        string? redirected = AppDataRedirect.Notice;
        RedirectBanner.Visibility = redirected != null ? Visibility.Visible : Visibility.Collapsed;
        RedirectText.Text = redirected ?? "";
        var pending = SettingsImport.Pending;
        ImportBanner.Visibility = pending != null ? Visibility.Visible : Visibility.Collapsed;
        if (pending != null) ImportText.Text = ImportBannerText(pending);
    }

    private void OnImportPendingChanged()
    {
        if (Dispatcher.CheckAccess()) RefreshSettingsBanners();
        else Dispatcher.BeginInvoke(RefreshSettingsBanners);
    }

    /// <summary>"Clink also has settings saved 2026-10-21 14:32 by a copy started from inside Claude (video quality, hotkeys)".</summary>
    internal static string ImportBannerText(ForeignSettings copy)
    {
        string what = copy.Differences.Count > 0 ? $" ({string.Join(", ", copy.Differences)})" : "";
        return $"Clink also has settings saved {copy.SavedLocalText} by a copy started from inside {copy.FriendlyName}{what}";
    }

    private void ImportUse_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsImport.Pending is not { } copy) return;
        ImportUseButton.IsEnabled = ImportDismissButton.IsEnabled = false;   // one click: it restarts Clink
        _app.ImportForeignSettings(copy);
        // Pending is still there when the import failed (a notice says why) and Clink carries on; after one that worked it is gone.
        if (SettingsImport.Pending != null) ImportUseButton.IsEnabled = ImportDismissButton.IsEnabled = true;
    }

    private void ImportDismiss_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsImport.Pending is { } copy) SettingsImport.Decline(_app.Settings, copy);   // PendingChanged then hides the banner (and the tray item)
    }

    // ------------------------------------------------------------------ updates

    private void RefreshUpdateState()
    {
        var v = _app.PendingVersion;
        UpdateButton.Content = v != null ? $"Update to v{v}" : "Update";
        UpdateButton.Visibility = v != null ? Visibility.Visible : Visibility.Collapsed;
        UpdateNowButton.Content = v != null ? $"Update to v{v}" : "Update";
        UpdateNowButton.Visibility = v != null ? Visibility.Visible : Visibility.Collapsed;
        // LastError also holds a download / install failure after a check that worked: only a failed check is called one.
        // A --dev instance never looks for updates: the version line below says so, so the "last check failed: updates are off in dev
        // mode" the updater leaves as its status (Updater.LastError) is not shown a second time as a problem.
        string? problem = _app.Updater.InstallProblem
            ?? (_app.Updater.LastError is string err && !(AppController.IsDev && string.Equals(err, Updater.DevModeNote, StringComparison.OrdinalIgnoreCase))
                ? (_app.Updater.CheckFailed ? "Last check failed: " : "Update problem: ") + err : null);
        UpdateProblemText.Text = problem ?? "";
        UpdateProblemText.Visibility = problem != null ? Visibility.Visible : Visibility.Collapsed;
        CheckUpdateButton.IsEnabled = _app.Updater.Repo != null;
        if (_app.Updater.Repo == null) UpdateVersionText.Text = $"Clink v{_app.Updater.VersionDisplay} · {NoUpdatesText.ToLowerInvariant()}";
    }

    /// <summary>Why there is nothing to check: a --dev instance never looks for updates; any other build has no update repository.</summary>
    private static string NoUpdatesText => AppController.IsDev ? Updater.DevModeNote : "Updates are not configured in this build";

    private void Update_Click(object sender, RoutedEventArgs e) => _app.UpdateNow();

    private void AutoUpdateChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _app.Settings.AutoUpdate = AutoUpdateCheck.IsChecked == true;
        _app.Settings.Save();
        // The background loop reads the switch on every pass; turned on, it checks now rather than at the end of its 6 h wait.
        if (_app.Settings.AutoUpdate) _app.Updater.Wake();
    }

    private async void Version_Click(object sender, MouseButtonEventArgs e) => await CheckForUpdatesAsync();

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e) => await CheckForUpdatesAsync();

    private async Task CheckForUpdatesAsync()
    {
        if (_checkingUpdate) return;
        var up = _app.Updater;
        if (up.Repo == null) { _app.Say(NoUpdatesText); return; }
        _checkingUpdate = true;
        _app.Say("Checking…");
        try
        {
            var check = up.CheckNowAsync();
            // A newer release is downloaded inside the same call (~30 MB), and the call also waits for a download already in
            // progress: keep the footer honest instead of letting the stats line take it back after 4 s.
            while (await Task.WhenAny(check, Task.Delay(2000)) != check)
            {
                string text = up.IsDownloading ? "Downloading update…" : "Still checking…";
                if (_app.LastNotice != text) _app.Say(text); else _noticeUntil = DateTime.UtcNow.AddSeconds(4);
            }
            var info = await check;
            string releasePage = info == null || string.IsNullOrEmpty(info.HtmlUrl) ? $"https://github.com/{up.Repo}/releases/latest" : info.HtmlUrl;
            if (info == null) _app.Say(up.LastError != null ? "Update check failed: " + up.LastError : $"You're up to date (v{up.VersionDisplay})");
            else if (_app.PendingVersion is Version pending && pending >= info.Version) _app.Say($"Update v{info.Version} is ready - click 'Update to v{pending}' to restart");
            else if (!_app.Settings.AutoUpdate)
            {
                // Nothing is downloaded with auto-update off, so make the answer actionable: show the release.
                _app.Say($"Clink v{info.Version} is available (auto-update is off) - opening the release page");
                OpenUrl(releasePage);
            }
            else
            {
                // Auto-update is on but nothing is ready to install (the download failed, Updater.LastError says why): an
                // "available" with nothing to click would be a dead end, so say why and show the release instead.
                string why = up.LastError?.TrimEnd('.') ?? "it could not be downloaded";
                _app.Say($"Update v{info.Version} found, but {why} - opening the release page");
                OpenUrl(releasePage);
            }
        }
        finally { _checkingUpdate = false; RefreshUpdateState(); }
    }

    private void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { _app.Say("Could not open the browser: " + ex.Message); }
    }

    // ------------------------------------------------------------------ clips

    /// <summary>Opens the clip in the trim editor (the Trim button, the toast, --trim-last).</summary>
    internal void OpenTrim(ClipEntry entry) => OpenClip(entry, trim: true);

    /// <summary>
    /// Opens a clip in the player (a click on its row; <paramref name="trim"/> false) or in the trim editor. A clip that is already
    /// open comes forward in the mode asked for instead of a second window opening on it.
    /// </summary>
    internal void OpenClip(ClipEntry entry, bool trim)
    {
        if (TrimWindow.Find(entry) is { } open) { open.Reveal(trim); return; }
        var w = new TrimWindow(entry, trim) { Owner = IsVisible ? this : null };
        w.Show();
    }

    private void Folder_Click(object sender, RoutedEventArgs e) => _app.OpenFolder();

    private void ClipNow_Click(object sender, RoutedEventArgs e) => _app.ClipNow();

    // ------------------------------------------------------------------ settings handlers

    private void ScheduleRestart()
    {
        if (_loading) return;
        _restartTimer.Stop();
        _restartTimer.Start();
    }

    private void QualityChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || _filling) return;
        var s = _app.Settings;
        s.Resolution = Selected<string>(ResolutionCombo) ?? SettingsDefaults.Resolution;
        s.Fps = Selected<int>(FpsCombo) is var f && f > 0 ? f : SettingsDefaults.Fps;
        if (BitrateCombo.IsEnabled && Selected<int>(BitrateCombo) is var b && b > 0) s.BitrateMbps = b;   // read-only while a size target picks it
        s.Codec = Selected<string>(CodecCombo) ?? SettingsDefaults.Codec;
        s.CaptureCursor = CursorCheck.IsChecked == true;
        s.Save();
        UpdateCodecDescription();
        UpdateEstimates();
        ScheduleRestart();
    }

    private void ReplayLengthCommitted(int seconds)
    {
        if (_loading) return;
        var s = _app.Settings;
        if (s.BufferSeconds != seconds)
        {
            s.BufferSeconds = seconds;
            s.Save();
            ScheduleRestart();
        }
        HotkeyEditor.DefaultSeconds = seconds;
        UpdateEstimates();
    }

    private void HotkeysChanged()
    {
        if (_loading) return;
        var s = _app.Settings;
        s.Hotkeys = HotkeyEditor.Actions.ToList();
        // Hotkeys[0] is the main hotkey (the editor keeps it first): the v1 single binding follows its keys while it has any. A cleared
        // main leaves the old keys in Hotkey, as Settings does on load (the tray and balloon labels read ClipHotkeyLabel from the list).
        if (s.Hotkeys.Count > 0 && s.Hotkeys[0].Keys.Vk > 0) s.Hotkey = s.Hotkeys[0].Keys.Clone();
        s.Save();
        _app.ReloadHotkeys();
    }

    private void AudioChanged(object sender, RoutedEventArgs e)
    {
        UpdateAudioEnabled();
        if (_loading) return;
        DismissAudioSetupBanner();
        var s = _app.Settings;
        s.TrackDesktop = TrackDesktopCheck.IsChecked == true;
        s.TrackGame = TrackGameCheck.IsChecked == true;
        s.TrackMic = TrackMicCheck.IsChecked == true;
        s.TrackVoice = TrackVoiceCheck.IsChecked == true;
        s.Save();
        UpdateEstimates();   // the track count is part of the size estimate and of a size target's bitrate
        ScheduleRestart();
    }

    private void AudioSourceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _filling) return;
        DismissAudioSetupBanner();
        var s = _app.Settings;
        // Only the picker that changed writes its setting, so what another picker happens to show can never clobber a saved value.
        if (sender == AllDeviceCombo) s.AllDeviceId = DeviceId(AllDeviceCombo);
        else if (sender == GameCombo) s.GameProcess = ProcessName(GameCombo);
        else if (sender == MicCombo) s.MicDeviceId = DeviceId(MicCombo);
        else if (sender == VoiceCombo) s.VoiceApp = ProcessName(VoiceCombo);
        else return;
        s.Save();
        ScheduleRestart();
    }

    private static string? DeviceId(ComboBox combo) => Selected<string>(combo) is { Length: > 0 } id ? id : null;   // "" = the default device

    private static string ProcessName(ComboBox combo) => Selected<string>(combo) is { Length: > 0 } name ? name : "auto";

    // ------------------------------------------------------------------ "set up your audio" banner

    // A hover counts once the pointer has rested on the group for a moment: WPF also raises MouseEnter when the group merely
    // scrolls under a pointer that is not moving, which must not dismiss the banner for good on the way past.
    private void AudioGroup_MouseEnter(object sender, MouseEventArgs e)
    {
        if (AudioSetupBanner.Visibility == Visibility.Collapsed) return;
        _bannerTimer.Stop();
        _bannerTimer.Start();
    }

    // The banner goes once the pointer is off the group: collapsing it while the pointer rests there (at the top of the pane
    // nothing can scroll to make up for it) would move the group up under it, and the next click would hit the row below.
    private void AudioGroup_MouseLeave(object sender, MouseEventArgs e)
    {
        _bannerTimer.Stop();
        if (_app.Settings.AudioSetupSeen) DismissAudioSetupBanner();
    }

    /// <summary>
    /// The pointer has rested on the audio group: the banner counts as seen (saved, so it never comes back) and goes when the
    /// pointer leaves the group (<see cref="AudioGroup_MouseLeave"/>).
    /// </summary>
    private void MarkAudioSetupSeen()
    {
        if (_loading || AudioSetupBanner.Visibility == Visibility.Collapsed || _app.Settings.AudioSetupSeen) return;
        _app.Settings.AudioSetupSeen = true;
        _app.Settings.Save();
    }

    /// <summary>
    /// Hides the banner above the audio group for good: the user has changed an option in the group, or has rested the pointer
    /// on it and moved off. <see cref="Settings.AudioSetupSeen"/> is saved so it never comes back; while the window is still
    /// loading only the visibility is touched. Where the pane is scrolled down it scrolls up by the banner's height so the group
    /// (and the option just clicked) stays in place instead of jumping up.
    /// </summary>
    private void DismissAudioSetupBanner()
    {
        _bannerTimer?.Stop();
        if (AudioSetupBanner == null || AudioSetupBanner.Visibility == Visibility.Collapsed) return;
        double shift = AudioSetupBanner.ActualHeight + AudioSetupBanner.Margin.Top;
        AudioSetupBanner.Visibility = Visibility.Collapsed;
        for (DependencyObject? p = AudioSetupBanner; p != null; p = VisualTreeHelper.GetParent(p))
            if (p is ScrollViewer sv) { if (sv.VerticalOffset > 0) sv.ScrollToVerticalOffset(Math.Max(0, sv.VerticalOffset - shift)); break; }
        if (_loading || _app.Settings.AudioSetupSeen) return;
        _app.Settings.AudioSetupSeen = true;
        _app.Settings.Save();
    }

    private void MiscChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _app.Settings.ClickSound = ClickSoundCheck.IsChecked == true;
        _app.Settings.Save();
        if (_app.Settings.ClickSound && sender == ClickSoundCheck) ClickSound.Play(_app.Settings.ClickVolume);
    }

    private void StartupChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _app.Settings.RunAtStartup = StartupCheck.IsChecked == true;
        _app.Settings.Save();
        _app.SetRunAtStartup(_app.Settings.RunAtStartup);
    }

    private void Token_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        TokenSaveButton.IsEnabled = true;
    }

    private void TokenSave_Click(object sender, RoutedEventArgs e)
    {
        string t = TokenBox.Password.Trim();
        _app.Settings.CopiumToken = string.IsNullOrEmpty(t) ? null : t;
        _app.Settings.Save();
        TokenSaveButton.IsEnabled = false;
        if (string.IsNullOrEmpty(t)) { CopiumStatus.Text = "Not connected"; CopiumStatus.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); }
        else if (!t.StartsWith("eup_")) { CopiumStatus.Text = "Saved (tokens usually start with eup_)"; CopiumStatus.SetResourceReference(TextBlock.ForegroundProperty, "Warn"); }
        else { CopiumStatus.Text = "Token saved"; CopiumStatus.SetResourceReference(TextBlock.ForegroundProperty, "Good"); }
    }

    private void CopiumSite_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("https://copium.pics/") { UseShellExecute = true }); } catch { }
    }

    private void CopiumPlusChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || _filling) return;   // _filling: the Copium Plus size target ticked the box (SizeTargetChanged saves)
        _app.Settings.CopiumPlus = CopiumPlusCheck.IsChecked == true;
        _app.Settings.Save();
    }

    // ------------------------------------------------------------------ toasts & status dot (§7)

    private void UpdateToastLabels()
    {
        if (ToastRecordingLabel == null) return;   // a slider's Minimum coerces its Value while the XAML is still loading
        ToastClipLabel.Text = $"{(int)ToastClipSlider.Value} s";
        ToastSharedLabel.Text = $"{(int)ToastSharedSlider.Value} s";
        ToastRecordingLabel.Text = $"{(int)ToastRecordingSlider.Value} s";
    }

    private void ToastChanged(object sender, RoutedEventArgs e)
    {
        UpdateToastLabels();
        if (_loading || _filling) return;
        var s = _app.Settings;
        s.ToastPosition = Selected<string>(ToastPositionCombo) ?? "bottom-right";
        s.ToastClip.Enabled = ToastClipCheck.IsChecked == true; s.ToastClip.Seconds = (int)ToastClipSlider.Value;
        s.ToastShared.Enabled = ToastSharedCheck.IsChecked == true; s.ToastShared.Seconds = (int)ToastSharedSlider.Value;
        s.ToastRecording.Enabled = ToastRecordingCheck.IsChecked == true; s.ToastRecording.Seconds = (int)ToastRecordingSlider.Value;
        ScheduleSave();   // sliders fire on every tick while dragging
    }

    private void ToastPreview_Click(object sender, RoutedEventArgs e)
    {
        _app.Toasts.Preview((sender as FrameworkElement)?.Tag as string ?? "clip");
    }

    private void DotChanged(object sender, RoutedEventArgs e)
    {
        if (DotPositionCombo == null || DotCheck == null) return;
        DotPositionCombo.IsEnabled = DotCheck.IsChecked == true;
        if (_loading || _filling) return;
        var s = _app.Settings;
        s.StatusDot = DotCheck.IsChecked == true;
        s.StatusDotPosition = Selected<string>(DotPositionCombo) ?? "top-right";
        s.Save();
        _app.ApplyStatusDot();
    }

    // ------------------------------------------------------------------ storage (§9)

    private void PathBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        // Enter commits like leaving the box does: move focus so LostFocus runs the handler.
        (sender as UIElement)?.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        e.Handled = true;
    }

    private void ClipsFolderBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var s = _app.Settings;
        string path = ClipsFolderBox.Text.Trim().Trim('"');
        if (path.Length == 0 || string.Equals(path, s.ClipsFolder, StringComparison.OrdinalIgnoreCase)) { ClipsFolderBox.Text = s.ClipsFolder; return; }
        if (!Path.IsPathFullyQualified(path))
        {
            // A relative path would resolve against whatever the current directory is (the exe's folder from Explorer, another
            // one when started at sign-in), so clips would land in different places.
            _app.Say(@"Use a full path for the clips folder, e.g. D:\Clips");
            ClipsFolderBox.Text = s.ClipsFolder;
            return;
        }
        if (!string.IsNullOrWhiteSpace(s.ArchivePath) && ArchiveService.IsSameFolder(path, s.ArchivePath))
        {
            _app.Say("The clips folder cannot be the archive location");
            ClipsFolderBox.Text = s.ClipsFolder;
            return;
        }
        try { Directory.CreateDirectory(path); }
        catch (Exception ex) { _app.Say("Cannot use that folder: " + ex.Message); ClipsFolderBox.Text = s.ClipsFolder; return; }
        s.ClipsFolder = path;
        s.Save();
        _app.Library.Load(path);
        _app.RecoverRecordingsAsync();   // unfinished recordings the new folder may hold
        _app.Say("Clips folder changed");
        ScheduleRestart();   // the engine writes clips to its configured folder
    }

    private void BrowseClips_Click(object sender, RoutedEventArgs e)
    {
        string? picked = PickFolder("Local clips folder", _app.Settings.ClipsFolder);
        if (picked == null) return;
        ClipsFolderBox.Text = picked;
        ClipsFolderBox_LostFocus(sender, e);
    }

    private void BrowseArchive_Click(object sender, RoutedEventArgs e)
    {
        string? picked = PickFolder("Archive location (NAS share, mapped drive or folder)", _app.Settings.ArchivePath);
        if (picked == null) return;
        ArchivePathBox.Text = picked;
        ArchivePathBox_LostFocus(sender, e);
    }

    private string? PickFolder(string title, string? initial)
    {
        try
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = title, Multiselect = false };
            if (!string.IsNullOrWhiteSpace(initial))
            {
                // An offline share blocks Directory.Exists for the SMB timeout (~20 s): look on a worker, give up on the hint quickly.
                var probe = Task.Run(() => { try { return Directory.Exists(initial); } catch { return false; } });
                if (probe.Wait(300) && probe.Result) dlg.InitialDirectory = initial;
            }
            return dlg.ShowDialog(this) == true ? dlg.FolderName : null;
        }
        catch (Exception ex) { _app.Say("Folder picker failed: " + ex.Message); return null; }
    }

    /// <summary>The days / GB rules apply only to the automatic moves, so only they follow the switch; the location, Test and Archive now… are always there.</summary>
    private void UpdateArchiveEnabled()
    {
        if (ArchiveRulesPanel == null || ArchiveCheck == null) return;
        ArchiveRulesPanel.IsEnabled = ArchiveCheck.IsChecked == true;
        ArchiveRulesPanel.Opacity = ArchiveCheck.IsChecked == true ? 1 : 0.55;
    }

    private void ArchiveChanged(object sender, RoutedEventArgs e)
    {
        UpdateArchiveEnabled();
        if (_loading) return;
        _app.Settings.ArchiveEnabled = ArchiveCheck.IsChecked == true;
        _app.Settings.Save();
        _app.Archive.Reevaluate();   // the status line follows the switch now, not at the next 5-minute run
        RefreshArchiveStatus();
    }

    /// <summary>
    /// Why the path last typed into the archive box was refused (the box went back to the saved path), until the user leaves the
    /// box again or a path is accepted. Clicking Test moves the focus first, so the refusal happens before Test runs: Test then
    /// keeps this reason on screen instead of testing the old saved path and reporting it as reachable.
    /// </summary>
    private string? _archiveRefusal;

    /// <summary>How many typed archive paths have been refused so far (<see cref="RefuseArchivePath"/>): Archive now… compares it before and after its own click.</summary>
    private int _archiveRefusals;

    /// <summary>
    /// <see cref="_archiveRefusal"/> was made while the focus moved onto Archive now…. A mouse press (and an automation client,
    /// which focuses the button first) moves the focus out of the path box onto the button just before its click arrives, and that
    /// is what refuses a path typed there. A refusal from an earlier Tab, or a click elsewhere, is not that click's.
    /// </summary>
    private bool _archiveRefusalForArchiveNow;

    private void ArchivePathBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var s = _app.Settings;
        string path = ArchivePathBox.Text.Trim().Trim('"');
        if (path.Length == 2 && path[1] == ':' && char.IsAsciiLetter(path[0])) { path += "\\"; ArchivePathBox.Text = path; }   // "E:" = the drive's root
        string? value = path.Length == 0 ? null : path;
        if (string.Equals(value, s.ArchivePath, StringComparison.OrdinalIgnoreCase))
        {
            if (ReferenceEquals(sender, ArchivePathBox)) _archiveRefusal = null;   // the user left the box on the saved path: nothing refused now
            return;
        }
        if (value != null && !Path.IsPathFullyQualified(value))
        {
            // "\nas\clips" or "nas\clips" (a lost backslash) would resolve against the current folder: C:\nas\clips, or next to the
            // exe, which also differs between an Explorer start and a sign-in start (see the clips folder).
            _app.Say(ArchiveService.FullPathHint);
            RefuseArchivePath(ArchiveService.FullPathHint);
            return;
        }
        if (value != null && ArchiveService.IsSameFolder(value, s.ClipsFolder))
        {
            // Moving a clip onto itself would be "copy verified, delete local": the one copy gone. Refused here and in the service.
            _app.Say("The archive location cannot be the clips folder itself");
            RefuseArchivePath("Archive location is the clips folder: pick another folder, share or drive");
            return;
        }
        _archiveRefusal = null;
        s.ArchivePath = value;
        s.Save();
        _app.Archive.Reevaluate();
        RefreshArchiveStatus();
    }

    private void RefuseArchivePath(string reason)
    {
        _archiveRefusal = reason;
        _archiveRefusals++;
        _archiveRefusalForArchiveNow = ReferenceEquals(Keyboard.FocusedElement, ArchiveNowButton);   // the focus is already on the element it moved to
        ArchiveStatus.Text = reason;
        ArchiveStatus.SetResourceReference(TextBlock.ForegroundProperty, "Warn");
        ArchivePathBox.Text = _app.Settings.ArchivePath ?? "";
    }

    private void ArchiveRulesChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var s = _app.Settings;
        if (int.TryParse(ArchiveDaysBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int days) && days >= 0) s.ArchiveAfterDays = days;
        ArchiveDaysBox.Text = s.ArchiveAfterDays.ToString(CultureInfo.InvariantCulture);
        string gbText = ArchiveGbBox.Text.Trim().Replace(',', '.');
        if (double.TryParse(gbText, NumberStyles.Float, CultureInfo.InvariantCulture, out double gb) && gb >= 0) s.ArchiveWhenLocalExceedsGB = gb;
        ArchiveGbBox.Text = s.ArchiveWhenLocalExceedsGB.ToString("0.##", CultureInfo.InvariantCulture);
        s.Save();
        _app.Archive.Reevaluate();
        RefreshArchiveStatus();
    }

    private async void ArchiveTest_Click(object sender, RoutedEventArgs e)
    {
        ArchivePathBox_LostFocus(sender, e);
        if (_archiveRefusal is string refused)
        {
            // What the user typed was refused and the box shows the saved path again: say why, do not test (and approve) the old path.
            _archiveRefusal = null;
            ArchiveStatus.Text = refused;
            ArchiveStatus.SetResourceReference(TextBlock.ForegroundProperty, "Warn");
            return;
        }
        string path = ArchivePathBox.Text.Trim().Trim('"');
        ArchiveTestButton.IsEnabled = false;
        ArchiveStatus.Text = "Testing…";
        ArchiveStatus.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        try
        {
            var (ok, message, _) = await _app.Archive.TestAsync(path);
            ArchiveStatus.Text = message;
            ArchiveStatus.SetResourceReference(TextBlock.ForegroundProperty, ok ? "Good" : "Warn");
        }
        finally { ArchiveTestButton.IsEnabled = true; }
    }

    /// <summary>
    /// "Archive now…": the dialog where the user picks what to move (what the rules pick, or every local clip) and watches it
    /// happen. It needs a location, nothing else: not the automatic switch and not a rule.
    /// </summary>
    private void ArchiveNow_Click(object sender, RoutedEventArgs e)
    {
        // A path typed into the box is taken (or refused) before the dialog opens, as Test does: the dialog must not move clips
        // to the old saved path while the box showed another. Only a refusal that this click caused counts; one from earlier
        // (the user tabbed away from a bad path, read the message, and now clicks) must not eat the click. A mouse press, or an
        // automation client, has already moved the focus onto the button (and refused the path) by the time the click arrives;
        // a key press on the focused button has not moved anything, so the call below is what takes or refuses the path.
        int before = _archiveRefusals;
        ArchivePathBox_LostFocus(sender, e);
        bool causedByThisClick = _archiveRefusals != before || _archiveRefusalForArchiveNow;
        if (causedByThisClick && _archiveRefusal is string refused)
        {
            _archiveRefusal = null;
            _archiveRefusalForArchiveNow = false;
            ArchiveStatus.Text = refused;
            ArchiveStatus.SetResourceReference(TextBlock.ForegroundProperty, "Warn");
            return;
        }
        if (string.IsNullOrWhiteSpace(_app.Settings.ArchivePath)) { _app.Say("Set an archive location first"); return; }
        new ArchiveWindow(_app) { Owner = this }.ShowDialog();
        RefreshArchiveStatus();
    }

    private bool _archiveReachable;

    private void RefreshArchiveStatus()
    {
        // Archived rows checked while the archive was offline (at login, before the NAS was up) look again once it answers.
        bool reachable = _app.Archive.IsReachable;
        if (reachable && !_archiveReachable) Clips.ArchiveIsBack();
        _archiveReachable = reachable;
        if (!IsLoaded) return;
        ArchiveStatus.Text = _app.Archive.Status;
        string t = ArchiveStatus.Text;
        bool problem = t.Contains("will retry") || t.Contains("unreachable") || t.Contains("denied") || t.Contains("no rule") || t.Contains("archive location");
        ArchiveStatus.SetResourceReference(TextBlock.ForegroundProperty, problem ? "Warn" : "Muted");
        // Archive now… works with any location set, whether or not the automatic moves are on.
        bool hasLocation = !string.IsNullOrWhiteSpace(_app.Settings.ArchivePath);
        ArchiveNowButton.IsEnabled = hasLocation;
        ArchiveNowButton.ToolTip = hasLocation ? "Choose which clips to move to the archive location now" : "Set an archive location first";
    }

    // ------------------------------------------------------------------ window chrome

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) { HotkeyEditor.CancelCapture(); WindowState = WindowState.Minimized; }

    private void Close_Click(object sender, RoutedEventArgs e) => HideToTray();

    public void HideToTray()
    {
        HotkeyEditor.CancelCapture();
        if (WindowState == WindowState.Normal)
        {
            _app.Settings.WindowX = Left; _app.Settings.WindowY = Top;
            _app.Settings.WindowWidth = ActualWidth; _app.Settings.WindowHeight = ActualHeight;
        }
        _saveTimer.Stop();
        _app.Settings.Save();
        Hide();
        if (!_hintShown)
        {
            _hintShown = true;
            string how = _app.ClipHotkeyLabel is string key ? $"Press {key} to save a clip." : "Add a hotkey under Capture → Hotkeys to save clips.";
            _app.ShowBalloon("Clink is still running", how + " Right-click the tray icon to quit.");
        }
    }

    public void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Topmost = true; Topmost = false;
    }
}
