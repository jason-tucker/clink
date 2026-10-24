using System.Windows;
using System.Windows.Controls;
using Clink.Services;
using Clink.UI;

namespace Clink.Tests;

/// <summary>
/// The Capture tab's hotkey list (main hotkey plus shorter ones): the pure helpers that decide which lengths a row offers, the
/// editor driven through its buttons (it needs no window), and the rule behind the Alt-chord menu mask.
/// </summary>
internal static class HotkeyEditorTests
{
    private static HotkeyAction Row(int vk, string action, int seconds, bool alt = false, bool shift = false, bool ctrl = false) =>
        new() { Keys = new HotkeyBinding { Vk = vk, Alt = alt, Shift = shift, Ctrl = ctrl }, Action = action, Seconds = seconds };

    private static void Click(Button b) => b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    // ------------------------------------------------------------------ pure helpers

    [UnitTest]
    public static void Additional_lengths_are_the_scale_strictly_below_the_replay_length()
    {
        Check.Equal(new[] { 5, 10, 15, 20, 30 }, HotkeyListEditor.AdditionalLengths(45));
        Check.Equal(Array.Empty<int>(), HotkeyListEditor.AdditionalLengths(5));
        Check.Equal(new[] { 5, 10, 15, 20, 30, 45, 60, 90, 120, 180, 300, 600, 900, 1200, 1800, 2700 }, HotkeyListEditor.AdditionalLengths(3600));
        // strictly below: the replay length itself is the main hotkey's
        Check.Equal(new[] { 5, 10, 15, 20 }, HotkeyListEditor.AdditionalLengths(30));
        Check.Equal(new[] { 5, 10, 15, 20, 30 }, HotkeyListEditor.AdditionalLengths(31));
        Check.Equal(Array.Empty<int>(), HotkeyListEditor.AdditionalLengths(0));
        for (int replay = 5; replay <= 3600; replay += 7)
        {
            var lengths = HotkeyListEditor.AdditionalLengths(replay);
            Check.True(lengths.All(l => l < replay), $"every length is under {replay} s");
            Check.Equal(lengths.OrderBy(l => l).ToArray(), lengths, $"ascending at {replay} s");
        }
    }

    [UnitTest]
    public static void A_new_hotkey_starts_on_the_longest_of_30_15_10_5_below_the_replay()
    {
        Check.Equal(30, HotkeyListEditor.DefaultNewLength(45));
        Check.Equal(30, HotkeyListEditor.DefaultNewLength(3600));
        Check.Equal(30, HotkeyListEditor.DefaultNewLength(31));
        Check.Equal(15, HotkeyListEditor.DefaultNewLength(30), "strictly below 30 s");
        Check.Equal(15, HotkeyListEditor.DefaultNewLength(16));
        Check.Equal(10, HotkeyListEditor.DefaultNewLength(15));
        Check.Equal(5, HotkeyListEditor.DefaultNewLength(10));
        Check.Equal(5, HotkeyListEditor.DefaultNewLength(6));
        Check.Equal(5, HotkeyListEditor.DefaultNewLength(5), "nothing is shorter than the smallest replay: 5 s");
        Check.Equal(5, HotkeyListEditor.DefaultNewLength(0));
        // whenever a shorter length exists, the row starts on one of the lengths its combo offers
        for (int replay = 6; replay <= 3600; replay += 5)
            Check.True(HotkeyListEditor.AdditionalLengths(replay).Contains(HotkeyListEditor.DefaultNewLength(replay)), $"offered at {replay} s");
    }

