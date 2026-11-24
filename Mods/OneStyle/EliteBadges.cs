using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using GamesByMalcs.Melvor2.Components.Characters;
using GamesByMalcs.Melvor2.Entities.Combat;
using Godot;

namespace OneStyle;

/// <summary>
/// What an elite is, right under its health bar, as in other action RPGs: a CHAMPION / UNIQUE pill, one pill per
/// affix with the game's own icon (its tooltip says what it does), the Unique's signature attack and passive, and the
/// bar itself framed in the elite's colour: red while enraged, a green flash when Vampiric heals, the ward hits left on
/// the Warded pill.
/// </summary>
public static partial class Elites
{
    const string BadgesName = "OneStyleEliteBadges", FrameName = "OneStyleEliteFrame";

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_EnemyHealthProgressBar")]
    static extern CharacterHealthProgressBar EnemyBarOf(CombatActiveView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_HealthProgress")]
    static extern TextureProgressBar HealthProgressOf(CharacterHealthProgressBar bar);

    public static readonly Dictionary<string, (string Icon, Color Color, string Text)> AffixInfo = new()
    {
        ["Hasted"] = ("res://Assets/Icons/TimeInterval_48.png", new Color(1f, 0.85f, 0.2f), "Hasted: attacks 12 to 18% faster."),
        ["Colossal"] = ("res://Assets/Icons/Hitpoints.png", new Color(0.85f, 0.35f, 0.3f), "Colossal: +25% hit points and +10% damage, but 10% slower."),
        ["Warded"] = ("res://Assets/Icons/Defence_64.png", new Color(0.45f, 0.8f, 1f), "Warded: a ward that takes the first 2 to 6 hits (each hit through it does less damage)."),
        ["Armoured"] = ("res://Assets/Icons/ArmourBroken.png", new Color(0.7f, 0.72f, 0.78f), "Armoured: takes 18% less damage."),
        ["Vampiric"] = ("res://Assets/Icons/Lifesteal.png", new Color(0.85f, 0.15f, 0.25f), "Vampiric: heals 10% of the damage it deals (at most 2.5% of its hit points a hit), fading away between 25 and 45 seconds."),
        ["Enraging"] = ("res://Assets/Icons/ablaze.png", new Color(1f, 0.45f, 0.15f), "Enraging: below 40% hit points it deals 20% more damage and attacks 10% faster."),
    };

    static readonly Dictionary<string, Texture2D?> s_icons = new();
    static Texture2D? Icon(string path)
    {
        if (s_icons.TryGetValue(path, out Texture2D? t)) return t;
        try { t = ResourceLoader.Exists(path) ? GD.Load<Texture2D>(path) : null; } catch { t = null; }
        return s_icons[path] = t;
    }

    static double s_healFlashUntil;

    /// <summary>Draws (or takes away) the badges and the bar frame on every combat view. Main thread only.</summary>
    static void UpdateBadges(SceneTree tree, bool on)
    {
        foreach (CombatActiveView view in Find<CombatActiveView>(tree.Root))
        {
            CharacterHealthProgressBar bar;
            try { bar = EnemyBarOf(view); } catch { continue; }
            if (bar == null || !GodotObject.IsInstanceValid(bar)) continue;
            Control? badges = BadgesFor(bar);
            Panel? frame = bar.GetNodeOrNull<Panel>(FrameName);
            TextureProgressBar fill = HealthProgressOf(bar);
            if (!on)
            {
                if (badges != null) badges.Visible = false;
                if (frame != null) frame.Visible = false;
                fill.TintProgress = Colors.White;
                continue;
            }
            Color color = Tier == EliteTier.Unique ? UniqueColor : ChampionColor;
            if (frame == null)
            {
                frame = new Panel { Name = FrameName, MouseFilter = Control.MouseFilterEnum.Ignore };
                frame.SetAnchorsPreset(Control.LayoutPreset.FullRect);
                frame.OffsetLeft = frame.OffsetTop = -3;
                frame.OffsetRight = frame.OffsetBottom = 3;
                bar.AddChild(frame);
            }
            var box = new StyleBoxFlat { DrawCenter = false, BorderColor = color };
            box.SetBorderWidthAll(3);
            box.SetCornerRadiusAll(5);
            frame.AddThemeStyleboxOverride("panel", box);
            frame.Visible = true;
            bool flashing = Time.GetTicksMsec() / 1000.0 < s_healFlashUntil;
            fill.TintProgress = flashing ? new Color(0.55f, 1f, 0.55f) : s_enraged ? new Color(1f, 0.45f, 0.4f) : Colors.White;

            badges ??= MakeBadges(bar);
            foreach (Node n in badges.GetChildren()) { badges.RemoveChild(n); n.QueueFree(); }
            badges.AddChild(Pill(Tier == EliteTier.Unique ? "UNIQUE" : "CHAMPION", null, color, true,
                Tier == EliteTier.Unique ? $"{Info!.UniqueName}\n{Info.Story}" : "A Champion: tougher, stronger and with better loot."));
            foreach (string a in Affixes)
            {
                var (icon, c, text) = AffixInfo.TryGetValue(a, out var i) ? i : ("", Colors.White, a);
                string label = a == "Warded" && s_combat != null ? $"Warded {s_combat.Enemy.Ward.Current}/{s_combat.Enemy.Ward.Max}"
                             : a == "Enraging" && s_enraged ? "Enraged!" : a;
                badges.AddChild(Pill(label, icon, c, false, text));
            }
            if (Tier == EliteTier.Unique && SignatureOn && Info != null && s_game!.Data.TryGetObject(Info.Signature, out Attack? sig))
                badges.AddChild(Pill(sig.DisplayName.Text, "res://Assets/Icons/special_attack.png", new Color(0.95f, 0.75f, 0.45f), false,
                    $"Signature attack: {sig.DisplayName.Text}"));
            badges.Visible = true;
        }
    }

    /// <summary>The badge row: under the bar when the bar sits in a box, otherwise hung below the bar.</summary>
    static Control? BadgesFor(CharacterHealthProgressBar bar)
    {
        Node? parent = bar.GetParent();
        return parent?.GetNodeOrNull<Control>(BadgesName) ?? bar.GetNodeOrNull<Control>(BadgesName);
    }

    static Control MakeBadges(CharacterHealthProgressBar bar)
    {
        var row = new HFlowContainer { Name = BadgesName, MouseFilter = Control.MouseFilterEnum.Pass };
        row.AddThemeConstantOverride("h_separation", 6);
        row.AddThemeConstantOverride("v_separation", 4);
        if (bar.GetParent() is BoxContainer parent)
        {
            parent.AddChild(row);
            parent.MoveChild(row, bar.GetIndex() + 1);
        }
        else
        {
            bar.AddChild(row);
            row.Position = new Vector2(0, bar.Size.Y + 6);
            row.CustomMinimumSize = new Vector2(bar.Size.X, 0);
            row.Size = new Vector2(bar.Size.X, 26);
        }
        return row;
    }

    static Control Pill(string text, string? iconPath, Color color, bool strong, string tooltip)
    {
        var pill = new PanelContainer { TooltipText = tooltip, MouseFilter = Control.MouseFilterEnum.Stop };
        var box = new StyleBoxFlat
        {
            BgColor = strong ? new Color(color.R * 0.35f, color.G * 0.35f, color.B * 0.35f, 0.92f) : new Color(0.07f, 0.07f, 0.09f, 0.88f),
            BorderColor = color,
            ContentMarginLeft = 7, ContentMarginRight = 8, ContentMarginTop = 2, ContentMarginBottom = 2,
        };
        box.SetBorderWidthAll(strong ? 2 : 1);
        box.SetCornerRadiusAll(9);
        pill.AddThemeStyleboxOverride("panel", box);
        var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 4);
        if (iconPath != null && Icon(iconPath) is { } tex)
            row.AddChild(new TextureRect
            {
                Texture = tex, CustomMinimumSize = new Vector2(18, 18), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, MouseFilter = Control.MouseFilterEnum.Ignore,
            });
        var label = new Label { Text = text, MouseFilter = Control.MouseFilterEnum.Ignore, VerticalAlignment = VerticalAlignment.Center };
        label.AddThemeFontSizeOverride("font_size", strong ? 14 : 13);
        label.AddThemeColorOverride("font_color", strong ? Colors.White : color);
        label.AddThemeConstantOverride("outline_size", 3);
        label.AddThemeColorOverride("font_outline_color", Colors.Black);
        row.AddChild(label);
        pill.AddChild(row);
        return pill;
    }

    /// <summary>For tests: the texts of the badges under the enemy's bar.</summary>
    public static List<string> BadgeTexts()
    {
        var texts = new List<string>();
        if (Engine.GetMainLoop() is not SceneTree tree) return texts;
        foreach (CombatActiveView view in Find<CombatActiveView>(tree.Root))
        {
            try
            {
                if (BadgesFor(EnemyBarOf(view)) is { Visible: true } badges)
                    texts.AddRange(Find<Label>(badges).Where(l => !l.IsQueuedForDeletion()).Select(l => l.Text));
            }
            catch { }
        }
        return texts;
    }
}
