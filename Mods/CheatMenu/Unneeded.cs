using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Components.ArtisanAction;
using GamesByMalcs.Melvor2.Components.ItemUpgrading;
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.Data.Recipes;
using GamesByMalcs.Melvor2.Entities;
using GamesByMalcs.Melvor2.Entities.Cooking;
using GamesByMalcs.Melvor2.Entities.Farming;
using GamesByMalcs.Melvor2.Entities.Firemaking;
using GamesByMalcs.Melvor2.Entities.Herblore;
using GamesByMalcs.Melvor2.Stats.Nodes;

namespace CheatMenu;

/// <summary>
/// Materials the player no longer wants to need ("bars without coal"). A material on this list is taken out of
/// every recipe and item upgrade that asks for it; everything else about the recipe stays as it is.
///
/// How: the game keeps what a recipe asks for in one list per recipe. The chosen materials are removed from
/// those lists (what each list held at first is remembered, so switching a material back on restores it), and
/// the costs the game already worked out for the open character are worked out again.
/// Nothing is written into the save; the list is kept per character in unneeded.json next to the mod DLL.
/// </summary>
public static class Unneeded
{
    static string FilePath => Path.Combine(Settings.Dir, "unneeded.json");
    static HashSet<string> s_ids = new();
    static string s_character = "";
    static Game? s_game;

    /// <summary>Every ingredient list of the open game.</summary>
    static List<RecipeIngredients> s_lists = new();
    /// <summary>
    /// What each list held before the mod touched it. The game's recipe data can be shared by every character
    /// opened in one session, so this is remembered once and never overwritten.
    /// </summary>
    static readonly Dictionary<RecipeIngredients, ItemQuantity[]> s_original = new(ReferenceEqualityComparer.Instance);
    static readonly FieldInfo? s_itemsField = typeof(RecipeIngredients).GetField("_items", BindingFlags.Instance | BindingFlags.NonPublic);

    static List<(Item Item, int Uses)> s_materials = new();

    /// <summary>How many materials are switched off.</summary>
    public static int Count => s_ids.Count;

    public static bool IsRemoved(Item item) => s_ids.Contains(item.Id);

    /// <summary>Everything some recipe asks for, the most asked-for first.</summary>
    public static IReadOnlyList<(Item Item, int Uses)> Materials => s_materials;

    /// <summary>For tests and the log: how many recipe costs of the open character were worked out again last time.</summary>
    public static int LastRefreshed { get; private set; }

    /// <summary>Loads the list of the character that was just opened and applies it.</summary>
    public static void Bind(Game game, string character)
    {
        s_character = character;
        s_game = game;
        s_ids = Store.Read<List<string>>(FilePath).TryGetValue(character, out List<string>? ids) ? ids.ToHashSet() : new HashSet<string>();
        Discover(game);
        Apply();
        if (s_ids.Count > 0) Engine.Log($"Materials: {s_ids.Count} material(s) are not needed for '{character}'.");
    }

    public static void Set(IEnumerable<Item> items, bool removed)
    {
        foreach (Item item in items)
        {
            if (removed) s_ids.Add(item.Id);
            else s_ids.Remove(item.Id);
        }
        Save();
        Apply();
    }

    public static void Clear()
    {
        s_ids.Clear();
        Save();
        Apply();
    }

    static void Save() => Store.Write(FilePath, s_character, s_ids.OrderBy(i => i).ToList(), "The list of materials that are not needed");

