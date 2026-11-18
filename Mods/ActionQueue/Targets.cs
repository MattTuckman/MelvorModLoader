using System;
using System.Collections.Generic;
using System.Linq;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Components.Actions;
using GamesByMalcs.Melvor2.Components.ArtisanAction;
using GamesByMalcs.Melvor2.Components.CombatAreas;
using GamesByMalcs.Melvor2.Components.CraftingAction;
using GamesByMalcs.Melvor2.Components.SlayerTasks;
using GamesByMalcs.Melvor2.Data;
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.Entities;
using GamesByMalcs.Melvor2.Entities.Agility;
using GamesByMalcs.Melvor2.Entities.Astrology;
using GamesByMalcs.Melvor2.Entities.Combat;
using GamesByMalcs.Melvor2.Entities.Cooking;
using GamesByMalcs.Melvor2.Entities.Firemaking;
using GamesByMalcs.Melvor2.Entities.Fishing;
using GamesByMalcs.Melvor2.Entities.Herblore;
using GamesByMalcs.Melvor2.Entities.Hunting;
using GamesByMalcs.Melvor2.Entities.Mining;
using GamesByMalcs.Melvor2.Entities.Slayer;
using GamesByMalcs.Melvor2.Entities.Thieving;
using GamesByMalcs.Melvor2.Entities.Woodcutting;

namespace ActionQueue;

/// <summary>One thing a skill can do: a tree, a rock, a recipe, a monster... Extra is only used by combat (its area).</summary>
public sealed record Target(string Id, string Name, string Extra = "");

/// <summary>
/// Every skill starts its work in its own way. This class knows, for each kind of action, what can be chosen,
/// what is being done right now, and how to start one particular thing - always through the game's own
/// "select" call and only when the game's own requirements for it are met.
/// </summary>
public static class Targets
{
    public const string Dungeon = "dungeon";
    /// <summary>A combat target that is not one fixed monster but "whatever this slayer master's task is".</summary>
    public const string Slayer = "slayer";

    static SlayerEntity? s_slayer;

    static ActiveSlayerMaster? Master(string masterId) =>
        s_slayer?.Tasks.ActiveMasters.FirstOrDefault(m => m.Key.Id == masterId).Value;

    /// <summary>The skills (and combat) the queue can drive, in the game's own order.</summary>
    public static List<(GameEntity Entity, ActionComponent Action)> Skills(Game game)
    {
        var list = new List<(GameEntity, ActionComponent)>();
        s_slayer = game.Entities.TryGetObjectById("MelvorBase:Slayer", out GameEntity? slayer) ? slayer as SlayerEntity : null;
        foreach (GameEntity entity in game.Entities.GetAllObjects())
            foreach (ActionComponent action in entity.Components.Values.OfType<ActionComponent>())
                if (Supported(action)) { list.Add((entity, action)); break; }
        return list;
    }

    public static ActionComponent? Find(Game game, string entityId) =>
        Skills(game).FirstOrDefault(s => s.Entity.Id == entityId).Action;

    static bool Supported(ActionComponent a) => a is WoodcuttingActionComponent or MiningActionComponent or FishingActionComponent
        or ArtisanActionComponent or CookingActionComponent or FiremakingActionComponent or HerbloreActionComponent
        or ThievingActionComponent or AgilityActionComponent or AstrologyActionComponent or HuntingActionComponent or CombatActionComponent;

    static Target Of(IDescribedObject thing) => new(thing.Id, thing.DisplayName.Text);

    // ---------------------------------------------------------------- what can be chosen

