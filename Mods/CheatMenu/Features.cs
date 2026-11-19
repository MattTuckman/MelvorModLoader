using System;
using System.Collections.Generic;
using System.Linq;

namespace CheatMenu;

/// <summary>One bonus the game's own modifier system understands: a stat on one component of one entity.</summary>
public sealed record StatRef(string StatsType, string Name, string Entity, string Component, double Unit);

/// <summary>A game-wide bonus group (applies to every skill at once).</summary>
public sealed record GlobalRef(string StatGroup, double Unit);

public enum FeatureKind { Toggle, Number }

/// <summary>
/// One row in the menu. The menu value is turned into a multiplier; every stat receives Unit x multiplier.
/// A multiplier of 0 means "off" and removes the bonus completely.
/// </summary>
public sealed class Feature
{
    public required string Id { get; init; }
    public required string Tab { get; init; }
    public required string En { get; init; }
    public FeatureKind Kind { get; init; } = FeatureKind.Number;
    public double Min { get; init; }
    public double Max { get; init; } = 1;
    public double Step { get; init; } = 1;
    /// <summary>Menu value that means "no change".</summary>
    public double Off { get; init; }
    public string Suffix { get; init; } = "";
    public Func<double, double> ToMultiplier { get; init; } = v => v;
    public List<StatRef> Stats { get; init; } = new();
    public List<GlobalRef> Globals { get; init; } = new();
    /// <summary>True for features handled in code (game speed) instead of the modifier system.</summary>
    public bool Custom { get; init; }
    /// <summary>Per-skill table only: which skill row and which column ("xp" or "speed") this belongs to.</summary>
    public string? Skill { get; init; }
    public string? Column { get; init; }
}

public static class Features
{
    public const string TabBank = "bank", TabXp = "xp", TabSpeed = "speed", TabSkills = "skills", TabCombat = "combat", TabPerSkill = "perskill";

    // (entity, stats class, component) for every skill that performs actions.
    static readonly (string Entity, string Class, string Component)[] Skills =
    {
        ("Woodcutting", "WoodcuttingAction", "Action"), ("Mining", "MiningAction", "Action"),
        ("Fishing", "FishingAction", "Action"), ("Firemaking", "FiremakingAction", "Action"),
        ("Cooking", "CookingAction", "Action"), ("Smithing", "ArtisanAction", "Action"),
        ("Crafting", "ArtisanAction", "Action"), ("Runecrafting", "ArtisanAction", "Action"),
        ("Fletching", "ArtisanAction", "Action"), ("Beastcrafting", "ArtisanAction", "Action"),
        ("Herblore", "HerbloreAction", "Action"), ("Thieving", "ThievingAction", "Action"),
        ("Agility", "Agility", "Action"), ("Astrology", "AstrologyAction", "Action"),
        ("Hunting", "HuntingAction", "Action"), ("Farming", "FarmingAction", "Passive"),
        ("Ranching", "RanchingAction", "Passive"),
    };

    /// <summary>The same stat name on every skill whose stats class is listed.</summary>
    static List<StatRef> OnSkills(string stat, double unit, params string[] classes) =>
        Skills.Where(s => classes.Contains(s.Class))
              .Select(s => new StatRef(s.Class, stat, s.Entity, s.Component, unit)).ToList();

    static StatRef Player(string stat, double unit) => new("Character", stat, "Combat", "Player", unit);
    static StatRef Combat(string cls, string stat, string component, double unit) => new(cls, stat, "Combat", component, unit);

    static double Percent(double v) => v / 100.0;       // 0..100 slider -> fraction
    static double Times(double v) => v - 1.0;            // "x5" -> +400%
    static double Faster(double v) => 1.0 - 1.0 / v;     // "x5 speed" -> -80% time (Unit is -1)