    static void Discover(Game game)
    {
        var found = new HashSet<RecipeIngredients>(ReferenceEqualityComparer.Instance);
        void Take(string kind, Func<IEnumerable<RecipeIngredients>> lists)
        {
            try { foreach (RecipeIngredients list in lists()) found.Add(list); }
            catch (Exception e) { Engine.Log($"Materials: {kind} could not be read ({(e.InnerException ?? e).Message}); left as it is."); }
        }
        Take("smithing, crafting and the like", () => game.Data.GetDataRegistryOutput<ArtisanRecipe>().GetAllObjects().Select(r => r.Ingredients));
        Take("cooking", () => game.Data.GetDataRegistryOutput<CookingRecipe>().GetAllObjects().Select(r => r.Ingredients));
        Take("firemaking", () => game.Data.GetDataRegistryOutput<FiremakingRecipe>().GetAllObjects().Select(r => r.Ingredients));
        Take("herblore", () => game.Data.GetDataRegistryOutput<HerbloreRecipe>().GetAllObjects().Select(r => r.Ingredients));
        Take("farming", () => game.Data.GetDataRegistryOutput<FarmingRecipe>().GetAllObjects().Select(r => r.Ingredients));
        Take("the grain mill", () => game.Data.GetDataRegistryOutput<GrainMillRecipe>().GetAllObjects().Select(r => r.Ingredients));
        Take("item upgrades", () => game.Data.GetDataRegistryOutput<ItemUpgrade>().GetAllObjects().Select(u => u.Costs));
        s_lists = found.ToList();

        foreach (RecipeIngredients list in s_lists)
            if (!s_original.ContainsKey(list) && s_itemsField?.GetValue(list) is List<ItemQuantity> items)
                s_original[list] = items.ToArray();

        s_materials = s_lists.Where(s_original.ContainsKey)
            .SelectMany(l => s_original[l].Select(q => q.Item).Distinct())
            .GroupBy(i => i)
            .Select(g => (Item: g.Key, Uses: g.Count()))
            .OrderByDescending(m => m.Uses).ThenBy(m => Actions.ItemName(m.Item), StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Makes every ingredient list hold what it held at first, minus the materials on the list.</summary>
    static void Apply()
    {
        var changed = new HashSet<RecipeIngredients>(ReferenceEqualityComparer.Instance);
        foreach (RecipeIngredients list in s_lists)
        {
            if (!s_original.TryGetValue(list, out ItemQuantity[]? original)) continue;
            if (s_itemsField?.GetValue(list) is not List<ItemQuantity> items) continue;
            List<ItemQuantity> wanted = original.Where(q => !s_ids.Contains(q.Item.Id)).ToList();
            if (items.SequenceEqual(wanted)) continue;
            items.Clear();
            items.AddRange(wanted);
            changed.Add(list);
        }
        LastRefreshed = changed.Count > 0 ? Refresh(changed) : 0;
    }

    /// <summary>
    /// The open character's skills have already worked out what each recipe costs. Those that belong to a changed
    /// list are worked out again, through the game's own update.
    /// </summary>
    static int Refresh(HashSet<RecipeIngredients> changed)
    {
        if (s_game == null) return 0;
        int refreshed = 0;
        try
        {
            foreach (GameEntity entity in s_game.Entities.GetAllObjects())
                foreach (object component in entity.Components.Values)
                {
                    if (component.GetType().GetProperty("ActiveRecipes")?.GetValue(component) is not IEnumerable recipes) continue;
                    foreach (object pair in recipes)
                    {
                        Type pairType = pair.GetType();
                        object? recipe = pairType.GetProperty("Key")?.GetValue(pair);
                        object? active = pairType.GetProperty("Value")?.GetValue(pair);
                        if (recipe == null || active == null) continue;
                        if (recipe.GetType().GetProperty("Ingredients")?.GetValue(recipe) is not RecipeIngredients list || !changed.Contains(list)) continue;
                        object? stats = active.GetType().GetProperty("Stats")?.GetValue(active);
                        if (stats != null && Find(stats.GetType(), "BaseItemCost")?.GetValue(stats) is StatNode node)
                        {
                            node.QueueUpdate();
                            refreshed++;
                        }
                    }
                }
        }
        catch (Exception e) { Engine.Log("Materials: recipe costs could not be worked out again: " + (e.InnerException ?? e).Message); }
        return refreshed;
    }

    static PropertyInfo? Find(Type? type, string name)
    {
        for (; type != null; type = type.BaseType)
            if (type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly) is { } p) return p;
        return null;
    }
}
