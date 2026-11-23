using System;
using System.Collections.Generic;
using System.Linq;
using CheatMenu;
using Godot;

namespace OneStyle;

/// <summary>
/// The window: four cards on top (Off, Melee, Ranged, Magic), then the class's title and tier with the bonuses it
/// gives now and at the next tier, and what the class opens. A click on a card changes the class at once.
/// </summary>
public sealed class ClassWindow
{
    readonly CanvasLayer _layer = new() { Layer = 118, Name = "OneStyle", Visible = false };
    readonly PanelContainer _panel;
    readonly Dictionary<Class, Button> _cards = new();
    readonly VBoxContainer _body = new();
    readonly Button _close;
    double _clock;
    string _signature = "";

    public bool Visible => _layer.Visible;

    /// <summary>For tests: the card of a class (pressing it is the player's click).</summary>
    public Button Card(Class c) => _cards[c];
    /// <summary>For tests: all text shown under the cards.</summary>
    public string BodyText => string.Join(" | ", Labels(_body).Select(l => l.Text).Where(t => t != ""));

    static IEnumerable<Label> Labels(Node n)
    {
        foreach (Node c in n.GetChildren())
        {
            if (c is Label l) yield return l;
            foreach (Label inner in Labels(c)) yield return inner;
        }
    }

    static Texture2D? Tex(string path)
    {
        try { return ResourceLoader.Exists(path) ? GD.Load<Texture2D>(path) : null; } catch { return null; }
    }

    static Texture2D? IconOf(Class c) => c switch
    {
        Class.Melee => Tex("res://Assets/Icons/Attack.png"),
        Class.Ranged => Tex("res://Assets/Icons/Ranged.png"),
        Class.Magic => Tex("res://Assets/Icons/Magic.png"),
        _ => null,
    };

    static string Blurb(Class c) => c switch
    {
        Class.Melee => "Swords, axes and maces. Attack and Strength carry you.",
        Class.Ranged => "Bows, crossbows and throwing weapons. Ranged carries you.",
        Class.Magic => "Staves and spells. Magic carries you.",
        _ => "The game as it is: every style counts.",
    };

    public ClassWindow(SceneTree tree)
    {
        var shade = new ColorRect { Color = new Color(0, 0, 0, 0.35f), MouseFilter = Control.MouseFilterEnum.Stop };
        shade.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        shade.GuiInput += e => { if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) Hide(); };
        _layer.AddChild(shade);
        var center = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _layer.AddChild(center);

