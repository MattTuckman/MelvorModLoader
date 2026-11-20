using System;
using System.Collections.Generic;
using System.Linq;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Components.Characters;
using GamesByMalcs.Melvor2.Components.CombatAreas;
using GamesByMalcs.Melvor2.Components.CombatFood;
using GamesByMalcs.Melvor2.Components.CombatLevels;
using GamesByMalcs.Melvor2.Components.CombatStats;
using GamesByMalcs.Melvor2.Components.CombatEffects;
using GamesByMalcs.Melvor2.Components.CombatPassives;
using GamesByMalcs.Melvor2.Components.Equipment;
using GamesByMalcs.Melvor2.Components.MagicSpells;
using GamesByMalcs.Melvor2.Components.Experience;
using GamesByMalcs.Melvor2.Data;
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.Entities;
using GamesByMalcs.Melvor2.Entities.Combat;
using GamesByMalcs.Melvor2.Modifiers;
using GamesByMalcs.Melvor2.Stats;

namespace Adventurers;

/// <summary>Who fights: an adventurer's levels, what they wear and how they fight.</summary>
public sealed record Fighter(IReadOnlyDictionary<CombatLevel, int> Levels, IReadOnlyList<Item> Gear, AttackStance Stance, Item? Food,
                             double EatAt = 0.25, double FoodUse = 1.0, double AccuracyBonus = 0.0, double ProtectionBonus = 0.0,
                             MagicSpell? Spell = null);

/// <summary>
/// What a fight between an adventurer and one monster comes to, worked out with the game's own combat numbers.
/// Nothing here is a second fighter: the game's own stat objects (the same ones that compute the player's
/// accuracy, max hit, evasion, damage reduction and attack speed) are built for the adventurer in a modifier
/// system of their own, which holds only what the adventurer has: their gear, its set bonuses, their stance and
/// the combat triangle. The player's own bonuses (pets, prayers, potions, agility, astrology...) never reach it.
/// </summary>
public sealed class Fight
{
    /// <summary>False when the adventurer cannot be sent there; <see cref="Why"/> says why, in plain words.</summary>
    public bool CanFight { get; init; }
    public string Why { get; init; } = "";

    public double HitChance { get; init; }        // adventurer -> monster, 0..1
    public int MinHit { get; init; }               // adventurer's own min/max hit, before the monster's reduction
    public int MaxHit { get; init; }
    public double AttackSeconds { get; init; }     // adventurer's attack interval
    public int MaxHp { get; init; }                 // adventurer's hitpoints
    public double EnemyHitChance { get; init; }    // monster -> adventurer
    public int EnemyMaxHit { get; init; }          // the most one monster attack can take off the adventurer
    public int EnemyHp { get; init; }
    public double AttacksPerKill { get; set; }    // the adventurer's attacks, misses included
    public double SecondsPerKill { get; set; }    // the 3 seconds before a monster appears included
    public double DamagePerKill { get; set; }      // hitpoints the adventurer loses per kill, on average
    public double FoodPerKill { get; set; }        // food eaten per kill, on average (0 when no damage is taken)
    public double AmmoPerKill { get; set; }        // ammunition used per kill (0 when the weapon uses none)
    public AmmoType? AmmoType { get; init; }
    /// <summary>Runes cast per kill, by rune (empty unless they fight with magic).</summary>
    public IReadOnlyList<(Item Rune, double PerKill)> Runes { get; set; } = new List<(Item, double)>();
    public IReadOnlyList<(Item Rune, double PerAttack)> RunesPerAttack { get; init; } = new List<(Item, double)>();
    public double AmmoPerKillSet { set => AmmoPerKill = value; }
    public IReadOnlyDictionary<CombatLevel, double> XpPerKill { get; set; } = new Dictionary<CombatLevel, double>();
    /// <summary>The fight in each of the monster's stances, by how likely the monster takes it.</summary>
    public IReadOnlyList<(double Weight, Duel Duel)> Duels { get; init; } = new List<(double, Duel)>();
    /// <summary>True when no blow of the monster can take them from above their eating point to nothing.</summary>
    public bool Safe { get; init; }
    public int EatAt { get; init; }
    public int Heal { get; init; }
    /// <summary>The adventurer's own special attacks that are counted, with how often they come (empty: none).</summary>
    public IReadOnlyList<(string Name, double Chance)> MySpecials { get; init; } = new List<(string, double)>();
    /// <summary>What of those attacks is left out because it only helps them (the real fight is a little better).</summary>
    public IReadOnlyList<string> LeftOut { get; init; } = new List<string>();

    public double KillsPerHour => SecondsPerKill > 0 ? 3600.0 / SecondsPerKill : 0;

    public static Fight No(string why) => new() { CanFight = false, Why = why };
}

/// <summary>
/// One fight against one monster in one of its stances, as raw numbers: what each side's attack lands with, the
/// damage a landed blow does (as a table to roll from), the attack intervals, the hitpoints, the eating rule and the
/// XP. The dungeon simulation plays fights out blow by blow from these.
/// </summary>
public sealed class Duel
{
    public string Enemy = "";
    public int EnemyHp;
    public long MyInterval, TheirInterval;                     // in the game's ticks (60 a second, TickTimer.MSToTicks)
    public const double TicksPerSecond = 60;
    public static long Ticks(double ms) => Math.Max(1, (long)Math.Round(ms / (1000.0 / TicksPerSecond)));
    public double MyLand, TheirLand;
    public int[] MyDamage = Array.Empty<int>(), TheirDamage = Array.Empty<int>();
    public double[] MyOdds = Array.Empty<double>(), TheirOdds = Array.Empty<double>();   // running totals, ending at 1
    public int TheirMaxHit;
    public List<(CombatLevel Level, double PerDamage, double OnKill)> Xp = new();
    public List<(Item Rune, int PerCast)> Runes = new();
    public double RunesKept;                                   // the chance a cast costs nothing
    public bool UsesAmmo;

    // the monster's side, blow by blow (CharacterComponent.PerformAttack / ModifyAttackDamage)
    public int TheirMin, TheirMax;                             // its min and max hit
    public double TheirHit, TheirNegate, TheirCrit, TheirCritMult;
    public List<Strike> Strikes = new();                       // its attacks, the plain one included, with their chances
    public List<Trigger> Triggers = new();                     // its passives that apply effects to the adventurer
    public bool Special;                                       // anything beyond the plain attack
    public int EnemyArmour;                                    // taken off the adventurer's blows until one is as big
    // statuses (stun, sleep, effects that last some turns): the whole duel again for every mix of them
    public List<Status> Statuses = new();                      // what this monster may bring, numbered (shared by the variants)
    public Duel[]? Variants;                                   // by the statuses' levels (Status.Stride); [0] has none
    public double[] StatusAvoid = Array.Empty<double>();       // the chance each status is avoided, by its number
    public double[] StatusScale = Array.Empty<double>();       // what the adventurer's reduction leaves of end-of-turn damage
    // the monster's ward (a second pool that softens blows until it is worn down) and lifesteal
    public int EnemyWard;
    public double TheirLifesteal, MyDoubleWard;
    public int[] MyRaw = Array.Empty<int>();                   // the adventurer's roll with critical hits, before any reduction
    public double[] MyRawOdds = Array.Empty<double>();
    public double MyScale;                                     // what the monster's reduction leaves of it
    public int MyMin, MyMax;                                   // the adventurer's min and max hit (an effect of theirs reads them)
    public double TheirReflect, ReflectScale;                  // the share of a blow the monster sends back, and what reaches them
    public double MyReflect, MyReflectScale;                   // the same from the adventurer (an effect on them: Sharp Skin)
    public double EffectScale;                                 // what their reduction leaves of an effect's damage in general
    public List<Trigger> MyTriggers = new();                   // the area's triggers that belong to the adventurer
    public Dictionary<string, Dot> Dots = new();               // its damage over time, by id
    // the adventurer's own attacks when their weapon (or spell) has special ones (empty: the plain blow only, as above)
    public List<Strike> MyStrikes = new();
    public double MyHit, MyCrit, MyCritMult, MyNegate;          // hit chance (before the monster's negation), critical hits, its negation
    public bool MyImmune;                                      // the monster cannot be hit at all
    public Dictionary<string, Dot> MonsterDots = new();        // the damage over time their attacks put on the monster, by id
    public bool MineSpecial => MyStrikes.Any(s => !s.Plain);

