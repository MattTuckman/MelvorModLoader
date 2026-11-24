using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using CheatMenu;
using GamesByMalcs.Melvor2.Components.CombatAreas;
using GamesByMalcs.Melvor2.Components.CombatLoot;
using GamesByMalcs.Melvor2.Components.CombatPassives;
using GamesByMalcs.Melvor2.Entities.Combat;
using Godot;

namespace OneStyle;

/// <summary>
/// The "Elites" button on every monster of the combat area menu, and the window it opens: the monster as Normal,
/// Champion and Unique side by side (picture, numbers, affixes, signature attack, story), with what each one gives.
/// </summary>
public static partial class Elites
{
    const string EliteButtonName = "OneStyleElitesButton";

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_ViewStatsButton")]
    static extern Button ViewStatsButtonOf(EnemyOptionsPanel panel);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Enemy")]
    static extern Enemy? EnemyOf(EnemyOptionsPanel panel);

    /// <summary>More lines per tier for the window, from the item systems (rare items, the Unique's own item).</summary>
    public static Func<Enemy, EliteTier, IEnumerable<(string Text, Color Color, Texture2D? Icon)>>? DropLines;

    static CanvasLayer? s_infoLayer;

    static void AddEliteButton(EnemyOptionsPanel panel)
    {
        try
        {
            if (!GodotObject.IsInstanceValid(panel)) return;
            Button stats = ViewStatsButtonOf(panel);
            if (stats?.GetParent() is not Node parent || parent.GetNodeOrNull(EliteButtonName) != null) return;
            var button = new Button { Name = EliteButtonName, Text = "Elites", TooltipText = "This monster as a Champion and as a Unique, and what they drop.",
                FocusMode = Control.FocusModeEnum.None };
            button.AddThemeColorOverride("font_color", UniqueColor);
            button.Pressed += () =>
            {
                if (EnemyOf(panel) is { } enemy) OpenInfo(enemy);
            };
            parent.AddChild(button);
            parent.MoveChild(button, stats.GetIndex() + 1);
        }
        catch (Exception e) { Log("One Style: the Elites button could not be added: " + e.Message); }
    }

    public static bool InfoOpen => s_infoLayer != null && GodotObject.IsInstanceValid(s_infoLayer) && s_infoLayer.Visible;
    public static string InfoText = "";

