using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Adventurers;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Components.CombatAreas;
using GamesByMalcs.Melvor2.Components.CombatLevels;
using GamesByMalcs.Melvor2.Components.CombatPassives;
using GamesByMalcs.Melvor2.Components.Equipment;
using GamesByMalcs.Melvor2.Data;
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.Entities;
using GamesByMalcs.Melvor2.Entities.Bank;
using GamesByMalcs.Melvor2.Entities.Combat;
using GamesByMalcs.Melvor2.Modifiers;

namespace CombatAdvisor;

/// <summary>The advisor's word on a fight. Never a death chance: only what the numbers can show.</summary>
public enum Verdict
{
    /// <summary>No single blow can take you from your auto-eat point to nothing, with your food (as shown).</summary>
    Safe,
    /// <summary>A blow can take you from just above your auto-eat point to nothing, or you need to step in (no food, cannot win).</summary>
    Risky,
    /// <summary>One blow can do all of your hitpoints.</summary>
    Deadly,
    /// <summary>The advisor cannot work this fight out (a mechanic it does not know, or something missing).</summary>
    Unknown,
}

/// <summary>What the player has on right now, as the advisor works with it.</summary>
public sealed class Setup
{
    public required Fighter Fighter { get; init; }
    public required AttackStance PlayerStance { get; init; }
    public Item? Food { get; init; }
    public int FoodOwned { get; init; }
    public Item? Ammo { get; init; }
    public int AmmoOwned { get; init; }
    /// <summary>The chance a shot keeps its ammunition (PlayerComponent.OnAttack: AmmoPreservationChance).</summary>
    public double AmmoKept { get; init; }
    /// <summary>Hitpoints share at which the game eats for you (CombatFoodComponent.AutoEat).</summary>
    public double EatShare { get; init; }
    /// <summary>Changes whenever anything the forecasts depend on changes.</summary>
    public required string Signature { get; init; }
    public bool Previewing => !ReferenceEquals(Fighter.Stance, PlayerStance);
}

/// <summary>One monster's forecast for one setup.</summary>
public sealed class Outlook
{
    public required BaseCombatArea Area { get; init; }
    public required Enemy Enemy { get; init; }
    public Verdict Verdict { get; set; }
    /// <summary>The verdict in one line, plain words.</summary>
    public string Why { get; set; } = "";
    /// <summary>
    /// Why the verdict is what it is: "safe", "harmless", "overEat" (a hit can take you from just above auto-eat to 0),
    /// "deadly", "defeated" (the played-out kills knocked you out), "noFood", "cannotWin", "unknown".
    /// </summary>
    public string Reason { get; set; } = "unknown";
    /// <summary>Something the player can fix that is not about danger (no food, no ammunition, no spell); empty when none.</summary>
    public string Problem { get; set; } = "";
    public Fight? Fight { get; set; }
    public double KillsPerHour => Fight?.KillsPerHour ?? 0;
    public double FoodPerHour => Fight == null ? 0 : Fight.FoodPerKill * Fight.KillsPerHour;
    public double AmmoPerHour => Fight == null ? 0 : Fight.AmmoPerKill * (1 - AmmoKept) * Fight.KillsPerHour;
    /// <summary>The chance a shot keeps its ammunition (the playout spends one every shot).</summary>
    public double AmmoKept { get; set; }
    public IEnumerable<(Item Rune, double PerHour)> RunesPerHour =>
        Fight == null ? Enumerable.Empty<(Item, double)>() : Fight.Runes.Select(r => (r.Rune, r.PerKill * Fight.KillsPerHour));
    public IEnumerable<(CombatLevel Level, double PerHour)> XpPerHour =>
        Fight == null ? Enumerable.Empty<(CombatLevel, double)>() : Fight.XpPerKill.Select(x => (x.Key, x.Value * Fight.KillsPerHour));
    /// <summary>The skill the stance trains most (Hitpoints only when nothing else is trained).</summary>
    public (CombatLevel Level, double PerHour)? MainXp =>
        XpPerHour.OrderBy(x => Combat.Local(x.Level.Id) == "Hitpoints" ? 1 : 0).ThenByDescending(x => x.PerHour)
                 .Select(x => ((CombatLevel, double)?)x).FirstOrDefault();
    public int MaxHp => Fight?.MaxHp ?? _maxHp;
    public int EatAt => Fight?.EatAt ?? _eatAt;
    public int EnemyMaxHit => Fight?.EnemyMaxHit ?? _enemyMaxHit;
    internal int _maxHp, _enemyMaxHit, _eatAt;
    /// <summary>True when the safety numbers are known (also for a deadly monster, which has no other numbers).</summary>
    public bool HasSafety => Fight != null || _enemyMaxHit > 0;
    /// <summary>True when the fight was played out blow by blow (special attacks, effects, armour).</summary>
    public bool Played => Fight?.Duels.Any(d => d.Duel.Special || d.Duel.EnemyArmour > 0 || d.Duel.MineSpecial) ?? false;

