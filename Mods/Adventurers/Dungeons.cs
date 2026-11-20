using System;
using System.Collections.Generic;
using System.Linq;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Components.CombatAreas;
using GamesByMalcs.Melvor2.Components.CombatLevels;
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.Entities.Combat;
using GamesByMalcs.Melvor2.Random;

namespace Adventurers;

/// <summary>A dungeon worked out for one adventurer: every monster it may send, in order, as fights.</summary>
public sealed class DungeonPlan
{
    public Dungeon Dungeon = null!;
    public bool CanGo;
    /// <summary>Refused only because most runs alone would fail: with a party it may still go.</summary>
    public bool RiskyAlone;
    public string Why = "";
    /// <summary>The places in order; each holds the monsters that may come there (by weight) and how many come.</summary>
    public List<(List<(double Weight, Fight Fight)> Options, int Count)> Waves = new();
    public int MaxHp, EatAt, Heal;
    public double FoodUse = 1;
    public int Monsters => Waves.Sum(w => w.Count);
    /// <summary>True when no monster in it can take them from above their eating point to nothing: no defeat possible.</summary>
    public bool Safe;

    // what the forecast found (a few thousand runs played with fixed seeds)
    public double DefeatChance, SecondsPerRun, FoodPerRun, ClearsPerHour, AmmoPerRun;
    public Dictionary<Item, double> RunesPerRun = new();
    public Dictionary<CombatLevel, double> XpPerRun = new();
    public string Deadliest = "";
    public int ForecastRuns;
}

/// <summary>What a party's forecast found: a run of them all, how often everyone or someone falls, and each one's share.</summary>
public sealed class PartyForecast
{
    public bool CanGo;
    public string Why = "";
    public List<DungeonPlan> Plans = new();
    public double WipeChance, KnockChance, SecondsPerRun, ClearsPerHour;
    public double[] FoodPerRun = Array.Empty<double>(), AmmoPerRun = Array.Empty<double>(), KnockedOut = Array.Empty<double>();
    public Dictionary<Item, double>[] RunesPerRun = Array.Empty<Dictionary<Item, double>>();
    public Dictionary<CombatLevel, double>[] XpPerRun = Array.Empty<Dictionary<CombatLevel, double>>();
    public string Deadliest = "";
    public int Runs;
}

/// <summary>What one run gave: how it ended, how long it took, what it used, the XP, the hitpoints left.</summary>
public sealed class RunResult
{
    public bool Cleared, Defeated, Withdrawn;
    public bool KnockedOut;                                    // (in a party: this one fell, the others may have fought on)
    public string Why = "", KilledBy = "";
    public double Seconds;
    public int EndHp, Food, Ammo, Kills;
    public long DamageTaken, Attacks;
    public long Landed, Dealt, TheirAttacks, TheirLanded;     // (for the tests)
    /// <summary>Effects still on the adventurer when the run ended (they go on into the next run).</summary>
    public List<CarriedEffect> Effects = new();
    public Dictionary<Item, int> Runes = new();
    public Dictionary<CombatLevel, double> Xp = new();
    /// <summary>How often each status went on (by its id), for the tests.</summary>
    public Dictionary<string, int> Given = new();
}

/// <summary>An effect on the adventurer kept from one run to the next (in adventurers.json): ticks are counted from the run's end.</summary>
public sealed class CarriedEffect
{
    public string Dot { get; set; } = "";
    public string From { get; set; } = "";
    public int Stacks { get; set; }
    public int PerProc { get; set; }
    public int Done { get; set; }
    public long Next { get; set; }
}

/// <summary>What there is to spend on a run: food, ammunition and runes, as whole numbers in the bank.</summary>
public sealed class Supplies
{
    public long Food, Ammo;
    public Dictionary<Item, long> Runes = new();
    public static Supplies Plenty => new() { Food = long.MaxValue / 4, Ammo = long.MaxValue / 4 };
    public long RunesOf(Item rune) => Runes.TryGetValue(rune, out long n) ? n : long.MaxValue / 4;
}

/// <summary>
/// Dungeons for adventurers. A run is played out blow by blow with the game's own rules and numbers (see
/// <see cref="Duel"/>): both attack timers start when a monster appears, three seconds after the last one died; when
/// both attack at the same moment the adventurer strikes first and the monster still strikes back (the game ticks the
/// player, then the enemy, then handles deaths); hitpoints carry over from monster to monster and from run to run;
/// they eat as the game's auto-eat does (at the threshold, as many servings as fit, never above the maximum). Every
/// run has its own seed, so a run always ends the same way however the time is worked out.
/// </summary>
public static class Dungeons
{
    /// <summary>The rest after a knock-out (the lodge's infirmary shortens it).</summary>
    public static double RecoveryHours => Lodge.RecoveryHours;
    public static string RecoveryText => RecoveryHours >= 2 ? $"{RecoveryHours:0} hours" : $"{RecoveryHours * 60:0} minutes";
    public const int RunsPerPack = 5;
    public static readonly long[] ClearTiers = { 0, 10, 50, 250 };
    static readonly string[] ClearWord = { "", "Raider", "Conqueror", "Vanquisher" };

    public static int Tier(long clears) => clears >= 250 ? 3 : clears >= 50 ? 2 : clears >= 10 ? 1 : 0;
    public static string Title(Dungeon d, int tier) => tier <= 0 ? "" : $"{d.DisplayName.Text} {ClearWord[Math.Min(3, tier)]}";

    /// <summary>The dungeons of the world map, in the game's order (no quest dungeons).</summary>
    public static List<Dungeon> All(Game game) =>
        game.Data.GetDataRegistryOutput<Dungeon>().GetAllObjects().Where(d => d.Category.Id != "MelvorBase:Quests" && !d.Id.Contains(":Quest")).ToList();

    public static bool Unlocked(Dungeon d) => d.UnlockRequirements.AreMet && d.DisplayRequirements.AreMet;

    /// <summary>Why adventurers cannot go to this dungeon yet (a monster or the place has what the model does not know), or "".</summary>
    public static string NotYet(Dungeon d)
    {
        if (!Specials.KnownArea(d, out string place)) return $"Not yet: {place}.";
        foreach (DungeonEnemies wave in d.Enemies)
            foreach (var w in wave.Enemies)
                if (!Combat.Supported(d, w.Enemy, out string why)) return $"{w.Enemy.DisplayName.Text}: {why}";
        return "";
    }

    /// <summary>Works the dungeon out for a fighter: every monster that may come, each as a fight (risky ones allowed).</summary>
    public static DungeonPlan Plan(Game game, Fighter who, Dungeon dungeon, GamesByMalcs.Melvor2.Modifiers.ModifierSystem? useGameModifiers = null)
    {
        var plan = new DungeonPlan { Dungeon = dungeon, FoodUse = who.FoodUse };
        string notYet = NotYet(dungeon);
        if (notYet != "") { plan.Why = notYet; return plan; }
        var known = new Dictionary<Enemy, Fight>();
        foreach (DungeonEnemies wave in dungeon.Enemies)
        {
            var options = new List<(double, Fight)>();
            double total = wave.Enemies.Sum(e => (double)e.Weight);
            if (total <= 0) { plan.Why = "This dungeon has a place the model does not know."; return plan; }
            foreach (var w in wave.Enemies)
            {
                if (!known.TryGetValue(w.Enemy, out Fight? f)) known[w.Enemy] = f = Combat.Evaluate(game, who, dungeon, w.Enemy, useGameModifiers, allowRisk: true, forDungeon: true);
                if (!f.CanFight) { plan.Why = $"{w.Enemy.DisplayName.Text}: {f.Why}"; return plan; }
                options.Add((w.Weight / total, f));
            }
            plan.Waves.Add((options, wave.Count));
        }
        if (known.Count == 0) { plan.Why = "This dungeon has no monsters."; return plan; }
        Fight any = known.Values.First();
        plan.MaxHp = any.MaxHp;
        plan.EatAt = any.EatAt;
        plan.Heal = any.Heal;
        plan.Safe = known.Values.All(f => f.Safe);
        // a damage over time from one monster may tick while another is fought, with that fight's reduction
        var allDuels = known.Values.SelectMany(f => f.Duels).SelectMany(d => d.Duel.Variants ?? new[] { d.Duel }).ToList();
        double worst = allDuels.Select(d => d.EffectScale).DefaultIfEmpty(0).Max();
        foreach (Dot dot in allDuels.SelectMany(d => d.Dots.Values).Where(x => !x.BySelf && x.PlainScale > 0))
            if (dot.MostPer * dot.MaxStacks * Math.Max(worst * dot.Scale / dot.PlainScale, dot.GapScale) >= any.EatAt) { plan.Safe = false; break; }
        if (!plan.Safe && plan.Heal <= 0) { plan.Why = "Monsters here hit back: the adventurer needs food."; return plan; }
        plan.CanGo = true;
        return plan;
    }