    static readonly List<Feature> Core = new()
    {
        // ---------------- Bank ----------------
        new() { Id = "bank.slots", Tab = TabBank, En = "Extra bank slots",
                Max = 10000, Step = 50, Stats = { new("BankInventory", "FlatMaxStacks", "Bank", "Inventory", 1) } },
        new() { Id = "bank.sellPrice", Tab = TabBank, En = "Sell price", Suffix = "x",
                Min = 1, Max = 100, Off = 1, ToMultiplier = Times, Stats = { new("ItemSale", "PercentPricePerItem", "Bank", "ItemSales", 1) } },
        new() { Id = "bank.buryReward", Tab = TabBank, En = "Bone burying reward", Suffix = "x",
                Min = 1, Max = 100, Off = 1, ToMultiplier = Times, Stats = { new("ItemBurying", "PercentReward", "Bank", "ItemBurying", 1) } },

        // ---------------- XP ----------------
        new() { Id = "xp.multiplier", Tab = TabXp, En = "XP multiplier", Suffix = "x",
                Min = 1, Max = 1000, Off = 1, ToMultiplier = Times, Globals = { new("GlobalPercentXpGained", 1) } },

        // ---------------- Speed ----------------
        new() { Id = "speed.game", Tab = TabSpeed, En = "Game speed", Suffix = "x",
                Min = 1, Max = 20, Off = 1, Custom = true },
        new() { Id = "speed.actions", Tab = TabSpeed, En = "Skill action speed", Suffix = "x",
                Min = 1, Max = 20, Off = 1, ToMultiplier = Faster, Globals = { new("GlobalPercentActionInterval", -1) } },
        new() { Id = "speed.spawn", Tab = TabSpeed, En = "Monster respawn speed", Suffix = "x",
                Min = 1, Max = 20, Off = 1, ToMultiplier = Faster, Stats = { Combat("CombatAction", "PercentSpawnInterval", "Action", -1) } },
        new() { Id = "speed.offlineHours", Tab = TabSpeed, En = "Extra offline time limit", Suffix = " h",
                Max = 720, Stats = { new("OfflineProgress", "FlatOfflineTime", "OfflineProgress", "Component", 3600000) } },

        // ---------------- Skills ----------------
        new() { Id = "skills.doubling", Tab = TabSkills, En = "Double product chance", Suffix = "%",
                Max = 100, Step = 5, ToMultiplier = Percent,
                Stats = OnSkills("DoublingChance", 1, "WoodcuttingAction", "MiningAction", "FishingAction", "FiremakingAction", "CookingAction",
                                 "ArtisanAction", "HerbloreAction", "ThievingAction", "Agility", "HuntingAction", "FarmingAction", "RanchingAction")
                        .Append(new StatRef("AstrologyAction", "ItemDoublingChance", "Astrology", "Action", 1)).ToList() },
        new() { Id = "skills.preservation", Tab = TabSkills, En = "Keep materials chance", Suffix = "%",
                Max = 100, Step = 5, ToMultiplier = Percent,
                Stats = OnSkills("PreservationChance", 1, "ArtisanAction", "CookingAction", "FarmingAction", "FiremakingAction",
                                 "HerbloreAction", "HuntingAction", "RanchingAction") },
        new() { Id = "skills.quantityPercent", Tab = TabSkills, En = "Product quantity", Suffix = "x",
                Min = 1, Max = 100, Off = 1, ToMultiplier = Times,
                Stats = OnSkills("PercentProductQuantity", 1, "ArtisanAction", "CookingAction", "FarmingAction", "HerbloreAction",
                                 "HuntingAction", "RanchingAction") },
        new() { Id = "skills.quantityFlat", Tab = TabSkills, En = "Extra products per action", Suffix = "",
                Max = 1000,
                Stats = OnSkills("FlatProductQuantity", 1, "WoodcuttingAction", "MiningAction", "FiremakingAction", "ArtisanAction",
                                 "CookingAction", "FarmingAction", "HerbloreAction", "HuntingAction", "RanchingAction") },
        new() { Id = "skills.cheaper", Tab = TabSkills, En = "Material cost reduction", Suffix = "%",
                Max = 100, Step = 5, ToMultiplier = Percent,
                Stats = OnSkills("PercentIngredientCost", -1, "ArtisanAction", "CookingAction", "FarmingAction", "FiremakingAction",
                                 "HerbloreAction", "RanchingAction") },
        new() { Id = "skills.instantCut", Tab = TabSkills, En = "Woodcutting: instant cut chance", Suffix = "%",
                Max = 100, Step = 5, ToMultiplier = Percent, Stats = { new("WoodcuttingAction", "InstantCutChance", "Woodcutting", "Action", 1) } },
        new() { Id = "skills.noStun", Tab = TabSkills, En = "Thieving: never stunned", Kind = FeatureKind.Toggle,
                Stats = { new("ThievingAction", "StunAvoidanceChance", "Thieving", "Action", 1) } },
        new() { Id = "skills.astroKnowledge", Tab = TabSkills, En = "Astrology: knowledge per study", Suffix = "x",
                Min = 1, Max = 1000, Off = 1, ToMultiplier = Times, Stats = { new("AstrologyAction", "PercentKnowledge", "Astrology", "Action", 1) } },

        // ---------------- Combat ----------------
        new() { Id = "combat.god", Tab = TabCombat, En = "God mode", Kind = FeatureKind.Toggle,
                Stats = { Player("AttackDamageNegationChance", 1), Player("PercentDamageTaken", -1) } },
        new() { Id = "combat.damage", Tab = TabCombat, En = "Damage dealt", Suffix = "x",
                Min = 1, Max = 1000, Off = 1, ToMultiplier = Times, Stats = { Player("PercentDamageDealt", 1) } },
        new() { Id = "combat.attackSpeed", Tab = TabCombat, En = "Attack speed", Suffix = "x",
                Min = 1, Max = 10, Off = 1, ToMultiplier = Faster, Stats = { Player("PercentAttackInterval", -1) } },
        new() { Id = "combat.accuracy", Tab = TabCombat, En = "Accuracy", Suffix = "x",
                Min = 1, Max = 100, Off = 1, ToMultiplier = Times, Stats = { Player("PercentAccuracyRating", 1) } },
        new() { Id = "combat.maxHp", Tab = TabCombat, En = "Maximum hitpoints", Suffix = "x",
                Min = 1, Max = 100, Off = 1, ToMultiplier = Times, Stats = { Player("PercentMaxHitpoints", 1) } },
        new() { Id = "combat.noDeathPenalty", Tab = TabCombat, En = "No death penalty", Kind = FeatureKind.Toggle,
                Stats = { Combat("CombatAction", "NoDeathPenalty", "Action", 1) } },
        new() { Id = "combat.lootDouble", Tab = TabCombat, En = "Double loot chance", Suffix = "%",
                Max = 100, Step = 5, ToMultiplier = Percent,
                Stats = { Combat("CombatLoot", "ItemDoublingChance", "Loot", 1), Combat("CombatLoot", "CurrencyDoublingChance", "Loot", 1),
                          Combat("CombatLoot", "BuriableDoublingChance", "Loot", 1) } },
        new() { Id = "combat.keepSupplies", Tab = TabCombat, En = "Keep supplies chance", Suffix = "%",
                Max = 100, Step = 5, ToMultiplier = Percent,
                Stats = { Combat("CombatFood", "FoodPreservationChance", "Food", 1), Combat("AmmoConsumption", "AmmoPreservationChance", "Player", 1),
                          Combat("MagicSpell", "RunePreservationChance", "MagicSpells", 1), Combat("Prayer", "CostPreservationChance", "Prayer", 1) } },
        new() { Id = "combat.slayerReward", Tab = TabCombat, En = "Slayer coin reward", Suffix = "x",
                Min = 1, Max = 100, Off = 1, ToMultiplier = Times, Stats = { new("SlayerTasks", "PercentRewardCurrency", "Slayer", "Tasks", 1) } },
        new() { Id = "combat.slayerFreeReroll", Tab = TabCombat, En = "Cheap slayer task rerolls", Kind = FeatureKind.Toggle,
                Stats = { new("SlayerTasks", "PercentRerollCost", "Slayer", "Tasks", -1) } },
    };

