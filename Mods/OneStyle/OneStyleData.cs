using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Core;
using GamesByMalcs.Melvor2.Entities;
using GamesByMalcs.Melvor2.Localization;
using GamesByMalcs.Melvor2.Saves;

namespace OneStyle;

/// <summary>What One Style keeps inside the game's own save (its own part, written with every save).</summary>
public sealed class SavedData
{
    public int Version { get; set; } = 1;
    public List<RolledItem> Items { get; set; } = new();
    /// <summary>Unique items already found (their first drop is certain) and the pity of each.</summary>
    public Dictionary<string, int> UniqueMisses { get; set; } = new();
    public List<string> UniquesFound { get; set; } = new();
    /// <summary>Drops that did not fit in the bank yet.</summary>
    public List<string> Pending { get; set; } = new();
    /// <summary>Counters of the unique powers that last (Sunken Reckoning's elite kills, Scrap Hunter's stacks...).</summary>
    public Dictionary<string, double> Counters { get; set; } = new();
}

/// <summary>
/// One Style's own entity in the game's entity list: the game writes it into every save with everything else, so the
/// rare items' definitions always match the save they are in (a copied or cloud save takes them along). The game
/// does not know it when it reads a save (One Style comes in after that): <see cref="SaveScan"/> reads it back.
///
/// Its region starts with the "give back" list the mod loader reads when One Style is gone:
/// int version, int count, then (string base item id, int quantity) per entry, then the JSON of <see cref="SavedData"/>.
/// </summary>
public sealed class OneStyleData : GameEntity
{
    public const string LocalName = "OneStyleData";
    public const string FullId = "MelvorBase:" + LocalName;
    public SavedData Data { get; set; } = new();

    public OneStyleData(ICoreSystems core, GamesByMalcs.Melvor2.Data.DataNamespace ns) : base(core, ns, LocalName) { }

    public override LocalizedString DisplayName { get; } = new("One Style");
    public override string IconPath => "res://Assets/Icons/modifiers.png";
    protected override void OnStartLoadingResources() { }
    protected override void OnFinishLoadingResources() { }

    /// <summary>Asked for the give-back list at every save: what the player owns of One Style's items, as normal items.</summary>
    public static Func<List<(string ItemId, int Quantity)>>? GiveBack;

    public override void WriteTo(SaveWriter writer)
    {
        List<(string ItemId, int Quantity)> back;
        try { back = GiveBack?.Invoke() ?? new(); }
        catch { back = new(); }
        writer.Write(1);
        writer.Write(back.Count);
        foreach (var (id, n) in back)
        {
            writer.Write(id);
            writer.Write(n);
        }
        writer.Write(JsonSerializer.Serialize(Data));
    }

    public override void ReadFrom(SaveReader reader) => Data = ReadPayload(reader).Data;

    public static (List<(string, int)> Back, SavedData Data) ReadPayload(SaveReader reader)
    {
        int version = reader.ReadInt32();
        int count = reader.ReadInt32();
        var back = new List<(string, int)>();
        for (int i = 0; i < count; i++) back.Add((reader.ReadString(), reader.ReadInt32()));
        SavedData data = JsonSerializer.Deserialize<SavedData>(reader.ReadString()) ?? new();
        return (back, data);
    }
}

/// <summary>
/// Reads the open character's save file once (after the game has read it) for what the game skipped: One Style's own
/// part, and the bank stacks, worn items and loadout items whose ids the game did not know yet.
/// </summary>
public sealed class SaveScan
{
    public sealed record Stack(int Tab, int Index, int Quantity, bool Placeholder, bool Locked, string ItemId);
    public sealed record Slot(string SlotId, int Quantity, bool Occupied, string ItemId);
    public sealed record LoadoutSlot(string LoadoutId, string SlotId, int Quantity, bool Occupied, string ItemId);

    public SavedData? Data;
    public readonly List<Stack> Stacks = new();
    public readonly List<Slot> Slots = new();
    public readonly List<LoadoutSlot> Loadouts = new();
    public bool Read;

