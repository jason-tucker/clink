using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Clink.Services;

namespace Clink.UI;

/// <summary>
/// The hotkey list (docs/SPEC-v2.md §6, v0.2.5): the main hotkey on top, which always saves the full replay and can be cleared
/// but not removed, and "More hotkeys" below it. <see cref="Actions"/> is the list in settings order: <c>[0]</c> is the main
/// (Settings puts it there, see <c>Settings.EnsureHotkeyModel</c>) and every later <see cref="HotkeyAction"/> is a row with the key
/// combo (click, then press the keys: <see cref="Hotkeys.CaptureNext"/>), the action (clip / pause / share last) and, for clips,
/// a fixed length shorter than the replay (<see cref="AdditionalLengths"/>). <see cref="Changed"/> fires after every edit with the
/// list already updated in <see cref="Actions"/>; the window saves it and calls <c>AppController.ReloadHotkeys()</c>.
/// </summary>
public partial class HotkeyListEditor : UserControl
{
    private static readonly (string label, string value)[] s_actions =
    {
        ("Clip", "clip"), ("Pause / resume", "pause"), ("Share last clip", "share-last"),
        ("Start / stop recording", "record"),
    };
    private static readonly int[] s_lengths = { 5, 10, 15, 20, 30, 45, 60, 90, 120, 180, 300, 600, 900, 1200, 1800, 2700, 3600 };
    private static readonly int[] s_newLengths = { 30, 15, 10, 5 };   // what "Add hotkey" starts on: the longest of these that is shorter than the replay

    private readonly List<HotkeyAction> _actions = new();
    private int _defaultSeconds = SettingsDefaults.BufferSeconds;
    private bool _building;
    private Button? _capturing;
    private string? _capturingLabel;

    private sealed record Choice(string Label, object Value) { public override string ToString() => Label; }

    private Window? _window;

    public HotkeyListEditor()
    {
        InitializeComponent();
        WireCapture(MainKeys, () => Main);
        // An armed capture takes the next key pressed anywhere on the system: it must not outlive the window being the
        // active one (Alt+Tab to the game, a click on Discord) or the editor being on screen.
        Loaded += (_, _) =>
        {
            if (_window != null) _window.Deactivated -= OnWindowDeactivated;
            _window = Window.GetWindow(this);
            if (_window != null) _window.Deactivated += OnWindowDeactivated;
        };
        Unloaded += (_, _) =>
        {
            CancelCapture();
            if (_window != null) { _window.Deactivated -= OnWindowDeactivated; _window = null; }
        };
        IsVisibleChanged += (_, e) => { if (e.NewValue is false) CancelCapture(); };
    }

    private void OnWindowDeactivated(object? sender, EventArgs e) => CancelCapture();

    /// <summary>Fires after the user changed something; <see cref="Actions"/> already holds the new list.</summary>
    public event Action? Changed;

    /// <summary>The bindings as edited (same objects as passed to <see cref="Load"/> where unchanged). <c>[0]</c> is the main hotkey.</summary>
    internal IReadOnlyList<HotkeyAction> Actions => _actions;

    /// <summary>The replay-length slider's value: what the main hotkey saves (its hint), and the ceiling of every additional clip row's length.</summary>
    public int DefaultSeconds
    {
        get => _defaultSeconds;
        set
        {
            if (value == _defaultSeconds) return;
            _defaultSeconds = value;
            Rebuild();
        }
    }

    /// <summary>
    /// Shows the list. <c>actions[0]</c> is the main hotkey: Settings guarantees it (a "clip" with Seconds 0, keys possibly unset),
    /// and a list that somehow lacks one gets an unset main in front here, so there is always a row to show and to set.
    /// </summary>
    internal void Load(IEnumerable<HotkeyAction> actions, int defaultSeconds)
    {
        _defaultSeconds = defaultSeconds;
        _actions.Clear();
        _actions.AddRange(actions.Where(a => a != null));
        if (_actions.Count == 0 || !Hotkeys.IsClip(_actions[0].Action) || _actions[0].Seconds > 0)
            _actions.Insert(0, new HotkeyAction { Keys = new HotkeyBinding { Vk = 0 }, Action = "clip", Seconds = 0 });
        Rebuild();
    }

