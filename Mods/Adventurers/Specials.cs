using System;
using System.Collections.Generic;
using System.Linq;
using GamesByMalcs.Melvor2.Components.Characters;
using GamesByMalcs.Melvor2.Components.CombatAreas;
using GamesByMalcs.Melvor2.Components.CombatEffects;
using GamesByMalcs.Melvor2.Components.CombatEffects.EffectTriggers;
using GamesByMalcs.Melvor2.Components.CombatPassives;
using GamesByMalcs.Melvor2.Entities.Combat;
using GamesByMalcs.Melvor2.Modifiers;

namespace Adventurers;

/// <summary>A damage-over-time effect (poison, burn, bleed...) as the game's data has it (DamageOverTimeEffect).</summary>
public sealed class Dot
{
    public string Id = "", Name = "";
    public long Interval;                 // ticks between procs
    public int Procs, MaxStacks, DefaultStacks;
    public DamageFormula Formula = null!;
    public bool OfSource;                 // the formula reads the one who applied it (else the one who has it)
    public double Avoid;                  // the adventurer's chance to avoid it (CombatEffectStats.AvoidanceChance)
    public double Scale;                  // what the adventurer's reduction leaves of its damage (ModifyEffectDamage)
    public double GapScale;               // the same between monsters: no resistance then (the next monster has no target yet)
    public int MostPer;                   // the most one stack can do per proc (before the reduction), for the safety number
    public double Lifesteal;              // what its damage heals the monster (CombatEffectStats.EffectLifesteal)
    public double PlainScale;             // the reduction of effects in general in the fight it came from (Scale / PlainScale: its own part)
    public bool BySelf;                   // put on the adventurer by the adventurer (an area's effect): they are its source
    public bool OnMonster;                // put on the monster by the adventurer's own attack (Scale: the monster's reduction)
    public string Key => OnMonster ? "monster:" + Id : BySelf ? "self:" + Id : Id;
}

/// <summary>
/// An effect that stays on the adventurer or the monster for a while and is not damage over time: a stun or sleep
/// (StunEffect), one that lasts some turns (TurnCountingEffect), one that stacks up for the fight (StackingEffect), one
/// of a random strength for some turns (RandomMagnitudeEffect), or damage or healing at the end of each turn
/// (EndOfTurnDamageEffect / EndOfTurnHealingEffect). What it changes in the stats is in the duel's variants.
/// </summary>
public sealed class Status
{
    public enum Kinds { Turns, Stacking, Random, EndDamage, EndHeal, DotStats }   // DotStats: what a damage over time also changes, by its stacks
    public string Id = "", Name = "";
    public Kinds Kind;
    public bool OnMonster;                // whose it is (it counts that one's turns)
    public bool ByAdventurer;             // put there by the adventurer's own attack (its source for avoiding it)
    public bool Stuns;
    public int DefaultTurns;
    public Status? Then;                  // what it leaves when it runs out (RemovalEffect)
    public int? ThenTurns;
    public int MaxStacks = 1, AddStacks = 1, MinStacks = 1;   // stacking: most and per use; random: the range
    public int DefaultMax = 1;            // a random one's own most (a use that sets none rolls up to it)
    public DamageFormula? Formula;        // end of turn: the damage or healing
    public bool OfSource;                 // ... read from the one who applied it (else the one who has it)
    public int Levels = 2;                // its levels in the variants (0 = off); 1 when it changes no stat
    public int Stride = 1;                // its place in the variant index
    public int Bit;                       // its number (avoidance, scale)
    public CombatEffect Effect = null!;
    public CombatEffectModifierTemplate? Modifiers;
}

/// <summary>An effect an attack or a passive applies, and how likely: damage over time, or a status.</summary>
public sealed class EffectUse
{
    public double Chance;
    public Dot Dot = null!;               // (null for a status)
    public int? Stacks;
    public Status? Status;
    public int? Turns;
    public int? MinStacks, MaxStacks;     // a random one's range, when the use sets it
    public string Name => Status?.Name ?? Dot.Name;
}

