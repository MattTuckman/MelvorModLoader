using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Components.Characters.Conditions;
using GamesByMalcs.Melvor2.Components.CombatEffects;
using GamesByMalcs.Melvor2.Components.Equipment;
using GamesByMalcs.Melvor2.Components.Experience;
using GamesByMalcs.Melvor2.Components.ItemUpgrading;
using GamesByMalcs.Melvor2.Conditions;
using GamesByMalcs.Melvor2.Core;
using GamesByMalcs.Melvor2.Data;
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.Entities;
using GamesByMalcs.Melvor2.Entities.Astrology;
using GamesByMalcs.Melvor2.Entities.Combat;
using GamesByMalcs.Melvor2.Entities.Combat.Goals;
using GamesByMalcs.Melvor2.Entities.Hunting;
using GamesByMalcs.Melvor2.Entities.Quests;
using GamesByMalcs.Melvor2.Entities.Shop;
using GamesByMalcs.Melvor2.Entities.Thieving;
using GamesByMalcs.Melvor2.Goals;
using GamesByMalcs.Melvor2.Localization;
using GamesByMalcs.Melvor2.Modifiers;

namespace OneStyle;

/// <summary>
/// Everything the chosen class changes, all of it in memory only (the game builds its data afresh for every opened
/// character, and nothing here is written into the save). Every change keeps what it replaced, so switching the
/// class, or to Off, puts the game back exactly as it was.
///
/// 1. Immunity: monsters immune to the class's style (Wind Drifter, the Amalgamations, Blood Sanctum) can be hit.
/// 2. Combat triangle: off for the class's style, against every monster (no penalty and no bonus: every class is
///    equal against every monster). 7 to 12 are in Patches.Balance.cs.
/// 3. Style locks: the archery targets of the first main quest (and any quest goal or monster that asks for another
///    style) take the class's style instead.
/// 4. Level locks: requirements on another style's level read the class's level instead: Battle Loadout, thieving
///    distractions, the four combat constellations, item upgrades, Hunting by hand, rings and amulets, and (Ranged)
///    the crossbows and throwing hammers that also ask for Strength. Other gear keeps its own requirements.
/// 5. Magic evasion: melee and ranged dodge spells with their full Defence (the game counts Magic for 70% of it).
/// 6. Class tiers: titles and bonuses at class level 20, 40, 60, 80 and 99.
/// </summary>
public static partial class Patches
{
    internal sealed class Source(string name) : INamedObject
    {
        public ILocalizedString DisplayName { get; } = new LocalizedString(name);
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Modifiers")]
    static extern ModifierSystem GetModifiers(Game game);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_Style")]
    static extern void SetStyle(UsingCombatStyleCondition condition, CombatStyle style);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_PlayerCombatStyle")]
    static extern void SetGoalStyle(KillEnemyGoal goal, CombatStyle? style);

    const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

    // A condition that is being listened to keeps its handler on the old skill's LevelChanged and caches whether it is
    // met. Pointing it at another skill means: handler off the old skill, onto the new one, then work it out again.
    static readonly Type s_levelEvent = typeof(EventCondition<ValueChangedEventArgs<int>>);
    static readonly FieldInfo? s_levelListening = s_levelEvent.GetField("_isListening", Hidden);
    static readonly MethodInfo? s_levelProgress = s_levelEvent.GetMethod("OnProgressEvent", Hidden);
    static readonly MethodInfo? s_levelUpdate = s_levelEvent.GetMethod("UpdateIsMet", Hidden);
    static readonly FieldInfo? s_levelComponent = typeof(ComponentCondition<ExperienceComponent, ValueChangedEventArgs<int>>)
        .GetField("<Component>k__BackingField", Hidden);
    static readonly Type s_styleEvent = typeof(EventCondition<ValueChangedEventArgs<CombatStyle>>);
    static readonly FieldInfo? s_styleListening = s_styleEvent.GetField("_isListening", Hidden);
    static readonly MethodInfo? s_styleUpdate = s_styleEvent.GetMethod("UpdateIsMet", Hidden);
    static readonly FieldInfo? s_triangleLookup = typeof(CombatActionComponent).GetField("_combatTriangleLookup", Hidden);
    static readonly MethodInfo? s_triangleUpdate = typeof(CombatActionComponent).GetMethod("UpdateCombatTriangle", Hidden);

