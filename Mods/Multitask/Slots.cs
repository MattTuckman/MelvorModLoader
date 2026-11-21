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

/// <summary>One of the player's recent actions that is being kept running.</summary>
public sealed class Kept
{
    public string Entity { get; set; } = "";      // the skill, e.g. MelvorBase:Mining
    public string SkillName { get; set; } = "";
    public string TargetId { get; set; } = "";    // the rock, recipe, monster...
    public string Extra { get; set; } = "";       // combat only: the area
    public string Name { get; set; } = "";        // "Coal"

    /// <summary>What it is doing right now, in words (not saved).</summary>
    [JsonIgnore] public string State = "";
    /// <summary>True while it is stopped and waiting to be started again by this mod.</summary>
    [JsonIgnore] internal bool Pending;
    /// <summary>The Action Queue started it: the queue decides when it runs, this mod never starts it again.</summary>
    [JsonIgnore] internal bool ByQueue;
    [JsonIgnore] internal double RetryAt;
    [JsonIgnore] internal long Pile;              // a recipe: how many crafts the bank had materials for at the last look
    [JsonIgnore] internal double GrewAt;          // ...and when that number last went up
    /// <summary>It waits because the bank holds nothing for it at all (not: a small pile is being collected).
    /// Such an action makes room first when something new starts.</summary>
    [JsonIgnore] internal bool OutOfMaterials;

    [JsonIgnore] public string Title => $"{SkillName}: {Name}";
}

/// <summary>
/// The idea, in the player's words: "just an on/off switch per slot; whatever I do last is what counts".
///
/// The game stops every running action when a new one starts. With a slot switched on, the action the player
/// was doing before is started again next to the new one. So with one slot on, the last two actions run
/// together; with two, the last three; with all 15, every action the game has. Nothing has to be picked anywhere:
///   - start something new            -> it joins; when there is no room, an action whose materials ran out
///                                       drops out first, otherwise the oldest
///   - choose another rock/recipe     -> that skill simply does the new thing (a skill can only do one)
///   - press the game's own Stop      -> that action is dropped
///   - materials run out              -> it waits and goes on when there are some again
///   - the character dies             -> the fight is dropped, it never walks back in
///
/// How: the game's "one at a time" lives in one place, the list it walks to stop things. That list is put
/// aside for the moment an earlier action is started again through the game's own start code.
/// </summary>
public static class Slots
{
    /// <summary>The most extra slots: the game has 16 actions (15 skills you train by doing them, plus combat),
    /// so 15 extra slots run all of them at once. Farming and Ranching run on their own and need none.</summary>
    public const int Count = 15;
    const double RetrySeconds = 2;
    /// <summary>A recipe that waits for materials starts again once there is enough for this many crafts...</summary>
    const int PileSize = 10;
    /// <summary>...or once the materials have not grown for this long.</summary>
    const double SettleSeconds = 30;

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_activeActions")]
    static extern ref List<ActionComponent> Running(ActiveActionManager manager);

    sealed class Saved
    {
        /// <summary>How many extra slots are on (0.4.0+). Older files only have <see cref="On"/>.</summary>
        public int? Extra { get; set; }
        /// <summary>One "true" per slot that is on; still written so an older Multitask reads the first two.</summary>
        public List<bool> On { get; set; } = new();
        /// <summary>0.5.0+: false = an action whose materials run out is dropped instead of waiting.</summary>
        public bool? WaitForMaterials { get; set; }
        public List<Kept> Recent { get; set; } = new();
    }

    public static string Dir = "";
    public static Action<string> Log { get; set; } = _ => { };
    static string FilePath => Path.Combine(Dir, "multitask.json");

    static Game? s_game;
    static string s_character = "";
    static Dictionary<string, ActionComponent> s_actions = new();
    static GameFooter? s_footer;
    static volatile bool s_died;
    /// <summary>Set by the game's own "an action started" event: the game has just stopped everything else for it.</summary>
    static volatile bool s_gameStartedSomething;
    static volatile ActionComponent? s_lastStarted;
    static bool s_restoring, s_startedNow;
    static string s_published = "", s_savedAs = "";
    static int s_extra;   // extra slots on: slot i is on when i < s_extra
    static bool s_wait = true;
    static List<Kept> s_recent = new();

