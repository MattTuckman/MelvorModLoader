using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Components.Actions;
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.Entities;
using GamesByMalcs.Melvor2.Entities.Bank;
using GamesByMalcs.Melvor2.Entities.Combat;

namespace ActionQueue;

/// <summary>When a step is over.</summary>
public enum Until { Stops, Item, Level, Minutes }

/// <summary>One line of the queue: do this, until that.</summary>
public sealed class Step
{
    public string Entity { get; set; } = "";      // the skill, e.g. MelvorBase:Woodcutting
    public string TargetId { get; set; } = "";    // the tree, recipe, monster...
    public string Extra { get; set; } = "";       // combat only: the area
    public string Name { get; set; } = "";        // "Woodcutting: Oak Tree"
    public Until Until { get; set; }
    public double Amount { get; set; }
    public string Subject { get; set; } = "";     // item id (Until.Item) or skill name (Until.Level)
    public string SubjectName { get; set; } = "";

    /// <summary>What happened to this step in the current run (not saved).</summary>
    public string State = "";

    public string Goal => Until switch
    {
        Until.Item => $"until {Amount:N0} {SubjectName} in the bank",
        Until.Level => $"until {SubjectName} level {Amount:0}",
        Until.Minutes => $"for {Amount:0.#} minute(s)",
        _ => "until it stops by itself",
    };
}

/// <summary>
/// Runs the steps one after another while the game is open. A step ends when its goal is reached or when the
/// action stops by itself (materials used up, bank full, death). If the player starts something else by hand,
/// the queue pauses instead of fighting them. The steps are kept per character in queue.json.
/// </summary>
public static class Queue
{
    sealed class Saved
    {
        public List<Step> Steps { get; set; } = new();
        public bool Repeat { get; set; }
    }

    public static string Dir = "";
    public static Action<string> Log { get; set; } = _ => { };
    static string FilePath => Path.Combine(Dir, "queue.json");

    static Game? s_game;
    static string s_character = "";
    static Dictionary<string, Item> s_items = new();
    static bool s_started, s_ranThisRound, s_died;
    static double s_seconds;
    static string s_following = "";   // a slayer step: the task monster it is fighting at the moment

    public static List<Step> Steps { get; private set; } = new();
    public static bool Repeat { get; private set; }
    public static bool Running { get; private set; }
    public static int Index { get; private set; }
    /// <summary>The last thing worth telling the player (why it paused, that it finished...).</summary>
    public static string Note { get; private set; } = "";
    public static event Action? Changed;

    // ---------------------------------------------------------------- loading and saving

