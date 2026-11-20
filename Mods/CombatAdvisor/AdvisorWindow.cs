using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Adventurers;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Components.CombatAreas;
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.Entities.Combat;
using Godot;

namespace CombatAdvisor;

/// <summary>
/// The advisor window, in the order a player asks: is it safe (the verdict, one sentence, a gauge of its biggest hit
/// against your auto-eat point and your HP, and what to do when it is not)? what do I fight with (gear, food, a stance
/// preview that changes nothing in the game)? what do I get per hour (kills, XP, supplies)? The how is behind
/// "Show details"; the other monsters of the area are below. Built on a layer of its own above the game's area menu,
/// in the game's own panels.
/// </summary>
public sealed class AdvisorWindow
{
    const float Width = 1000, Height = 690;
    static readonly CultureInfo C = CultureInfo.InvariantCulture;

    readonly CanvasLayer _layer;
    readonly VBoxContainer _body;
    Game? _game;
    CombatArea? _area;
    Enemy? _enemy;
    AttackStance? _preview;
    int _hours = 1;
    bool _reserve = true;
    bool _details;

    /// <summary>Monsters still to work out for the comparison table (one per frame, the rows fill in).</summary>
    readonly Queue<Enemy> _todo = new();
    readonly List<Outlook> _waiting = new();
    Outlook? _main;
    readonly Dictionary<Enemy, HBoxContainer> _rows = new();
    Setup? _setup;

    public bool Visible => _layer.Visible;
    public Enemy? Enemy => _enemy;
    public CombatArea? Area => _area;
    public Setup? Setup => _setup;
    public Control Panel { get; }

    public AdvisorWindow(SceneTree tree)
    {
        _layer = new CanvasLayer { Layer = 90, Visible = false };
        var dim = new ColorRect { Color = new Color(0, 0, 0, 0.55f), MouseFilter = Control.MouseFilterEnum.Stop };
        dim.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        dim.GuiInput += e => { if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) Close(); };
        _layer.AddChild(dim);