    /// <summary>False while the kills are still being played out (on a thread of their own): no verdict yet.</summary>
    public bool Ready => Playing == null;
    internal Task<RunResult>? Playing { get; set; }
    internal bool HasFood { get; set; }
}

/// <summary>
/// Works out fights with the player's own setup and every bonus they have right now (gear, prayers, potions,
/// pets, upgrades...). The game's bonus list is copied into a private one, so nothing of the running game is ever
/// changed: the bonuses that belong to the fight on at the moment (combat triangle, elements, the area, the
/// monster's passives, effects) are left out of the copy and the ones of the monster looked at are put in.
/// </summary>
public static class Forecast
{
    // the player's own special attacks (a weapon's, a special spell's) are worked out too
    static Forecast() => Specials.WeaponSpecials = true;

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Modifiers")]
    static extern ModifierSystem GetModifiers(Game game);

    static readonly PropertyInfo? s_values = typeof(ModifierSystem).GetProperty("Values", BindingFlags.Instance | BindingFlags.NonPublic);

    static readonly Dictionary<string, Outlook> s_cache = new();

    public static Action<string> Log { get; set; } = _ => { };

    public static CombatEntity? Combat(Game game) => Adventurers.Combat.Entity(game);

    /// <summary>The player's setup; <paramref name="stance"/> previews another stance of the same weapon.</summary>
    public static Setup? Current(Game game, AttackStance? stance = null)
    {
        if (Combat(game) is not { } combat) return null;
        var levels = Adventurers.Combat.Levels(game).Values.ToDictionary(l => l, l => combat.PlayerCombatLevels.ActiveLevels[l].Level);
        var gear = combat.Equipment.EquippedItems.Where(i => i.Id != "MelvorBase:DefaultWeapon").Distinct().ToList();
        AttackStance playerStance = combat.Player.GetSelectedStance(combat.Equipment.CurrentWeapon.Weapon.Type);
        AttackStance use = stance ?? playerStance;
        var spell = combat.MagicSpells.SelectedSpell?.Data;
        Item? food = combat.Food.SelectedFood?.Item;
        int foodOwned = food == null ? 0 : combat.Food.Inventory.GetQuantity(food);
        double eatShare = 0.25, foodUse = 1.0;
        if (combat.Food.Stats is { } fs)
        {
            eatShare = fs.AutoEatThreshold.Value;
            // auto-eat heals with its own efficiency, and a preserved serving is not used up
            double plain = fs.Healing.Value, auto = fs.AutoEatHealing.Value;
            double kept = Math.Clamp(fs.FoodPreservationChance.Total.Value, 0, 1);
            if (auto > 0 && plain > 0) foodUse = plain / auto * (1 - kept);
        }
        Item? ammo = gear.FirstOrDefault(i => i.Capabilities.TryGet(out EquipmentCapability? e) && e.AmmoType != null);
        int ammoOwned = 0;
        if (ammo != null)
            foreach (EquipmentSlot slot in game.Data.GetDataRegistryOutput<EquipmentSlot>().GetAllObjects())
                if (combat.Equipment.TryGetActiveSlot(slot, out IActiveEquipmentSlot? s) && s.Equipped?.Item == ammo) ammoOwned += s.Quantity;

        double ammoKept = 0;
        if (ammo != null && combat.Player is GamesByMalcs.Melvor2.Components.Characters.PlayerComponent player)
            try { ammoKept = Math.Clamp(player.AmmoStats.AmmoPreservationChance.Total.Value, 0, 1); } catch { }
        var fighter = new Fighter(levels, gear, use, food, EatAt: eatShare, FoodUse: foodUse, Spell: spell);
        ModifierSystem live = GetModifiers(game);
        int sources = 0;
        double weight = 0;
        if (s_values?.GetValue(live) is Dictionary<INamedObject, Dictionary<ModifierValues, double>> all)
            foreach (var (source, values) in all)
                if (!OfTheFight(source)) { sources += values.Count; weight += values.Values.Sum(); }   // (what a fight brings and takes is left out of the copies anyway)
        string signature = string.Join("|", gear.Select(g => g.Id).OrderBy(x => x)) + "#" + use.Id + "#" + spell?.Id + "#" + food?.Id + "#"
                           + string.Join(",", levels.Values) + $"#{eatShare:0.###}#{foodUse:0.###}#{sources}#{weight:0.###}";
        return new Setup
        {
            Fighter = fighter, PlayerStance = playerStance, Food = food, FoodOwned = foodOwned, Ammo = ammo, AmmoOwned = ammoOwned,
            AmmoKept = ammoKept, EatShare = eatShare, Signature = signature + $"#{ammoKept:0.###}",
        };
    }