    public static Action<string> Log = _ => { };
    public static event Action? Changed;

    static Game? s_game;
    static ModifierSystem? s_mods;
    static Class s_class;
    public static Class Current => s_class;
    public static bool Bound => s_game != null;

    /// <summary>What could not be done with this game version (shown in the window and the log).</summary>
    public static List<string> Problems { get; } = new();

    // ---- what was changed, to put it back
    static readonly List<(LevelCondition Condition, ExperienceComponent Original)> s_levels = new();
    static readonly List<LevelCondition> s_toMelee = new();
    static readonly List<(UsingCombatStyleCondition Condition, CombatStyle Original)> s_styles = new();
    static readonly List<(KillEnemyGoal Goal, CombatStyle? Original)> s_goals = new();
    static Dictionary<CombatStyle, List<CombatTriangleBonus>>? s_triangleRow;
    static Dictionary<CombatStyle, List<CombatTriangleBonus>>? s_triangleOriginal;
    static readonly Source s_immunity = new("One Style: no immunity to your class");
    static readonly Source s_evasion = new("One Style: Defence dodges spells");
    static readonly Source s_perks = new("One Style: class tier");
    static ModifierValues? s_evasionValues, s_perkValues;
    static double s_evasionNow, s_perkNow;
    readonly record struct Watched(ExperienceComponent Skill, EventHandler<ValueChangedEventArgs<int>> Handler);
    static readonly List<Watched> s_watched = new();

    // ---- counts, for the window and the tests
    public static int MappedLevels => s_levels.Count;
    public static int MappedStyles => s_styles.Count + s_goals.Count;
    public static int TrianglePenaltiesRemoved { get; private set; }
    public static int TriangleBonusesRemoved { get; private set; }

    /// <summary>A character was opened: its class is applied (before the game works out the time it was closed).</summary>
    public static void Bind(Game game)
    {
        if (ReferenceEquals(s_game, game)) return;
        lock (s_lock)
        {
            Forget();
            s_game = game;
            s_mods = GetModifiers(game);
            // The save's key is read once, here: on the way back to the main menu the game forgets it before the
            // mod notices, and a click then would be filed under the character's name.
            s_key = Store.KeyOf(game);
            s_class = Store.Load(game, s_key);
            Apply();
            // Monsters are watched whatever the class: elites appear with Off too, and the weak spot comes after them.
            Step("monsters", () => WatchEnemy());
            Step("elites", () => Elites.Bind(game, s_key, s_mods));
        }
        Log($"'{game.CharacterName}': class {Classes.Name(s_class)}" + (s_class == Class.None ? "." : $", {s_levels.Count} level lock(s) and {MappedStyles} style lock(s) follow it."));
        Changed?.Invoke();
    }

    static string s_key = "";

    /// <summary>Back to the main menu: that game and its data are gone, so there is nothing to put back.</summary>
    public static void Unbind()
    {
        if (s_game == null) return;
        lock (s_lock)
        {
            Elites.Unbind();
            Forget();
            s_game = null;
            s_mods = null;
            s_class = Class.None;
            s_key = "";
        }
        Changed?.Invoke();
    }

    /// <summary>The player picked a class (or Off). Takes effect at once.</summary>
    public static void Set(Class c)
    {
        if (s_game == null || c == s_class || MelvorModLoader.GameAccess.CurrentGame != s_game) return;
        lock (s_lock)
        {
            Restore();
            s_class = c;
            Store.Save(s_key, c);
            Apply();
        }
        RefreshViews();
        Log($"Class set to {Classes.Name(c)}.");
        Changed?.Invoke();
    }

    /// <summary>
    /// The combat page shows the triangle's colour and only works it out again when a style changes: after a class
    /// change in the middle of a fight, it is asked once more (the game's own method, found by name; skipped if gone).
    /// </summary>
    static void RefreshViews()
    {
        try
        {
            if (Godot.Engine.GetMainLoop() is not Godot.SceneTree tree) return;
            var stack = new Stack<Godot.Node>();
            stack.Push(tree.Root);
            while (stack.Count > 0)
            {
                Godot.Node n = stack.Pop();
                if (n is OffensiveStatsContainer stats) typeof(OffensiveStatsContainer).GetMethod("UpdateCombatStyleLabelStyle", Hidden)?.Invoke(stats, null);
                foreach (Godot.Node child in n.GetChildren()) stack.Push(child);
            }
        }
        catch (Exception e) { Log("One Style: the combat page could not be refreshed: " + e.Message); }
    }