/// <summary>One of the monster's attacks (Attack): its chance to be chosen, its hits, and what it applies. Also one of
/// the adventurer's own (a weapon's special attack, <see cref="Duel.MyStrikes"/>); <see cref="Plain"/>: their plain blow.</summary>
public sealed class Strike
{
    public string Name = "";
    public bool Plain;
    public double Chance;
    public List<(int Count, long Gap, List<(DamageFormula Formula, bool OfDefender)> Parts)> Hits = new();
    public bool Avoidable = true;
    public double Scale;                  // what the adventurer's reduction leaves (by the attack's damage type)
    public List<EffectUse> PreHit = new(), OnHit = new();
}

/// <summary>A passive of the monster (or of the area, for the monster or the adventurer) that applies an effect when something happens.</summary>
public sealed class Trigger
{
    public string On = "";                // OnAttack, OnHit, OnFirstAttack, ... WhenHit, WhenAttacked, ... AtStartOfFight, OnDeath
    public EffectUse Use = new();
}

/// <summary>
/// What the model knows of monsters' special attacks and passives (step A: multi-hit attacks, the attack chosen each
/// turn, damage formulas, damage over time on the adventurer, passives that apply it or only shield the monster from
/// effects). <see cref="Known"/> says whether a monster uses anything beyond that.
/// </summary>
public static class Specials
{
    static bool KnownFormula(DamageFormula f) =>
        f is NormalDamageFormula or MaxHitDamageFormula or CurrentHitpointsDamageFormula or DamageDealtDamageFormula or FixedDamageFormula;

