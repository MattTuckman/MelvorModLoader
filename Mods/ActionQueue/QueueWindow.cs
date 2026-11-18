using System;
using System.Collections.Generic;
using System.Linq;
using CheatMenu;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Components.Actions;
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.Entities;
using Godot;

namespace ActionQueue;

/// <summary>The F6 window: add steps on top (one at a time, or a whole plan for an item), watch the queue run below.</summary>
public sealed class QueueWindow
{
    static readonly string[] UntilNames =
    {
        "it stops by itself", "I own this many of an item", "a skill reaches this level", "this many minutes have passed",
        "only this many of an item are left",
    };

    readonly CanvasLayer _layer = new() { Layer = 118, Name = "ActionQueue", Visible = false };
    readonly Label _status = Style.Label("", Style.Good);
    readonly Label _now = Style.Label("", Style.Muted, 12);
    readonly OptionButton _skillPick = new() { CustomMinimumSize = new Vector2(170, 0) };
    readonly OptionButton _targetPick = new() { CustomMinimumSize = new Vector2(300, 0), FitToLongestItem = false, ClipText = true, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
    readonly OptionButton _untilPick = new() { CustomMinimumSize = new Vector2(250, 0) };
    readonly SpinBox _amount = new() { MinValue = 1, MaxValue = 100_000_000, Step = 1, Value = 1000, CustomMinimumSize = new Vector2(120, 0), SelectAllOnFocus = true };
    readonly LineEdit _itemSearch = new() { PlaceholderText = "Search item...", CustomMinimumSize = new Vector2(140, 0), ClearButtonEnabled = true };
    readonly OptionButton _itemPick = new() { CustomMinimumSize = new Vector2(210, 0), FitToLongestItem = false, ClipText = true };
    readonly OptionButton _levelPick = new() { CustomMinimumSize = new Vector2(170, 0) };
    readonly Tree _table;
    readonly Button _run = new() { CustomMinimumSize = new Vector2(150, 0) };
    readonly Button _repeat = new() { ToggleMode = true, CustomMinimumSize = new Vector2(230, 0) };

    // The card on top has two faces: the form for one step, and the planner.
    readonly Button _stepMode = new() { Text = "Add one step", ToggleMode = true };
    readonly Button _planMode = new() { Text = "Plan an item", ToggleMode = true };
    readonly VBoxContainer _stepForm = new(), _planForm = new() { Visible = false };
    readonly OptionButton _planKind = new() { CustomMinimumSize = new Vector2(132, 0) };
    readonly SpinBox _planAmount = new() { MinValue = 1, MaxValue = 100_000_000, Step = 1, Value = 100, CustomMinimumSize = new Vector2(120, 0), SelectAllOnFocus = true };
    readonly LineEdit _planSearch = new() { PlaceholderText = "Search item...", CustomMinimumSize = new Vector2(150, 0), ClearButtonEnabled = true };
    readonly OptionButton _planPick = new() { CustomMinimumSize = new Vector2(220, 0), FitToLongestItem = false, ClipText = true };
    readonly Label _planNote = Style.Label("", Style.Muted, 12);
    readonly Button _planAdd = new() { Text = "Add these steps to the queue" };
    Tree _planTable = null!;
    List<Item> _makeable = new(), _shownMakeable = new();
    PlanResult? _plan;
    string _planShown = "";

    // The third face of the card: queues kept under a name, and share codes.
    readonly Button _savedMode = new() { Text = "Saved queues", ToggleMode = true };
    readonly VBoxContainer _savedForm = new() { Visible = false };
    readonly LineEdit _presetName = new() { PlaceholderText = "Name, e.g. Smithing day", CustomMinimumSize = new Vector2(260, 0), MaxLength = 40 };
    readonly LineEdit _code = new() { PlaceholderText = "Share code (AQ1:...)", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClearButtonEnabled = true };
    readonly Button _deletePreset = new() { Text = "Delete" };
    readonly OptionButton _quick = new() { CustomMinimumSize = new Vector2(230, 0), FitToLongestItem = false, ClipText = true };
    Tree _presetTable = null!;
    List<string> _quickNames = new();
    string _armedDelete = "";
    int _armToken;
    SceneTree _tree = null!;

    Game? _game;
    List<(GameEntity Entity, ActionComponent Action)> _skills = new();
    List<Target> _targets = new();
    List<Item> _items = new(), _shownItems = new();
    List<string> _levelSkills = new();
    bool _syncing;
    Until _lastKind;
    double _amountLeft = 5, _amountOther = 1000;   // "only N left" keeps its own number, apart from the other goals

    public bool Visible => _layer.Visible;
    public Control Panel { get; }

    public QueueWindow(SceneTree tree)
    {
        _tree = tree;
        var shade = new ColorRect { Color = new Color(0, 0, 0, 0.25f), MouseFilter = Control.MouseFilterEnum.Ignore };
        shade.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _layer.AddChild(shade);
        var center = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _layer.AddChild(center);

        var panel = new PanelContainer { CustomMinimumSize = new Vector2(1000, 700), Theme = Style.Build() };
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
        header.AddChild(Style.Label("ACTION QUEUE", Style.Accent, 20));
        Label hint = Style.Label("Do this, then that, then that.", Style.Muted);
        hint.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        hint.VerticalAlignment = VerticalAlignment.Center;
        hint.SizeFlagsVertical = Control.SizeFlags.Fill;
        hint.ClipText = true;
        header.AddChild(hint);
        StyleBoxFlat go = Style.Box(Style.GoodDim, 5, Style.Good, 1, 12, 5);
        _run.AddThemeStyleboxOverride("normal", go);
        _run.AddThemeStyleboxOverride("hover", Style.Box(new Color(0.16f, 0.38f, 0.23f), 5, Style.Good, 1, 12, 5));
        _run.Pressed += () => { if (Queue.Running) Queue.Pause("Paused."); else Queue.Start(); };
        _quick.TooltipText = "Switch to one of your saved queues. A running queue keeps running, from the first step of the new one.";
        _quick.ItemSelected += QuickPicked;
        header.AddChild(_quick);
        header.AddChild(_run);
        header.AddChild(MakeButton("Close  (F6)", Hide));
        head.AddChild(header);
        root.AddChild(head);

        // ---- body
        var margin = new MarginContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        foreach (string m in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(m, 16);
        var body = new VBoxContainer();
        body.AddThemeConstantOverride("separation", 8);
        margin.AddChild(body);
        root.AddChild(margin);

        // -- new step
        var card = new PanelContainer();
        card.AddThemeStyleboxOverride("panel", Style.Box(Style.Card, 5, null, 0, 12, 10));
        var form = new VBoxContainer();
        form.AddThemeConstantOverride("separation", 8);
        card.AddChild(form);
        body.AddChild(card);

        var modes = new HBoxContainer();
        modes.AddThemeConstantOverride("separation", 6);
        var group = new ButtonGroup();
        foreach (Button mode in new[] { _stepMode, _planMode, _savedMode })
        {
            mode.ButtonGroup = group;
            StyleBoxFlat picked = Style.Box(Style.AccentDim, 5, Style.Accent, 1, 12, 5);
            mode.AddThemeStyleboxOverride("pressed", picked);
            mode.AddThemeStyleboxOverride("hover_pressed", picked);
            modes.AddChild(mode);
        }
        _stepMode.ButtonPressed = true;
        _stepMode.TooltipText = "Choose one action and when it is over.";
        _planMode.TooltipText = "Say which item you want and how many. The steps to get there are worked out for you.";
        _savedMode.TooltipText = "Keep several queues under a name, switch between them, and share one as a short code.";
        foreach (Button mode in new[] { _stepMode, _planMode, _savedMode }) mode.Toggled += _ => ShowFace();
        Label modeHint = Style.Label("", Style.Muted, 12);
        modeHint.Text = "Build the queue step by step, let a plan work the steps out, or keep whole queues under a name.";
        modeHint.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        modeHint.HorizontalAlignment = HorizontalAlignment.Right;
        modeHint.ClipText = true;
        modes.AddChild(modeHint);
        form.AddChild(modes);
        _stepForm.AddThemeConstantOverride("separation", 8);

        var what = new HBoxContainer();
        what.AddThemeConstantOverride("separation", 10);
        what.AddChild(Fixed("Do", 50));
        _skillPick.ItemSelected += _ => FillTargets(null);
        _targetPick.ItemSelected += _ => SuggestItem();
        what.AddChild(_skillPick);
        what.AddChild(_targetPick);
        Button useNow = MakeButton("Use what I am doing now", UseCurrent);
        useNow.TooltipText = "Fills in the skill and the action your character is performing right now.";
        what.AddChild(useNow);
        _stepForm.AddChild(what);

        var until = new HBoxContainer();
        until.AddThemeConstantOverride("separation", 10);
        until.AddChild(Fixed("until", 50));
        foreach (string name in UntilNames) _untilPick.AddItem(name);
        _untilPick.ItemSelected += _ => UntilChanged();
        until.AddChild(_untilPick);
        until.AddChild(_amount);
        _itemSearch.TextChanged += FilterItems;
        _itemSearch.TooltipText = "Filled in with what the chosen action makes. Type here to count a different item.";
        _itemPick.TooltipText = "The item that is counted in the bank.";
        until.AddChild(_itemSearch);
        until.AddChild(_itemPick);
        until.AddChild(_levelPick);
        until.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        until.AddChild(MakeButton("Add step", AddStep));
        _stepForm.AddChild(until);
        _stepForm.AddChild(_now);
        form.AddChild(_stepForm);
        BuildPlanForm(form);
        BuildSavedForm(form);

        // -- the queue
        _table = new Tree
        {
            Columns = 4, HideRoot = true, SelectMode = Tree.SelectModeEnum.Row, ColumnTitlesVisible = true,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 150)
        };
        string[] titles = { "#", "Do", "Until", "State" };
        for (int c = 0; c < titles.Length; c++)
        {
            _table.SetColumnTitle(c, titles[c]);
            _table.SetColumnTitleAlignment(c, HorizontalAlignment.Left);
            _table.SetColumnExpand(c, c is 1 or 3);
        }
        _table.SetColumnCustomMinimumWidth(0, 44);
        _table.SetColumnCustomMinimumWidth(2, 290);
        body.AddChild(_table);

        var buttons = new HBoxContainer();
        buttons.AddThemeConstantOverride("separation", 10);
        buttons.AddChild(Style.Label("Selected step:", Style.Muted));
        buttons.AddChild(MakeButton("Move up", () => MoveSelected(-1)));
        buttons.AddChild(MakeButton("Move down", () => MoveSelected(1)));
        buttons.AddChild(MakeButton("Remove", () => { int i = SelectedIndex(); if (i >= 0) Queue.RemoveAt(i); }));
        buttons.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        StyleBoxFlat on = Style.Box(Style.GoodDim, 5, Style.Good, 1, 12, 5);
        _repeat.AddThemeStyleboxOverride("pressed", on);
        _repeat.AddThemeStyleboxOverride("hover_pressed", on);
        _repeat.Toggled += pressed => { if (!_syncing) Queue.SetRepeat(pressed); };
        buttons.AddChild(_repeat);
        buttons.AddChild(MakeButton("Remove all", Queue.Clear));
        body.AddChild(buttons);

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

        Queue.Changed += Repaint;
        tree.Root.AddChild(_layer);
    }

    void BuildPlanForm(VBoxContainer form)
    {
        _planForm.AddThemeConstantOverride("separation", 8);
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 10);
        _planKind.AddItem("Make");
        _planKind.AddItem("Own in total");
        _planKind.TooltipText = "Make: this many more than you have now.\nOwn in total: until the bank holds this many (what you already have counts).";
        _planKind.ItemSelected += _ => { _planShown = ""; Replan(); };
        row.AddChild(_planKind);
        _planAmount.ValueChanged += _ => Replan();
        row.AddChild(_planAmount);
        row.AddChild(Style.Label("of", Style.Muted));
        _planSearch.TextChanged += FilterMakeable;
        row.AddChild(_planSearch);
        _planPick.TooltipText = "Every item one of your skills can make or gather.";
        _planPick.ItemSelected += _ => Replan();
        row.AddChild(_planPick);
        row.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        StyleBoxFlat go = Style.Box(Style.GoodDim, 5, Style.Good, 1, 12, 5);
        _planAdd.AddThemeStyleboxOverride("normal", go);
        _planAdd.AddThemeStyleboxOverride("hover", Style.Box(new Color(0.16f, 0.38f, 0.23f), 5, Style.Good, 1, 12, 5));
        _planAdd.Pressed += AddPlan;
        row.AddChild(_planAdd);
        _planForm.AddChild(row);

        _planTable = new Tree
        {
            Columns = 4, HideRoot = true, SelectMode = Tree.SelectModeEnum.Row, ColumnTitlesVisible = true,
            CustomMinimumSize = new Vector2(0, 184)
        };
        string[] titles = { "Step", "Until the bank holds", "In the bank", "Takes about" };
        for (int c = 0; c < titles.Length; c++)
        {
            _planTable.SetColumnTitle(c, titles[c]);
            _planTable.SetColumnTitleAlignment(c, HorizontalAlignment.Left);
            _planTable.SetColumnExpand(c, c is 0 or 1);
        }
        _planTable.SetColumnCustomMinimumWidth(2, 130);
        _planTable.SetColumnCustomMinimumWidth(3, 130);
        _planForm.AddChild(_planTable);
        _planNote.ClipText = true;
        _planForm.AddChild(_planNote);
        form.AddChild(_planForm);
    }