    public static List<Target> All(ActionComponent action) => action switch
    {
        WoodcuttingActionComponent c => c.Trees.Keys.Select(Of).ToList(),
        MiningActionComponent c => c.Rocks.Keys.Select(Of).ToList(),
        FishingActionComponent c => c.Locations.Values.SelectMany(l => l.Recipes).Select(r => Of(r.Recipe)).ToList(),
        ArtisanActionComponent c => Recipes(c),
        CookingActionComponent c => Recipes(c),
        FiremakingActionComponent c => Recipes(c),
        HerbloreActionComponent c => Recipes(c),
        ThievingActionComponent c => c.ActiveRecipes.Keys.Select(Of).ToList(),
        AgilityActionComponent c => c.Courses.Keys.Select(Of).ToList(),
        AstrologyActionComponent c => c.ActiveConstellations.Keys.Select(Of).ToList(),
        HuntingActionComponent c => c.Locations.Values.SelectMany(l => l.Animals).Select(a => Of(a.Animal)).ToList(),
        CombatActionComponent c => CombatTargets(c),
        _ => new List<Target>(),
    };

    static List<Target> Recipes<TRecipe, TActive>(CraftingActionComponent<TRecipe, TActive> c)
        where TRecipe : class, ICraftingRecipe, INamespacedDataClass<TRecipe> where TActive : class, IActiveCraftingRecipe =>
        c.ActiveRecipes.Keys.Select(r => new Target(r.Id, r.DisplayName.Text)).ToList();

    static List<Target> CombatTargets(CombatActionComponent c)
    {
        var list = new List<Target>();
        if (c.Entity is not CombatEntity combat) return list;
        if (s_slayer != null)
            list.AddRange(s_slayer.Tasks.ActiveMasters.Keys.Select(m => new Target(m.Id, $"Slayer tasks of {m.DisplayName.Text}  (follows the task)", Slayer)));
        foreach (BaseCombatArea area in combat.Areas.ActiveAreas.Keys)
        {
            if (area is CombatArea normal)
                list.AddRange(normal.Enemies.Select(e => new Target(e.Id, $"{e.DisplayName.Text}  ({area.DisplayName.Text})", area.Id)));
            else if (area is Dungeon dungeon)
                list.Add(new Target(dungeon.Id, $"Dungeon: {dungeon.DisplayName.Text}", Dungeon));
        }
        return list;
    }

    // ---------------------------------------------------------------- what it makes

    /// <summary>
    /// The item this target mainly produces (the log, the ore, the bar, the most common fish...), or null when
    /// there is no single product (combat, thieving, agility, astrology). Used to fill in "until I own N of".
    /// </summary>
    public static Item? Product(ActionComponent action, Target target) => action switch
    {
        WoodcuttingActionComponent c => c.Trees.Keys.FirstOrDefault(r => r.Id == target.Id)?.Product.Item,
        MiningActionComponent c => c.Rocks.Keys.FirstOrDefault(r => r.Id == target.Id)?.Product.Item,
        FishingActionComponent c => c.Locations.Values.SelectMany(l => l.Recipes).FirstOrDefault(r => r.Recipe.Id == target.Id)
            ?.Recipe.RandomProducts.Items.MaxBy(p => p.Weight)?.Item,
        ArtisanActionComponent c => RecipeProduct(c, target.Id),
        CookingActionComponent c => RecipeProduct(c, target.Id),
        FiremakingActionComponent c => RecipeProduct(c, target.Id),
        HerbloreActionComponent c => RecipeProduct(c, target.Id),
        HuntingActionComponent c => c.Locations.Values.SelectMany(l => l.Animals).FirstOrDefault(a => a.Animal.Id == target.Id)
            ?.Animal.Products.FirstOrDefault()?.Item,
        _ => null,
    };

    static Item? RecipeProduct<TRecipe, TActive>(CraftingActionComponent<TRecipe, TActive> c, string id)
        where TRecipe : class, ICraftingRecipe, INamespacedDataClass<TRecipe> where TActive : class, IActiveCraftingRecipe
    {
        TActive? recipe = c.ActiveRecipes.FirstOrDefault(r => r.Key.Id == id).Value;
        if (recipe == null) return null;
        return recipe.Stats.Product?.Value.Item ?? recipe.Stats.Products?.Values.FirstOrDefault()?.Value.Item;
    }

    // ---------------------------------------------------------------- what is being done now