    [UnitTest]
    public static void The_length_combo_lists_the_shorter_lengths_and_keeps_what_the_row_saved()
    {
        var ordinary = HotkeyListEditor.LengthChoices(45, 30);
        Check.Equal(new[] { 5, 10, 15, 20, 30 }, ordinary.Select(c => c.Value).ToArray());
        Check.Equal(new[] { "5 s", "10 s", "15 s", "20 s", "30 s" }, ordinary.Select(c => c.Label).ToArray());

        // a row from before the main hotkey that saves the full replay, shown as that
        var legacy = HotkeyListEditor.LengthChoices(45, 0);
        Check.Equal(new[] { 5, 10, 15, 20, 30, 0 }, legacy.Select(c => c.Value).ToArray());
        Check.Equal("Full replay (0:45)", legacy[^1].Label);

        // at or above the replay length: kept, marked as capped (the buffer holds no more), after the shorter ones
        var capped = HotkeyListEditor.LengthChoices(45, 600);
        Check.Equal((600, "10:00 (capped to replay)"), capped[^1]);
        Check.Equal(6, capped.Count);
        Check.Equal((45, "45 s (capped to replay)"), HotkeyListEditor.LengthChoices(45, 45)[^1]);

        // a hand-edited value under the replay that is not on the scale: shown as it is, in order
        var odd = HotkeyListEditor.LengthChoices(45, 25);
        Check.Equal(new[] { 5, 10, 15, 20, 25, 30 }, odd.Select(c => c.Value).ToArray());
        Check.Equal("25 s", odd.First(c => c.Value == 25).Label);

        // the smallest replay has nothing shorter: the full replay is the only entry, whatever length the row has saved
        Check.Equal(new[] { (0, "Full replay (0:05)") }, HotkeyListEditor.LengthChoices(5, 0).ToArray());
        Check.Equal(new[] { (5, "Full replay (0:05)") }, HotkeyListEditor.LengthChoices(5, 5).ToArray());
        Check.Equal(new[] { (600, "Full replay (0:05)") }, HotkeyListEditor.LengthChoices(5, 600).ToArray());
        Check.Equal(new[] { (3, "3 s") }, HotkeyListEditor.LengthChoices(5, 3).ToArray());   // under the replay, so it is not the full replay
        Check.Equal((0, "Full replay (1:00:00)"), HotkeyListEditor.LengthChoices(3600, 0)[^1]);
        Check.Equal(0, HotkeyListEditor.LengthChoices(45, -3)[^1].Value, "a negative length is the full replay too");
        foreach (int replay in new[] { 5, 45, 3600 })
            foreach (int current in new[] { -1, 0, 5, 25, 30, 45, 600, 3600 })
                Check.True(HotkeyListEditor.LengthChoices(replay, current).Count > 0, $"never empty ({replay} s replay, {current} s saved)");
    }

    [UnitTest]
    public static void The_length_tooltip_spells_out_what_the_closed_box_cuts_short()
    {
        Check.Contains(HotkeyListEditor.LengthTip(45, 0), "full replay (0:45)");
        Check.Contains(HotkeyListEditor.LengthTip(45, 600), "replay is 0:45 long");
        Check.Contains(HotkeyListEditor.LengthTip(45, 600), "10:00 you set");
        Check.Contains(HotkeyListEditor.LengthTip(45, 45), "saves all of it");
        Check.Contains(HotkeyListEditor.LengthTip(45, 15), "shorter than the replay length");
        Check.DoesNotContain(HotkeyListEditor.LengthTip(45, 15), "capped");
    }

    // ------------------------------------------------------------------ the editor, through its buttons

