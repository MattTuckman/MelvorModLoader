using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using ActionQueue;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Components.Actions;
using GamesByMalcs.Melvor2.Entities;
using GamesByMalcs.Melvor2.Entities.Combat;
using GamesByMalcs.Melvor2.GameMenus.Footer;
using MelvorModLoader;

namespace Multitask;

/// <summary>One extra action that is kept running beside whatever the player is doing.</summary>
public sealed class Slot
{
    public bool On { get; set; }
    public string Entity { get; set; } = "";      // the skill, e.g. MelvorBase:Mining
    public string SkillName { get; set; } = "";
    public string TargetId { get; set; } = "";    // the rock, recipe, monster...
    public string Extra { get; set; } = "";       // combat only: the area
    public string Name { get; set; } = "";        // "Copper Ore"

    /// <summary>What the slot is doing right now, in words (not saved).</summary>
    [JsonIgnore] public string State = "";
    [JsonIgnore] internal double RetryAt;
    [JsonIgnore] internal string Following = "";  // a slayer slot: the task monster it is fighting
    [JsonIgnore] internal long Pile;              // a recipe slot: how many crafts the bank had materials for at the last look
    [JsonIgnore] internal double GrewAt;          // ...and when that number last went up

    [JsonIgnore] public bool HasTarget => Entity != "" && TargetId != "";
    [JsonIgnore] public string Title => HasTarget ? $"{SkillName}: {Name}" : "";
}

/// <summary>
/// The game stops every running action when a new one starts. It does that by walking its list of running
/// actions - and that list is the only thing that says "one at a time": the list itself, the save file and
/// the screen all cope with several. So a slot's action is started through the game's own start code while
/// the list is put aside for a moment; nothing else is touched.
///
/// Anything the player starts in the game still stops everything (the game's rule). A slot notices within
/// half a second and starts its action again. It also starts again when materials come back, and it turns
/// itself off when the character dies, so it never walks back into a fight.
/// </summary>
public static class Slots
{
    public const int Count = 2;
    const double RetrySeconds = 2;
    /// <summary>A recipe slot starts again once there is enough for this many crafts...</summary>
    const int PileSize = 10;
    /// <summary>...or once the materials have not grown for this long.</summary>
    const double SettleSeconds = 30;

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_activeActions")]
    static extern ref List<ActionComponent> Running(ActiveActionManager manager);

    public static string Dir = "";
    public static Action<string> Log { get; set; } = _ => { };
    static string FilePath => Path.Combine(Dir, "multitask.json");

    static Game? s_game;
    static string s_character = "";
    static Dictionary<string, ActionComponent> s_actions = new();
    static GameFooter? s_footer;
    static volatile bool s_died;
    static bool s_startedNow;
    static string s_published = "";

    public static List<Slot> All { get; private set; } = NewSlots();
    /// <summary>How many times a slot had to start its action since the character was opened.</summary>
    public static int Starts { get; private set; }
    public static event Action? Changed;

    static List<Slot> NewSlots() => Enumerable.Range(0, Count).Select(_ => new Slot()).ToList();

    // ---------------------------------------------------------------- loading and saving