    public static Target? Current(ActionComponent action)
    {
        if (!action.IsStarted) return null;
        IDescribedObject? thing = action switch
        {
            WoodcuttingActionComponent c => c.CuttingTree?.Recipe,
            MiningActionComponent c => c.CurrentRock?.Recipe,
            FishingActionComponent c => c.CurrentRecipe?.Recipe,
            ArtisanActionComponent c => c.CreatingRecipe?.Recipe,
            CookingActionComponent c => c.CreatingRecipe?.Recipe,
            FiremakingActionComponent c => c.CreatingRecipe?.Recipe,
            HerbloreActionComponent c => c.CreatingRecipe?.Recipe,
            ThievingActionComponent c => c.CurrentRecipe == null ? null : c.ActiveRecipes.FirstOrDefault(r => r.Value == c.CurrentRecipe).Key,
            AgilityActionComponent c => c.CurrentCourse?.Course,
            AstrologyActionComponent c => c.StudiedConstellation?.Constellation,
            HuntingActionComponent c => c.CurrentAnimal?.Animal,
            _ => null,
        };
        if (thing != null) return Of(thing);
        if (action is CombatActionComponent { Entity: CombatEntity combat })
        {
            BaseCombatArea? area = combat.Areas.CurrentArea?.Area;
            if (area is Dungeon dungeon) return new Target(dungeon.Id, $"Dungeon: {dungeon.DisplayName.Text}", Dungeon);
            Enemy? enemy = combat.Areas.SelectedEnemy;
            if (area != null && enemy != null) return new Target(enemy.Id, $"{enemy.DisplayName.Text}  ({area.DisplayName.Text})", area.Id);
        }
        return null;
    }

    // ---------------------------------------------------------------- starting one thing

    /// <summary>
    /// Stops whatever is running and starts this target. Returns false, with a reason, when the game does not
    /// allow it right now (level or other requirement not met, materials missing, target no longer exists).
    /// </summary>
    public static bool Start(Game game, ActionComponent action, Target target, out string problem)
    {
        problem = "";
        if (!Allowed(action, target, out problem)) return false;
        game.ActionManager.TryStopActive(force: true);
        Select(action, target);
        if (IsDoing(action, target)) return true;
        problem = target.Extra == Slayer ? "this slayer master has no task to give right now" : "it could not be started (materials missing?)";
        return false;
    }

    [System.Runtime.CompilerServices.UnsafeAccessor(System.Runtime.CompilerServices.UnsafeAccessorKind.Field, Name = "_activeActions")]
    static extern ref List<ActionComponent> RunningList(ActiveActionManager manager);

    /// <summary>
    /// Starts this target next to whatever is running, instead of in place of it. The game stops every running
    /// action when one starts by walking its list of running actions; that list is put aside for the moment of
    /// the start, so the game's own start code runs untouched and stops nothing. Only used when the Multitask
    /// mod has made room for more than one action.
    /// </summary>
    public static bool StartBeside(Game game, ActionComponent action, Target target, out string problem)
    {
        problem = "";
        if (!Allowed(action, target, out problem)) return false;
        List<ActionComponent> running = RunningList(game.ActionManager);
        List<ActionComponent> kept = running.ToList();
        running.Clear();
        try { Select(action, target); }
        finally
        {
            int at = 0;
            foreach (ActionComponent other in kept)
                if (other.IsStarted && !running.Contains(other)) running.Insert(at++, other);
        }
        if (IsDoing(action, target)) return true;
        problem = target.Extra == Slayer ? "this slayer master has no task to give right now" : "it could not be started (materials missing?)";
        return false;
    }

    /// <summary>True while the action is running and working on this target (a slayer target: on its master's current task).</summary>
    public static bool IsDoing(ActionComponent action, Target target)
    {
        Target? now = Current(action);
        if (now == null) return false;
        return target.Extra == Slayer ? Master(target.Id)?.CurrentTask?.Id == now.Id : now.Id == target.Id;
    }

    // ---------------------------------------------------------------- living beside the Multitask mod

    /// <summary>
    /// The Multitask mod keeps extra actions running beside the main one. It writes the skills it is holding
    /// right now ("id|id") under this name on the scene root, so the two mods need not know each other.
    /// </summary>
    public const string HeldMeta = "MultitaskHeld";