    void BuildSavedForm(VBoxContainer form)
    {
        _savedForm.AddThemeConstantOverride("separation", 8);
        var top = new HBoxContainer();
        top.AddThemeConstantOverride("separation", 10);
        top.AddChild(Fixed("Name", 50));
        _presetName.TextSubmitted += _ => SaveNamed();
        top.AddChild(_presetName);
        Button save = MakeButton("Save the queue below under this name", SaveNamed);
        StyleBoxFlat go = Style.Box(Style.GoodDim, 5, Style.Good, 1, 12, 5);
        save.AddThemeStyleboxOverride("normal", go);
        save.AddThemeStyleboxOverride("hover", Style.Box(new Color(0.16f, 0.38f, 0.23f), 5, Style.Good, 1, 12, 5));
        top.AddChild(save);
        Label tip = Style.Label("To switch quickly, use the list at the top of the window.", Style.Muted, 12);
        tip.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        tip.HorizontalAlignment = HorizontalAlignment.Right;
        tip.ClipText = true;
        top.AddChild(tip);
        _savedForm.AddChild(top);

        _presetTable = new Tree
        {
            Columns = 3, HideRoot = true, SelectMode = Tree.SelectModeEnum.Row, ColumnTitlesVisible = true,
            CustomMinimumSize = new Vector2(0, 150)
        };
        string[] titles = { "Saved queue", "Steps", "What it does" };
        for (int c = 0; c < titles.Length; c++)
        {
            _presetTable.SetColumnTitle(c, titles[c]);
            _presetTable.SetColumnTitleAlignment(c, HorizontalAlignment.Left);
            _presetTable.SetColumnExpand(c, c == 2);
        }
        _presetTable.SetColumnCustomMinimumWidth(0, 250);
        _presetTable.SetColumnCustomMinimumWidth(1, 70);
        _presetTable.ItemSelected += () => { if (SelectedPreset() is { } name) _presetName.Text = name == Queue.AutoName ? "" : name; Disarm(); };
        _presetTable.ItemActivated += LoadSelected;
        _savedForm.AddChild(_presetTable);

        var bottom = new HBoxContainer();
        bottom.AddThemeConstantOverride("separation", 10);
        Button load = MakeButton("Load", LoadSelected);
        load.TooltipText = "Puts the selected queue in place of the one below. A double-click does the same.";
        bottom.AddChild(load);
        _deletePreset.Pressed += DeleteSelected;
        bottom.AddChild(_deletePreset);
        bottom.AddChild(new VSeparator());
        Button copy = MakeButton("Copy share code", CopyCode);
        copy.TooltipText = "Turns the selected saved queue (or, with none selected, the queue below) into a short text and copies it.";
        bottom.AddChild(copy);
        _code.TooltipText = "A share code somebody gave you. Paste it here and press Import.";
        bottom.AddChild(_code);
        Button import = MakeButton("Import", ImportCode);
        import.TooltipText = "Reads the code in the box (or, when the box is empty, the clipboard) and adds it to your saved queues.";
        bottom.AddChild(import);
        _savedForm.AddChild(bottom);
        form.AddChild(_savedForm);
    }

