using System;
using System.Collections.Generic;
using System.Linq;
using ActionQueue;
using CheatMenu;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Components.Actions;
using GamesByMalcs.Melvor2.Entities;
using Godot;

namespace Multitask;

/// <summary>The F5 window: one row per slot - on/off, the skill, the action, and what the slot is doing.</summary>
public sealed class SlotWindow
{
    sealed class Row
    {
        public Button Switch = new() { ToggleMode = true, CustomMinimumSize = new Vector2(64, 0) };
        public OptionButton Skill = new() { CustomMinimumSize = new Vector2(170, 0) };
        public OptionButton Target = new() { CustomMinimumSize = new Vector2(300, 0), FitToLongestItem = false, ClipText = true, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        public Label State = Style.Label("", Style.Muted, 12);
        public List<Target> Targets = new();
    }

    readonly CanvasLayer _layer = new() { Layer = 118, Name = "Multitask", Visible = false };
    readonly Label _status = Style.Label("", Style.Muted);
    readonly List<Row> _rows = new();
    Game? _game;
    List<(GameEntity Entity, ActionComponent Action)> _skills = new();
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

        var panel = new PanelContainer { CustomMinimumSize = new Vector2(940, 0), Theme = Style.Build() };
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
        Label hint = Style.Label("Extra actions that keep running beside the one you choose in the game.", Style.Muted);
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

        for (int i = 0; i < Slots.Count; i++) body.AddChild(BuildRow(i));

        Label note = Style.Label(
            "How it works: start your main action in the game as usual. Each slot that is ON keeps its own action going next to it.\n" +
            "Whenever you start something in the game, the game stops everything for a moment; the slots come back within a second.\n" +
            "A slot waits when its materials run out and goes on when there are some again. A fighting slot turns itself off if your character dies.",
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

    Control BuildRow(int index)
    {
        var row = new Row();
        _rows.Add(row);
        var card = new PanelContainer();
        card.AddThemeStyleboxOverride("panel", Style.Box(Style.Card, 5, null, 0, 12, 10));
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 6);
        card.AddChild(box);

        var line = new HBoxContainer();
        line.AddThemeConstantOverride("separation", 10);
        Label name = Style.Label($"Slot {index + 1}", Style.Text);
        name.CustomMinimumSize = new Vector2(52, 0);
        line.AddChild(name);
        StyleBoxFlat on = Style.Box(Style.GoodDim, 5, Style.Good, 1, 12, 5);
        row.Switch.AddThemeStyleboxOverride("pressed", on);
        row.Switch.AddThemeStyleboxOverride("hover_pressed", on);
        row.Switch.TooltipText = "ON: this action is kept running. OFF: it is stopped and left alone.";
        row.Switch.Toggled += pressed => { if (!_syncing) Slots.SetOn(index, pressed); };
        line.AddChild(row.Switch);
        row.Skill.ItemSelected += _ => { if (!_syncing) { FillTargets(index, null); Apply(index); } };
        line.AddChild(row.Skill);
        row.Target.ItemSelected += _ => { if (!_syncing) Apply(index); };
        line.AddChild(row.Target);
        var useNow = new Button { Text = "Use what I am doing now" };
        useNow.TooltipText = "Moves your current action into this slot. Then start something else in the game: both will run.";
        useNow.Pressed += () => UseCurrent(index);
        line.AddChild(useNow);
        box.AddChild(line);

        row.State.ClipText = true;
        box.AddChild(row.State);
        return card;
    }

    public void Show(Game game)
    {
        if (!ReferenceEquals(_game, game))
        {
            _game = game;
            _skills = Targets.Skills(game);
            _syncing = true;
            for (int i = 0; i < _rows.Count; i++)
            {
                Row row = _rows[i];
                row.Skill.Clear();
                foreach (var skill in _skills) row.Skill.AddItem(skill.Entity.DisplayName.Text);
                Slot slot = Slots.All[i];
                int at = Math.Max(0, _skills.FindIndex(s => s.Entity.Id == slot.Entity));
                if (row.Skill.ItemCount > 0) row.Skill.Select(at);
                FillTargets(i, slot.TargetId == "" ? null : slot);
            }
            _syncing = false;
        }
        Repaint();
        _layer.Visible = true;
    }