    [UnitTest]
    public static void The_editor_shows_the_main_hotkey_on_top_and_the_other_hotkeys_as_rows()
    {
        var main = Row(0x43, "clip", 0, alt: true);
        var shorter = Row(0x78, "clip", 15, shift: true);
        var pause = Row(0x79, "pause", 0);
        var ed = new HotkeyListEditor();
        ed.Load(new[] { main, shorter, pause }, 45);

        Check.Equal(3, ed.Actions.Count);
        Check.True(ReferenceEquals(main, ed.Actions[0]), "the main is the first action");
        Check.Equal("Alt+C", ed.MainKeys.Content);
        Check.Equal("saves the full replay (0:45)", ed.MainHint.Text);
        Check.True(ed.MainClear.IsEnabled, "a main with keys can be cleared");
        Check.Equal(2, ed.Rows.Children.Count);
        Check.Equal(Visibility.Collapsed, ed.EmptyText.Visibility);

        // the shorter clip row: [keys] [action] [length] [remove], the length combo offers lengths under the replay and shows 15 s
        var grid = (Grid)ed.Rows.Children[0];
        var length = (ComboBox)grid.Children[2];
        Check.Equal("15 s", length.SelectedItem?.ToString());
        Check.Equal(5, length.Items.Count);
        // the pause row has no length
        Check.Equal(Visibility.Collapsed, ((ComboBox)((Grid)ed.Rows.Children[1]).Children[2]).Visibility);

        ed.DefaultSeconds = 90;
        Check.Equal("saves the full replay (1:30)", ed.MainHint.Text);
        Check.Equal(7, ((ComboBox)((Grid)ed.Rows.Children[0]).Children[2]).Items.Count);   // 5 .. 60 under 1:30
        Check.True(ReferenceEquals(main, ed.Actions[0]) && ReferenceEquals(shorter, ed.Actions[1]), "the rows are the same objects");

        // a replay no longer than the row's length: the row says it is capped (and the tooltip says why), its saved length is kept
        ed.DefaultSeconds = 10;
        var capped = (ComboBox)((Grid)ed.Rows.Children[0]).Children[2];
        Check.Equal("15 s (capped to replay)", capped.SelectedItem?.ToString());
        Check.Contains(capped.ToolTip as string, "saves all of it");
        Check.Equal(15, shorter.Seconds);
    }

    [UnitTest]
    public static void A_list_without_a_main_hotkey_gets_an_unset_one_in_front()
    {
        var shorter = Row(0x78, "clip", 15);
        var ed = new HotkeyListEditor();
        ed.Load(new[] { shorter }, 45);
        Check.Equal(2, ed.Actions.Count);
        Check.Equal(0, ed.Actions[0].Keys.Vk);
        Check.Equal("clip", ed.Actions[0].Action);
        Check.Equal(0, ed.Actions[0].Seconds);
        Check.True(ReferenceEquals(shorter, ed.Actions[1]));
        Check.Equal("Not set", ed.MainKeys.Content);
        Check.False(ed.MainClear.IsEnabled, "nothing to clear");

        ed.Load(Array.Empty<HotkeyAction>(), 45);
        Check.Equal(1, ed.Actions.Count);
        Check.Equal(Visibility.Visible, ed.EmptyText.Visibility);
        Check.Equal(0, ed.Rows.Children.Count);
    }

    [UnitTest]
    public static void The_main_hotkey_can_be_cleared_but_not_removed()
    {
        var main = Row(0x43, "clip", 0, alt: true);
        var ed = new HotkeyListEditor();
        ed.Load(new[] { main, Row(0x78, "clip", 15) }, 45);
        int changed = 0;
        ed.Changed += () => changed++;

        Click(ed.MainClear);
        Check.Equal(1, changed, "clearing is a change");
        Check.Equal(2, ed.Actions.Count, "the main stays in the list");
        Check.True(ReferenceEquals(main, ed.Actions[0]));
        Check.Equal(0, ed.Actions[0].Keys.Vk);
        Check.False(ed.Actions[0].Keys.Alt, "no modifier is left over");
        Check.Equal("clip", ed.Actions[0].Action);
        Check.Equal(0, ed.Actions[0].Seconds);
        Check.Equal("Not set", ed.MainKeys.Content);
        Check.False(ed.MainClear.IsEnabled);
        Check.Equal(1, ed.Rows.Children.Count, "the other row is untouched");
    }