    static Button MakeButton(string text, Action onPressed)
    {
        var b = new Button { Text = text };
        b.Pressed += onPressed;
        return b;
    }

    static Label Fixed(string text, float width)
    {
        Label label = Style.Label(text, Style.Muted);
        label.CustomMinimumSize = new Vector2(width, 0);
        return label;
    }

    static void Fill(OptionButton pick, IEnumerable<string> names, int select = 0)
    {
        pick.Clear();
        foreach (string n in names) pick.AddItem(n);
        if (pick.ItemCount > 0) pick.Select(Math.Clamp(select, 0, pick.ItemCount - 1));
    }

    public void Show(Game game)
    {
        if (!ReferenceEquals(_game, game))
        {
            _game = game;
            _skills = Targets.Skills(game);
            _items = game.Data.GetDataRegistryOutput<Item>().GetAllObjects()
                .OrderBy(i => i.DisplayName.GetPluralText(1), StringComparer.OrdinalIgnoreCase).ToList();
            _levelSkills = game.LeveledEntities.Select(s => s.DisplayName.Text).ToList();
            Fill(_skillPick, _skills.Select(s => s.Entity.DisplayName.Text));
            Fill(_levelPick, _levelSkills);
            FilterItems("");
            FillTargets(null);
            _makeable = Planner.Makeable(game);
            _planSearch.Text = "";
            FilterMakeable("");
        }
        UntilChanged();
        Repaint();
        Pulse();
        _layer.Visible = true;
    }

