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
public enum Until { Stops, Item, Level, Minutes, ItemLeft, Made }   // new kinds go at the end: saved queues store the number

/// <summary>Idle: not started yet. Waiting: its turn has come but it cannot run right now. Done: over (also when skipped).</summary>
public enum RunState { Idle, Running, Waiting, Done }

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
    /// <summary>Where the step stands in the current run (not saved).</summary>
    public RunState Run;
    internal double Seconds, RetryAt, GrewAt;
    internal long Pile, GainedAtStart;
    /// <summary>"Make N more": what came in before a pause, so carrying on does not start counting from zero.</summary>
    internal long MadeBefore;
    internal int OwnedAtStart;
    internal bool Ran, CountGains;
    internal string Following = "";   // a slayer step: the task monster it is fighting at the moment

    public string Goal => Until switch
    {
        Until.Item => $"until {Amount:N0} {SubjectName} in the bank",
        Until.Made => $"until {Amount:N0} more {SubjectName} are made",
        Until.ItemLeft => Amount <= 0 ? $"until no {SubjectName} is left in the bank" : $"until only {Amount:N0} {SubjectName} are left in the bank",
        Until.Level => $"until {SubjectName} level {Amount:0}",
        Until.Minutes => $"for {Amount:0.#} minute(s)",
        _ => "until it stops by itself",
    };
}

/// <summary>A queue kept under a name, to switch back to later.</summary>
public sealed class Preset
{
    public List<Step> Steps { get; set; } = new();
    public bool Repeat { get; set; }
}

/// <summary>
/// Runs the steps while the game is open. A step ends when its goal is reached or when the action stops by
/// itself (materials used up, bank full, death). If the player starts something else by hand, the queue pauses
/// instead of fighting them. The steps are kept per character in queue.json.
///
/// Normally the steps run one after another. When the Multitask mod has made room for several actions at once,
/// as many steps as fit run side by side: the fish are cooked while more are caught. A step that needs what an
/// earlier step makes waits for it instead of being skipped, and "until I own N" counts what was gathered,
/// not what happens to lie in the bank after another step has used some of it.
/// </summary>
public static class Queue
{
    sealed class Saved
    {
        public List<Step> Steps { get; set; } = new();
        public bool Repeat { get; set; }
        /// <summary>The saved queue the steps above came from ("" when they were never saved).</summary>
        public string Name { get; set; } = "";
        public Dictionary<string, Preset> Presets { get; set; } = new();
        public bool SideBySide { get; set; } = true;
        /// <summary>What to do while the queue waits and once it is done (null: nothing).</summary>
        public Step? Idle { get; set; }
    }

    /// <summary>Where the queue that was on screen goes when another one is loaded over unsaved changes.</summary>
    public const string AutoName = "Unsaved queue (auto)";

    public static string Dir = "";
    public static Action<string> Log { get; set; } = _ => { };
    static string FilePath => Path.Combine(Dir, "queue.json");

    static Game? s_game;
    static string s_character = "";
    static Dictionary<string, Item> s_items = new();
    static bool s_ranThisRound, s_died;
    static double s_clock;
    /// <summary>Everything the bank has received, per item, since the character was opened (other steps may use it up again).</summary>
    static readonly Dictionary<string, long> s_gained = new();

    /// <summary>A waiting recipe starts again once there is enough for this many crafts, or the materials stopped growing for a while.</summary>
    const int PileSize = 10;
    const double SettleSeconds = 30, RetrySeconds = 2;

    /// <summary>Run steps side by side when the Multitask mod has room for it (the player can switch this off).</summary>
    public static bool SideBySide { get; private set; } = true;
    /// <summary>How many steps may run at once right now.</summary>
    public static int Lanes => SideBySide ? Targets.MultitaskCapacity() : 1;

    public static List<Step> Steps { get; private set; } = new();
    public static bool Repeat { get; private set; }
    static Dictionary<string, Preset> s_presets = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The saved queue the current steps came from, or "" when they are not saved.</summary>
    public static string Name { get; private set; } = "";
    public static bool Running { get; private set; }
    public static int Index { get; private set; }
    /// <summary>The last thing worth telling the player (why it paused, that it finished...).</summary>
    public static string Note { get; private set; } = "";
    public static event Action? Changed;

