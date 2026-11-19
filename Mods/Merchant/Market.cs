using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Components.ArtisanAction;
using GamesByMalcs.Melvor2.Data.Currencies;
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.Entities;
using GamesByMalcs.Melvor2.Entities.Bank;
using GamesByMalcs.Melvor2.Entities.Cooking;
using GamesByMalcs.Melvor2.Entities.Fishing;
using GamesByMalcs.Melvor2.Entities.Mining;
using GamesByMalcs.Melvor2.Entities.Woodcutting;
using Godot;

namespace Merchant;

/// <summary>One thing the merchant sells this visit.</summary>
public sealed class Offer
{
    public required Item Item;
    /// <summary>Price of one. It does not change during a visit.</summary>
    public long Price;
    public int Stock;
    public int Bought;
    /// <summary>Something that only turns up now and then while gathering (a gem, a spore).</summary>
    public bool Rare;
    /// <summary>The bargain of this visit.</summary>
    public bool Deal;
    /// <summary>He brought it because the player ordered it.</summary>
    public bool Ordered;
    public int Left => Stock - Bought;
}

/// <summary>One thing the merchant buys this visit, for more than the bank pays.</summary>
public sealed class Want
{
    public required Item Item;
    public long Pays;
    public int Wants;
    public int Sold;
    public int Left => Wants - Sold;
}

/// <summary>
/// The travelling merchant. Every few hours of real time he arrives with new goods:
///  - raw materials the player is already able to gather (ores, logs, raw fish) - the game's own shop sells none;
///  - one or two things that only turn up now and then while gathering;
///  - and a short list of goods he buys for more than the bank pays.
/// Prices come from the one number the game has for every item, its bank sell price. Buying costs twice that
/// (an hour's worth of coal costs about what half an hour of fighting earns), his bargain a little less, and
/// what he is looking for he buys at one and a half times. His lowest selling price is never below his highest
/// buying price, so buying and selling back cannot earn money; and what he is buying today, and anything those
/// goods are made of, he does not sell today - so buying, crafting and selling to him is no money machine either.
/// The player may place a standing order for one raw material: from the next visit on he always has it with him
/// (and is then never looking for it, or for anything made of it).
/// What was bought and sold is kept per character in merchant.json next to the mod DLL; nothing goes into the save.
/// </summary>
public static class Market
{
    public const int HoursPerVisit = 4;
    /// <summary>Buying costs this many times the bank's sell price (exact numbers: prices are rounded up to whole gold).</summary>
    public const decimal Markup = 2m, RareMarkup = 4m;
    /// <summary>The bargain of a visit costs this many times the bank's sell price. Not below <see cref="WantedBonus"/>.</summary>
    public const decimal DealMarkup = 1.6m;
    /// <summary>What he is looking for, he buys at this many times the bank's sell price.</summary>
    public const decimal WantedBonus = 1.5m;

    /// <summary>The bank price times a factor, rounded up to whole gold.</summary>
    public static long Times(Item item, decimal factor) => (long)Math.Ceiling(BankPrice(item) * factor);
    const string Gold = "MelvorBase:GoldPieces";

    public static string Dir = "";
    public static Action<string> Log = _ => { };
    /// <summary>The clock; tests move it.</summary>
    public static Func<DateTime> Now = () => DateTime.UtcNow;

    static Game? s_game;
    static string s_character = "";
    static long s_visit = -1;
    static Item? s_order;
    static long s_seen = -1;                                     // the visit whose goods the player has looked at
    static readonly HashSet<string> s_told = new();              // wanted goods the player had in the bank while looking

    /// <summary>The raw material he brings on every visit, if the player ordered one.</summary>
    public static Item? Order => s_order;

    public static List<Offer> Offers { get; private set; } = new();
    public static List<Want> Wanted { get; private set; } = new();

    /// <summary>Raised when the goods changed (a new visit) or something was bought or sold.</summary>
    public static event Action? Changed;

