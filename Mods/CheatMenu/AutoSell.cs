using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.Entities.Bank;

namespace CheatMenu;

/// <summary>
/// Sells the items on a per-character list as soon as they are in the bank, at the normal sale price.
/// A stack the player has locked in the bank is left alone.
/// The list is kept in autosell.json next to the mod DLL.
/// </summary>
public static class AutoSell
{
    static string FilePath => Path.Combine(Settings.Dir, "autosell.json");
    static HashSet<string> s_ids = new();
    static string s_character = "";

    public static IReadOnlyCollection<string> Ids => s_ids;

    /// <summary>Loads the list of the character that was just opened.</summary>
    public static void Bind(string character)
    {
        s_character = character;
        s_ids = Store.Read<List<string>>(FilePath).TryGetValue(character, out List<string>? ids) ? ids.ToHashSet() : new HashSet<string>();
    }

    public static void Add(Item item) { if (s_ids.Add(item.Id)) Save(); }
    public static void Remove(Item item) { if (s_ids.Remove(item.Id)) Save(); }
    public static void Clear() { if (s_ids.Count > 0) { s_ids.Clear(); Save(); } }

    static void Save()
    {
        Store.Write(FilePath, s_character, s_ids.OrderBy(i => i).ToList(), "The auto-sell list");
    }

    /// <summary>Sells every listed item currently in the bank. Returns how many items were sold.</summary>
    public static int RunOnce(Game game)
    {
        if (s_ids.Count == 0) return 0;
        BankEntity? bank = Actions.Bank(game);
        if (bank == null) return 0;
        var toSell = bank.Inventory.Stacks.Where(s => s.Quantity > 0 && !s.IsLocked && !s.IsPlaceholder && s_ids.Contains(s.Item.Id))
                         .Select(s => (s.Item, s.Quantity)).ToList();
        int sold = 0;
        foreach (var (item, quantity) in toSell)
        {
            Actions.Sell(bank, item, quantity);
            sold += quantity;
        }
        return sold;
    }
}