    /// <summary>Every skill in menu order.</summary>
    public static readonly string[] SkillNames =
    {
        "Woodcutting", "Mining", "Fishing", "Firemaking", "Cooking", "Smithing", "Crafting", "Runecrafting", "Fletching",
        "Beastcrafting", "Herblore", "Thieving", "Agility", "Astrology", "Hunting", "Farming", "Ranching",
        "Attack", "Strength", "Defence", "Hitpoints", "Ranged", "Magic", "Prayer", "Slayer",
    };

    // The stats that set how long one action takes, per stats class.
    static readonly Dictionary<string, string[]> IntervalStats = new()
    {
        ["WoodcuttingAction"] = new[] { "PercentInterval" }, ["MiningAction"] = new[] { "PercentInterval" },
        ["FiremakingAction"] = new[] { "PercentInterval" }, ["CookingAction"] = new[] { "PercentInterval" },
        ["ArtisanAction"] = new[] { "PercentInterval" }, ["ThievingAction"] = new[] { "PercentInterval" },
        ["Agility"] = new[] { "PercentInterval" }, ["AstrologyAction"] = new[] { "PercentInterval" },
        ["FarmingAction"] = new[] { "PercentInterval" },
        ["FishingAction"] = new[] { "PercentMinInterval", "PercentMaxInterval" },
        ["HerbloreAction"] = new[] { "PercentPreparingInterval", "PercentHeatingInterval", "PercentStirringInterval", "PercentBottlingInterval" },
        ["HuntingAction"] = new[] { "PercentTrackingInterval", "PercentTrappingInterval", "PercentSurveyInterval" },
        ["RanchingAction"] = new[] { "PercentProductInterval" },
    };