    static long VisitAt(DateTime utc) => new DateTimeOffset(utc, TimeSpan.Zero).ToUnixTimeSeconds() / (HoursPerVisit * 3600L);

    public static long Visit => s_visit;

    public static TimeSpan UntilNext
    {
        get
        {
            DateTime now = Now();
            DateTime next = DateTimeOffset.FromUnixTimeSeconds((VisitAt(now) + 1) * HoursPerVisit * 3600L).UtcDateTime;
            return next - now;
        }
    }

    // ---------------------------------------------------------------- the game's things

    static BankEntity? Bank(Game game) =>
        game.Entities.TryGetObjectById("MelvorBase:Bank", out GameEntity? e) && e is BankEntity bank ? bank : null;

    static Currency? GoldOf(Game game) =>
        game.Data.GetDataRegistryOutput<Currency>().GetAllObjects().FirstOrDefault(c => c.Id == Gold);

    public static Texture2D? GoldIcon(Game game) => GoldOf(game)?.Icon;

    public static long GoldOwned(Game game) => Bank(game) is { } bank && GoldOf(game) is { } gold ? bank.Wallet.GetQuantity(gold) : 0;

    public static int Owned(Game game, Item item) => Bank(game)?.Inventory.GetQuantity(item) ?? 0;

    /// <summary>What the bank pays for one, in gold; 0 for things that cannot be sold for gold.</summary>
    public static long BankPrice(Item item) => item.SellsFor is { } s && s.Currency.Id == Gold ? s.Quantity : 0;

    public static string Name(Item item) => item.DisplayName.GetPluralText(1);