    public void Hide() => _layer.Visible = false;

    /// <summary>Called about once a second while open.</summary>
    public void Pulse()
    {
        if (_game == null) return;
        ActionComponent? active = Targets.Main(_game);
        Target? target = active == null ? null : Targets.Current(active);
        _now.Text =active == null ? "Your character is doing nothing right now."
            : target == null ? $"Right now: {active.Entity.DisplayName.Text} (the queue cannot drive this one)"
            : $"Right now: {active.Entity.DisplayName.Text}: {target.Name}";
        if (_planForm.Visible) Replan();   // the bank changes while the game runs
    }

    // ---------------------------------------------------------------- the planner

    void ShowFace()
    {
        bool plan = _planMode.ButtonPressed, saved = _savedMode.ButtonPressed;
        bool planWasShown = _planForm.Visible;
        _stepForm.Visible = !plan && !saved;
        _planForm.Visible = plan;
        _savedForm.Visible = saved;
        if (plan && !planWasShown) { _planShown = ""; Replan(); }
        if (saved) PaintSaved();
    }

    // ---------------------------------------------------------------- saved queues and share codes

    string? SelectedPreset() => _presetTable.GetSelected() is { } row ? row.GetMetadata(0).AsString() : null;

    static string Summary(Preset preset)
    {
        string text = string.Join("  >  ", preset.Steps.Take(3).Select(s => s.Name));
        if (preset.Steps.Count > 3) text += $"  >  ... (+{preset.Steps.Count - 3})";
        return preset.Repeat ? text + "   (starts over)" : text;
    }

    void PaintSaved()
    {
        string? selected = SelectedPreset();
        _presetTable.Clear();
        TreeItem root = _presetTable.CreateItem();
        foreach (string name in Queue.PresetNames)
        {
            Preset preset = Queue.GetPreset(name)!;
            TreeItem row = _presetTable.CreateItem(root);
            row.SetMetadata(0, name);
            bool current = name == Queue.Name;
            row.SetText(0, current ? (Queue.Modified ? name + "   (loaded, changed since)" : name + "   (loaded)") : name);
            row.SetText(1, preset.Steps.Count.ToString());
            row.SetText(2, Summary(preset));
            row.SetCustomColor(0, current ? Style.Good : name == Queue.AutoName ? Style.Muted : Style.Text);
            row.SetCustomColor(1, Style.Muted);
            row.SetCustomColor(2, Style.Muted);
            if (name == selected) row.Select(0);
        }
        if (Queue.PresetNames.Count == 0)
        {
            TreeItem row = _presetTable.CreateItem(root);
            row.SetMetadata(0, "");
            row.SetText(0, "Nothing saved yet");
            row.SetText(2, "Build a queue below, type a name above and press Save.");
            row.SetSelectable(0, false); row.SetSelectable(1, false); row.SetSelectable(2, false);
            for (int c = 0; c < 3; c++) row.SetCustomColor(c, Style.Muted);
        }
    }