    public static (int[] Values, double[] Odds) Table(IReadOnlyDictionary<int, double> blows)
    {
        var list = blows.Where(kv => kv.Key > 0 && kv.Value > 0).OrderBy(kv => kv.Key).ToList();
        double total = list.Sum(kv => kv.Value), run = 0;
        var values = new int[list.Count];
        var odds = new double[list.Count];
        for (int i = 0; i < list.Count; i++) { values[i] = list[i].Key; run += list[i].Value / total; odds[i] = run; }
        if (odds.Length > 0) odds[^1] = 1;
        return (values, odds);
    }

    public static int Roll(int[] values, double[] odds, double u)
    {
        int i = Array.BinarySearch(odds, u);
        if (i < 0) i = ~i;
        return values[Math.Min(i, values.Length - 1)];
    }
}

public static class Combat
{
    /// <summary>The game waits this long after a kill before the next monster appears (CombatActionComponent.BaseSpawnInterval).</summary>
    public const double SpawnSeconds = 3.0;

    public static CombatEntity? Entity(Game game) =>
        game.Entities.TryGetObjectById("MelvorBase:Combat", out GameEntity? e) ? e as CombatEntity : null;

    /// <summary>The six combat levels, by their id without the namespace ("Attack", "Hitpoints"...).</summary>
    public static Dictionary<string, CombatLevel> Levels(Game game) =>
        game.Data.GetDataRegistryOutput<CombatLevel>().GetAllObjects().ToDictionary(l => Local(l.Id), l => l);

    public static string Local(string id) => id.Contains(':') ? id[(id.IndexOf(':') + 1)..] : id;

    /// <summary>
    /// The monsters an adventurer can be sent to in this version: in an area the player has unlocked, a monster
    /// the player may fight, and one whose fighting the model knows completely (a plain attack, no special
    /// attacks or passives, an area without effects or darkness). <paramref name="why"/> explains the rest.
    /// </summary>
    public static bool Supported(BaseCombatArea area, Enemy enemy, out string why)
    {
        why = "";
        if (!Specials.Known(enemy, out string unknown)) why = $"Not yet: {unknown}.";
        else if (!Specials.KnownArea(area, out string place)) why = $"Not yet: {place}.";
        else if (Specials.VariantCount(Specials.Statuses(enemy, area)) > Specials.MostVariants) why = "Not yet: too many effects at once there.";
        return why == "";
    }

    public static bool Unlocked(CombatArea area, Enemy enemy) =>
        area.UnlockRequirements.AreMet && area.DisplayRequirements.AreMet && enemy.Requirements.AreMet
        && (enemy.DisplayRequirements?.AreMet ?? true);

    /// <summary>
    /// Whether the adventurer may wear an item: combat level requirements are checked against the adventurer's
    /// own levels, any other requirement (Slayer, Prayer, Astrology...) against the player's, as in the game.
    /// </summary>
    public static bool CanWear(Game game, IReadOnlyDictionary<CombatLevel, int> levels, Item item, out string why)
    {
        why = "";
        if (!item.Capabilities.TryGet(out EquipmentCapability? equipment)) { why = "It cannot be worn."; return false; }
        // a lit lantern burns down as its wearer is hit in the dark (EquipmentCapability.ConsumesOn): not for adventurers yet
        if (equipment.ConsumesOn is { Count: > 0 }) { why = "It is used up as it is worn; adventurers cannot use those yet."; return false; }
        var byComponent = levels.Keys.ToDictionary(l => (object)l.Component, l => l);
        foreach (var condition in equipment.Requirements.Conditions)
        {
            if (condition is LevelCondition level && byComponent.TryGetValue(level.Component, out CombatLevel? combat))
            {
                int have = levels.TryGetValue(combat, out int v) ? v : 1;
                if (have < level.Level) { why = $"Needs {Name(combat)} {level.Level} (the adventurer has {have})."; return false; }
            }
            else if (!condition.IsMet)
            {
                why = condition is LevelCondition other ? $"Needs your {other.Component.Entity.DisplayName.Text} level {other.Level}." : "You do not meet its requirements.";
                return false;
            }
        }
        return true;
    }

    public static string Name(CombatLevel level) => level.Component.Entity.DisplayName.Text;

    /// <summary>
    /// Whether the adventurer may cast a spell: its Magic level against the adventurer's own, anything else (the
    /// spell book's Astrology event) against the player's.
    /// </summary>
    public static bool CanCast(Game game, IReadOnlyDictionary<CombatLevel, int> levels, MagicSpell spell, out string why)
    {
        why = "";
        var byComponent = levels.Keys.ToDictionary(l => (object)l.Component, l => l);
        foreach (var condition in spell.UnlockRequirements.Conditions.Concat(spell.Book.Requirements.Conditions))
        {
            if (condition is LevelCondition level && byComponent.TryGetValue(level.Component, out CombatLevel? combat))
            {
                int have = levels.TryGetValue(combat, out int v) ? v : 1;
                if (have < level.Level) { why = $"Needs {Name(combat)} {level.Level} (the adventurer has {have})."; return false; }
            }
            else if (!condition.IsMet) { why = $"You have not unlocked {spell.Book.DisplayName.Text} yet."; return false; }
        }
        return true;
    }

    /// <summary>The spells a weapon can cast (its elements), plain ones only, in the game's order.</summary>
    public static List<MagicSpell> Spells(Game game, IEnumerable<Item> gear)
    {
        var weapon = Weapon(gear);
        if (weapon == null) return new List<MagicSpell>();
        return game.Data.GetDataRegistryOutput<MagicSpell>().GetAllObjects()
            .Where(s => s.AttackOverride == null && weapon.Value.Weapon.Elements.Contains(s.Element)).ToList();
    }

    /// <summary>The weapon among the gear, if any.</summary>
    public static (Item Item, EquipmentCapability Equipment, WeaponCapability Weapon)? Weapon(IEnumerable<Item> gear)
    {
        foreach (Item item in gear)
            if (item.Capabilities.TryGet(out WeaponCapability? weapon) && item.Capabilities.TryGet(out EquipmentCapability? equipment))
                return (item, equipment, weapon);
        return null;
    }

    /// <summary>The stances the adventurer's weapon allows (fists: the melee ones).</summary>
    public static IReadOnlyList<AttackStance> Stances(Game game, IEnumerable<Item> gear)
    {
        var weapon = Weapon(gear);
        WeaponType type = weapon?.Weapon.Type ?? Fists(game).Weapon.Type;
        return type.Stances.ToList();
    }

    static (Item Item, EquipmentCapability Equipment, WeaponCapability Weapon) Fists(Game game)
    {
        Item fists = game.Data.GetDataRegistryOutput<Item>().GetObjectById("MelvorBase:DefaultWeapon");
        return (fists, fists.Capabilities.Get<EquipmentCapability>(), fists.Capabilities.Get<WeaponCapability>());
    }