    static Dictionary<string, List<Slot>> Read()
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, List<Slot>>>(File.ReadAllText(FilePath)) ?? new(); }
        catch { return new(); }
    }

    static void Save()
    {
        if (s_character == "") return;
        try
        {
            // Only this character's entry is replaced in what is on disk right now.
            Dictionary<string, List<Slot>> all = Read();
            all[s_character] = All;
            File.WriteAllText(FilePath, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) { Log("The slots could not be saved: " + e.Message); }
    }

    public static void Bind(Game game)
    {
        if (ReferenceEquals(s_game, game)) return;
        s_game = game;
        s_character = game.CharacterName;
        s_actions = Targets.Skills(game).ToDictionary(s => s.Entity.Id, s => s.Action);
        s_footer = null;
        s_died = false;
        Starts = 0;
        Read().TryGetValue(s_character, out List<Slot>? saved);
        All = saved ?? NewSlots();
        while (All.Count < Count) All.Add(new Slot());
        if (All.Count > Count) All = All.Take(Count).ToList();
        // A death must never turn into "walk straight back in". The event can arrive on the game's own
        // thread (offline progress), so it only raises a flag; the next tick acts on it.
        if (game.Entities.TryGetObjectById("MelvorBase:Combat", out GameEntity? combat) && combat is CombatEntity fighter)
            fighter.Action.PlayerDied += (_, _) => { if (ReferenceEquals(game, s_game)) s_died = true; };
        Changed?.Invoke();
    }

    /// <summary>No character is open any more.</summary>
    public static void Unbind()
    {
        if (s_game == null) return;
        s_game = null;
        s_character = "";
        Publish(new List<string>());
    }

    // ---------------------------------------------------------------- editing

    public static void Set(int index, GameEntity entity, Target target)
    {
        if (index < 0 || index >= All.Count) return;
        Slot slot = All[index];
        if (slot.Entity == entity.Id && slot.TargetId == target.Id && slot.Extra == target.Extra) return;
        StopIfDoing(slot);
        slot.Entity = entity.Id;
        slot.SkillName = entity.DisplayName.Text;
        slot.TargetId = target.Id;
        slot.Extra = target.Extra;
        slot.Name = target.Name;
        slot.State = "";
        slot.RetryAt = 0;
        slot.Following = "";
        Save();
        Changed?.Invoke();
    }

    public static void SetOn(int index, bool on)
    {
        if (index < 0 || index >= All.Count) return;
        Slot slot = All[index];
        if (slot.On == on) return;
        slot.On = on;
        slot.RetryAt = 0;
        slot.State = "";
        if (!on) StopIfDoing(slot);
        Save();
        Changed?.Invoke();
    }

    static void StopIfDoing(Slot slot)
    {
        if (s_game == null || !slot.HasTarget || !s_actions.TryGetValue(slot.Entity, out ActionComponent? action)) return;
        if (Targets.IsDoing(action, new Target(slot.TargetId, slot.Name, slot.Extra))) action.TryStop(force: true);
    }

    // ---------------------------------------------------------------- running

    /// <summary>Call a couple of times per second while a character is open. <paramref name="now"/> is any steady clock in seconds.</summary>
    public static void Tick(Game game, double now)
    {
        if (!ReferenceEquals(game, s_game)) return;
        bool changed = false;
        if (s_died)
        {
            s_died = false;
            foreach (Slot slot in All.Where(s => s.On && s.Entity == "MelvorBase:Combat"))
            {
                slot.On = false;
                slot.State = "turned off: your character died";
                changed = true;
            }
            if (changed) Save();
        }

        var holding = new List<string>();
        for (int i = 0; i < All.Count; i++)
        {
            Slot slot = All[i];
            string before = slot.State;
            try { Step(game, i, slot, now, holding); }
            catch (Exception e)
            {
                Log($"Slot {i + 1} ('{slot.Title}') failed: {e.Message}");
                slot.State = "waiting: an error occurred (see the log)";
                slot.RetryAt = now + 10;
            }
            changed |= before != slot.State;
        }
        Publish(holding);
        Order(game, holding);
        if (changed) Changed?.Invoke();
    }

    static void Step(Game game, int index, Slot slot, double now, List<string> holding)
    {
        if (!slot.On) { if (!slot.State.StartsWith("turned off")) slot.State = ""; return; }
        if (!slot.HasTarget) { slot.State = "choose an action"; return; }
        if (All.Take(index).Any(other => other.On && other.Entity == slot.Entity)) { slot.State = "waiting: the other slot uses this skill"; return; }
        if (!s_actions.TryGetValue(slot.Entity, out ActionComponent? action)) { slot.State = "waiting: this skill is not in the game"; return; }

        var target = new Target(slot.TargetId, slot.Name, slot.Extra);
        if (Targets.IsDoing(action, target))
        {
            slot.Following = Targets.Current(action)?.Id ?? "";
            slot.State = "running";
            holding.Add(slot.Entity);
            return;
        }
        bool nextTask = false;
        if (action.IsStarted)
        {
            // The skill is busy. Either the player is using it for something else (leave it alone), or this is
            // a slayer slot whose task was just completed: then it moves on to the new task monster.
            nextTask = slot.Extra == Targets.Slayer && slot.Following != "" && Targets.Current(action)?.Id == slot.Following;
            if (!nextTask) { slot.State = "waiting: this skill is doing something else"; return; }
        }
        if (now < slot.RetryAt) return;
        slot.RetryAt = now + RetrySeconds;

        if (!Targets.CanStart(action, target, out string problem)) { slot.State = "waiting: " + problem; return; }
        // A recipe that eats what the main action makes would start and stop for every single fish, and the
        // game reports each stop. So it waits for a small pile - or, when the pile has stopped growing
        // (nothing is making more), works through whatever there is.
        long batches = Targets.Batches(game, action, target);
        if (batches > slot.Pile) slot.GrewAt = now;
        slot.Pile = batches;
        if (batches == 0) { slot.State = "waiting: the materials have run out"; return; }
        if (batches < PileSize && now - slot.GrewAt < SettleSeconds) { slot.State = $"waiting: collecting materials ({batches}/{PileSize})"; return; }
        if (nextTask) action.TryStop(force: true);
        bool started = Beside(game, () => Targets.Start(game, action, target, out problem));
        if (!started) { slot.State = "waiting: " + problem; return; }
        Starts++;
        s_startedNow = true;
        slot.RetryAt = 0;
        slot.Following = Targets.Current(action)?.Id ?? "";
        slot.State = "running";
        holding.Add(slot.Entity);
    }

    /// <summary>Runs the game's own start code while the game sees nothing running, so it stops nothing.</summary>
    static bool Beside(Game game, Func<bool> start)
    {
        List<ActionComponent> running = Running(game.ActionManager);
        List<ActionComponent> kept = running.ToList();
        running.Clear();
        try { return start(); }
        finally
        {
            foreach (ActionComponent action in kept)
                if (action.IsStarted && !running.Contains(action)) running.Add(action);
        }
    }

    /// <summary>
    /// The game shows the last action of its list in the bottom bar. Slot actions are moved to the front so the
    /// bar keeps showing what the player chose.
    /// </summary>
    static void Order(Game game, List<string> holding)
    {
        List<ActionComponent> running = Running(game.ActionManager);
        if (running.Count < 2) return;
        List<ActionComponent> wanted = running.Where(a => holding.Contains(a.Entity.Id)).Concat(running.Where(a => !holding.Contains(a.Entity.Id))).ToList();
        bool reordered = !wanted.SequenceEqual(running);
        if (reordered)
        {
            running.Clear();
            running.AddRange(wanted);
        }
        // The bar bound itself to the slot's action the moment that started; point it back at the main one.
        if (!reordered && !s_startedNow) return;
        s_startedNow = false;
        try
        {
            if (s_footer == null || !Godot.GodotObject.IsInstanceValid(s_footer)) s_footer = GameAccess.FindNode<GameFooter>();
            s_footer?.Update();
        }
        catch (Exception e) { Log("The bottom bar could not be refreshed: " + e.Message); }
    }

    /// <summary>Tells other mods (the Action Queue) which skills are being kept running by a slot right now.</summary>
    static void Publish(List<string> holding)
    {
        string text = string.Join("|", holding);
        if (text == s_published) return;
        s_published = text;
        try { ((Godot.SceneTree)Godot.Engine.GetMainLoop()).Root.SetMeta(Targets.HeldMeta, text); } catch { }
    }
}