    // ================================================================ the class level and tier

    static ExperienceComponent? Skill(string id) =>
        s_game?.LeveledEntities.FirstOrDefault(s => s.Experience.Entity.Id == id)?.Experience;

    /// <summary>The class's level: Ranged or Magic, for melee the higher of Attack and Strength.</summary>
    public static int ClassLevel => s_class switch
    {
        Class.Melee => Math.Max(Skill("MelvorBase:Attack")?.Level ?? 1, Skill("MelvorBase:Strength")?.Level ?? 1),
        Class.Ranged => Skill("MelvorBase:Ranged")?.Level ?? 1,
        Class.Magic => Skill("MelvorBase:Magic")?.Level ?? 1,
        _ => 0,
    };

    public static int Tier => s_class == Class.None ? 0 : Classes.Tier(ClassLevel);

    /// <summary>The skill that stands in for other styles' levels: for melee the higher of Attack and Strength.</summary>
    static ExperienceComponent? StandIn() => s_class switch
    {
        Class.Melee => new[] { Skill("MelvorBase:Attack"), Skill("MelvorBase:Strength") }.Where(s => s != null).OrderByDescending(s => s!.Level).FirstOrDefault(),
        Class.Ranged => Skill("MelvorBase:Ranged"),
        Class.Magic => Skill("MelvorBase:Magic"),
        _ => null,
    };

    // ================================================================ applying and putting back

    static void Apply()
    {
        Problems.Clear();
        TrianglePenaltiesRemoved = TriangleBonusesRemoved = 0;
        if (s_game == null || s_class == Class.None) return;
        CombatStyle? style = Try("combat style", () => s_game.Data.GetObject<CombatStyle>(Classes.StyleId(s_class)));
        if (style == null) return;
        Step("immunity", () => AddImmunity());
        Step("combat triangle", () => FixTriangle(style));
        Step("style locks", () => MapStyles(style));
        Step("level locks", () => MapLevels());
        Step("hunting by hand", () => RestoreTrapless());
        Step("magic evasion", () => UpdateEvasion());
        Step("class tier", () => UpdatePerks());
        ApplyBalance(style);
        Watch();
    }

    /// <summary>Everything back the way the game had it.</summary>
    static void Restore()
    {
        Unwatch();
        foreach ((LevelCondition c, ExperienceComponent original) in s_levels) Try("a level lock", () => { Point(c, original); return true; });
        s_levels.Clear();
        s_toMelee.Clear();
        foreach ((UsingCombatStyleCondition c, CombatStyle original) in s_styles) Try("a style lock", () => { Restyle(c, original); return true; });
        s_styles.Clear();
        foreach ((KillEnemyGoal g, CombatStyle? original) in s_goals) Try("a quest goal", () => { SetGoalStyle(g, original); return true; });
        s_goals.Clear();
        if (s_triangleRow != null && s_triangleOriginal != null)
        {
            foreach (var (enemy, list) in s_triangleOriginal) s_triangleRow[enemy] = list;
            Try("combat triangle", () => { RefreshTriangle(); return true; });
        }
        s_triangleRow = null;
        s_triangleOriginal = null;
        if (s_mods != null)
            foreach (Source source in new[] { s_immunity, s_evasion, s_perks })
                try { s_mods.Remove(source); } catch { }
        s_evasionNow = s_perkNow = 0;
        s_evasionValues = s_perkValues = null;
        TrianglePenaltiesRemoved = TriangleBonusesRemoved = 0;
        RestoreBalance();
    }

    /// <summary>Drops the records without touching the game (its data is gone with it).</summary>
    static void Forget()
    {
        Unwatch();
        s_levels.Clear();
        s_toMelee.Clear();
        s_styles.Clear();
        s_goals.Clear();
        s_triangleRow = null;
        s_triangleOriginal = null;
        s_evasionNow = s_perkNow = 0;
        s_evasionValues = s_perkValues = null;
        TrianglePenaltiesRemoved = TriangleBonusesRemoved = 0;
        ForgetBalance();
        Problems.Clear();
    }

