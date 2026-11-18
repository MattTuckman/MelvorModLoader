// The code of the wiki's "Cookbook" page (docs/modding/cookbook.md), kept here so that it is compiled and
// run against the real game by the test rig (Dev\run-test.ps1 -Only docs). When a recipe changes on the
// page, change it here too, and the other way round.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Components.Actions;
using GamesByMalcs.Melvor2.Components.CombatEffects;
using GamesByMalcs.Melvor2.Data;
using GamesByMalcs.Melvor2.Data.Currencies;
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.Entities;
using GamesByMalcs.Melvor2.Entities.Bank;
using GamesByMalcs.Melvor2.Entities.Combat;
using GamesByMalcs.Melvor2.Entities.Woodcutting;
using GamesByMalcs.Melvor2.Localization;
using GamesByMalcs.Melvor2.Modifiers;
using Godot;
using MelvorModLoader;

namespace TestHarness;

static class DocsRecipes
{
    // ---------------------------------------------------------------- the bank
    public static BankEntity? Bank(Game game) =>
        game.Entities.TryGetObjectById("MelvorBase:Bank", out GameEntity? e) ? e as BankEntity : null;

    // ---------------------------------------------------------------- finding items
    public static Item? ItemById(Game game, string id) =>
        game.Data.GetDataRegistryOutput<Item>().TryGetObjectById(id, out Item? item) ? item : null;

    public static Item? ItemByName(Game game, string name) =>
        game.Data.GetDataRegistryOutput<Item>().GetAllObjects()
            .FirstOrDefault(i => string.Equals(i.DisplayName.GetPluralText(1), name, StringComparison.OrdinalIgnoreCase));

    // ---------------------------------------------------------------- items in and out
    public static string ItemsInAndOut(BankEntity bank, Item item)
    {
        int before = bank.Inventory.GetQuantity(item);
        int added = bank.Inventory.AddQuantity(item, 100);      // returns how many actually went in
        int removed = bank.Inventory.RemoveQuantity(item, 40);  // returns how many actually came out
        int after = bank.Inventory.GetQuantity(item);
        return $"{before} + {added} - {removed} = {after}";
    }

    // ---------------------------------------------------------------- currencies
    public static string Currencies(Game game, BankEntity bank)
    {
        long added = bank.Wallet.AddGoldPieces(1_000);
        Currency gold = game.Data.GetDataRegistryOutput<Currency>().GetObjectById("MelvorBase:GoldPieces");
        long gp = bank.Wallet.GetQuantity(gold);
        string all = string.Join(", ", game.Data.GetDataRegistryOutput<Currency>().GetAllObjects().Select(c => c.Id));
        return $"+{added} -> {gp:N0} {gold.DisplayName.Text}; all currencies: {all}";
    }

    // ---------------------------------------------------------------- selling like the game does
    public static void Sell(BankEntity bank, Item item, int quantity)
    {
        var stats = bank.ItemSales.GetItemStats(item);       // the price, with every bonus applied
        try { bank.ItemSales.SellItem(item, stats, quantity); }
        finally { stats.Destroy(); }                          // stat objects must be released
    }

    // ---------------------------------------------------------------- skills
    public static string Skills(Game game) =>
        string.Join(", ", game.LeveledEntities.Select(s => $"{s.DisplayName.Text} {s.Experience.Level}"));

    public static void SetLevel(Game game, string skillId, int level)
    {
        var skill = game.LeveledEntities.First(s => s.Id == skillId);
        var xp = skill.Experience;
        xp.SetXp(xp.Table.GetXpForLevel(Math.Clamp(level, 1, xp.LevelCap)));
    }

    // ---------------------------------------------------------------- events
    public static void ListenToEvents(Game game, ModContext ctx)
    {
        BankEntity bank = Bank(game)!;
        bank.ItemSales.ItemSold += (_, e) =>
            ctx.Log($"Sold {e.Quantity} x {e.Item.DisplayName.GetPluralText(e.Quantity)} for {e.CurrencyGain.Quantity}");

        if (game.Entities.TryGetObjectById("MelvorBase:Combat", out GameEntity? c) && c is CombatEntity combat)
            combat.Loot.EnemyLooted += (_, e) =>
                ctx.Log($"{e.Enemy.DisplayName.Text} dropped {string.Join(", ", e.Items.Select(i => $"{i.Quantity} {i.Item.DisplayName.GetPluralText(i.Quantity)}"))}");

        foreach (var skill in game.LeveledEntities)
            skill.Experience.LevelChanged += (_, e) => ctx.Log($"{skill.DisplayName.Text} is now level {e.NewValue}");
    }

    // ---------------------------------------------------------------- actions
    public static string WhatIsRunning(Game game)
    {
        ActionComponent? running = game.ActionManager.ActiveActions.LastOrDefault();
        return running == null ? "nothing"
            : $"{running.Entity.DisplayName.Text}: {running.CurrentDescription?.DisplayName.Text ?? "?"}";
    }

