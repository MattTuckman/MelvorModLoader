using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Components.CombatEffects;
using GamesByMalcs.Melvor2.Data;
using GamesByMalcs.Melvor2.Localization;
using GamesByMalcs.Melvor2.Modifiers;
using Godot;
using MelvorModLoader;

namespace CheatMenu;

/// <summary>
/// Turns menu values into bonuses in the game's own modifier system (the one items, pets and prayers use).
/// Nothing is written into the save: bonuses are re-applied from settings.json each time a character is opened.
/// </summary>
public static class Engine
{
    sealed class Source(string name) : INamedObject
    {
        public ILocalizedString DisplayName { get; } = new LocalizedString(name);
    }

    sealed class Applied
    {
        public required Source Source;
        public required ModifierValues Values;
        public double Multiplier;
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Modifiers")]
    static extern ModifierSystem GetModifiers(Game game);

    static Game? s_game;
    static ModifierSystem? s_mods;
    static readonly Dictionary<string, Applied> s_applied = new();
    static Dictionary<string, double> s_values = new();

    public static Action<string> Log { get; set; } = _ => { };
    public static Game? Game => s_game;
    public static string Character { get; private set; } = "";

    /// <summary>Stats the running game version did not accept (shown in the log, skipped).</summary>
    public static List<string> Problems { get; } = new();

    /// <summary>Call when a character is opened. Loads that character's settings and applies them.</summary>
    public static void Bind(Game game)
    {
        if (ReferenceEquals(s_game, game)) return;
        Settings.Flush();
        s_game = game;
        s_mods = GetModifiers(game);
        s_applied.Clear();
        Problems.Clear();
        Character = game.CharacterName;
        s_values = Settings.Load(Character);
        AutoSell.Bind(Character);
        LootFilter.Bind(game, Character);
        foreach (Feature f in Features.All) Apply(f);
        Log($"'{Character}': {ActiveCount()} setting(s) applied.");
    }

    /// <summary>How many settings differ from the game's normal behaviour (optionally in one tab).</summary>
    public static int ActiveCount(string? tab = null) =>
        s_values.Count(v => Features.Find(v.Key) is { } f && v.Value != f.Off && (tab == null || f.Tab == tab));

    public static bool IsActive(string id) => GetValue(id) != Features.Get(id).Off;

    public static double GetValue(string id) => s_values.TryGetValue(id, out double v) ? v : Features.Get(id).Off;

    public static void SetValue(string id, double value)
    {
        Feature f = Features.Get(id);
        value = f.Kind == FeatureKind.Toggle ? (value >= 0.5 ? 1 : 0) : Math.Clamp(value, f.Min, f.Max);
        if (value == f.Off) s_values.Remove(id); else s_values[id] = value;
        Apply(f);
        Settings.Save(Character, s_values);
        Changed?.Invoke();
    }

    /// <summary>The same value for several settings, as one change.</summary>
    public static void SetValues(IEnumerable<string> ids, double value)
    {
        foreach (string id in ids)
        {
            Feature f = Features.Get(id);
            double v = f.Kind == FeatureKind.Toggle ? (value >= 0.5 ? 1 : 0) : Math.Clamp(value, f.Min, f.Max);
            if (v == f.Off) s_values.Remove(id); else s_values[id] = v;
            Apply(f);
        }
        Settings.Save(Character, s_values);
        Changed?.Invoke();
    }

    /// <summary>Raised after any value changed (the menu repaints its counters).</summary>
    public static event Action? Changed;

    public static void ResetAll()
    {
        s_values.Clear();
        foreach (Feature f in Features.All) Apply(f);
        Settings.Save(Character, s_values);
        Changed?.Invoke();
    }

    /// <summary>For tests: what the game's modifier tables report for each stat of a feature right now.</summary>
    public static List<string> Diagnose(string id)
    {
        var lines = new List<string>();
        if (s_mods == null || !s_applied.TryGetValue(id, out Applied? applied)) return lines;
        foreach (ComponentModifierValue v in applied.Values.Component)
        {
            ModifierQuery query = s_mods.CreateQueryForStat(v.Stat, v.LocalContext);
            lines.Add($"{v.Stat.Name}={query.Total:0.###}");
            query.Destroy();
        }
        foreach (GlobalModifierValue g in applied.Values.Global)
            lines.Add($"{g.StatGroup}(global) x{applied.Multiplier:0.###}");
        return lines;
    }

    static void Apply(Feature f)
    {
        if (f.Custom || s_game == null || s_mods == null) return;
        double value = GetValue(f.Id);
        double multiplier = value == f.Off ? 0 : f.Kind == FeatureKind.Toggle ? 1 : f.ToMultiplier(value);

        s_applied.TryGetValue(f.Id, out Applied? applied);
        if (multiplier == 0)
        {
            if (applied is { Multiplier: not 0 })
            {
                s_mods.Remove(applied.Source);
                applied.Multiplier = 0;
            }
            return;
        }
        if (applied == null)
        {
            applied = new Applied { Source = new Source("Cheat Menu: " + f.En), Values = Build(f, s_game) };
            s_applied[f.Id] = applied;
        }
        if (applied.Multiplier == 0) s_mods.Add(applied.Source, applied.Values, multiplier);
        else s_mods.Update(applied.Source, applied.Values, multiplier);
        applied.Multiplier = multiplier;
    }