    /// <summary>The main hotkey (<c>Actions[0]</c>), or null before <see cref="Load"/>.</summary>
    private HotkeyAction? Main => _actions.Count > 0 ? _actions[0] : null;

    // ------------------------------------------------------------------ lengths (pure: HotkeyEditorTests)

    /// <summary>
    /// The fixed lengths an additional clip hotkey offers for a replay length: the slider's scale (5 s – 1 h) strictly below it. The
    /// main hotkey already saves the whole replay, and a buffer holds no more than that, so a longer pick would only be the same clip.
    /// </summary>
    internal static int[] AdditionalLengths(int replaySeconds) => s_lengths.Where(l => l < replaySeconds).ToArray();

    /// <summary>The length "Add hotkey" starts a row on: the longest of 30 / 15 / 10 / 5 s that is shorter than the replay; 5 s when none is.</summary>
    internal static int DefaultNewLength(int replaySeconds)
    {
        foreach (int l in s_newLengths)
            if (l < replaySeconds) return l;
        return 5;
    }

    /// <summary>
    /// What a clip row's length combo lists for a replay length and the row's saved <paramref name="current"/> length: the
    /// <see cref="AdditionalLengths"/>, plus the saved value when it is not among them so the combo shows what the row really does.
    /// 0 is a row from before the main hotkey existed that saves the full replay ("Full replay (0:45)"); a value at or above the
    /// replay length is "capped to replay" (the buffer holds no more); any other value is shown as it is (a hand-edited 25 s).
    /// With nothing shorter to offer (the smallest replay, 5 s) the only entry is the full replay. Ordered by length, the full
    /// replay last.
    /// </summary>
    internal static List<(int Value, string Label)> LengthChoices(int replaySeconds, int current)
    {
        var items = AdditionalLengths(replaySeconds).Select(l => (Value: l, Label: Capabilities.FormatDuration(l))).ToList();
        string full = $"Full replay ({Capabilities.FormatClipLength(replaySeconds)})";
        if (current <= 0) items.Add((0, full));
        else if (items.All(i => i.Value != current))
        {
            bool capped = current >= replaySeconds;
            items.Add((current, items.Count == 0 && capped ? full : capped ? $"{Capabilities.FormatDuration(current)} (capped to replay)" : Capabilities.FormatDuration(current)));
        }
        return items.OrderBy(i => i.Value == 0 ? int.MaxValue : i.Value).ToList();
    }

    /// <summary>The length combo's tooltip for a saved <paramref name="current"/> length: what the row does, in full for the entries the closed box cuts short.</summary>
    internal static string LengthTip(int replaySeconds, int current)
    {
        string replay = Capabilities.FormatClipLength(replaySeconds);
        if (current <= 0) return $"Saves the full replay ({replay}), like the main hotkey. Pick a length to make it a shorter clip";
        if (current >= replaySeconds) return $"The replay is {replay} long, so this hotkey saves all of it; the {Capabilities.FormatDuration(current)} you set counts again once the replay is longer";
        return "How much this hotkey saves: a fixed length shorter than the replay length (the main hotkey saves the whole replay)";
    }

    // ------------------------------------------------------------------ rows