    static void Step(string what, Action action)
    {
        try { action(); }
        catch (Exception e)
        {
            Problems.Add($"{what}: {(e.InnerException ?? e).Message}");
            Log($"One Style: {what} could not be applied ({(e.InnerException ?? e).Message}); the rest still works.");
        }
    }

    static T? Try<T>(string what, Func<T> f)
    {
        try { return f(); }
        catch (Exception e)
        {
            Problems.Add($"{what}: {(e.InnerException ?? e).Message}");
            Log($"One Style: {what} failed: {(e.InnerException ?? e).Message}");
            return default;
        }
    }

    // ================================================================ 1. immunity

    static void AddImmunity()
    {
        // The game asks the enemy's ImmunityFrom with the attacker's style in the context: -100 for this style only.
        var value = Value("Target", "ImmunityFrom", -100, "Enemy", ("CombatStyle", Classes.StyleId(s_class)));
        s_mods!.Add(s_immunity, Values(value));
    }

    // ================================================================ 2. combat triangle

    static void FixTriangle(CombatStyle style)
    {
        var combat = (CombatEntity)s_game!.Entities.GetObjectById("MelvorBase:Combat");
        if (s_triangleLookup?.GetValue(combat.Action) is not Dictionary<CombatStyle, Dictionary<CombatStyle, List<CombatTriangleBonus>>> lookup)
            throw new InvalidOperationException("the game's combat triangle table was not found");
        if (!lookup.TryGetValue(style, out var row)) return;
        s_triangleRow = row;
        s_triangleOriginal = new Dictionary<CombatStyle, List<CombatTriangleBonus>>(row);
        // A new list for every enemy style (never the same list changed in place): the game compares the list it
        // applied with the one it finds, takes the old bonuses off and puts the new ones on.
        // Since 0.2 every matchup of the class is taken out of the table, the good ones too: the classes are equal
        // against every monster. A missing matchup is neutral (the game shows an empty one as a red penalty badge).
        // (0.1 kept the good ones, which left Magic favoured against 78 monsters, Melee against 59, Ranged against 55.)
        foreach (CombatStyle enemy in row.Keys.ToList())
        {
            TrianglePenaltiesRemoved += row[enemy].Count(b => !b.IsPositiveForPlayer);
            TriangleBonusesRemoved += row[enemy].Count(b => b.IsPositiveForPlayer);
            row.Remove(enemy);
        }
        RefreshTriangle();
    }

    static void RefreshTriangle()
    {
        var combat = (CombatEntity)s_game!.Entities.GetObjectById("MelvorBase:Combat");
        s_triangleUpdate?.Invoke(combat.Action, null);
    }

    // ================================================================ 3. style locks

    static void MapStyles(CombatStyle style)
    {
        // Monsters that only let one style fight them (the archery targets of the first main quest).
        foreach (Enemy enemy in s_game!.Data.GetDataRegistryOutput<Enemy>().GetAllObjects())
        {
            MapStyleGroup(enemy.Requirements, style);
            MapStyleGroup(enemy.DisplayRequirements, style);
        }
        // Quest goals that ask for a style ("hit the targets with Ranged"). Mastery Guild tasks are left alone.
        foreach (Quest quest in s_game.Data.GetDataRegistryOutput<Quest>().GetAllObjects())
        {
            MapStyleGroup(quest.Requirements, style);
            foreach (QuestStep step in quest.Steps)
                foreach (QuestTask task in step.TasksInOrder)
                {
                    MapStyleGroup(task.ConditionGoals, style);
                    foreach (Goal goal in task.Goals)
                        if (goal is KillEnemyGoal kill && kill.PlayerCombatStyle != null && kill.PlayerCombatStyle != style && kill.PlayerStance == null)
                        {
                            s_goals.Add((kill, kill.PlayerCombatStyle));
                            SetGoalStyle(kill, style);
                        }
                }
        }
    }

    static void MapStyleGroup(ConditionGroup? group, CombatStyle style)
    {
        if (group == null) return;
        foreach (Condition c in group.Conditions)
            // Only what the player must use. "The enemy uses Ranged" describes the monster and stays as it is.
            if (c is UsingCombatStyleCondition u && !u.Component.IsEnemy && u.Style != style)
            {
                s_styles.Add((u, u.Style));
                Restyle(u, style);
            }
    }

