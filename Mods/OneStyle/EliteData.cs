using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace OneStyle;

public enum EliteTier { Normal, Champion, Unique }

/// <summary>One monster's elite identity, from data/elites.json (the hack-and-slash design, 179 monsters).</summary>
public sealed class EliteInfo
{
    public string Id { get; set; } = "";
    /// <summary>The picture's name without extension, in art/champion and art/unique.</summary>
    public string File { get; set; } = "";
    public string UniqueName { get; set; } = "";
    public string Story { get; set; } = "";
    public List<string> Affixes { get; set; } = new();
    public string Signature { get; set; } = "";
    public double SignatureChance { get; set; }
    /// <summary>The signature attack's biggest single hit as a share of max hit; below 0 = left out.</summary>
    public double SignaturePeak { get; set; }
    public string Passive { get; set; } = "";
}

/// <summary>What one character has done with elites; kept next to the mod, never in the game's save.</summary>
public sealed class EliteProgress
{
    public long Ember { get; set; }
    /// <summary>Kills per monster: [normal, champion, unique] (online kills only).</summary>
    public Dictionary<string, int[]> Kills { get; set; } = new();
    /// <summary>Kills of a monster since its last Unique (the Unique chance rises with it).</summary>
    public Dictionary<string, int> SinceUnique { get; set; } = new();
    /// <summary>Spawns of a monster that stay normal after one of its elites (no two in a row).</summary>
    public Dictionary<string, int> Gap { get; set; } = new();
    /// <summary>The monster marked for +25% Unique chance (100 trophies).</summary>
    public string Marked { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public bool NoDeathPenalty { get; set; } = true;
    public List<string> Recent { get; set; } = new();

    public int[] KillsOf(string id)
    {
        if (!Kills.TryGetValue(id, out int[]? k) || k.Length < 3) Kills[id] = k = new int[3];
        return k;
    }
    public int Trophies => Kills.Values.Count(k => k.Length >= 3 && k[2] > 0);
}

public static class EliteStore
{
    public static string Dir = "";
    public static Action<string> Log = _ => { };
    static string FilePath => Path.Combine(Dir, "elites-progress.json");
    static readonly JsonSerializerOptions s_json = new() { WriteIndented = true };

    static Dictionary<string, EliteProgress> Read()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<Dictionary<string, EliteProgress>>(File.ReadAllText(FilePath)) ?? new() : new(); }
        catch (Exception e)
        {
            Log("elites-progress.json could not be read, it is kept aside: " + e.Message);
            try { File.Move(FilePath, FilePath + ".unreadable", true); } catch { }
            return new();
        }
    }

    public static EliteProgress Load(string key) => key != "" && Read().TryGetValue(key, out EliteProgress? p) ? p : new EliteProgress();

    public static void Save(string key, EliteProgress p)
    {
        if (key == "") return;
        try
        {
            Dictionary<string, EliteProgress> all = Read();
            all[key] = p;
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(all, s_json));
            File.Move(tmp, FilePath, true);
        }
        catch (Exception e) { Log("The elite progress could not be saved: " + e.Message); }
    }

    public static Dictionary<string, EliteInfo> LoadInfo(string modDir)
    {
        try
        {
            string path = Path.Combine(modDir, "data", "elites.json");
            List<EliteInfo> list = JsonSerializer.Deserialize<List<EliteInfo>>(File.ReadAllText(path)) ?? new();
            return list.ToDictionary(i => i.Id);
        }
        catch (Exception e)
        {
            Log("data/elites.json could not be read: elites are off. " + e.Message);
            return new();
        }
    }
}