    /// <summary>Two settings per skill: its own XP multiplier and, for skills that perform actions, its own speed.</summary>
    static IEnumerable<Feature> PerSkill()
    {
        foreach (string name in SkillNames)
        {
            yield return new Feature
            {
                Id = $"skill.{name}.xp", Tab = TabPerSkill, En = name + " XP", Skill = name, Column = "xp", Suffix = "x",
                Min = 1, Max = 1000, Off = 1, ToMultiplier = Times, Stats = { new("Experience", "PercentXPGained", name, "Experience", 1) }
            };
            var skill = Skills.FirstOrDefault(s => s.Entity == name);
            if (skill.Entity == null) continue;
            yield return new Feature
            {
                Id = $"skill.{name}.speed", Tab = TabPerSkill, En = name + " speed", Skill = name, Column = "speed", Suffix = "x",
                Min = 1, Max = 20, Off = 1, ToMultiplier = Faster,
                Stats = IntervalStats[skill.Class].Select(stat => new StatRef(skill.Class, stat, skill.Entity, skill.Component, -1)).ToList()
            };
        }
    }

    public static readonly IReadOnlyList<Feature> All = Core.Concat(PerSkill()).ToList();

    /// <summary>One short line shown under a setting's name.</summary>
    public static readonly Dictionary<string, string> Hints = new()
    {
        ["bank.slots"] = "Adds bank slots on top of the ones you own.",
        ["bank.sellPrice"] = "Multiplies what you get for selling items.",
        ["bank.buryReward"] = "Multiplies what you get for burying bones.",
        ["xp.multiplier"] = "Multiplies all XP you gain, in every skill.",
        ["speed.game"] = "Runs the whole game faster: skills, combat and timers.",
        ["speed.actions"] = "Shortens every skill action. Combat is not affected. Multiplies with a skill's own speed on the Per skill page (x2 here and x4 there is x8); together they stop at x20.",
        ["speed.spawn"] = "Monsters appear sooner after a kill.",
        ["speed.offlineHours"] = "Raises the limit of offline progress you can collect.",
        ["skills.doubling"] = "Chance that a skill action gives double products.",
        ["skills.preservation"] = "Chance that an action does not use up its materials.",
        ["skills.quantityPercent"] = "Multiplies how many items an action produces.",
        ["skills.quantityFlat"] = "Adds this many extra items to every action.",
        ["skills.cheaper"] = "Recipes need fewer materials.",
        ["skills.instantCut"] = "Chance to fell a tree with a single hit.",
        ["skills.noStun"] = "A failed pickpocket never stuns you.",
        ["skills.astroKnowledge"] = "Multiplies the Knowledge each study adds to a constellation. Astrology XP is a separate setting.",
        ["combat.god"] = "You take no damage in combat.",
        ["combat.damage"] = "Multiplies all damage you deal.",
        ["combat.attackSpeed"] = "You attack this many times faster.",
        ["combat.accuracy"] = "Multiplies your accuracy rating.",
        ["combat.maxHp"] = "Multiplies your maximum hitpoints.",
        ["combat.noDeathPenalty"] = "Dying no longer costs you an item.",
        ["combat.lootDouble"] = "Chance that a kill drops double items, coins and bones.",
        ["combat.keepSupplies"] = "Chance not to use up food, ammo, runes or prayer points.",
        ["combat.slayerReward"] = "Multiplies the slayer coins earned per task kill.",
        ["combat.slayerFreeReroll"] = "Changing your slayer task costs 1 coin (the game's minimum).",
    };

    static readonly Dictionary<string, Feature> ById = All.ToDictionary(f => f.Id);

    public static Feature Get(string id) => ById[id];
    public static Feature? Find(string id) => ById.GetValueOrDefault(id);
}
