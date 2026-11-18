using System.Collections.Generic;
using System.Linq;
using CheatMenu;
using GamesByMalcs.Melvor2;
using Godot;

namespace Multitask;

/// <summary>The F5 window: one on/off switch per slot, and a list of what is running together right now.</summary>
public sealed class SlotWindow
{
    readonly CanvasLayer _layer = new() { Layer = 118, Name = "Multitask", Visible = false };
    readonly Label _status = Style.Label("", Style.Muted);
    readonly List<Button> _switches = new();
    readonly List<Label> _lines = new();
    readonly Label _summary = Style.Label("", Style.Text);
    bool _syncing;

    public bool Visible => _layer.Visible;
    public Control Panel { get; }

    public SlotWindow(SceneTree tree)
    {
        var shade = new ColorRect { Color = new Color(0, 0, 0, 0.25f), MouseFilter = Control.MouseFilterEnum.Ignore };
        shade.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _layer.AddChild(shade);
        var center = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _layer.AddChild(center);

        var panel = new PanelContainer { CustomMinimumSize = new Vector2(760, 0), Theme = Style.Build() };
        StyleBoxFlat frame = Style.Box(Style.Window, 8, Style.AccentDim, 1);
        frame.ShadowColor = new Color(0, 0, 0, 0.55f);
        frame.ShadowSize = 14;
        panel.AddThemeStyleboxOverride("panel", frame);
        center.AddChild(panel);
        Panel = panel;

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 0);
        panel.AddChild(root);