    /// <summary>The actions being kept, the latest first.</summary>
    public static IReadOnlyList<Kept> Recent => s_recent;
    public static bool IsOn(int slot) => slot >= 0 && slot < s_extra;
    /// <summary>How many extra slots are on (0 = the plain game).</summary>
    public static int Extra => s_extra;
    /// <summary>How many actions may run together: the latest one plus one per slot that is on.</summary>
    public static int Capacity => 1 + s_extra;
    /// <summary>True: an action whose materials run out keeps its place and goes on when there are some again.
    /// False: it is dropped from the list, as if the player had pressed Stop.</summary>
    public static bool WaitForMaterials => s_wait;
    /// <summary>How many times this mod started an action again since the character was opened.</summary>
    public static int Starts { get; private set; }
    /// <summary>The last thing worth telling the player.</summary>
    public static string Note { get; private set; } = "";
    public static event Action? Changed;

    // ---------------------------------------------------------------- loading and saving

    static Dictionary<string, Saved> Read()
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, Saved>>(File.ReadAllText(FilePath)) ?? new(); }
        catch { return new(); }   // also an older file with another layout: start clean
    }

    static string Signature() => s_extra + (s_wait ? "" : "d") + "|" + string.Join(";", s_recent.Select(k => $"{k.Entity},{k.TargetId},{k.Extra}"));

    static void Save()
    {
        if (s_character == "") return;
        string now = Signature();
        if (now == s_savedAs) return;
        try
        {
            // Only this character's entry is replaced in what is on disk right now.
            Dictionary<string, Saved> all = Read();
            all[s_character] = new Saved { Extra = s_extra, On = Enumerable.Repeat(true, s_extra).ToList(), WaitForMaterials = s_wait, Recent = s_recent };
            File.WriteAllText(FilePath, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
            s_savedAs = now;
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
        Note = "";
        Read().TryGetValue(s_character, out Saved? saved);
        s_extra = Math.Clamp(saved?.Extra ?? saved?.On.Count(on => on) ?? 0, 0, Count);
        s_wait = saved?.WaitForMaterials ?? true;
        s_recent = (saved?.Recent ?? new List<Kept>()).Where(k => k.Entity != "" && k.TargetId != "" && s_actions.ContainsKey(k.Entity))
            .GroupBy(k => k.Entity).Select(g => g.First()).ToList();
        // What was running when the game was closed is brought back by the game itself. Anything on file that
        // is not running now is waiting to be started again (only looked at while a slot is on).
        foreach (Kept k in s_recent) k.Pending = true;
        s_savedAs = Signature();
        PublishCapacity(Capacity);
        s_gameStartedSomething = false;
        // The game raises this whenever any action starts - also when the same one is started again, which
        // looks like "nothing changed" afterwards although the game stopped everything else for it.
        game.ActionManager.ActionStarted += (_, e) =>
        {
            if (!ReferenceEquals(game, s_game) || s_restoring) return;
            s_gameStartedSomething = true;
            s_lastStarted = e.Action;
        };
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
        Watching = "";
        PublishCapacity(1);
        Publish(new List<string>());
        Targets.PublishPlayerKept(Array.Empty<string>());
        PublishOrder(Array.Empty<string>());
        s_driven = "";
    }

    // ---------------------------------------------------------------- the switches

    static void PublishCapacity(int capacity)
    {
        try { ((Godot.SceneTree)Godot.Engine.GetMainLoop()).Root.SetMeta(Targets.CapacityMeta, capacity); } catch { }
    }

    /// <summary>Slots are on from the first one up: switching slot N on also switches on the ones before it,
    /// switching it off also switches off the ones after it.</summary>
    public static void SetOn(int slot, bool on)
    {
        if (slot < 0 || slot >= Count || IsOn(slot) == on) return;
        SetExtra(on ? slot + 1 : slot);
    }

    /// <summary>How many extra slots are on, 0 to <see cref="Count"/>.</summary>
    public static void SetExtra(int extra)
    {
        extra = Math.Clamp(extra, 0, Count);
        if (extra == s_extra) return;
        s_extra = extra;
        PublishCapacity(Capacity);
        Note = "";
        if (s_game != null) Trim(s_game, stopExtras: true);   // a slot switched off: what no longer fits is stopped
        Save();
        Changed?.Invoke();
    }

    public static void SetWaitForMaterials(bool wait)
    {
        if (wait == s_wait) return;
        s_wait = wait;
        Note = "";
        // Switched off: what already waits for materials goes now.
        if (!wait)
            foreach (Kept k in s_recent.Where(k => k.OutOfMaterials && !k.ByQueue && !IsRunning(k)).ToList())
            {
                s_recent.Remove(k);
                Note = $"{k.Title} was dropped: its materials ran out.";
            }
        Save();
        Changed?.Invoke();
    }

    /// <summary>Takes one action off the list (the x in the window): it is stopped if it runs, and not started again.</summary>
    public static bool Drop(int index)
    {
        if (index < 0 || index >= s_recent.Count) return false;
        Kept kept = s_recent[index];
        s_recent.RemoveAt(index);
        if (s_game != null && IsRunning(kept)) s_actions[kept.Entity].TryStop(force: true);
        Note = $"{kept.Title} was taken off the list.";
        Save();
        Changed?.Invoke();
        return true;
    }

    // ---------------------------------------------------------------- running

    static bool IsRunning(Kept k) =>
        s_actions.TryGetValue(k.Entity, out ActionComponent? action) && Targets.IsDoing(action, new Target(k.TargetId, k.Name, k.Extra));

    /// <summary>Call a couple of times per second while a character is open. <paramref name="now"/> is any steady clock in seconds.</summary>
    public static void Tick(Game game, double now)
    {
        if (!ReferenceEquals(game, s_game)) return;
        string before = Signature() + "|" + string.Join(";", s_recent.Select(k => k.State)) + "|" + Note;
        PublishCapacity(Capacity);
        try { Look(game, now); }
        catch (Exception e) { Log("Multitask failed: " + e.Message); }
        Save();
        if (before != Signature() + "|" + string.Join(";", s_recent.Select(k => k.State)) + "|" + Note) Changed?.Invoke();
    }

    static void Look(Game game, double now)
    {
        // 1. A death: the fight is dropped for good.
        if (s_died)
        {
            s_died = false;
            Kept? fight = s_recent.FirstOrDefault(k => k.Entity == "MelvorBase:Combat");
            if (fight != null)
            {
                s_recent.Remove(fight);
                Note = $"Your character died, so {fight.Title} was dropped.";
            }
        }

        // 2. What runs now. Something new, or a skill doing another thing than before, is the player's latest
        //    choice and goes to the front.
        var queueSkills = new HashSet<string>(Targets.QueueDriving().Split('|', StringSplitOptions.RemoveEmptyEntries));
        bool somethingNew = s_gameStartedSomething;
        s_gameStartedSomething = false;
        foreach (ActionComponent action in game.ActionManager.ActiveActions.ToList())
        {
            if (!s_actions.TryGetValue(action.Entity.Id, out ActionComponent? known) || !ReferenceEquals(known, action)) continue;
            Target? target = Targets.Current(action);
            if (target == null) continue;
            Kept? kept = s_recent.FirstOrDefault(k => k.Entity == action.Entity.Id);
            if (kept == null)
            {
                kept = new Kept { Entity = action.Entity.Id, SkillName = action.Entity.DisplayName.Text };
                s_recent.Insert(0, kept);
                somethingNew = true;
            }
            else if (kept.TargetId != target.Id || kept.Extra != target.Extra)
            {
                s_recent.Remove(kept);
                s_recent.Insert(0, kept);
                somethingNew = true;
            }
            kept.TargetId = target.Id;
            kept.Extra = target.Extra;
            kept.Name = target.Name;
            kept.Pending = false;
            kept.OutOfMaterials = false;
            kept.ByQueue = queueSkills.Contains(kept.Entity);
            kept.State = "running";
        }

        // What the game started last is the player's latest action, also when it is the same one as before.
        ActionComponent? last = s_lastStarted;
        s_lastStarted = null;
        if (last != null && s_recent.FindIndex(k => k.Entity == last.Entity.Id) is var at && at > 0 && last.IsStarted)
        {
            Kept latest = s_recent[at];
            s_recent.RemoveAt(at);
            s_recent.Insert(0, latest);
        }

        // With every slot off this is the plain game: remember what runs, start nothing.
        if (Capacity == 1)
        {
            s_recent.RemoveAll(k => !IsRunning(k));
            Trim(game, stopExtras: false);
            Publish(new List<string>());
            Targets.PublishPlayerKept(s_recent.Where(k => !k.ByQueue).Select(k => k.Entity));
            PublishOrder(s_recent.Select(k => k.Entity));
            NoteDriven();
            return;
        }

        // 3. What does not run.
        foreach (Kept kept in s_recent.ToList())
        {
            if (IsRunning(kept)) continue;
            if (kept.ByQueue) { s_recent.Remove(kept); continue; }   // the queue's own step: never ours to start again
            if (kept.Pending) continue;
            if (somethingNew) { kept.Pending = true; kept.RetryAt = 0; continue; }   // stopped by the game because of the new action
            // It stopped while nothing new was started: the player pressed Stop, or it ran out of something.
            ActionComponent action = s_actions[kept.Entity];
            if (Targets.Batches(game, action, new Target(kept.TargetId, kept.Name, kept.Extra)) == 0)
            {
                if (s_wait) { kept.Pending = true; kept.OutOfMaterials = true; continue; }   // out of materials: wait
                Note = $"{kept.Title} was dropped: its materials ran out.";
            }
            s_recent.Remove(kept);
        }

        // 4. No more than fits. With every slot off that is one action: plain game.
        Trim(game, stopExtras: false);

        // 5. Start again what is waiting.
        foreach (Kept kept in s_recent.Where(k => k.Pending).Reverse().ToList()) Restore(game, kept, now);

        var held = s_recent.Skip(1).Where(IsRunning).Select(k => k.Entity).ToList();
        Publish(held);
        // The Action Queue only starts its "while idle" action in a slot nobody else holds, running or waiting.
        Targets.PublishPlayerKept(s_recent.Where(k => !k.ByQueue).Select(k => k.Entity));
        PublishOrder(s_recent.Select(k => k.Entity));
        NoteDriven();
        Order(game);
    }

    static void Trim(Game game, bool stopExtras)
    {
        HashSet<string> standIns = StandIns();
        while (s_recent.Count > Capacity)
        {
            // Who makes room, first to last (the oldest first within each group; the latest action never):
            //   1. a stand-in another mod started while the player's own action had nothing to do,
            //   2. one of the player's actions that waits because its materials ran out (it does nothing anyway),
            //   3. the oldest of the player's own actions.
            // The Action Queue's own steps keep their place.
            int at = LastIndex(k => standIns.Contains(k.Entity) && !k.ByQueue);
            if (at < 0) at = LastIndex(k => k.OutOfMaterials && !k.ByQueue && !IsRunning(k));
            if (at < 0) at = s_recent.FindLastIndex(k => !k.ByQueue);
            if (at < 0) at = s_recent.Count - 1;
            Kept oldest = s_recent[at];
            s_recent.RemoveAt(at);
            // Normally it is not running any more (the game stopped it for the new action). It still is when
            // the queue started a step beside it, or when a slot was just switched off.
            if (IsRunning(oldest)) s_actions[oldest.Entity].TryStop(force: true);
        }
    }

    /// <summary>The oldest entry that matches, never the latest one (index 0); -1 when none does.</summary>
    static int LastIndex(Predicate<Kept> match)
    {
        int at = s_recent.FindLastIndex(match);
        return at > 0 ? at : -1;
    }

    // ---------------------------------------------------------------- other mods

    /// <summary>Scene-root meta any mod may set: "id|id" of skills it started only as a stand-in (to keep the
    /// character busy). Such an action is the first to make room when the player starts something.</summary>
    public const string StandInMeta = "MultitaskStandIn";
    /// <summary>The same idea as published by the "Default Action" mod: true while its stand-in runs (or the ids).</summary>
    public const string DefaultActionMeta = "DefaultActionDriving";
    /// <summary>Published here: every action kept, running or waiting, the latest first ("id|id").</summary>
    public const string OrderMeta = "MultitaskOrder";

    static string s_driven = "";       // the latest action while Default Action said "true": its stand-in
    static string s_orderPublished = "\0";

    static HashSet<string> StandIns()
    {
        var ids = new HashSet<string>();
        try
        {
            Godot.Window root = ((Godot.SceneTree)Godot.Engine.GetMainLoop()).Root;
            foreach (string meta in new[] { StandInMeta, DefaultActionMeta })
            {
                if (!root.HasMeta(meta)) continue;
                Godot.Variant value = root.GetMeta(meta);
                if (value.VariantType == Godot.Variant.Type.String)
                    foreach (string id in value.AsString().Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) ids.Add(id);
                else if (value.VariantType == Godot.Variant.Type.Bool && value.AsBool() && s_driven != "") ids.Add(s_driven);
            }
        }
        catch { }
        return ids;
    }

    /// <summary>Remembers which action Default Action is driving (the latest one while it says "true").</summary>
    static void NoteDriven()
    {
        try
        {
            Godot.Window root = ((Godot.SceneTree)Godot.Engine.GetMainLoop()).Root;
            bool driving = root.HasMeta(DefaultActionMeta) && root.GetMeta(DefaultActionMeta) is { VariantType: Godot.Variant.Type.Bool } v && v.AsBool();
            if (!driving) s_driven = "";
            else if (s_driven == "" && s_recent.Count > 0) s_driven = s_recent[0].Entity;
        }
        catch { }
    }

    static void PublishOrder(IEnumerable<string> entities)
    {
        string order = string.Join("|", entities);
        if (order == s_orderPublished) return;
        s_orderPublished = order;
        try { ((Godot.SceneTree)Godot.Engine.GetMainLoop()).Root.SetMeta(OrderMeta, order); } catch { }
    }

    static void Restore(Game game, Kept kept, double now)
    {
        ActionComponent action = s_actions[kept.Entity];
        var target = new Target(kept.TargetId, kept.Name, kept.Extra);
        if (action.IsStarted)
        {
            // The skill went on to something else in the meantime; step 2 picks that up at the next look.
            kept.State = "waiting: this skill is doing something else";
            return;
        }
        if (now < kept.RetryAt) return;
        kept.RetryAt = now + RetrySeconds;

        if (!Targets.CanStart(action, target, out string problem))
        {
            s_recent.Remove(kept);
            Note = $"{kept.Title} was dropped: {problem}.";
            return;
        }
        // A recipe that eats what another action makes would start and stop for every single fish, and the
        // game reports each stop. So it waits for a small pile - or, when the pile has stopped growing
        // (nothing is making more), works through whatever there is.
        long batches = Targets.Batches(game, action, target);
        if (batches > kept.Pile) kept.GrewAt = now;
        kept.Pile = batches;
        kept.OutOfMaterials = batches == 0;
        if (batches == 0 && !s_wait)
        {
            s_recent.Remove(kept);
            Note = $"{kept.Title} was dropped: its materials ran out.";
            return;
        }
        if (batches == 0) { kept.State = "waiting: the materials have run out"; return; }
        if (batches < PileSize && now - kept.GrewAt < SettleSeconds) { kept.State = $"waiting: collecting materials ({batches}/{PileSize})"; return; }

        if (!Beside(game, () => Targets.Start(game, action, target, out problem))) { kept.State = "waiting: " + problem; return; }
        Starts++;
        s_startedNow = true;
        kept.Pending = false;
        kept.RetryAt = 0;
        kept.State = "running";
    }

    /// <summary>Runs the game's own start code while the game sees nothing running, so it stops nothing.</summary>
    static bool Beside(Game game, Func<bool> start)
    {
        List<ActionComponent> running = Running(game.ActionManager);
        List<ActionComponent> kept = running.ToList();
        running.Clear();
        s_restoring = true;
        try { return start(); }
        finally
        {
            s_restoring = false;
            foreach (ActionComponent action in kept)
                if (action.IsStarted && !running.Contains(action)) running.Add(action);
        }
    }

    /// <summary>The skill whose page the player has open ("" for the bank, shop... or no character).</summary>
    public static string Watching { get; private set; } = "";

    /// <summary>
    /// The player opened another page. When that skill is one of the running actions, the bottom bar shows it
    /// (the game does the same for Farming and Ranching); otherwise it shows the latest action.
    /// </summary>
    public static void Watch(Game game, string entityId)
    {
        if (entityId == Watching) return;
        Watching = entityId;
        if (!ReferenceEquals(game, s_game)) return;
        try { Order(game); }
        catch (Exception e) { Log("The bottom bar could not follow the page: " + e.Message); }
    }

    /// <summary>
    /// The game shows the last action of its list in the bottom bar. The list is kept in the order "oldest
    /// first, latest last", so the bar shows what the player did last - or, when the page the player has open
    /// belongs to one of the running actions, that one.
    /// </summary>
    static void Order(Game game)
    {
        List<ActionComponent> running = Running(game.ActionManager);
        if (running.Count < 2) { s_startedNow = false; return; }
        int Rank(ActionComponent a)
        {
            if (a.Entity.Id == Watching) return -2;   // the page on screen: last, so the bar shows it
            int at = s_recent.FindIndex(k => k.Entity == a.Entity.Id);
            return at < 0 ? -1 : at;   // something this mod does not know: treat it as the very latest
        }
        List<ActionComponent> wanted = running.OrderByDescending(Rank).ToList();
        bool reordered = !wanted.SequenceEqual(running);
        if (reordered)
        {
            running.Clear();
            running.AddRange(wanted);
        }
        // The bar bound itself to whatever started last (also to what this mod just started again).
        if (!reordered && !s_startedNow) return;
        s_startedNow = false;
        try
        {
            if (s_footer == null || !Godot.GodotObject.IsInstanceValid(s_footer)) s_footer = GameAccess.FindNode<GameFooter>();
            s_footer?.Update();
        }
        catch (Exception e) { Log("The bottom bar could not be refreshed: " + e.Message); }
    }

    /// <summary>Tells other mods (the Action Queue) which skills are being kept running beside the latest action.</summary>
    static void Publish(List<string> held)
    {
        string text = string.Join("|", held);
        if (text == s_published) return;
        s_published = text;
        try { ((Godot.SceneTree)Godot.Engine.GetMainLoop()).Root.SetMeta(Targets.HeldMeta, text); } catch { }
    }
}