    void SaveNamed()
    {
        string name = _presetName.Text;
        if (Share.CleanName(name) == "" && Queue.Name != "") name = Queue.Name;   // no name typed: save over the loaded one
        Queue.SavePreset(name, out string message);
        Say(message);
    }

    void LoadSelected()
    {
        Disarm();
        string? name = SelectedPreset();
        if (string.IsNullOrEmpty(name)) { Say("Select a saved queue in the list first."); return; }
        Queue.LoadPreset(name, out string message);
        Say(message);
    }

    void DeleteSelected()
    {
        string? name = SelectedPreset();
        if (string.IsNullOrEmpty(name)) { Say("Select a saved queue in the list first."); return; }
        if (_armedDelete != name)
        {
            // Two clicks, so a slip of the mouse does not throw a queue away.
            _armedDelete = name;
            _deletePreset.Text = "Really delete?";
            int token = ++_armToken;
            _tree.CreateTimer(4).Timeout += () => { if (token == _armToken) Disarm(); };
            Say($"Press Delete again to remove '{name}' for good.");
            return;
        }
        Disarm();
        Queue.DeletePreset(name, out string message);
        Say(message);
    }

    void Disarm()
    {
        _armToken++;
        _armedDelete = "";
        _deletePreset.Text = "Delete";
    }

    void CopyCode()
    {
        string? name = SelectedPreset();
        if (name == "") name = null;
        string? code = Queue.ExportCode(name);
        if (code == null) { Say("There is nothing to share yet: build or select a queue first."); return; }
        _code.Text = code;
        try { DisplayServer.ClipboardSet(code); } catch { }
        Say($"Share code of {(name == null ? "the queue below" : $"'{name}'")} copied ({code.Length} characters). Paste it in a chat; it is also in the box.");
    }

    void ImportCode()
    {
        string text = _code.Text.Trim();
        if (text == "") { try { text = DisplayServer.ClipboardGet(); } catch { text = ""; } }
        if (Queue.ImportCode(text, out string imported, out string message))
        {
            _code.Text = "";
            PaintSaved();
            SelectPreset(imported);
        }
        Say(message);
    }

    void SelectPreset(string name)
    {
        for (TreeItem? row = _presetTable.GetRoot()?.GetFirstChild(); row != null; row = row.GetNext())
            if (row.GetMetadata(0).AsString() == name) { row.Select(0); _presetName.Text = name; return; }
    }

    void QuickPicked(long index)
    {
        if (_syncing || index < 0 || index >= _quickNames.Count) return;
        string name = _quickNames[(int)index];
        if (name == "") return;
        Queue.LoadPreset(name, out string message);
        Say(message);
    }

    void PaintQuick()
    {
        _syncing = true;
        _quick.Clear();
        _quickNames = new List<string>();
        if (Queue.Name == "")
        {
            _quick.AddItem(Queue.PresetNames.Count == 0 ? "Queue: not saved" : "Queue: not saved (pick one)");
            _quickNames.Add("");
        }
        foreach (string name in Queue.PresetNames)
        {
            _quick.AddItem(name == Queue.Name ? (Queue.Modified ? $"Queue: {name} *" : $"Queue: {name}") : name);
            _quickNames.Add(name);
        }
        int at = Queue.Name == "" ? 0 : _quickNames.IndexOf(Queue.Name);
        if (at >= 0) _quick.Select(at);
        _syncing = false;
    }

    /// <summary>For tests: the saved-queues face, its rows, and the controls a player would use.</summary>
    public void ShowSavedFace() => _savedMode.ButtonPressed = true;
    public int SavedRows => Queue.PresetNames.Count == 0 ? 0 : _presetTable.GetRoot()?.GetChildCount() ?? 0;
    public List<string> QuickTexts => Enumerable.Range(0, _quick.ItemCount).Select(i => _quick.GetItemText(i)).ToList();
    public string QuickSelected => _quick.Selected >= 0 ? _quick.GetItemText(_quick.Selected) : "";
    public void PickQuick(string name) { int at = _quickNames.IndexOf(name); if (at >= 0) { _quick.Select(at); QuickPicked(at); } }
    public void TypeNameAndSave(string name) { _presetName.Text = name; SaveNamed(); }
    public void SelectSaved(string name) => SelectPreset(name);
    public void PressLoad() => LoadSelected();
    public void PressDelete() => DeleteSelected();
    public string PressCopy() { CopyCode(); return _code.Text; }
    public void PasteAndImport(string code) { _code.Text = code; ImportCode(); }
    public string StatusText => _status.Text;