    [UnitTest]
    public static void Add_hotkey_adds_a_shorter_clip_row_and_waits_for_its_keys()
    {
        var ed = new HotkeyListEditor();
        ed.Load(new[] { Row(0x43, "clip", 0, alt: true) }, 45);
        Check.Equal(Visibility.Visible, ed.EmptyText.Visibility);
        int changed = 0;
        ed.Changed += () => changed++;
        try
        {
            Click(ed.AddButton);
            Check.Equal(2, ed.Actions.Count);
            var added = ed.Actions[1];
            Check.Equal("clip", added.Action);
            Check.Equal(30, added.Seconds);
            Check.Equal(0, added.Keys.Vk);
            Check.Equal(1, ed.Rows.Children.Count);
            Check.Equal(Visibility.Collapsed, ed.EmptyText.Visibility);
            Check.Equal("Press keys", ((Button)((Grid)ed.Rows.Children[0]).Children[0]).Content, "the new row is waiting for keys");
            Check.True(changed >= 1);

            // a shorter replay starts the next row on a shorter length; the longest of 30 / 15 / 10 / 5 under it
            ed.DefaultSeconds = 20;
            Click(ed.AddButton);
            Check.Equal(15, ed.Actions[2].Seconds);
            ed.DefaultSeconds = 5;
            Click(ed.AddButton);
            Check.Equal(5, ed.Actions[3].Seconds);
            Check.Equal(4, ed.Actions.Count);
        }
        finally { ed.CancelCapture(); }
    }

    [UnitTest]
    public static void A_row_switched_to_clip_gets_a_fixed_length_and_a_removed_row_goes()
    {
        var ed = new HotkeyListEditor();
        var legacy = Row(0x79, "pause", 0);
        ed.Load(new[] { Row(0x43, "clip", 0, alt: true), legacy, Row(0x7A, "share-last", 0) }, 45);
        int changed = 0;
        ed.Changed += () => changed++;

        var grid = (Grid)ed.Rows.Children[0];
        var action = (ComboBox)grid.Children[1];
        action.SelectedIndex = 0;   // Clip
        Check.Equal("clip", legacy.Action);
        Check.Equal(30, legacy.Seconds, "a clip row starts on a fixed length, not on the full replay");
        Check.Equal(Visibility.Visible, ((ComboBox)grid.Children[2]).Visibility);
        Check.Equal("30 s", ((ComboBox)grid.Children[2]).SelectedItem?.ToString());
        Check.True(changed >= 1);

        Click((Button)((Grid)ed.Rows.Children[0]).Children[3]);   // remove
        Check.Equal(2, ed.Actions.Count);
        Check.False(ed.Actions.Contains(legacy));
        Check.Equal("share-last", ed.Actions[1].Action);
    }

    [UnitTest]
    public static void A_row_can_be_switched_to_start_stop_recording_and_a_saved_record_row_is_kept()
    {
        var ed = new HotkeyListEditor();
        var row = Row(0x79, "pause", 0);
        ed.Load(new[] { Row(0x43, "clip", 0, alt: true), row, Row(0x7A, "record", 0) }, 45);
        var kept = (ComboBox)((Grid)ed.Rows.Children[1]).Children[1];
        Check.Equal("Start / stop recording", kept.SelectedItem?.ToString(), "a saved record row shows its action");
        Check.Equal("record", ed.Actions[2].Action);

        var action = (ComboBox)((Grid)ed.Rows.Children[0]).Children[1];
        action.SelectedIndex = 3;
        Check.Equal("record", row.Action);
    }

    // ------------------------------------------------------------------ the Alt-chord menu mask

    [UnitTest]
    public static void A_key_swallowed_with_Alt_but_without_Ctrl_gets_the_menu_mask()
    {
        Check.True(Hotkeys.NeedsMenuMask(ctrl: false, alt: true), "Alt+C");
        Check.False(Hotkeys.NeedsMenuMask(ctrl: true, alt: true), "Ctrl+Alt+C never opens the menu bar");
        Check.False(Hotkeys.NeedsMenuMask(ctrl: false, alt: false), "F9");
        Check.False(Hotkeys.NeedsMenuMask(ctrl: true, alt: false), "Ctrl+F9");
    }
}
