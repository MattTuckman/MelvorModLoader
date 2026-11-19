using System;
using System.Collections.Generic;
using CheatMenu;
using Godot;

namespace ActionQueue;

/// <summary>
/// For tests: a look into one of the window's pick lists. The lists themselves are internal (their source file is
/// shared by several mods), so tests see them only through this.
/// </summary>
public sealed class PickProbe
{
    public Func<int> Count = null!;
    public Action<string> SetFilter = null!;
    public Func<List<string>> VisibleKeys = null!, VisibleGroups = null!, FoldedGroups = null!;
    public Func<string> CountText = null!, ButtonText = null!;
    public Func<string, bool> Pick = null!;
    public Action<Key> PressKey = null!;
    public Func<string?> Highlighted = null!, SelectedKey = null!;
    public Action Open = null!, Close = null!;
    public Func<bool> IsOpen = null!;
    public Func<Rect2I> PopupRect = null!;
    /// <summary>Text, group, subgroup, hint and muted of an entry; null when there is no such key.</summary>
    public Func<string, (string Text, string Group, string Subgroup, string Hint, bool Muted)?> Entry = null!;
    public Func<List<string>> Keys = null!;
    /// <summary>How many times the player's pick was reported since this probe was made.</summary>
    public int Picks;

    internal static PickProbe Of<T>(PickList<T> list)
    {
        var probe = new PickProbe();
        probe.Count = () => list.Count;
        probe.SetFilter = list.SetFilter;
        probe.VisibleKeys = () => list.VisibleKeys;
        probe.VisibleGroups = () => list.VisibleGroups;
        probe.FoldedGroups = () => list.FoldedGroups;
        probe.CountText = () => list.CountText;
        probe.ButtonText = () => list.ButtonText;
        probe.Pick = list.Pick;
        probe.PressKey = list.PressKey;
        probe.Highlighted = () => list.HighlightedKey;
        probe.SelectedKey = () => list.SelectedKey;
        probe.Open = list.Open;
        probe.Close = list.Close;
        probe.IsOpen = () => list.IsOpen;
        probe.PopupRect = () => list.PopupRect;
        probe.Entry = key => list.Entry(key) is { } e ? (e.Text, e.Group, e.Subgroup, e.Hint, e.Muted) : null;
        probe.Keys = () => { list.SetFilter(""); return list.VisibleKeys; };
        list.Picked += _ => probe.Picks++;
        return probe;
    }
}