    static object? SaveManagerValue(string name) =>
        typeof(SaveManager).GetProperty(name, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(null);

    public static SaveScan Of(Game game)
    {
        var scan = new SaveScan();
        if (SaveManagerValue("ActiveSaveId") is not Guid id || id == Guid.Empty) return scan;
        if (SaveManagerValue("ActiveSaveSource") is not ISaveSource source) return scan;
        Stream file;
        // always the active save: after loading a backup the game has already written it over the active save and
        // renumbered its backups, so the backup number would now point at another file
        try { file = source.GetReadStreamForId(id); }
        catch (Exception) { return scan; }   // a new character has no file yet
        using (file)
        using (var unpacked = new BrotliStream(file, CompressionMode.Decompress))
        using (var reader = new SaveReader(unpacked, game.Data, game.Entities))
        {
            reader.Read<SaveHeader>();
            int entities = reader.ReadInt32();
            for (int i = 0; i < entities; i++)
            {
                string entityId = reader.ReadString();
                switch (entityId)
                {
                    case OneStyleData.FullId:
                        using (SaveReader inner = Inner(reader, game)) scan.Data = OneStyleData.ReadPayload(inner).Data;
                        break;
                    case "MelvorBase:Bank":
                        using (SaveReader inner = Inner(reader, game)) Components(inner, game, "Inventory", c => scan.ReadBank(c));
                        break;
                    case "MelvorBase:Combat":
                        using (SaveReader inner = Inner(reader, game)) Components(inner, game, "Equipment", c => scan.ReadEquipment(c, game));
                        break;
                    default:
                        reader.SkipRegion();
                        break;
                }
            }
        }
        scan.Read = true;
        return scan;
    }

    static void Components(SaveReader entity, Game game, string wanted, Action<SaveReader> read)
    {
        int count = entity.ReadInt32();
        for (int j = 0; j < count; j++)
        {
            string componentId = entity.ReadString();
            if (componentId != wanted) { entity.SkipRegion(); continue; }
            using SaveReader inner = Inner(entity, game);
            read(inner);
            return;
        }
    }

    /// <summary>The next region of <paramref name="outer"/> as a reader of its own.</summary>
    public static SaveReader Inner(SaveReader outer, Game game)
    {
        int length = outer.ReadInt32();
        byte[] bytes = outer.ReadBytes(length);
        var stream = new MemoryStream(length + 4);
        stream.Write(BitConverter.GetBytes(outer.VersionInt));
        stream.Write(bytes);
        stream.Position = 0;
        return new SaveReader(stream, game.Data, game.Entities);
    }

    /// <summary>BankInventoryComponent.ReadFrom: the default tab, then every tab (StackableInventory.ReadFrom plus its label and tags).</summary>
    void ReadBank(SaveReader r)
    {
        r.ReadInt32();   // DefaultNewItemTab
        int tabs = r.ReadInt32();
        for (int t = 0; t < tabs; t++)
        {
            r.ReadInt32(); r.ReadInt32(); r.ReadInt32(); r.ReadBoolean(); r.ReadBoolean();   // max stacks, min/max quantity, combining, same stacks
            int stacks = r.ReadInt32();
            for (int i = 0; i < stacks; i++)
            {
                int quantity = r.ReadInt32();
                bool placeholder = r.ReadBoolean();
                bool locked = r.Version >= SaveVersion.Alpha0_4_4 && r.ReadBoolean();
                string itemId = r.ReadString();
                Stacks.Add(new Stack(t, i, quantity, placeholder, locked, itemId));
            }
            r.ReadString();   // label
            if (r.Version < SaveVersion.Alpha0_8_0) continue;
            int tags = r.ReadInt32();
            for (int g = 0; g < tags; g++) { r.ReadBoolean(); r.ReadBoolean(); r.ReadString(); }
        }
    }

    /// <summary>EquipmentComponent.ReadFrom: the worn slots, then every loadout (EquipmentLoadout / EquipmentSlotLoadout).</summary>
    void ReadEquipment(SaveReader r, Game game)
    {
        int slots = r.ReadInt32();
        for (int i = 0; i < slots; i++)
        {
            string slotId = r.ReadString();
            using SaveReader s = Inner(r, game);
            var (q, occ, item) = SlotContent(s);
            Slots.Add(new Slot(slotId, q, occ, item));
        }
        int loadouts = r.ReadInt32();
        for (int i = 0; i < loadouts; i++)
        {
            string loadoutId = r.ReadString();
            using SaveReader l = Inner(r, game);
            int count = l.ReadInt32();
            for (int j = 0; j < count; j++)
            {
                string slotId = l.ReadString();
                using SaveReader s = Inner(l, game);
                var (q, occ, item) = SlotContent(s);
                Loadouts.Add(new LoadoutSlot(loadoutId, slotId, q, occ, item));
            }
        }
    }

    /// <summary>ActiveEquipmentSlot / EquipmentSlotLoadout: quantity, occupied, has item, item id.</summary>
    static (int Quantity, bool Occupied, string ItemId) SlotContent(SaveReader s)
    {
        int quantity = s.ReadInt32();
        bool occupied = s.ReadBoolean();
        string item = s.ReadBoolean() ? s.ReadString() : "";
        return (quantity, occupied, item);
    }
}