    private void Rebuild()
    {
        _building = true;
        try
        {
            CancelCapture();
            RefreshMain();
            Rows.Children.Clear();
            for (int i = 1; i < _actions.Count; i++) Rows.Children.Add(BuildRow(_actions[i]));
            EmptyText.Visibility = _actions.Count <= 1 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { _building = false; }
    }

    /// <summary>The main row: its keys, whether there is anything to clear, and what it saves (the replay length changes with the slider).</summary>
    private void RefreshMain()
    {
        var m = Main;
        MainKeys.Content = m == null ? "Not set" : KeysLabel(m);
        MainKeys.IsEnabled = m != null;
        MainClear.IsEnabled = m?.Keys is { Vk: > 0 };
        MainHint.Text = $"saves the full replay ({Capabilities.FormatClipLength(_defaultSeconds)})";
    }

    private void MainClear_Click(object sender, RoutedEventArgs e)
    {
        if (Main is not { } m) return;
        CancelCapture();
        m.Keys = new HotkeyBinding { Vk = 0 };   // Vk 0 is "not set": the class default would be F9
        RefreshMain();
        Raise();
    }

    /// <summary>A keys button starts a capture for its row when clicked, and gives it up when the focus leaves it.</summary>
    private void WireCapture(Button keys, Func<HotkeyAction?> row)
    {
        keys.Click += (_, _) => { if (row() is { } a) BeginCapture(keys, a); };
        keys.LostKeyboardFocus += (_, _) => { if (ReferenceEquals(_capturing, keys)) CancelCapture(); };   // clicked elsewhere: the next key is not a hotkey
        // A refused bare arrow reaches the window: it must not move the focus (that would disarm the capture, and the next
        // arrow would change the row's action). Tab still leaves.
        keys.PreviewKeyDown += (_, e) => { if (ReferenceEquals(_capturing, keys) && e.Key is Key.Left or Key.Right or Key.Up or Key.Down) e.Handled = true; };
    }

    private UIElement BuildRow(HotkeyAction a)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 84 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var keys = new Button
        {
            Content = KeysLabel(a),
            Padding = new Thickness(8, 6, 8, 6),   // "Press keys" stays on one line down to the column's MinWidth: the row does not jump when a capture starts
            HorizontalContentAlignment = HorizontalAlignment.Left,
            FontWeight = FontWeights.SemiBold,
            ToolTip = "Click, then press the key combo you want (Esc cancels)",
            Tag = a,
        };
        WireCapture(keys, () => a);
        grid.Children.Add(keys);

        var oneLine = TryFindResource("OneLine") as DataTemplate;
        var action = new ComboBox { Margin = new Thickness(8, 0, 0, 0), Width = 156, ToolTip = "What the keys do", ItemTemplate = oneLine };
        action.ItemsSource = s_actions.Select(x => new Choice(x.label, x.value)).ToArray();
        action.SelectedItem = ((Choice[])action.ItemsSource).FirstOrDefault(c => string.Equals((string)c.Value, Normalize(a.Action), StringComparison.OrdinalIgnoreCase)) ?? ((Choice[])action.ItemsSource)[0];
        Grid.SetColumn(action, 1);
        grid.Children.Add(action);

        var length = new ComboBox { Margin = new Thickness(8, 0, 0, 0), Width = 124, ItemTemplate = oneLine };   // the tooltip comes with FillLengths
        FillLengths(length, a.Seconds);
        length.Visibility = Hotkeys.IsClip(a.Action) ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumn(length, 2);
        grid.Children.Add(length);

        var remove = new Button
        {
            Style = TryFindResource("GlyphButton") as Style,
            Content = "",
            Margin = new Thickness(8, 0, 0, 0),
            ToolTip = "Remove this hotkey",
        };
        Grid.SetColumn(remove, 3);
        grid.Children.Add(remove);

        action.SelectionChanged += (_, _) =>
        {
            if (_building || action.SelectedItem is not Choice c) return;
            a.Action = (string)c.Value;
            // An additional clip hotkey is a shorter clip: a row switched to Clip that saves the full replay (or has no length) starts on a fixed one.
            if (Hotkeys.IsClip(a.Action) && a.Seconds <= 0) { a.Seconds = DefaultNewLength(_defaultSeconds); FillLengths(length, a.Seconds); }
            length.Visibility = Hotkeys.IsClip(a.Action) ? Visibility.Visible : Visibility.Collapsed;
            Raise();
        };
        length.SelectionChanged += (_, _) =>
        {
            if (_building || length.SelectedItem is not Choice c) return;
            a.Seconds = (int)c.Value;
            length.ToolTip = LengthTip(_defaultSeconds, a.Seconds);
            Raise();
        };
        remove.Click += (_, _) =>
        {
            if (ReferenceEquals(_capturing, keys)) CancelCapture();
            _actions.Remove(a);
            Rebuild();
            Raise();
        };
        return grid;
    }

    private void FillLengths(ComboBox combo, int current)
    {
        // Only lengths shorter than the replay buffer (LengthChoices): the audio timelines keep the slider's value, so a longer
        // pick would start silent (the engine clamps the save to the buffer anyway). A saved value the list lacks stays selectable, marked.
        var items = LengthChoices(_defaultSeconds, current).Select(x => new Choice(x.Label, x.Value)).ToList();
        bool was = _building;
        _building = true;
        try
        {
            combo.ItemsSource = items;
            combo.SelectedItem = items.FirstOrDefault(c => (int)c.Value == Math.Max(0, current)) ?? items[0];
            combo.ToolTip = LengthTip(_defaultSeconds, current);
        }
        finally { _building = was; }
    }

