using System;
using System.Collections.Generic;
using System.Linq;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Components.Equipment;
using GamesByMalcs.Melvor2.Components.Experience;
using GamesByMalcs.Melvor2.Conditions;
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.Entities;
using GamesByMalcs.Melvor2.Entities.Bank;

namespace OneStyle;

/// <summary>
/// What a rare item asks for, and where the game says it comes from.
///
/// Levels: a rare item keeps its base item's skills, each at least 80% of its item level (a Rare ring from a level 100
/// monster asks for level 80, not the base ring's level 1). A unique item asks for its own level: a weapon or ammo in
/// its style's skill (melee: Attack), armour and offhands Defence (plus the style's skill when it has one), rings,
/// amulets and capes Hitpoints. Never above 99.
///
/// "Found in / Used in" (the game's bank tags): only "Combat" as where it is found. The base item's crafting skill,
/// shop and upgrades do not apply to a rare item.
/// </summary>
public static partial class RareItems
{
    static readonly Dictionary<string, string> s_styleSkill = new()
    {
        ["MelvorBase:Melee"] = "MelvorBase:Attack", ["MelvorBase:Ranged"] = "MelvorBase:Ranged", ["MelvorBase:Magic"] = "MelvorBase:Magic",
    };

    /// <summary>The level a rare item asks for at least.</summary>
    public static int NeededLevel(RolledItem record) =>
        Math.Clamp(record.UniqueId != "" && Uniques.Defs.TryGetValue(record.UniqueId, out UniqueDef? u) ? u.Level : (int)Math.Round(record.ILvl * 0.8), 1, 99);

    static ExperienceComponent? ExperienceOf(Game game, string skillId) =>
        game.Entities.TryGetObjectById(skillId, out GameEntity? e) && e is ILeveledEntity<ExperienceComponent> s ? s.Experience : null;

    internal static ConditionGroup RequirementsFor(Game game, RolledItem record, Item baseItem, EquipmentCapability cap)
    {
        int need = NeededLevel(record);
        var kept = cap.Requirements.Conditions.Where(c => c is not LevelCondition).ToList();
        var levels = new Dictionary<string, (int Level, ExperienceComponent Experience)>();
        void Want(string skillId, int level)
        {
            if (ExperienceOf(game, skillId) is not { } xp) return;
            int was = levels.TryGetValue(skillId, out var have) ? have.Level : 0;
            levels[skillId] = (Math.Min(99, Math.Max(was, level)), xp);
        }
        string slot = SlotOf(baseItem);
        if (record.UniqueId != "" && Uniques.Defs.TryGetValue(record.UniqueId, out UniqueDef? def))
        {
            bool armour = slot is "Helmet" or "Torso" or "Legs" or "Gloves" or "Boots" or "Offhand";
            bool jewel = slot is "Ring" or "Amulet" or "Cape";
            if (s_styleSkill.TryGetValue(def.Style, out string? styleSkill) && !jewel) Want(styleSkill, need);
            if (armour) Want("MelvorBase:Defence", need);
            if (jewel) Want("MelvorBase:Hitpoints", need);
            if (levels.Count == 0) Want(slot is "Weapon" or "Ammo" ? StyleSkillOf(baseItem) : "MelvorBase:Hitpoints", need);
        }
        else
        {
            // rings, amulets and capes are for every class: a combat style's level on them becomes Hitpoints
            bool jewel = slot is "Ring" or "Amulet" or "Cape";
            foreach (LevelCondition l in cap.Requirements.Conditions.OfType<LevelCondition>())
            {
                string skill = l.Component.Entity.Id;
                if (jewel && skill is "MelvorBase:Attack" or "MelvorBase:Strength" or "MelvorBase:Ranged" or "MelvorBase:Magic") skill = "MelvorBase:Hitpoints";
                Want(skill, Math.Max(l.Level, need));
            }
            if (levels.Count == 0)
                Want(slot is "Weapon" or "Ammo" ? StyleSkillOf(baseItem) : slot is "Ring" or "Amulet" or "Cape" ? "MelvorBase:Hitpoints" : "MelvorBase:Defence", need);
        }
        foreach (var (_, (level, xp)) in levels) kept.Add(new LevelCondition(level, xp, false));
        return new ConditionGroup(kept);
    }

    static string StyleSkillOf(Item item) => s_styleSkill.TryGetValue(StyleOf(item), out string? s) ? s : "MelvorBase:Attack";

    /// <summary>"Found in: Combat", nothing else.</summary>
    internal static List<ItemBankTag> BankTagsFor(Game game) =>
        game.Data.TryGetObject("MelvorBase:Combat", out BankTagCategory? combat)
            ? new List<ItemBankTag> { new(combat, isProduct: true, isIngredient: false) }
            : new List<ItemBankTag>();
}