    void FilterMakeable(string text)
    {
        text = text.Trim();
        _shownMakeable = (text == "" ? _makeable : _makeable.Where(i => i.DisplayName.GetPluralText(1).Contains(text, StringComparison.OrdinalIgnoreCase)))
            .Take(200).ToList();
        Fill(_planPick, _shownMakeable.Select(i => i.DisplayName.GetPluralText(1)));
        Replan();
    }

    /// <summary>Works the plan out again and redraws it, but only when something in it has changed.</summary>
    void Replan()
    {
        if (_game == null || !_planForm.Visible) return;
        Item? item = _planPick.Selected >= 0 && _planPick.Selected < _shownMakeable.Count ? _shownMakeable[_planPick.Selected] : null;
        if (item == null)
        {
            _plan = null;
            DrawPlan("none", () => { _planNote.Text = "No item with that name can be made by your skills."; });
            return;
        }
        PlanResult plan = _plan = Planner.Compute(_game, item, (long)_planAmount.Value, more: _planKind.Selected == 0);
        string shown = item.Id + "|" + plan.Own + "|" + plan.More + "|" + plan.Problem + "|" + string.Join(";", plan.Steps.Concat(plan.Supply)
            .Select(l => $"{l.Item.Id},{l.Maker?.Target.Id},{l.Need},{l.Have},{l.Actions},{l.Seconds:0},{l.Problem}"));
        DrawPlan(shown, () =>
        {
            TreeItem root = _planTable.CreateItem();
            int number = 0;
            foreach (PlanLine line in plan.Steps)
            {
                TreeItem row = _planTable.CreateItem(root);
                row.SetText(0, $"{++number}.  {line.Maker!.Name}");
                row.SetText(1, $"{line.Need:N0} {line.ItemName}");
                row.SetText(2, line.Have.ToString("N0"));
                row.SetText(3, line.Maker.Seconds > 0 ? Planner.Time(line.Seconds) : "cannot be told");
                row.SetCustomColor(2, Style.Muted);
                row.SetCustomColor(3, Style.Muted);
                if (line.Problem != "")
                {
                    row.SetText(3, "locked");
                    row.SetCustomColor(0, Style.Bad);
                    row.SetCustomColor(3, Style.Bad);
                    for (int c = 0; c < 4; c++) row.SetTooltipText(c, "Locked for now: " + line.Problem + ". The queue skips a step it cannot start.");
                }
            }
            foreach (PlanLine line in plan.Supply)
            {
                TreeItem row = _planTable.CreateItem(root);
                row.SetText(0, "Bring yourself (no skill on the queue makes it)");
                row.SetText(1, $"{line.Need:N0} {line.ItemName}");
                row.SetText(2, line.Have.ToString("N0"));
                for (int c = 0; c < 3; c++) row.SetCustomColor(c, Style.Accent);
            }
            if (plan.Steps.Count == 0 && plan.Problem != "")
            {
                // Nothing to do: say why where the steps would be, not only in the small line below.
                TreeItem row = _planTable.CreateItem(root);
                row.SetText(0, "No steps needed");
                row.SetText(1, plan.Problem);
                row.SetCustomColor(0, Style.Accent);
                row.SetCustomColor(1, Style.Accent);
                row.SetSelectable(0, false); row.SetSelectable(1, false); row.SetSelectable(2, false); row.SetSelectable(3, false);
            }
            _planNote.Text = PlanSummary(plan);
        });
        _planAdd.Disabled = plan.Steps.Count == 0;
    }

    void DrawPlan(string shown, Action draw)
    {
        if (shown == _planShown) return;
        _planShown = shown;
        _planTable.Clear();
        _planAdd.Disabled = true;
        draw();
    }

    static string PlanSummary(PlanResult plan)
    {
        if (plan.Problem != "") return plan.Problem;
        string text = plan.More ? $"To make {plan.Asked:N0} more (the bank will then hold {plan.Own:N0}): " : "";
        text += plan.Steps.Count == 1 ? "1 step" : $"{plan.Steps.Count} steps";
        int timed = plan.Steps.Count - plan.UnknownTimes;
        if (timed > 0) text += $", about {Planner.Time(plan.Seconds)} at normal game speed";
        if (plan.UnknownTimes > 0) text += timed > 0 ? $" plus {plan.UnknownTimes} whose time cannot be told" : ", time cannot be told in advance";
        text += ".";
        if (plan.Money.Count > 0) text += "  Costs " + string.Join(", ", plan.Money.Select(m => $"{m.Value:N0} {m.Key}")) + ".";
        if (plan.Supply.Count > 0) text += "  Yellow lines: items you have to bring yourself.";
        if (plan.Steps.Any(l => l.Problem != "")) text += "  Red lines are still locked.";
        return text;
    }

    void AddPlan()
    {
        if (_plan == null || _plan.Steps.Count == 0) return;
        PlanResult plan = _plan;
        int added = Planner.AddToQueue(plan);
        string itemName = plan.Item.DisplayName.GetPluralText(1);
        Say(plan.More ? $"Plan added: {added} step(s) to make {plan.Asked:N0} more {itemName}." : $"Plan added: {added} step(s) to own {plan.Own:N0} {itemName}.");
        _planShown = "";
        Replan();   // what was just added now counts as "will be made"
    }