    /// <summary>The stances the player's weapon allows.</summary>
    public static IReadOnlyList<AttackStance> Stances(Game game, Setup setup) => Adventurers.Combat.Stances(game, setup.Fighter.Gear);

    /// <summary>How many of an item the bank holds (runes are taken from the bank).</summary>
    public static int Owned(Game game, Item item) =>
        game.Entities.TryGetObjectById("MelvorBase:Bank", out GameEntity? b) && b is BankEntity bank ? bank.Inventory.GetQuantity(item) : 0;

    public static bool Cached(Setup setup, BaseCombatArea area, Enemy enemy, out Outlook? outlook) =>
        s_cache.TryGetValue(Key(setup, area, enemy), out outlook);

    static string Key(Setup setup, BaseCombatArea area, Enemy enemy) => setup.Signature + "@" + area.Id + "/" + enemy.Id;

    /// <summary>Forgets every forecast (the tests use it; a changed setup has another key anyway).</summary>
    public static void Forget() => s_cache.Clear();

    /// <summary>The forecast of one monster (worked out once per setup, then remembered).</summary>
    public static Outlook For(Game game, Setup setup, BaseCombatArea area, Enemy enemy)
    {
        string key = Key(setup, area, enemy);
        if (s_cache.TryGetValue(key, out Outlook? known)) return known;
        if (s_cache.Count > 400) s_cache.Clear();
        Outlook outlook;
        try { outlook = Work(game, setup, area, enemy); }
        catch (Exception e)
        {
            Log($"{enemy.DisplayName.Text}: the forecast failed: {(e.InnerException ?? e).Message}");
            outlook = new Outlook { Area = area, Enemy = enemy, Verdict = Verdict.Unknown, Why = "The advisor could not work this fight out." };
        }
        return s_cache[key] = outlook;
    }

