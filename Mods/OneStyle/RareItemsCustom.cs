using System;
using System.Collections.Generic;
using System.Linq;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Components.Equipment;
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.Entities.Combat;
using GamesByMalcs.Melvor2.Modifiers;

namespace OneStyle;

/// <summary>
/// The affixes the game has no bonus for (worked by One Style: Slayer's, Executing, of Embers, of Calling, of Momentum)
/// and the caps on everything the worn rare items give together (03-karar.md 5): attack interval -40%, lifesteal 10%,
/// damage negation 15%, damage taken -20%, loot doubling 25%, monster spawn time -50%, elite chance +50%.
/// </summary>
public static partial class RareItems
{
    static readonly Patches.Source s_caps = new("One Style: rare item caps");
    static bool s_capsOn;

    /// <summary>What the worn rare items give of one affix, as the player reads it (9 for +9%).</summary>
    public static double Worn(Game game, string affixId) => WornAffixes(game).Where(a => a.Id == affixId).Sum(a => a.Value);

    /// <summary>The same for the custom affixes, by their Custom name.</summary>
    public static double WornCustom(Game game, string custom) =>
        WornAffixes(game).Where(a => Affixes.TryGetValue(a.Id, out AffixDef? d) && d.Custom == custom).Sum(a => a.Value);

    static IEnumerable<RolledAffix> WornAffixes(Game game)
    {
        if (Combat(game) is not { } combat) yield break;
        foreach (EquipmentSlot slot in combat.Equipment.Config.EquipmentSlots)
            if (combat.Equipment.TryGetActiveSlot(slot, out IActiveEquipmentSlot? a) && !a.IsEmpty && !a.IsOccupied && RecordOf(a.Equipped!.Item) is { } r)
                foreach (RolledAffix x in r.Affixes) yield return x;
    }

    static readonly (string Affix, double Cap, string StatsType, string Name, string Component, double Scale)[] s_capped =
    {
        ("Swift", 40, "Character", "PercentAttackInterval", "Player", -0.01),
        ("Leeching", 10, "Character", "FlatCombatStatValue", "Player", 0.01),
        ("Warding2", 15, "Character", "AttackDamageNegationChance", "Player", 0.01),
        ("Stalwart", 20, "Character", "PercentDamageTaken", "Player", -0.01),
        ("Plenty", 25, "CombatLoot", "ItemDoublingChance", "Loot", 0.01),
        ("Haste", 50, "CombatAction", "PercentSpawnInterval", "Action", -0.01),
    };

    /// <summary>Worked out again whenever gear changes: what the rare items give above a cap is taken off again.</summary>
    public static void UpdateCaps(Game game)
    {
        UniquePowers.Refresh(game);
        if (Patches.Modifiers is not { } mods) return;
        if (s_capsOn) { mods.Remove(s_caps); s_capsOn = false; }
        var values = new List<ComponentModifierValue>();
        foreach (var (affix, cap, statsType, name, component, scale) in s_capped)
        {
            double over = Worn(game, affix) - cap;
            if (over <= 0) continue;
            values.Add(affix == "Leeching"
                ? Patches.Bonus(statsType, name, -over * scale, component, ("CombatStat", "MelvorBase:Lifesteal"))
                : Patches.Bonus(statsType, name, -over * scale, component));
        }
        if (values.Count == 0) return;
        mods.Add(s_caps, Patches.Bonuses(values.ToArray()));
        s_capsOn = true;
    }

    static EquipmentComponent? s_watchedEquipment;
    static readonly EventHandler s_onSwap = (_, _) => { if (s_game != null) UpdateCaps(s_game); };
    static readonly EventHandler<ItemEquippedEventArgs> s_onGear = (_, _) => { if (s_game != null) UpdateCaps(s_game); };

    /// <summary>Follows equipping and unequipping (for the caps).</summary>
    static void WatchGear(Game game)
    {
        if (s_watchedEquipment != null)
        {
            s_watchedEquipment.ItemEquipped -= s_onGear;
            s_watchedEquipment.ItemUnequipped -= s_onGear;
            s_watchedEquipment.LoadoutSwapped -= s_onSwap;
            s_watchedEquipment = null;
        }
        if (Combat(game) is not { } combat) return;
        s_watchedEquipment = combat.Equipment;
        s_watchedEquipment.ItemEquipped += s_onGear;
        s_watchedEquipment.ItemUnequipped += s_onGear;
        s_watchedEquipment.LoadoutSwapped += s_onSwap;
        UpdateCaps(game);
    }
}