    /// <summary>For tests: open the planner, ask for an item and return the plan it shows.</summary>
    public PlanResult? PlanFor(string itemId, long own, bool more = false)
    {
        _planMode.ButtonPressed = true;
        _planKind.Select(more ? 0 : 1);
        _planSearch.Text = "";
        FilterMakeable("");
        int at = _shownMakeable.FindIndex(i => i.Id == itemId);
        if (at < 0)
        {
            Item? item = _makeable.FirstOrDefault(i => i.Id == itemId);
            if (item == null) return null;
            _planSearch.Text = item.DisplayName.GetPluralText(1);
            FilterMakeable(_planSearch.Text);
            at = _shownMakeable.FindIndex(i => i.Id == itemId);
            if (at < 0) return null;
        }
        _planPick.Select(at);
        _planAmount.Value = own;
        _planShown = "";
        Replan();
        return _plan;
    }

    /// <summary>For tests: press "Add these steps to the queue".</summary>
    public void PressAddPlan() => AddPlan();

    /// <summary>For tests: what the planner shows under its table, and how many rows the table has.</summary>
    public string PlanNote => _planNote.Text;
    public int PlanRows => _planTable.GetRoot()?.GetChildCount() ?? 0;

    /// <summary>For tests: back to the one-step form.</summary>
    public void ShowStepForm() => _stepMode.ButtonPressed = true;

    // ---------------------------------------------------------------- the "new step" form

    void FillTargets(string? selectId)
    {
        if (_skillPick.Selected < 0 || _skillPick.Selected >= _skills.Count) return;
        _targets = Targets.All(_skills[_skillPick.Selected].Action);
        int at = selectId == null ? 0 : Math.Max(0, _targets.FindIndex(t => t.Id == selectId));
        Fill(_targetPick, _targets.Select(t => t.Name), at);
        // A level goal is usually about the skill being trained.
        int level = _levelSkills.IndexOf(_skills[_skillPick.Selected].Entity.DisplayName.Text);
        if (level >= 0) _levelPick.Select(level);
        if ((Until)_untilPick.Selected == Until.ItemLeft) SuggestFood(); else SuggestItem();
    }

    /// <summary>
    /// "Until I own N of an item" almost always means the thing the action makes, so that item is filled in
    /// whenever the action changes. Another item can still be searched for and picked.
    /// </summary>
    void SuggestItem()
    {
        if (_skillPick.Selected < 0 || _skillPick.Selected >= _skills.Count || _targetPick.Selected < 0 || _targetPick.Selected >= _targets.Count) return;
        if ((Until)_untilPick.Selected == Until.ItemLeft) return;   // counting down: the item was chosen on purpose
        Item? product = Targets.Product(_skills[_skillPick.Selected].Action, _targets[_targetPick.Selected]);
        if (product == null) return;
        string name = product.DisplayName.GetPluralText(1);
        _itemSearch.Text = name;      // setting the text from code does not raise TextChanged
        FilterItems(name);
        int at = _shownItems.IndexOf(product);
        if (at >= 0) _itemPick.Select(at);
    }

    void SuggestFood()
    {
        if (_game == null || !_game.Entities.TryGetObjectById("MelvorBase:Combat", out GameEntity? e) || e is not GamesByMalcs.Melvor2.Entities.Combat.CombatEntity combat) return;
        // The food chosen for the fight; without one, the food the bank has most of.
        Item? food = combat.Food.SelectedFood?.Item;
        if (food == null && _game.Entities.TryGetObjectById("MelvorBase:Bank", out GameEntity? b) && b is GamesByMalcs.Melvor2.Entities.Bank.BankEntity bank)
            food = bank.Inventory.Stacks.Where(s => s.Quantity > 0 && s.Item.Capabilities.TryGet<GamesByMalcs.Melvor2.Components.CombatFood.FoodCapability>(out _))
                .OrderByDescending(s => s.Quantity).FirstOrDefault()?.Item;
        if (food == null) return;
        string name = food.DisplayName.GetPluralText(1);
        _itemSearch.Text = name;
        FilterItems(name);
        int at = _shownItems.IndexOf(food);
        if (at >= 0) _itemPick.Select(at);
    }

    /// <summary>For tests: pick how a step ends, the way the list does.</summary>
    public void ChooseUntil(Until kind) { _untilPick.Select((int)kind); UntilChanged(); }
    public double AmountShown => _amount.Value;
    public void SetAmount(double value) => _amount.Value = value;

    /// <summary>For tests: choose a skill and one of its actions the way the two lists do.</summary>
    public bool Choose(string entityId, string targetId)
    {
        int skill = _skills.FindIndex(s => s.Entity.Id == entityId);
        if (skill < 0) return false;
        _skillPick.Select(skill);
        FillTargets(targetId);
        return _targetPick.Selected >= 0 && _targets[_targetPick.Selected].Id == targetId;
    }

    /// <summary>For tests: the item the "until I own" goal would count right now.</summary>
    public Item? CountedItem => _itemPick.Selected >= 0 && _itemPick.Selected < _shownItems.Count ? _shownItems[_itemPick.Selected] : null;

