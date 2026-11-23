using System;
using System.Collections.Generic;
using System.Linq;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Data.Currencies;
using GamesByMalcs.Melvor2.Data.Items;
using Godot;
using static CheatMenu.Lang;

namespace CheatMenu;

/// <summary>
/// The F8 window, built entirely in code: a header, a list of sections on the left and one page per section.
/// A setting is applied the moment it is changed (a slider: when it is released), so there is no Apply button.
/// </summary>
public sealed class Menu
{
    const float NameWidth = 350, ToolNameWidth = 190, ValueWidth = 120, ResetWidth = 64;

    sealed class Section
    {
        public required string Title;
        public required Control Page;
        public required Button Button;
        public required Label Badge;
        public Func<int>? Count;
    }

    readonly SceneTree _tree;
    readonly CanvasLayer _layer = new() { Layer = 120, Name = "CheatMenu", Visible = false };
    readonly Label _character = Style.Label("", Style.Muted);
    readonly Label _status = Style.Label("", Style.Good);
    readonly List<Section> _sections = new();
    readonly List<Action> _refreshers = new();   // copy engine values into the controls
    readonly List<Action> _painters = new();     // recolour rows after a change
    readonly MarginContainer _pages = new() { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
    readonly VBoxContainer _nav = new();
    readonly ButtonGroup _navGroup = new();
    bool _syncing;

    Game? _game;
    List<Item> _items = new(), _autoSellItems = new();
    List<Currency> _currencies = new();
    OptionButton _currencyPick = null!, _skillPick = null!, _autoSellPick = null!;
    /// <summary>Searchable, grouped item lists: one to give an item, one to put an item on the auto-sell list.</summary>
    PickList<Item> _giveItem = null!, _autoSellAdd = null!;
    static readonly Theme s_theme = Style.Build();
    /// <summary>The theme of this window: the shared one, with Taiwan's fonts first when it is in Traditional Chinese.</summary>
    readonly Theme _theme = ThemeFor(s_theme);
    /// <summary>The language this window was built in (it is built again when that changes).</summary>
    public string BuiltIn { get; } = Active;

    // Loot filter page
    List<Item> _droppable = new(), _lootShown = new();
    LineEdit _lootSearch = null!;
    OptionButton _lootShow = null!;
    Tree _lootTree = null!;
    List<(Item Item, int Uses)> _matShown = new();
    LineEdit _matSearch = null!;
    OptionButton _matShow = null!;
    Tree _matTree = null!;
    Label _matStats = null!;
    Label _lootStats = null!;

    public bool Visible => _layer.Visible;
    /// <summary>The window panel (used by layout tests).</summary>
    public Control Panel { get; private set; } = null!;
    public int PageCount => _sections.Count;
    public string PageTitle(int index) => _sections[index].Title;
    public Control PageControl(int index) => _sections[index].Page;

    public Menu(SceneTree tree)
    {
        _tree = tree;

        var shade = new ColorRect { Color = new Color(0, 0, 0, 0.25f), MouseFilter = Control.MouseFilterEnum.Ignore };
        shade.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _layer.AddChild(shade);

        var center = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _layer.AddChild(center);

        var panel = new PanelContainer { CustomMinimumSize = new Vector2(1120, 700), Theme = _theme };
        // Arabic is written from right to left: the whole window is mirrored (sections on the right, sliders reversed).
        if (RightToLeft) panel.LayoutDirection = Control.LayoutDirectionEnum.Rtl;
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
        header.AddChild(Style.Label(T("CHEAT MENU"), Style.Accent, 20));
        _character.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _character.VerticalAlignment = VerticalAlignment.Center;
        _character.SizeFlagsVertical = Control.SizeFlags.Fill;
        header.AddChild(_character);
        header.AddChild(LanguagePick());
        header.AddChild(ResetAllButton());
        _close = MakeButton("Close  (F8)", Hide);
        _close.Text = CloseText;
        header.AddChild(_close);
        head.AddChild(header);
        root.AddChild(head);

        // ---- body: sections on the left, the chosen page on the right
        var body = new HBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 0);
        root.AddChild(body);

        StyleBoxFlat sideBox = Style.Box(Style.Sidebar, 0, null, 0, 8, 10);
        sideBox.BorderColor = Style.Line;
        sideBox.BorderWidthRight = 1;
        var side = new PanelContainer { CustomMinimumSize = new Vector2(190, 0) };
        side.AddThemeStyleboxOverride("panel", sideBox);
        var sideColumn = new VBoxContainer();
        _nav.AddThemeConstantOverride("separation", 3);
        sideColumn.AddChild(_nav);
        sideColumn.AddChild(new Control { SizeFlagsVertical = Control.SizeFlags.ExpandFill });
        Label version = Style.Label("Cheat Menu " + (typeof(Menu).Assembly.GetName().Version?.ToString(3) ?? ""), Style.Muted, 12);
        version.HorizontalAlignment = HorizontalAlignment.Center;
        sideColumn.AddChild(version);
        side.AddChild(sideColumn);
        body.AddChild(side);

        foreach (string m in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            _pages.AddThemeConstantOverride(m, 16);
        body.AddChild(_pages);

        AddFeaturePage("Bank", Features.TabBank);
        AddFeaturePage("XP", Features.TabXp);
        AddFeaturePage("Speed", Features.TabSpeed);
        AddFeaturePage("Skills", Features.TabSkills);
        AddFeaturePage("Combat", Features.TabCombat);
        BuildPerSkill(AddPage("Per skill", () => Engine.ActiveCount(Features.TabPerSkill)));
        BuildLoot(AddPage("Loot filter", () => LootFilter.Count, scroll: false));
        BuildMaterials(AddPage("Materials", () => Unneeded.Count, scroll: false));
        BuildTools(AddPage("Tools", () => AutoSell.Ids.Count));

        // ---- footer
        StyleBoxFlat footBox = Style.Box(Style.Sidebar, 0, null, 0, 18, 8);
        footBox.CornerRadiusBottomLeft = footBox.CornerRadiusBottomRight = 7;
        footBox.BorderColor = Style.Line;
        footBox.BorderWidthTop = 1;
        var foot = new PanelContainer();
        foot.AddThemeStyleboxOverride("panel", footBox);
        var footer = new HBoxContainer();
        _status.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _status.ClipText = true;
        footer.AddChild(_status);
        footer.AddChild(Style.Label(T("Changes apply at once and are remembered per character"), Style.Muted, 12));
        foot.AddChild(footer);
        root.AddChild(foot);

        Engine.Changed += Repaint;
        tree.Root.AddChild(_layer);
        ShowPage(0);
    }

    public void Show(Game game)
    {
        _close.Text = CloseText;
        if (!ReferenceEquals(_game, game))
        {
            _game = game;
            _items = Actions.Items(game);
            _currencies = Actions.Currencies(game);
            _droppable = LootFilter.Droppable(game);
            Fill(_currencyPick, _currencies.Select(c => c.DisplayName.Text));
            Fill(_skillPick, Actions.Skills(game).Select(s => s.DisplayName.Text));
        }
        // The bank counts in the item lists are those of the moment the menu opens.
        List<PickEntry<Item>> picks = ItemPicks.For(game, _items);
        _giveItem.SetEntries(picks);
        _autoSellAdd.SetEntries(picks);
        RefreshAutoSell();
        RefreshLoot();
        RefreshMaterials();
        _character.Text = Engine.Character;
        Say("");
        Refresh();
        _layer.Visible = true;
    }

    /// <summary>For tests: the "Give item" list (count, search, groups, popup) and the auto-sell list.</summary>
    public int GiveItemCount => _giveItem.Count;
    public List<string> GiveItemSearch(string text) { _giveItem.SetFilter(text); return _giveItem.VisibleKeys; }
    public List<string> GiveItemGroups => _giveItem.VisibleGroups;
    public void OpenGiveItem() => _giveItem.Open();
    public Rect2I GiveItemPopup => _giveItem.PopupRect;
    public bool PickAutoSell(string itemId) => _autoSellAdd.Pick(itemId);
    public string AutoSellChoice => _autoSellAdd.ButtonText;

    public void Hide()
    {
        _giveItem.Close();
        _autoSellAdd.Close();
        _layer.Visible = false;
        Settings.Flush();
    }

    public void ShowPage(int index)
    {
        for (int i = 0; i < _sections.Count; i++)
        {
            _sections[i].Page.Visible = i == index;
            _sections[i].Button.SetPressedNoSignal(i == index);
        }
    }

    /// <summary>Copy the engine's values into the controls (after load or reset).</summary>
    public void Refresh()
    {
        _syncing = true;
        foreach (Action r in _refreshers) r();
        _syncing = false;
        Repaint();
    }

    /// <summary>Called about once a second while the window is open: numbers that change on their own.</summary>
    public void Pulse()
    {
        if (!Visible) return;
        UpdateLootStats();
    }

    void Repaint()
    {
        foreach (Action p in _painters) p();
        foreach (Section s in _sections)
        {
            int count = s.Count?.Invoke() ?? 0;
            s.Badge.Text = count > 0 ? count.ToString() : "";
        }
    }

    void Say(string text) => _status.Text = text;

    // ---------------------------------------------------------------- building blocks

    VBoxContainer AddPage(string title, Func<int>? count, bool scroll = true)
    {
        var page = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        page.AddThemeConstantOverride("separation", 5);
        Control host = page;
        if (scroll)
        {
            var scroller = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
            scroller.AddChild(page);
            host = scroller;
        }
        host.Visible = false;
        _pages.AddChild(host);

        int index = _sections.Count;
        var button = new Button { Text = T(title), ToggleMode = true, ButtonGroup = _navGroup, Alignment = HorizontalAlignment.Left };
        StyleBoxFlat chosen = Style.Box(Style.Card, 5, Style.Accent, 0, 12, 8);
        chosen.BorderWidthLeft = 3;
        button.AddThemeStyleboxOverride("normal", Style.Box(Style.Sidebar, 5, null, 0, 15, 8));
        button.AddThemeStyleboxOverride("hover", Style.Box(Style.Card, 5, null, 0, 15, 8));
        button.AddThemeStyleboxOverride("pressed", chosen);
        button.AddThemeStyleboxOverride("hover_pressed", chosen);
        button.Pressed += () => ShowPage(index);
        var badge = Style.Label("", Style.Accent, 12);
        badge.MouseFilter = Control.MouseFilterEnum.Ignore;
        badge.HorizontalAlignment = HorizontalAlignment.Right;
        badge.VerticalAlignment = VerticalAlignment.Center;
        badge.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        badge.OffsetRight = -12;
        button.AddChild(badge);
        _nav.AddChild(button);

        _sections.Add(new Section { Title = title, Page = host, Button = button, Badge = badge, Count = count });
        return page;
    }

    void AddFeaturePage(string title, string tab)
    {
        VBoxContainer page = AddPage(title, () => Engine.ActiveCount(tab));
        foreach (Feature f in Features.All.Where(f => f.Tab == tab))
            page.AddChild(FeatureRow(f));
    }

    Button _close = null!;

    /// <summary>"Close  (F8)" in the menu's language, with the key the player chose (none: just "Close").</summary>
    static string CloseText
    {
        get
        {
            string label = CheatMenuMod.Instance?.Key.Label ?? "F8";
            string text = T("Close  (F8)");
            return label == "" ? text.Replace("(F8)", "").TrimEnd() : text.Replace("F8", label);
        }
    }

    /// <summary>For tests: what the Close button says.</summary>
    public string CloseLabel => _close.Text;

    static Button MakeButton(string text, Action onPressed)
    {
        var b = new Button { Text = T(text) };
        b.Pressed += onPressed;
        return b;
    }

    /// <summary>The language of the window. A change builds the window again in that language.</summary>
    /// <summary>
    /// The language list, with the word "Language" in front so it reads as a language choice. In another language
    /// the English word follows in brackets, so a player who cannot read the window still finds the list. "Auto"
    /// says which language it gives right now.
    /// </summary>
    Control LanguagePick()
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        string word = T("Language");
        Label label = Style.Label(Active == English ? word : $"{word} (\u2068Language\u2069)", Style.Muted, 15);
        label.VerticalAlignment = VerticalAlignment.Center;
        label.SizeFlagsVertical = Control.SizeFlags.Fill;
        row.AddChild(label);
        var pick = new OptionButton
        {
            TooltipText = "Language / 语言 / 語言 / Idioma / Langue / اللغة / भाषा\nAuto: the game's own language when the menu has it, else English",
            FitToLongestItem = true,
        };
        foreach ((string code, string name) in Choices)
            pick.AddItem(code == Auto ? $"{T("Auto")} (\u2068{Current.Name}\u2069)" : name);
        pick.Select(Math.Max(0, Array.FindIndex(Choices, c => c.Code == Choice)));
        pick.ItemSelected += i => { if (Choices[i].Code != Choice) LanguageChosen?.Invoke(Choices[i].Code); };
        row.AddChild(pick);
        return row;
    }

