using System;
using System.Globalization;
using Adventurers;
using GamesByMalcs.Melvor2.Components.CombatAreas;
using GamesByMalcs.Melvor2.Entities.Combat;
using Godot;
using System.Collections.Generic;
using System.Linq;
using GamesByMalcs.Melvor2.Components.CombatEffects;
using GamesByMalcs.Melvor2.Components.Characters;

namespace CombatAdvisor;

/// <summary>Colours, words and number formats the strip and the window share.</summary>
public static class Words
{
    public static readonly Color SafeColour = new(0.30f, 0.78f, 0.35f);
    public static readonly Color RiskyColour = new(1.00f, 0.66f, 0.18f);
    public static readonly Color DeadlyColour = new(0.93f, 0.25f, 0.22f);
    public static readonly Color UnknownColour = new(0.62f, 0.60f, 0.57f);

    public static Color Of(Verdict v) => v switch
    {
        Verdict.Safe => SafeColour, Verdict.Risky => RiskyColour, Verdict.Deadly => DeadlyColour, _ => UnknownColour,
    };

    public static string Name(Verdict v) => v switch
    {
        Verdict.Safe => "SAFE", Verdict.Risky => "RISKY", Verdict.Deadly => "DEADLY", _ => "UNKNOWN",
    };

    /// <summary>"1 in 4 attacks", "every attack", "15% of attacks".</summary>
    public static string HowOften(double chance)
    {
        if (chance >= 0.999) return "every attack";
        double n = 1 / chance;
        if (Math.Abs(n - Math.Round(n)) < 0.02 && n <= 20) return $"1 in {Math.Round(n):0} attacks";
        return $"{chance * 100:0}% of attacks";
    }

