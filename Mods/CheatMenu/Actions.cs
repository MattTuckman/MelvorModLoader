using System;
using System.Collections.Generic;
using System.Linq;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Components.Experience;
using GamesByMalcs.Melvor2.Data.Currencies;
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.Entities;
using GamesByMalcs.Melvor2.Entities.Bank;

namespace CheatMenu;

/// <summary>One-shot actions (the "Tools" tab). Each works directly on the open character.</summary>
public static class Actions
{
    public static BankEntity? Bank(Game game) =>
        game.Entities.TryGetObjectById("MelvorBase:Bank", out GameEntity? e) ? e as BankEntity : null;

    public static List<Currency> Currencies(Game game) =>
        game.Data.GetDataRegistryOutput<Currency>().GetAllObjects().ToList();

    public static List<Item> Items(Game game) =>
        game.Data.GetDataRegistryOutput<Item>().GetAllObjects().OrderBy(ItemName, StringComparer.OrdinalIgnoreCase).ToList();

    public static string ItemName(Item item) => item.DisplayName.GetPluralText(1);

    public static List<ILeveledEntity<ExperienceComponent>> Skills(Game game) => game.LeveledEntities.ToList();

    public static void AddCurrency(Game game, Currency currency, long amount) => Bank(game)?.Wallet.AddQuantity(currency, amount);

    public static void AddItem(Game game, Item item, int quantity) => Bank(game)?.Inventory.AddQuantity(item, quantity);

    /// <summary>True when the player has locked this item's stack in the bank (the game's own "do not sell" mark).</summary>
    public static bool IsLocked(BankEntity bank, Item item) => bank.Inventory.Stacks.Any(s => s.Item == item && s.IsLocked);

    /// <summary>Sells through the game's own sale code and releases the price lookup it needs afterwards.</summary>
    public static void Sell(BankEntity bank, Item item, int quantity)
    {
        var stats = bank.ItemSales.GetItemStats(item);
        try { bank.ItemSales.SellItem(item, stats, quantity); }
        finally { stats.Destroy(); }
    }

    public static void SetLevel(ILeveledEntity<ExperienceComponent> skill, int level)
    {
        ExperienceComponent xp = skill.Experience;
        xp.SetXp(xp.Table.GetXpForLevel(Math.Clamp(level, 1, xp.LevelCap)));
    }

    public static void SetAllLevels(Game game, int level)
    {
        foreach (var skill in game.LeveledEntities) SetLevel(skill, level);
    }

    public static void MaxAllLevels(Game game)
    {
        foreach (var skill in game.LeveledEntities) SetLevel(skill, skill.Experience.LevelCap);
    }

    public static void RunOfflineHours(Game game, double hours) => game.RequestOfflineHours(hours);

    public static void CompleteAllQuests(Game game) => game.Quests?.Quests.CompleteAllQuests();
}