    /// <summary>Raised when the player picks another language (the mod then builds a new window).</summary>
    public event Action<string>? LanguageChosen;

    /// <summary>The page on show (to open the new window on the same page).</summary>
    public int CurrentPage => Math.Max(0, _sections.FindIndex(s => s.Page.Visible));

    /// <summary>Takes the window out of the game for good.</summary>
    public void Dispose()
    {
        Engine.Changed -= Repaint;
        _giveItem.Close();
        _autoSellAdd.Close();
        _layer.QueueFree();
    }

    /// <summary>Asks for a second click, so one stray click cannot wipe every setting.</summary>
    Button ResetAllButton()
    {
        string idle = T("Reset all settings");
        var button = new Button { Text = idle };
        bool armed = false;
        button.Pressed += () =>
        {
            if (!armed)
            {
                armed = true;
                button.Text = T("Click again to confirm");
                _tree.CreateTimer(3).Timeout += () => { armed = false; if (GodotObject.IsInstanceValid(button)) button.Text = idle; };
                return;
            }
            armed = false;
            button.Text = idle;
            Engine.ResetAll();
            Refresh();
            Say(T("All settings are back to normal."));
        };
        return button;
    }

    static PanelContainer Card(Control content, bool active = false)
    {
        var card = new PanelContainer();
        card.AddThemeStyleboxOverride("panel", active ? s_rowOn : s_rowOff);
        card.AddChild(content);
        return card;
    }

