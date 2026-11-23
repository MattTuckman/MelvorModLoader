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
        root.AddChild(_body);

        tree.Root.AddChild(_layer);
    }

    public void Show()
    {
        _close.Text = OneStyleMod.Instance?.Key.With("Close") ?? "Close";
        _layer.Visible = true;
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

    static string Signature() => $"{Patches.Current}|{Patches.ClassLevel}|{Patches.Tier}|{Patches.MappedLevels}|{Patches.Problems.Count}";

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
        ov.AddChild(Bullet("The combat triangle never works against you; its advantages stay."));
        ov.AddChild(Bullet($"Quest goals and monsters that ask for another style take {style} (the archery targets of the first main quest)."));
        ov.AddChild(Bullet($"{Patches.MappedLevels} requirements on another style's level read your {skill} level: Battle Loadout, thieving distractions, "
                           + "the combat constellations, item upgrades, Hunting by hand, rings and amulets"
                           + (c == Class.Ranged ? ", and crossbows and throwing hammers that also ask for Strength." : ".")));
        if (c is Class.Melee or Class.Ranged) ov.AddChild(Bullet($"Your Defence counts in full when you dodge spells (the game uses Magic for 70% of it)."));
        ov.AddChild(Text("Weapons and armour of other styles keep their own requirements. Nothing is written into your save.", Style.Muted, 12));
        open.AddChild(ov);
        _body.AddChild(open);

        if (Patches.Problems.Count > 0)
            _body.AddChild(Text("Not working with this game version: " + string.Join("; ", Patches.Problems), Style.Bad, 12));
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