    /// <summary>Works out the fight. <paramref name="useGameModifiers"/> is for the tests only: the player's own
    /// modifier system instead of the adventurer's, so the result can be compared with the player's live stats.</summary>
    public static Fight Evaluate(Game game, Fighter who, BaseCombatArea area, Enemy enemy, ModifierSystem? useGameModifiers = null, bool allowRisk = false, bool forDungeon = false)
    {
        if (Entity(game) is not { } combat) return Fight.No("The game's combat was not found.");
        if (!Supported(area, enemy, out string why)) return Fight.No(why);
        if (!Specials.Known(enemy, out string unknown)) return Fight.No($"{enemy.DisplayName.Text}: {unknown}.");

        var data = game.Data;
        var weapon = Weapon(who.Gear) ?? Fists(game);
        if (!weapon.Weapon.Type.Stances.Contains(who.Stance)) return Fight.No("The weapon cannot be used in that stance.");
        MagicSpell? spell = null;
        if (who.Stance.CombatStyle.UsesSpells)
        {
            spell = who.Spell;
            if (spell == null) return Fight.No("Choose a spell for them to cast.");
            if (spell.AttackOverride != null && !Specials.WeaponSpecials) return Fight.No($"{spell.DisplayName.Text} is a special spell; adventurers cannot cast those yet.");
            if (!weapon.Weapon.Elements.Contains(spell.Element))
                return Fight.No($"{weapon.Item.DisplayName.GetPluralText(1)} cannot cast {spell.Element.DisplayName.Text} spells.");
            if (!CanCast(game, who.Levels, spell, out string cannot)) return Fight.No(cannot);
        }
        if (!Specials.WeaponSpecials && who.Gear.FirstOrDefault(i => i.Capabilities.TryGet(out EquipmentCapability? e) && e.Attacks is { Count: > 0 }) is { } special)
            return Fight.No($"{special.DisplayName.GetPluralText(1)} has a special attack; adventurers cannot use those yet.");
        // their own special attacks (Combat Advisor): every one must be known, what only helps them may be left out
        Attack plainAttack = data.GetDataRegistryOutput<Attack>().GetObjectById("MelvorBase:Normal");
        var mine = Specials.WeaponSpecials ? Specials.MyAttacks(who.Gear, spell, plainAttack) : new List<(Attack Attack, double Chance)>();
        var leftOut = new List<string>();
        if (mine.Count > 0 && !Specials.KnownMine(mine, out string mineWhy, leftOut))
        {
            string owner = spell?.AttackOverride != null ? spell.DisplayName.Text
                : who.Gear.FirstOrDefault(i => i.Capabilities.TryGet(out EquipmentCapability? e) && e.Attacks is { Count: > 0 })?.DisplayName.GetPluralText(1) ?? "The weapon";
            return Fight.No($"{owner} has a special attack the advisor does not know yet ({mineWhy}).");
        }
        if (who.Gear.FirstOrDefault(i => i.Capabilities.TryGet(out EquipmentCapability? e) && e.ConsumesOn is { Count: > 0 }) is { } spent)
            return Fight.No($"{spent.DisplayName.GetPluralText(1)} is used up as it is worn; adventurers cannot use those yet.");
        AmmoType? ammo = weapon.Weapon.RequiredAmmoType;
        if (ammo != null && !who.Gear.Any(i => i.Capabilities.TryGet(out EquipmentCapability? e) && e.AmmoType == ammo))
            return Fight.No("The weapon needs ammunition.");

        // One stance of the monster at a time; a monster that has several picks one when it appears.
        var stances = enemy.Stances.ToList();
        int weightSum = stances.Sum(s => s.Weight);
        if (stances.Count == 0 || weightSum <= 0) return Fight.No("This monster has no way of fighting the model knows.");

        var parts = new List<(double W, Side S)>();
        foreach (WeightedAttackStance s in stances)
        {
            var statuses = Specials.Statuses(enemy, area, mine);
            long count = Specials.VariantCount(statuses);
            if (count > Specials.MostVariants && mine.Count > 0)
            {
                // too many at once with their own attacks' statuses: those only help them, so they are left out
                statuses = Specials.Statuses(enemy, area);
                count = Specials.VariantCount(statuses);
                foreach (var (attack, _) in mine)
                    foreach (AttackEffectChance e in attack.PreHitEffects.Concat(attack.OnHitEffects))
                        if (e.Effect is not ConfiguredDamageOverTimeEffect && !leftOut.Contains(e.Effect.Data.DisplayName.Text)) leftOut.Add(e.Effect.Data.DisplayName.Text);
            }
            if (count > Specials.MostVariants) return Fight.No($"{enemy.DisplayName.Text}: too many effects at once (not yet).");
            // the fight with every mix of the statuses' levels, as the game's stat objects work it out (one set of them)
            List<Side> all = Work(game, combat, who, weapon, spell, area, enemy, s.Stance, useGameModifiers, statuses, (int)count, mine, plainAttack);
            Side side = all[0];
            if (side.Why != "") return Fight.No(side.Why);
            if (count > 1)
            {
                var variants = all.Select(v => v.Duel).ToArray();
                foreach (Side v in all) side.EnemyMaxHit = Math.Max(side.EnemyMaxHit, v.EnemyMaxHit);
                foreach (Duel d in variants) d.Variants = variants;
                // damage over time rolled under one mix of statuses may tick under another
                foreach (var g in variants.SelectMany(d => d.Dots.Values).GroupBy(d => d.Key))
                    side.EnemyMaxHit = Math.Max(side.EnemyMaxHit,
                        (int)Math.Max(g.Max(d => d.MostPer) * g.Max(d => d.MaxStacks) * g.Max(d => Math.Max(d.Scale, d.GapScale)), 1));
                side.Duel.TheirMaxHit = side.EnemyMaxHit;
            }
            parts.Add(((double)s.Weight / weightSum, side));
        }

        Side first = parts.OrderByDescending(p => p.W).First().S;
        double attacks = parts.Sum(p => p.W * p.S.AttacksPerKill);
        double seconds = SpawnSeconds + parts.Sum(p => p.W * p.S.AttacksPerKill * p.S.AttackSeconds);
        double damage = parts.Sum(p => p.W * p.S.DamagePerKill);
        int enemyMax = parts.Max(p => p.S.EnemyMaxHit);
        int maxHp = first.MaxHp;

        // Safe to send: one blow of the monster must never take the adventurer from just above the point where
        // they eat to nothing. The game eats at a share of max hitpoints (CombatFoodComponent.AutoEat).
        int eatAt = (int)Math.Round(who.EatAt * maxHp);
        bool safe = enemyMax < eatAt;
        if (allowRisk ? enemyMax >= maxHp : !safe)
            return Fight.No(allowRisk
                ? $"Too dangerous: one hit of {enemy.DisplayName.Text} can do {enemyMax} damage, all of their {maxHp} hitpoints. More Defence, Hitpoints or armour is needed."
                : $"Too dangerous: one hit of this monster can do {enemyMax} damage, and the adventurer only eats at {eatAt} of {maxHp} hitpoints. More Defence, Hitpoints or armour is needed.");
        // (a special attack that cannot be avoided may still hurt it: the playout finds out)
        if ((double.IsInfinity(attacks) || attacks <= 0) && !parts.Any(p => p.S.Duel.MineSpecial)) return Fight.No("The adventurer cannot hurt this monster.");

        double food = 0;
        int heal = 0;
        if (who.Food != null && who.Food.Capabilities.TryGet(out FoodCapability? dish)) heal = first.Heal > 0 ? first.Heal : dish.BaseHealing;
        if (damage > 0)
        {
            if (who.Food == null || !who.Food.Capabilities.TryGet(out FoodCapability? meal) || meal.BaseHealing <= 0)
                return Fight.No("This monster hits back: the adventurer needs food.");
            // The game eats when hitpoints fall to the threshold and never heals above the maximum: one serving
            // brings back at most what is missing at that point (CombatFoodComponent.AutoEat, CharacterHealth.Heal).
            double healed = Math.Min(heal, Math.Max(1, maxHp - eatAt));
            food = damage / healed * who.FoodUse;
        }

        // XP: the game gives XP for the damage done (the monster's hitpoints, no more), by stance, plus a bonus
        // for armour and ward on the kill (CombatExperienceComponent / CombatExperienceStats).
        var xp = new Dictionary<CombatLevel, double>();
        var levels = Levels(game);
        foreach (var gain in who.Stance.ExperienceGain)
        {
            CombatLevel? level = levels.Values.FirstOrDefault(l => ReferenceEquals(l.Component, gain.Component));
            if (level == null) continue;
            double perKill = parts.Sum(p => p.W * gain.ExperiencePerDamage * (p.S.EnemyHp + 4.0 * p.S.EnemyArmour + 50.0 * p.S.EnemyWard));
            xp[level] = xp.GetValueOrDefault(level) + perKill;
            foreach (var (_, side) in parts)
                side.Duel.Xp.Add((level, gain.ExperiencePerDamage, gain.ExperiencePerDamage * (4.0 * side.EnemyArmour + 50.0 * side.EnemyWard)));
        }

        var made = new Fight
        {
            CanFight = true,
            HitChance = first.HitChance, MinHit = first.MinHit, MaxHit = first.MaxHit, AttackSeconds = first.AttackSeconds,
            MaxHp = maxHp, EnemyHitChance = first.EnemyHitChance, EnemyMaxHit = enemyMax, EnemyHp = first.EnemyHp,
            AttacksPerKill = attacks, SecondsPerKill = seconds, DamagePerKill = damage, FoodPerKill = food,
            AmmoPerKill = ammo != null ? attacks : 0, AmmoType = ammo, XpPerKill = xp,
            Runes = first.Runes.Select(r => (r.Rune, r.PerAttack * attacks)).ToList(),
            RunesPerAttack = first.Runes,
            Duels = parts.Select(p => (p.W, p.S.Duel)).ToList(),
            Safe = safe, EatAt = eatAt, Heal = heal,
            MySpecials = mine.Where(m => m.Attack != plainAttack).Select(m => (m.Attack.DisplayName.Text, m.Chance)).ToList(),
            LeftOut = leftOut,
        };
        // A monster with special attacks or effects, or their own special attacks: the damage is found by playing a
        // couple of thousand kills out blow by blow (the averaged formula above only knows a plain blow).
        bool played = made.Duels.Any(d => d.Duel.Special || d.Duel.EnemyArmour > 0 || d.Duel.MineSpecial);
        // (a dungeon's plan plays its runs itself: it needs only the duels)
        if (forDungeon) return made;
        RunResult? hunted = null;
        if (played && made.Heal > 0) hunted = Dungeons.Hunting(made, who.FoodUse);
        else if (made.Duels.Any(d => d.Duel.Special && d.Duel.Strikes.Any(x => x.Hits.Count > 0) && d.Duel.TheirHit > 0 && d.Duel.TheirMax > 0))
            return Fight.No("This monster hits back: the adventurer needs food.");
        else if (played) hunted = Dungeons.Hunting(made, who.FoodUse);
        if (hunted != null && hunted.Withdrawn)
            return Fight.No(hunted.Why == "Out of food." ? "This monster hurts them: the adventurer needs food."
                : area.Darkness > 0 && first.HitChance < 0.05
                ? $"It is too dark there for the adventurer to hit {enemy.DisplayName.Text}: they need light (a lantern, glowing food...)."
                : $"The adventurer cannot beat {enemy.DisplayName.Text}: they cannot hurt it fast enough.");
        if (hunted != null && hunted.Defeated && !allowRisk)
            return Fight.No($"Too dangerous: {enemy.DisplayName.Text} can knock them out. More Defence, Hitpoints or armour is needed.");
        return made;
    }

