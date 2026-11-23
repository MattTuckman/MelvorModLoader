using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Components.Characters;
using GamesByMalcs.Melvor2.Components.CombatLevels;
using GamesByMalcs.Melvor2.Components.Experience;
using GamesByMalcs.Melvor2.Components.MasteryGuild;
using GamesByMalcs.Melvor2.Components.Pets;
using GamesByMalcs.Melvor2.Core;
using GamesByMalcs.Melvor2.Data;
using GamesByMalcs.Melvor2.Entities.Astrology;
using GamesByMalcs.Melvor2.Entities.Combat;
using GamesByMalcs.Melvor2.Localization;
using GamesByMalcs.Melvor2.Modifiers;
using GamesByMalcs.Melvor2.Triggers;

namespace OneStyle;

/// <summary>
/// One Style 0.2: the classes on an equal footing. What a class had less of than the others, it gets:
///
/// 7. Weak spot: against every monster, the class's stances meet the monster's lowest evasion of all nine stances
///    (a monster is no longer "the wrong style" for anyone). Worked out again for every monster that appears.
/// 8. Elemental edge (Melee, Ranged): +15% max hit against monsters with an element, like a mage using the right spell.
/// 9. Melee shares its XP: whatever Attack gains, Strength gains too, and the other way round (one skill to train,
///    like Ranged and Magic).
/// 10. Guild and Astrology equivalents (Ranged, Magic): what melee gets from the Attack and Strength guilds and from
///    its constellations, the class gets from its own guild ranks and constellation.
/// 11. Class identity: Melee lifesteal, Ranged critical hits, Magic ward; Ranged and Magic carry light (two-handed
///    weapons leave no hand for a lantern).
/// 12. The pets Fen and Bruno (Attack, Strength) drop with the Ranged or Magic class's XP.
/// The combat triangle is off altogether for the class (see FixTriangle).
/// </summary>
public static partial class Patches
{
    static readonly Source s_weakSpot = new("One Style: weak spot");
    static readonly Source s_equivalents = new("One Style: guild and astrology equivalents");
    static readonly Source s_identity = new("One Style: class identity");

    static readonly FieldInfo? s_elementLookup = typeof(CombatActionComponent).GetField("_elementalBonusLookup", Hidden);
    static readonly MethodInfo? s_elementUpdate = typeof(CombatActionComponent).GetMethod("UpdateElementalBonuses", Hidden);
    static readonly FieldInfo? s_triggerComponent = typeof(ComponentTrigger<ExperienceComponent, ValueChangedEventArgs<double>>)
        .GetField("<Component>k__BackingField", Hidden);

    /// <summary>The elemental table rows this class added (Neutral against each element), to take out again.</summary>
    static readonly List<Element> s_edgeAdded = new();
    static bool s_edgeRowAdded;
    static bool s_weakOn, s_equivOn;
    public static bool IdentityOn { get; private set; }
    static string s_equivKey = "";
    static readonly List<(ChancePerExperienceTrigger Trigger, ExperienceComponent Original)> s_pets = new();
    static CombatEntity? s_enemyWatched;
    static EventHandler<EnemyEventArgs>? s_enemyHandler;
    static readonly List<(ExperienceComponent Skill, EventHandler<ValueChangedEventArgs<double>> Handler)> s_xpWatched = new();
    [ThreadStatic] static bool t_sharing;

    // ---- for the window and the tests
    public static int WeakSpotStances { get; private set; }
    public static int EdgeElements => s_edgeAdded.Count;
    public static List<string> EquivalentsActive { get; } = new();
    public static int PetsFollowing => s_pets.Count;
    public static double XpShared { get; private set; }

    static CombatEntity Combat() => (CombatEntity)s_game!.Entities.GetObjectById("MelvorBase:Combat");

    static void ApplyBalance(CombatStyle style)
    {
        Step("elemental edge", () => AddEdge());
        Step("weak spot", () => WatchEnemy());
        Step("melee XP sharing", () => ShareXp());
        Step("guild and astrology equivalents", () => UpdateEquivalents());
        Step("class identity", () => AddIdentity());
        Step("pets", () => MovePets());
    }

    static void RestoreBalance()
    {
        UnwatchEnemy();
        UnshareXp();
        Try("elemental edge", () => { RemoveEdge(); return true; });
        foreach (var (trigger, original) in s_pets) Try("a pet", () => { Repoint(trigger, original); return true; });
        s_pets.Clear();
        if (s_mods != null)
            foreach (Source source in new[] { s_weakSpot, s_equivalents, s_identity })
                try { s_mods.Remove(source); } catch { }
        ForgetBalance();
    }