    static ModifierValues Build(Feature f, Game game)
    {
        var globals = new List<GlobalModifierValue>();
        var components = new List<ComponentModifierValue>();
        foreach (GlobalRef g in f.Globals)
        {
            string json = "{\"StatGroup\": \"" + g.StatGroup + "\", \"Value\": " + Num(g.Unit) + ", \"Condition\": null, \"Scaling\": null}";
            try { globals.Add(new GlobalModifierValue(game.Data, JsonDocument.Parse(json).RootElement)); }
            catch (Exception e) { Problem(f, g.StatGroup, e); }
        }
        foreach (StatRef s in f.Stats)
        {
            string json = "{\"Stat\": {\"StatsType\": \"MelvorBase:" + s.StatsType + "\", \"Name\": \"" + s.Name + "\"}, \"Value\": " + Num(s.Unit)
                + ", \"LocalContext\": {\"Component\": {\"Entity\": \"MelvorBase:" + s.Entity + "\", \"Component\": \"" + s.Component + "\"}}"
                + ", \"FromContext\": null, \"Condition\": null, \"Scaling\": null}";
            try { components.Add(new ComponentModifierValue(game.Data, JsonDocument.Parse(json).RootElement)); }
            catch (Exception e) { Problem(f, $"{s.StatsType}.{s.Name} @ {s.Entity}/{s.Component}", e); }
        }
        return new ModifierValues(globals, components, new List<CombatEffectModifierValue>());
    }

    static void Problem(Feature f, string what, Exception e)
    {
        string msg = $"{f.Id}: {what} -> {(e.InnerException ?? e).Message}";
        Problems.Add(msg);
        Log("Skipped bonus: " + msg);
    }

    static string Num(double d) => d.ToString("R", CultureInfo.InvariantCulture);
}

/// <summary>Per-character values in settings.json next to the mod DLL.</summary>
static class Settings
{
    public static string Dir = "";
    static string FilePath => Path.Combine(Dir, "settings.json");
    static string s_character = "";
    static Dictionary<string, double> s_mine = new();

    public static Dictionary<string, double> Load(string character)
    {
        Store.Read<Dictionary<string, double>>(FilePath).TryGetValue(character, out Dictionary<string, double>? values);
        var known = Features.All.Select(f => f.Id).ToHashSet();
        return values?.Where(v => known.Contains(v.Key)).ToDictionary(v => v.Key, v => v.Value) ?? new();
    }

    static bool s_dirty;

    /// <summary>Remembers the values; the file itself is written by <see cref="Flush"/> a moment later.</summary>
    public static void Save(string character, Dictionary<string, double> values)
    {
        if (character == "") return;
        s_character = character;
        s_mine = new Dictionary<string, double>(values);
        s_dirty = true;
    }

    /// <summary>Writes settings.json if something changed since the last write.</summary>
    public static void Flush()
    {
        if (!s_dirty) return;
        s_dirty = false;
        Store.Write(FilePath, s_character, s_mine, "The settings");
    }
}

/// <summary>
/// The mod's files hold one entry per character. A write replaces only the open character's entry in what is
/// on disk at that moment, so two running copies of the game cannot wipe each other's characters.
/// </summary>
static class Store
{
    public static Dictionary<string, T> Read<T>(string path)
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, T>>(File.ReadAllText(path)) ?? new(); }
        catch { return new(); }
    }

    public static void Write<T>(string path, string character, T entry, string what)
    {
        if (character == "") return;
        try
        {
            Dictionary<string, T> all = Read<T>(path);
            all[character] = entry;
            File.WriteAllText(path, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) { Engine.Log(what + " could not be saved: " + e.Message); }
    }
}

/// <summary>Whole-game speed: runs extra game ticks on every physics frame, while the game itself is ticking.</summary>
static class GameSpeed
{
    static GameDriver? s_driver;
    static double s_debt;

    public static SceneTree? Tree { get; set; }

    public static void OnPhysicsFrame()
    {
        Game? game = Engine.Game;
        if (game is not { IsSaveLoaded: true }) return;
        double speed = Engine.GetValue("speed.game");
        if (speed <= 1) { s_debt = 0; return; }

        // While the game computes offline progress it ticks itself on another thread and pauses the scene tree.
        // Ticking it from here at the same time would have two threads inside the game at once.
        if (Tree is { Paused: true }) { s_debt = 0; return; }

        if (s_driver == null || !GodotObject.IsInstanceValid(s_driver))
            s_driver = GameAccess.FindNode<GameDriver>();
        // The game pauses its own ticking during offline catch-up and loading; stay in step with it.
        if (s_driver == null || !s_driver.IsPhysicsProcessing()) return;

        s_debt += speed - 1;
        while (s_debt >= 1)
        {
            game.PropagateTick();
            s_debt -= 1;
        }
    }
}
