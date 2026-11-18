using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Components.CombatAreas;
using GamesByMalcs.Melvor2.Components.CombatLoot;
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.Entities;
using GamesByMalcs.Melvor2.Entities.Bank;
using GamesByMalcs.Melvor2.Entities.Combat;

namespace CheatMenu;

/// <summary>What happens to an item when a monster or a dungeon drops it.</summary>
public enum LootRule { Keep, Sell, Destroy }

/// <summary>
/// Per-character rules for combat drops. The game puts a drop straight into the bank and then announces it;
/// on that announcement the dropped amount (and only that amount) is sold or removed again.
/// Items already in the bank, or gained in any other way, are never touched; neither is a stack locked in the bank.
/// The rules are kept in lootfilter.json next to the mod DLL.
/// </summary>
public static class LootFilter
{
    static string FilePath => Path.Combine(Settings.Dir, "lootfilter.json");
    static Dictionary<string, LootRule> s_rules = new();
    static string s_character = "";
    static Game? s_game;
    static bool s_failed;

    /// <summary>Items handled since the character was opened.</summary>
    public static long Sold { get; private set; }
    public static long Destroyed { get; private set; }

    public static int Count => s_rules.Count;

    public static LootRule Get(Item item) => s_rules.TryGetValue(item.Id, out LootRule r) ? r : LootRule.Keep;

    /// <summary>Loads the rules of the character that was just opened and starts listening for its drops.</summary>
    public static void Bind(Game game, string character)
    {
        s_character = character;
        s_rules = new Dictionary<string, LootRule>();
        if (Store.Read<Dictionary<string, string>>(FilePath).TryGetValue(character, out Dictionary<string, string>? saved))
            foreach (var (id, rule) in saved)
                if (Enum.TryParse(rule, out LootRule r) && r != LootRule.Keep) s_rules[id] = r;
        Sold = Destroyed = 0;

        if (ReferenceEquals(s_game, game)) return;
        s_game = game;
        CombatEntity? combat = Combat(game);
        if (combat == null) { Engine.Log("Loot filter: combat was not found, the filter is inactive."); return; }
        combat.Loot.EnemyLooted += (_, e) => Handle(game, e.Items);
        combat.Loot.DungeonLooted += (_, e) => Handle(game, e.Items);
    }

    public static void Set(IEnumerable<Item> items, LootRule rule)
    {
        foreach (Item item in items)
        {
            if (rule == LootRule.Keep) s_rules.Remove(item.Id);
            else s_rules[item.Id] = rule;
        }
        Save();
    }

    public static void Clear()
    {
        s_rules.Clear();
        Save();
    }

    static void Save()
    {
        Store.Write(FilePath, s_character, s_rules.OrderBy(r => r.Key).ToDictionary(r => r.Key, r => r.Value.ToString()), "The loot filter");
    }

    static void Handle(Game game, IReadOnlyList<ItemQuantity> drops)
    {
        // Only the open character's drops, and never let a problem here reach the game's own loot code.
        if (s_rules.Count == 0 || !ReferenceEquals(game, s_game)) return;
        try
        {
            BankEntity? bank = Actions.Bank(game);
            if (bank == null) return;
            foreach (ItemQuantity drop in drops)
            {
                if (!s_rules.TryGetValue(drop.Item.Id, out LootRule rule)) continue;
                int amount = Math.Min(drop.Quantity, bank.Inventory.GetQuantity(drop.Item));
                if (amount <= 0 || Actions.IsLocked(bank, drop.Item)) continue;   // a locked stack is the player's "never sell"
                if (rule == LootRule.Sell)
                {
                    Actions.Sell(bank, drop.Item, amount);
                    Sold += amount;
                }
                else
                {
                    Destroyed += bank.Inventory.RemoveQuantity(drop.Item, amount);
                }
            }
        }
        catch (Exception e)
        {
            if (!s_failed) Engine.Log("Loot filter error (shown once): " + e.Message);
            s_failed = true;
        }
    }

    // ---------------------------------------------------------------- lists for the menu

    public static CombatEntity? Combat(Game game) =>
        game.Entities.TryGetObjectById("MelvorBase:Combat", out GameEntity? e) ? e as CombatEntity : null;

    static IEnumerable<Item> ItemsOf(CombatDrops drops) =>
        drops.FixedItems.Select(i => i.Item).Concat(drops.ItemTables.SelectMany(t => t.Table.Items.Select(i => i.Item)));

    /// <summary>Every item a monster or a dungeon can drop, by name.</summary>
    public static List<Item> Droppable(Game game) =>
        game.Data.GetDataRegistryOutput<Enemy>().GetAllObjects().SelectMany(e => ItemsOf(e.Drops))
            .Concat(game.Data.GetDataRegistryOutput<Dungeon>().GetAllObjects().SelectMany(d => ItemsOf(d.Rewards)))
            .Distinct().OrderBy(Actions.ItemName, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The monster that is selected in combat right now, and what it can drop.</summary>
    public static (string Name, HashSet<Item> Items) CurrentEnemy(Game game)
    {
        Enemy? enemy = Combat(game)?.Areas.SelectedEnemy;
        return enemy == null ? ("", new HashSet<Item>()) : (enemy.DisplayName.Text, ItemsOf(enemy.Drops).ToHashSet());
    }
}