    static void ForgetBalance()
    {
        UnwatchEnemy();
        UnshareXp();
        s_edgeAdded.Clear();
        s_edgeRowAdded = false;
        s_pets.Clear();
        s_weakOn = s_equivOn = false;
        IdentityOn = false;
        s_equivKey = "";
        EquivalentsActive.Clear();
        WeakSpotStances = 0;
    }

    // ================================================================ 8. elemental edge

    static void AddEdge()
    {
        if (s_class is not (Class.Melee or Class.Ranged)) return;
        CombatEntity combat = Combat();
        if (s_elementLookup?.GetValue(combat.Action) is not Dictionary<Element, Dictionary<Element, List<ElementalBonus>>> lookup)
            throw new InvalidOperationException("the game's elemental table was not found");
        Element neutral = s_game!.Data.GetObject<Element>("MelvorBase:Neutral");
        if (!lookup.TryGetValue(neutral, out var row))
        {
            row = new Dictionary<Element, List<ElementalBonus>>();
            lookup[neutral] = row;
            s_edgeRowAdded = true;
        }
        ModifierValues bonus = Values(Value("Character", "PercentMaxHit", 0.15, "Player"));
        foreach (Element element in s_game.Data.GetDataRegistryOutput<Element>().GetAllObjects())
        {
            if (element == neutral || row.ContainsKey(element)) continue;
            row[element] = new List<ElementalBonus>
            {
                new(new LocalizedString($"{Classes.Name(s_class)} vs {element.DisplayName.Text}"), neutral, element, true, bonus,
                    neutral.Namespace, "OneStyleEdgeVs" + element.LocalId),
            };
            s_edgeAdded.Add(element);
        }
        s_elementUpdate?.Invoke(combat.Action, null);
    }

    static void RemoveEdge()
    {
        if (s_game == null || s_edgeAdded.Count == 0 && !s_edgeRowAdded) return;
        CombatEntity combat = Combat();
        if (s_elementLookup?.GetValue(combat.Action) is Dictionary<Element, Dictionary<Element, List<ElementalBonus>>> lookup)
        {
            Element neutral = s_game.Data.GetObject<Element>("MelvorBase:Neutral");
            if (lookup.TryGetValue(neutral, out var row))
            {
                foreach (Element e in s_edgeAdded) row.Remove(e);
                if (s_edgeRowAdded && row.Count == 0) lookup.Remove(neutral);
            }
            s_elementUpdate?.Invoke(combat.Action, null);
        }
        s_edgeAdded.Clear();
        s_edgeRowAdded = false;
    }

    // ================================================================ 7. weak spot

    static void WatchEnemy()
    {
        UnwatchEnemy();
        CombatEntity combat = Combat();
        s_enemyHandler = (_, _) => { lock (s_lock) { try { UpdateWeakSpot(); } catch (Exception e) { Log("One Style: weak spot: " + e.Message); } } };
        // Spawned, not EnemyChanged: the game sets the new monster first and its levels and stats after that.
        combat.Enemy.Spawned += s_enemyHandler;
        s_enemyWatched = combat;
        UpdateWeakSpot();
    }

    static void UnwatchEnemy()
    {
        if (s_enemyWatched != null && s_enemyHandler != null) s_enemyWatched.Enemy.Spawned -= s_enemyHandler;
        s_enemyWatched = null;
        s_enemyHandler = null;
    }