    public static string Number(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>A stretch of time the way the window writes it: "2h 30m".</summary>
    public static string In(TimeSpan time) => time.TotalMinutes < 1 ? "less than a minute" : $"{(int)time.TotalHours}h {time.Minutes:00}m";

    static bool Locked(BankEntity bank, Item item) => bank.Inventory.Stacks.Any(s => s.Item == item && s.IsLocked);

    // ---------------------------------------------------------------- opening a character, time passing

    public static void Bind(Game game)
    {
        if (ReferenceEquals(s_game, game) && s_character == game.CharacterName) return;
        s_game = game;
        s_character = game.CharacterName;
        long visit = VisitAt(Now());
        ReadAll().TryGetValue(s_character, out Saved? saved);
        Dictionary<string, Item> items = game.Data.GetDataRegistryOutput<Item>().GetAllObjects().ToDictionary(i => i.Id);
        s_order = saved?.Order is { } id && items.TryGetValue(id, out Item? ordered) ? ordered : null;
        s_seen = saved?.Seen ?? -1;
        s_told.Clear();
        if (saved != null && saved.Visit == visit) Restore(saved, items, visit);
        else Arrive(game, visit);
        Changed?.Invoke();
    }

    public static void Unbind()
    {
        s_game = null;
        s_character = "";
        s_visit = -1;
        s_order = null;
        s_seen = -1;
        s_told.Clear();
        Offers = new();
        Wanted = new();
    }

    /// <summary>Called about once a second: has the next visit begun?</summary>
    public static void Tick(Game game)
    {
        if (!ReferenceEquals(s_game, game)) return;
        long visit = VisitAt(Now());
        if (visit == s_visit) return;
        Arrive(game, visit);
        Changed?.Invoke();
    }

    static void Arrive(Game game, long visit)
    {
        (Offers, Wanted) = Goods(game, s_character, visit, s_order);
        s_visit = visit;
        s_told.Clear();
        Save();
        Log($"The merchant arrived for '{s_character}': {Offers.Count} thing(s) for sale, {Wanted.Count} wanted.");
    }

    // ---------------------------------------------------------------- what he brings

    static int Seed(string character, long visit)
    {
        uint hash = 2166136261;                                  // the same goods for the same character and visit, on every start
        foreach (char c in character) hash = (hash ^ c) * 16777619;
        return unchecked((int)(hash ^ (uint)visit ^ (uint)(visit >> 32)));
    }

    /// <summary>A round number a person would put on a shelf.</summary>
    static int Round(double n) => n < 10 ? (int)Math.Max(1, Math.Round(n)) : n < 100 ? (int)Math.Round(n / 5) * 5 : n < 1000 ? (int)Math.Round(n / 50) * 50 : (int)Math.Round(n / 100) * 100;

    static void Shuffle<T>(List<T> list, Random rng)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    static bool Sellable(Item? item) => item != null && BankPrice(item) > 0;

    /// <summary>What the character can gather by now, kind by kind, and what only turns up now and then while doing so.</summary>
    static (List<Item> Mined, List<Item> Cut, List<Item> Fished, List<Item> Rare) Gathered(Game game)
    {
        var mined = new List<Item>(); var cut = new List<Item>(); var fished = new List<Item>(); var rare = new List<Item>();
        try
        {
            foreach (MiningRecipe rock in game.Data.GetDataRegistryOutput<MiningRecipe>().GetAllObjects().Where(r => r.Requirements.AreMet))
            {
                mined.Add(rock.Product.Item);
                rare.AddRange(rock.RandomProductsPerHit.Select(p => p.Item));
            }
            foreach (WoodcuttingRecipe tree in game.Data.GetDataRegistryOutput<WoodcuttingRecipe>().GetAllObjects().Where(r => r.Requirements.AreMet))
            {
                cut.Add(tree.Product.Item);
                rare.AddRange(tree.RandomProductsPerCut.Select(p => p.Item));
                rare.AddRange(tree.RandomProductsPerHit.Select(p => p.Item));
            }
            foreach (FishingRecipe water in game.Data.GetDataRegistryOutput<FishingRecipe>().GetAllObjects().Where(r => r.Requirements.AreMet))
            {
                fished.AddRange(water.RandomProducts.Items.Select(p => p.Item));
                rare.AddRange(water.SpecialProducts.Select(p => p.Item));
            }
        }
        catch (Exception e) { Log("The game's gathering data could not be read: " + (e.InnerException ?? e).Message); }
        mined = mined.Where(Sellable).Distinct().ToList();
        cut = cut.Where(Sellable).Distinct().ToList();
        fished = fished.Where(Sellable).Distinct().ToList();
        var raw = mined.Concat(cut).Concat(fished).Distinct().ToList();
        rare = rare.Where(Sellable).Distinct().Except(raw).ToList();
        return (mined, cut, fished, rare);
    }

    /// <summary>What the player may order: every raw material the character is able to gather by now, by name.</summary>
    public static List<Item> Orderable(Game game)
    {
        var (mined, cut, fished, _) = Gathered(game);
        return mined.Concat(cut).Concat(fished).Distinct().OrderBy(Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// The goods of one visit. Depends only on the character's name, the visit, what the character has unlocked
    /// at that moment and what was ordered - no state is touched, so tests can look at any visit.
    /// </summary>
    public static (List<Offer> Offers, List<Want> Wanted) Goods(Game game, string character, long visit, Item? ordered = null)
    {
        var rng = new Random(Seed(character, visit));
        var (mined, cut, fished, rare) = Gathered(game);
        var raw = mined.Concat(cut).Concat(fished).Distinct().ToList();
        if (ordered != null && !raw.Contains(ordered)) ordered = null;

        // What the character can make by now, and what everything is made of.
        var made = new List<Item>();
        var madeOf = new Dictionary<Item, HashSet<Item>>();
        try
        {
            IEnumerable<ArtisanRecipe> recipes = game.Data.GetDataRegistryOutput<ArtisanRecipe>().GetAllObjects()
                .Concat(game.Data.GetDataRegistryOutput<CookingRecipe>().GetAllObjects()).Distinct();
            foreach (ArtisanRecipe recipe in recipes)
            {
                Item? product = recipe.Products.FirstOrDefault()?.Item;
                if (product == null) continue;
                if (!madeOf.TryGetValue(product, out HashSet<Item>? parts)) madeOf[product] = parts = new HashSet<Item>();
                foreach (ItemQuantity part in recipe.Ingredients.Items) parts.Add(part.Item);
                // Things made only for a quest are left out.
                if (recipe.Requirements.AreMet && recipe.DisplayRequirements == null && Sellable(product) && !raw.Contains(product)) made.Add(product);
            }
        }
        catch (Exception e) { Log("The game's recipes could not be read: " + (e.InnerException ?? e).Message); }
        made = made.Distinct().ToList();

        // Everything these goods are made of, down to the raw materials.
        HashSet<Item> PartsOf(IEnumerable<Item> goods)
        {
            var all = new HashSet<Item>();
            var open = new Queue<Item>(goods);
            while (open.Count > 0)
                if (madeOf.TryGetValue(open.Dequeue(), out HashSet<Item>? parts))
                    foreach (Item part in parts)
                        if (all.Add(part)) open.Enqueue(part);
            return all;
        }

        // ---- what he is looking for: two things the character can make, one the character can gather.
        // What was ordered he sells for sure, so he is not looking for it, nor for anything made of it.
        if (ordered != null) made = made.Where(m => !PartsOf(new[] { m }).Contains(ordered)).ToList();
        var wantedItems = new List<Item>();
        Shuffle(made, rng);
        wantedItems.AddRange(made.Take(2));
        if (raw.Count >= 6)
        {
            var some = raw.Where(i => i != ordered && (ordered == null || !PartsOf(new[] { i }).Contains(ordered))).ToList();
            Shuffle(some, rng);
            wantedItems.Add(some[0]);
        }
        var wanted = wantedItems.Select(item =>
        {
            long pays = Times(item, WantedBonus);
            return new Want { Item = item, Pays = pays, Wants = Round(Math.Clamp(40000.0 / pays, 5, 300)) };
        }).ToList();

        // ---- what he will not sell today: what he is buying, and anything those goods are made of
        var notToday = new HashSet<Item>(wantedItems);
        notToday.UnionWith(PartsOf(wantedItems));

        // ---- for sale: the order first, then some of each kind, so it is never all logs
        var offers = new List<Offer>();
        if (ordered != null)
        {
            long price = Times(ordered, Markup);
            offers.Add(new Offer { Item = ordered, Price = price, Stock = Round(Math.Clamp(10000.0 / price, 20, 1000)), Ordered = true });
        }
        int Room(List<Item> kind, int count) => ordered != null && kind.Contains(ordered) ? count - 1 : count;   // the order takes a place of its kind
        void Put(List<Item> kind, int count, bool isRare)
        {
            var pick = kind.Where(i => !notToday.Contains(i) && offers.All(o => o.Item != i)).ToList();
            Shuffle(pick, rng);
            foreach (Item item in pick.Take(count))
            {
                long price = Times(item, isRare ? RareMarkup : Markup);
                int stock = isRare ? Round(Math.Clamp(4000.0 / price, 3, 50)) : Round(Math.Clamp(10000.0 / price, 20, 1000));
                offers.Add(new Offer { Item = item, Price = price, Stock = stock, Rare = isRare });
            }
        }
        Put(mined, Room(mined, 4), false);
        Put(cut, Room(cut, 2), false);
        Put(fished, Room(fished, 2), false);
        Put(raw, 8 - offers.Count, false);                        // a young character has few kinds: fill up with whatever there is
        var plain = offers.Where(o => !o.Ordered).ToList();
        if (plain.Count > 1)
        {
            Offer deal = plain[rng.Next(plain.Count)];
            deal.Deal = true;
            deal.Price = Times(deal.Item, DealMarkup);
            deal.Stock = Round(Math.Clamp(10000.0 / deal.Price, 20, 1000));
        }
        Put(rare, 2, true);
        return (offers, wanted);
    }

    // ---------------------------------------------------------------- the standing order

    /// <summary>Orders a raw material for every visit from the next one on; null cancels. Returns what to tell the player.</summary>
    public static string SetOrder(Game game, Item? item)
    {
        if (!ReferenceEquals(s_game, game)) return "";
        if (item != null && !Orderable(game).Contains(item)) return $"He cannot bring {Name(item)}: it is not something you are able to gather yet.";
        if (item == s_order) return item == null ? "You have no order with him." : $"{Name(item)} is already on order.";
        s_order = item;
        Save();
        Changed?.Invoke();
        return item == null
            ? "Order cancelled. From the next visit on he brings what he likes."
            : $"Ordered: he brings {Name(item)} on every visit from the next one on (in {In(UntilNext)}), until you order something else.";
    }

    // ---------------------------------------------------------------- what is new (the mark on the top bar button)

    /// <summary>Goods he is looking for that the player could sell him right now.</summary>
    public static List<Want> CanSell(Game game)
    {
        BankEntity? bank = Bank(game);
        if (bank == null || !ReferenceEquals(s_game, game)) return new();
        return Wanted.Where(w => w.Left > 0 && bank.Inventory.GetQuantity(w.Item) > 0 && !Locked(bank, w.Item)).ToList();
    }

    /// <summary>
    /// What the player has not looked at yet: goods of a new visit, and goods he is looking for that have turned
    /// up in the bank since the window was last open.
    /// </summary>
    public static (bool NewGoods, List<Want> ToSell) News(Game game)
    {
        if (!ReferenceEquals(s_game, game)) return (false, new());
        return (s_seen != s_visit, CanSell(game).Where(w => !s_told.Contains(w.Item.Id)).ToList());
    }

    /// <summary>The player is looking at the window: nothing shown there is news any more.</summary>
    public static void MarkSeen(Game game)
    {
        if (!ReferenceEquals(s_game, game)) return;
        bool changed = s_seen != s_visit;
        s_seen = s_visit;
        foreach (Want want in CanSell(game)) changed |= s_told.Add(want.Item.Id);
        if (changed) Save();
    }

    // ---------------------------------------------------------------- prices

    /// <summary>What <paramref name="count"/> of an offer cost together: simply the price times the count.</summary>
    public static long Cost(Offer offer, int count) => count <= 0 ? 0 : offer.Price * count;

    /// <summary>How many of an offer this much gold buys (never more than are left).</summary>
    public static int Affordable(Offer offer, long gold) => (int)Math.Min(offer.Left, gold / Math.Max(1, offer.Price));

    // ---------------------------------------------------------------- buying and selling

    /// <summary>Buys up to <paramref name="count"/> of an offer. Returns what to tell the player.</summary>
    public static string Buy(Game game, Offer offer, int count)
    {
        BankEntity? bank = Bank(game);
        Currency? gold = GoldOf(game);
        if (bank == null || gold == null) return "The bank was not found.";
        count = Math.Min(count, offer.Left);
        if (count <= 0) return $"{Name(offer.Item)}: sold out until the next visit.";
        long have = bank.Wallet.GetQuantity(gold);
        count = Math.Min(count, Affordable(offer, have));
        if (count <= 0) return $"Not enough gold: one {Name(offer.Item)} costs {Number(offer.Price)}.";

        int got = bank.Inventory.AddQuantity(offer.Item, count);
        if (got <= 0) return "Your bank is full.";
        long cost = Cost(offer, got);
        bank.Wallet.RemoveQuantity(gold, cost);
        offer.Bought += got;
        Save();
        Changed?.Invoke();
        return $"Bought {Number(got)} {Name(offer.Item)} for {Number(cost)} gold." + (got < count ? " Your bank had no room for more." : "");
    }

    /// <summary>Sells up to <paramref name="count"/> of something he is looking for. Returns what to tell the player.</summary>
    public static string Sell(Game game, Want want, int count)
    {
        BankEntity? bank = Bank(game);
        Currency? gold = GoldOf(game);
        if (bank == null || gold == null) return "The bank was not found.";
        if (want.Left <= 0) return $"{Name(want.Item)}: he has enough of it until the next visit.";
        if (Locked(bank, want.Item)) return $"{Name(want.Item)} is locked in your bank. Unlock it there first.";
        count = Math.Min(Math.Min(count, want.Left), bank.Inventory.GetQuantity(want.Item));
        if (count <= 0) return $"You have no {Name(want.Item)}.";

        int before = bank.Inventory.GetQuantity(want.Item);
        bank.Inventory.RemoveQuantity(want.Item, count);
        int gone = before - bank.Inventory.GetQuantity(want.Item);
        if (gone <= 0) return $"{Name(want.Item)} could not be taken out of your bank.";
        long paid = want.Pays * gone;
        bank.Wallet.AddQuantity(gold, paid);
        want.Sold += gone;
        Save();
        Changed?.Invoke();
        return $"Sold {Number(gone)} {Name(want.Item)} for {Number(paid)} gold (the bank would have paid {Number(BankPrice(want.Item) * gone)}).";
    }

    // ---------------------------------------------------------------- remembering

    sealed class SavedOffer { public string Id { get; set; } = ""; public long Price { get; set; } public int Stock { get; set; } public int Bought { get; set; } public bool Rare { get; set; } public bool Deal { get; set; } public bool Ordered { get; set; } }
    sealed class SavedWant { public string Id { get; set; } = ""; public long Pays { get; set; } public int Wants { get; set; } public int Sold { get; set; } }
    sealed class Saved
    {
        public long Visit { get; set; }
        public List<SavedOffer> Offers { get; set; } = new();
        public List<SavedWant> Wanted { get; set; } = new();
        /// <summary>The standing order (an item id), if any.</summary>
        public string? Order { get; set; }
        /// <summary>The visit whose goods the player has looked at.</summary>
        public long Seen { get; set; } = -1;
        /// <summary>Wanted goods the player had in the bank while looking at the window, this visit.</summary>
        public List<string> Told { get; set; } = new();
    }

    static string FilePath => Path.Combine(Dir, "merchant.json");

    static Dictionary<string, Saved> ReadAll()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<Dictionary<string, Saved>>(File.ReadAllText(FilePath)) ?? new() : new(); }
        catch (Exception e) { Log("merchant.json could not be read, starting fresh: " + e.Message); return new(); }
    }

    static void Save()
    {
        if (s_character == "") return;
        try
        {
            Dictionary<string, Saved> all = ReadAll();
            all[s_character] = new Saved
            {
                Visit = s_visit,
                Offers = Offers.Select(o => new SavedOffer { Id = o.Item.Id, Price = o.Price, Stock = o.Stock, Bought = o.Bought, Rare = o.Rare, Deal = o.Deal, Ordered = o.Ordered }).ToList(),
                Wanted = Wanted.Select(w => new SavedWant { Id = w.Item.Id, Pays = w.Pays, Wants = w.Wants, Sold = w.Sold }).ToList(),
                Order = s_order?.Id,
                Seen = s_seen,
                Told = s_told.ToList(),
            };
            File.WriteAllText(FilePath, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) { Log("merchant.json could not be written: " + e.Message); }
    }

    /// <summary>The goods of the visit that is still going on, as they were left.</summary>
    static void Restore(Saved saved, Dictionary<string, Item> items, long visit)
    {
        Offers = saved.Offers.Where(o => items.ContainsKey(o.Id))
            .Select(o => new Offer { Item = items[o.Id], Price = o.Price, Stock = o.Stock, Bought = Math.Clamp(o.Bought, 0, o.Stock), Rare = o.Rare, Deal = o.Deal, Ordered = o.Ordered }).ToList();
        Wanted = saved.Wanted.Where(w => items.ContainsKey(w.Id))
            .Select(w => new Want { Item = items[w.Id], Pays = w.Pays, Wants = w.Wants, Sold = Math.Clamp(w.Sold, 0, w.Wants) }).ToList();
        foreach (string id in saved.Told) s_told.Add(id);
        s_visit = visit;
    }
}