    public void Hide() => _layer.Visible = false;

    void FillTargets(int index, Slot? select)
    {
        Row row = _rows[index];
        if (row.Skill.Selected < 0 || row.Skill.Selected >= _skills.Count) return;
        row.Targets = Targets.All(_skills[row.Skill.Selected].Action);
        bool was = _syncing;
        _syncing = true;
        row.Target.Clear();
        foreach (Target t in row.Targets) row.Target.AddItem(t.Name);
        int at = select == null ? 0 : Math.Max(0, row.Targets.FindIndex(t => t.Id == select.TargetId && t.Extra == select.Extra));
        if (row.Target.ItemCount > 0) row.Target.Select(at);
        _syncing = was;
    }

    void Apply(int index)
    {
        Row row = _rows[index];
        if (row.Skill.Selected < 0 || row.Skill.Selected >= _skills.Count || row.Target.Selected < 0 || row.Target.Selected >= row.Targets.Count) return;
        Slots.Set(index, _skills[row.Skill.Selected].Entity, row.Targets[row.Target.Selected]);
    }

    void UseCurrent(int index)
    {
        if (_game == null) return;
        ActionComponent? active = Targets.Main(_game);
        Target? target = active == null ? null : Targets.Current(active);
        int skill = active == null ? -1 : _skills.FindIndex(s => ReferenceEquals(s.Action, active));
        if (target == null || skill < 0) { _status.Text = "Start an action in the game first, then press this."; return; }
        Choose(index, _skills[skill].Entity.Id, target.Id, target.Extra);
        Slots.SetOn(index, true);
        _status.Text = $"Slot {index + 1} now keeps {_skills[skill].Entity.DisplayName.Text}: {target.Name}. Start your next action in the game.";
    }

    /// <summary>Puts a skill and one of its actions into a slot the way the two lists do (also used by tests).</summary>
    public bool Choose(int index, string entityId, string targetId, string extra = "")
    {
        if (index < 0 || index >= _rows.Count) return false;
        int skill = _skills.FindIndex(s => s.Entity.Id == entityId);
        if (skill < 0) return false;
        Row row = _rows[index];
        _syncing = true;
        row.Skill.Select(skill);
        _syncing = false;
        FillTargets(index, new Slot { TargetId = targetId, Extra = extra });
        if (row.Target.Selected < 0 || row.Targets[row.Target.Selected].Id != targetId) return false;
        Apply(index);
        return true;
    }

    /// <summary>For tests: what a row shows.</summary>
    public string RowText(int index) => $"{(_rows[index].Switch.ButtonPressed ? "ON" : "OFF")} | {_rows[index].State.Text}";

    void Repaint()
    {
        _syncing = true;
        for (int i = 0; i < _rows.Count && i < Slots.All.Count; i++)
        {
            Slot slot = Slots.All[i];
            Row row = _rows[i];
            row.Switch.ButtonPressed = slot.On;
            row.Switch.Text = slot.On ? "ON" : "OFF";
            string state = slot.State != "" ? slot.State : slot.On ? "starting..." : slot.HasTarget ? "off" : "off - pick a skill and an action, then switch it on";
            row.State.Text = slot.HasTarget ? $"{slot.Title}   -   {state}" : state;
            Color color = slot.State == "running" ? Style.Good : slot.State.StartsWith("turned off") ? Style.Bad : slot.State.StartsWith("waiting") ? Style.Accent : Style.Muted;
            row.State.AddThemeColorOverride("font_color", color);
        }
        _syncing = false;
        int running = Slots.All.Count(s => s.State == "running");
        _status.Text = running == 0 ? "No slot is running." : running == 1 ? "1 slot is running beside your main action." : $"{running} slots are running beside your main action.";
    }
}