    /// <summary>The Action Queue writes the skill of the step it is running under this name ("" when it runs nothing).</summary>
    public const string DrivingMeta = "ActionQueueDriving";

    public static void PublishDriving(string entityId)
    {
        try { ((Godot.SceneTree)Godot.Engine.GetMainLoop()).Root.SetMeta(DrivingMeta, entityId); } catch { }
    }

    /// <summary>The skill the Action Queue is running a step on right now, or "".</summary>
    public static string QueueDriving()
    {
        try
        {
            Godot.Node root = ((Godot.SceneTree)Godot.Engine.GetMainLoop()).Root;
            return root.HasMeta(DrivingMeta) ? root.GetMeta(DrivingMeta).AsString() : "";
        }
        catch { return ""; }
    }

    /// <summary>The Multitask mod writes how many actions may run at once (1 = the plain game) under this name.</summary>
    public const string CapacityMeta = "MultitaskCapacity";

    public static int MultitaskCapacity()
    {
        try
        {
            Godot.Node root = ((Godot.SceneTree)Godot.Engine.GetMainLoop()).Root;
            return root.HasMeta(CapacityMeta) ? Math.Clamp(root.GetMeta(CapacityMeta).AsInt32(), 1, 8) : 1;
        }
        catch { return 1; }
    }

    static HashSet<string> Held()
    {
        var held = new HashSet<string>();
        try
        {
            Godot.Node root = ((Godot.SceneTree)Godot.Engine.GetMainLoop()).Root;
            if (root.HasMeta(HeldMeta))
                foreach (string id in root.GetMeta(HeldMeta).AsString().Split('|', StringSplitOptions.RemoveEmptyEntries)) held.Add(id);
        }
        catch { }
        return held;
    }

    /// <summary>The action the player (or the queue) is doing: the running action that no Multitask slot is holding.</summary>
    public static ActionComponent? Main(Game game)
    {
        HashSet<string> held = Held();
        return game.ActionManager.ActiveActions.LastOrDefault(a => !held.Contains(a.Entity.Id));
    }

    /// <summary>False when a recipe cannot be made right now because ingredients or money are missing. Gathering needs nothing.</summary>
    public static bool HasMaterials(ActionComponent action, Target target) => action switch
    {
        ArtisanActionComponent c => CanCreate(c, target.Id),
        CookingActionComponent c => CanCreate(c, target.Id),
        FiremakingActionComponent c => CanCreate(c, target.Id),
        HerbloreActionComponent c => CanCreate(c, target.Id),
        _ => true,
    };

    /// <summary>How many times a recipe can be made from what the bank holds now; a very large number when it costs no items.</summary>
    public static long Batches(Game game, ActionComponent action, Target target) => action switch
    {
        ArtisanActionComponent c => Batches(game, c, target.Id),
        CookingActionComponent c => Batches(game, c, target.Id),
        FiremakingActionComponent c => Batches(game, c, target.Id),
        HerbloreActionComponent c => Batches(game, c, target.Id),
        _ => long.MaxValue,
    };

    static long Batches<TRecipe, TActive>(Game game, CraftingActionComponent<TRecipe, TActive> c, string id)
        where TRecipe : class, ICraftingRecipe, INamespacedDataClass<TRecipe> where TActive : class, IActiveCraftingRecipe
    {
        TActive? recipe = c.ActiveRecipes.FirstOrDefault(r => r.Key.Id == id).Value;
        if (recipe == null || !c.CanCreateRecipe(recipe)) return 0;
        if (!game.Entities.TryGetObjectById("MelvorBase:Bank", out GameEntity? e) || e is not GamesByMalcs.Melvor2.Entities.Bank.BankEntity bank) return 1;
        long batches = long.MaxValue;
        foreach (var cost in recipe.Stats.ItemCost.Values)
            if (cost.Value.Quantity > 0) batches = Math.Min(batches, bank.Inventory.GetQuantity(cost.Value.Item) / cost.Value.Quantity);
        return Math.Max(1, batches);
    }