        // ---- header
        StyleBoxFlat headBox = Style.Box(Style.Sidebar, 0, null, 0, 18, 12);
        headBox.CornerRadiusTopLeft = headBox.CornerRadiusTopRight = 7;
        headBox.BorderColor = Style.Line;
        headBox.BorderWidthBottom = 1;
        var head = new PanelContainer();
        head.AddThemeStyleboxOverride("panel", headBox);
        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 12);
        header.AddChild(Style.Label("MULTITASK", Style.Accent, 20));
        Label hint = Style.Label("Start something new and what you were doing keeps running.", Style.Muted);
        hint.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        hint.VerticalAlignment = VerticalAlignment.Center;
        hint.SizeFlagsVertical = Control.SizeFlags.Fill;
        hint.ClipText = true;
        header.AddChild(hint);
        var close = new Button { Text = "Close  (F5)" };
        close.Pressed += Hide;
        header.AddChild(close);
        head.AddChild(header);
        root.AddChild(head);

        // ---- body
        var margin = new MarginContainer();
        foreach (string m in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(m, 16);
        var body = new VBoxContainer();
        body.AddThemeConstantOverride("separation", 10);
        margin.AddChild(body);
        root.AddChild(margin);

        // -- the switches
        var switchCard = new PanelContainer();
        switchCard.AddThemeStyleboxOverride("panel", Style.Box(Style.Card, 5, null, 0, 12, 10));
        var switchRow = new HBoxContainer();
        switchRow.AddThemeConstantOverride("separation", 10);
        for (int i = 0; i < Slots.Count; i++)
        {
            int slot = i;
            switchRow.AddChild(Style.Label($"Slot {i + 1}", Style.Text));
            var button = new Button { ToggleMode = true, CustomMinimumSize = new Vector2(70, 0) };
            StyleBoxFlat on = Style.Box(Style.GoodDim, 5, Style.Good, 1, 12, 5);
            button.AddThemeStyleboxOverride("pressed", on);
            button.AddThemeStyleboxOverride("hover_pressed", on);
            button.TooltipText = "ON: one more of your recent actions keeps running. OFF: it is stopped.";
            button.Toggled += pressed => { if (!_syncing) Slots.SetOn(slot, pressed); };
            _switches.Add(button);
            switchRow.AddChild(button);
            switchRow.AddChild(new Control { CustomMinimumSize = new Vector2(14, 0) });
        }
        _summary.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _summary.HorizontalAlignment = HorizontalAlignment.Right;
        switchRow.AddChild(_summary);
        switchCard.AddChild(switchRow);
        body.AddChild(switchCard);

        // -- what runs together now
        var listCard = new PanelContainer();
        listCard.AddThemeStyleboxOverride("panel", Style.Box(Style.Card, 5, null, 0, 12, 10));
        var list = new VBoxContainer();
        list.AddThemeConstantOverride("separation", 6);
        list.AddChild(Style.Label("Running together right now (your latest first):", Style.Muted, 12));
        for (int i = 0; i < Slots.Count + 1; i++)
        {
            Label line = Style.Label("", Style.Text);
            line.ClipText = true;
            _lines.Add(line);
            list.AddChild(line);
        }
        listCard.AddChild(list);
        body.AddChild(listCard);

        Label note = Style.Label(
            "Nothing to pick here. Play as usual: when you start a new action, the one before it keeps going next to it.\n" +
            "One slot on: your last two actions run together. Both on: your last three.\n" +
            "To drop an action, press its own Stop button in the game. A skill can only do one thing: choose another rock or recipe and it just switches.\n" +
            "An action waits when its materials run out and goes on when there are some again. A fight is dropped if your character dies.",
            Style.Muted, 12);
        note.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        body.AddChild(note);

        // ---- footer
        StyleBoxFlat footBox = Style.Box(Style.Sidebar, 0, null, 0, 18, 8);
        footBox.CornerRadiusBottomLeft = footBox.CornerRadiusBottomRight = 7;
        footBox.BorderColor = Style.Line;
        footBox.BorderWidthTop = 1;
        var foot = new PanelContainer();
        foot.AddThemeStyleboxOverride("panel", footBox);
        _status.ClipText = true;
        _status.CustomMinimumSize = new Vector2(0, 20);
        foot.AddChild(_status);
        root.AddChild(foot);

        Slots.Changed += Repaint;
        tree.Root.AddChild(_layer);
    }

    public void Show(Game game)
    {
        Repaint();
        _layer.Visible = true;
    }

    public void Hide() => _layer.Visible = false;

    /// <summary>For tests: press a slot's switch the way a click does, and read what the window shows.</summary>
    public void PressSwitch(int slot) => _switches[slot].ButtonPressed = !_switches[slot].ButtonPressed;
    public string SwitchText(int slot) => _switches[slot].Text;
    public string LineText(int index) => _lines[index].Text;
    public string StatusText => _status.Text;

    void Repaint()
    {
        _syncing = true;
        for (int i = 0; i < _switches.Count; i++)
        {
            _switches[i].ButtonPressed = Slots.IsOn(i);
            _switches[i].Text = Slots.IsOn(i) ? "ON" : "OFF";
        }
        _syncing = false;

        int room = Slots.Capacity;
        _summary.Text = room == 1 ? "One action at a time (the plain game)." : $"Up to {room} actions at the same time.";
        _summary.AddThemeColorOverride("font_color", room == 1 ? Style.Muted : Style.Good);

        IReadOnlyList<Kept> recent = Slots.Recent;
        for (int i = 0; i < _lines.Count; i++)
        {
            Label line = _lines[i];
            line.Visible = i < room;
            if (i >= recent.Count)
            {
                line.Text = i == 0 ? "1.  (nothing yet: start an action in the game)" : $"{i + 1}.  (free: the action before your next one will go here)";
                line.AddThemeColorOverride("font_color", Style.Muted);
                continue;
            }
            Kept kept = recent[i];
            string state = kept.State == "" ? "starting..." : kept.State;
            line.Text = $"{i + 1}.  {kept.Title}   -   {state}";
            line.AddThemeColorOverride("font_color", kept.State == "running" ? Style.Good : kept.State.StartsWith("waiting") ? Style.Accent : Style.Text);
        }

        int running = recent.Count(k => k.State == "running");
        _status.Text = Slots.Note != "" ? Slots.Note
            : room == 1 ? "Both slots are off."
            : running <= 1 ? "Start another action in the game: the one you are doing now will keep running."
            : $"{running} actions are running together.";
    }
}
