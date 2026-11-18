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
public enum Until { Stops, Item, Level, Minutes, ItemLeft }   // new kinds go at the end: saved queues store the number

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
    internal int OwnedAtStart;
    internal bool Ran, CountGains;
    internal string Following = "";   // a slayer step: the task monster it is fighting at the moment

    public string Goal => Until switch
    {
        Until.Item => $"until {Amount:N0} {SubjectName} in the bank",
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
            all[s_character] = new Saved { Steps = Steps, Repeat = Repeat, Name = Name, Presets = new Dictionary<string, Preset>(s_presets), SideBySide = SideBySide };
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

    static void Publish() => Targets.PublishDriving(Running ? string.Join("|", Steps.Where(s => s.Run == RunState.Running).Select(s => s.Entity).Distinct()) : "");

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
            s.State = "";
            s.Run = i < Index ? RunState.Done : RunState.Idle;   // after a pause: carry on where it stopped
            s.Ran = false;
            s.Seconds = s.RetryAt = s.GrewAt = 0;
            s.Pile = 0;
        }
        Running = true;
        s_ranThisRound = false;
        Note = "";
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
        foreach (Step s in Steps.Where(s => s.Run is RunState.Running or RunState.Waiting)) s.State = "paused";
        Note = why;
        Publish();
        Changed?.Invoke();
    }

    static void Finish(string why)
    {
        if (Running) Trace($"ended: {why}");
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
                foreach (Step s in Steps) { s.State = ""; s.Run = RunState.Idle; s.Ran = false; s.Seconds = s.RetryAt = s.GrewAt = 0; s.Pile = 0; }
            }
            else Finish(s_ranThisRound ? "The queue has finished." : "Nothing in the queue could be done, so it stopped.");
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
        bool earlier = EarlierAtWork(step);
        string problem;
        if (earlier)
        {
            // An earlier step is still at work and may yet provide what this one lacks: wait instead of giving up.
            if (!Targets.CanStart(action, target, out problem)) { Wait(step, "waiting: " + problem); return; }
            long batches = Targets.Batches(game, action, target);
            if (batches > step.Pile) step.GrewAt = s_clock;
            step.Pile = batches;
            if (batches == 0) { Wait(step, "waiting: for materials from an earlier step"); return; }
            // Starting for every single fish would have the game report "action stopped" each time: work in piles.
            if (batches < PileSize && s_clock - step.GrewAt < SettleSeconds) { Wait(step, $"waiting: collecting materials ({batches}/{PileSize})"); return; }
        }

        bool othersAtWork = Steps.Any(s => s != step && s.Run == RunState.Running);
        bool started = othersAtWork ? Targets.StartBeside(game, action, target, out problem) : Targets.Start(game, action, target, out problem);
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
        Trace($"{Number(step)}: started{(othersAtWork ? " beside the others" : "")}{(step.Until is Until.Item or Until.ItemLeft ? $", bank holds {Owned(game, step):N0} {step.SubjectName}" : "")}");
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
            if (main != null && !Steps.Any(s => s.Run == RunState.Running && s.Entity == main.Entity.Id))
            {
                Pause("You started something else, so the queue is paused.");
                return;
            }
            // It stopped by itself. Out of materials while an earlier step is still making them: wait for more.
            if (EarlierAtWork(step) && Targets.Batches(game, action, wanted) == 0)
            {
                step.Pile = 0;
                step.GrewAt = s_clock;
                Wait(step, "waiting: for materials from an earlier step");
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

    static int Level(Game game, Step step) =>
        game.LeveledEntities.FirstOrDefault(s => s.DisplayName.Text == step.Subject)?.Experience.Level ?? 0;

    static bool Reached(Game game, Step step) => step.Until switch
    {
        Until.Item => Counted(game, step) >= step.Amount,
        Until.ItemLeft => Owned(game, step) <= step.Amount,   // counts down: food being eaten, materials being used
        Until.Level => Level(game, step) >= step.Amount,
        Until.Minutes => step.Seconds >= step.Amount * 60,
        _ => false,
    };

    static string Clock(double seconds) => $"{(int)(seconds / 60)}:{(int)(seconds % 60):00}";

    static string Progress(Game game, Step step) => step.Until switch
    {
        Until.Item => $"{Counted(game, step):N0} / {step.Amount:N0}",
        Until.ItemLeft => $"{Owned(game, step):N0} left, stops at {step.Amount:N0}",
        Until.Level => $"level {Level(game, step)} / {step.Amount:0}",
        Until.Minutes => $"{Clock(step.Seconds)} / {Clock(step.Amount * 60)}",
        _ => Clock(step.Seconds),
    };

    /// <summary>One line for the little on-screen reminder.</summary>
    public static string Headline
    {
        get
        {
            if (!Running) return "";
            var atWork = Steps.Where(s => s.Run == RunState.Running).ToList();
            if (atWork.Count == 0) return Index < Steps.Count ? $"Queue {Index + 1}/{Steps.Count}: {Steps[Index].Name}  -  {Steps[Index].State}" : "";
            if (atWork.Count == 1) return $"Queue {Steps.IndexOf(atWork[0]) + 1}/{Steps.Count}: {atWork[0].Name}  -  {atWork[0].State}";
            return "Queue: " + string.Join("   +   ", atWork.Select(s => $"{s.Name} - {s.State.Replace("running  ", "")}"));
        }
    }
}