    void UseCurrent()
    {
        if (_game == null) return;
        ActionComponent? active = Targets.Main(_game);
        Target? target = active == null ? null : Targets.Current(active);
        int skill = active == null ? -1 : _skills.FindIndex(s => ReferenceEquals(s.Action, active));
        if (target == null || skill < 0) { Say("Start an action in the game first, then press this."); return; }
        _skillPick.Select(skill);
        FillTargets(target.Id);
        Say($"Filled in: {_skills[skill].Entity.DisplayName.Text}: {target.Name}");
    }

    void UntilChanged()
    {
        var kind = (Until)_untilPick.Selected;
        _amount.Visible = kind != Until.Stops;
        _itemSearch.Visible = _itemPick.Visible = kind is Until.Item or Until.ItemLeft;
        _amount.MinValue = kind == Until.ItemLeft ? 0 : 1;
        if (kind == Until.ItemLeft && _lastKind != Until.ItemLeft)
        {
            // Counting down is mostly about food in a fight: offer the food the character is eating, and a small number.
            _amountOther = _amount.Value;
            _amount.Value = _amountLeft;
            SuggestFood();
        }
        else if (kind != Until.ItemLeft && _lastKind == Until.ItemLeft)
        {
            _amountLeft = _amount.Value;
            _amount.Value = Math.Max(1, _amountOther);
            if (kind == Until.Item) SuggestItem();
        }
        _lastKind = kind;
        _levelPick.Visible = kind == Until.Level;
        _amount.Suffix = kind == Until.Minutes ? " min" : "";
        _amount.MaxValue = kind == Until.Level ? 999 : 100_000_000;
        if (kind == Until.Level && _amount.Value > 999) _amount.Value = 99;
    }

    void FilterItems(string text)
    {
        text = text.Trim();
        _shownItems = (text == "" ? _items : _items.Where(i => i.DisplayName.GetPluralText(1).Contains(text, StringComparison.OrdinalIgnoreCase)))
            .Take(150).ToList();
        Fill(_itemPick, _shownItems.Select(i => i.DisplayName.GetPluralText(1)));
    }

    void AddStep()
    {
        if (_skillPick.Selected < 0 || _targetPick.Selected < 0 || _targetPick.Selected >= _targets.Count) return;
        GameEntity entity = _skills[_skillPick.Selected].Entity;
        Target target = _targets[_targetPick.Selected];
        var step = new Step
        {
            Entity = entity.Id, TargetId = target.Id, Extra = target.Extra,
            Name = $"{entity.DisplayName.Text}: {target.Name}",
            Until = (Until)_untilPick.Selected, Amount = _amount.Value,
        };
        if (step.Until is Until.Item or Until.ItemLeft)
        {
            if (_itemPick.Selected < 0 || _itemPick.Selected >= _shownItems.Count) { Say("Pick the item to count first."); return; }
            Item item = _shownItems[_itemPick.Selected];
            step.Subject = item.Id;
            step.SubjectName = item.DisplayName.GetPluralText(1);
        }
        else if (step.Until == Until.Level)
        {
            if (_levelPick.Selected < 0) return;
            step.Subject = step.SubjectName = _levelSkills[_levelPick.Selected];
        }
        Queue.Add(step);
        Say($"Added: {step.Name}, {step.Goal}.");
    }

    // ---------------------------------------------------------------- the list

    int SelectedIndex() => _table.GetSelected() is { } row ? (int)row.GetMetadata(0) : -1;

    void MoveSelected(int by)
    {
        int index = SelectedIndex();
        if (index < 0) return;
        if (Queue.Running) { Say("Pause the queue before changing the order."); return; }
        Queue.Move(index, by);
        SelectRow(index + by);
    }

    void SelectRow(int index)
    {
        TreeItem? row = _table.GetRoot()?.GetFirstChild();
        for (int i = 0; i < index && row != null; i++) row = row.GetNext();
        row?.Select(0);
    }

    void Say(string text) => _status.Text = text;

    void Repaint()
    {
        int selected = SelectedIndex();
        _table.Clear();
        TreeItem root = _table.CreateItem();
        for (int i = 0; i < Queue.Steps.Count; i++)
        {
            Step step = Queue.Steps[i];
            bool current = Queue.Running && i == Queue.Index;
            TreeItem row = _table.CreateItem(root);
            row.SetMetadata(0, i);
            row.SetText(0, (i + 1).ToString());
            row.SetText(1, step.Name);
            row.SetText(2, step.Goal);
            row.SetText(3, step.State);
            row.SetCustomColor(0, Style.Muted);
            row.SetCustomColor(3, step.State.StartsWith("skipped") ? Style.Bad : current ? Style.Good : Style.Muted);
            if (current)
                for (int c = 0; c < 4; c++) row.SetCustomBgColor(c, new Color(Style.GoodDim, 0.55f));
        }
        if (selected >= 0 && selected < Queue.Steps.Count) SelectRow(selected);

        _run.Text = Queue.Running ? "Pause" : "Start the queue";
        _syncing = true;
        _repeat.ButtonPressed = Queue.Repeat;
        _syncing = false;
        _repeat.Text = Queue.Repeat ? "Start over at the end: ON" : "Start over at the end: OFF";
        if (Queue.Note != "") Say(Queue.Note);
        else if (Queue.Running) Say(Queue.Headline);
        PaintQuick();
        if (_savedForm.Visible) PaintSaved();
    }
}