    /// <summary>The fight against the monster in one of its stances.</summary>
    sealed class Side
    {
        public string Why = "";
        public double HitChance, EnemyHitChance, AttackSeconds, AttacksPerKill, DamagePerKill;
        public int MinHit, MaxHit, MaxHp, EnemyMaxHit, EnemyHp, EnemyArmour, EnemyWard, Heal;
        public DamageType? MyType;
        public List<(Item Rune, double PerAttack)> Runes = new();
        public Duel Duel = new();
    }

    static List<Side> Work(Game game, CombatEntity combat, Fighter who, (Item Item, EquipmentCapability Equipment, WeaponCapability Weapon) weapon,
                     MagicSpell? spell, BaseCombatArea area, Enemy enemy, AttackStance enemyStance, ModifierSystem? gameModifiers,
                     List<Status> statuses, int count, List<(Attack Attack, double Chance)> myAttacks, Attack plainAttack)
    {
        var data = game.Data;
        var combatStats = data.GetDataRegistryOutput<CombatStat>();
        var damageTypes = data.GetDataRegistryOutput<DamageType>();
        var allStances = data.GetDataRegistryOutput<AttackStance>().GetAllObjects().ToList();
        var levelRegistry = data.GetDataRegistryOutput<CombatLevel>();
        Element neutral = data.GetDataRegistryOutput<Element>().GetObjectById("MelvorBase:Neutral");

        ModifierSystem mods;
        if (gameModifiers != null) mods = gameModifiers;
        else
        {
            mods = new ModifierSystem(data.StatClasses);
            mods.OnDataLoaded();
        }
        bool own = gameModifiers == null;
        // Only what holds whatever the player is doing: an entry with a condition or a scaling reads the player's
        // own situation (their Slayer task, their hitpoints...), so it is left out. That can only make the
        // adventurer weaker than the player with the same gear, never stronger.
        var added = new List<(INamedObject Source, ModifierValues Values)>();
        void Add(INamedObject source, ModifierValues? values)
        {
            if (!own || values == null) return;
            var plain = values.Component.Where(v => v.Condition == null && v.Scaling == null).ToList();
            if (plain.Count == 0) return;
            var only = new ModifierValues(new List<GlobalModifierValue>(), plain, new List<GamesByMalcs.Melvor2.Components.CombatEffects.CombatEffectModifierValue>());
            mods.Add(source, only);
            added.Add((source, only));
        }

        // The adventurer: gear stats and bonuses, set bonuses, stance bonuses (EquipmentComponent, PlayerComponent).
        var mine = new SetCombatStats();
        mine.Setup(combatStats, damageTypes);
        bool armed = false;
        foreach (Item item in who.Gear)
        {
            if (!item.Capabilities.TryGet(out EquipmentCapability? equipment)) continue;
            mine.Add(item, equipment.Stats);
            Add(item, equipment.Bonuses);
            if (item.Capabilities.Contains<WeaponCapability>()) armed = true;
        }
        if (!armed) mine.Add(weapon.Item, weapon.Equipment.Stats);
        if (spell != null) mine.Add(spell, spell.Stats);              // the spell's max hit (MagicSpellsComponent.SelectSpell)
        if (own)
        {
            foreach (EquipmentSetBonus set in data.GetDataRegistryOutput<EquipmentSetBonus>().GetAllObjects())
            {
                int worn = set.Items.Count(i => who.Gear.Contains(i));
                foreach (SetBonusModifiers tier in set.Bonuses)
                    if (worn >= tier.ItemCount) Add(set, tier.Modifiers);
            }
            Add(who.Stance, who.Stance.Bonuses);
            if (who.Food != null && who.Food.Capabilities.TryGet(out FoodCapability? meal) && meal.Bonuses?.Component == combat.Food) Add(who.Food, meal.Bonuses.Modifiers);
        }
        var myLevels = new SetCombatLevels(levelRegistry);
        myLevels.SetLevels(who.Levels);
        Element element = spell?.Element ?? (weapon.Weapon.Elements.Count > 0 ? weapon.Weapon.Elements[0] : neutral);
        var me = new CharacterStats(mods, combat.Player, mine, myLevels, who.Stance, element, allStances, area.Darkness);

        // The monster (EnemyComponent): its stats and levels as the game sets them.
        var theirs = new SetCombatStats();
        theirs.Setup(combatStats, damageTypes);
        theirs.Add(enemy, enemy.Stats);
        var theirLevels = new SetCombatLevels(levelRegistry);
        theirLevels.SetLevels(enemy.Levels);
        var them = new CharacterStats(mods, combat.Enemy, theirs, theirLevels, enemyStance, enemy.Element, allStances, 0);

        // The combat triangle and elements, as CombatActionComponent adds them while fighting.
        if (own)
        {
            foreach (CombatTriangleBonus bonus in combat.Action.GetCombatTriangleBonuses(me.CombatStyle.Value, them.CombatStyle.Value, true))
                Add(bonus, bonus.Bonuses);
            foreach (ElementalBonus bonus in combat.Action.GetElementalBonuses(me.Element.Value, them.Element.Value, true))
                Add(bonus, bonus.Bonuses);
            Add(area.Category, area.Category.Bonuses);
            // the monster's and the area's passives (EnemyComponent / CombatAreasComponent: CombatPassivesComponent.AddPassives)
            foreach (CombatPassive passive in enemy.Passives.Concat(area.Passives).Distinct()) Add(passive, passive.Modifiers);
        }
        int magnitude = 0;
        if (area.Effect != null)
        {
            var areaStats = new CombatAreasStats(mods, combat.Areas, area);
            try { magnitude = areaStats.AreaEffectMagnitude.Value; }
            finally { try { areaStats.Destroy(); } catch { } }
            // (in the tests' parity mode the game's own system already holds the area the player is fighting in)
            if (own && magnitude > 0)
            {
                mods.Add(area, area.Effect.Modifiers, magnitude);
                added.Add((area, area.Effect.Modifiers));
            }
        }
        var meOnThem = Target(mods, me, them);
        var themOnMe = Target(mods, them, me);
        // The runes one cast costs, with what their gear takes off (MagicSpellStats.RuneCosts: at least 1 of each).
        MagicSpellStats? runeStats = spell != null ? new MagicSpellStats(mods, combat.MagicSpells, spell) : null;
        // each status templated once for the one who has it (ActiveCombatEffect.GetTemplatedModifiers)
        var templated = statuses.ToDictionary(st => st, st => st.Modifiers?.CreateModifiers(st.OnMonster ? combat.Enemy : combat.Player));
        // what their food heals, as the game works it out (CombatFoodStats.Healing: the area's effect, the gear's bonuses)
        int heal = 0;
        if (who.Food != null && who.Food.Capabilities.TryGet(out FoodCapability? meal2))
        {
            var foodStats = new CombatFoodStats(mods, combat.Food, new FoodItem(who.Food, meal2));
            try { heal = foodStats.Healing.Value; }
            finally { try { foodStats.Destroy(); } catch { } }
        }
        var sides = new List<Side>();
        try
        {
            for (int index = 0; index < count; index++)
            {
                // the statuses on in this variant (stacks multiply what they change: AddOrUpdateModifiers with the stacks);
                // in the tests' parity mode too, taken off again below
                var statusAdded = new List<(INamedObject Source, ModifierValues Values)>();
                foreach (Status st in statuses)
                {
                    int level = st.Levels > 1 ? index / st.Stride % st.Levels : 0;
                    if (level <= 0 || templated[st] is not { } values) continue;
                    mods.Add(st.Effect, values, st.Kind is Status.Kinds.Stacking or Status.Kinds.Random or Status.Kinds.DotStats ? level : 1);
                    statusAdded.Add((st.Effect, values));
                }
                try
                {
                    // (the averaged numbers are not needed when their own special attacks are played out blow by blow)
                    bool plainOnly = myAttacks.All(a => a.Attack == plainAttack);
                    Side side = Read(who, weapon, enemy, me, them, meOnThem, themOnMe, index == 0 && plainOnly, plainOnly);
                    Build(game, combat, mods, who, enemy, me, them, themOnMe, side, statuses, area, magnitude, meOnThem, myAttacks, plainAttack);
                    side.Heal = heal;
                    if (runeStats != null)
                    {
                        // One roll saves the whole cast (MagicSpellsComponent.TryConsumeAttackRuneCosts): on average a share of every rune.
                        double kept = Math.Clamp(runeStats.RunePreservationChance.Total.Value, 0, 1);
                        side.Runes = runeStats.RuneCosts.Values.Select(v => (v.Value.Item, v.Value.Quantity * (1 - kept))).ToList();
                        side.Duel.Runes = runeStats.RuneCosts.Values.Select(v => (v.Value.Item, v.Value.Quantity)).ToList();
                        side.Duel.RunesKept = kept;
                    }
                    sides.Add(side);
                    if (index == 0 && side.Why != "") break;
                }
                finally
                {
                    foreach (var (source, values) in statusAdded)
                        try { mods.Remove(source, values); } catch { }
                }
            }
            return sides;
        }
        finally
        {
            // Stat objects listen to their modifier system: let go of them, above all when it is the game's own.
            foreach (ComponentStats? stats in new ComponentStats?[] { meOnThem, themOnMe, me, them, runeStats })
                try { stats?.Destroy(); } catch { }
            // and the entries added to the adventurer's own system, which listen to the game in turn
            foreach (var (source, values) in added)
                try { mods.Remove(source, values); } catch { }
        }
    }