    private static string Normalize(string? action)
    {
        string a = (action ?? "clip").Trim().ToLowerInvariant();
        return a == "share" ? "share-last" : a.Length == 0 ? "clip" : a;
    }

    private static string KeysLabel(HotkeyAction a) => a.Keys == null || a.Keys.Vk <= 0 ? "Not set" : a.Keys.ToString();

    // ------------------------------------------------------------------ capture

    private void BeginCapture(Button button, HotkeyAction a)
    {
        CancelCapture();
        _capturing = button;
        _capturingLabel = button.Content as string;
        button.Content = "Press keys";
        button.Focus();   // so clicking anywhere else (LostKeyboardFocus) disarms the capture
        Arm(button, a);
    }

    /// <summary>Waits for the next combo for row <paramref name="a"/>, again after one that could not be taken. Esc cancels.</summary>
    private void Arm(Button button, HotkeyAction a) => Hotkeys.CaptureNext(
        (vk, ctrl, alt, shift, win) => Dispatcher.BeginInvoke(() =>
        {
            if (!ReferenceEquals(_capturing, button)) return;   // a newer capture replaced this one, or it was cancelled
            if (vk == 0x1B) { _capturing = null; button.Content = _capturingLabel; Hint(null); return; }   // Esc cancels
            var keys = new HotkeyBinding { Vk = vk, Ctrl = ctrl, Alt = alt, Shift = shift, Win = win };
            // Only the first row with a combo ever fires: a second one would look set up and do nothing. Still listening.
            if (_actions.Any(o => !ReferenceEquals(o, a) && o.Keys is { } k && k.Vk == vk && k.Ctrl == ctrl && k.Alt == alt && k.Shift == shift && k.Win == win))
            {
                Hint($"{keys} is already used by another hotkey. Press other keys, or Esc.");
                Arm(button, a);
                return;
            }
            _capturing = null;
            Hint(null);
            a.Keys = keys;
            button.Content = keys.ToString();
            RefreshMain();   // the main's Clear button is for a main that has keys
            Raise();
        }),
        // A bare letter, digit or editing key would be eaten in every program from then on: not taken, still listening.
        () => Dispatcher.BeginInvoke(() =>
        {
            if (ReferenceEquals(_capturing, button)) Hint("Letters, digits and editing keys need Ctrl or Alt; F-keys, ScrollLock and Pause work alone.");
        }));

    /// <summary>Shows why the last keys were not taken, under the row being captured (the main hotkey's, or the rows'), or hides it (null).</summary>
    private void Hint(string? text)
    {
        var shown = ReferenceEquals(_capturing, MainKeys) ? MainCaptureHint : CaptureHint;
        MainCaptureHint.Visibility = CaptureHint.Visibility = Visibility.Collapsed;
        if (text == null) return;
        shown.Text = text;
        shown.Visibility = Visibility.Visible;
    }

    /// <summary>Stops waiting for keys (the previous "Press keys" button gets its label back).</summary>
    public void CancelCapture()
    {
        if (_capturing == null) return;
        Hotkeys.CaptureNext(null);
        _capturing.Content = _capturingLabel;
        _capturing = null;
        Hint(null);
    }

    // ------------------------------------------------------------------ add

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        // A fresh row has no keys yet: the hook skips it (Vk == 0) until the user presses some. It is a shorter clip: the longest of
        // 30 / 15 / 10 / 5 s that is under the replay length (the main hotkey is the full replay).
        var a = new HotkeyAction { Keys = new HotkeyBinding { Vk = 0 }, Action = "clip", Seconds = DefaultNewLength(_defaultSeconds) };
        _actions.Add(a);
        Rebuild();
        if (Rows.Children.Count > 0 && Rows.Children[^1] is Grid g && g.Children.Count > 0 && g.Children[0] is Button b) BeginCapture(b, a);
        Raise();
    }

    private void Raise() { if (!_building) Changed?.Invoke(); }
}
