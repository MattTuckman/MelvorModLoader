using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Components.Inventories;
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.Entities;
using GamesByMalcs.Melvor2.Entities.Bank;
using GamesByMalcs.Melvor2.Saves;

namespace MelvorModLoader;

/// <summary>
/// Gives items back when a mod that kept its own items in the save has been removed. Such a mod writes an entity of its
/// own into the save, which starts with a "give back" list: int version, int count, then (string item id, int quantity).
/// When a character opens and the mod that wrote it is not loaded, the game has skipped that entity and the mod's own
/// items with it; the loader reads the list from the save file and puts those (normal) items into the bank.
/// The game does not write the entity again without the mod, so this happens once.
/// </summary>
static class Rescue
{
    /// <summary>The entities a mod writes, and the mod that writes each.</summary>
    static readonly Dictionary<string, string> s_owners = new() { ["MelvorBase:OneStyleData"] = "OneStyle" };

    static Game? s_done;

    static object? SaveManagerValue(string name) =>
        typeof(SaveManager).GetProperty(name, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(null);

    /// <summary>Called a few times a second; works once per opened character.</summary>
    public static void Check()
    {
        Game? game = GameAccess.CurrentGame;
        if (game is not { IsSaveLoaded: true } || ReferenceEquals(s_done, game)) return;
        s_done = game;
        var missing = s_owners.Where(o => !Loader.LoadedMods.Contains(o.Value)).Select(o => o.Key).ToHashSet();
        if (missing.Count == 0) return;
        try
        {
            List<(string Owner, string Item, int Quantity)> back = Read(game, missing);
            if (back.Count == 0) return;
            if (!game.Entities.TryGetObjectById("MelvorBase:Bank", out GameEntity? e) || e is not BankEntity bank) return;
            int given = 0;
            foreach (var (owner, id, n) in back)
            {
                if (!game.Data.TryGetObject(id, out Item? item)) continue;
                int added = bank.Inventory.AddQuantity(item, n, AddItemFlags.None);
                given += added;
                Log.Write($"Rescue: {owner} is not installed; gave back {added} x {id}{(added < n ? $" (the bank had no room for {n - added})" : "")}.");
            }
            if (given > 0) Log.Write($"Rescue: {given} item(s) of removed mods are back in the bank as normal items.");
        }
        catch (Exception ex) { Log.Write("Rescue: " + ex.Message); }
    }

    static List<(string, string, int)> Read(Game game, HashSet<string> wanted)
    {
        var list = new List<(string, string, int)>();
        if (SaveManagerValue("ActiveSaveId") is not Guid id || id == Guid.Empty) return list;
        if (SaveManagerValue("ActiveSaveSource") is not ISaveSource source) return list;
        // always the active save: after loading a backup the game has already written it over the active save and
        // renumbered its backups, so the backup number would now point at another file
        using Stream file = source.GetReadStreamForId(id);
        using var unpacked = new BrotliStream(file, CompressionMode.Decompress);
        using var reader = new SaveReader(unpacked, game.Data, game.Entities);
        reader.Read<SaveHeader>();
        int entities = reader.ReadInt32();
        for (int i = 0; i < entities; i++)
        {
            string entityId = reader.ReadString();
            int length = reader.ReadInt32();
            if (!wanted.Contains(entityId)) { reader.ReadBytes(length); continue; }
            using var region = new BinaryReader(new MemoryStream(reader.ReadBytes(length)));
            region.ReadInt32();   // version
            int count = region.ReadInt32();
            for (int j = 0; j < count; j++) list.Add((s_owners[entityId], region.ReadString(), region.ReadInt32()));
        }
        return list;
    }
}