    /// <summary>
    /// The game's evasion for a stance is (level + 9) x (the stance's defence bonus + 64). For every stance of the
    /// class whose evasion is above the monster's lowest, the monster's defence bonus for that stance is lowered just
    /// enough to meet it. Percent and darkness changes still apply on top, as for any monster.
    /// </summary>
    public static void UpdateWeakSpot()
    {
        if (s_mods == null || s_game == null) return;
        if (s_weakOn) { s_mods.Remove(s_weakSpot); s_weakOn = false; }
        WeakSpotStances = 0;
        if (s_class == Class.None) return;
        CombatEntity combat = Combat();
        if (combat.Enemy.Enemy == null) return;
        var stats = combat.Enemy.Stats;
        var levels = stats.ModifiedCombatLevels.Dict;
        var totals = stats.ModifiedCombatStatTotals.Dict;
        string styleId = Classes.StyleId(s_class);
        var rows = new List<(AttackStance Stance, double Level, double Defence, double Base)>();
        foreach (AttackStance stance in s_game.Data.GetDataRegistryOutput<AttackStance>().GetAllObjects())
        {
            if (!totals.TryGetValue(stance.EvasionStat, out var def)) continue;
            double level = CombatLevelRatio.ComputeLevel(stance.CombatStyle.EvasionLevels, levels);
            rows.Add((stance, level, def.Value, Math.Floor((level + 9) * (def.Value + 64))));
        }
        if (rows.Count == 0) return;
        double lowest = rows.Min(r => r.Base);
        var values = new List<ComponentModifierValue>();
        foreach (var (stance, level, defence, evasion) in rows)
        {
            if (stance.CombatStyle.Id != styleId || evasion <= lowest) continue;
            double change = lowest / (level + 9) - 64 - defence;
            values.Add(Value("Character", "FlatCombatStatValue", change, "Enemy", ("CombatStat", stance.EvasionStat.Id)));
        }
        if (values.Count == 0) return;
        s_mods.Add(s_weakSpot, Values(values.ToArray()));
        s_weakOn = true;
        WeakSpotStances = values.Count;
    }

    // ================================================================ 9. melee shares its XP

    static void ShareXp()
    {
        UnshareXp();
        XpShared = 0;
        if (s_class != Class.Melee) return;
        ExperienceComponent? attack = Skill("MelvorBase:Attack"), strength = Skill("MelvorBase:Strength");
        if (attack == null || strength == null) return;
        void Link(ExperienceComponent from, ExperienceComponent to)
        {
            EventHandler<ValueChangedEventArgs<double>> handler = (_, e) =>
            {
                double gained = e.NewValue - e.OldValue;
                if (t_sharing || gained <= 0) return;
                t_sharing = true;
                try { to.AddRawXp(gained); XpShared += gained; }
                finally { t_sharing = false; }
            };
            from.XpChanged += handler;
            s_xpWatched.Add((from, handler));
        }
        Link(attack, strength);
        Link(strength, attack);
    }

    static void UnshareXp()
    {
        foreach (var (skill, handler) in s_xpWatched) skill.XpChanged -= handler;
        s_xpWatched.Clear();
    }

    // ================================================================ 10. guild and astrology equivalents

    static bool GuildRank(string id) =>
        s_game!.Data.TryGetObject(id, out MasteryGuildBonus? bonus) && bonus.Requirements.AreMet;

    static bool MajorNode(string id) =>
        s_game!.Data.TryGetObject(id, out AstrologyNode? node)
        && s_game.Entities.TryGetObjectById("MelvorBase:Astrology", out var e) && e is AstrologyEntity astrology
        && astrology.Action.IsMajorNodeUnlocked(node);

    /// <summary>Called a few times a second by the mod: guild ranks and constellations are earned while playing.</summary>
    public static void Poll()
    {
        lock (s_lock)
        {
            if (s_game == null || s_class is not (Class.Ranged or Class.Magic)) return;
            try { UpdateEquivalents(); } catch (Exception e) { Log("One Style: guild and astrology equivalents: " + e.Message); }
        }
    }