    static Side Read(Fighter who, (Item Item, EquipmentCapability Equipment, WeaponCapability Weapon) weapon, Enemy enemy,
                     CharacterStats me, CharacterStats them, TargetCharacterStats meOnThem, TargetCharacterStats themOnMe, bool averages = true, bool myTables = true)
    {
        var side = new Side
        {
            MaxHp = me.MaxHitpoints.Value, EnemyHp = them.MaxHitpoints.Value,
            EnemyArmour = them.BaseArmour.Value, EnemyWard = them.BaseMaxWard.Value,
            AttackSeconds = me.AttackInterval.Value / 1000.0,
            MinHit = me.MinHit.Value, MaxHit = me.MaxHit.Value,
        };


        // Adventurer -> monster: CharacterComponent.PerformAttack and ModifyAttackDamage.
        DamageType myType = weapon.Weapon.DamageType;
        double hit = Math.Clamp(meOnThem.HitChance.Value + who.AccuracyBonus, 0, 1);
        if (meOnThem.ImmunityFrom.Total.Value > 0) hit = 0;
        side.MyType = myType;
        side.HitChance = hit;
        double negate = them.AttackDamageNegationChance.Total.Value;
        double scale = (1 + me.PercentDamageDealt.Total.Value)
                       * (1 + them.PercentDamageTaken.Total.Value + meOnThem.PercentDamageTakenFrom.Total.Value + meOnThem.PercentDamageTakenFromAttacks.Total.Value)
                       * (1 - meOnThem.DefenderResistances.Dict[myType].Value);
        // The monster's armour only stops blows smaller than it; applying it to every blow is the cautious side.
        // (their own special attacks are rolled hit by hit in the playout: no tables of their blows then)
        var dealt = myTables ? Blows(side.MinHit, side.MaxHit, me.CriticalHitChance.Value, me.CriticalHitMultiplier.Value, scale, them.Armour.Value) : new Dictionary<int, double>();
        var bare = myTables && them.Armour.Value > 0 ? Blows(side.MinHit, side.MaxHit, me.CriticalHitChance.Value, me.CriticalHitMultiplier.Value, scale, 0) : dealt;
        double land = hit * (1 - Math.Clamp(negate, 0, 1));
        side.AttacksPerKill = averages ? AttacksToKill(side.EnemyHp, land, dealt) : 0;

        // Monster -> adventurer, with the adventurer's protection bonus (a trait) on top.
        double enemyHit = themOnMe.HitChance.Value;
        side.EnemyHitChance = enemyHit;
        double resist = Math.Clamp(themOnMe.DefenderResistances.Dict[enemy.DamageType].Value + who.ProtectionBonus, 0, 1);
        double theirScale = (1 + them.PercentDamageDealt.Total.Value)
                            * (1 + me.PercentDamageTaken.Total.Value + themOnMe.PercentDamageTakenFrom.Total.Value + themOnMe.PercentDamageTakenFromAttacks.Total.Value)
                            * (1 - resist);
        // Armour is left out on this side, so the adventurer is never thought safer or thriftier than they are.
        var taken = Blows(them.MinHit.Value, them.MaxHit.Value, them.CriticalHitChance.Value, them.CriticalHitMultiplier.Value, theirScale, 0);
        side.EnemyMaxHit = enemyHit > 0 && taken.Count > 0 ? taken.Keys.Max() : 0;
        double perBlow = taken.Sum(kv => kv.Key * kv.Value);
        // Both start their attack timers when the monster appears; the monster swings only as often as it gets
        // to before the adventurer's killing blow (a monster killed in one blow by a faster weapon never swings).
        double theirAttacks = averages ? Swings(side.EnemyHp, land, dealt, me.AttackInterval.Value, them.AttackInterval.Value, side.AttacksPerKill) : 0;
        double theirLand = enemyHit * (1 - Math.Clamp(me.AttackDamageNegationChance.Total.Value, 0, 1));
        side.DamagePerKill = theirAttacks * theirLand * perBlow;

        var mine = Duel.Table(bare);
        var theirs = Duel.Table(taken);
        var raw = Duel.Table(myTables ? Blows(side.MinHit, side.MaxHit, me.CriticalHitChance.Value, me.CriticalHitMultiplier.Value, 1.0, 0) : new Dictionary<int, double>());
        side.Duel = new Duel
        {
            Enemy = enemy.DisplayName.Text, EnemyHp = side.EnemyHp,
            MyInterval = Duel.Ticks(me.AttackInterval.Value), TheirInterval = Duel.Ticks(them.AttackInterval.Value),
            MyLand = land, TheirLand = theirLand,
            MyDamage = mine.Values, MyOdds = mine.Odds, TheirDamage = theirs.Values, TheirOdds = theirs.Odds,
            TheirMaxHit = side.EnemyMaxHit, UsesAmmo = weapon.Weapon.RequiredAmmoType != null,
            EnemyArmour = them.Armour.Value,
            EnemyWard = them.MaxWard.Value, TheirLifesteal = Math.Max(0, them.Lifesteal.Value), MyScale = scale,
            MyDoubleWard = Math.Clamp(me.DoubleWardDamageChance.Total.Value, 0, 1),
            MyRaw = raw.Values, MyRawOdds = raw.Odds, MyMin = side.MinHit, MyMax = side.MaxHit,
            TheirReflect = Math.Max(0, them.PercentAttackReflect.Total.Value),
            MyReflect = Math.Max(0, me.PercentAttackReflect.Total.Value),
            MyReflectScale = (1 + me.PercentDamageDealt.Total.Value) * (1 + them.PercentDamageTaken.Total.Value + meOnThem.PercentDamageTakenFrom.Total.Value)
                             * (1 - Math.Clamp(meOnThem.DefenderResistances.Dict[myType].Value, 0, 1)),
            ReflectScale = (1 + them.PercentDamageDealt.Total.Value) * (1 + me.PercentDamageTaken.Total.Value + themOnMe.PercentDamageTakenFrom.Total.Value) * (1 - resist),
        };
        return side;
    }