    static readonly StyleBoxFlat s_rowOn = Style.Row(true), s_rowOff = Style.Row(false);

    /// <summary>A tool row: a name on the left, its controls pushed to the right.</summary>
    static PanelContainer ToolRow(string name, params Control[] controls)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 10);
        row.AddChild(new Label { Text = T(name), CustomMinimumSize = new Vector2(ToolNameWidth, 0), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        foreach (Control c in controls) row.AddChild(c);
        return Card(row);
    }

    static Label Heading(string text)
    {
        Label label = Style.Label(T(text).ToUpperInvariant(), Style.Muted, 12);
        label.CustomMinimumSize = new Vector2(0, 24);
        label.VerticalAlignment = VerticalAlignment.Bottom;
        return label;
    }

    static SpinBox MakeSpin(double min, double max, double step, double value, string suffix = "", float width = ValueWidth) =>
        new() { MinValue = min, MaxValue = max, Step = step, Value = value, Suffix = suffix, CustomMinimumSize = new Vector2(width, 0), SelectAllOnFocus = true };

    static void Fill(OptionButton pick, IEnumerable<string> names)
    {
        pick.Clear();
        foreach (string n in names) pick.AddItem(n);
        if (pick.ItemCount > 0) pick.Select(0);
    }

    static string Shown(Feature f, double value) =>
        value == f.Off ? T("back to normal")
        : f.Kind == FeatureKind.Toggle ? T("ON")
        : value.ToString("0.##") + f.Suffix.Trim();

    /// <summary>Hands a value to the game right away and says so in the status line.</summary>
    void Commit(Feature f, double value)
    {
        Engine.SetValue(f.Id, value);
        Say($"{Name(f)}: {Shown(f, Engine.GetValue(f.Id))}");
    }

    // ---------------------------------------------------------------- one setting per row

    Control FeatureRow(Feature f)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 12);
        PanelContainer card = Card(row);

        var names = new VBoxContainer { CustomMinimumSize = new Vector2(NameWidth, 0), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
        names.AddThemeConstantOverride("separation", 0);
        Label name = Style.Label(Name(f));
        names.AddChild(name);
        if (Features.Hints.TryGetValue(f.Id, out string? hint))
        {
            hint = T(hint);
            Label small = Style.Label(hint, Style.Muted, 12);
            small.ClipText = true;
            small.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            small.TooltipText = hint;
            small.MouseFilter = Control.MouseFilterEnum.Pass;
            names.AddChild(small);
        }
        row.AddChild(names);

        void Paint()
        {
            bool active = Engine.IsActive(f.Id);
            card.AddThemeStyleboxOverride("panel", active ? s_rowOn : s_rowOff);
            name.AddThemeColorOverride("font_color", active ? Style.Accent : Style.Text);
        }
        _painters.Add(Paint);

        if (f.Kind == FeatureKind.Toggle)
        {
            // A plain ON/OFF switch: the game's own check box art is far larger than a text row.
            var toggle = new Button { ToggleMode = true, Text = T("OFF"), CustomMinimumSize = new Vector2(ValueWidth, 0) };
            StyleBoxFlat on = Style.Box(Style.GoodDim, 5, Style.Good, 1, 12, 5);
            toggle.AddThemeStyleboxOverride("pressed", on);
            toggle.AddThemeStyleboxOverride("hover_pressed", on);
            toggle.AddThemeColorOverride("font_pressed_color", Style.Good);
            toggle.AddThemeColorOverride("font_hover_pressed_color", Style.Good);
            toggle.Toggled += pressed =>
            {
                toggle.Text = pressed ? T("ON") : T("OFF");
                if (!_syncing) Commit(f, pressed ? 1 : 0);
            };
            _refreshers.Add(() => { toggle.ButtonPressed = Engine.GetValue(f.Id) >= 0.5; toggle.Text = toggle.ButtonPressed ? T("ON") : T("OFF"); });
            row.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
            row.AddChild(toggle);
            row.AddChild(new Control { CustomMinimumSize = new Vector2(ResetWidth, 0) });
            return card;
        }

        var slider = new HSlider
        {
            MinValue = f.Min, MaxValue = f.Max, Step = f.Step, CustomMinimumSize = new Vector2(180, 16),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            ExpEdit = f.Min >= 1 && f.Max >= 100   // wide "x1..x1000" ranges: fine control at the low end
        };
        SpinBox spin = MakeSpin(f.Min, f.Max, f.Step, f.Off, f.Suffix);
        var reset = new Button { Text = T("Reset"), CustomMinimumSize = new Vector2(ResetWidth, 0), TooltipText = T("Back to the game's normal value") };
        reset.AddThemeFontSizeOverride("font_size", 12);

        void Put(double v)
        {
            bool was = _syncing;
            _syncing = true;
            slider.Value = v;
            spin.Value = v;
            _syncing = was;
        }

        // While the slider is being dragged only the number moves; the game gets the value on release.
        bool dragging = false;
        slider.DragStarted += () => dragging = true;
        slider.DragEnded += _ => { dragging = false; Commit(f, slider.Value); };
        slider.ValueChanged += v =>
        {
            if (_syncing) return;
            _syncing = true; spin.Value = v; _syncing = false;
            if (!dragging) Commit(f, v);
        };
        spin.ValueChanged += v =>
        {
            if (_syncing) return;
            _syncing = true; slider.Value = v; _syncing = false;
            Commit(f, v);
        };
        reset.Pressed += () => { Put(f.Off); Commit(f, f.Off); };
        _refreshers.Add(() => Put(Engine.GetValue(f.Id)));
        _painters.Add(() => reset.Disabled = !Engine.IsActive(f.Id));

        row.AddChild(slider);
        row.AddChild(spin);
        row.AddChild(reset);
        return card;
    }

    /// <summary>A number box bound to one setting (no slider): used where many settings share a table.</summary>
    SpinBox FeatureBox(Feature f)
    {
        SpinBox spin = MakeSpin(f.Min, f.Max, f.Step, f.Off, f.Suffix, 104);
        spin.ValueChanged += v => { if (!_syncing) Commit(f, v); };
        _refreshers.Add(() => spin.Value = Engine.GetValue(f.Id));
        return spin;
    }

    // ---------------------------------------------------------------- Per skill

    void BuildPerSkill(VBoxContainer page)
    {
        Label intro = Style.Label(T("Each skill's own XP multiplier and action speed. These stack with the settings on the XP and Speed pages."), Style.Muted, 12);
        page.AddChild(intro);

        page.AddChild(BulkRow());

        var columns = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        columns.AddThemeConstantOverride("separation", 12);
        page.AddChild(columns);

        int half = (Features.SkillNames.Length + 1) / 2;
        foreach (string[] part in new[] { Features.SkillNames.Take(half).ToArray(), Features.SkillNames.Skip(half).ToArray() })
        {
            var grid = new GridContainer { Columns = 3, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            grid.AddThemeConstantOverride("h_separation", 10);
            grid.AddThemeConstantOverride("v_separation", 3);
            Label skillHead = Style.Label(T("SKILL"), Style.Muted, 12);
            skillHead.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            grid.AddChild(skillHead);
            grid.AddChild(Style.Label(T("XP"), Style.Muted, 12));
            grid.AddChild(Style.Label(T("SPEED"), Style.Muted, 12));

            foreach (string skill in part)
            {
                Label name = Style.Label(Lang.Skill(skill));
                name.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                grid.AddChild(name);
                var mine = new List<Feature>();
                foreach (string column in new[] { "xp", "speed" })
                {
                    Feature? f = Features.All.FirstOrDefault(x => x.Skill == skill && x.Column == column);
                    if (f != null) mine.Add(f);
                    grid.AddChild(f != null ? FeatureBox(f) : new Control());
                }
                _painters.Add(() => name.AddThemeColorOverride("font_color", mine.Any(f => Engine.IsActive(f.Id)) ? Style.Accent : Style.Text));
            }
            PanelContainer card = Card(grid);
            card.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            card.SizeFlagsVertical = Control.SizeFlags.ShrinkBegin;
            columns.AddChild(card);
        }
    }

    /// <summary>One card with two separate controls: an XP multiplier and a speed written into every skill of the table.</summary>
    PanelContainer BulkRow()
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 10);
        Label title = Style.Label(T("All skills at once"));
        title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        title.TooltipText = T("Writes one value into every skill of the table below. XP and speed are set separately.");
        title.MouseFilter = Control.MouseFilterEnum.Pass;
        row.AddChild(title);

        void Add(string name, string column, double max)
        {
            List<string> ids = Features.All.Where(f => f.Column == column).Select(f => f.Id).ToList();
            SpinBox spin = MakeSpin(1, max, 1, 2, "x", 104);
            row.AddChild(Style.Label(T(name), Style.Muted));
            row.AddChild(spin);
            row.AddChild(MakeButton("Apply to all", () =>
            {
                Engine.SetValues(ids, spin.Value);
                Refresh();
                Say(spin.Value == 1 ? F("{0}: back to normal for all {1} skills", T(name), ids.Count) : F("{0}: {1}x for all {2} skills", T(name), spin.Value.ToString("0.##"), ids.Count));
            }));
        }
        Add("XP", "xp", 1000);
        row.AddChild(new Control { CustomMinimumSize = new Vector2(14, 0) });
        Add("Speed", "speed", 20);
        return Card(row, active: true);
    }

    // ---------------------------------------------------------------- Loot filter

    static readonly string[] RuleNames = { "Keep", "Sell", "Destroy" };
    static readonly Color[] RuleColors = { Style.Muted, Style.Good, Style.Bad };

    void BuildLoot(VBoxContainer page)
    {
        Label intro = Style.Label(T(
            "Choose what happens to items that monsters and dungeons drop. Only the dropped amount is sold or destroyed; " +
            "what is already in your bank is never touched. Double-click an item to step through Keep, Sell and Destroy."), Style.Muted, 12);
        intro.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        intro.CustomMinimumSize = new Vector2(400, 0);
        page.AddChild(intro);

        var filters = new HBoxContainer();
        filters.AddThemeConstantOverride("separation", 10);
        _lootSearch = new LineEdit { PlaceholderText = T("Search drops..."), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClearButtonEnabled = true };
        _lootSearch.TextChanged += _ => RefreshLoot();
        _lootShow = new OptionButton { CustomMinimumSize = new Vector2(230, 0) };
        _lootShow.AddItem(T("All drops"));
        _lootShow.AddItem(T("Drops of the current monster"));
        _lootShow.AddItem(T("Only items with a rule"));
        _lootShow.ItemSelected += _ => RefreshLoot();
        filters.AddChild(_lootSearch);
        filters.AddChild(_lootShow);
        page.AddChild(filters);

        _lootTree = new Tree
        {
            Columns = 2, HideRoot = true, SelectMode = Tree.SelectModeEnum.Multi, ColumnTitlesVisible = true,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 200)
        };
        _lootTree.SetColumnTitle(0, T("Item"));
        _lootTree.SetColumnTitle(1, T("When dropped"));
        _lootTree.SetColumnTitleAlignment(0, HorizontalAlignment.Left);
        _lootTree.SetColumnTitleAlignment(1, HorizontalAlignment.Left);
        _lootTree.SetColumnExpand(0, true);
        _lootTree.SetColumnExpand(1, false);
        _lootTree.SetColumnCustomMinimumWidth(1, 160);
        _lootTree.ItemActivated += () =>
        {
            TreeItem? picked = _lootTree.GetSelected();
            if (picked == null) return;
            Item item = _lootShown[(int)picked.GetMetadata(0)];
            SetLootRule(new[] { item }, (LootRule)(((int)LootFilter.Get(item) + 1) % 3));
        };
        page.AddChild(_lootTree);

        var actions = new HBoxContainer();
        actions.AddThemeConstantOverride("separation", 10);
        actions.AddChild(Style.Label(T("Selected items:"), Style.Muted));
        actions.AddChild(MakeButton("Keep", () => SetLootRule(SelectedLoot(), LootRule.Keep)));
        actions.AddChild(MakeButton("Sell when dropped", () => SetLootRule(SelectedLoot(), LootRule.Sell)));
        actions.AddChild(MakeButton("Destroy when dropped", () => SetLootRule(SelectedLoot(), LootRule.Destroy)));
        actions.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        actions.AddChild(MakeButton("Remove all rules", () =>
        {
            LootFilter.Clear();
            RefreshLoot();
            Repaint();
            Say(T("Loot filter: every drop is kept again."));
        }));
        page.AddChild(actions);

        _lootStats = Style.Label("", Style.Muted, 12);
        page.AddChild(_lootStats);
    }

    List<Item> SelectedLoot()
    {
        var picked = new List<Item>();
        for (TreeItem? row = _lootTree.GetNextSelected(null); row != null; row = _lootTree.GetNextSelected(row))
            picked.Add(_lootShown[(int)row.GetMetadata(0)]);
        return picked;
    }

    void SetLootRule(IReadOnlyCollection<Item> items, LootRule rule)
    {
        if (items.Count == 0) { Say(T("Select one or more items in the list first.")); return; }
        LootFilter.Set(items, rule);
        RefreshLoot(items);
        Repaint();
        string what = items.Count == 1 ? Actions.ItemName(items.First()) : F("{0} items", items.Count);
        Say(F("Loot filter: {0} -> {1}", what, T(RuleNames[(int)rule])));
    }

    void RefreshLoot() => RefreshLoot(null);

    void RefreshLoot(IReadOnlyCollection<Item>? keepSelected)
    {
        if (_game == null) return;
        IEnumerable<Item> list = _droppable;
        string note = "";
        if (_lootShow.Selected == 1)
        {
            var (enemy, drops) = LootFilter.CurrentEnemy(_game);
            list = list.Where(drops.Contains);
            note = (enemy == "" ? T("No monster is selected in combat.") : F("Showing the drops of {0}.", enemy)) + "  ";
        }
        else if (_lootShow.Selected == 2) list = list.Where(i => LootFilter.Get(i) != LootRule.Keep);
        string text = _lootSearch.Text.Trim();
        if (text != "") list = list.Where(i => Actions.ItemName(i).Contains(text, StringComparison.OrdinalIgnoreCase));
        _lootShown = list.ToList();

        _lootTree.Clear();
        TreeItem root = _lootTree.CreateItem();
        for (int i = 0; i < _lootShown.Count; i++)
        {
            Item item = _lootShown[i];
            int rule = (int)LootFilter.Get(item);
            TreeItem row = _lootTree.CreateItem(root);
            row.SetText(0, Actions.ItemName(item));
            row.SetIcon(0, item.Icon);
            row.SetIconMaxWidth(0, 22);
            row.SetMetadata(0, i);
            row.SetText(1, T(RuleNames[rule]));
            row.SetCustomColor(1, RuleColors[rule]);
            row.SetSelectable(1, false);
            if (keepSelected != null && keepSelected.Contains(item)) row.Select(0);
        }
        _lootNote = note;
        UpdateLootStats();
    }

    string _lootNote = "";

    void UpdateLootStats() =>
        _lootStats.Text = _lootNote + F("{0} of {1} items shown  |  {2} rule(s)  |  since this character was opened: {3:N0} sold, {4:N0} destroyed",
                                        _lootShown.Count, _droppable.Count, LootFilter.Count, LootFilter.Sold, LootFilter.Destroyed);

    // ---------------------------------------------------------------- Materials

    void BuildMaterials(VBoxContainer page)
    {
        Label intro = Style.Label(T(
            "Pick the materials you no longer want to need. A material set to \"Not needed\" is taken out of every recipe and item upgrade " +
            "that asks for it: bars without coal, potions without vials. Double-click a material to switch it."), Style.Muted, 12);
        intro.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        intro.CustomMinimumSize = new Vector2(400, 0);
        page.AddChild(intro);

        var filters = new HBoxContainer();
        filters.AddThemeConstantOverride("separation", 10);
        _matSearch = new LineEdit { PlaceholderText = T("Search materials..."), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClearButtonEnabled = true };
        _matSearch.TextChanged += _ => RefreshMaterials();
        _matShow = new OptionButton { CustomMinimumSize = new Vector2(230, 0) };
        _matShow.AddItem(T("All materials, most used first"));
        _matShow.AddItem(T("Only the ones not needed"));
        _matShow.ItemSelected += _ => RefreshMaterials();
        filters.AddChild(_matSearch);
        filters.AddChild(_matShow);
        page.AddChild(filters);

        _matTree = new Tree
        {
            Columns = 3, HideRoot = true, SelectMode = Tree.SelectModeEnum.Multi, ColumnTitlesVisible = true,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 200)
        };
        _matTree.SetColumnTitle(0, T("Material"));
        _matTree.SetColumnTitle(1, T("Asked for by"));
        _matTree.SetColumnTitle(2, T("In recipes"));
        for (int column = 0; column < 3; column++) _matTree.SetColumnTitleAlignment(column, HorizontalAlignment.Left);
        _matTree.SetColumnExpand(0, true);
        _matTree.SetColumnExpand(1, false);
        _matTree.SetColumnExpand(2, false);
        _matTree.SetColumnCustomMinimumWidth(1, 130);
        _matTree.SetColumnCustomMinimumWidth(2, 130);
        _matTree.ItemActivated += () =>
        {
            TreeItem? picked = _matTree.GetSelected();
            if (picked == null) return;
            Item item = _matShown[(int)picked.GetMetadata(0)].Item;
            SetMaterials(new[] { item }, !Unneeded.IsRemoved(item));
        };
        page.AddChild(_matTree);

        var actions = new HBoxContainer();
        actions.AddThemeConstantOverride("separation", 10);
        actions.AddChild(Style.Label(T("Selected materials:"), Style.Muted));
        actions.AddChild(MakeButton("Not needed", () => SetMaterials(SelectedMaterials(), true)));
        actions.AddChild(MakeButton("Needed again", () => SetMaterials(SelectedMaterials(), false)));
        actions.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        actions.AddChild(MakeButton("Everything needed again", () =>
        {
            Unneeded.Clear();
            RefreshMaterials();
            Repaint();
            Say(T("Materials: every recipe asks for all of its materials again."));
        }));
        page.AddChild(actions);

        _matStats = Style.Label("", Style.Muted, 12);
        page.AddChild(_matStats);
    }

    List<Item> SelectedMaterials()
    {
        var picked = new List<Item>();
        for (TreeItem? row = _matTree.GetNextSelected(null); row != null; row = _matTree.GetNextSelected(row))
            picked.Add(_matShown[(int)row.GetMetadata(0)].Item);
        return picked;
    }

    /// <summary>For tests: selects a material's row the way a click does. False when it is not in the list.</summary>
    public bool PickMaterial(string name)
    {
        TreeItem? root = _matTree.GetRoot();
        for (TreeItem? row = root?.GetFirstChild(); row != null; row = row.GetNext())
        {
            bool mine = row.GetText(0) == name;
            if (mine) row.Select(0); else row.Deselect(0);
        }
        return SelectedMaterials().Count == 1;
    }

    void SetMaterials(IReadOnlyCollection<Item> items, bool removed)
    {
        if (items.Count == 0) { Say(T("Select one or more materials in the list first.")); return; }
        Unneeded.Set(items, removed);
        RefreshMaterials(items);
        Repaint();
        string what = items.Count == 1 ? Actions.ItemName(items.First()) : F("{0} materials", items.Count);
        Say(removed ? F("Materials: {0} -> no longer needed in any recipe.", what) : F("Materials: {0} -> needed again.", what));
    }

    void RefreshMaterials() => RefreshMaterials(null);

    void RefreshMaterials(IReadOnlyCollection<Item>? keepSelected)
    {
        if (_game == null) return;
        IEnumerable<(Item Item, int Uses)> list = Unneeded.Materials;
        if (_matShow.Selected == 1) list = list.Where(m => Unneeded.IsRemoved(m.Item));
        string text = _matSearch.Text.Trim();
        if (text != "") list = list.Where(m => Actions.ItemName(m.Item).Contains(text, StringComparison.OrdinalIgnoreCase));
        _matShown = list.ToList();

        _matTree.Clear();
        TreeItem root = _matTree.CreateItem();
        for (int i = 0; i < _matShown.Count; i++)
        {
            var (item, uses) = _matShown[i];
            bool removed = Unneeded.IsRemoved(item);
            TreeItem row = _matTree.CreateItem(root);
            row.SetText(0, Actions.ItemName(item));
            row.SetIcon(0, item.Icon);
            row.SetIconMaxWidth(0, 22);
            row.SetMetadata(0, i);
            row.SetText(1, uses == 1 ? T("1 recipe") : F("{0} recipes", uses));
            row.SetCustomColor(1, Style.Muted);
            row.SetSelectable(1, false);
            row.SetText(2, removed ? T("Not needed") : T("Needed"));
            row.SetCustomColor(2, removed ? Style.Good : Style.Muted);
            row.SetSelectable(2, false);
            if (keepSelected != null && keepSelected.Contains(item)) row.Select(0);
        }
        _matStats.Text = F("{0} of {1} materials shown  |  {2} not needed", _matShown.Count, Unneeded.Materials.Count, Unneeded.Count);
    }

    // ---------------------------------------------------------------- Tools

    void BuildTools(VBoxContainer page)
    {
        page.AddChild(Heading("Money and items"));
        _currencyPick = new OptionButton { CustomMinimumSize = new Vector2(230, 0) };
        SpinBox currencyAmount = MakeSpin(1, 1e12, 1, 1_000_000);
        page.AddChild(ToolRow("Give currency", _currencyPick, currencyAmount, MakeButton("Add", () =>
        {
            if (_game == null || _currencyPick.Selected < 0) return;
            Currency c = _currencies[_currencyPick.Selected];
            Actions.AddCurrency(_game, c, (long)currencyAmount.Value);
            Say($"+{currencyAmount.Value:N0} {c.DisplayName.Text}");
        })));

        _giveItem = new PickList<Item>(T("Choose an item"), _theme);
        _giveItem.Control.CustomMinimumSize = new Vector2(330, 0);
        _giveItem.Control.TooltipText = T("Click and type to search. Items are grouped by the bank's own kinds.");
        SpinBox itemAmount = MakeSpin(1, int.MaxValue, 1, 100);
        page.AddChild(ToolRow("Give item", _giveItem.Control, itemAmount, MakeButton("Add", () =>
        {
            if (_game == null || _giveItem.Selected is not { } chosen) { Say(T("Choose an item first.")); return; }
            Actions.AddItem(_game, chosen.Value, (int)itemAmount.Value);
            Say($"+{itemAmount.Value:N0} {Actions.ItemName(chosen.Value)}");
        })));

        // Auto-sell list: its own item list, so it does not depend on the row above.
        _autoSellAdd = new PickList<Item>(T("Choose an item to sell"), _theme);
        _autoSellAdd.Control.CustomMinimumSize = new Vector2(260, 0);
        _autoSellPick = new OptionButton { CustomMinimumSize = new Vector2(200, 0), FitToLongestItem = false, ClipText = true, TooltipText = T("The items being sold now.") };
        PanelContainer autoSell = ToolRow("Auto-sell from bank",
            _autoSellAdd.Control,
            MakeButton("Add", () =>
            {
                if (_autoSellAdd.Selected is not { } chosen) { Say(T("Choose the item to sell first.")); return; }
                AutoSell.Add(chosen.Value);
                RefreshAutoSell();
                Repaint();
                Say(F("Auto-sell: {0} added.", Actions.ItemName(chosen.Value)));
            }),
            _autoSellPick,
            MakeButton("Remove", () =>
            {
                if (_autoSellPick.Selected < 0 || _autoSellPick.Selected >= _autoSellItems.Count) return;
                AutoSell.Remove(_autoSellItems[_autoSellPick.Selected]);
                RefreshAutoSell();
                Repaint();
            }));
        autoSell.TooltipText = T("Items on this list are sold from the bank about once a second, wherever they came from.");
        page.AddChild(autoSell);

        page.AddChild(Heading("Levels"));
        _skillPick = new OptionButton { CustomMinimumSize = new Vector2(230, 0) };
        SpinBox level = MakeSpin(1, 999, 1, 99);
        page.AddChild(ToolRow("Set skill level", _skillPick, level, MakeButton("Set", () =>
        {
            if (_game == null || _skillPick.Selected < 0) return;
            var skill = Actions.Skills(_game)[_skillPick.Selected];
            Actions.SetLevel(skill, (int)level.Value);
            Say(F("{0}: level {1}", skill.DisplayName.Text, skill.Experience.Level));
        })));
        page.AddChild(ToolRow("All skills",
            MakeButton("Set all to the level above", () =>
            {
                if (_game == null) return;
                Actions.SetAllLevels(_game, (int)level.Value);
                Say(T("All skills set."));
            }),
            MakeButton("Max all", () =>
            {
                if (_game == null) return;
                Actions.MaxAllLevels(_game);
                Say(T("All skills are at their maximum level."));
            })));

        page.AddChild(Heading("Time and quests"));
        SpinBox hours = MakeSpin(0.1, 720, 0.1, 8, " h");
        page.AddChild(ToolRow("Skip time (as if the game was closed)", hours, MakeButton("Run", () =>
        {
            if (_game == null) return;
            Actions.RunOfflineHours(_game, hours.Value);
            Hide();
        })));
        page.AddChild(ToolRow("Quests", MakeButton("Complete all quests", () =>
        {
            if (_game == null) return;
            Actions.QuestResult r = Actions.CompleteAllQuests(_game);
            string said = F("{0} quest(s) completed, rewards given.", r.Done);
            if (r.Waiting > 0) said += " " + F("{0} reward(s) did not fit in the bank: claim them on the Quests page.", r.Waiting);
            if (r.Failed > 0) said += " " + F("{0} quest(s) could not be completed (see the log).", r.Failed);
            if (r.Done + r.Waiting + r.Failed == 0) said = T("All quests are already completed.");
            Say(said);
        })));
    }

    void RefreshAutoSell()
    {
        _autoSellItems = _items.Where(i => AutoSell.Ids.Contains(i.Id)).ToList();
        Fill(_autoSellPick, _autoSellItems.Select(Actions.ItemName));
    }

}