    static bool CanCreate<TRecipe, TActive>(CraftingActionComponent<TRecipe, TActive> c, string id)
        where TRecipe : class, ICraftingRecipe, INamespacedDataClass<TRecipe> where TActive : class, IActiveCraftingRecipe
    {
        TActive? recipe = c.ActiveRecipes.FirstOrDefault(r => r.Key.Id == id).Value;
        return recipe != null && c.CanCreateRecipe(recipe);
    }

    /// <summary>Whether the game would let this target be started now (materials aside). Nothing is started.</summary>
    public static bool CanStart(ActionComponent action, Target target, out string problem) => Allowed(action, target, out problem);

    /// <summary>The game's own requirement check for a target; nothing is started here.</summary>
    static bool Allowed(ActionComponent action, Target target, out string problem)
    {
        const string locked = "its requirements are not met yet";
        problem = "it no longer exists in the game";

        // Two skills need a choice the player makes on the skill's own page; the queue does not make it for them.
        if (action is HuntingActionComponent hunting)
        {
            ActiveHuntingAnimal? animal = hunting.Locations.Values.SelectMany(l => l.Animals).FirstOrDefault(a => a.Animal.Id == target.Id);
            if (animal == null) return false;
            problem = !animal.Animal.Requirements.AreMet ? locked
                : !hunting.CanTrackAnimal(animal) ? "choose a trap for it on the Hunting page first (and keep that trap in the bank)" : "";
            return problem == "";
        }
        if (action is CombatActionComponent && target.Extra == Slayer)
        {
            ActiveSlayerMaster? master = Master(target.Id);
            if (master == null) return false;
            Enemy? task = master.CurrentTask;
            CombatArea? taskArea = task == null ? null : master.GetCurrentTaskArea(task);
            problem = !master.Data.UnlockRequirements.AreMet ? "this slayer master is not unlocked yet"
                : taskArea != null && !taskArea.UnlockRequirements.AreMet ? $"the task monster's area ({taskArea.DisplayName.Text}) is not unlocked yet"
                : task != null && !task.Requirements.AreMet ? $"the task monster ({task.DisplayName.Text}) cannot be fought yet" : "";
            return problem == "";
        }
        if (action is AgilityActionComponent agility)
        {
            ActiveAgilityCourse? course = agility.Courses.FirstOrDefault(c => c.Key.Id == target.Id).Value;
            if (course == null) return false;
            problem = !course.Course.Requirements.AreMet ? locked
                : course.SelectedPath == null ? "choose a path for it on the Agility page first" : "";
            return problem == "";
        }

        bool? ok = action switch
        {
            WoodcuttingActionComponent c => c.Trees.Keys.FirstOrDefault(r => r.Id == target.Id)?.Requirements.AreMet,
            MiningActionComponent c => c.Rocks.Keys.FirstOrDefault(r => r.Id == target.Id)?.Requirements.AreMet,
            FishingActionComponent c => c.Locations.Values.SelectMany(l => l.Recipes).FirstOrDefault(r => r.Recipe.Id == target.Id)?.Recipe.Requirements.AreMet,
            ArtisanActionComponent c => RecipeAllowed(c, target.Id),
            CookingActionComponent c => RecipeAllowed(c, target.Id),
            FiremakingActionComponent c => RecipeAllowed(c, target.Id),
            HerbloreActionComponent c => RecipeAllowed(c, target.Id),
            ThievingActionComponent c => c.ActiveRecipes.FirstOrDefault(r => r.Key.Id == target.Id).Value is { } recipe ? c.CanThieveRecipe(recipe) : null,
            AstrologyActionComponent c => c.ActiveConstellations.Keys.FirstOrDefault(r => r.Id == target.Id)?.Requirements.AreMet,
            CombatActionComponent { Entity: CombatEntity combat } => CombatAllowed(combat, target),
            _ => null,
        };
        if (ok == null) return false;
        problem = ok.Value ? "" : locked;
        return ok.Value;
    }