    /// <summary>
    /// The monster's side of the duel blow by blow: its attacks with the chance each is chosen (the plain one gets
    /// what the others leave, CharacterComponent.NormalizeAttackChances), their hits and damage, the effects they and
    /// its passives apply, with what the adventurer's reduction and avoidance make of them. Also the safety number:
    /// the biggest single damage the monster can do (one hit, or one tick of an effect at its most stacks).
    /// </summary>
    static void Build(Game game, CombatEntity combat, ModifierSystem mods, Fighter who, Enemy enemy, CharacterStats me, CharacterStats them,
                      TargetCharacterStats themOnMe, Side side, List<Status> statuses, BaseCombatArea area, int magnitude,
                      TargetCharacterStats meOnThem, List<(Attack Attack, double Chance)> myAttacks, Attack plainAttack)
    {
        Duel duel = side.Duel;
        duel.TheirMin = them.MinHit.Value;
        duel.TheirMax = them.MaxHit.Value;
        duel.TheirHit = themOnMe.HitChance.Value;
        duel.TheirNegate = Math.Clamp(me.AttackDamageNegationChance.Total.Value, 0, 1);
        duel.TheirCrit = Math.Clamp(them.CriticalHitChance.Value, 0, 1);
        duel.TheirCritMult = them.CriticalHitMultiplier.Value;
        duel.Special = Specials.Any(enemy) || duel.EnemyWard > 0 || duel.TheirLifesteal > 0 || duel.TheirReflect > 0;
        double dealt = 1 + them.PercentDamageDealt.Total.Value;
        double Resist(DamageType type) => 1 - Math.Clamp(themOnMe.DefenderResistances.Dict[type].Value + who.ProtectionBonus, 0, 1);
        double attackScale(DamageType type) => dealt * (1 + me.PercentDamageTaken.Total.Value + themOnMe.PercentDamageTakenFrom.Total.Value + themOnMe.PercentDamageTakenFromAttacks.Total.Value) * Resist(type);

        var dots = new Dictionary<(DamageOverTimeEffect, bool), Dot>();
        Dot DotOf(ConfiguredDamageOverTimeEffect configured, bool bySelf)
        {
            DamageOverTimeEffect e = configured.Effect;
            if (dots.TryGetValue((e, bySelf), out Dot? known)) return known;
            if (bySelf)
            {
                // the adventurer as its source (an area's effect): their own damage, through their own reduction (SelfTargetStats)
                var own = new CombatEffectStats(mods, (CharacterComponent)combat.Player, (CharacterComponent)combat.Player, (CombatEffect)e);
                var self = Target(mods, me, me);
                try
                {
                    double scale = (1 + me.PercentDamageDealt.Total.Value)
                                   * (1 + me.PercentDamageTaken.Total.Value + own.PercentDamageTaken.Total.Value + self.PercentDamageTakenFrom.Total.Value)
                                   * (1 - Math.Clamp(self.DefenderResistances.Dict[side.MyType ?? enemy.DamageType].Value + who.ProtectionBonus, 0, 1));
                    return dots[(e, true)] = new Dot
                    {
                        Id = e.Id, Name = e.DisplayName.Text, Interval = Duel.Ticks(e.ProcInterval), Procs = e.ProcCount,
                        MaxStacks = Math.Max(1, e.MaxStacks), DefaultStacks = Math.Max(1, e.DefaultStacksToAdd),
                        Formula = e.DamagePerProc.Formula, OfSource = e.DamagePerProc.Character == EffectCharacter.Source,
                        Avoid = Math.Clamp(own.AvoidanceChance.Total.Value, 0, 1), Scale = scale, GapScale = scale, BySelf = true,
                    };
                }
                finally { try { own.Destroy(); } catch { } try { self.Destroy(); } catch { } }
            }
            var stats = new CombatEffectStats(mods, (CharacterComponent)combat.Player, (CharacterComponent)combat.Enemy, (CombatEffect)e);
            try
            {
                return dots[(e, false)] = new Dot
                {
                    Id = e.Id, Name = e.DisplayName.Text, Interval = Duel.Ticks(e.ProcInterval), Procs = e.ProcCount,
                    MaxStacks = Math.Max(1, e.MaxStacks), DefaultStacks = Math.Max(1, e.DefaultStacksToAdd),
                    Formula = e.DamagePerProc.Formula, OfSource = e.DamagePerProc.Character == EffectCharacter.Source,
                    Avoid = Math.Clamp(stats.AvoidanceChance.Total.Value, 0, 1),
                    Scale = dealt * (1 + me.PercentDamageTaken.Total.Value + stats.PercentDamageTaken.Total.Value + themOnMe.PercentDamageTakenFrom.Total.Value) * Resist(enemy.DamageType),
                    GapScale = dealt * (1 + me.PercentDamageTaken.Total.Value + stats.PercentDamageTaken.Total.Value),
                    Lifesteal = Math.Max(0, stats.EffectLifesteal.Total.Value),
                };
            }
            finally { try { stats.Destroy(); } catch { } }
        }
        EffectUse Use(ConfiguredCombatEffect effect, double chance, RelativeCharacter to, bool byMe = false)
        {
            if (effect is not ConfiguredDamageOverTimeEffect)
            {
                bool onMonster = byMe ? to == RelativeCharacter.Target : to == RelativeCharacter.Self;
                Status status = statuses.First(x => x.Effect == effect.Data && x.OnMonster == onMonster);
                var use = new EffectUse { Chance = chance, Status = status };
                switch (effect)
                {
                    case ConfiguredStunEffect s: use.Turns = s.Configuration.Turns; break;
                    case ConfiguredTurnCountingEffect c: use.Turns = c.Configuration.Turns; break;
                    case ConfiguredStackingEffect k: use.Stacks = k.Configuration.StacksToAdd; break;
                    case ConfiguredRandomMagnitudeEffect r:
                        use.Turns = r.Configuration.Turns; use.MinStacks = r.Configuration.MinStacks; use.MaxStacks = r.Configuration.MaxStacks; break;
                }
                return use;
            }
            var configured = (ConfiguredDamageOverTimeEffect)effect;
            return new EffectUse { Chance = chance, Dot = DotOf(configured, byMe), Stacks = configured.Configuration.StacksToAdd };
        }

        // the attacks and their chances (the plain one first, with what the others leave)
        Attack plain = game.Data.GetDataRegistryOutput<Attack>().GetObjectById("MelvorBase:Normal");
        var chances = new List<(Attack Attack, double Chance)>();
        double total = enemy.Attacks.Sum(a => a.Chance);
        if (total < 1) chances.Add((plain, 1 - total));
        foreach (var g in enemy.Attacks.GroupBy(a => a.Attack)) chances.Add((g.Key, total < 1 ? g.Sum(a => a.Chance) : g.Sum(a => a.Chance) / total));
        int most = 0;
        foreach (var (attack, chance) in chances)
        {
            var strike = new Strike { Name = attack.DisplayName.Text, Chance = chance, Avoidable = attack.IsAvoidable, Scale = attackScale(attack.DamageType ?? enemy.DamageType) };
            foreach (AttackHit hit in attack.Hits)
            {
                strike.Hits.Add((hit.Count, Duel.Ticks(hit.Interval), hit.Damage.Select(d => (d.Formula, d.Character == AttackCharacter.Defender)).ToList()));
                int raw = hit.Damage.Sum(d => Specials.Most(d.Formula, d.Character == AttackCharacter.Defender ? side.MaxHit : duel.TheirMax, d.Character == AttackCharacter.Defender ? side.MaxHp : side.EnemyHp));
                int crit = duel.TheirCrit > 0 ? (int)(raw * duel.TheirCritMult) : raw;
                most = Math.Max(most, (int)Math.Max(crit * strike.Scale, 1));
            }
            foreach (var e in attack.PreHitEffects) strike.PreHit.Add(Use(e.Effect, e.Chance, e.AppliesTo));
            foreach (var e in attack.OnHitEffects) strike.OnHit.Add(Use(e.Effect, e.Chance, e.AppliesTo));
            duel.Strikes.Add(strike);
        }

        // The adventurer's own attacks when they have special ones (CharacterComponent.PerformAttack, every hit):
        // their hits and damage, through the monster's reduction by the attack's damage type (ModifyAttackDamage),
        // and what they put on the monster or on themselves.
        int myMostBlow = 0;
        if (myAttacks.Count > 0)
        {
            duel.MyHit = side.HitChance;
            duel.MyImmune = meOnThem.ImmunityFrom.Total.Value > 0;
            duel.MyCrit = Math.Clamp(me.CriticalHitChance.Value, 0, 1);
            duel.MyCritMult = me.CriticalHitMultiplier.Value;
            duel.MyNegate = Math.Clamp(them.AttackDamageNegationChance.Total.Value, 0, 1);
            double MyScale(DamageType type) => (1 + me.PercentDamageDealt.Total.Value)
                * (1 + them.PercentDamageTaken.Total.Value + meOnThem.PercentDamageTakenFrom.Total.Value + meOnThem.PercentDamageTakenFromAttacks.Total.Value)
                * (1 - meOnThem.DefenderResistances.Dict[type].Value);
            DamageType myType = side.MyType ?? enemy.DamageType;
            var monsterDots = new Dictionary<DamageOverTimeEffect, Dot>();
            Dot MonsterDot(ConfiguredDamageOverTimeEffect configured)
            {
                // on the monster, from them (ActiveCombatEffect.Damage -> ModifyEffectDamage, the source's damage type)
                DamageOverTimeEffect e = configured.Effect;
                if (monsterDots.TryGetValue(e, out Dot? known)) return known;
                var stats = new CombatEffectStats(mods, (CharacterComponent)combat.Enemy, (CharacterComponent)combat.Player, (CombatEffect)e);
                try
                {
                    return monsterDots[e] = new Dot
                    {
                        Id = e.Id, Name = e.DisplayName.Text, Interval = Duel.Ticks(e.ProcInterval), Procs = e.ProcCount,
                        MaxStacks = Math.Max(1, e.MaxStacks), DefaultStacks = Math.Max(1, e.DefaultStacksToAdd),
                        Formula = e.DamagePerProc.Formula, OfSource = e.DamagePerProc.Character == EffectCharacter.Source,
                        Avoid = Math.Clamp(stats.AvoidanceChance.Total.Value, 0, 1),
                        Scale = (1 + me.PercentDamageDealt.Total.Value)
                                * (1 + them.PercentDamageTaken.Total.Value + stats.PercentDamageTaken.Total.Value + meOnThem.PercentDamageTakenFrom.Total.Value)
                                * (1 - meOnThem.DefenderResistances.Dict[myType].Value),
                        Lifesteal = Math.Max(0, stats.EffectLifesteal.Total.Value), OnMonster = true,
                    };
                }
                finally { try { stats.Destroy(); } catch { } }
            }
            EffectUse? MineUse(AttackEffectChance e)
            {
                if (!Specials.MineEffect(e, out bool keep, out _) || !keep) return null;
                if (e.Effect is ConfiguredDamageOverTimeEffect d) return new EffectUse { Chance = e.Chance, Dot = MonsterDot(d), Stacks = d.Configuration.StacksToAdd };
                bool onMonster = e.AppliesTo == RelativeCharacter.Target;
                // (left out when there were too many statuses at once)
                return statuses.Any(x => x.Effect == e.Effect.Data && x.OnMonster == onMonster) ? Use(e.Effect, e.Chance, e.AppliesTo, byMe: true) : null;
            }
            foreach (var (attack, chance) in myAttacks)
            {
                var strike = new Strike
                {
                    Name = attack.DisplayName.Text, Plain = attack == plainAttack, Chance = chance, Avoidable = attack.IsAvoidable,
                    Scale = MyScale(attack.DamageType ?? myType),
                };
                foreach (AttackHit hit in attack.Hits)
                {
                    strike.Hits.Add((hit.Count, Duel.Ticks(hit.Interval), hit.Damage.Select(d => (d.Formula, d.Character == AttackCharacter.Defender)).ToList()));
                    int raw = hit.Damage.Sum(d => Specials.Most(d.Formula, d.Character == AttackCharacter.Defender ? duel.TheirMax : side.MaxHit, d.Character == AttackCharacter.Defender ? side.EnemyHp : side.MaxHp));
                    int crit = duel.MyCrit > 0 ? (int)(raw * duel.MyCritMult) : raw;
                    myMostBlow = Math.Max(myMostBlow, (int)Math.Max(crit * strike.Scale, 1));
                }
                foreach (var e in attack.PreHitEffects) if (MineUse(e) is { } u) strike.PreHit.Add(u);
                foreach (var e in attack.OnHitEffects) if (MineUse(e) is { } u) strike.OnHit.Add(u);
                duel.MyStrikes.Add(strike);
            }
            duel.MonsterDots = monsterDots.Values.ToDictionary(d => d.Id, d => d);
        }
        // identical trigger entries add their chances (CombatEffectModifierTable): one roll for them all
        foreach (var passive in enemy.Passives)
            foreach (var v in passive.Modifiers.CombatEffect)
            {
                string on = Specials.TriggerName(v.Modifier.Trigger);
                EffectUse use = Use(v.Modifier.Effect, v.Chance, v.Modifier.AppliesTo, !v.IsEnemy);
                List<Trigger> list = v.IsEnemy ? duel.Triggers : duel.MyTriggers;
                Trigger? same = list.FirstOrDefault(x => x.On == on && x.Use.Dot == use.Dot && x.Use.Stacks == use.Stacks
                                                         && x.Use.Status == use.Status && x.Use.Turns == use.Turns && x.Use.MinStacks == use.MinStacks && x.Use.MaxStacks == use.MaxStacks);
                if (same != null) same.Use.Chance += use.Chance;
                else list.Add(new Trigger { On = on, Use = use });
            }
        // the area's: its effect's (chance times its strength) and its passives', the monster's or the adventurer's own
        var fromEffect = new HashSet<CombatEffectModifierValue>(area.Effect?.Modifiers.CombatEffect ?? Enumerable.Empty<CombatEffectModifierValue>());
        foreach (CombatEffectModifierValue v in Specials.AreaTriggers(area))
        {
            double chance = v.Chance * (fromEffect.Contains(v) ? magnitude : 1);
            if (chance <= 0) continue;
            string on = Specials.TriggerName(v.Modifier.Trigger);
            EffectUse use = Use(v.Modifier.Effect, chance, v.Modifier.AppliesTo, !v.IsEnemy);
            List<Trigger> list = v.IsEnemy ? duel.Triggers : duel.MyTriggers;
            Trigger? same = list.FirstOrDefault(x => x.On == on && x.Use.Dot == use.Dot && x.Use.Stacks == use.Stacks && x.Use.Status == use.Status && x.Use.Turns == use.Turns && x.Use.MinStacks == use.MinStacks && x.Use.MaxStacks == use.MaxStacks);
            if (same != null) same.Use.Chance += use.Chance;
            else list.Add(new Trigger { On = on, Use = use });
            duel.Special = true;
        }
        int mostBlow = most;                    // the biggest blow, as reduced (what a "damage dealt" effect reads)
        foreach (Dot dot in dots.Values)
        {
            int per = dot.Formula is DamageDealtDamageFormula dd ? (int)(Math.Max(dot.BySelf ? side.MaxHit * 2 : mostBlow, 1) * dd.Percent)
                      : Specials.Most(dot.Formula, dot.BySelf ? side.MaxHit : duel.TheirMax, dot.OfSource && !dot.BySelf ? side.EnemyHp : side.MaxHp);
            dot.MostPer = Math.Max(per, 1);
            most = Math.Max(most, (int)Math.Max(dot.MostPer * dot.MaxStacks * Math.Max(dot.Scale, dot.GapScale), 1));
        }
        if (duel.TheirReflect > 0 && (duel.MyDamage.Length > 0 || myMostBlow > 0))
        {
            int top = Math.Min(Math.Max(duel.MyDamage.Length > 0 ? duel.MyDamage[^1] : 0, myMostBlow), side.EnemyHp);
            most = Math.Max(most, Math.Max((int)((int)(top * duel.TheirReflect) * duel.ReflectScale), 1));
        }
        if (duel.Special) side.EnemyMaxHit = Math.Max(side.EnemyMaxHit, most);
        duel.TheirMaxHit = side.EnemyMaxHit;
        duel.Dots = dots.Values.ToDictionary(d => d.Key, d => d);
        duel.EffectScale = dealt * (1 + me.PercentDamageTaken.Total.Value + themOnMe.PercentDamageTakenFrom.Total.Value) * Resist(enemy.DamageType);
        foreach (Dot d in dots.Values) if (!d.BySelf) d.PlainScale = duel.EffectScale;

        // how likely each status is avoided by the one it would go on (CombatEffectsComponent.ApplyEffect): a stun's
        // Staggered, for one, makes them avoid the next stun. What a status leaves comes from the holder itself.
        duel.Statuses = statuses;
        duel.StatusAvoid = new double[statuses.Count];
        duel.StatusScale = new double[statuses.Count];
        foreach (Status st in statuses)
        {
            var holder = (CharacterComponent)(st.OnMonster ? combat.Enemy : combat.Player);
            var source = (CharacterComponent)(st.ByAdventurer ? combat.Player : combat.Enemy);
            var stats = new CombatEffectStats(mods, holder, source, st.Effect);
            try
            {
                duel.StatusAvoid[st.Bit] = Math.Clamp(stats.AvoidanceChance.Total.Value, 0, 1);
                if (st.Kind == Status.Kinds.EndDamage)
                {
                    // as damage over time (ActiveCombatEffect.Damage -> ModifyEffectDamage), by the monster's damage type
                    duel.StatusScale[st.Bit] = dealt * (1 + me.PercentDamageTaken.Total.Value + stats.PercentDamageTaken.Total.Value
                                                        + themOnMe.PercentDamageTakenFrom.Total.Value) * Resist(enemy.DamageType);
                    int per = Specials.Most(st.Formula!, st.OfSource ? duel.TheirMax : side.MaxHit, st.OfSource ? side.EnemyHp : side.MaxHp);
                    side.EnemyMaxHit = Math.Max(side.EnemyMaxHit, (int)Math.Max(per * duel.StatusScale[st.Bit], 1));
                    duel.TheirMaxHit = side.EnemyMaxHit;
                }
            }
            finally { try { stats.Destroy(); } catch { } }
        }
    }

