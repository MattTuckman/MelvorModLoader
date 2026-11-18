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
    readonly SpinBox _planAmount = new() { MinValue = 1, MaxValue = 100_000_000, Step = 1, Value = 100, CustomMinimumSize = new Vector2(120, 0), SelectAllOnFocus = true };
    readonly LineEdit _planSearch = new() { PlaceholderText = "Search item...", CustomMinimumSize = new Vector2(170, 0), ClearButtonEnabled = true };
    readonly OptionButton _planPick = new() { CustomMinimumSize = new Vector2(260, 0), FitToLongestItem = false, ClipText = true };
    readonly Label _planNote = Style.Label("", Style.Muted, 12);
    readonly Button _planAdd = new() { Text = "Add these steps to the queue" };
    Tree _planTable = null!;
    List<Item> _makeable = new(), _shownMakeable = new();
    PlanResult? _plan;
    string _planShown = "";

    Game? _game;
    List<(GameEntity Entity, ActionComponent Action)> _skills = new();
    List<Target> _targets = new();
    List<Item> _items = new(), _shownItems = new();
    List<string> _levelSkills = new();
    bool _syncing;

    public bool Visible => _layer.Visible;
    public Control Panel { get; }

    public QueueWindow(SceneTree tree)
    {
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
        Label hint = Style.Label("Do one thing until a goal is reached, then the next. Runs while the game is open.", Style.Muted);
        hint.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        hint.VerticalAlignment = VerticalAlignment.Center;
        hint.SizeFlagsVertical = Control.SizeFlags.Fill;
        hint.ClipText = true;
        header.AddChild(hint);
        StyleBoxFlat go = Style.Box(Style.GoodDim, 5, Style.Good, 1, 12, 5);
        _run.AddThemeStyleboxOverride("normal", go);
        _run.AddThemeStyleboxOverride("hover", Style.Box(new Color(0.16f, 0.38f, 0.23f), 5, Style.Good, 1, 12, 5));
        _run.Pressed += () => { if (Queue.Running) Queue.Pause("Paused."); else Queue.Start(); };
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
        foreach (Button mode in new[] { _stepMode, _planMode })
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
        _planMode.Toggled += ShowPlanFace;
        Label modeHint = Style.Label("", Style.Muted, 12);
        modeHint.Text = "A plan follows the recipes back to what must be gathered and adds every step to the end of the queue.";
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
        row.AddChild(Fixed("Own", 50));
        _planAmount.TooltipText = "How many you want to have in the bank in the end (not how many more).";
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

    void ShowPlanFace(bool plan)
    {
        _stepForm.Visible = !plan;
        _planForm.Visible = plan;
        if (plan) { _planShown = ""; Replan(); }
    }

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
        PlanResult plan = _plan = Planner.Compute(_game, item, (long)_planAmount.Value);
        string shown = item.Id + "|" + plan.Own + "|" + plan.Problem + "|" + string.Join(";", plan.Steps.Concat(plan.Supply)
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
        string text = plan.Steps.Count == 1 ? "1 step" : $"{plan.Steps.Count} steps";
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
        Say($"Plan added: {added} step(s) to own {plan.Own:N0} {plan.Item.DisplayName.GetPluralText(1)}.");
        _planShown = "";
        Replan();   // what was just added now counts as "will be made"
    }

    /// <summary>For tests: open the planner, ask for an item and return the plan it shows.</summary>
    public PlanResult? PlanFor(string itemId, long own)
    {
        _planMode.ButtonPressed = true;
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
        SuggestItem();
    }

    /// <summary>
    /// "Until I own N of an item" almost always means the thing the action makes, so that item is filled in
    /// whenever the action changes. Another item can still be searched for and picked.
    /// </summary>
    void SuggestItem()
    {
        if (_skillPick.Selected < 0 || _skillPick.Selected >= _skills.Count || _targetPick.Selected < 0 || _targetPick.Selected >= _targets.Count) return;
        Item? product = Targets.Product(_skills[_skillPick.Selected].Action, _targets[_targetPick.Selected]);
        if (product == null) return;
        string name = product.DisplayName.GetPluralText(1);
        _itemSearch.Text = name;      // setting the text from code does not raise TextChanged
        FilterItems(name);
        int at = _shownItems.IndexOf(product);
        if (at >= 0) _itemPick.Select(at);
    }

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
        int skill =active == null ? -1 : _skills.FindIndex(s => ReferenceEquals(s.Action, active));
        if (target == null || skill < 0) { Say("Start an action in the game first, then press this."); return; }
        _skillPick.Select(skill);
        FillTargets(target.Id);
        Say($"Filled in: {_skills[skill].Entity.DisplayName.Text}: {target.Name}");
    }

    void UntilChanged()
    {
        var kind = (Until)_untilPick.Selected;
        _amount.Visible = kind != Until.Stops;
        _itemSearch.Visible = _itemPick.Visible = kind == Until.Item;
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
        if (step.Until == Until.Item)
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
    }
}