    static void Restyle(UsingCombatStyleCondition c, CombatStyle style)
    {
        SetStyle(c, style);
        if (s_styleListening?.GetValue(c) is true)
        {
            s_styleUpdate?.Invoke(c, null);
            Tell(c, s_styleProgressed);
        }
    }

    // ================================================================ 4. level locks

    static void MapLevels()
    {
        ExperienceComponent? to = StandIn();
        if (to == null) throw new InvalidOperationException("the class's skill was not found");
        Game game = s_game!;
        foreach (ShopPurchase p in game.Data.GetDataRegistryOutput<ShopPurchase>().GetAllObjects()) { MapGroup(p.Requirements, to); MapGroup(p.DisplayRequirements, to); }
        foreach (ThievingDistraction d in game.Data.GetDataRegistryOutput<ThievingDistraction>().GetAllObjects()) MapGroup(d.Requirements, to);
        foreach (AstrologyNode n in game.Data.GetDataRegistryOutput<AstrologyNode>().GetAllObjects()) MapGroup(n.UnlockRequirements, to);
        foreach (AstrologyConstellation c in game.Data.GetDataRegistryOutput<AstrologyConstellation>().GetAllObjects())
        {
            MapGroup(c.Requirements, to);
            MapGroup(c.SecondaryRequirements, to);
            MapGroup(c.DisplayRequirements, to);
        }
        foreach (ItemUpgrade u in game.Data.GetDataRegistryOutput<ItemUpgrade>().GetAllObjects()) { MapGroup(u.Requirements, to); MapGroup(u.DisplayRequirements, to); }
        foreach (HuntingAnimal a in game.Data.GetDataRegistryOutput<HuntingAnimal>().GetAllObjects()) MapGroup(a.TraplessRequirements, to);
        foreach (Item item in game.Data.GetDataRegistryOutput<Item>().GetAllObjects())
            if (item.Capabilities.TryGet(out EquipmentCapability? gear) && gear != null && SharedGear(gear))
                MapGroup(gear.Requirements, to);
    }

    /// <summary>
    /// Gear whose other-style requirements follow the class: rings and amulets (every class wears them), and for
    /// Ranged the crossbows and throwing hammers that ask for Strength besides Ranged. Weapons and armour of another
    /// style keep their requirements, so a class stays a class.
    /// </summary>
    static bool SharedGear(EquipmentCapability gear)
    {
        string type = gear.Type.Id;
        if (type is "MelvorBase:Ring" or "MelvorBase:Amulet") return true;
        if (s_class != Class.Ranged) return false;
        var skills = gear.Requirements.Conditions.OfType<LevelCondition>().Select(c => c.Component.Entity.Id).ToList();
        return skills.Contains("MelvorBase:Ranged") && skills.Contains("MelvorBase:Strength");
    }

    static void MapGroup(ConditionGroup? group, ExperienceComponent to)
    {
        if (group == null) return;
        string[] own = Classes.OwnSkills[s_class];
        foreach (Condition c in group.Conditions)
            if (c is LevelCondition level && Classes.Offensive.Contains(level.Component.Entity.Id) && !own.Contains(level.Component.Entity.Id))
            {
                s_levels.Add((level, level.Component));
                if (s_class == Class.Melee) s_toMelee.Add(level);
                Point(level, to);
            }
    }

    /// <summary>Points a level requirement at another skill, keeping the game's own listening intact.</summary>
    static void Point(LevelCondition c, ExperienceComponent to)
    {
        ExperienceComponent from = c.Component;
        if (ReferenceEquals(from, to)) return;
        if (s_levelComponent == null || s_levelListening == null || s_levelProgress == null || s_levelUpdate == null)
            throw new InvalidOperationException("the game's level requirement has changed shape");
        // Under the condition's own lock, as the game's own (un)subscribing is.
        var conditionLock = s_levelLock?.GetValue(c) as System.Threading.Lock;
        conditionLock?.Enter();
        bool listening;
        try
        {
            listening = s_levelListening.GetValue(c) is true;
            var handler = (EventHandler<ValueChangedEventArgs<int>>)Delegate.CreateDelegate(typeof(EventHandler<ValueChangedEventArgs<int>>), c, s_levelProgress);
            if (listening) from.LevelChanged -= handler;
            s_levelComponent.SetValue(c, to);
            if (listening)
            {
                to.LevelChanged += handler;
                s_levelUpdate.Invoke(c, null);
            }
        }
        finally { conditionLock?.Exit(); }
        if (listening) Tell(c, s_levelProgressed);
    }