    /// <summary>
    /// A special attack in plain words: "Crushing Blade, 1 in 4 attacks: 3 hits of 85% damage; can stun (10% a hit)".
    /// </summary>
    public static string Special(Attack attack, double chance)
    {
        var c = CultureInfo.InvariantCulture;
        var hits = new List<string>();
        foreach (AttackHit hit in attack.Hits)
        {
            var parts = hit.Damage.Select(d => d.Formula switch
            {
                NormalDamageFormula n => $"{n.Percent * 100:0.#}% damage",
                MaxHitDamageFormula m => $"{m.Percent * 100:0.#}% of your max hit",
                _ => "damage",
            }).ToList();
            string what = parts.Count == 0 ? "no damage" : string.Join(" + ", parts);
            if (what == "0% damage") what = "no damage";
            hits.Add(hit.Count == 1 ? $"1 hit of {what}" : $"{hit.Count} hits of {what}");
        }
        // merge "1 hit of X" groups that only differ by their timing
        string how = string.Join(", then ", hits);
        var effects = attack.PreHitEffects.Concat(attack.OnHitEffects)
            .Select(e => $"{(e.AppliesTo == RelativeCharacter.Self ? "gives you" : "can cause")} {e.Effect.Data.DisplayName.Text}"
                         + (e.Chance < 0.999 ? $" ({(e.Chance * 100).ToString("0.#", c)}%{(attack.Hits.Sum(h => h.Count) > 1 ? " a hit" : "")})" : ""))
            .Distinct().ToList();
        string text = $"{attack.DisplayName.Text}, {HowOften(chance)}: {how}";
        if (!attack.IsAvoidable) text += " (cannot miss)";
        if (effects.Count > 0) text += "; " + string.Join(", ", effects);
        return text;
    }

    /// <summary>12, 1.2k, 24.5k, 1.3M.</summary>
    public static string Short(double n)
    {
        var c = CultureInfo.InvariantCulture;
        if (n >= 1_000_000) return (n / 1_000_000).ToString(n >= 10_000_000 ? "0" : "0.0", c) + "M";
        if (n >= 1_000) return (n / 1_000).ToString(n >= 10_000 ? "0" : "0.0", c) + "k";
        if (n >= 10) return Math.Round(n).ToString("0", c);
        if (n >= 0.05) return n.ToString("0.0", c);
        return n > 0 ? "<0.1" : "0";
    }

    public static string Hours(double h)
    {
        if (double.IsInfinity(h) || h > 999) return "for ever";
        if (h >= 10) return Math.Floor(h).ToString(CultureInfo.InvariantCulture) + " h";
        int minutes = (int)Math.Round(h * 60);
        return minutes >= 60 ? $"{minutes / 60} h {minutes % 60} min" : $"{minutes} min";
    }

    /// <summary>A small coloured box with the verdict word (the strip and the window).</summary>
    public static PanelContainer Pill(Verdict v, int size = 12)
    {
        var pill = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
        Color c = Of(v);
        StyleBoxFlat box = Look.Flat(new Color(c.R * 0.22f, c.G * 0.22f, c.B * 0.22f), c, size > 12 ? 12 : 8, size > 12 ? 4 : 2, 4);
        box.SetBorderWidthAll(1);
        pill.AddThemeStyleboxOverride("panel", box);
        Label word = Look.Body(Name(v), size);
        word.Modulate = c;
        pill.AddChild(word);
        return pill;
    }
}

/// <summary>
/// The line the advisor adds to a monster card in the game's combat area menu: verdict, kills, XP and food per
/// hour, and the button to the advisor window. The game reuses its cards; the strip follows the monster bound.
/// </summary>
public sealed class Strip
{
    public const string NodeName = "CombatAdvisorStrip";

    public PanelContainer Root { get; }
    readonly HBoxContainer _row;
    readonly Button _open;
    public string Key { get; private set; } = "";
    public CombatArea? Area { get; private set; }
    public Enemy? Enemy { get; private set; }
    public bool Filled { get; private set; }

    public event Action<CombatArea, Enemy>? Opened;

    public Strip()
    {
        Root = new PanelContainer { Name = NodeName, MouseFilter = Control.MouseFilterEnum.Pass, CustomMinimumSize = new Vector2(0, 30) };
        StyleBoxFlat box = Look.Flat(new Color(0.09f, 0.08f, 0.07f, 0.92f), new Color(0.30f, 0.27f, 0.24f), 10, 3, 4);
        box.SetBorderWidthAll(1);
        Root.AddThemeStyleboxOverride("panel", box);
        var line = new HBoxContainer();
        line.AddThemeConstantOverride("separation", 14);
        _row = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _row.AddThemeConstantOverride("separation", 14);
        line.AddChild(_row);
        _open = Look.Push("Advisor");
        _open.TooltipText = "Combat Advisor: the fight worked out with your own gear, stance, food and bonuses.";
        _open.Pressed += () => { if (Area != null && Enemy != null) Opened?.Invoke(Area, Enemy); };
        line.AddChild(_open);
        Root.AddChild(line);
    }

    /// <summary>The monster the card shows now; the strip says "working it out" until <see cref="Show"/>.</summary>
    public void Bind(string key, CombatArea area, Enemy enemy)
    {
        if (key == Key) return;
        Key = key;
        Area = area;
        Enemy = enemy;
        Filled = false;
        Clear();
        _row.AddChild(Look.Caption("Combat Advisor: working it out..."));
    }

    public void Show(Outlook o, Setup? setup = null)
    {
        Clear();
        Filled = true;
        _row.AddChild(Words.Pill(o.Verdict));
        if (o.Fight == null)
        {
            Label why = Look.Caption(o.Why);
            why.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            why.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            why.ClipText = true;
            _row.AddChild(why);
            Root.TooltipText = o.Why;
            return;
        }
        _row.AddChild(Stat(Words.Short(o.KillsPerHour), "kills/h"));
        if (o.MainXp is { } xp) _row.AddChild(Stat(Words.Short(xp.PerHour), Combat.Local(xp.Level.Id) + " XP/h"));
        if (o.FoodPerHour > 0 && o.Fight.Heal > 0)
        {
            HBoxContainer food = Stat(Words.Short(o.FoodPerHour), "food/h");
            _row.AddChild(food);
            // the food in the bank lasts less than an hour: say so, in amber
            double lasts = setup == null ? double.PositiveInfinity : setup.FoodOwned / o.FoodPerHour;
            if (lasts < 1)
            {
                food.Modulate = Words.RiskyColour;
                _row.AddChild(Look.Text($"food lasts {Words.Hours(lasts)}", "fs12_bold_grey", 12, Words.RiskyColour));
            }
        }
        else _row.AddChild(Stat("0", "food/h"));
        if (o.Problem != "") _row.AddChild(Look.Text(o.Problem.Split(':')[0], "fs12_bold_grey", 12, Words.RiskyColour));
        string special = "";
        if (o.Fight.MySpecials.Count > 0)
        {
            // their weapon's special attack is part of these numbers (amber: some of what it does is left out)
            string names = string.Join(", ", o.Fight.MySpecials.Select(s => s.Name));
            bool partly = o.Fight.LeftOut.Count > 0;
            _row.AddChild(Look.Text(partly ? $"{names} partly counted" : $"{names} counted", "fs12_bold_grey", 12, partly ? Words.RiskyColour : null));
            special = "\n" + string.Join("\n", o.Fight.MySpecials.Select(s => $"Your special attack {s.Name} ({Words.HowOften(s.Chance)}) is part of these numbers."));
            if (partly) special += "\nNot counted: " + string.Join(", ", o.Fight.LeftOut) + ".";
        }
        Root.TooltipText = o.Why + (o.Problem != "" && o.Problem != o.Why ? "\n" + o.Problem : "") + special;
    }

    static HBoxContainer Stat(string value, string unit)
    {
        var box = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        box.AddThemeConstantOverride("separation", 4);
        box.AddChild(Look.Body(value, 14));
        box.AddChild(Look.Caption(unit));
        return box;
    }

    void Clear()
    {
        foreach (Node n in _row.GetChildren()) { _row.RemoveChild(n); n.QueueFree(); }
    }
}