    public static bool StartWoodcutting(Game game)
    {
        if (!game.Entities.TryGetObjectById("MelvorBase:Woodcutting", out GameEntity? e) || e is not WoodcuttingEntity wc) return false;
        WoodcuttingTree? tree = wc.Action.Trees.Values.LastOrDefault(t => t.Recipe.Requirements.AreMet);   // the best tree we may cut
        if (tree == null) return false;
        if (!wc.Action.IsStarted || wc.Action.CuttingTree != tree) wc.Action.SelectTree(tree);              // selecting the running tree again would stop it
        return wc.Action.IsStarted;
    }

    // ---------------------------------------------------------------- a bonus through the modifier system
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Modifiers")]
    static extern ModifierSystem GetModifiers(Game game);

    sealed class Source(string name) : INamedObject
    {
        public ILocalizedString DisplayName { get; } = new LocalizedString(name);
    }

    static readonly Source BankBonus = new("My mod: extra bank slots");
    static ModifierValues? s_bankValues;
    static Game? s_bankValuesFor;

    // A modifier says: "this stat, on this component, changes by this much". The game reads the same JSON
    // shape from its data files, so the value is built from JSON. Build it once per character and keep the
    // object: the game recognises a modifier by the object itself, not by what is in it.
    static ModifierValues BankValues(Game game)
    {
        if (s_bankValues != null && ReferenceEquals(s_bankValuesFor, game)) return s_bankValues;
        string json = """
            {
              "Stat": { "StatsType": "MelvorBase:BankInventory", "Name": "FlatMaxStacks" },
              "Value": 1,
              "LocalContext": { "Component": { "Entity": "MelvorBase:Bank", "Component": "Inventory" } },
              "FromContext": null, "Condition": null, "Scaling": null
            }
            """;
        var value = new ComponentModifierValue(game.Data, JsonDocument.Parse(json).RootElement);
        s_bankValues = new ModifierValues(new List<GlobalModifierValue>(), new List<ComponentModifierValue> { value }, new List<CombatEffectModifierValue>());
        s_bankValuesFor = game;
        return s_bankValues;
    }

    public static void AddBankSlots(Game game, int slots) =>
        GetModifiers(game).AddOrUpdate(BankBonus, BankValues(game), slots);   // "Value": 1 slot, times this multiplier

    public static void RemoveBankSlots(Game game) => GetModifiers(game).Remove(BankBonus);

    // ---------------------------------------------------------------- a private member
    // A private property: name the getter, "get_" + property name.
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Modifiers")]
    static extern ModifierSystem ModifiersOf(Game game);

    // A private field: returns a reference, so you can even replace it.
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_activeActions")]
    static extern ref List<ActionComponent> RunningListOf(ActiveActionManager manager);

    public static string PrivateMembers(Game game) =>
        $"modifier system: {ModifiersOf(game).GetType().Name}; running list holds {RunningListOf(game.ActionManager).Count}";

    // ---------------------------------------------------------------- the map page's snippets
    public static string MapSnippets(Game game)
    {
        IReadOnlyDataRegistry<Item> items = game.Data.GetDataRegistryOutput<Item>();
        Item oak = items.GetObjectById("MelvorBase:OakLog");
        bool found = items.TryGetObjectById("MelvorBase:OakLog", out Item? maybe);
        game.Entities.TryGetObjectById("MelvorBase:Bank", out GameEntity? e);
        var names = new List<string>();
        foreach (var (name, component) in e!.Components)
            names.Add($"{name} = {component.GetType().Name}");
        return $"{oak.DisplayName.GetPluralText(1)} / {oak.DisplayName.GetPluralText(5)}, found {found && maybe == oak}; bank: {string.Join(", ", names)}";
    }

    // ---------------------------------------------------------------- per-character settings
    public sealed class MySettings
    {
        public bool Enabled { get; set; } = true;
        public int Threshold { get; set; } = 100;
    }

    public static MySettings LoadSettings(ModContext ctx, Game game)
    {
        string file = Path.Combine(ctx.Directory, "settings.json");
        try
        {
            var all = JsonSerializer.Deserialize<Dictionary<string, MySettings>>(File.ReadAllText(file));
            if (all != null && all.TryGetValue(game.CharacterName, out MySettings? mine)) return mine;
        }
        catch { }   // no file yet, or a broken one: start with the defaults
        return new MySettings();
    }

    public static void SaveSettings(ModContext ctx, Game game, MySettings settings)
    {
        string file = Path.Combine(ctx.Directory, "settings.json");
        Dictionary<string, MySettings> all;
        try { all = JsonSerializer.Deserialize<Dictionary<string, MySettings>>(File.ReadAllText(file)) ?? new(); }
        catch { all = new(); }
        all[game.CharacterName] = settings;   // replace only this character's entry
        File.WriteAllText(file, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
    }
}