        _panel = new PanelContainer { CustomMinimumSize = new Vector2(820, 0), Theme = Style.Build() };
        StyleBoxFlat frame = Style.Box(Style.Window, 8, Style.AccentDim, 1, 18, 14);
        frame.ShadowColor = new Color(0, 0, 0, 0.55f);
        frame.ShadowSize = 14;
        _panel.AddThemeStyleboxOverride("panel", frame);
        center.AddChild(_panel);

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 12);
        _panel.AddChild(root);

        // ---- header
        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 10);
        Label title = Style.Label("ONE STYLE", Style.Accent, 20);
        header.AddChild(title);
        Label sub = Style.Label("Play the whole game with one combat style. You can change it at any time.", Style.Muted, 13);
        sub.VerticalAlignment = VerticalAlignment.Center;
        sub.SizeFlagsVertical = Control.SizeFlags.Fill;
        header.AddChild(sub);
        header.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        _close = new Button { Text = "Close" };
        _close.Pressed += Hide;
        header.AddChild(_close);
        root.AddChild(header);

        // ---- the four cards
        var cards = new HBoxContainer();
        cards.AddThemeConstantOverride("separation", 10);
        foreach (Class c in new[] { Class.None, Class.Melee, Class.Ranged, Class.Magic })
        {
            var card = new Button
            {
                ToggleMode = true, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 118),
                TooltipText = Blurb(c), FocusMode = Control.FocusModeEnum.None,
            };
            var inside = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore, Alignment = BoxContainer.AlignmentMode.Center };
            inside.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            inside.OffsetLeft = 10;
            inside.OffsetRight = -10;
            inside.AddThemeConstantOverride("separation", 4);
            var icon = new TextureRect
            {
                Texture = IconOf(c), CustomMinimumSize = new Vector2(40, 40), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, MouseFilter = Control.MouseFilterEnum.Ignore,
                SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter,
            };
            inside.AddChild(icon);
            Label name = Style.Label(Classes.Name(c), Style.Text, 17);
            name.HorizontalAlignment = HorizontalAlignment.Center;
            name.MouseFilter = Control.MouseFilterEnum.Ignore;
            inside.AddChild(name);
            Label blurb = Style.Label(Blurb(c), Style.Muted, 11);
            blurb.HorizontalAlignment = HorizontalAlignment.Center;
            blurb.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            blurb.MouseFilter = Control.MouseFilterEnum.Ignore;
            inside.AddChild(blurb);
            card.AddChild(inside);
            Class picked = c;
            card.Pressed += () => { Patches.Set(picked); Repaint(); };
            _cards[c] = card;
            cards.AddChild(card);
        }
        root.AddChild(cards);

        _body.AddThemeConstantOverride("separation", 8);
        _body.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        // Since 0.2 the text below the cards is longer than a small screen: it scrolls.
        _scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _scroll.AddChild(_body);
        root.AddChild(_scroll);

        tree.Root.AddChild(_layer);
        _tree = tree;
    }

    readonly ScrollContainer _scroll;
    readonly SceneTree _tree;

    /// <summary>The scrolling part as tall as the screen allows (the header and the cards take about 300 pixels).</summary>
    void FitHeight() =>
        _scroll.CustomMinimumSize = new Vector2(0, Math.Clamp(_tree.Root.GetVisibleRect().Size.Y - 330, 240, 620));

    public void Show()
    {
        _close.Text = OneStyleMod.Instance?.Key.With("Close") ?? "Close";
        _layer.Visible = true;
        FitHeight();
        Repaint();
    }

    public void Hide() => _layer.Visible = false;

    /// <summary>Called every frame by the mod: once a second, follow the levels.</summary>
    public void Tick(double delta)
    {
        if (!_layer.Visible) return;
        _clock += delta;
        if (_clock < 1) return;
        _clock = 0;
        if (Signature() != _signature) Repaint();
    }

    static string Signature() => $"{Patches.Current}|{Patches.ClassLevel}|{Patches.Tier}|{Patches.MappedLevels}|{Patches.Problems.Count}|{Rings.Problems.Count}|{Patches.EquivalentsActive.Count}";

    void Repaint()
    {
        _signature = Signature();
        Class c = Patches.Current;
        foreach (var (cls, card) in _cards)
        {
            card.SetPressedNoSignal(cls == c);
            card.AddThemeStyleboxOverride("normal", Style.Box(cls == c ? Style.AccentDim : Style.Card, 6, cls == c ? Style.Accent : Style.Line, 1, 8, 8));
            card.AddThemeStyleboxOverride("pressed", Style.Box(Style.AccentDim, 6, Style.Accent, 2, 8, 8));
            card.AddThemeStyleboxOverride("hover", Style.Box(cls == c ? Style.AccentDim : Style.ControlHover, 6, Style.Accent, 1, 8, 8));
        }
        foreach (Node n in _body.GetChildren()) { _body.RemoveChild(n); n.QueueFree(); }

        if (c == Class.None)
        {
            _body.AddChild(Text("No class chosen: the game plays as usual. Pick Melee, Ranged or Magic above.", Style.Muted, 14));
            _body.AddChild(RingSection());
            return;
        }

        int level = Patches.ClassLevel, tier = Patches.Tier;
        string skill = c == Class.Melee ? "Attack / Strength" : Classes.Name(c);

        // ---- title and tier
        var titleCard = Section();
        var tv = new VBoxContainer();
        tv.AddThemeConstantOverride("separation", 4);
        tv.AddChild(Text($"{Classes.Title(c, tier)}  ·  tier {tier} of 5  ·  class level {level} ({skill})", Style.Accent, 17));
        int? next = Classes.TierLevels.Cast<int?>().FirstOrDefault(t => t > level);
        tv.AddChild(Text(next is int n2
            ? $"Next tier at level {n2}: {Classes.Title(c, tier + 1)}."
            : "Highest tier reached.", Style.Muted, 13));
        var bar = new ProgressBar { MinValue = 0, MaxValue = 5, Value = tier, ShowPercentage = false, CustomMinimumSize = new Vector2(0, 8) };
        bar.AddThemeStyleboxOverride("background", Style.Box(Style.Field, 4));
        bar.AddThemeStyleboxOverride("fill", Style.Box(Style.Accent, 4));
        tv.AddChild(bar);
        List<string> now = Patches.PerkLines(c, tier).ToList();
        tv.AddChild(Text(now.Count > 0 ? "Now: " + string.Join(",  ", now) : "No tier bonus yet: the first comes at level 20.", Style.Good, 13));
        if (tier < 5) tv.AddChild(Text("Next tier: " + string.Join(",  ", Patches.PerkLines(c, tier + 1)), Style.Muted, 13));
        titleCard.AddChild(tv);
        _body.AddChild(titleCard);

        // ---- what the class opens
        var open = Section();
        var ov = new VBoxContainer();
        ov.AddThemeConstantOverride("separation", 3);
        ov.AddChild(Text("What your class opens", Style.Text, 15));
        string style = Classes.Name(c).ToLowerInvariant();
        ov.AddChild(Bullet($"Monsters immune to {style} can be hit with {style}: Wind Drifter, the Amalgamations, Blood Sanctum."));
        ov.AddChild(Bullet("The combat triangle is off for your class: no penalty and no bonus against any monster."));
        ov.AddChild(Bullet($"Quest goals and monsters that ask for another style take {style} (the archery targets of the first main quest)."));
        ov.AddChild(Bullet($"{Patches.MappedLevels} requirements on another style's level read your {skill} level: Battle Loadout, thieving distractions, "
                           + "the combat constellations, item upgrades, Hunting by hand, rings and amulets"
                           + (c == Class.Ranged ? ", and crossbows and throwing hammers that also ask for Strength." : ".")));
        if (c is Class.Melee or Class.Ranged) ov.AddChild(Bullet($"Your Defence counts in full when you dodge spells (the game uses Magic for 70% of it)."));
        ov.AddChild(Text("Weapons and armour of other styles keep their own requirements. Nothing of the class is written into your save.", Style.Muted, 12));
        open.AddChild(ov);
        _body.AddChild(open);

        // ---- equal footing (0.2)
        var fair = Section();
        var fv = new VBoxContainer();
        fv.AddThemeConstantOverride("separation", 3);
        fv.AddChild(Text("Equal footing", Style.Text, 15));
        fv.AddChild(Bullet($"Weak spot: against every monster, your {style} stances meet its lowest evasion of all styles."));
        if (c is Class.Melee or Class.Ranged) fv.AddChild(Bullet("Elemental edge: +15% max hit against monsters with an element, as a mage with the right spell."));
        if (c == Class.Melee) fv.AddChild(Bullet("Attack and Strength share their XP: whatever one gains, the other gains too."));
        fv.AddChild(Bullet(c switch
        {
            Class.Melee => "Class identity: 3% lifesteal with melee.",
            Class.Ranged => "Class identity: +5% critical hit chance and +25% critical damage with ranged, and +30 light.",
            _ => "Class identity: +3 ward and 25% chance of double ward damage with magic, and +30 light.",
        }));
        if (c is Class.Ranged or Class.Magic)
        {
            fv.AddChild(Bullet($"The pets Fen and Bruno can drop from your {Classes.Name(c)} XP."));
            fv.AddChild(Bullet($"Your {Classes.Name(c)} guild ranks 3-5 and your constellation give what melee gets from the Attack and Strength guilds and its constellations."));
            fv.AddChild(Text(Patches.EquivalentsActive.Count > 0 ? "Earned: " + string.Join(";  ", Patches.EquivalentsActive) : "Earned: none yet.", Style.Good, 12));
        }
        fair.AddChild(fv);
        _body.AddChild(fair);
        _body.AddChild(RingSection());

        if (Patches.Problems.Count > 0)
            _body.AddChild(Text("Not working with this game version: " + string.Join("; ", Patches.Problems), Style.Bad, 12));
    }

    string _ringNote = "";

    /// <summary>The six ring slots (every class, and Off too) and the button to empty slots 2 to 6 before removing the mod.</summary>
    PanelContainer RingSection()
    {
        var box = Section();
        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 4);
        v.AddChild(Text($"{Rings.Total} ring slots", Style.Text, 15));
        v.AddChild(Text("Ring Slot 2 is open without its Crafting 150 lock, and four more ring slots are on the equipment grid. "
                        + "Each ring counts once: wear different rings. Works with every class, and with Off.", Style.Muted, 12));
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 10);
        var bank = new Button { Text = "Put rings 2-6 in the bank" };
        bank.Pressed += () =>
        {
            if (MelvorModLoader.GameAccess.CurrentGame is not { IsSaveLoaded: true } game) return;
            int before = Rings.InAdded(game), moved = Rings.ToBank(game);
            _ringNote = moved == before ? $"{moved} ring(s) put in the bank." : $"{moved} of {before} ring(s) put in the bank: make room in the bank for the rest.";
            Repaint();
        };
        row.AddChild(bank);
        if (_ringNote != "") row.AddChild(Text(_ringNote, Style.Good, 12));
        v.AddChild(row);
        v.AddChild(Text("Before you remove One Style, press this: without the mod the game drops what Ring Slot 2 to 6 hold.", Style.Bad, 12));
        if (Rings.Problems.Count > 0) v.AddChild(Text("Ring slots: " + string.Join("; ", Rings.Problems), Style.Bad, 12));
        box.AddChild(v);
        return box;
    }

    static PanelContainer Section()
    {
        var p = new PanelContainer();
        p.AddThemeStyleboxOverride("panel", Style.Box(Style.Card, 6, Style.Line, 1, 14, 10));
        return p;
    }

    static Label Text(string text, Color color, int size)
    {
        Label l = Style.Label(text, color, size);
        l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        return l;
    }

    static Label Bullet(string text) => Text("•  " + text, Style.Text, 13);
}