    static Outlook Work(Game game, Setup setup, BaseCombatArea area, Enemy enemy)
    {
        var outlook = new Outlook { Area = area, Enemy = enemy };
        if (!Adventurers.Combat.Supported(area, enemy, out string why))
        {
            outlook.Verdict = Verdict.Unknown;
            outlook.Reason = "unknown";
            outlook.Why = Plain(why.StartsWith("Not yet: ") ? "The advisor does not know " + why["Not yet: ".Length..].TrimEnd('.') + " yet." : why);
            return outlook;
        }
        ModifierSystem mods = Copy(game, setup, area, enemy);
        outlook.HasFood = setup.Food != null;
        outlook.AmmoKept = setup.AmmoKept;
        // (forDungeon: the fight without its blow-by-blow part, which is played out below on a thread of its own)
        Fight fight = Adventurers.Combat.Evaluate(game, setup.Fighter, area, enemy, mods, allowRisk: true, forDungeon: true);
        if (!fight.CanFight)
        {
            string w = Plain(fight.Why);
            if (fight.Why.StartsWith("Too dangerous"))
            {
                outlook.Verdict = Verdict.Deadly;
                outlook.Reason = "deadly";
                outlook.Why = w;
                // "one hit of X can do 508 damage, all of their 400 hitpoints": the numbers for the safety gauge
                var m = System.Text.RegularExpressions.Regex.Match(fight.Why, @"can do (\d+) damage, all of their (\d+) hitpoints");
                if (m.Success)
                {
                    outlook._enemyMaxHit = int.Parse(m.Groups[1].Value); outlook._maxHp = int.Parse(m.Groups[2].Value); outlook._eatAt = (int)Math.Round(setup.EatShare * outlook._maxHp);
                    outlook.Why = $"One hit of {enemy.DisplayName.Text} can do {outlook._enemyMaxHit}: more than all your HP ({outlook._maxHp}).";
                }
            }
            else if (fight.Why.Contains("needs food") || fight.Why.Contains("hurts them"))
            {
                outlook.Verdict = Verdict.Risky;
                outlook.Reason = "noFood";
                outlook.Problem = "No food selected: this monster hurts you, and nothing heals you.";
                outlook.Why = outlook.Problem;
            }
            else if (fight.Why.Contains("cannot hurt") || fight.Why.Contains("cannot beat") || fight.Why.Contains("too dark"))
            {
                outlook.Verdict = Verdict.Risky;
                outlook.Reason = "cannotWin";
                outlook.Problem = w;
                outlook.Why = w;
            }
            else
            {
                outlook.Verdict = Verdict.Unknown;
                outlook.Reason = "unknown";
                outlook.Why = w;
                if (fight.Why.Contains("ammunition") || fight.Why.Contains("spell for")) outlook.Problem = w;
            }
            return outlook;
        }
        GameXp(game, mods, setup, fight);
        outlook.Fight = fight;
        if (setup.Ammo == null && fight.AmmoType != null) outlook.Problem = "No ammunition equipped.";
        if (outlook.Played)
        {
            // special attacks and effects: the damage they do is found by playing kills out blow by blow
            // (Dungeons.Hunting in the model), which takes a moment - on a thread, so the game never stutters
            if (fight.Heal <= 0 && fight.Duels.Any(d => d.Duel.Special && d.Duel.Strikes.Any(x => x.Hits.Count > 0) && d.Duel.TheirHit > 0 && d.Duel.TheirMax > 0))
            {
                outlook.Fight = null;
                outlook.Verdict = Verdict.Risky;
                outlook.Reason = "noFood";
                outlook.Problem = "No food selected: this monster hurts you, and nothing heals you.";
                outlook.Why = outlook.Problem;
                return outlook;
            }
            double use = setup.Fighter.FoodUse;
            outlook.Playing = Task.Run(() => Dungeons.HuntingRun(fight, use, PlayedKills, 0x5EC1A15UL));
            s_pending.Add(outlook);
            return outlook;
        }
        Judge(outlook);
        return outlook;
    }

    /// <summary>Kills played out for a monster with special attacks (as the Adventurers' model does).</summary>
    const int PlayedKills = 2000;

    static readonly List<Outlook> s_pending = new();

    /// <summary>Called every frame: forecasts whose kills have been played out get their numbers and verdict.</summary>
    public static bool Update()
    {
        bool any = false;
        for (int i = s_pending.Count - 1; i >= 0; i--)
        {
            Outlook o = s_pending[i];
            if (o.Playing is { IsCompleted: false }) continue;
            s_pending.RemoveAt(i);
            Finish(o);
            any = true;
        }
        return any;
    }

    /// <summary>Waits for the played-out kills (the tests; the game uses <see cref="Update"/>) and gives the verdict.</summary>
    public static Outlook Finish(Outlook o)
    {
        if (o.Playing == null) return o;
        Task<RunResult> task = o.Playing;
        o.Playing = null;
        s_pending.Remove(o);
        RunResult r;
        try { r = task.Result; }
        catch (Exception e)
        {
            Exception inner = e.InnerException ?? e;
            Log($"{o.Enemy.DisplayName.Text}: the kills could not be played out: {inner.GetType().Name}: {inner.Message} "
                + string.Join(" / ", (inner.StackTrace ?? "").Split('\n').Take(3).Select(l => l.Trim())));
            o.Fight = null;
            o.Verdict = Verdict.Unknown;
            o.Reason = "unknown";
            o.Why = "The advisor could not work this fight out.";
            return o;
        }
        Fight fight = o.Fight!;
        int done = Math.Max(1, r.Kills);
        fight.DamagePerKill = (double)r.DamageTaken / done;
        fight.FoodPerKill = (double)r.Food / done;
        fight.SecondsPerKill = r.Seconds / done;
        fight.AttacksPerKill = r.Attacks / (double)done;
        fight.AmmoPerKill = (double)r.Ammo / done;
        fight.Runes = fight.RunesPerAttack.Select(x => (x.Rune, x.PerAttack * r.Attacks / done)).ToList();
        fight.XpPerKill = r.Xp.ToDictionary(kv => kv.Key, kv => kv.Value / done);
        if (r.Withdrawn)
        {
            o.Verdict = Verdict.Risky;
            o.Reason = r.Why == "Out of food." ? "noFood" : "cannotWin";
            o.Problem = r.Why == "Out of food." ? "No food selected: this monster hurts you, and nothing heals you."
                : o.Area.Darkness > 0 && fight.HitChance < 0.05 ? $"It is too dark there to hit {o.Enemy.DisplayName.Text}: you need light (a lantern, glowing food...)."
                : $"You cannot hurt {o.Enemy.DisplayName.Text} fast enough to beat it.";
            o.Why = o.Problem;
            o.Fight = null;
            return o;
        }
        Judge(o);
        if (r.Defeated && o.Verdict == Verdict.Safe)
        {
            o.Verdict = Verdict.Risky;
            o.Reason = "defeated";
            o.Why = $"In {PlayedKills:N0} test kills, its special attacks and effects knocked you out at least once.";
        }
        return o;
    }