    static Dictionary<string, Saved> Read()
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, Saved>>(File.ReadAllText(FilePath)) ?? new(); }
        catch { return new(); }
    }

    public static void Bind(Game game)
    {
        if (ReferenceEquals(s_game, game)) return;
        s_game = game;
        s_character = game.CharacterName;
        s_items = game.Data.GetDataRegistryOutput<Item>().GetAllObjects().ToDictionary(i => i.Id);
        Read().TryGetValue(s_character, out Saved? saved);
        Steps = saved?.Steps ?? new List<Step>();
        Repeat = saved?.Repeat ?? false;
        Running = false;
        Index = 0;
        Note = "";
        // A death must never turn into "walk straight back in": remember it, the runner pauses on it.
        if (game.Entities.TryGetObjectById("MelvorBase:Combat", out GameEntity? combat) && combat is CombatEntity fighter)
            fighter.Action.PlayerDied += (_, _) => { if (ReferenceEquals(game, s_game)) s_died = true; };
        Changed?.Invoke();
    }

    static void Save()
    {
        if (s_character == "") return;
        try
        {
            // Only this character's entry is replaced in what is on disk right now.
            Dictionary<string, Saved> all = Read();
            all[s_character] = new Saved { Steps = Steps, Repeat = Repeat };
            File.WriteAllText(FilePath, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) { Log("The queue could not be saved: " + e.Message); }
    }

    // ---------------------------------------------------------------- editing

    public static void Add(Step step) { Steps.Add(step); Save(); Changed?.Invoke(); }

    public static void RemoveAt(int index)
    {
        if (index < 0 || index >= Steps.Count) return;
        if (Running && index == Index) { s_started = false; }
        Steps.RemoveAt(index);
        if (index < Index) Index--;
        if (Steps.Count == 0) Finish("The queue is empty.");
        Save();
        Changed?.Invoke();
    }

    public static void Move(int index, int by)
    {
        int to = index + by;
        if (Running || index < 0 || index >= Steps.Count || to < 0 || to >= Steps.Count) return;
        (Steps[index], Steps[to]) = (Steps[to], Steps[index]);
        Save();
        Changed?.Invoke();
    }

    public static void Clear()
    {
        Steps.Clear();
        Finish("");
        Save();
        Changed?.Invoke();
    }

    public static void SetRepeat(bool repeat) { Repeat = repeat; Save(); Changed?.Invoke(); }

    // ---------------------------------------------------------------- running

    public static void Start()
    {
        if (Steps.Count == 0) { Note = "Add a step first."; Changed?.Invoke(); return; }
        foreach (Step s in Steps) s.State = "";
        if (Index >= Steps.Count) Index = 0;
        Running = true;
        s_started = false;
        s_ranThisRound = false;
        Note = "";
        Changed?.Invoke();
    }

    public static void Pause(string why)
    {
        if (!Running) return;
        Running = false;
        s_started = false;
        if (Index < Steps.Count && Steps[Index].State.StartsWith("running")) Steps[Index].State = "paused";
        Note = why;
        Changed?.Invoke();
    }

    static void Finish(string why)
    {
        Running = false;
        s_started = false;
        Index = 0;
        Note = why;
    }

    static void Next()
    {
        s_started = false;
        Index++;
        if (Index < Steps.Count) return;
        if (Repeat && s_ranThisRound)
        {
            Index = 0;
            s_ranThisRound = false;
            foreach (Step s in Steps) s.State = "";
        }
        else Finish(s_ranThisRound ? "The queue has finished." : "Nothing in the queue could be done, so it stopped.");
    }

    /// <summary>Call a few times per second while a character is open.</summary>
    public static void Tick(Game game, double seconds)
    {
        if (!Running || !ReferenceEquals(game, s_game)) return;
        if (Steps.Count == 0) { Finish("The queue is empty."); Changed?.Invoke(); return; }
        if (Index >= Steps.Count) Index = 0;
        Step step = Steps[Index];
        ActionComponent? action = Targets.Find(game, step.Entity);
        try
        {
            if (action == null) { step.State = "skipped: this skill is not in the game"; Next(); }
            else if (!s_started) Begin(game, step, action);
            else Watch(game, step, action, seconds);
        }
        catch (Exception e)
        {
            Log($"Step '{step.Name}' failed: {e.Message}");
            step.State = "skipped: an error occurred (see the log)";
            Next();
        }
        Changed?.Invoke();
    }

    static void Begin(Game game, Step step, ActionComponent action)
    {
        if (step.Until is Until.Item or Until.Level && Met(game, step))
        {
            step.State = "done (the goal was already reached)";
            Next();
            return;
        }
        if (Targets.Start(game, action, new Target(step.TargetId, step.Name, step.Extra), out string problem))
        {
            s_started = true;
            s_ranThisRound = true;
            s_seconds = 0;
            s_died = false;
            s_following = Targets.Current(action)?.Id ?? "";
            step.State = "running";
        }
        else
        {
            step.State = "skipped: " + problem;
            Next();
        }
    }

    static void Watch(Game game, Step step, ActionComponent action, double seconds)
    {
        var wanted = new Target(step.TargetId, step.Name, step.Extra);
        Target? now = Targets.Current(action);
        if (now == null)
        {
            if (s_died) { s_died = false; Pause("Your character died, so the queue is paused."); return; }
            if (game.ActionManager.HasActiveAction) { Pause("You started something else, so the queue is paused."); return; }
            step.State = "done (it stopped by itself)";
            Next();
            return;
        }
        if (!Targets.IsDoing(action, wanted))
        {
            // A slayer step follows its master: the task was completed, so walk to the new task monster.
            if (step.Extra == Targets.Slayer && now.Id == s_following)
            {
                if (Targets.Start(game, action, wanted, out string problem)) s_following = Targets.Current(action)?.Id ?? "";
                else { step.State = "done: " + problem; Next(); }
                return;
            }
            Pause("You changed what this skill is doing, so the queue is paused.");
            return;
        }
        s_following = now.Id;

        s_seconds += seconds;
        if (Met(game, step))
        {
            if (!game.ActionManager.TryStopActive()) game.ActionManager.TryStopActive(force: true);
            step.State = "done";
            Next();
            return;
        }
        step.State = "running  " + Progress(game, step);
    }

    static BankEntity? Bank(Game game) => game.Entities.TryGetObjectById("MelvorBase:Bank", out GameEntity? e) ? e as BankEntity : null;

    static int Owned(Game game, Step step) =>
        s_items.TryGetValue(step.Subject, out Item? item) ? Bank(game)?.Inventory.GetQuantity(item) ?? 0 : 0;

    static int Level(Game game, Step step) =>
        game.LeveledEntities.FirstOrDefault(s => s.DisplayName.Text == step.Subject)?.Experience.Level ?? 0;

    static bool Met(Game game, Step step) => step.Until switch
    {
        Until.Item => Owned(game, step) >= step.Amount,
        Until.Level => Level(game, step) >= step.Amount,
        Until.Minutes => s_seconds >= step.Amount * 60,
        _ => false,
    };

    static string Clock(double seconds) => $"{(int)(seconds / 60)}:{(int)(seconds % 60):00}";

    static string Progress(Game game, Step step) => step.Until switch
    {
        Until.Item => $"{Owned(game, step):N0} / {step.Amount:N0}",
        Until.Level => $"level {Level(game, step)} / {step.Amount:0}",
        Until.Minutes => $"{Clock(s_seconds)} / {Clock(step.Amount * 60)}",
        _ => Clock(s_seconds),
    };

    /// <summary>One line for the little on-screen reminder.</summary>
    public static string Headline =>
        Running && Index < Steps.Count ? $"Queue {Index + 1}/{Steps.Count}: {Steps[Index].Name}  -  {Steps[Index].State}" : "";
}