    static readonly FieldInfo? s_levelLock = s_levelEvent.GetField("_objectLock", Hidden);
    static readonly FieldInfo? s_levelProgressed = s_levelEvent.GetField("InternalConditionProgressed", Hidden);
    static readonly FieldInfo? s_styleProgressed = s_styleEvent.GetField("InternalConditionProgressed", Hidden);

    /// <summary>
    /// The game only tells the pages showing a requirement when it changes from met to not met (or progresses). A
    /// requirement that stays met but now names another skill would keep showing the old name: tell them once.
    /// </summary>
    static void Tell(object condition, FieldInfo? progressed)
    {
        try { (progressed?.GetValue(condition) as EventHandler)?.Invoke(condition, EventArgs.Empty); }
        catch (Exception e) { Log("One Style: a requirement on screen could not be refreshed: " + e.Message); }
    }

    // ================================================================ 4b. Hunting by hand after loading

    /// <summary>
    /// A hunt done by hand is switched on again while the save loads, before this mod has pointed its Strength
    /// requirement at the class: the game then keeps it switched on but leaves its bonus off (and only reacts when
    /// the requirement stops being met). Put the bonus on for those (the game's own call, which never adds it twice).
    /// </summary>
    /// <summary>For tests: hunts by hand whose bonus was put back.</summary>
    public static int TraplessRestored { get; private set; }

    static void RestoreTrapless()
    {
        TraplessRestored = 0;
        if (!s_game!.Entities.TryGetObjectById("MelvorBase:Hunting", out GameEntity? hunting) || hunting == null) return;
        MethodInfo? add = typeof(ActiveHuntingAnimal).GetMethod("AddTraplessBonus", Hidden);
        foreach (HuntingActionComponent c in hunting.Components.Values.OfType<HuntingActionComponent>())
            foreach (ActiveHuntingAnimal a in c.Locations.Values.SelectMany(l => l.Animals))
                if (a.TraplessEnabled && a.Animal.TraplessRequirements is { AreMet: true } && s_levels.Any(l => a.Animal.TraplessRequirements.Conditions.Contains(l.Condition)))
                {
                    add?.Invoke(a, null);
                    if (add != null) TraplessRestored++;
                }
    }

    // ================================================================ 5. magic evasion

    /// <summary>
    /// Against spells the game counts Defence for 30% and Magic for 70% of the evasion. While the class's style is in
    /// use, the player's Magic counts as high as Defence for that (a level bonus with the class's style in its
    /// context: Magic's accuracy and damage are not used by melee or ranged, and no XP or requirement changes).
    /// </summary>
    static void UpdateEvasion()
    {
        if (s_mods == null || s_class is not (Class.Melee or Class.Ranged)) return;
        int defence = Skill("MelvorBase:Defence")?.Level ?? 1, magic = Skill("MelvorBase:Magic")?.Level ?? 1;
        double want = Math.Max(0, defence - magic);
        s_evasionValues ??= Values(Value("Character", "FlatCombatLevel", 1, "Player", ("CombatLevel", "MelvorBase:Magic"), ("CombatStyle", Classes.StyleId(s_class))));
        SetStrength(s_evasion, s_evasionValues, ref s_evasionNow, want);
    }

    // ================================================================ 6. class tiers

    static ComponentModifierValue[] PerkValues(Class c)
    {
        string style = Classes.StyleId(c);
        return c switch
        {
            Class.Melee => new[]
            {
                Value("Character", "PercentMaxHit", 0.03, "Player", ("CombatStyle", style)),
                Value("Character", "MinHitBasedOnMaxHit", 0.04, "Player", ("CombatStyle", style)),
                Value("CombatAction", "PercentSpawnInterval", -0.05, "Action"),
            },
            Class.Ranged => new[]
            {
                Value("AmmoConsumption", "AmmoPreservationChance", 0.08, "Player"),
                Value("Character", "PercentAccuracyRating", 0.04, "Player", ("CombatStyle", style)),
                Value("Character", "PercentAttackInterval", -0.02, "Player", ("CombatStyle", style)),
            },
            Class.Magic => new[]
            {
                Value("MagicSpell", "RunePreservationChance", 0.08, "MagicSpells"),
                Value("Character", "PercentAccuracyRating", 0.04, "Player", ("CombatStyle", style)),
                Value("Character", "PercentMaxHit", 0.03, "Player", ("CombatStyle", style)),
            },
            _ => Array.Empty<ComponentModifierValue>(),
        };
    }

