using System;
using System.Collections.Generic;
using System.Linq;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.Entities;
using GamesByMalcs.Melvor2.Entities.Bank;

namespace CheatMenu;

/// <summary>Item lists for a <see cref="PickList{T}"/>, grouped the way the bank sorts items into kinds.</summary>
internal static class ItemPicks
{
    /// <summary>
    /// An item has several kinds; it is shown once, under the first kind it is a product of (else its first kind,
    /// else "Other"), and found by the others when searching. Alphabetical inside a group, groups alphabetical with
    /// "Other" last. The hint is the bank count.
    /// </summary>
    public static List<PickEntry<Item>> For(Game game, IEnumerable<Item> items)
    {
        BankEntity? bank = game.Entities.TryGetObjectById("MelvorBase:Bank", out GameEntity? b) ? b as BankEntity : null;
        var list = new List<(string Group, PickEntry<Item> Entry)>();
        foreach (Item item in items)
        {
            ItemBankTag? main = item.BankTags.FirstOrDefault(t => t.IsProduct) ?? item.BankTags.FirstOrDefault();
            string group = main?.BankTag.DisplayName.Text ?? "Other";
            int owned = bank?.Inventory.GetQuantity(item) ?? 0;
            string name = item.DisplayName.GetPluralText(1);
            list.Add((group, new PickEntry<Item>(item.Id, name, item, group, "", item.Icon, main?.BankTag.Icon,
                owned > 0 ? $"{owned:N0} in bank" : "", Keywords: string.Join(" ", item.BankTags.Select(t => t.BankTag.DisplayName.Text)))));
        }
        return list.OrderBy(x => x.Group == "Other" ? 1 : 0).ThenBy(x => x.Group, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Entry.Text, StringComparer.OrdinalIgnoreCase).Select(x => x.Entry).ToList();
    }
}