    /// <summary>What the class has earned of the melee-only rewards. Cheap: called a few times a second.</summary>
    public static void UpdateEquivalents()
    {
        if (s_mods == null || s_game == null || s_class is not (Class.Ranged or Class.Magic)) return;
        string skill = s_class == Class.Ranged ? "Ranged" : "Magic", style = Classes.StyleId(s_class);
        var on = new List<(string Name, ComponentModifierValue[] Values)>();
        if (GuildRank($"MelvorBase:{skill}Rank3"))
            on.Add(("+6% " + (s_class == Class.Ranged ? "ranged strength" : "magic damage") + $" ({skill} guild rank 3, as Strength rank 3)", new[]
            {
                Value("Character", "PercentCombatStatValue", 0.06, "Player", ("CombatStat", s_class == Class.Ranged ? "MelvorBase:RangedStrengthBonus" : "MelvorBase:MagicDamageBonus")),
            }));
        if (GuildRank($"MelvorBase:{skill}Rank4"))
            on.Add(($"-10% monster evasion and -10% monster armour ({skill} guild rank 4, as Attack and Strength rank 4)", new[]
            {
                Value("Character", "PercentEvasionRating", -0.10, "Enemy"),
                Value("Character", "PercentCombatStatValue", -0.10, "Enemy", ("CombatStat", "MelvorBase:Armour")),
            }));
        if (GuildRank($"MelvorBase:{skill}Rank5"))
            on.Add(($"+5% minimum hit and +15% power ({skill} guild rank 5, as Attack and Strength rank 5)", new[]
            {
                Value("Character", "MinHitBasedOnMaxHit", 0.05, "Player", ("CombatStyle", style)),
                Value("Character", "PercentPowerRating", 0.15, "Player", ("CombatStyle", style)),
            }));
        if (s_class == Class.Ranged && MajorNode("MelvorBase:SylliaMajor3"))
            on.Add(("+4 Ranged levels (Syllia major 3, as Terra major 4)", new[]
            {
                Value("Character", "FlatCombatLevel", 4, "Player", ("CombatLevel", "MelvorBase:Ranged")),
            }));
        string accuracyNode = s_class == Class.Ranged ? "MelvorBase:SylliaMajor4" : "MelvorBase:ArachiMajor4";
        if (MajorNode(accuracyNode))
            on.Add(($"+15% accuracy ({(s_class == Class.Ranged ? "Syllia" : "Arachi")} major 4, as Iridan major 3)", new[]
            {
                Value("Character", "PercentAccuracyRating", 0.15, "Player", ("CombatStyle", style)),
            }));
        string key = string.Join("|", on.Select(o => o.Name));
        if (key == s_equivKey && (s_equivOn || on.Count == 0)) return;
        if (s_equivOn) { s_mods.Remove(s_equivalents); s_equivOn = false; }
        EquivalentsActive.Clear();
        s_equivKey = key;
        if (on.Count == 0) return;
        s_mods.Add(s_equivalents, Values(on.SelectMany(o => o.Values).ToArray()));
        s_equivOn = true;
        EquivalentsActive.AddRange(on.Select(o => o.Name));
    }

    // ================================================================ 11. class identity

    static ComponentModifierValue[] IdentityValues(Class c)
    {
        string style = Classes.StyleId(c);
        return c switch
        {
            Class.Melee => new[]
            {
                Value("Character", "FlatCombatStatValue", 0.03, "Player", ("CombatStyle", style), ("CombatStat", "MelvorBase:Lifesteal")),
            },
            Class.Ranged => new[]
            {
                Value("Character", "FlatCriticalHitChance", 0.05, "Player", ("CombatStyle", style)),
                Value("Character", "FlatCriticalHitMultiplier", 0.25, "Player", ("CombatStyle", style)),
                Value("Character", "FlatLight", 30, "Player"),
            },
            Class.Magic => new[]
            {
                Value("Character", "FlatCombatStatValue", 3, "Player", ("CombatStyle", style), ("CombatStat", "MelvorBase:MaximumWard")),
                Value("Character", "DoubleWardDamageChance", 0.25, "Player", ("CombatStyle", style)),
                Value("Character", "FlatLight", 30, "Player"),
            },
            _ => Array.Empty<ComponentModifierValue>(),
        };
    }

    static void AddIdentity()
    {
        if (s_mods == null || s_class == Class.None) return;
        s_mods.Add(s_identity, Values(IdentityValues(s_class)));
        IdentityOn = true;
    }

    // ================================================================ 12. pets

    static void MovePets()
    {
        if (s_class is not (Class.Ranged or Class.Magic) || StandIn() is not { } to) return;
        foreach (string id in new[] { "MelvorBase:Fen", "MelvorBase:Bruno" })
        {
            if (!s_game!.Data.TryGetObject(id, out Pet? pet) || pet.Trigger is not ChancePerExperienceTrigger trigger) continue;
            ExperienceComponent original = trigger.Component;
            if (original == to) continue;
            Repoint(trigger, to);
            s_pets.Add((trigger, original));
        }
    }

    /// <summary>Points a pet's XP trigger at another skill: off, swap, on again (so it listens to the new skill).</summary>
    static void Repoint(ChancePerExperienceTrigger trigger, ExperienceComponent to)
    {
        if (s_triggerComponent == null) throw new InvalidOperationException("the pet trigger's skill field was not found");
        bool enabled = trigger.Enabled;
        if (enabled) trigger.Enabled = false;
        s_triggerComponent.SetValue(trigger, to);
        if (enabled) trigger.Enabled = true;
    }

    /// <summary>The skill a pet's trigger listens to (tests).</summary>
    public static string PetSkill(Game game, string petId) =>
        game.Data.TryGetObject(petId, out Pet? pet) && pet.Trigger is ChancePerExperienceTrigger t ? t.Component.Entity.Id : "";
}