        var center = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _layer.AddChild(center);
        var panel = new PanelContainer { CustomMinimumSize = new Vector2(Width, Height), MouseFilter = Control.MouseFilterEnum.Stop, FocusMode = Control.FocusModeEnum.All };
        panel.AddThemeStyleboxOverride("panel", Look.Board);
        panel.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        // Esc closes this window only: the key is taken here, so the game's area menu below stays open
        panel.GuiInput += e =>
        {
            if (e is InputEventKey { Pressed: true, Keycode: Key.Escape } || e.IsActionPressed("ui_cancel"))
            {
                panel.AcceptEvent();
                Close();
            }
        };
        center.AddChild(panel);
        Panel = panel;

        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, CustomMinimumSize = new Vector2(Width - 40, Height - 40) };
        panel.AddChild(scroll);
        _body = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _body.AddThemeConstantOverride("separation", 12);
        scroll.AddChild(_body);
        tree.Root.CallDeferred(Node.MethodName.AddChild, _layer);
    }

    public void Open(Game game, CombatArea area, Enemy enemy)
    {
        _game = game;
        bool otherArea = !ReferenceEquals(area, _area);
        _area = area;
        _enemy = enemy;
        if (otherArea) _preview = null;
        _layer.Visible = true;
        Build();
        Callable.From(() => { if (Visible) Panel.GrabFocus(); }).CallDeferred();
    }

    public void Close()
    {
        _layer.Visible = false;
        _todo.Clear();
    }

    /// <summary>For the tests: preview a stance as the stance buttons do.</summary>
    public void PreviewStance(AttackStance? stance)
    {
        _preview = stance;
        Build();
    }

    public void SetHours(int hours) { _hours = hours; Build(); }

    /// <summary>The window's words (labels and buttons), for the tests.</summary>
    public List<string> Texts()
    {
        var found = new List<string>();
        void Walk(Node n)
        {
            if (n is CanvasItem { Visible: false }) return;
            if (n is Label l && l.Text != "") found.Add(l.Text);
            if (n is Button b && b.Text != "") found.Add(b.Text);
            foreach (Node c in n.GetChildren()) Walk(c);
        }
        Walk(_body);
        return found;
    }

    /// <summary>Called every frame: the comparison table fills in (one monster per frame); setup changes redraw.</summary>
    public void Tick()
    {
        if (!Visible || _game == null || _area == null || _enemy == null) return;
        if (Forecast.Current(_game, _preview) is { } now && _setup != null && now.Signature != _setup.Signature) { Build(); return; }
        if (_main is { Ready: true } && _mainWaiting) { Build(); return; }
        if (_setup == null) return;
        bool changed = false;
        for (int i = _waiting.Count - 1; i >= 0; i--)
        {
            if (!_waiting[i].Ready) continue;
            Outlook o = _waiting[i];
            _waiting.RemoveAt(i);
            if (_rows.TryGetValue(o.Enemy, out HBoxContainer? row) && GodotObject.IsInstanceValid(row)) FillRow(row, o);
            changed = true;
        }
        ulong start = Time.GetTicksMsec();
        while (_todo.Count > 0 && Time.GetTicksMsec() - start < 12)
        {
            Enemy e = _todo.Dequeue();
            Outlook o = Forecast.For(_game, _setup, _area, e);
            if (!o.Ready) { _waiting.Add(o); continue; }
            if (_rows.TryGetValue(e, out HBoxContainer? row) && GodotObject.IsInstanceValid(row)) FillRow(row, o);
            changed = true;
        }
        if (changed && TableDone) MarkBest();
    }

    // ------------------------------------------------------------------------------------------- building

    void Build()
    {
        foreach (Node n in _body.GetChildren()) { _body.RemoveChild(n); n.QueueFree(); }
        _rows.Clear();
        _todo.Clear();
        _waiting.Clear();
        if (_game == null || _area == null || _enemy == null) return;
        if (Visible) Callable.From(() => { if (Visible && !Panel.HasFocus()) Panel.GrabFocus(); }).CallDeferred();
        _setup = Forecast.Current(_game, _preview);
        if (_setup == null) { _body.AddChild(Look.Body("The game's combat was not found.")); return; }
        Outlook o = Forecast.For(_game, _setup, _area, _enemy);
        _main = o;
        _mainWaiting = !o.Ready;
        _body.AddChild(Header());
        if (!o.Ready)
        {
            _body.AddChild(Look.Body("Working the fight out hit by hit (2,000 test kills)..."));
            return;
        }
        _body.AddChild(VerdictCard(o));
        _body.AddChild(SetupBar(_setup, o));
        if (o.Fight != null)
        {
            var tiles = new HBoxContainer();
            tiles.AddThemeConstantOverride("separation", 12);
            tiles.AddChild(Tile("KILLS PER HOUR", PaceLines(o)));
            tiles.AddChild(Tile("XP PER HOUR", XpLines(o)));
            tiles.AddChild(Tile("SUPPLIES", SupplyLines(o)));
            _body.AddChild(tiles);
        }
        _body.AddChild(Details(o));
        if (Comparison() is { } table) _body.AddChild(table);
    }

    /// <summary>The monster, where it lives, Close.</summary>
    Control Header()
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 14);
        row.AddChild(Look.Icon(_enemy!.Sprite, 52));
        var names = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, Alignment = BoxContainer.AlignmentMode.Center };
        names.AddChild(Look.Heading(_enemy.DisplayName.Text));
        names.AddChild(Look.Caption($"Combat Advisor  ·  {_area!.DisplayName.Text}  ·  level {_enemy.CombatLevel}  ·  {_enemy.BaseMaxHitpoints} HP"));
        row.AddChild(names);
        Button close = Look.Push("Close");
        close.Pressed += Close;
        row.AddChild(close);
        return row;
    }

    // ------------------------------------------------------------------------------------------- 1. is it safe?

    /// <summary>The answer first: the verdict, one sentence, the gauge, and what to do when it is not safe.</summary>
    Control VerdictCard(Outlook o)
    {
        Color colour = Words.Of(o.Verdict);
        var card = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        card.AddThemeStyleboxOverride("panel", Look.Card);
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 14);
        row.AddChild(new ColorRect { Color = colour, CustomMinimumSize = new Vector2(4, 0), SizeFlagsVertical = Control.SizeFlags.ExpandFill });
        var box = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        box.AddThemeConstantOverride("separation", 8);

        var top = new HBoxContainer();
        top.AddThemeConstantOverride("separation", 12);
        Control pill = Words.Pill(o.Verdict, 18);
        pill.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        top.AddChild(pill);
        top.AddChild(Wrap(Headline(o), o.Verdict == Verdict.Safe ? Look.Cream : colour, 16));
        box.AddChild(top);
        if (Advice(o) is { Length: > 0 } advice) box.AddChild(Wrap(advice, Look.Cream, 13));
        if (o.HasSafety) box.AddChild(Gauge(o));
        row.AddChild(box);
        card.AddChild(row);
        card.TooltipText = "\"Safe\": no single hit can take you from just above your auto-eat point to 0. "
                           + "Worked out from your gear, stance, food and every bonus you have now (prayers, potions, pets...). It is not a promise against bad luck.";
        return card;
    }

    /// <summary>The verdict in one plain sentence.</summary>
    string Headline(Outlook o)
    {
        string name = _enemy!.DisplayName.Text;
        return o.Reason switch
        {
            "safe" => $"Safe to leave running: its biggest hit ({o.EnemyMaxHit}) is smaller than your auto-eat point ({o.EatAt} HP).",
            "harmless" => $"Safe to leave running: {name} cannot hurt you with this setup.",
            "overEat" => $"Risky: one hit ({o.EnemyMaxHit}) can take you from just above your auto-eat point ({o.EatAt} HP) to 0.",
            "defeated" => "Risky: in 2,000 test kills its special attacks and effects knocked you out at least once.",
            "noFood" => "Risky: no food selected, and it hurts you. Nothing would heal you.",
            "cannotWin" => "Risky: " + (o.Problem != "" ? o.Problem : o.Why),
            "deadly" => $"Deadly: one hit ({o.EnemyMaxHit}) is more than all your HP ({o.MaxHp}).",
            _ => "Not worked out: " + o.Why,
        };
    }

    /// <summary>What to do about it (nothing when it is safe).</summary>
    string Advice(Outlook o)
    {
        int hit = o.EnemyMaxHit;
        return o.Reason switch
        {
            "overEat" => $"To make it safe: raise your auto-eat point above {hit} HP, or bring its hit below {o.EatAt} (Defence, armour, damage reduction).",
            "defeated" => "More HP, Defence, armour or damage reduction makes the rare bad streaks survivable.",
            "noFood" => "Select food: auto-eat then heals you whenever your HP gets low.",
            "deadly" => $"You need more than {hit} max HP, or bring its hit below {o.MaxHp} (Defence, armour, damage reduction). "
                        + $"To leave it running, its hit must stay below your auto-eat point ({o.EatAt} HP).",
            _ => "",
        };
    }

    /// <summary>
    /// Its biggest hit as a bar from 0, against two lines: your auto-eat point (gold) and your max HP (white). Stopping
    /// before the gold line is safe; crossing it is risky; crossing the white line too is deadly.
    /// </summary>
    Control Gauge(Outlook o)
    {
        int max = Math.Max(1, o.MaxHp), eat = o.EatAt, hit = o.EnemyMaxHit;
        bool food = _setup?.Food != null;
        Color hc = Words.Of(hit >= max ? Verdict.Deadly : hit >= eat ? Verdict.Risky : Verdict.Safe);
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 4);

        var numbers = new HBoxContainer();
        numbers.AddThemeConstantOverride("separation", 22);
        numbers.AddChild(Look.Text($"Its biggest hit: {hit}", "fs14_bold", 14, hc));
        numbers.AddChild(Look.Text(food ? $"You auto-eat at: {eat} HP" : "Auto-eat: no food selected", "fs14_bold", 14, food ? Look.Gold : Words.RiskyColour));
        numbers.AddChild(Look.Text($"Your max HP: {max}", "fs14_bold", 14, Look.Cream));
        var spare = Look.Text(hit >= max ? $"{hit - max} more than your max HP"
                              : hit >= eat ? $"{hit - eat + 1} too much to leave running"
                              : $"{eat - hit} HP to spare", "fs14_bold", 14, hc);
        spare.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        spare.HorizontalAlignment = HorizontalAlignment.Right;
        numbers.AddChild(spare);
        box.AddChild(numbers);

        var bar = new Control { CustomMinimumSize = new Vector2(0, 22), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        double scale = Math.Max(max, hit) * 1.04;
        bar.Draw += () =>
        {
            float w = bar.Size.X, h = bar.Size.Y;
            float X(double v) => (float)Math.Clamp(v / scale, 0, 1) * w;
            bar.DrawRect(new Rect2(0, 0, w, h), new Color(0.07f, 0.06f, 0.05f));
            bar.DrawRect(new Rect2(0, 0, X(eat), h), new Color(0.08f, 0.15f, 0.08f));                     // safe: below auto-eat
            bar.DrawRect(new Rect2(X(eat), 0, X(max) - X(eat), h), new Color(0.14f, 0.11f, 0.07f));        // risky: above it
            if (hit > max) bar.DrawRect(new Rect2(X(max), 0, w - X(max), h), new Color(0.17f, 0.06f, 0.05f)); // past all your HP
            if (hit > 0) bar.DrawRect(new Rect2(0, h * 0.22f, Math.Max(6, X(hit)), h * 0.56f), hc);           // its biggest hit
            bar.DrawRect(new Rect2(Math.Max(0, X(eat) - 1.5f), 0, 3, h), food ? Look.Gold : new Color(0.5f, 0.45f, 0.35f));
            bar.DrawRect(new Rect2(Math.Min(w - 3, X(max) - 1.5f), 0, 3, h), Look.Cream);
            bar.DrawRect(new Rect2(0, 0, w, h), new Color(0.32f, 0.29f, 0.26f), false, 1);
        };
        box.AddChild(bar);
        return box;
    }

    // ------------------------------------------------------------------------------------------- 2. with what?

    /// <summary>What they fight with, their special attack (counted), and the stance preview.</summary>
    Control SetupBar(Setup s, Outlook o)
    {
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 6);
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        row.AddChild(Look.Caption("You fight with:"));
        var weapon = Combat.Weapon(s.Fighter.Gear);
        if (weapon != null) { row.AddChild(Look.Icon(weapon.Value.Item.Icon, 22)); row.AddChild(Look.Body(weapon.Value.Item.DisplayName.GetPluralText(1), 13)); }
        else row.AddChild(Look.Body("Fists", 13));
        if (o.Fight is { MySpecials.Count: > 0 } fight)
        {
            bool partly = fight.LeftOut.Count > 0;
            Label tag = Look.Text(string.Join(", ", fight.MySpecials.Select(x => x.Name)) + (partly ? " partly counted" : " counted"),
                                  "fs12_bold_grey", 12, partly ? Words.RiskyColour : Look.Grey);
            tag.MouseFilter = Control.MouseFilterEnum.Stop;
            tag.TooltipText = SpecialText(s, fight);
            row.AddChild(tag);
        }
        if (s.Fighter.Spell != null && s.Fighter.Stance.CombatStyle.UsesSpells)
        {
            row.AddChild(Look.Caption("·"));
            row.AddChild(Look.Body(s.Fighter.Spell.DisplayName.Text, 13));
        }
        row.AddChild(Look.Caption("·"));
        if (s.Food != null)
        {
            row.AddChild(Look.Icon(s.Food.Icon, 22));
            row.AddChild(Look.Body($"{s.Food.DisplayName.GetPluralText(2)} x{s.FoodOwned.ToString("N0", C)}", 13));
        }
        else row.AddChild(Look.Text("no food selected", "fs12_bold_grey", 12, Words.RiskyColour));
        row.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        row.AddChild(Look.Caption("Stance:"));
        foreach (AttackStance stance in Forecast.Stances(_game!, s))
        {
            Button b = Look.Choice(stance.DisplayName.Text, ReferenceEquals(stance, s.Fighter.Stance));
            AttackStance chosen = stance;
            b.TooltipText = ReferenceEquals(stance, s.PlayerStance) ? "Your stance in the game." : "Try it: only the numbers here change, never your stance in the game.";
            b.Pressed += () => PreviewStance(ReferenceEquals(chosen, s.PlayerStance) ? null : chosen);
            row.AddChild(b);
        }
        box.AddChild(row);
        if (s.Previewing)
        {
            var note = new HBoxContainer();
            note.AddThemeConstantOverride("separation", 10);
            note.AddChild(Look.Text($"Preview only: these numbers are for {s.Fighter.Stance.DisplayName.Text}. Your stance in the game stays {s.PlayerStance.DisplayName.Text}.",
                                    "fs12_bold_grey", 12, Look.Gold));
            Button back = Look.Push("Back to " + s.PlayerStance.DisplayName.Text);
            back.Pressed += () => PreviewStance(null);
            note.AddChild(back);
            box.AddChild(note);
        }
        return box;
    }

    /// <summary>Their own special attacks in words, and that they are counted (or what of them is not).</summary>
    string SpecialText(Setup s, Fight fight)
    {
        if (_game == null) return "";
        Attack plain = _game.Data.GetDataRegistryOutput<Attack>().GetObjectById("MelvorBase:Normal");
        var spell = s.Fighter.Stance.CombatStyle.UsesSpells ? s.Fighter.Spell : null;
        var lines = Specials.MyAttacks(s.Fighter.Gear, spell, plain).Where(a => a.Attack != plain).Select(a => Words.Special(a.Attack, a.Chance)).ToList();
        lines.Add(fight.LeftOut.Count == 0 ? "Counted in every number here, hit by hit." : "Counted in every number here, except: " + string.Join(", ", fight.LeftOut) + ".");
        return string.Join("\n", lines);
    }

    // ------------------------------------------------------------------------------------------- 3. what do I get?

    static IEnumerable<Control> PaceLines(Outlook o)
    {
        Fight f = o.Fight!;
        yield return Big(Words.Short(o.KillsPerHour), "kills per hour");
        yield return Line($"One kill every {f.SecondsPerKill.ToString("0.0", C)} s");
    }

    static IEnumerable<Control> XpLines(Outlook o)
    {
        var all = o.XpPerHour.OrderByDescending(x => x.PerHour).ToList();
        if (all.Count == 0) { yield return Line("No XP from this stance."); yield break; }
        var main = o.MainXp!.Value;
        yield return Big(Words.Short(main.PerHour), Combat.Local(main.Level.Id) + " XP");
        foreach (var x in all.Where(x => !ReferenceEquals(x.Level, main.Level))) yield return Line($"{Combat.Local(x.Level.Id)}: {Words.Short(x.PerHour)} XP");
    }

    IEnumerable<Control> SupplyLines(Outlook o)
    {
        var needs = new List<(Item? Item, string Name, double PerHour, int Have)>();
        if (o.FoodPerHour > 0) needs.Add((_setup!.Food, _setup.Food?.DisplayName.GetPluralText(2) ?? "Food", o.FoodPerHour, _setup.FoodOwned));
        if (o.AmmoPerHour > 0) needs.Add((_setup!.Ammo, _setup.Ammo?.DisplayName.GetPluralText(2) ?? "Ammunition", o.AmmoPerHour, _setup.AmmoOwned));
        foreach (var (rune, perHour) in o.RunesPerHour.Where(r => r.PerHour > 0)) needs.Add((rune, rune.DisplayName.GetPluralText(2), perHour, Forecast.Owned(_game!, rune)));
        if (needs.Count == 0)
        {
            yield return Big("0", "used per hour");
            yield return Line("No food, ammunition or runes used.");
            yield break;
        }
        yield return Big(Words.Short(needs[0].PerHour), needs[0].Name.ToLowerInvariant() + " per hour");

        var toggle = new HBoxContainer();
        toggle.AddThemeConstantOverride("separation", 6);
        toggle.AddChild(Look.Caption("For:"));
        foreach (int h in new[] { 1, 8 })
        {
            Button b = Look.Choice(h + " h", h == _hours);
            int hours = h;
            b.Pressed += () => SetHours(hours);
            toggle.AddChild(b);
        }
        Button reserve = Look.Choice("+20% spare", _reserve);
        reserve.TooltipText = "Plan 20% more than the average, for unlucky streaks. The per-hour numbers stay the same.";
        reserve.Pressed += () => { _reserve = !_reserve; Build(); };
        toggle.AddChild(reserve);
        yield return toggle;

        double factor = _hours * (_reserve ? 1.2 : 1.0);
        (string Name, double Lasts)? first = null;
        foreach (var (item, name, perHour, have) in needs)
        {
            double want = Math.Ceiling(perHour * factor);
            double lasts = perHour > 0 ? have / perHour : double.PositiveInfinity;
            if (first == null || lasts < first.Value.Lasts) first = (name, lasts);
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 6);
            if (item != null) row.AddChild(Look.Icon(item.Icon, 20));
            row.AddChild(Look.Text($"{name}: need {Words.Short(want)} for {_hours} h, you have {Words.Short(have)}", "fs12_bold_grey", 12, have >= want ? Look.Cream : Words.RiskyColour));
            yield return row;
        }
        if (first != null)
            yield return Line(needs.Count == 1 ? $"What you have lasts {Words.Hours(first.Value.Lasts)}." : $"{first.Value.Name} run out first: in {Words.Hours(first.Value.Lasts)}.",
                              first.Value.Lasts < _hours ? Words.RiskyColour : Look.Grey);
    }

    // ------------------------------------------------------------------------------------------- the how (hidden)

    /// <summary>"Show details": the numbers behind the answer, for those who want them.</summary>
    Control Details(Outlook o)
    {
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 4);
        Button toggle = Look.Push(_details ? "Hide details" : "Show details");
        toggle.SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin;
        toggle.Pressed += () => { _details = !_details; Build(); };
        box.AddChild(toggle);
        if (!_details) return box;
        var lines = new List<string>();
        if (o.Fight is { } f)
        {
            lines.Add($"You hit {f.HitChance * 100:0}% of the time for {f.MinHit}-{f.MaxHit} damage, one attack every {f.AttackSeconds.ToString("0.0#", C)} s. "
                      + $"A kill takes {f.SecondsPerKill.ToString("0.0", C)} s, 3 s of it waiting for the next monster.");
            lines.Add($"{_enemy!.DisplayName.Text} hits you {f.EnemyHitChance * 100:0}% of the time, at most {f.EnemyMaxHit} in one go.");
            if (_setup?.Food != null) lines.Add($"Auto-eat: at {o.EatAt} of {o.MaxHp} HP; each {_setup.Food.DisplayName.GetPluralText(1)} heals {f.Heal}.");
            if (f.MySpecials.Count > 0) lines.AddRange(SpecialText(_setup!, f).Split('\n'));
            if (o.Played) lines.Add("Worked out by playing 2,000 kills hit by hit, special attacks and effects included.");
        }
        else if (o.HasSafety) lines.Add($"Its biggest hit {o.EnemyMaxHit}, your max HP {o.MaxHp}, auto-eat at {o.EatAt} HP.");
        lines.Add("From your gear, stance, food, levels and every bonus you have now (prayers, potions, pets...). Nothing in your game is changed.");
        foreach (string l in lines) box.AddChild(Line(l, Look.Grey));
        return box;
    }

    // ------------------------------------------------------------------------------------------- the area table

    /// <summary>The other monsters of the area side by side (none when it is the only one you can fight there).</summary>
    Control? Comparison()
    {
        var enemies = _area!.Enemies.Where(e => Combat.Unlocked(_area, e)).ToList();
        if (enemies.Count < 2) return null;
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 2);
        var title = new HBoxContainer();
        title.AddChild(Look.Body("Monsters in " + _area.DisplayName.Text, 15));
        var hint = Look.Caption("Click one to see it");
        hint.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        hint.HorizontalAlignment = HorizontalAlignment.Right;
        title.AddChild(hint);
        box.AddChild(title);
        box.AddChild(Pad(Header("Monster", "Safety", "Kills/h", "XP/h", "Food/h", "")));
        var plain = new StyleBoxEmpty { ContentMarginLeft = 8, ContentMarginRight = 8, ContentMarginTop = 1, ContentMarginBottom = 1 };
        var lit = Look.Flat(new Color(1, 1, 1, 0.06f), new Color(0, 0, 0, 0), 8, 1, 3);
        var here = Look.Flat(new Color(1f, 0.82f, 0.43f, 0.10f), Look.Gold, 8, 1, 3);
        foreach (Enemy e in enemies)
        {
            var row = Header(e.DisplayName.Text, "...", "", "", "", "");
            bool current = ReferenceEquals(e, _enemy);
            row.GetChild<Label>(0).Modulate = current ? Look.Gold : Look.Cream;
            var frame = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Stop, MouseDefaultCursorShape = Control.CursorShape.PointingHand };
            frame.AddThemeStyleboxOverride("panel", current ? here : plain);
            if (!current)
            {
                frame.MouseEntered += () => frame.AddThemeStyleboxOverride("panel", lit);
                frame.MouseExited += () => frame.AddThemeStyleboxOverride("panel", plain);
            }
            Enemy target = e;
            frame.GuiInput += ev =>
            {
                if (ev is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) Callable.From(() => { _enemy = target; Build(); }).CallDeferred();
            };
            row.MouseFilter = Control.MouseFilterEnum.Ignore;
            frame.AddChild(row);
            box.AddChild(frame);
            _rows[e] = row;
            if (Forecast.Cached(_setup!, _area, e, out Outlook? known) && known != null) FillRow(row, known);
            else _todo.Enqueue(e);
        }
        if (_todo.Count == 0) CallBest();
        return box;
    }

    static Control Pad(Control c)
    {
        var m = new MarginContainer();
        m.AddThemeConstantOverride("margin_left", 8);
        m.AddChild(c);
        return m;
    }

    void CallBest() => Callable.From(MarkBest).CallDeferred();

    static HBoxContainer Header(string a, string b, string c, string d, string e, string f)
    {
        var row = new HBoxContainer { CustomMinimumSize = new Vector2(0, 24) };
        row.AddThemeConstantOverride("separation", 8);
        float[] widths = { 270, 100, 90, 180, 90, 150 };
        string[] texts = { a, b, c, d, e, f };
        for (int i = 0; i < texts.Length; i++)
        {
            Label l = i == 0 ? Look.Body(texts[i], 14) : Look.Caption(texts[i]);
            l.CustomMinimumSize = new Vector2(widths[i], 0);
            l.ClipText = true;
            l.MouseFilter = Control.MouseFilterEnum.Ignore;
            row.AddChild(l);
        }
        return row;
    }

    static void FillRow(HBoxContainer row, Outlook o)
    {
        Label verdict = row.GetChild<Label>(1);
        verdict.Text = Words.Name(o.Verdict);
        verdict.Modulate = Words.Of(o.Verdict);
        row.GetChild<Label>(2).Text = o.Fight == null ? "-" : Words.Short(o.KillsPerHour);
        row.GetChild<Label>(3).Text = o.MainXp is { } xp ? $"{Words.Short(xp.PerHour)} {Combat.Local(xp.Level.Id)}" : "-";
        row.GetChild<Label>(4).Text = o.Fight == null ? "-" : Words.Short(o.FoodPerHour);
        if (row.GetParent() is Control frame) frame.TooltipText = o.Why;
        row.SetMeta("xp", o.Verdict == Verdict.Safe && o.Problem == "" && o.MainXp is { } m ? m.PerHour : -1.0);
    }

    /// <summary>"Best XP here": the most XP among safe monsters with nothing missing; never an unknown one.</summary>
    void MarkBest()
    {
        HBoxContainer? best = null;
        double most = 0;
        foreach (HBoxContainer row in _rows.Values)
        {
            if (!GodotObject.IsInstanceValid(row)) continue;
            row.GetChild<Label>(5).Text = "";
            double xp = row.HasMeta("xp") ? row.GetMeta("xp").AsDouble() : -1;
            if (xp > most) { most = xp; best = row; }
        }
        if (best == null) return;
        Label tag = best.GetChild<Label>(5);
        tag.Text = "Best safe XP";
        tag.Modulate = Look.Gold;
    }

    /// <summary>The monster marked best in the table (for the tests), or null.</summary>
    public string? BestRow() =>
        _rows.FirstOrDefault(r => GodotObject.IsInstanceValid(r.Value) && r.Value.GetChild<Label>(5).Text != "").Key?.DisplayName.Text;

    public bool TableDone => _todo.Count == 0 && _waiting.Count == 0 && !_mainWaiting;
    bool _mainWaiting;

    // ------------------------------------------------------------------------------------------- small pieces

    static Control Tile(string title, IEnumerable<Control> lines)
    {
        var card = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 132) };
        card.AddThemeStyleboxOverride("panel", Look.Card);
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 5);
        box.AddChild(Look.Text(title, "fs12_bold_grey", 12, Look.Grey));
        foreach (Control l in lines) box.AddChild(l);
        card.AddChild(box);
        return card;
    }

    static Control Big(string value, string unit)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 6);
        Label v = Look.Body(value, 20);
        v.Modulate = Look.Gold;
        row.AddChild(v);
        row.AddChild(Look.Caption(unit));
        return row;
    }

    static Label Line(string text, Color? colour = null)
    {
        Label l = Look.Text(text, "fs12_bold_grey", 12, colour ?? Look.Cream);
        l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        return l;
    }

    static Label Wrap(string text, Color colour, int size = 14)
    {
        Label l = Look.Body(text, size);
        l.Modulate = colour;
        l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        l.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        return l;
    }
}