    /// <summary>
    /// The forecast the page shows and sending checks (a dungeon where most runs fail is refused). It is worked out
    /// once per plan, when first asked for; time passing never needs it (levels only rise, so risk only falls).
    /// </summary>
    public static void EnsureForecast(DungeonPlan plan)
    {
        if (!plan.CanGo || plan.ForecastRuns > 0) return;
        Forecast(plan);
        if (plan.DefeatChance > 0.5) { plan.CanGo = false; plan.RiskyAlone = true; plan.Why = $"Too dangerous: about {Odds(plan.DefeatChance)} runs would fail (most often at {plan.Deadliest}). More Defence, Hitpoints or armour is needed."; }
    }

    /// <summary>
    /// A party's forecast: runs one after the other with fixed seeds, each with the hitpoints and effects the last one
    /// left; one knocked out rests (the party waits for them), everyone knocked out is a defeat. Refused when most
    /// runs would end with everyone down.
    /// </summary>
    public static PartyForecast ForecastParty(List<DungeonPlan> plans)
    {
        var pf = new PartyForecast { Plans = plans };
        if (plans.FirstOrDefault(p => !p.CanGo && !p.RiskyAlone) is { } no) { pf.Why = no.Why; return pf; }
        int n = plans.Count;
        const int runs = 1000;
        var hp = plans.Select(p => p.MaxHp).ToArray();
        var carried = new List<CarriedEffect>?[n];
        pf.FoodPerRun = new double[n]; pf.AmmoPerRun = new double[n]; pf.KnockedOut = new double[n];
        pf.RunesPerRun = Enumerable.Range(0, n).Select(_ => new Dictionary<Item, double>()).ToArray();
        pf.XpPerRun = Enumerable.Range(0, n).Select(_ => new Dictionary<CombatLevel, double>()).ToArray();
        double seconds = 0, rest = 0;
        int wipes = 0, knocks = 0, clears = 0;
        var killers = new Dictionary<string, int>();
        for (int i = 0; i < runs; i++)
        {
            var members = plans.Select((p, k) => new Member { Plan = p, Hp = hp[k], Have = Supplies.Plenty, Carried = carried[k] }).ToList();
            var got = PlayParty(members, new Xoshiro256StarStarRandom(Loot.Mix(0xD06E0FUL + (ulong)i * 0x9E3779B97F4A7C15UL)));
            RunResult first = got[0];
            if (first.Withdrawn)
            {
                pf.Runs = i + 1; pf.WipeChance = 1;
                pf.Deadliest = first.Why.StartsWith("They cannot beat ") ? first.Why["They cannot beat ".Length..].TrimEnd('.') : first.Why;
                pf.Why = $"The party cannot beat {pf.Deadliest}.";
                return pf;
            }
            seconds += first.Seconds;
            bool anyDown = false;
            for (int k = 0; k < n; k++)
            {
                RunResult r = got[k];
                pf.FoodPerRun[k] += r.Food; pf.AmmoPerRun[k] += r.Ammo;
                foreach (var (rune, x) in r.Runes) pf.RunesPerRun[k][rune] = pf.RunesPerRun[k].GetValueOrDefault(rune) + x;
                foreach (var (level, x) in r.Xp) pf.XpPerRun[k][level] = pf.XpPerRun[k].GetValueOrDefault(level) + x;
                if (r.KnockedOut) { pf.KnockedOut[k]++; anyDown = true; if (r.KilledBy != "") killers[r.KilledBy] = killers.GetValueOrDefault(r.KilledBy) + 1; }
                hp[k] = r.KnockedOut || r.Defeated ? plans[k].MaxHp : r.EndHp;
                carried[k] = r.Cleared ? r.Effects : null;
            }
            if (first.Defeated) wipes++;
            else clears++;
            if (anyDown) { knocks++; rest += RecoveryHours * 3600; }
        }
        pf.Runs = runs;
        pf.WipeChance = (double)wipes / runs;
        pf.KnockChance = (double)knocks / runs;
        pf.SecondsPerRun = seconds / runs;
        for (int k = 0; k < n; k++)
        {
            pf.FoodPerRun[k] /= runs; pf.AmmoPerRun[k] /= runs; pf.KnockedOut[k] /= runs;
            pf.RunesPerRun[k] = pf.RunesPerRun[k].ToDictionary(kv => kv.Key, kv => kv.Value / runs);
            pf.XpPerRun[k] = pf.XpPerRun[k].ToDictionary(kv => kv.Key, kv => kv.Value / runs);
        }
        pf.Deadliest = killers.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).FirstOrDefault() ?? "";
        pf.ClearsPerHour = 3600.0 * clears / Math.Max(1, seconds + rest);
        pf.CanGo = pf.WipeChance <= 0.5;
        if (!pf.CanGo) pf.Why = $"Too dangerous: about {Odds(pf.WipeChance)} runs would end with everyone knocked out (most often by {pf.Deadliest}).";
        return pf;
    }

    /// <summary>"1 in 20", "1 in 3"...</summary>
    public static string Odds(double p) => p <= 0 ? "none" : p >= 0.4 ? $"{p * 100:0}% of" : $"1 in {Math.Round(1 / p):0}";

    /// <summary>
    /// The forecast: a few thousand runs one after the other with fixed seeds (hitpoints carried over, a defeat
    /// followed by recovery at full health), plenty of supplies. What the page shows; not what happens.
    /// </summary>
    static void Forecast(DungeonPlan plan)
    {
        const int runs = 3000;
        int hp = plan.MaxHp, defeats = 0;
        double seconds = 0, food = 0, ammo = 0;
        var runes = new Dictionary<Item, double>();
        var xp = new Dictionary<CombatLevel, double>();
        var killers = new Dictionary<string, int>();
        List<CarriedEffect>? carried = null;
        for (int i = 0; i < runs; i++)
        {
            RunResult r = Play(plan, hp, Supplies.Plenty, new Xoshiro256StarStarRandom(Loot.Mix(0xD06E0FUL + (ulong)i * 0x9E3779B97F4A7C15UL)), carried);
            carried = r.Cleared ? r.Effects : null;
            seconds += r.Seconds;
            food += r.Food;
            ammo += r.Ammo;
            foreach (var (rune, n) in r.Runes) runes[rune] = runes.GetValueOrDefault(rune) + n;
            foreach (var (level, x) in r.Xp) xp[level] = xp.GetValueOrDefault(level) + x;
            if (r.Withdrawn)
            {
                plan.ForecastRuns = runs;
                plan.DefeatChance = 1;
                plan.Deadliest = r.Why.StartsWith("They cannot beat ") ? r.Why["They cannot beat ".Length..].TrimEnd('.') : r.Why;
                return;
            }
            if (r.Defeated) { defeats++; killers[r.KilledBy] = killers.GetValueOrDefault(r.KilledBy) + 1; hp = plan.MaxHp; }
            else hp = r.EndHp;
        }
        plan.ForecastRuns = runs;
        plan.DefeatChance = (double)defeats / runs;
        plan.SecondsPerRun = seconds / runs;
        plan.FoodPerRun = food / runs * 1.0;
        plan.AmmoPerRun = ammo / runs;
        plan.RunesPerRun = runes.ToDictionary(kv => kv.Key, kv => kv.Value / runs);
        plan.XpPerRun = xp.ToDictionary(kv => kv.Key, kv => kv.Value / runs);
        plan.Deadliest = killers.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).FirstOrDefault() ?? "";
        double p = plan.DefeatChance;
        plan.ClearsPerHour = 3600 * (1 - p) / (plan.SecondsPerRun + p * RecoveryHours * 3600);
    }

    /// <summary>An effect on an adventurer while it lasts (ActiveDamageOverTimeEffect).</summary>
    sealed class Active
    {
        public Dot Dot = null!;
        public string From = "";
        public int Stacks, PerProc, Done;
        public long Next;
        public int Owner = -1;                   // on the monster: the member whose attack put it there
    }

    /// <summary>A status while it lasts (ActiveStunEffect, ActiveTurnCountingEffect...).</summary>
    sealed class On
    {
        public Status S = null!;
        public int Turns;
        public int Level;                        // its level in the variants (stacks for stacking and random ones)
        public bool Skip;                        // put on during its holder's own turn: that turn's end does not count
    }

    /// <summary>
    /// What all who fight together share: the monster (its hitpoints, ward, armour, attack, turn, statuses) and the
    /// run's dice. One adventurer alone is a party of one, and plays exactly as before.
    /// </summary>
    sealed class Shared
    {
        public IRandom Rng = null!;
        public bool Between;                     // in the three seconds between two monsters
        public List<State> Members = new();
        public List<On> MonsterStatuses = new(); // on the monster (they count its turns)
        public int TheirStunned;
        public List<Active> MonsterEffects = new(); // damage over time the adventurers' attacks put on the monster
        public int StrikeAt = -1;                // the attack the monster is making (by its place), or none
        public bool TheirTurnOver;               // a stun ended the monster's turn inside its attack
        public int EnemyHp, Ward;
        public bool Dying;                       // its hitpoints reached nothing this tick (no healing saves it: IsDying)
        public bool Broken;                      // its armour broke
        public long Theirs;                      // the next tick the monster acts
        public int Met;                          // monsters met this run (who strikes first on a shared tick turns with it)
        /// <summary>The one the monster attacks: the first in the formation still standing.</summary>
        public State? Front => Members.FirstOrDefault(m => !m.Out);
    }

    /// <summary>One adventurer in the run: what they carry from monster to monster, and their side of the fight.</summary>
    sealed class State
    {
        public Shared F = null!;
        public int Index;
        public bool Out;                         // knocked out: out of this run
        public DungeonPlan Plan = null!;
        public RunResult Run = null!;
        public Supplies Have = null!;
        public long Food, Ammo;
        public Dictionary<Item, long> RunesLeft = new();
        public List<Active> Effects = new();
        public Duel? Base, Cur;                  // their fight with this monster, and the variant for what is on now
        public List<On> Statuses = new();        // on them
        public int Stunned;                      // their stunned turns left (0: they attack)
        public bool TurnOver;                    // a stun ended their turn inside their attack
        public long Mine;                        // the next tick they act
        public long Turns;                       // turns this monster (a fight that never ends is given up)
        public long DealtHere;                   // damage done to this monster (for the kill's bonus XP)
        // their own special attack in progress (Duel.MyStrikes): which, the hit group and the hit in it, this turn's counts
        public int MyStrikeAt = -1, MyGroup, MyInGroup, MyAttacks, MyHits, MyMisses, MyLastDealt;
        public bool MyFromTurn;

        // the shared fight, read through each member as the one-adventurer engine always did
        public IRandom Rng => F.Rng;
        public bool Between => F.Between;
        public int EnemyHp { get => F.EnemyHp; set => F.EnemyHp = value; }
        public int Ward { get => F.Ward; set => F.Ward = value; }
        public bool Dying { get => F.Dying; set => F.Dying = value; }
        public int StrikeAt { get => F.StrikeAt; set => F.StrikeAt = value; }
        public int TheirStunned { get => F.TheirStunned; set => F.TheirStunned = value; }
        public bool TheirTurnOver { get => F.TheirTurnOver; set => F.TheirTurnOver = value; }
        public long Theirs { get => F.Theirs; set => F.Theirs = value; }
    }

    /// <summary>One adventurer of a party: their plan of the dungeon, hitpoints, supplies and what the last run left on them.</summary>
    public sealed class Member
    {
        public DungeonPlan Plan = null!;
        public int Hp;
        public Supplies Have = null!;
        public IEnumerable<CarriedEffect>? Carried;
    }

    const long SpawnTicks = (long)(Combat.SpawnSeconds * Duel.TicksPerSecond);
    const int MostAttacks = 20_000;

    /// <summary>
    /// Plays one run from <paramref name="hp"/> hitpoints with what there is to spend, tick by tick as the game does
    /// (CombatEntity.Tick: effects first, then the adventurer, then the monster, then deaths). A run that would need
    /// more food, ammunition or runes than there are ends there: they withdraw (no reward, no injury).
    /// </summary>
    public static RunResult Play(DungeonPlan plan, int hp, Supplies have, IRandom rng, IEnumerable<CarriedEffect>? carried = null) =>
        PlayParty(new List<Member> { new() { Plan = plan, Hp = hp, Have = have, Carried = carried } }, rng)[0];

    /// <summary>
    /// A run of a party (two or three adventurers fighting together; one alone is a party of one): each attacks on
    /// their own turn with their own numbers, the monster attacks the first in the formation still standing. One
    /// knocked out is out of the run; all knocked out is a defeat. One out of supplies: all withdraw. Every
    /// member's result carries how the run ended; <see cref="RunResult.KnockedOut"/> says who fell.
    /// </summary>
    public static List<RunResult> PlayParty(List<Member> members, IRandom rng)
    {
        var f = new Shared { Rng = rng };
        foreach (Member m in members)
        {
            var st = new State { F = f, Index = f.Members.Count, Plan = m.Plan, Run = new RunResult { EndHp = m.Hp }, Have = m.Have, Food = m.Have.Food, Ammo = m.Have.Ammo };
            if (m.Carried != null)
            {
                // the effects the last run left on them, if this dungeon's monsters still know them
                var duelsHere = m.Plan.Waves.SelectMany(w => w.Options).SelectMany(o => o.Fight.Duels).Select(d => d.Duel).ToList();
                var dots = duelsHere.SelectMany(d => d.Dots.Values).GroupBy(d => d.Key).ToDictionary(g => g.Key, g => g.First());
                foreach (CarriedEffect c in m.Carried)
                    if ((duelsHere.FirstOrDefault(d => d.Enemy == c.From && d.Dots.ContainsKey(c.Dot))?.Dots[c.Dot] ?? dots.GetValueOrDefault(c.Dot)) is { } dot)
                        st.Effects.Add(new Active { Dot = dot, From = c.From, Stacks = c.Stacks, PerProc = c.PerProc, Done = c.Done, Next = Math.Max(1, c.Next) });
            }
            f.Members.Add(st);
        }
        DungeonPlan lead = members[0].Plan;
        long t = 0;
        for (int w = 0; w < lead.Waves.Count; w++)
            for (int c = 0; c < lead.Waves[w].Count; c++)
            {
                // which monster comes, and in which stance (CombatAreasComponent.MoveNextEnemy, EnemyComponent): the
                // same for everyone (each sees it with their own numbers)
                int option = PickIndex(lead.Waves[w].Options, rng);
                int stance = PickIndex(lead.Waves[w].Options[option].Fight.Duels, rng);
                var duels = f.Members.Select(m => m.Plan.Waves[w].Options[option].Fight.Duels[stance].Duel).ToList();
                if (!Encounter(f, duels, ref t)) return Finish(f, t, false);
            }
        return Finish(f, t, true);
    }

    static List<RunResult> Finish(Shared f, long t, bool cleared)
    {
        var withdrawn = f.Members.FirstOrDefault(m => m.Run.Withdrawn);
        bool defeated = f.Members.All(m => m.Out);
        foreach (State m in f.Members)
        {
            RunResult run = m.Run;
            run.Seconds = t / Duel.TicksPerSecond;
            run.Cleared = cleared && !defeated && withdrawn == null;
            run.Defeated = defeated;
            if (withdrawn != null && !run.Withdrawn) { run.Withdrawn = true; run.Why = withdrawn.Run.Why; }
            if (defeated && run.KilledBy == "") run.KilledBy = f.Members.Select(x => x.Run.KilledBy).FirstOrDefault(k => k != "") ?? "";
            long end = t;
            run.Effects = run.Cleared && !m.Out ? m.Effects.Select(a => new CarriedEffect { Dot = a.Dot.Key, From = a.From, Stacks = a.Stacks, PerProc = a.PerProc, Done = a.Done, Next = a.Next - end }).ToList()
                                              : new List<CarriedEffect>();
        }
        return f.Members.Select(m => m.Run).ToList();
    }

    static IEnumerable<State> Standing(Shared f) => f.Members.Where(m => !m.Out);

    /// <summary>One monster, from the three seconds before it appears to its death. False when the run ends.</summary>
    static bool Encounter(Shared f, List<Duel> duels, ref long t)
    {
        IRandom rng = f.Rng;
        long spawn = t + SpawnTicks;
        // they eat before going on when they are at or below their eating point (the game would wait for a hit)
        foreach (State m in Standing(f).ToList())
            if (m.Run.EndHp <= m.Plan.EatAt && !Eat(m, t)) return false;
        // effects on them keep ticking while the next monster comes
        f.Between = true;
        foreach (State m in f.Members) m.Base = m.Cur = null;
        while (true)
        {
            long at = Standing(f).SelectMany(m => m.Effects).Select(e => e.Next).DefaultIfEmpty(long.MaxValue).Min();
            if (at > spawn) break;
            foreach (State m in Standing(f).ToList())
                if (m.Effects.Any(e => e.Next == at) && !Procs(m, at, out t)) return false;
            t = at;
        }
        f.Between = false;
        t = spawn;
        State front = f.Front!;
        Duel lead = duels[front.Index];
        // when they all strike on the same tick, who goes first turns with every monster: nobody takes every last blow
        int first = f.Met++ % f.Members.Count;
        f.MonsterStatuses.Clear();
        foreach (State m in f.Members)
        {
            m.Base = m.Cur = duels[m.Index];
            m.Statuses.Clear();
            Remake(m);
            m.Stunned = 0;
            m.Turns = 0;
            m.DealtHere = 0;
            m.MyStrikeAt = -1;
            m.MyLastDealt = 0;
        }
        f.MonsterEffects.Clear();
        f.EnemyHp = lead.EnemyHp;
        f.Ward = lead.EnemyWard;
        f.Dying = false;
        f.Broken = false;                            // (EnemyComponent: ArmourBroken = false at every spawn)
        int lastDealt = 0;
        // all start an attack turn, then what starts the fight (CharacterComponent.StartFighting: the timer before FightStarted)
        foreach (State m in Standing(f)) m.Mine = spawn + m.Base!.MyInterval;
        f.Theirs = spawn + lead.TheirInterval;
        f.StrikeAt = -1;
        f.TheirStunned = 0;
        for (int i = 0; i < front.Cur!.Triggers.Count; i++)
            if (front.Cur!.Triggers[i].On == "AtStartOfFight") Apply(front, front.Cur!.Triggers[i].Use, f.EnemyHp, lastDealt, t, false, false);
        // A monster is never met at or below the eating point without eating first.
        foreach (State m in Standing(f).ToList())
            if (m.Run.EndHp <= m.Plan.EatAt && !Eat(m, t)) return false;

        int group = 0, inGroup = 0, attacks = 0, hits = 0, misses = 0;
        bool fromTurn = false;                       // the hit about to be made started from the turn timer
        while (true)
        {
            long next = f.Theirs;
            foreach (State m in Standing(f))
            {
                next = Math.Min(next, m.Mine);
                if (m.Effects.Count > 0) next = Math.Min(next, m.Effects.Min(e => e.Next));
            }
            if (f.MonsterEffects.Count > 0) next = Math.Min(next, f.MonsterEffects.Min(e => e.Next));
            t = next;

            // 1. effects on them, then on the monster
            foreach (State m in Standing(f).ToList())
                if (!m.Out && m.Effects.Count > 0 && m.Effects.Min(e => e.Next) == t && !Procs(m, t, out t)) return false;
            if (f.MonsterEffects.Count > 0 && f.MonsterEffects.Min(e => e.Next) == t) MonsterProcs(f, t);

            // 2. each of them (from this monster's first): a stunned turn passes, or they attack
            for (int j = 0; j < f.Members.Count; j++)
            {
                State st = f.Members[(first + j) % f.Members.Count];
                if (st.Out || st.Mine != t) continue;
                RunResult run = st.Run;
                Duel duel = st.Base!;
                if (++st.Turns > MostAttacks) return Withdraw(st, $"They cannot beat {duel.Enemy}.", t);
                if (st.Stunned > 0)
                {
                    // StunnedAction: the turn passes; the last one ends the stun (and what it leaves comes before the next timer)
                    st.Stunned--;
                    if (!TurnEnded(st, false, t)) return false;
                    st.Mine = t + st.Cur!.MyInterval;
                    continue;
                }
                if (st.Cur!.MyStrikes.Count > 0)
                {
                    // their weapon (or spell) has special attacks: hit by hit
                    if (!MyTurn(st, t, lastDealt)) return false;
                    continue;
                }
                Duel cur = st.Cur!;
                run.Attacks++;
                foreach (var (rune, cost) in cur.Runes)
                    if (st.RunesLeft.GetValueOrDefault(rune, st.Have.RunesOf(rune)) < cost)
                        return Withdraw(st, $"Out of {rune.DisplayName.GetPluralText(2)}.", t);
                if (cur.Runes.Count > 0 && rng.NextDouble() >= cur.RunesKept)
                    foreach (var (rune, cost) in cur.Runes)
                    {
                        st.RunesLeft[rune] = st.RunesLeft.GetValueOrDefault(rune, st.Have.RunesOf(rune)) - cost;
                        run.Runes[rune] = run.Runes.GetValueOrDefault(rune) + cost;
                    }
                if (cur.UsesAmmo)
                {
                    if (st.Ammo < 1) return Withdraw(st, "Out of ammunition.", t);
                    st.Ammo--;
                    run.Ammo++;
                }
                bool landed = rng.NextDouble() < cur.MyLand;
                if (landed)
                {
                    int blow;
                    if (cur.MyRaw.Length > 0 && (duel.EnemyWard > 0 || cur.EnemyArmour > 0))
                    {
                        // CharacterComponent.ModifyAttackDamage: the reduction, then what the ward leaves (a share as it
                        // wears down), then armour until a blow is as big as it (then it breaks), rounded once
                        double v = Duel.Roll(cur.MyRaw, cur.MyRawOdds, rng.NextDouble()) * cur.MyScale;
                        if (f.Ward > 0 && duel.EnemyWard > 0) v *= 1 - (double)f.Ward / duel.EnemyWard;
                        if (!f.Broken) v -= cur.EnemyArmour;
                        blow = (int)Math.Max(v, 1);
                        if (!f.Broken && cur.EnemyArmour > 0 && blow >= cur.EnemyArmour) f.Broken = true;
                        // every blow that lands takes one ward, two now and then (DoubleWardDamageChance)
                        if (f.Ward > 0) f.Ward = Math.Max(0, f.Ward - (rng.NextDouble() < cur.MyDoubleWard ? 2 : 1));
                    }
                    else blow = Duel.Roll(cur.MyDamage, cur.MyOdds, rng.NextDouble());
                    int done = Math.Min(blow, Math.Max(f.EnemyHp, 0));
                    f.EnemyHp -= done;
                    if (f.EnemyHp <= 0) f.Dying = true;
                    run.Landed++;
                    run.Dealt += done;
                    st.DealtHere += done;
                    foreach (var (level, per, _) in duel.Xp) run.Xp[level] = run.Xp.GetValueOrDefault(level) + per * done;
                    // what the monster sends back (ReflectAttackDamage: a share of the damage done, never their last hitpoint)
                    if (done > 0 && cur.TheirReflect > 0 && run.EndHp > 1)
                    {
                        int back = Math.Min(Math.Max((int)((int)(done * cur.TheirReflect) * cur.ReflectScale), 1), run.EndHp - 1);
                        if (!Hurt(st, back, duel.Enemy, t, true)) return false;
                    }
                }
                // their own triggers (the area's), then the monster's that answer being attacked (DefendedTrigger: one
                // attack a turn for them), inside their attack: what they put on them skips this turn's end
                st.TurnOver = false;
                for (int i = 0; i < st.Cur!.MyTriggers.Count && !st.Out; i++)
                {
                    Trigger trig = st.Cur!.MyTriggers[i];
                    if (Specials.OnAttackFires(trig.On, landed, 1, landed ? 1 : 0, landed ? 0 : 1)
                        && !Apply(st, trig.Use, f.EnemyHp, lastDealt, t, !st.TurnOver, f.StrikeAt >= 0)) return false;
                }
                for (int i = 0; i < st.Cur!.Triggers.Count && !st.Out; i++)
                {
                    Trigger trig = st.Cur!.Triggers[i];
                    if (Specials.WhenAttackedFires(trig.On, landed, 1, landed ? 1 : 0, landed ? 0 : 1)
                        && !Apply(st, trig.Use, f.EnemyHp, lastDealt, t, !st.TurnOver, f.StrikeAt >= 0)) return false;
                }
                if (st.Out) continue;
                // the turn ends and the next one starts (unless a stun already did both)
                if (!st.TurnOver)
                {
                    if (!TurnEnded(st, false, t)) return false;
                    st.Mine = t + st.Cur!.MyInterval;
                }
                st.TurnOver = false;
            }

            // 3. the monster: its turn, or the next hit of the attack it is making (still on the tick it dies), at the
            // first still standing. Every number is read from that one's variant for what is on at that moment.
            if (f.Theirs == t && f.TheirStunned > 0)
            {
                // the monster's stunned turn passes (StunnedAction)
                f.TheirStunned--;
                if (!TurnEnded(f.Front!, true, t)) return false;
                f.Theirs = t + f.Front!.Cur!.TheirInterval;
            }
            else if (f.Theirs == t)
            {
                State st = f.Front!;
                RunResult run = st.Run;
                f.TheirTurnOver = false;
                if (f.StrikeAt < 0)
                {
                    f.StrikeAt = PickStrike(st.Cur!.Strikes, rng);
                    group = inGroup = attacks = hits = misses = 0;
                    fromTurn = true;
                }
                for (int i = 0; i < st.Cur!.Strikes[f.StrikeAt].PreHit.Count; i++)
                    if (!Apply(st, st.Cur!.Strikes[f.StrikeAt].PreHit[i], f.EnemyHp, lastDealt, t, st.MyStrikeAt >= 0, true)) return false;
                Duel cur = st.Cur!;
                Strike strike = cur.Strikes[f.StrikeAt];
                bool hit = !strike.Avoidable || rng.NextDouble() < cur.TheirHit;
                if (hit)
                {
                    int raw = 0;
                    foreach (var (formula, ofDefender) in strike.Hits[group].Parts)
                        raw += Specials.Value(formula, ofDefender ? cur.MyMin : cur.TheirMin, ofDefender ? cur.MyMax : cur.TheirMax, ofDefender ? run.EndHp : f.EnemyHp,
                                              ofDefender ? 0 : lastDealt, (lo, hi) => rng.NextInclusiveInt32(lo, Math.Max(lo, hi)));
                    if (rng.NextDouble() < cur.TheirCrit) raw = (int)(raw * cur.TheirCritMult);
                    if (rng.NextDouble() < cur.TheirNegate) raw = 0;
                    lastDealt = 0;
                    if (raw > 0)
                    {
                        int dmg = (int)Math.Max(raw * strike.Scale, 1);
                        lastDealt = Math.Min(dmg, run.EndHp);
                        if (!Hurt(st, dmg, st.Base!.Enemy, t, true)) return false;
                        // what they send back (Sharp Skin on them: ReflectAttackDamage, never the monster's last hitpoint)
                        if (!st.Out && lastDealt > 0 && cur.MyReflect > 0 && f.EnemyHp > 1)
                            f.EnemyHp -= Math.Min(Math.Max((int)((int)(lastDealt * cur.MyReflect) * cur.MyReflectScale), 1), f.EnemyHp - 1);
                    }
                    for (int i = 0; f.StrikeAt >= 0 && i < st.Cur!.Strikes[f.StrikeAt].OnHit.Count && !st.Out; i++)
                        if (!Apply(st, st.Cur!.Strikes[f.StrikeAt].OnHit[i], f.EnemyHp, lastDealt, t, st.MyStrikeAt >= 0, !f.TheirTurnOver)) return false;
                    hits++;
                    run.TheirLanded++;
                }
                else { lastDealt = 0; misses++; }
                attacks++;
                run.TheirAttacks++;
                for (int i = 0; i < st.Cur!.Triggers.Count && !st.Out; i++)
                {
                    Trigger trig = st.Cur!.Triggers[i];
                    if (Specials.OnAttackFires(trig.On, hit, attacks, hits, misses) && !Apply(st, trig.Use, f.EnemyHp, lastDealt, t, st.MyStrikeAt >= 0, !f.TheirTurnOver)) return false;
                }
                for (int i = 0; i < st.Cur!.MyTriggers.Count && !st.Out; i++)
                {
                    Trigger trig = st.Cur!.MyTriggers[i];
                    if (Specials.WhenAttackedFires(trig.On, hit, attacks, hits, misses) && !Apply(st, trig.Use, f.EnemyHp, lastDealt, t, st.MyStrikeAt >= 0, !f.TheirTurnOver)) return false;
                }
                // (one knocked out mid-attack: the rest of the attack goes to the next one standing)
                State turnOf = f.Front!;
                if (f.TheirTurnOver) { f.TheirTurnOver = false; fromTurn = false; }       // stunned inside its attack: the rest of it is gone
                else
                {
                    if (++inGroup >= strike.Hits[group].Count) { group++; inGroup = 0; }
                    // The next hit of a multi-hit attack: its timer starts within the same character tick and is ticked
                    // once right away when that tick began at the turn timer (CharacterComponent.Tick), a tick sooner.
                    if (group >= strike.Hits.Count)
                    {
                        f.StrikeAt = -1;
                        if (!TurnEnded(turnOf, true, t)) return false;
                        f.Theirs = t + turnOf.Cur!.TheirInterval;
                    }
                    else f.Theirs = t + Math.Max(1, strike.Hits[group].Gap - (fromTurn ? 1 : 0));
                    fromTurn = false;
                }
            }

            // 4. deaths: the fight ends, and with it every status (FightEnded); damage over time goes on
            if (f.Dying || f.EnemyHp <= 0)
            {
                State front2 = f.Front!;
                for (int i = 0; i < front2.Cur!.Triggers.Count; i++)
                {
                    Trigger trig = front2.Cur!.Triggers[i];
                    if (trig.On == "OnDeath" && trig.Use.Status == null && !Apply(front2, trig.Use, 0, lastDealt, t, false, false)) return false;
                }
                // the kill's bonus XP (armour, ward), shared by the damage each did to it
                long total = f.Members.Sum(m => m.DealtHere);
                foreach (State m in f.Members)
                {
                    double share = total > 0 ? (double)m.DealtHere / total : f.Members.Count == 1 ? 1 : 0;
                    if (share <= 0) continue;
                    foreach (var (level, _, onKill) in m.Base!.Xp) m.Run.Xp[level] = m.Run.Xp.GetValueOrDefault(level) + onKill * share;
                }
                foreach (State m in f.Members)
                {
                    m.Run.Kills++;
                    m.Statuses.Clear();
                    m.Stunned = 0;
                    m.MyStrikeAt = -1;
                    m.Base = m.Cur = null;
                }
                f.MonsterStatuses.Clear();
                f.MonsterEffects.Clear();
                f.TheirStunned = 0;
                f.StrikeAt = -1;
                return true;
            }
        }
    }

    /// <summary>
    /// One hit of the adventurer's own attack when their weapon (or spell) has special attacks (AttackAction,
    /// CharacterComponent.PerformAttack). At the turn's start the runes of a cast are paid and the attack is rolled
    /// (the plain blow among them); every hit then costs one ammunition (PlayerComponent.OnAttack), applies the
    /// attack's pre-hit effects, rolls to hit unless it cannot be avoided, adds up its damage, a critical hit and the
    /// monster's negation, takes the monster's reduction, ward and armour, applies the on-hit effects and fires both
    /// sides' triggers with this turn's counts. The next hit comes after its group's interval; after the last one
    /// the turn ends. False when the run ends.
    /// </summary>
    static bool MyTurn(State st, long t, int lastDealt)
    {
        Shared f = st.F;
        IRandom rng = f.Rng;
        RunResult run = st.Run;
        Duel duel = st.Base!;
        Duel cur = st.Cur!;
        if (st.MyStrikeAt < 0)
        {
            run.Attacks++;
            foreach (var (rune, cost) in cur.Runes)
                if (st.RunesLeft.GetValueOrDefault(rune, st.Have.RunesOf(rune)) < cost)
                    return Withdraw(st, $"Out of {rune.DisplayName.GetPluralText(2)}.", t);
            if (cur.Runes.Count > 0 && rng.NextDouble() >= cur.RunesKept)
                foreach (var (rune, cost) in cur.Runes)
                {
                    st.RunesLeft[rune] = st.RunesLeft.GetValueOrDefault(rune, st.Have.RunesOf(rune)) - cost;
                    run.Runes[rune] = run.Runes.GetValueOrDefault(rune) + cost;
                }
            st.MyStrikeAt = PickStrike(cur.MyStrikes, rng);
            st.MyGroup = st.MyInGroup = st.MyAttacks = st.MyHits = st.MyMisses = 0;
            st.MyFromTurn = true;
        }
        if (cur.UsesAmmo)
        {
            if (st.Ammo < 1) return Withdraw(st, "Out of ammunition.", t);
            st.Ammo--;
            run.Ammo++;
        }
        st.TurnOver = false;
        int at = st.MyStrikeAt;
        for (int i = 0; i < st.Cur!.MyStrikes[at].PreHit.Count && !st.Out; i++)
            if (!Apply(st, st.Cur!.MyStrikes[at].PreHit[i], f.EnemyHp, lastDealt, t, true, f.StrikeAt >= 0)) return false;
        if (st.Out) return true;
        cur = st.Cur!;
        Strike strike = cur.MyStrikes[at];
        bool landed = !cur.MyImmune && (!strike.Avoidable || rng.NextDouble() < cur.MyHit);
        if (landed)
        {
            int raw = 0;
            foreach (var (formula, ofDefender) in strike.Hits[st.MyGroup].Parts)
                raw += Specials.Value(formula, ofDefender ? cur.TheirMin : cur.MyMin, ofDefender ? cur.TheirMax : cur.MyMax,
                                      ofDefender ? Math.Max(f.EnemyHp, 0) : run.EndHp, ofDefender ? lastDealt : st.MyLastDealt,
                                      (lo, hi) => rng.NextInclusiveInt32(lo, Math.Max(lo, hi)));
            if (rng.NextDouble() < cur.MyCrit) raw = (int)(raw * cur.MyCritMult);
            if (rng.NextDouble() < cur.MyNegate) raw = 0;
            int done = 0;
            if (raw > 0)
            {
                // ModifyAttackDamage: the reduction, what the ward leaves (a share as it wears down), armour until a blow
                // is as big as it (then it breaks), at least 1
                double v = raw * strike.Scale;
                if (f.Ward > 0 && duel.EnemyWard > 0) v *= 1 - (double)f.Ward / duel.EnemyWard;
                if (!f.Broken) v -= cur.EnemyArmour;
                int blow = (int)Math.Max(v, 1);
                done = Math.Min(blow, Math.Max(f.EnemyHp, 0));
                f.EnemyHp -= done;
                if (f.EnemyHp <= 0) f.Dying = true;
                if (f.Ward > 0) f.Ward = Math.Max(0, f.Ward - (rng.NextDouble() < cur.MyDoubleWard ? 2 : 1));
                if (!f.Broken && cur.EnemyArmour > 0 && blow >= cur.EnemyArmour) f.Broken = true;
                run.Dealt += done;
                st.DealtHere += done;
                foreach (var (level, per, _) in duel.Xp) run.Xp[level] = run.Xp.GetValueOrDefault(level) + per * done;
                if (done > 0 && cur.TheirReflect > 0 && run.EndHp > 1)
                {
                    int back = Math.Min(Math.Max((int)((int)(done * cur.TheirReflect) * cur.ReflectScale), 1), run.EndHp - 1);
                    if (!Hurt(st, back, duel.Enemy, t, true)) return false;
                }
            }
            st.MyLastDealt = done;
            run.Landed++;
            for (int i = 0; st.MyStrikeAt >= 0 && i < st.Cur!.MyStrikes[at].OnHit.Count && !st.Out; i++)
                if (!Apply(st, st.Cur!.MyStrikes[at].OnHit[i], f.EnemyHp, lastDealt, t, true, f.StrikeAt >= 0)) return false;
        }
        else st.MyLastDealt = 0;
        st.MyAttacks++;
        if (landed) st.MyHits++; else st.MyMisses++;
        for (int i = 0; i < st.Cur!.MyTriggers.Count && !st.Out; i++)
        {
            Trigger trig = st.Cur!.MyTriggers[i];
            if (Specials.OnAttackFires(trig.On, landed, st.MyAttacks, st.MyHits, st.MyMisses)
                && !Apply(st, trig.Use, f.EnemyHp, lastDealt, t, !st.TurnOver, f.StrikeAt >= 0)) return false;
        }
        for (int i = 0; i < st.Cur!.Triggers.Count && !st.Out; i++)
        {
            Trigger trig = st.Cur!.Triggers[i];
            if (Specials.WhenAttackedFires(trig.On, landed, st.MyAttacks, st.MyHits, st.MyMisses)
                && !Apply(st, trig.Use, f.EnemyHp, lastDealt, t, !st.TurnOver, f.StrikeAt >= 0)) return false;
        }
        if (st.Out) return true;
        // stunned inside their own attack: the turn already ended and the rest of the attack is gone
        if (st.TurnOver || st.MyStrikeAt < 0) { st.MyStrikeAt = -1; st.TurnOver = false; return true; }
        Strike now = st.Cur!.MyStrikes[at];
        if (++st.MyInGroup >= now.Hits[st.MyGroup].Count) { st.MyGroup++; st.MyInGroup = 0; }
        if (st.MyGroup >= now.Hits.Count)
        {
            st.MyStrikeAt = -1;
            if (!TurnEnded(st, false, t)) return false;
            st.Mine = t + st.Cur!.MyInterval;
        }
        // the next hit: its timer starts within the same character tick, a tick sooner when that began at the turn timer
        else st.Mine = t + Math.Max(1, now.Hits[st.MyGroup].Gap - (st.MyFromTurn ? 1 : 0));
        st.MyFromTurn = false;
        return true;
    }

    /// <summary>
    /// Damage over time an adventurer's attack puts on the monster (CombatEffectsComponent.ApplyEffect): not on a
    /// monster that still has a ward, unless avoided; one already on gains stacks up to its most (and the damage of
    /// the new ones), but lasts no longer.
    /// </summary>
    static void MonsterDot(State st, EffectUse use, long t)
    {
        Shared f = st.F;
        IRandom rng = st.Rng;
        if (st.Cur == null || f.Ward > 0 || f.Dying) return;
        Duel cur = st.Cur;
        Dot dot = cur.MonsterDots.TryGetValue(use.Dot.Id, out Dot? d) ? d : use.Dot;
        int Roll() => Math.Max(1, Specials.Value(dot.Formula, dot.OfSource ? cur.MyMin : cur.TheirMin, dot.OfSource ? cur.MyMax : cur.TheirMax,
                                                 dot.OfSource ? st.Run.EndHp : Math.Max(f.EnemyHp, 0), dot.OfSource ? st.MyLastDealt : 0,
                                                 (lo, hi) => rng.NextInclusiveInt32(lo, Math.Max(lo, hi))));
        int add = Math.Max(1, use.Stacks ?? dot.DefaultStacks);
        Active? on = f.MonsterEffects.FirstOrDefault(e => e.Dot.Id == dot.Id);
        if (on != null)
        {
            if (rng.NextDouble() < dot.Avoid || on.Stacks >= on.Dot.MaxStacks) return;
            int more = Math.Min(on.Dot.MaxStacks - on.Stacks, add);
            on.Stacks += more;
            on.PerProc += Roll() * more;
            Remake(st, everyone: true);
            return;
        }
        if (rng.NextDouble() < dot.Avoid) return;
        int stacks = Math.Min(add, dot.MaxStacks);
        f.MonsterEffects.Add(new Active { Dot = dot, Stacks = stacks, PerProc = Roll() * stacks, Next = t + Math.Max(1, dot.Interval), Owner = st.Index });
        Remake(st, everyone: true);
    }

    /// <summary>
    /// The damage over time on the monster whose time has come (ActiveCombatEffect.Damage -> ModifyEffectDamage: the
    /// reduction as the one who put it there sees it now, armour unless broken, at least 1; nothing while it has a
    /// ward). It gives no XP (the game gives XP for attacks only); its lifesteal heals the one who put it there.
    /// </summary>
    static void MonsterProcs(Shared f, long at)
    {
        foreach (Active a in f.MonsterEffects.Where(e => e.Next == at).ToList())
        {
            State? by = a.Owner >= 0 && a.Owner < f.Members.Count ? f.Members[a.Owner] : null;
            Duel? cur = by?.Cur ?? f.Front?.Cur;
            Dot now = cur != null && cur.MonsterDots.TryGetValue(a.Dot.Id, out Dot? d) ? d : a.Dot;
            if (f.Ward <= 0 && !f.Dying && f.EnemyHp > 0)
            {
                double v = a.PerProc * now.Scale;
                if (!f.Broken && cur != null) v -= cur.EnemyArmour;
                int done = Math.Min((int)Math.Max(v, 1), f.EnemyHp);
                f.EnemyHp -= done;
                if (f.EnemyHp <= 0) f.Dying = true;
                if (by != null && !by.Out)
                {
                    by.Run.Dealt += done;
                    by.DealtHere += done;
                    if (now.Lifesteal > 0 && by.Run.EndHp > 0) by.Run.EndHp = Math.Min(by.Plan.MaxHp, by.Run.EndHp + Math.Max(1, (int)(done * now.Lifesteal)));
                }
            }
            if (++a.Done >= a.Dot.Procs)
            {
                f.MonsterEffects.Remove(a);
                if (f.Members.Count > 0) Remake(f.Members[0], everyone: true);
            }
            else a.Next = at + a.Dot.Interval;
        }
    }

    /// <summary>The effects whose time has come at tick <paramref name="at"/> hurt the adventurer. False when the run ends.</summary>
    static bool Procs(State st, long at, out long t)
    {
        t = at;
        foreach (Active a in st.Effects.Where(e => e.Next == at).ToList())
        {
            if (st.Out) break;
            // its damage as reduced now: by the monster being fought and the statuses on (none between monsters)
            Dot now = st.Cur != null && st.Cur.Dots.TryGetValue(a.Dot.Key, out Dot? d) ? d : a.Dot;
            double scale = st.Between ? a.Dot.GapScale
                : now != a.Dot || st.Cur == null || a.Dot.BySelf || a.Dot.PlainScale <= 0 ? now.Scale
                : st.Cur.EffectScale * a.Dot.Scale / a.Dot.PlainScale;
            if (!Hurt(st, (int)Math.Max(a.PerProc * scale, 1), a.From, at, !st.Between && st.Cur != null && !a.Dot.BySelf, now.Lifesteal)) return false;
            if (st.Out) break;
            if (++a.Done >= a.Dot.Procs) { st.Effects.Remove(a); Remake(st); }
            else a.Next = at + a.Dot.Interval;
        }
        return true;
    }

    /// <summary>
    /// An effect applied by the monster (CombatEffectsComponent.ApplyEffect): first its own chance, then the chance to
    /// avoid it. Damage over time already on them gains stacks (up to its most) and the damage of the new ones, but
    /// lasts no longer (ActiveDamageOverTimeEffect.ReapplyConfig); a status already on stays as it is.
    /// <paramref name="mine"/> and <paramref name="theirs"/>: whether the adventurer or the monster is in the middle
    /// of their own turn (TurnAction.IsExecuting).
    /// </summary>
    static bool Apply(State st, EffectUse use, int enemyHp, int lastDealt, long t, bool mine, bool theirs)
    {
        IRandom rng = st.Rng;
        if (!(rng.NextDouble() < use.Chance)) return true;
        if (use.Status != null) return Give(st, use.Status, use.Turns, use.Stacks, use.MinStacks, use.MaxStacks, use.Status.OnMonster ? theirs : mine, t);
        if (use.Dot.OnMonster) { MonsterDot(st, use, t); return true; }
        if (st.Out) return true;
        Duel duel = st.Cur!;
        Dot dot = use.Dot;
        int Roll() => Math.Max(1, Specials.Value(dot.Formula, dot.OfSource ? (dot.BySelf ? duel.MyMin : duel.TheirMin) : 0, dot.OfSource ? (dot.BySelf ? duel.MyMax : duel.TheirMax) : 0,
                                                 dot.OfSource && !dot.BySelf ? Math.Max(enemyHp, 0) : st.Run.EndHp, dot.OfSource && !dot.BySelf ? lastDealt : 0,
                                                 (lo, hi) => rng.NextInclusiveInt32(lo, Math.Max(lo, hi))));
        int add = Math.Max(1, use.Stacks ?? dot.DefaultStacks);
        Active? on = st.Effects.FirstOrDefault(e => e.Dot.Id == dot.Id);
        if (on != null)
        {
            if (rng.NextDouble() < dot.Avoid || on.Stacks >= on.Dot.MaxStacks) return true;
            int more = Math.Min(on.Dot.MaxStacks - on.Stacks, add);
            on.Stacks += more;
            dot = on.Dot;
            on.PerProc += Roll() * more;
            Remake(st);
            return true;
        }
        if (rng.NextDouble() < dot.Avoid) return true;
        int stacks = Math.Min(add, dot.MaxStacks);
        // kept as the monster's plain duel has it: between monsters no status is on
        Dot kept = st.Base != null && st.Base.Dots.TryGetValue(dot.Key, out Dot? plain) ? plain : dot;
        st.Effects.Add(new Active { Dot = kept, From = dot.BySelf ? "the area" : duel.Enemy, Stacks = stacks, PerProc = Roll() * stacks, Next = t + Math.Max(1, dot.Interval) });
        Remake(st);
        return true;
    }

    /// <summary>
    /// A status goes on (CombatEffectsComponent.ApplyEffect, then OnApplied): unless avoided. One already on: a stacking
    /// one gains stacks (up to its most), any other stays as it is. A stun takes the turn away: the stunned one's timer
    /// starts again with a full stunned turn, and when they were in the middle of their own attack, that turn ends
    /// there (CharacterComponent.Stun). On the monster it is the monster's, shared by everyone fighting it. False when
    /// the run ends (a turn's end can hurt).
    /// </summary>
    static bool Give(State st, Status s, int? turns, int? stacks, int? least, int? most, bool executing, long t)
    {
        if (st.Cur == null) return true;
        if (!s.OnMonster && st.Out) return true;
        Shared f = st.F;
        // a negative effect does not go on a monster that still has a ward
        if (s.OnMonster && !s.Effect.IsPositive && f.Ward > 0) return true;
        IRandom rng = st.Rng;
        List<On> list = s.OnMonster ? f.MonsterStatuses : st.Statuses;
        On? have = list.FirstOrDefault(o => o.S.Id == s.Id && o.S.OnMonster == s.OnMonster && o.S.Kind == s.Kind);
        if (have != null)
        {
            bool avoided = rng.NextDouble() < st.Cur.StatusAvoid[s.Bit];
            if (!avoided && s.Kind == Status.Kinds.Stacking && have.Level < s.MaxStacks)
            {
                have.Level += Math.Min(s.MaxStacks - have.Level, Math.Max(1, stacks ?? s.AddStacks));
                Remake(st, s.OnMonster);
            }
            return true;
        }
        if (rng.NextDouble() < st.Cur.StatusAvoid[s.Bit]) return true;
        var on = new On { S = s, Skip = executing };
        int n = Math.Max(1, turns ?? s.DefaultTurns);
        switch (s.Kind)
        {
            case Status.Kinds.Stacking: on.Level = Math.Min(Math.Max(1, stacks ?? s.AddStacks), s.MaxStacks); break;
            case Status.Kinds.Random:
                on.Turns = n;
                on.Level = Math.Clamp(rng.NextInclusiveInt32(least ?? s.MinStacks, Math.Max(least ?? s.MinStacks, most ?? s.DefaultMax)), 0, s.Levels - 1);
                break;
            case Status.Kinds.EndDamage: case Status.Kinds.EndHeal: break;
            default: on.Turns = n; on.Level = 1; break;
        }
        list.Add(on);
        st.Run.Given[s.Id] = st.Run.Given.GetValueOrDefault(s.Id) + 1;
        Remake(st, s.OnMonster);
        if (s.Stuns && !s.OnMonster)
        {
            if (executing) { if (!TurnEnded(st, false, t)) return false; st.TurnOver = true; }
            st.MyStrikeAt = -1;                      // (the rest of their own attack is gone)
            st.Stunned = n;
            st.Mine = t + st.Cur!.MyInterval;
        }
        else if (s.Stuns)
        {
            // the monster loses its turn: in the middle of its attack, that turn ends there and the rest of the attack is gone
            if (executing) { if (!TurnEnded(st, true, t)) return false; f.TheirTurnOver = true; }
            f.StrikeAt = -1;
            f.TheirStunned = n;
            f.Theirs = t + (f.Front ?? st).Cur!.TheirInterval;
        }
        return true;
    }

    /// <summary>
    /// The end of a turn of an adventurer or the monster (CharacterComponent.TurnEnded), each status of theirs in the
    /// order it went on: one that lasts some turns counts it (one put on during that turn skips it) and, run out,
    /// leaves what it leaves, then goes; end-of-turn damage hurts the adventurer (as damage over time does), end-of-turn
    /// healing heals the monster. Stacks wait for the fight's end. For the monster, <paramref name="st"/> is the one it
    /// fights. False when the run ends.
    /// </summary>
    static bool TurnEnded(State st, bool monster, long t)
    {
        Shared f = st.F;
        List<On> list = monster ? f.MonsterStatuses : st.Statuses;
        foreach (On on in list.ToList())
        {
            if (!list.Contains(on) || st.Cur == null) continue;
            Duel cur = st.Cur;
            switch (on.S.Kind)
            {
                case Status.Kinds.Stacking: continue;
                case Status.Kinds.EndDamage:
                {
                    if (st.Out) continue;
                    int raw = Specials.Value(on.S.Formula!, on.S.OfSource ? cur.TheirMin : cur.MyMin, on.S.OfSource ? cur.TheirMax : cur.MyMax,
                                             on.S.OfSource ? Math.Max(f.EnemyHp, 0) : st.Run.EndHp, 0, (lo, hi) => st.Rng.NextInclusiveInt32(lo, Math.Max(lo, hi)));
                    if (!Hurt(st, (int)Math.Max(raw * cur.StatusScale[on.S.Bit], 1), cur.Enemy, t, true)) return false;
                    continue;
                }
                case Status.Kinds.EndHeal:
                {
                    int heal = Specials.Value(on.S.Formula!, cur.TheirMin, cur.TheirMax, Math.Max(f.EnemyHp, 0), 0, (lo, hi) => st.Rng.NextInclusiveInt32(lo, Math.Max(lo, hi)));
                    if (!f.Dying) f.EnemyHp = Math.Min(cur.EnemyHp, f.EnemyHp + Math.Max(0, heal));
                    continue;
                }
            }
            if (on.Skip) { on.Skip = false; continue; }
            if (--on.Turns > 0) continue;
            if (on.S.Then != null && !Give(st, on.S.Then, on.S.ThenTurns, null, null, null, false, t)) return false;
            list.Remove(on);
            Remake(st, monster);
        }
        return true;
    }

    /// <summary>The duel for what is on now (theirs and the monster's); a change on the monster changes everyone's.</summary>
    static void Remake(State st, bool everyone = false)
    {
        if (everyone) { foreach (State m in st.F.Members) Remake(m); return; }
        if (st.Base == null) return;
        int index = 0;
        foreach (On on in st.Statuses.Concat(st.F.MonsterStatuses))
            if (on.S.Levels > 1) index += Math.Min(on.Level, on.S.Levels - 1) * on.S.Stride;
        foreach (Status s in st.Base.Statuses)
            if (s.Kind == Status.Kinds.DotStats && (s.OnMonster ? st.F.MonsterEffects : st.Effects).FirstOrDefault(e => e.Dot.Id == s.Id) is { } burning)
                index += Math.Min(burning.Stacks, s.Levels - 1) * s.Stride;
        st.Cur = st.Base.Variants != null && index < st.Base.Variants.Length ? st.Base.Variants[index] : st.Base;
    }

    /// <summary>
    /// Damage to an adventurer, then eating as the game's auto-eat does. Knocked out: out of the run (with a party,
    /// the others fight on). False when the run ends there.
    /// </summary>
    static bool Hurt(State st, int dmg, string by, long at, bool fromMonster = false, double effectLifesteal = 0)
    {
        if (st.Out) return true;
        Shared f = st.F;
        RunResult run = st.Run;
        int removed = Math.Min(dmg, run.EndHp);
        run.DamageTaken += removed;
        if (dmg >= run.EndHp)
        {
            run.EndHp = 0;
            run.KnockedOut = true;
            run.KilledBy = by;
            run.Seconds = at / Duel.TicksPerSecond;
            st.Out = true;
            st.Effects.Clear();                                   // (the game clears the effects of who falls)
            st.Statuses.Clear();
            if (f.Members.All(m => m.Out)) { run.Defeated = true; return false; }
            // the monster's attack in progress goes on at the next one standing
            Remake(f.Front!);
            return true;
        }
        run.EndHp -= dmg;
        bool on = run.EndHp > st.Plan.EatAt || Eat(st, at);
        // the monster heals from what it took off them (CharacterComponent.Lifesteal; effects: their own lifesteal too)
        if (on && fromMonster && st.Cur != null && !f.Dying && f.EnemyHp > 0)
        {
            if (st.Cur.TheirLifesteal > 0) f.EnemyHp = Math.Min(st.Cur.EnemyHp, f.EnemyHp + Math.Max(1, (int)(removed * st.Cur.TheirLifesteal)));
            if (effectLifesteal > 0) f.EnemyHp = Math.Min(st.Cur.EnemyHp, f.EnemyHp + Math.Max(1, (int)(removed * effectLifesteal)));
        }
        return on;
    }

    /// <summary>
    /// CombatFoodComponent.AutoEat: as many servings as fit under the maximum, at least one. When the food runs out
    /// before they are back above the eating point, they withdraw rather than fight on (and risk a defeat that
    /// food would have prevented).
    /// </summary>
    static bool Eat(State st, long at)
    {
        DungeonPlan plan = st.Plan;
        RunResult run = st.Run;
        if (plan.Heal <= 0 || st.Food < 1) { Withdraw(st, "Out of food.", at); return false; }
        int servings = (int)Math.Max(1, Math.Floor((double)(plan.MaxHp - run.EndHp) / plan.Heal));
        servings = (int)Math.Min(servings, st.Food);
        run.EndHp = Math.Min(plan.MaxHp, run.EndHp + servings * plan.Heal);
        int paid = 0;
        for (int i = 0; i < servings; i++) if (plan.FoodUse >= 1 || st.Rng.NextDouble() < plan.FoodUse) paid++;
        st.Food -= paid;
        run.Food += paid;
        if (run.EndHp <= plan.EatAt) { Withdraw(st, "Out of food.", at); return false; }
        return true;
    }

    static bool Withdraw(State st, string why, long at)
    {
        st.Run.Withdrawn = true;
        st.Run.Why = why;
        st.Run.Seconds = at / Duel.TicksPerSecond;
        return false;
    }

    static int PickIndex<T>(IReadOnlyList<(double Weight, T Item)> options, IRandom rng)
    {
        if (options.Count == 1) return 0;
        double u = rng.NextDouble(), run = 0;
        for (int i = 0; i < options.Count; i++) { run += options[i].Weight; if (u < run) return i; }
        return options.Count - 1;
    }

    static int PickStrike(List<Strike> strikes, IRandom rng)
    {
        // CharacterComponent.RollNextAttack: running total of the chances; the first one when nothing is reached
        double u = rng.NextDouble(), run = 0;
        for (int i = 0; i < strikes.Count; i++) { run += strikes[i].Chance; if (u < run) return i; }
        return 0;
    }

    /// <summary>
    /// A monster with special attacks or effects hunted again and again: two thousand kills played out from full
    /// health (hitpoints and effects carried from kill to kill, plenty of food), for what it costs per kill.
    /// </summary>
    public static RunResult Hunting(Fight fight, double foodUse)
    {
        RunResult r = HuntingRun(fight, foodUse, 2000, 0x5EC1A15UL);
        int done = Math.Max(1, r.Kills);
        fight.DamagePerKill = (double)r.DamageTaken / done;
        fight.FoodPerKill = (double)r.Food / done;
        fight.SecondsPerKill = r.Seconds / done;
        fight.AttacksPerKill = r.Attacks / (double)done;
        fight.AmmoPerKill = (double)r.Ammo / done;
        fight.Runes = fight.RunesPerAttack.Select(x => (x.Rune, x.PerAttack * r.Attacks / done)).ToList();
        fight.XpPerKill = r.Xp.ToDictionary(kv => kv.Key, kv => kv.Value / done);
        return r;
    }

    /// <summary>A monster hunted <paramref name="kills"/> times in a row from full health, plenty of food.</summary>
    public static RunResult HuntingRun(Fight fight, double foodUse, int kills, ulong seed)
    {
        // a risky monster (a dungeon's) may knock them out: they start again at full health, and the kills go on
        var all = new RunResult();
        var rng = new Xoshiro256StarStarRandom(seed);
        for (int tries = 0; all.Kills < kills && tries < 1000; tries++)
        {
            var plan = new DungeonPlan { CanGo = true, MaxHp = fight.MaxHp, EatAt = fight.EatAt, Heal = fight.Heal, FoodUse = foodUse, Safe = true };
            plan.Waves.Add((new List<(double, Fight)> { (1.0, fight) }, kills - all.Kills));
            RunResult r = Play(plan, fight.MaxHp, Supplies.Plenty, rng);
            all.Seconds += r.Seconds; all.Food += r.Food; all.Ammo += r.Ammo; all.Kills += r.Kills;
            all.DamageTaken += r.DamageTaken; all.Attacks += r.Attacks; all.Landed += r.Landed; all.Dealt += r.Dealt;
            all.TheirAttacks += r.TheirAttacks; all.TheirLanded += r.TheirLanded;
            foreach (var (k, v) in r.Runes) all.Runes[k] = all.Runes.GetValueOrDefault(k) + v;
            foreach (var (k, v) in r.Xp) all.Xp[k] = all.Xp.GetValueOrDefault(k) + v;
            foreach (var (k, v) in r.Given) all.Given[k] = all.Given.GetValueOrDefault(k) + v;
            all.EndHp = r.EndHp;
            if (r.Defeated) { all.Defeated = true; all.KilledBy = r.KilledBy; }
            if (r.Withdrawn) { all.Withdrawn = true; all.Why = r.Why; break; }
            if (r.Cleared) { all.Cleared = true; break; }
        }
        return all;
    }

    static T Pick<T>(IReadOnlyList<(double Weight, T Item)> options, IRandom rng)
    {
        if (options.Count == 1) return options[0].Item;
        double u = rng.NextDouble(), run = 0;
        foreach (var (w, item) in options) { run += w; if (u < run) return item; }
        return options[^1].Item;
    }
}