    /// <summary>The verdict from the numbers.</summary>
    static void Judge(Outlook outlook)
    {
        Fight fight = outlook.Fight!;
        Enemy enemy = outlook.Enemy;
        if (fight.EnemyMaxHit >= fight.MaxHp)
        {
            outlook.Verdict = Verdict.Deadly;
            outlook.Reason = "deadly";
            outlook.Why = $"One hit of {enemy.DisplayName.Text} can do {fight.EnemyMaxHit}: more than all your HP ({fight.MaxHp}).";
        }
        else if (!fight.Safe)
        {
            outlook.Verdict = Verdict.Risky;
            outlook.Reason = "overEat";
            outlook.Why = $"One hit ({fight.EnemyMaxHit}) can take you from just above your auto-eat point ({fight.EatAt} HP) to 0.";
        }
        else if (fight.DamagePerKill > 0 && !outlook.HasFood)
        {
            outlook.Verdict = Verdict.Risky;
            outlook.Reason = "noFood";
            outlook.Problem = "No food selected: this monster hurts you, and nothing heals you.";
            outlook.Why = outlook.Problem;
        }
        else
        {
            outlook.Verdict = Verdict.Safe;
            outlook.Reason = fight.DamagePerKill <= 0 ? "harmless" : "safe";
            outlook.Why = fight.DamagePerKill <= 0
                ? $"{enemy.DisplayName.Text} cannot hurt you with this setup."
                : $"Its biggest hit ({fight.EnemyMaxHit}) is smaller than your auto-eat point ({fight.EatAt} HP).";
        }
    }

    /// <summary>The model's words were written for adventurers: say them to the player.</summary>
    static string Plain(string text) => text
        .Replace("the adventurer only eats", "you only eat").Replace("The adventurer cannot", "You cannot").Replace("the adventurer cannot", "you cannot")
        .Replace("the adventurer needs", "you need").Replace("The adventurer needs", "You need").Replace("adventurers cannot use those yet", "the advisor cannot work those out yet")
        .Replace("adventurers cannot cast those yet", "the advisor cannot work those out yet").Replace("all of their", "all of your")
        .Replace("they need light", "you need light").Replace("they cannot hurt", "you cannot hurt").Replace("can knock them out", "can knock you out")
        .Replace("Choose a spell for them to cast.", "Choose a spell to cast.").Replace("for the adventurer to hit", "to hit")
        .Replace("The weapon cannot", "Your weapon cannot").Replace("The weapon needs", "Your weapon needs");