    static bool? RecipeAllowed<TRecipe, TActive>(CraftingActionComponent<TRecipe, TActive> c, string id)
        where TRecipe : class, ICraftingRecipe, INamespacedDataClass<TRecipe> where TActive : class, IActiveCraftingRecipe =>
        c.ActiveRecipes.Keys.FirstOrDefault(r => r.Id == id)?.Requirements.AreMet;

    static bool? CombatAllowed(CombatEntity combat, Target target)
    {
        BaseCombatArea? area = CombatArea(combat, target);
        if (area == null) return null;
        if (!area.UnlockRequirements.AreMet) return false;
        return area is not CombatArea normal || normal.Enemies.FirstOrDefault(e => e.Id == target.Id)?.Requirements.AreMet != false;
    }

    static BaseCombatArea? CombatArea(CombatEntity combat, Target target) =>
        combat.Areas.ActiveAreas.Keys.FirstOrDefault(a => a.Id == (target.Extra == Dungeon ? target.Id : target.Extra));

    static void Select(ActionComponent action, Target target)
    {
        switch (action)
        {
            case WoodcuttingActionComponent c: c.SelectTree(c.Trees.First(t => t.Key.Id == target.Id).Value); break;
            case MiningActionComponent c: c.SelectRock(c.Rocks.First(r => r.Key.Id == target.Id).Value); break;
            case FishingActionComponent c:
            {
                ActiveFishingRecipe recipe = c.Locations.Values.SelectMany(l => l.Recipes).First(r => r.Recipe.Id == target.Id);
                c.SetCurrentLocation(recipe.ActiveLocation);
                c.SelectRecipe(recipe);
                break;
            }
            case ArtisanActionComponent c: StartRecipe(c, target.Id); break;
            case CookingActionComponent c: StartRecipe(c, target.Id); break;
            case FiremakingActionComponent c: StartRecipe(c, target.Id); break;
            case HerbloreActionComponent c: StartRecipe(c, target.Id); break;
            case ThievingActionComponent c: c.TryStartActiveRecipe(c.ActiveRecipes.First(r => r.Key.Id == target.Id).Value); break;
            case AgilityActionComponent c: c.SelectCourse(c.Courses.First(r => r.Key.Id == target.Id).Value); break;
            case AstrologyActionComponent c: c.StudyConstellation(c.ActiveConstellations.First(r => r.Key.Id == target.Id).Value); break;
            case HuntingActionComponent c:
            {
                ActiveHuntingAnimal animal = c.Locations.Values.SelectMany(l => l.Animals).First(a => a.Animal.Id == target.Id);
                c.SelectLocation(animal.Location);
                c.SelectAnimal(animal);
                c.StartTracking();
                break;
            }
            case CombatActionComponent { Entity: CombatEntity combat } c when target.Extra == Slayer:
            {
                ActiveSlayerMaster master = Master(target.Id)!;
                if (master.CurrentTask == null) master.StartInitialTask();   // the same as asking the master for a first task
                Enemy? task = master.CurrentTask;
                if (task == null) break;
                combat.Areas.SelectEnemy(master.GetCurrentTaskArea(task), task);
                if (!c.IsStarted) c.TryStart();
                break;
            }
            case CombatActionComponent { Entity: CombatEntity combat } c:
            {
                BaseCombatArea area = CombatArea(combat, target)!;
                if (area is Dungeon dungeon) combat.Areas.SelectDungeon(dungeon);
                else if (area is CombatArea normal) combat.Areas.SelectEnemy(normal, normal.Enemies.First(e => e.Id == target.Id));
                if (!c.IsStarted) c.TryStart();   // selecting an enemy usually starts the fight by itself
                break;
            }
        }
    }

    static void StartRecipe<TRecipe, TActive>(CraftingActionComponent<TRecipe, TActive> c, string id)
        where TRecipe : class, ICraftingRecipe, INamespacedDataClass<TRecipe> where TActive : class, IActiveCraftingRecipe
    {
        c.SelectRecipe(c.ActiveRecipes.First(r => r.Key.Id == id).Value);
        c.TryStart();
    }
}