    /// <summary>
    /// The "while idle" action: done while every step the queue has left is waiting (for materials, for its skill
    /// to be free...) and after the queue has finished. Only Entity, TargetId, Extra and Name are used. It only
    /// ever takes a free slot: the player's own actions are never pushed out for it.
    /// </summary>
    public static Step? Idle { get; private set; }
    /// <summary>What the "while idle" action is up to, for the window ("" when there is nothing to say).</summary>
    public static string IdleState { get; private set; } = "";
    /// <summary>True while the idle action runs because the queue started it (so the queue may stop it again).</summary>
    static bool s_idleOwned, s_idleBlocked, s_idleRanOut;
    static double s_idleRetryAt, s_idleGrewAt;
    static long s_idlePile;

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
        s_presets = new Dictionary<string, Preset>(saved?.Presets ?? new(), StringComparer.OrdinalIgnoreCase);
        Name = saved?.Name is { } savedName && s_presets.ContainsKey(savedName) ? savedName : "";
        SideBySide = saved?.SideBySide ?? true;
        Idle = saved?.Idle is { Entity: not "", TargetId: not "" } idle ? idle : null;
        IdleState = "";
        s_idleOwned = s_idleBlocked = s_idleRanOut = false;
        lock (s_gained) s_gained.Clear();
        if (Bank(game) is { } bank)
            bank.Inventory.ItemQuantityChanged += (_, e) =>
            {
                if (!ReferenceEquals(game, s_game) || e.QuantityChange <= 0) return;
                lock (s_gained) s_gained[e.Item.Id] = s_gained.GetValueOrDefault(e.Item.Id) + e.QuantityChange;
            };
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
            all[s_character] = new Saved { Steps = Steps, Repeat = Repeat, Name = Name, Presets = new Dictionary<string, Preset>(s_presets), SideBySide = SideBySide, Idle = Idle };
            File.WriteAllText(FilePath, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) { Log("The queue could not be saved: " + e.Message); }
    }

    // ---------------------------------------------------------------- editing

    public static void Add(Step step) { Steps.Add(step); Save(); Changed?.Invoke(); }

    public static void AddRange(IEnumerable<Step> steps) { Steps.AddRange(steps); Save(); Changed?.Invoke(); }

    /// <summary>
    /// Stops the action of a step that is running, if the character is still on it. Taking a running step out of
    /// the queue (or emptying the queue) must not leave its action going on: to the player that looks as if the
    /// queue were still at work.
    /// </summary>
    static void StopOwn(Step step)
    {
        if (s_game == null || !Running || step.Run != RunState.Running) return;
        ActionComponent? action = Targets.Find(s_game, step.Entity);
        if (action != null && Targets.IsDoing(action, new Target(step.TargetId, step.Name, step.Extra))) action.TryStop(force: true);
    }

    /// <summary>
    /// Tells Multitask which skills the queue is driving: its running steps, and the idle action while the queue
    /// runs it. Multitask leaves those alone (it never starts them again and never counts them as the player's).
    /// </summary>
    static void Publish()
    {
        if (!Running) { Targets.PublishDriving(""); return; }
        IEnumerable<string> driving = Steps.Where(s => s.Run == RunState.Running).Select(s => s.Entity);
        if (s_idleOwned && Idle != null) driving = driving.Append(Idle.Entity);
        Targets.PublishDriving(string.Join("|", driving.Distinct()));
    }

    static void SetIndex()
    {
        int first = Steps.FindIndex(s => s.Run != RunState.Done);
        Index = first < 0 ? 0 : first;
    }

    public static void RemoveAt(int index)
    {
        if (index < 0 || index >= Steps.Count) return;
        StopOwn(Steps[index]);
        Steps.RemoveAt(index);
        if (Steps.Count == 0) Finish("The queue is empty.");
        else if (Running) SetIndex();
        else if (index < Index) Index--;
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
        foreach (Step step in Steps) StopOwn(step);
        Steps.Clear();
        Name = "";
        Finish(Running ? "The queue was emptied, so what it had started was stopped." : "");
        Save();
        Changed?.Invoke();
    }

    public static void SetRepeat(bool repeat) { Repeat = repeat; Save(); Changed?.Invoke(); }

    public static void SetSideBySide(bool on) { SideBySide = on; Save(); Changed?.Invoke(); }

    /// <summary>Chooses the "while idle" action (null: nothing). One the queue had started is stopped when it changes.</summary>
    public static void SetIdle(Step? idle)
    {
        if (idle != null && (idle.Entity == "" || idle.TargetId == "")) idle = null;
        bool same = idle != null && Idle != null && idle.Entity == Idle.Entity && idle.TargetId == Idle.TargetId && idle.Extra == Idle.Extra;
        if (!same)
        {
            StopIdle();
            Idle = idle == null ? null : new Step { Entity = idle.Entity, TargetId = idle.TargetId, Extra = idle.Extra, Name = idle.Name };
            IdleState = "";
            s_idleBlocked = s_idleRanOut = false;
            s_idleRetryAt = 0;
            Trace(Idle == null ? "while idle: nothing" : $"while idle: {Idle.Name}");
        }
        Save();
        Publish();
        Changed?.Invoke();
    }

    // ---------------------------------------------------------------- saved queues

    public static List<string> PresetNames => s_presets.Keys.OrderBy(n => n == AutoName ? 1 : 0).ThenBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

    public static Preset? GetPreset(string name) => s_presets.TryGetValue(name, out Preset? preset) ? preset : null;

    static Step Copy(Step s) => new()
    {
        Entity = s.Entity, TargetId = s.TargetId, Extra = s.Extra, Name = s.Name,
        Until = s.Until, Amount = s.Amount, Subject = s.Subject, SubjectName = s.SubjectName,
    };

    static bool Same(Step a, Step b) => a.Entity == b.Entity && a.TargetId == b.TargetId && a.Extra == b.Extra
        && a.Until == b.Until && a.Amount == b.Amount && a.Subject == b.Subject;

    /// <summary>True when the steps on screen differ from the saved queue they came from.</summary>
    public static bool Modified =>
        Name != "" && s_presets.TryGetValue(Name, out Preset? preset)
        && (preset.Repeat != Repeat || preset.Steps.Count != Steps.Count || preset.Steps.Where((s, i) => !Same(s, Steps[i])).Any());

    /// <summary>Keeps the current steps under a name (an existing queue of that name is replaced).</summary>
    public static bool SavePreset(string? name, out string message)
    {
        name = Share.CleanName(name);
        if (name == "") { message = "Type a name for the queue first."; return false; }
        if (Steps.Count == 0) { message = "There are no steps to save. Add some first."; return false; }
        bool replaced = s_presets.ContainsKey(name);
        s_presets[name] = new Preset { Steps = Steps.Select(Copy).ToList(), Repeat = Repeat };
        Name = s_presets.Keys.First(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
        Save();
        message = replaced ? $"Saved over '{Name}' ({Steps.Count} step(s))." : $"Saved as '{Name}' ({Steps.Count} step(s)).";
        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// Puts a saved queue in place of the current one. A running queue keeps running, from the first step of the
    /// new one. Steps that were never saved (or were changed since) are kept under <see cref="AutoName"/>.
    /// </summary>
    public static bool LoadPreset(string name, out string message)
    {
        if (!s_presets.TryGetValue(name, out Preset? preset)) { message = "That saved queue no longer exists."; return false; }
        bool wasRunning = Running;
        bool kept = false;
        if (Steps.Count > 0 && (Name == "" || Modified) && !string.Equals(name, AutoName, StringComparison.OrdinalIgnoreCase))
        {
            s_presets[AutoName] = new Preset { Steps = Steps.Select(Copy).ToList(), Repeat = Repeat };
            kept = true;
        }
        foreach (Step step in Steps) StopOwn(step);   // a clean switch: the old queue's actions do not linger
        Steps = preset.Steps.Select(Copy).ToList();
        Repeat = preset.Repeat;
        Name = s_presets.Keys.First(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
        Index = 0;
        s_ranThisRound = false;
        Running = wasRunning && Steps.Count > 0;
        Note = "";
        Save();
        message = $"Loaded '{Name}' ({Steps.Count} step(s))" + (Running ? ", and it goes on running from its first step." : ".")
            + (kept ? $" What was here before is kept as '{AutoName}'." : "");
        Changed?.Invoke();
        return true;
    }

    public static bool DeletePreset(string name, out string message)
    {
        if (!s_presets.Remove(name)) { message = "That saved queue no longer exists."; return false; }
        if (string.Equals(Name, name, StringComparison.OrdinalIgnoreCase)) Name = "";
        Save();
        message = $"Deleted the saved queue '{name}'. The steps on screen are untouched.";
        Changed?.Invoke();
        return true;
    }

    /// <summary>The share code of a saved queue, or of the steps on screen when <paramref name="name"/> is null.</summary>
    public static string? ExportCode(string? name)
    {
        if (name == null) return Steps.Count == 0 ? null : Share.Export(Name, Steps, Repeat);
        return s_presets.TryGetValue(name, out Preset? preset) ? Share.Export(name, preset.Steps, preset.Repeat) : null;
    }

    /// <summary>Reads a share code and keeps its queue as a saved queue (nothing on screen changes).</summary>
    public static bool ImportCode(string? code, out string importedName, out string message)
    {
        importedName = "";
        if (!Share.TryImport(code, out string name, out List<Step> steps, out bool repeat, out message)) return false;
        if (name == "" || string.Equals(name, AutoName, StringComparison.OrdinalIgnoreCase)) name = "Shared queue";
        string unique = name;
        for (int n = 2; s_presets.ContainsKey(unique); n++) unique = $"{name} ({n})";
        s_presets[unique] = new Preset { Steps = steps, Repeat = repeat };
        importedName = unique;
        Save();

        // Tell the player how much of it exists in their game (levels aside: locked steps are skipped when they come up).
        int known = 0;
        if (s_game != null)
            foreach (Step step in steps)
            {
                ActionComponent? action = Targets.Find(s_game, step.Entity);
                if (action != null && Targets.All(action).Any(t => t.Id == step.TargetId && t.Extra == step.Extra)) known++;
            }
        message = $"Imported '{unique}': {steps.Count} step(s)"
            + (known == steps.Count ? "." : $", {steps.Count - known} of them for things your game does not have (those will be skipped).")
            + " Select it and press Load to use it.";
        Changed?.Invoke();
        return true;
    }

    // ---------------------------------------------------------------- running

    public static void Start()
    {
        if (Steps.Count == 0) { Note = "Add a step first."; Changed?.Invoke(); return; }
        if (Index >= Steps.Count) Index = 0;
        for (int i = 0; i < Steps.Count; i++)
        {
            Step s = Steps[i];
            if (s.State != "paused") s.MadeBefore = 0;            // "make N more" carries on after a pause, else counts afresh
            s.State = "";
            s.Run = i < Index ? RunState.Done : RunState.Idle;   // after a pause: carry on where it stopped
            s.Ran = false;
            s.Seconds = s.RetryAt = s.GrewAt = 0;
            s.Pile = 0;
        }
        Running = true;
        s_ranThisRound = false;
        Note = "";
        // An idle action left running from the last time belongs to the player now: the queue does not take it over.
        s_idleOwned = s_idleBlocked = s_idleRanOut = false;
        s_idleRetryAt = 0;
        IdleState = "";
        Trace($"started at step {Index + 1} of {Steps.Count}, up to {Lanes} at once: " + string.Join(" | ", Steps.Select(s => $"{s.Name} {s.Goal}")));
        Changed?.Invoke();
    }

    /// <summary>
    /// A small diary of what the queue did and why (queue-log.txt next to the mod). It exists so that
    /// "it skipped a step" can be answered from facts; it holds nothing but step names and item counts.
    /// </summary>
    static void Trace(string text)
    {
        try
        {
            string file = Path.Combine(Dir, "queue-log.txt");
            var info = new FileInfo(file);
            if (info.Exists && info.Length > 200_000)
            {
                string[] lines = File.ReadAllLines(file);
                File.WriteAllLines(file, lines.Skip(lines.Length / 2));
            }
            File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{s_character}] {text}{Environment.NewLine}");
        }
        catch { }
    }

    public static void Pause(string why)
    {
        if (!Running) return;
        SetIndex();
        Trace($"paused at step {Index + 1}: {why}");
        Running = false;
        foreach (Step s in Steps.Where(s => s.Run is RunState.Running or RunState.Waiting))
        {
            s.State = "paused";
            if (s.Ran) s.MadeBefore += Gained(s) - s.GainedAtStart;   // keep what "make N more" has already made
        }
        // Like the steps' own actions, an idle action that runs keeps running: the player decides from here.
        s_idleOwned = false;
        IdleState = "";
        Note = why;
        Publish();
        Changed?.Invoke();
    }

    static void Finish(string why)
    {
        if (Running) Trace($"ended: {why}");
        StopIdle();   // emptied or cut short: the idle action the queue started goes with it (a real finish lets go of it first)
        IdleState = "";
        Running = false;
        Index = 0;
        Note = why;
        Publish();
    }

    static string Number(Step step) => $"step {Steps.IndexOf(step) + 1} '{step.Name}' ({step.Goal})";

    static void End(Step step, string state)
    {
        step.State = state;
        step.Run = RunState.Done;
        Trace($"{Number(step)}: {state}");
    }

    /// <summary>Call a few times per second while a character is open.</summary>
    public static void Tick(Game game, double seconds)
    {
        if (!Running || !ReferenceEquals(game, s_game)) return;
        if (Steps.Count == 0) { Finish("The queue is empty."); Changed?.Invoke(); return; }
        s_clock += seconds;

        // 0. The idle action was a fight and the character died in it: never walk straight back in. (A combat step
        //    cannot run at the same time - one skill, one thing - so the death was the idle action's.)
        if (s_died && s_idleOwned && Idle?.Entity == "MelvorBase:Combat" && !Steps.Any(s => s.Run == RunState.Running && s.Entity == Idle.Entity))
        {
            s_died = false;
            s_idleOwned = false;
            s_idleBlocked = true;
            IdleState = "stopped: your character died (it starts again when you start the queue)";
            Trace("while idle: the character died, the fight is not started again");
        }

        // 1. The steps that are at work.
        foreach (Step step in Steps.Where(s => s.Run == RunState.Running).ToList())
        {
            if (!Running) break;   // one of them paused the queue
            Guard(step, () => Watch(game, step, seconds));
        }

        // 2. Room for more? Waiting steps try again; then the next steps in order take the free places.
        if (Running)
        {
            int lanes = Lanes;
            foreach (Step step in Steps.ToList())
            {
                if (!Running) break;
                if (step.Run == RunState.Waiting) { Guard(step, () => Begin(game, step)); continue; }
                if (step.Run != RunState.Idle) continue;
                if (Steps.Count(s => s.Run is RunState.Running or RunState.Waiting) >= lanes) continue;
                // A skill does one thing at a time: a later step for the same skill waits its turn.
                if (Steps.Any(s => s != step && s.Run is RunState.Running or RunState.Waiting && s.Entity == step.Entity)) continue;
                Guard(step, () => Begin(game, step));
            }
        }

        // 3. All done?
        if (Running && Steps.All(s => s.Run == RunState.Done))
        {
            if (Repeat && s_ranThisRound)
            {
                s_ranThisRound = false;
                foreach (Step s in Steps) { s.State = ""; s.Run = RunState.Idle; s.Ran = false; s.Seconds = s.RetryAt = s.GrewAt = 0; s.Pile = s.MadeBefore = 0; }
            }
            else
            {
                s_idleOwned = false;   // a real finish: the idle action keeps going, as the player's own from now on
                Finish(s_ranThisRound ? "The queue has finished." : "Nothing in the queue could be done, so it stopped.");
                try { IdleAfterEnd(game); }
                catch (Exception e) { Log("The idle action could not be started: " + e.Message); }
            }
        }

        // 4. Every step that is left waits: the idle action takes a free slot meanwhile (or makes room again).
        if (Running)
        {
            try { KeepIdle(game); }
            catch (Exception e) { Log("The idle action failed: " + e.Message); IdleState = "failed (see the log)"; }
        }
        if (Running) SetIndex();
        Publish();
        Changed?.Invoke();
    }

    static void Guard(Step step, Action work)
    {
        try { work(); }
        catch (Exception e)
        {
            Log($"Step '{step.Name}' failed: {e.Message}");
            End(step, "skipped: an error occurred (see the log)");
        }
    }

    /// <summary>True while a step before this one is not over yet: it may still make what this one needs.</summary>
    static bool EarlierAtWork(Step step) => Steps.TakeWhile(s => s != step).Any(s => s.Run != RunState.Done);

    /// <summary>
    /// True while more of what this step uses may still come in: an earlier step is not over, or something
    /// running beside it (the player's own mining, an action Multitask keeps going) makes one of its materials.
    /// "Until it stops" takes the stop at its word when the supply is not the queue's own: the player may mine forever.
    /// </summary>
    static bool MoreComing(Game game, Step step, ActionComponent action, Target target) =>
        EarlierAtWork(step) || (step.Until != Until.Stops && Supplied(game, action, target));

    /// <summary>
    /// True while some other running action (the player's own, one kept by Multitask, another step, the idle
    /// action) makes an item this recipe uses: mining uncut emeralds beside crafting emeralds.
    /// </summary>
    static bool Supplied(Game game, ActionComponent action, Target target)
    {
        HashSet<string> needs = Targets.Ingredients(action, target);
        if (needs.Count == 0) return false;
        // What runs beside it, and on what. Every product counts, not just the main one: a fishing spot gives several fish.
        var beside = new Dictionary<string, string>();
        foreach (ActionComponent other in game.ActionManager.ActiveActions.ToList())
            if (!ReferenceEquals(other, action) && Targets.Current(other) is Target doing) beside[other.Entity.Id] = doing.Id;
        if (beside.Count == 0) return false;
        foreach ((Item item, List<Maker> makers) in Planner.Makers(game))
            if (needs.Contains(item.Id) && makers.Any(m => beside.TryGetValue(m.Entity.Id, out string? on) && on == m.Target.Id)) return true;
        return false;
    }

    // ---------------------------------------------------------------- the "while idle" action

    static Target IdleTarget(Step idle) => new(idle.TargetId, idle.Name, idle.Extra);

    static void StopAction(ActionComponent action) { if (!action.TryStop()) action.TryStop(force: true); }

    /// <summary>Stops the idle action if the queue started it and it still runs.</summary>
    static void StopIdle()
    {
        if (!s_idleOwned) return;
        s_idleOwned = false;
        if (s_game == null || Idle == null) return;
        ActionComponent? action = Targets.Find(s_game, Idle.Entity);
        if (action != null && Targets.IsDoing(action, IdleTarget(Idle))) StopAction(action);
    }

    /// <summary>
    /// How many more actions fit right now: the slots Multitask gives (1 without it), less the queue's running
    /// steps, the idle action the queue runs, and the player's own actions - running or waiting to be started
    /// again by Multitask. Below 1 there is no room.
    /// </summary>
    static int FreeSlots(Game game)
    {
        var queue = Running ? Steps.Where(s => s.Run == RunState.Running).Select(s => s.Entity).ToHashSet() : new HashSet<string>();
        string idle = s_idleOwned && Idle != null ? Idle.Entity : "";
        IEnumerable<string> player = Targets.PlayerKept() ?? game.ActionManager.ActiveActions.Select(a => a.Entity.Id).ToHashSet();
        int used = queue.Count + (idle != "" ? 1 : 0) + player.Count(e => !queue.Contains(e) && e != idle);
        return Targets.MultitaskCapacity() - used;
    }

    /// <summary>Why the idle action may not run right now while the queue runs, or "" when it may.</summary>
    static string IdleBlocker(Game game, ActionComponent action, Target target, bool doing)
    {
        if (s_idleBlocked) return IdleState;
        int running = Steps.Count(s => s.Run == RunState.Running);
        if (!Steps.Any(s => s.Run is RunState.Waiting or RunState.Idle) || running >= Lanes) return "";   // the queue is at work
        if (Steps.Any(s => s.Run == RunState.Running && s.Entity == action.Entity.Id)) return "not now: a step is using this skill";
        if (action.IsStarted && !doing) return "not now: this skill is doing something else";
        HashSet<string> uses = Targets.Ingredients(action, target);
        if (uses.Count > 0)
            foreach (Step step in Steps.Where(s => s.Run != RunState.Done))
                if (Targets.Find(game, step.Entity) is { } stepAction
                    && Targets.Ingredients(stepAction, new Target(step.TargetId, step.Name, step.Extra)).Overlaps(uses))
                    return "not now: it would use up what a step of the queue needs";
        int free = FreeSlots(game) + (s_idleOwned && doing ? 1 : 0);
        if (free <= 0) return "not now: no free slot (switch on a Multitask slot for it)";
        return "ok";
    }

    /// <summary>While the queue runs: start the idle action when every step left is waiting, stop it when a step needs the place.</summary>
    static void KeepIdle(Game game)
    {
        if (Idle == null) { IdleState = ""; return; }
        ActionComponent? action = Targets.Find(game, Idle.Entity);
        if (action == null) { IdleState = "this skill is not in the game"; return; }
        Target target = IdleTarget(Idle);
        bool doing = Targets.IsDoing(action, target);
        if (s_idleOwned && !doing)
        {
            s_idleOwned = false;
            if (Targets.Batches(game, action, target) == 0)
            {
                // Out of materials: it starts again once there is a little pile, like a waiting step.
                s_idleRanOut = true;
                s_idlePile = 0;
                s_idleGrewAt = s_clock;
                Trace($"while idle: {Idle.Name} ran out of materials");
            }
            else
            {
                // Not out of anything: the player stopped it or started something by hand, or the bank is full.
                // Starting it again would fight them.
                s_idleBlocked = true;
                IdleState = "stopped (by you, or the bank is full); it comes back when you start the queue again";
                Trace($"while idle: {Idle.Name} was stopped, it is not started again in this run");
            }
        }
        string why = IdleBlocker(game, action, target, doing);
        if (why != "ok")
        {
            if (s_idleOwned && doing)
            {
                StopAction(action);
                s_idleOwned = false;
                Trace($"while idle: stopped {Idle.Name} ({(why == "" ? "the queue is at work again" : why)})");
            }
            if (!s_idleBlocked) IdleState = why;
            return;
        }
        if (doing) { IdleState = s_idleOwned ? "running now" : "running now (started by you)"; return; }
        TryStartIdle(game, action, target);
    }

    static void TryStartIdle(Game game, ActionComponent action, Target target)
    {
        if (s_clock < s_idleRetryAt) return;
        s_idleRetryAt = s_clock + RetrySeconds;
        if (!Targets.CanStart(action, target, out string problem)) { IdleState = "cannot start: " + problem; return; }
        long batches = Targets.Batches(game, action, target);
        if (batches == 0)
        {
            if (!s_idleRanOut) { s_idleRanOut = true; s_idlePile = 0; s_idleGrewAt = s_clock; }
            IdleState = "waiting: its materials have run out";
            return;
        }
        if (s_idleRanOut && batches < PileSize)
        {
            if (batches > s_idlePile) s_idleGrewAt = s_clock;
            s_idlePile = batches;
            if (s_clock - s_idleGrewAt < SettleSeconds) { IdleState = $"waiting: collecting materials ({batches}/{PileSize})"; return; }
        }
        bool others = game.ActionManager.ActiveActions.Count > 0;
        if (!(others ? Targets.StartBeside(game, action, target, out problem) : Targets.Start(game, action, target, out problem)))
        {
            IdleState = "cannot start: " + problem;
            return;
        }
        s_idleOwned = true;
        s_idleRanOut = false;
        s_idlePile = 0;
        IdleState = "running now";
        Trace($"while idle: started {Idle!.Name}{(others ? " beside what runs" : "")}");
    }

    /// <summary>A step is about to start: the idle action makes room when it uses the same skill or the last free slot.</summary>
    static void MakeRoomFor(Game game, Step step, bool beside)
    {
        if (!s_idleOwned || Idle == null) return;
        if (beside && Idle.Entity != step.Entity && FreeSlots(game) > 0) return;   // there is room for both
        s_idleOwned = false;
        ActionComponent? action = Targets.Find(game, Idle.Entity);
        if (action != null && Targets.IsDoing(action, IdleTarget(Idle))) StopAction(action);
        IdleState = "made room for a step";
        Trace($"while idle: stopped {Idle.Name} to make room for {Number(step)}");
    }

    /// <summary>The queue has finished: start the idle action (it is the player's own from then on).</summary>
    static void IdleAfterEnd(Game game)
    {
        if (Idle == null) return;
        ActionComponent? action = Targets.Find(game, Idle.Entity);
        if (action == null) return;
        Target target = IdleTarget(Idle);
        string Tell(string text) { Note = $"{Note} {text}"; Trace("after the end: " + text); return text; }
        if (Targets.IsDoing(action, target)) { Tell($"{Idle.Name} goes on (your idle action)."); return; }
        if (s_idleBlocked) return;
        if (action.IsStarted) { Tell($"{Idle.Name} was not started: that skill is doing something else."); return; }
        if (FreeSlots(game) <= 0) { Tell($"{Idle.Name} was not started: there is no free slot for it."); return; }
        if (!Targets.CanStart(action, target, out string problem)) { Tell($"{Idle.Name} was not started: {problem}."); return; }
        if (Targets.Batches(game, action, target) == 0) { Tell($"{Idle.Name} was not started: its materials have run out."); return; }
        bool others = game.ActionManager.ActiveActions.Count > 0;
        if (others ? Targets.StartBeside(game, action, target, out problem) : Targets.Start(game, action, target, out problem))
            Tell($"Now doing your idle action: {Idle.Name}.");
        else Tell($"{Idle.Name} was not started: {problem}.");
    }

    static string WaitingText(Step step) =>
        EarlierAtWork(step) ? "waiting: for materials from an earlier step" : "waiting: for materials (still being gathered)";

    static void Wait(Step step, string why)
    {
        if (step.Run != RunState.Waiting) Trace($"{Number(step)}: {why}");
        step.Run = RunState.Waiting;
        step.State = why;
    }

    static void Begin(Game game, Step step)
    {
        ActionComponent? action = Targets.Find(game, step.Entity);
        if (action == null) { End(step, "skipped: this skill is not in the game"); return; }
        if (!step.Ran && step.Until is Until.Item or Until.Level or Until.ItemLeft && Reached(game, step))
        {
            // Not needed: say what was found, so "it never mined" explains itself.
            End(step, step.Until switch
            {
                Until.Item => $"done: the bank already holds {Owned(game, step):N0} {step.SubjectName}, nothing to do",
                Until.ItemLeft => $"done: already only {Owned(game, step):N0} {step.SubjectName} left, nothing to do",
                _ => $"done: {step.SubjectName} is already level {Level(game, step)}, nothing to do",
            });
            return;
        }
        if (step.Run == RunState.Waiting && s_clock < step.RetryAt) return;
        step.RetryAt = s_clock + RetrySeconds;

        var target = new Target(step.TargetId, step.Name, step.Extra);
        bool earlier = MoreComing(game, step, action, target);
        string problem;
        if (earlier)
        {
            // An earlier step, or something running beside, may yet provide what this one lacks: wait instead of giving up.
            if (!Targets.CanStart(action, target, out problem)) { Wait(step, "waiting: " + problem); return; }
            long batches = Targets.Batches(game, action, target);
            if (batches > step.Pile) step.GrewAt = s_clock;
            step.Pile = batches;
            if (batches == 0) { Wait(step, WaitingText(step)); return; }
            // Starting for every single fish would have the game report "action stopped" each time: work in piles.
            if (batches < PileSize && s_clock - step.GrewAt < SettleSeconds) { Wait(step, $"waiting: collecting materials ({batches}/{PileSize})"); return; }
        }

        bool othersAtWork = Steps.Any(s => s != step && s.Run == RunState.Running);
        MakeRoomFor(game, step, othersAtWork);
        bool started = othersAtWork ?Targets.StartBeside(game, action, target, out problem) : Targets.Start(game, action, target, out problem);
        if (!started)
        {
            if (earlier) Wait(step, "waiting: " + problem);
            else End(step, step.Ran ? "done (it stopped by itself)" : "skipped: " + problem);
            return;
        }
        if (!step.Ran)
        {
            step.Seconds = 0;
            step.OwnedAtStart = Owned(game, step);
            step.GainedAtStart = Gained(step);
            step.CountGains = Lanes > 1;
        }
        step.Ran = true;
        step.Run = RunState.Running;
        step.Following = Targets.Current(action)?.Id ?? "";
        step.State = "running";
        s_ranThisRound = true;
        s_died = false;
        Trace($"{Number(step)}: started{(othersAtWork ? " beside the others" : "")}{(step.Until is Until.Item or Until.ItemLeft or Until.Made ? $", bank holds {Owned(game, step):N0} {step.SubjectName}" : "")}{(step.Until == Until.Made && step.MadeBefore > 0 ? $", {step.MadeBefore:N0} made before the pause" : "")}");
    }

    static void Watch(Game game, Step step, double seconds)
    {
        ActionComponent? action = Targets.Find(game, step.Entity);
        if (action == null) { End(step, "skipped: this skill is not in the game"); return; }
        var wanted = new Target(step.TargetId, step.Name, step.Extra);
        Target? now = Targets.Current(action);
        if (now == null)
        {
            if (s_died) { s_died = false; Pause("Your character died, so the queue is paused."); return; }
            // Did the player start something else? Actions kept running by Multitask, and the queue's own
            // other steps, do not count.
            ActionComponent? main = Targets.Main(game);
            bool idleOfOurs = main != null && s_idleOwned && Idle != null && main.Entity.Id == Idle.Entity && Targets.IsDoing(main, IdleTarget(Idle));
            if (main != null && !idleOfOurs && !Steps.Any(s => s.Run == RunState.Running && s.Entity == main.Entity.Id))
            {
                Pause("You started something else, so the queue is paused.");
                return;
            }
            // It stopped by itself. Out of materials while an earlier step, or something running beside it, is
            // still making them: wait for more instead of calling the step done.
            if (Targets.Batches(game, action, wanted) == 0 && MoreComing(game, step, action, wanted))
            {
                step.Pile = 0;
                step.GrewAt = s_clock;
                Wait(step, WaitingText(step));
                return;
            }
            End(step, $"done (it stopped by itself)");
            return;
        }
        if (!Targets.IsDoing(action, wanted))
        {
            // A slayer step follows its master: the task was completed, so walk to the new task monster.
            if (step.Extra == Targets.Slayer && now.Id == step.Following)
            {
                bool others = Steps.Any(s => s != step && s.Run == RunState.Running);
                if (others ? Targets.StartBeside(game, action, wanted, out string problem) : Targets.Start(game, action, wanted, out problem))
                    step.Following = Targets.Current(action)?.Id ?? "";
                else End(step, "done: " + problem);
                return;
            }
            Pause("You changed what this skill is doing, so the queue is paused.");
            return;
        }
        step.Following = now.Id;

        step.Seconds += seconds;
        if (Reached(game, step))
        {
            // Only this step's own action is stopped: everything else that runs goes on.
            if (!action.TryStop()) action.TryStop(force: true);
            End(step, "done");
            return;
        }
        step.State = "running  " + Progress(game, step);
    }

    static BankEntity? Bank(Game game) => game.Entities.TryGetObjectById("MelvorBase:Bank", out GameEntity? e) ? e as BankEntity : null;

    static int Owned(Game game, Step step) =>
        s_items.TryGetValue(step.Subject, out Item? item) ? Bank(game)?.Inventory.GetQuantity(item) ?? 0 : 0;

    static long Gained(Step step) { lock (s_gained) return s_gained.GetValueOrDefault(step.Subject); }

    /// <summary>
    /// What counts for "until I own N". One step at a time: what the bank holds. Side by side: what the bank
    /// held at the start plus what came in since - another step may be using the item up as fast as it comes.
    /// </summary>
    static long Counted(Game game, Step step) =>
        step.CountGains && step.Ran ? Math.Max(Owned(game, step), step.OwnedAtStart + (Gained(step) - step.GainedAtStart)) : Owned(game, step);

    /// <summary>"Make N more": how many of the item came into the bank since the step first started (pauses included).</summary>
    static long Made(Step step) => step.MadeBefore + (step.Ran ? Gained(step) - step.GainedAtStart : 0);

    static int Level(Game game, Step step) =>
        game.LeveledEntities.FirstOrDefault(s => s.DisplayName.Text == step.Subject)?.Experience.Level ?? 0;

    static bool Reached(Game game, Step step) => step.Until switch
    {
        Until.Item => Counted(game, step) >= step.Amount,
        Until.ItemLeft => Owned(game, step) <= step.Amount,   // counts down: food being eaten, materials being used
        Until.Level => Level(game, step) >= step.Amount,
        Until.Minutes => step.Seconds >= step.Amount * 60,
        Until.Made => Made(step) >= step.Amount,
        _ => false,
    };

    static string Clock(double seconds) => $"{(int)(seconds / 60)}:{(int)(seconds % 60):00}";

    static string Progress(Game game, Step step) => step.Until switch
    {
        Until.Item => $"{Counted(game, step):N0} / {step.Amount:N0}",
        Until.ItemLeft => $"{Owned(game, step):N0} left, stops at {step.Amount:N0}",
        Until.Level => $"level {Level(game, step)} / {step.Amount:0}",
        Until.Minutes => $"{Clock(step.Seconds)} / {Clock(step.Amount * 60)}",
        Until.Made => $"{Made(step):N0} / {step.Amount:N0} made",
        _ => Clock(step.Seconds),
    };

    /// <summary>One line for the little on-screen reminder.</summary>
    public static string Headline
    {
        get
        {
            if (!Running) return "";
            var atWork = Steps.Where(s => s.Run == RunState.Running).ToList();
            string meanwhile = s_idleOwned && Idle != null ? $"   (meanwhile: {Idle.Name})" : "";
            if (atWork.Count == 0) return Index < Steps.Count ? $"Queue {Index + 1}/{Steps.Count}: {Steps[Index].Name}  -  {Steps[Index].State}{meanwhile}" : "";
            if (atWork.Count == 1) return $"Queue {Steps.IndexOf(atWork[0]) + 1}/{Steps.Count}: {atWork[0].Name}  -  {atWork[0].State}{meanwhile}";
            return "Queue: " + string.Join("   +   ", atWork.Select(s => $"{s.Name} - {s.State.Replace("running  ", "")}")) + meanwhile;
        }
    }

    /// <summary>For tests: true while the idle action runs because the queue started it.</summary>
    public static bool IdleRunsForQueue => s_idleOwned;
}