    /// <summary>
    /// The player's bonuses in a private copy, set up for a fight against <paramref name="enemy"/> in
    /// <paramref name="area"/> (what CombatActionComponent and PlayerComponent would have on in that fight).
    /// </summary>
    /// <summary>
    /// The XP as the game gives it (CombatExperienceStats / ExperienceStats.XpToAdd): the XP per damage of the stance
    /// with the player's own XP bonuses for each skill on top, (base + flat) x (1 + percent), for every hit and for the
    /// kill's bonus. The fight model only knows the stance's plain XP per damage.
    /// </summary>
    static void GameXp(Game game, ModifierSystem mods, Setup setup, Fight fight)
    {
        if (Combat(game) is not { } combat) return;
        GamesByMalcs.Melvor2.Components.CombatExperience.CombatExperienceStats? stats = null;
        try
        {
            stats = new GamesByMalcs.Melvor2.Components.CombatExperience.CombatExperienceStats(mods, combat.ExperienceGain, setup.Fighter.Stance, 1, 0, 0);
            // per skill: what one point of damage gives (D = (per + flat)(1 + pct)) and the bonus with no armour or ward (K = flat (1 + pct))
            var perDamage = new Dictionary<object, double>();
            var onKill = new Dictionary<object, double>();
            foreach (var s in stats.ExperienceStats.List) perDamage[s.ExperienceComponent.Value] = s.ExperienceStats.XpToAdd.Value;
            foreach (var s in stats.BonusKilledExperience.List) onKill[s.ExperienceComponent.Value] = s.ExperienceStats.XpToAdd.Value;
            var perKill = new Dictionary<CombatLevel, double>();
            foreach (var (weight, duel) in fight.Duels)
            {
                var xp = new List<(CombatLevel Level, double PerDamage, double OnKill)>();
                foreach (var (level, per, kill) in duel.Xp)
                {
                    double d = perDamage.TryGetValue(level.Component, out double v) ? v : per;
                    double k0 = onKill.TryGetValue(level.Component, out double w) ? w : 0;
                    double times = per > 0 ? (d - k0) / per : 1;              // (1 + pct)
                    double newKill = kill * times + k0;                        // (kill + flat)(1 + pct)
                    xp.Add((level, d, newKill));
                    perKill[level] = perKill.GetValueOrDefault(level) + weight * (d * duel.EnemyHp + newKill);
                }
                duel.Xp.Clear();
                duel.Xp.AddRange(xp);
            }
            fight.XpPerKill = perKill;
        }
        catch (Exception e) { Log("The XP bonuses could not be read: " + e.Message); }
        finally { try { stats?.Destroy(); } catch { } }
    }

    static ModifierSystem Copy(Game game, Setup setup, BaseCombatArea area, Enemy enemy)
    {
        var data = game.Data;
        var copy = new ModifierSystem(data.StatClasses);
        copy.OnDataLoaded();
        ModifierSystem live = GetModifiers(game);
        if (s_values?.GetValue(live) is Dictionary<INamedObject, Dictionary<ModifierValues, double>> all)
            foreach (var (source, values) in all)
            {
                if (OfTheFight(source)) continue;
                if (setup.Previewing && source is AttackStance) continue;   // the previewed stance replaces the player's
                foreach (var (v, m) in values) copy.Add(source, v, m);
            }
        CombatEntity combat = Combat(game)!;
        AttackStance stance = setup.Fighter.Stance;
        if (setup.Previewing && stance.Bonuses != null) copy.Add(stance, stance.Bonuses);
        // the combat triangle and the elements against this monster (CombatActionComponent, while fighting)
        AttackStance theirs = enemy.Stances.OrderByDescending(s => s.Weight).First().Stance;
        foreach (var bonus in combat.Action.GetCombatTriangleBonuses(stance.CombatStyle, theirs.CombatStyle, true)) Add(copy, bonus, bonus.Bonuses);
        var weapon = Adventurers.Combat.Weapon(setup.Fighter.Gear);
        Element mine = setup.Fighter.Spell != null && stance.CombatStyle.UsesSpells ? setup.Fighter.Spell.Element
            : weapon != null && weapon.Value.Weapon.Elements.Count > 0 ? weapon.Value.Weapon.Elements[0]
            : data.GetDataRegistryOutput<Element>().GetObjectById("MelvorBase:Neutral");
        foreach (var bonus in combat.Action.GetElementalBonuses(mine, enemy.Element, true)) Add(copy, bonus, bonus.Bonuses);
        // the area's category, the monster's and the area's passives (CombatAreasComponent, EnemyComponent)
        Add(copy, area.Category, area.Category.Bonuses);
        foreach (CombatPassive passive in enemy.Passives.Concat(area.Passives).Distinct()) Add(copy, passive, passive.Modifiers);
        if (area.Effect != null)
        {
            var areaStats = new CombatAreasStats(copy, combat.Areas, area);
            int magnitude;
            try { magnitude = areaStats.AreaEffectMagnitude.Value; }
            finally { try { areaStats.Destroy(); } catch { } }
            if (magnitude > 0) copy.Add(area, area.Effect.Modifiers, magnitude);
        }
        return copy;
    }

    static void Add(ModifierSystem mods, INamedObject source, ModifierValues? values)
    {
        if (values != null) mods.Add(source, values);
    }

    /// <summary>Bonuses that belong to the fight going on, not to the player.</summary>
    static bool OfTheFight(INamedObject source) =>
        source is CombatTriangleBonus or ElementalBonus or CombatAreaCategory or CombatPassive or BaseCombatArea
        || (source.GetType().Namespace?.Contains("CombatEffects") ?? false);
}