    /// <summary>Who attacks whom, exactly as CharacterComponent.CreateTargetStats builds it.</summary>
    static TargetCharacterStats Target(ModifierSystem mods, CharacterStats attacker, CharacterStats defender) =>
        new(mods, attacker.Component, attacker.CombatStyle, attacker.AttackStance, attacker.Element, attacker.AccuracyRating, attacker.PowerRatings,
            defender.Component, defender.EvasionRatings, defender.ProtectionRatings, defender.CombatStyle, defender.AttackStance, defender.Element);

    /// <summary>
    /// The damage one landed blow does and how likely each amount is: a roll between min and max hit
    /// (NormalDamageFormula), a critical hit now and then, then the defender's reduction, at least 1.
    /// </summary>
    public static Dictionary<int, double> Blows(int min, int max, double critChance, double critMultiplier, double scale, int armour)
    {
        var odds = new Dictionary<int, double>();
        if (max < min) max = min;
        long count = (long)max - min + 1;
        // Very wide rolls are walked in even steps; the shape is what matters, not every single number.
        long step = Math.Max(1, count / 4000);
        critChance = Math.Clamp(critChance, 0, 1);
        void Add(int raw, double p)
        {
            if (p <= 0 || raw <= 0) return;
            int done = (int)Math.Max(raw * scale - armour, 1.0);
            odds[done] = odds.GetValueOrDefault(done) + p;
        }
        var rolls = new List<long>();
        for (long r = min; r < max; r += step) rolls.Add(r);
        rolls.Add(max);                                           // (the biggest hit is always in)
        double each = 1.0 / rolls.Count;
        foreach (long r in rolls)
        {
            Add((int)r, each * (1 - critChance));
            Add((int)(r * critMultiplier), each * critChance);
        }
        return odds;
    }