    static void UpdatePerks()
    {
        if (s_mods == null || s_class == Class.None) return;
        s_perkValues ??= Values(PerkValues(s_class));
        SetStrength(s_perks, s_perkValues, ref s_perkNow, Tier);
    }

    static void SetStrength(Source source, ModifierValues values, ref double now, double want)
    {
        if (s_mods == null || Math.Abs(want - now) < 1e-9) return;
        if (want == 0) s_mods.Remove(source);
        else if (now == 0) s_mods.Add(source, values, want);
        else s_mods.Update(source, values, want);
        now = want;
    }

    // ================================================================ following the levels

    static readonly object s_lock = new();

    static void Watch()
    {
        Unwatch();
        foreach (string id in new[] { "MelvorBase:Attack", "MelvorBase:Strength", "MelvorBase:Ranged", "MelvorBase:Magic", "MelvorBase:Defence" })
            if (Skill(id) is { } skill)
            {
                EventHandler<ValueChangedEventArgs<int>> handler = (_, _) => LevelChanged();
                skill.LevelChanged += handler;
                s_watched.Add(new Watched(skill, handler));
            }
    }

    static void Unwatch()
    {
        foreach (Watched w in s_watched) w.Skill.LevelChanged -= w.Handler;
        s_watched.Clear();
    }

    /// <summary>A combat level changed (also while the game works out the time it was closed, on its own thread).</summary>
    static void LevelChanged()
    {
        lock (s_lock)
        {
            if (s_game == null || s_class == Class.None) return;
            if (s_class == Class.Melee && StandIn() is { } best)
                foreach (LevelCondition c in s_toMelee)
                    try { Point(c, best); } catch (Exception e) { Log("One Style: a level lock could not follow: " + e.Message); }
            try { UpdateEvasion(); UpdatePerks(); } catch (Exception e) { Log("One Style: a bonus could not follow the level: " + e.Message); }
        }
        // No Changed here: this may run on the game's offline thread. The window follows the levels on its own.
    }

    // ================================================================ bonuses as the game's own modifier values

    static ComponentModifierValue Value(string statsType, string name, double value, string component, params (string Key, string Value)[] context)
    {
        var local = new Dictionary<string, object?> { ["Component"] = new Dictionary<string, string> { ["Entity"] = "MelvorBase:Combat", ["Component"] = component } };
        foreach (var (key, v) in context) local[key] = v;
        var json = new Dictionary<string, object?>
        {
            ["Stat"] = new Dictionary<string, string> { ["StatsType"] = "MelvorBase:" + statsType, ["Name"] = name },
            ["Value"] = value,
            ["LocalContext"] = local,
            ["FromContext"] = null,
            ["Condition"] = null,
            ["Scaling"] = null,
        };
        using JsonDocument doc = JsonDocument.Parse(JsonSerializer.Serialize(json));
        return new ComponentModifierValue(s_game!.Data, doc.RootElement.Clone());
    }

    static ModifierValues Values(params ComponentModifierValue[] values) =>
        new(new List<GlobalModifierValue>(), values.ToList(), new List<CombatEffectModifierValue>());

    // ================================================================ for the window and the tests

    /// <summary>The bonuses of a tier as text, with the numbers of that tier.</summary>
    public static IEnumerable<string> PerkLines(Class c, int tier)
    {
        if (tier <= 0) yield break;
        foreach (string line in Classes.Perks(c))
        {
            // "+3% max hit" at tier 2 -> "+6% max hit"
            int end = line.IndexOf('%');
            if (end > 1 && double.TryParse(line[1..end], NumberStyles.Float, CultureInfo.InvariantCulture, out double per))
                yield return line[0] + (per * tier).ToString("0.##", CultureInfo.InvariantCulture) + line[end..];
            else yield return line;
        }
    }

    /// <summary>For tests: the skill a mapped level requirement reads now.</summary>
    public static string ReadsFrom(LevelCondition c) => c.Component.Entity.Id;
}
