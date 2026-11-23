using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using GamesByMalcs.Melvor2;

namespace OneStyle;

public enum Class { None, Melee, Ranged, Magic }

/// <summary>The three classes: their skills, tiers and titles.</summary>
public static class Classes
{
    /// <summary>The game's combat style object id for a class ("MelvorBase:Melee").</summary>
    public static string StyleId(Class c) => "MelvorBase:" + c;

    /// <summary>The offensive skills a class trains itself (its own requirements are never changed).</summary>
    public static readonly IReadOnlyDictionary<Class, string[]> OwnSkills = new Dictionary<Class, string[]>
    {
        [Class.Melee] = new[] { "MelvorBase:Attack", "MelvorBase:Strength" },
        [Class.Ranged] = new[] { "MelvorBase:Ranged" },
        [Class.Magic] = new[] { "MelvorBase:Magic" },
    };

    /// <summary>Every offensive skill (the ones a class may stand in for).</summary>
    public static readonly string[] Offensive = { "MelvorBase:Attack", "MelvorBase:Strength", "MelvorBase:Ranged", "MelvorBase:Magic" };

    /// <summary>Tiers start at these class levels.</summary>
    public static readonly int[] TierLevels = { 20, 40, 60, 80, 99 };

    public static int Tier(int classLevel)
    {
        int tier = 0;
        foreach (int at in TierLevels) if (classLevel >= at) tier++;
        return tier;
    }

    static readonly IReadOnlyDictionary<Class, string[]> s_titles = new Dictionary<Class, string[]>
    {
        [Class.Melee] = new[] { "Recruit", "Squire", "Vanguard", "Champion", "Warlord", "Juggernaut" },
        [Class.Ranged] = new[] { "Novice", "Scout", "Hunter", "Sharpshooter", "Deadeye", "Horizon Walker" },
        [Class.Magic] = new[] { "Initiate", "Apprentice", "Adept", "Arcanist", "Archmage", "Archon" },
    };

    public static string Title(Class c, int tier) => c == Class.None ? "" : s_titles[c][Math.Clamp(tier, 0, 5)];

    public static string Name(Class c) => c switch { Class.Melee => "Melee", Class.Ranged => "Ranged", Class.Magic => "Magic", _ => "Off" };

    /// <summary>What each tier gives (shown in the window; the numbers are in <see cref="Patches"/>).</summary>
    public static string[] Perks(Class c) => c switch
    {
        Class.Melee => new[] { "+3% max hit with melee", "+4% minimum hit (of the max hit) with melee", "-5% monster respawn time" },
        Class.Ranged => new[] { "+8% chance to keep ammo", "+4% accuracy with ranged", "-2% attack time with ranged" },
        Class.Magic => new[] { "+8% chance to keep runes", "+4% accuracy with magic", "+3% max hit with magic" },
        _ => Array.Empty<string>(),
    };
}

/// <summary>
/// The class of each character, in "onestyle.json" next to the mod (never in the save). Keyed by the save's own id,
/// so a renamed character keeps its class; the name stands in when the id cannot be read.
/// </summary>
public static class Store
{
    public static string Dir = "";
    public static Action<string> Log = _ => { };
    static string FilePath => Path.Combine(Dir, "onestyle.json");

    static readonly PropertyInfo? s_activeSave = typeof(GamesByMalcs.Melvor2.Saves.SaveManager)
        .GetProperty("ActiveSaveId", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

    public static string KeyOf(Game game)
    {
        try
        {
            if (s_activeSave?.GetValue(null) is Guid id && id != Guid.Empty) return "save:" + id.ToString("N");
        }
        catch { }
        return game.CharacterName;
    }

    static Dictionary<string, string> Read()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath)) ?? new() : new(); }
        catch (Exception e)
        {
            Log("onestyle.json could not be read, it is kept aside: " + e.Message);
            try { File.Move(FilePath, FilePath + ".unreadable", true); } catch { }
            return new();
        }
    }

    /// <summary>The class kept under this key (a save id, or a name when the id could not be read).</summary>
    public static Class Load(Game game, string key)
    {
        Dictionary<string, string> all = Read();
        return all.TryGetValue(key, out string? s) && Enum.TryParse(s, out Class c) ? c : Class.None;
    }

    public static void Save(string key, Class c)
    {
        if (key == "") return;
        try
        {
            Dictionary<string, string> all = Read();
            all[key] = c.ToString();
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, FilePath, true);
        }
        catch (Exception e) { Log("The class could not be saved: " + e.Message); }
    }
}