    /// <summary>
    /// How many attacks it takes on average to bring <paramref name="hp"/> hitpoints to nothing, when each attack
    /// lands with <paramref name="land"/> and then does damage by <paramref name="blows"/>. Worked out exactly
    /// for up to a few million steps, beyond that with the usual renewal estimate (mean plus the average overshoot).
    /// </summary>
    public static double AttacksToKill(int hp, double land, IReadOnlyDictionary<int, double> blows)
    {
        if (hp <= 0) return 0;
        if (land <= 0 || blows.Count == 0) return double.PositiveInfinity;
        var list = blows.Where(kv => kv.Key > 0 && kv.Value > 0).OrderBy(kv => kv.Key).ToArray();
        double total = list.Sum(kv => kv.Value);
        if (total <= 0) return double.PositiveInfinity;
        if ((long)hp * list.Length <= 1_000_000)
        {
            // f(h) = 1 + (1 - land) f(h) + land * sum(P(d) f(h - d))   =>   f(h) = 1 / land + sum(P(d) f(h - d))
            var f = new double[hp + 1];
            for (int h = 1; h <= hp; h++)
            {
                double sum = 0;
                foreach (var (d, p) in list) sum += p / total * (d >= h ? 0 : f[h - d]);
                f[h] = 1 / land + sum;
            }
            return f[hp];
        }
        double mean = list.Sum(kv => kv.Key * kv.Value) / total;
        double square = list.Sum(kv => (double)kv.Key * kv.Key * kv.Value) / total;
        return (hp + square / (2 * mean)) / mean / land;
    }

    /// <summary>
    /// How often the monster swings on average before it dies: the number of its whole attack intervals that fit
    /// before the adventurer's killing attack, averaged over how many attacks the kill takes. Worked out from the
    /// spread of attacks per kill where that is small enough, otherwise as plain time over interval (a little more).
    /// </summary>
    public static double Swings(int hp, double land, IReadOnlyDictionary<int, double> blows, double myInterval, double theirInterval, double meanAttacks)
    {
        if (theirInterval <= 0) return 0;
        double plain = meanAttacks * myInterval / theirInterval;
        var list = blows.Where(kv => kv.Key > 0 && kv.Value > 0).ToArray();
        double total = list.Sum(kv => kv.Value);
        if (hp <= 0 || land <= 0 || total <= 0 || (long)hp * list.Length > 200_000) return plain;
        // (each round walks every hitpoint and blow: past a couple of million steps the plain estimate is used, the
        // game's frame must not wait for it)
        if ((double)hp * list.Length * Math.Min(20_000, Math.Max(1, meanAttacks) * 3) > 2_000_000) return plain;
        var alive = new double[hp + 1];                          // chance the monster has this many hitpoints left
        alive[hp] = 1;
        double swings = 0, left = 1;
        for (int k = 1; k <= 20_000 && left > 1e-9; k++)
        {
            var next = new double[hp + 1];
            double killed = 0;
            for (int h = 1; h <= hp; h++)
            {
                double m = alive[h];
                if (m == 0) continue;
                next[h] += m * (1 - land);
                foreach (var (d, p) in list)
                {
                    double q = m * land * p / total;
                    if (d >= h) killed += q; else next[h - d] += q;
                }
            }
            swings += killed * Math.Floor(k * myInterval / theirInterval);
            left -= killed;
            alive = next;
        }
        return left > 1e-6 ? plain : swings;
    }

    /// <summary>The game's XP table: level for XP and XP for level (1..99).</summary>
    public static int LevelFor(double xp) => Math.Min(99, StandardExperienceTable.Default.GetLevelFromXp(xp));
    public static double XpFor(int level) => StandardExperienceTable.Default.GetXpForLevel(level);
}