    // what a status may change: plain character stats the game's stat objects work out (and avoiding effects)
    static readonly HashSet<string> StatusStats = new()
    {
        "PercentDamageTaken", "PercentAttackInterval", "PercentEvasionRating", "FlatEvasionRating", "PercentAccuracyRating",
        "FlatProtectionRating", "PercentProtectionRating", "FlatPowerRating", "PercentMaxHit", "AvoidanceChance",
        "FlatCombatStatValue", "PercentCombatStatValue", "PercentAttackReflect",
    };
    static readonly System.Reflection.FieldInfo? TemplateValues =
        typeof(ModifierTemplate).GetField("_baseValues", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

    /// <summary>The entries of a status's modifier template.</summary>
    public static List<ComponentModifierValue> Entries(CombatEffectModifierTemplate? t) =>
        t != null && TemplateValues?.GetValue(t) is List<ComponentModifierValue> list ? list : new List<ComponentModifierValue>();

    static bool KnownStatus(ConfiguredCombatEffect e, RelativeCharacter to, int depth, out string why, bool byAdventurer = false)
    {
        why = "";
        string name = e.Data.DisplayName.Text;
        (CombatEffectModifierTemplate? mods, ConfiguredCombatEffect? then) = e switch
        {
            ConfiguredStunEffect s => (s.Effect.Modifiers, s.Effect.RemovalEffect),
            ConfiguredTurnCountingEffect c => (c.Effect.Modifiers, c.Effect.RemovalEffect),
            ConfiguredStackingEffect k => (k.Effect.Modifiers, null),
            ConfiguredRandomMagnitudeEffect r => (r.Effect.Modifiers, null),
            _ => (null, null),
        };
        // (the monster stunned by the adventurer's own trigger is known; the monster stunning itself is not)
        if (e is ConfiguredStunEffect && to != RelativeCharacter.Target && !byAdventurer) { why = "the monster stuns itself (not yet)"; return false; }
        if (e is ConfiguredEndOfTurnDamageEffect d)
        {
            if (to != RelativeCharacter.Target) { why = $"{name} on itself (not yet)"; return false; }
            if (!KnownFormula(d.Effect.DamagePerTurn.Formula)) { why = $"{name}'s damage (not yet)"; return false; }
            return true;
        }
        if (e is ConfiguredEndOfTurnHealingEffect h)
        {
            if (to != RelativeCharacter.Self) { why = $"{name} on the adventurer (not yet)"; return false; }
            if (!KnownFormula(h.Effect.HealingPerTurn.Formula)) { why = $"{name}'s healing (not yet)"; return false; }
            return true;
        }
        if (!TemplateKnown(mods)) { why = $"{name} (not yet)"; return false; }
        if (then == null) return true;
        if (depth > 3 || then is not (ConfiguredStunEffect or ConfiguredTurnCountingEffect)) { why = $"{then.Data.DisplayName.Text} (not yet)"; return false; }
        return KnownStatus(then, to, depth + 1, out why, byAdventurer);
    }

    static bool TemplateKnown(CombatEffectModifierTemplate? mods) =>
        TemplateValues != null && Entries(mods).All(v => v.Condition == null && v.Scaling == null && v.FromContext == null && StatusStats.Contains(v.Stat.Name)
            && v.Stat.StatsClassId == (v.Stat.Name == "AvoidanceChance" ? "MelvorBase:CombatEffect" : "MelvorBase:Character"));

    static bool IsStatus(ConfiguredCombatEffect e) =>
        e is ConfiguredStunEffect or ConfiguredTurnCountingEffect or ConfiguredStackingEffect or ConfiguredRandomMagnitudeEffect
          or ConfiguredEndOfTurnDamageEffect or ConfiguredEndOfTurnHealingEffect;

    /// <summary>The most duel variants one monster stance may need (every mix of its statuses' levels).</summary>
    public const int MostVariants = 1024;

    /// <summary>
    /// The statuses a monster can put on the adventurer or on itself (its attacks, its passives, what those leave when
    /// they run out), each once per side, with their levels and places in the variant index.
    /// </summary>
    public static List<Status> Statuses(Enemy enemy, BaseCombatArea? area = null, IEnumerable<(Attack Attack, double Chance)>? mine = null)
    {
        var list = new List<Status>();
        Status? Of(ConfiguredCombatEffect e, bool onMonster, int depth, bool byMe = false)
        {
            Status? s = Make(e, onMonster, depth, byMe);
            if (s != null && byMe) s.ByAdventurer = true;
            return s;
        }
        Status? Make(ConfiguredCombatEffect e, bool onMonster, int depth, bool byMe = false)
        {
            // what a damage over time also changes: on the adventurer (the monster's), or on the monster (their own attack's)
            if (e is ConfiguredDamageOverTimeEffect dot && dot.Effect.Modifiers != null && (!onMonster || byMe))
            {
                if (list.FirstOrDefault(x => x.Effect == dot.Effect && x.Kind == Status.Kinds.DotStats && x.OnMonster == onMonster) is { } seen) return seen;
                var withStats = new Status
                {
                    Id = dot.Effect.Id, Name = dot.Effect.DisplayName.Text, Kind = Status.Kinds.DotStats, Bit = list.Count, Effect = dot.Effect, OnMonster = onMonster,
                    Modifiers = dot.Effect.Modifiers, MaxStacks = Math.Max(1, dot.Effect.MaxStacks), Levels = Math.Max(1, dot.Effect.MaxStacks) + 1,
                };
                list.Add(withStats);
                return withStats;
            }
            if (!IsStatus(e)) return null;
            CombatEffect effect = e.Data;
            Status? known = list.FirstOrDefault(x => x.Effect == effect && x.OnMonster == onMonster);
            if (known != null)
            {
                if (e is ConfiguredRandomMagnitudeEffect again)
                {
                    known.MaxStacks = Math.Max(known.MaxStacks, again.Configuration.MaxStacks ?? again.Effect.DefaultMaxStacks);
                    known.Levels = known.MaxStacks + 1;
                }
                return known;
            }
            var made = new Status { Id = effect.Id, Name = effect.DisplayName.Text, OnMonster = onMonster, Bit = list.Count, Effect = effect };
            ConfiguredCombatEffect? then = null;
            switch (e)
            {
                case ConfiguredStunEffect s:
                    made.Stuns = true; made.DefaultTurns = s.Effect.DefaultTurns; made.Modifiers = s.Effect.Modifiers; then = s.Effect.RemovalEffect; break;
                case ConfiguredTurnCountingEffect c:
                    made.DefaultTurns = c.Effect.DefaultTurns; made.Modifiers = c.Effect.Modifiers; then = c.Effect.RemovalEffect; break;
                case ConfiguredStackingEffect k:
                    made.Kind = Status.Kinds.Stacking; made.Modifiers = k.Effect.Modifiers;
                    made.MaxStacks = Math.Max(1, k.Effect.MaxStacks); made.AddStacks = Math.Max(1, k.Effect.DefaultStacksToAdd);
                    made.Levels = made.MaxStacks + 1; break;
                case ConfiguredRandomMagnitudeEffect r:
                    made.Kind = Status.Kinds.Random; made.Modifiers = r.Effect.Modifiers; made.DefaultTurns = r.Effect.DefaultTurns;
                    made.MinStacks = Math.Max(0, r.Effect.DefaultMinStacks);
                    made.MaxStacks = Math.Max(1, r.Configuration.MaxStacks ?? r.Effect.DefaultMaxStacks);
                    made.DefaultMax = Math.Max(1, r.Effect.DefaultMaxStacks);
                    made.Levels = made.MaxStacks + 1; break;
                case ConfiguredEndOfTurnDamageEffect d:
                    made.Kind = Status.Kinds.EndDamage; made.Formula = d.Effect.DamagePerTurn.Formula;
                    made.OfSource = d.Effect.DamagePerTurn.Character == EffectCharacter.Source; made.Levels = 1; break;
                case ConfiguredEndOfTurnHealingEffect h:
                    made.Kind = Status.Kinds.EndHeal; made.Formula = h.Effect.HealingPerTurn.Formula;
                    made.OfSource = h.Effect.HealingPerTurn.Character == EffectCharacter.Source; made.Levels = 1; break;
            }
            made.DefaultTurns = Math.Max(1, made.DefaultTurns);
            list.Add(made);
            if (then != null && depth < 4)
            {
                made.Then = Of(then, onMonster, depth + 1);
                made.ThenTurns = then is ConfiguredStunEffect ts ? ts.Configuration.Turns : then is ConfiguredTurnCountingEffect tc ? tc.Configuration.Turns : null;
            }
            return made;
        }
        foreach (AttackChance chance in enemy.Attacks)
            foreach (AttackEffectChance e in chance.Attack.PreHitEffects.Concat(chance.Attack.OnHitEffects))
                Of(e.Effect, e.AppliesTo == RelativeCharacter.Self, 0);
        foreach (CombatPassive passive in enemy.Passives)
            foreach (CombatEffectModifierValue v in passive.Modifiers.CombatEffect)
                Of(v.Modifier.Effect, OnMonster(v), 0);
        if (area != null)
            foreach (CombatEffectModifierValue v in AreaTriggers(area))
                Of(v.Modifier.Effect, OnMonster(v), 0);
        // the adventurer's own attacks: what they put on the monster ("target") or on themselves
        if (mine != null)
            foreach (var (attack, _) in mine)
                foreach (AttackEffectChance e in attack.PreHitEffects.Concat(attack.OnHitEffects))
                    if ((e.Effect is not ConfiguredDamageOverTimeEffect || e.Effect is ConfiguredDamageOverTimeEffect { Effect.Modifiers: not null })
                        && MineEffect(e, out bool keep, out _) && keep)
                        Of(e.Effect, e.AppliesTo == RelativeCharacter.Target, 0, byMe: true);
        int stride = 1;
        foreach (Status s in list) { s.Stride = stride; stride = (int)Math.Min((long)stride * s.Levels, int.MaxValue / 4); }
        return list;
    }

    /// <summary>How many duel variants a monster's statuses need.</summary>
    public static long VariantCount(List<Status> statuses) => statuses.Aggregate(1L, (n, s) => Math.Min(n * s.Levels, long.MaxValue / 1024));

    /// <summary>
    /// Whether the model knows an effect landing on the adventurer (<paramref name="to"/> Target) or on the monster (Self);
    /// <paramref name="byAdventurer"/>: it is the adventurer's own trigger that puts it there.
    /// </summary>
    static bool KnownEffect(ConfiguredCombatEffect e, RelativeCharacter to, out string why, bool byAdventurer = false)
    {
        why = "";
        if (IsStatus(e)) return KnownStatus(e, to, 0, out why, byAdventurer);
        if (e is not ConfiguredDamageOverTimeEffect dot) { why = $"{e.Data.DisplayName.Text} (not yet)"; return false; }
        if (dot.Effect.Modifiers != null && !TemplateKnown(dot.Effect.Modifiers)) { why = $"{dot.Effect.DisplayName.Text} also changes stats (not yet)"; return false; }
        if (dot.Effect.ShouldInterruptTurn) { why = $"{dot.Effect.DisplayName.Text} interrupts turns (not yet)"; return false; }
        if (!KnownFormula(dot.Effect.DamagePerProc.Formula)) { why = $"{dot.Effect.DisplayName.Text}'s damage (not yet)"; return false; }
        if (to != RelativeCharacter.Target) { why = $"the monster strengthens itself with {dot.Effect.DisplayName.Text} (not yet)"; return false; }
        return true;
    }

    /// <summary>True when everything this monster does is known to the model; otherwise says what is not.</summary>
    public static bool Known(Enemy enemy, out string why)
    {
        why = "";
        foreach (AttackChance chance in enemy.Attacks)
        {
            Attack a = chance.Attack;
            foreach (AttackHit hit in a.Hits)
                foreach (AttackDamage part in hit.Damage)
                    if (!KnownFormula(part.Formula)) { why = $"{a.DisplayName.Text}: its damage (not yet)"; return false; }
            foreach (AttackEffectChance e in a.PreHitEffects.Concat(a.OnHitEffects))
                if (!KnownEffect(e.Effect, e.AppliesTo, out string w)) { why = $"{a.DisplayName.Text}: {w}"; return false; }
        }
        foreach (CombatPassive passive in enemy.Passives)
        {
            var m = passive.Modifiers;
            // what shields the monster (from effects, from a style) or hurts back: added to the stats as the game does
            if (!m.Component.All(PassiveKnown)) { why = $"{passive.DisplayName.Text} (not yet)"; return false; }
            if (m.Global.Count > 0) { why = $"{passive.DisplayName.Text} (not yet)"; return false; }
            foreach (CombatEffectModifierValue v in m.CombatEffect)
            {
                if (v.Condition != null || v.Scaling != null) { why = $"{passive.DisplayName.Text} (not yet)"; return false; }
                bool trigger = v.IsEnemy
                    ? v.Modifier.Trigger is OnAttackCombatEffectTrigger or WhenAttackedCombatEffectTrigger or AtStartOfFightCombatEffectTrigger or OnDeathCombatEffectTrigger
                    : v.Modifier.Trigger is OnAttackCombatEffectTrigger or WhenAttackedCombatEffectTrigger;
                if (!trigger) { why = $"{passive.DisplayName.Text} (not yet)"; return false; }
                if (!KnownEffect(v.Modifier.Effect, OnMonster(v) ? RelativeCharacter.Self : RelativeCharacter.Target, out string w, !v.IsEnemy)) { why = $"{passive.DisplayName.Text}: {w}"; return false; }
            }
        }
        var statuses = Statuses(enemy);
        if (VariantCount(statuses) > MostVariants) { why = "too many effects at once (not yet)"; return false; }
        return true;
    }

    /// <summary>
    /// Whether the model works out the adventurer's own special attacks (a weapon's, a special spell's). Off for the
    /// Adventurers mod (its adventurers cannot use those yet); the Combat Advisor turns it on for the player.
    /// </summary>
    public static bool WeaponSpecials;

    /// <summary>
    /// The plain blow too is played hit by hit (as a weapon's special attacks are) when a fight is played out blow by
    /// blow: the game's own steps for every hit (hit roll, then negation, then damage) instead of the averaged tables.
    /// </summary>
    public static bool HitByHit;

    /// <summary>
    /// The adventurer's attacks as the game rolls them each turn (PlayerComponent.GetAvailableAttacks: a special
    /// spell's attack alone, else every special attack the gear brings; CharacterComponent.NormalizeAttackChances:
    /// the plain blow gets what they leave, or they are scaled to one). Empty when there is only the plain blow.
    /// </summary>
    public static List<(Attack Attack, double Chance)> MyAttacks(IEnumerable<GamesByMalcs.Melvor2.Data.Items.Item> gear, GamesByMalcs.Melvor2.Components.MagicSpells.MagicSpell? spell, Attack plain)
    {
        var list = new List<AttackChance>();
        if (spell?.AttackOverride != null) list.Add(new AttackChance(spell.AttackOverride, 1.0));
        else
            foreach (var item in gear.Distinct())
                if (item.Capabilities.TryGet(out GamesByMalcs.Melvor2.Components.Equipment.EquipmentCapability? e) && e.Attacks != null) list.AddRange(e.Attacks);
        var result = new List<(Attack, double)>();
        if (list.Count == 0)
        {
            if (HitByHit) result.Add((plain, 1.0));
            return result;
        }
        double total = list.Sum(a => a.Chance);
        if (total < 1) result.Add((plain, 1 - total));
        foreach (var g in list.GroupBy(a => a.Attack)) result.Add((g.Key, total < 1 ? g.Sum(a => a.Chance) : g.Sum(a => a.Chance) / total));
        return result;
    }

    /// <summary>
    /// One effect of the adventurer's own attack: true when the model can take it as it is (<paramref name="keep"/>),
    /// or can leave it out because it only helps the adventurer (a debuff on the monster, a boost of their own: the
    /// real fight is then a little better than the forecast). False when it could hurt them and is not known.
    /// </summary>
    public static bool MineEffect(AttackEffectChance e, out bool keep, out string why)
    {
        keep = false;
        bool onMonster = e.AppliesTo == RelativeCharacter.Target;
        // (seen from the monster's side: what lands on the monster is its "self", what lands on the adventurer its "target")
        if (KnownEffect(e.Effect, onMonster ? RelativeCharacter.Self : RelativeCharacter.Target, out why, byAdventurer: true)) { keep = true; return true; }
        // damage over time on the monster: known when its damage is, and what it changes in the monster's stats
        if (onMonster && e.Effect is ConfiguredDamageOverTimeEffect dot && (dot.Effect.Modifiers == null || TemplateKnown(dot.Effect.Modifiers))
            && !dot.Effect.ShouldInterruptTurn && KnownFormula(dot.Effect.DamagePerProc.Formula)) { keep = true; why = ""; return true; }
        return onMonster ? !e.Effect.Data.IsPositive : e.Effect.Data.IsPositive;
    }

    /// <summary>True when the model knows the adventurer's own attacks; what it leaves out (only helps them) goes in
    /// <paramref name="leftOut"/>, by name.</summary>
    public static bool KnownMine(IEnumerable<(Attack Attack, double Chance)> mine, out string why, List<string> leftOut)
    {
        why = "";
        foreach (var (a, _) in mine)
        {
            if (a.Hits.Count == 0) { why = $"{a.DisplayName.Text} (not yet)"; return false; }
            foreach (AttackHit hit in a.Hits)
                foreach (AttackDamage part in hit.Damage)
                    if (!KnownFormula(part.Formula)) { why = $"{a.DisplayName.Text}: its damage (not yet)"; return false; }
            foreach (AttackEffectChance e in a.PreHitEffects.Concat(a.OnHitEffects))
            {
                if (!MineEffect(e, out bool keep, out _)) { why = $"{a.DisplayName.Text}: {e.Effect.Data.DisplayName.Text} (not yet)"; return false; }
                if (!keep && !leftOut.Contains(e.Effect.Data.DisplayName.Text)) leftOut.Add(e.Effect.Data.DisplayName.Text);
            }
        }
        return true;
    }

    /// <summary>The trigger entries an area brings: its effect's and its passives'.</summary>
    public static IEnumerable<CombatEffectModifierValue> AreaTriggers(BaseCombatArea area) =>
        (area.Effect?.Modifiers.CombatEffect ?? Enumerable.Empty<CombatEffectModifierValue>())
            .Concat(area.Passives.SelectMany(p => p.Modifiers.CombatEffect));

    /// <summary>Whether an entry's effect lands on the monster: the monster's own "self", or the adventurer's "target".</summary>
    public static bool OnMonster(CombatEffectModifierValue v) => v.IsEnemy ? v.Modifier.AppliesTo == RelativeCharacter.Self : v.Modifier.AppliesTo == RelativeCharacter.Target;

    static readonly HashSet<string> PassiveStats = new() { "CombatEffect:AvoidanceChance", "Target:ImmunityFrom", "Character:PercentAttackReflect" };
    static bool PassiveKnown(ComponentModifierValue v) =>
        v.Condition == null && v.Scaling == null && v.FromContext == null && PassiveStats.Contains(v.Stat.StatsClassId.Replace("MelvorBase:", "") + ":" + v.Stat.Name);

    // what an area may change: plain stats the game's stat objects work out, food's healing, what the monster is immune to
    static readonly HashSet<string> AreaStats = new()
    {
        "Character:PercentAccuracyRating", "Character:PercentEvasionRating", "Character:FlatEvasionRating", "Character:PercentProtectionRating",
        "Character:FlatProtectionRating", "Character:PercentAttackInterval", "Character:PercentMaxHit", "Character:PercentDamageTaken",
        "Character:PercentDamageDealt", "Character:FlatPowerRating", "CombatFood:PercentHealing", "CombatEffect:EffectLifesteal",
        "CombatEffect:AvoidanceChance", "CombatEffect:PercentDamageTaken", "Target:ImmunityFrom",
    };

    /// <summary>
    /// True when everything an area (or dungeon) does is known to the model: its darkness (the game's own stats work it
    /// out), its effect (stat changes, triggers for the monster or for the adventurer) and its passives.
    /// </summary>
    public static bool KnownArea(BaseCombatArea area, out string why)
    {
        why = "";
        string name = area.DisplayName.Text;
        if (area.Effect != null)
        {
            ModifierValues m = area.Effect.Modifiers;
            if (m.Global.Count > 0) { why = $"{name}'s effect (not yet)"; return false; }
            foreach (ComponentModifierValue v in m.Component)
                if (v.Condition != null || v.Scaling != null || v.FromContext != null || !AreaStats.Contains(v.Stat.StatsClassId.Replace("MelvorBase:", "") + ":" + v.Stat.Name))
                { why = $"{name}'s effect (not yet)"; return false; }
        }
        foreach (CombatPassive passive in area.Passives)
        {
            var m = passive.Modifiers;
            if (!m.Component.All(PassiveKnown) || m.Global.Count > 0) { why = $"{passive.DisplayName.Text} (not yet)"; return false; }
        }
        foreach (CombatEffectModifierValue v in AreaTriggers(area))
        {
            if (v.Condition != null || v.Scaling != null) { why = $"{name}'s effect (not yet)"; return false; }
            bool known = v.IsEnemy
                ? v.Modifier.Trigger is OnAttackCombatEffectTrigger or WhenAttackedCombatEffectTrigger or AtStartOfFightCombatEffectTrigger or OnDeathCombatEffectTrigger
                : v.Modifier.Trigger is OnAttackCombatEffectTrigger or WhenAttackedCombatEffectTrigger;
            if (!known) { why = $"{name}'s effect (not yet)"; return false; }
            // what lands on the adventurer is as if the monster put it there; the adventurer applies nothing to the monster
            if (!KnownEffect(v.Modifier.Effect, OnMonster(v) ? RelativeCharacter.Self : RelativeCharacter.Target, out string w, !v.IsEnemy)) { why = $"{name}: {w}"; return false; }
        }
        return true;
    }

    /// <summary>True when the monster has anything beyond a plain attack.</summary>
    public static bool Any(Enemy enemy) => enemy.Attacks.Count > 0 || enemy.Passives.Any(p => p.Modifiers.CombatEffect.Count > 0)
        || enemy.Stats.Stats.Any(v => v.Value > 0 && (v.Stat.Id.EndsWith(":MaximumWard") || v.Stat.Id.EndsWith(":Lifesteal")));

    public static string TriggerName(CombatEffectTrigger t) => t switch
    {
        OnAttackCombatEffectTrigger a => a.Type.ToString(),
        WhenAttackedCombatEffectTrigger w => w.Type.ToString(),
        AtStartOfFightCombatEffectTrigger => "AtStartOfFight",
        OnDeathCombatEffectTrigger => "OnDeath",
        _ => "",
    };

    /// <summary>What a trigger of the monster's (on its own attacks) asks of the hit just done.</summary>
    public static bool OnAttackFires(string on, bool hit, int attacksThisTurn, int hitsThisTurn, int missesThisTurn) => on switch
    {
        "OnAttack" => true,
        "OnFirstAttack" => attacksThisTurn == 1,
        "OnHit" => hit,
        "OnFirstHit" => hit && hitsThisTurn == 1,
        "OnMiss" => !hit,
        "OnFirstMiss" => !hit && missesThisTurn == 1,
        "OnFirstAttackHit" => hit && attacksThisTurn == 1,
        "OnFirstAttackMiss" => !hit && attacksThisTurn == 1,
        _ => false,
    };

    /// <summary>What a trigger of the monster's (on the adventurer's attacks on it) asks.</summary>
    public static bool WhenAttackedFires(string on, bool hit, int attacksThisTurn, int hitsThisTurn, int missesThisTurn) => on switch
    {
        "WhenAttacked" => true,
        "WhenFirstAttacked" => attacksThisTurn == 1,
        "WhenHit" => hit,
        "WhenFirstHit" => hit && hitsThisTurn == 1,
        "WhenAttackEvaded" => !hit,
        "WhenFirstAttackEvaded" => !hit && missesThisTurn == 1,
        _ => false,
    };

    /// <summary>A formula's value for a character: "roll" picks between its minimum and maximum.</summary>
    public static int Value(DamageFormula f, int minHit, int maxHit, int currentHp, int lastDealt, Func<int, int, int> roll) => f switch
    {
        NormalDamageFormula n => roll((int)(minHit * n.Percent), (int)(maxHit * n.Percent)),
        MaxHitDamageFormula m => (int)(maxHit * m.Percent),
        CurrentHitpointsDamageFormula c => (int)(currentHp * c.Percent),
        DamageDealtDamageFormula d => (int)(lastDealt * d.Percent),
        FixedDamageFormula x => x.Damage,
        _ => 0,
    };

    /// <summary>The most a formula can come to (for the safety rule), with a character's hitpoints at their maximum.</summary>
    public static int Most(DamageFormula f, int maxHit, int maxHp) => f switch
    {
        NormalDamageFormula n => (int)(maxHit * n.Percent),
        MaxHitDamageFormula m => (int)(maxHit * m.Percent),
        CurrentHitpointsDamageFormula c => (int)(maxHp * c.Percent),
        DamageDealtDamageFormula d => (int)(maxHit * d.Percent * 2),        // (a blow's damage, a critical hit at most)
        FixedDamageFormula x => x.Damage,
        _ => int.MaxValue / 4,
    };
}