    /// <summary>Opens the window for one monster.</summary>
    public static void OpenInfo(Enemy enemy)
    {
        if (Engine.GetMainLoop() is not SceneTree tree) return;
        CloseInfo();
        EliteInfo? info = InfoOf(enemy.Id);
        s_infoLayer = new CanvasLayer { Layer = 117, Name = "OneStyleEliteInfo" };
        var shade = new ColorRect { Color = new Color(0, 0, 0, 0.45f), MouseFilter = Control.MouseFilterEnum.Stop };
        shade.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        shade.GuiInput += e => { if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) CloseInfo(); };
        s_infoLayer.AddChild(shade);
        var center = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        s_infoLayer.AddChild(center);
        var panel = new PanelContainer { Theme = Style.Build(), CustomMinimumSize = new Vector2(1040, 0) };
        StyleBoxFlat frame = Style.Box(Style.Window, 8, UniqueColor.Darkened(0.4f), 1, 18, 14);
        frame.ShadowColor = new Color(0, 0, 0, 0.55f);
        frame.ShadowSize = 14;
        panel.AddThemeStyleboxOverride("panel", frame);
        center.AddChild(panel);
        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 10);
        panel.AddChild(root);

        var header = new HBoxContainer();
        header.AddChild(Style.Label(enemy.DisplayName.Text.ToUpperInvariant() + "  ·  ELITE VERSIONS", UniqueColor, 19));
        header.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        var close = new Button { Text = "Close", FocusMode = Control.FocusModeEnum.None };
        close.Pressed += CloseInfo;
        header.AddChild(close);
        root.AddChild(header);

        if (info == null)
        {
            root.AddChild(Style.Label("This monster has no Champion or Unique version (it has no picture in the game yet).", Style.Muted, 14));
        }
        else
        {
            double hp = enemy.BaseMaxHitpoints;
            double interval = enemy.Stats.Stats.FirstOrDefault(s => s.Stat.Id == "MelvorBase:AttackInterval")?.Value ?? 2400;
            var columns = new HBoxContainer();
            columns.AddThemeConstantOverride("separation", 12);
            columns.AddChild(Column(enemy, info, EliteTier.Normal, hp, interval));
            columns.AddChild(Column(enemy, info, EliteTier.Champion, hp, interval));
            columns.AddChild(Column(enemy, info, EliteTier.Unique, hp, interval));
            var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
                CustomMinimumSize = new Vector2(0, Math.Clamp(tree.Root.GetVisibleRect().Size.Y - 200, 300, 760)) };
            columns.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            scroll.AddChild(columns);
            root.AddChild(scroll);
            int[] kills = s_progress.KillsOf(enemy.Id);
            int since = s_progress.SinceUnique.GetValueOrDefault(enemy.Id);
            root.AddChild(Style.Label($"Your kills: {kills[0]} normal, {kills[1]} Champion, {kills[2]} Unique  ·  since its last Unique: {since}"
                                      + (kills[2] > 0 ? "  ·  trophy owned" : "  ·  no trophy yet"), Style.Muted, 13));
        }
        tree.Root.AddChild(s_infoLayer);
        InfoText = string.Join(" | ", Find<Label>(s_infoLayer).Select(l => l.Text));
    }

    public static void CloseInfo()
    {
        if (s_infoLayer != null && GodotObject.IsInstanceValid(s_infoLayer)) s_infoLayer.QueueFree();
        s_infoLayer = null;
    }

    static Control Column(Enemy enemy, EliteInfo info, EliteTier tier, double hp, double interval)
    {
        Color color = tier switch { EliteTier.Champion => ChampionColor, EliteTier.Unique => UniqueColor, _ => Style.Text };
        var card = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        StyleBoxFlat box = Style.Box(Style.Card, 6, tier == EliteTier.Normal ? Style.Line : color, tier == EliteTier.Normal ? 1 : 2, 12, 10);
        card.AddThemeStyleboxOverride("panel", box);
        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 4);
        card.AddChild(v);

        Texture2D? picture = tier == EliteTier.Normal ? enemy.Sprite : TextureOf(info, tier);
        v.AddChild(new TextureRect { Texture = picture, CustomMinimumSize = new Vector2(0, 170), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered });
        string title = tier switch { EliteTier.Champion => "Champion " + enemy.DisplayName.Text, EliteTier.Unique => info.UniqueName, _ => enemy.DisplayName.Text };
        Label name = Style.Label(title, color, 16);
        name.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        v.AddChild(name);
        void Line(string text, Color c, int size = 12)
        {
            Label l = Style.Label(text, c, size);
            l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            v.AddChild(l);
        }
        if (tier == EliteTier.Unique) Line(info.Story, Style.Muted);

        if (tier == EliteTier.Normal)
        {
            Line(FormattableString.Invariant($"Hit points {hp:N0}  ·  attack every {interval / 1000:0.00} s"), Style.Text);
            Line("Your normal fight.", Style.Muted);
            AddDrops(v, enemy, tier);
            return card;
        }
        bool unique = tier == EliteTier.Unique;
        List<string> affixes = unique ? info.Affixes.ToList() : new List<string>();
        EliteNumbers n = Compute(hp, interval, unique, affixes, unique ? info : null);
        Line(FormattableString.Invariant($"Hit points {hp * n.HpMult:N0} (x{n.HpMult:0.##})  ·  attack every {interval * (1 + n.Speed) / 1000:0.00} s"), Style.Text);
        Line($"Damage +{n.Damage * 100:0}%  ·  accuracy +{n.Accuracy * 100:0}%  ·  protection +{n.Protection * 100:0}%", Style.Text);
        if (unique)
        {
            foreach (string a in affixes)
                if (AffixInfo.TryGetValue(a, out var ai)) Line("• " + ai.Text, ai.Color);
            if (n.Signature && s_game != null && s_game.Data.TryGetObject(info.Signature, out Attack? sig))
                Line($"• Signature attack: {sig.DisplayName.Text}", new Color(0.95f, 0.75f, 0.45f));
            if (info.Passive != "" && s_game != null && s_game.Data.TryGetObject(info.Passive, out CombatPassive? passive) && !enemy.Passives.Contains(passive))
                Line($"• Passive: {passive.DisplayName.Text}", new Color(0.95f, 0.75f, 0.45f));
        }
        else
        {
            Line(hp >= 1200 ? "Two random affixes from:" : "One random affix from:", Style.Muted);
            foreach (var (a, ai) in AffixInfo) Line("• " + ai.Text, ai.Color);
        }
        int band = Band(hp);
        bool hasLoot = enemy.Drops.FixedItems.Count > 0 || enemy.Drops.ItemTables.Count > 0 || enemy.Drops.FixedCurrencies.Count > 0;
        Line(unique
            ? $"Rewards: {(hasLoot ? "5 more loot rolls" : "gold from its hit points")}, +50% combat XP, {12 + 4 * band} Ember (first kill +3 rolls, +40 Ember, its trophy)"
            : $"Rewards: {(hasLoot ? "2 more loot rolls" : "gold from its hit points")}, +25% combat XP, {2 + band} Ember (first kill +1 roll, +10 Ember)", Style.Good);
        Line(unique ? "Chance: 0.8% in combat areas, 1.5% on your slayer task, 0.6% in dungeons, 4% as a dungeon's last boss, rising after 100 kills without one."
                    : "Chance: 6% in combat areas, 10% on your slayer task, 8% in dungeons, 10% as a dungeon's last boss.", Style.Muted);
        AddDrops(v, enemy, tier);
        return card;
    }

    static void AddDrops(VBoxContainer v, Enemy enemy, EliteTier tier)
    {
        if (DropLines == null) return;
        foreach (var (text, color, icon) in DropLines(enemy, tier))
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 6);
            if (icon != null)
                row.AddChild(new TextureRect { Texture = icon, CustomMinimumSize = new Vector2(24, 24), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                    StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered });
            Label l = Style.Label(text, color, 12);
            l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            l.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            row.AddChild(l);
            v.AddChild(row);
        }
    }
}
