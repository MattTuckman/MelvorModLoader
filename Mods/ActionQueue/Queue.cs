using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Components.Actions;
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.Entities;
using GamesByMalcs.Melvor2.Entities.Bank;
using GamesByMalcs.Melvor2.Entities.Combat;
using GamesByMalcs.Melvor2.Components.ArtisanAction;
using GamesByMalcs.Melvor2.Entities.Agility;
using GamesByMalcs.Melvor2.Entities.Hunting;
using GamesByMalcs.Melvor2.EventBus;
using GamesByMalcs.Melvor2.Saves;
using GamesByMalcs.Melvor2.Components.ItemSales;

namespace ActionQueue;

/// <summary>When a step is over.</summary>
public enum Until { Stops, Item, Level, Minutes, ItemLeft, Made, Times, Spent }   // new kinds go at the end: saved queues store the number

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
    /// <summary>The lane (1..MaxLanes): with steps in more than one lane, every lane does its own steps in order, side by side.</summary>
    public int Lane { get; set; } = 1;
    /// <summary>A recipe's other way of making for this step (Smithing's Sacrificial Anvil): its id to switch it on
    /// when the step starts, "" to switch it off, null (older steps) to leave the game's switch as it is.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Mode { get; set; }

    /// <summary>A "sell" step is not an action: TargetId is the item, Amount how many to keep in the bank (0: sell all).</summary>
    public const string SellEntity = "ActionQueue:Sell";
    [JsonIgnore] public bool IsSell => Entity == SellEntity;

    /// <summary>What happened to this step in the current run (not saved).</summary>
    public string State = "";
    /// <summary>Where the step stands in the current run (not saved).</summary>
    public RunState Run;
    internal double Seconds, RetryAt, GrewAt;
    internal long Pile, GainedAtStart, DoneAtStart, SpentAtStart;
    /// <summary>"Make N more" / "N times" / "use up N": what was counted before a pause, so carrying on does not start from zero.</summary>
    internal long CountBefore;
    internal int OwnedAtStart;
    internal bool Ran, CountGains;
    internal string Following = "";   // a slayer step: the task monster it is fighting at the moment

    public string Goal => IsSell ? (Amount <= 0 ? "sell all of them" : $"sell all but {Amount:N0}") : Until switch
    {
        Until.Item => $"until {Amount:N0} {SubjectName} in the bank",
        Until.Made => $"until {Amount:N0} more {SubjectName} are made",
        Until.ItemLeft => Amount <= 0 ? $"until no {SubjectName} is left in the bank" : $"until only {Amount:N0} {SubjectName} are left in the bank",
        Until.Level => $"until {SubjectName} level {Amount:0}",
        Until.Minutes => $"for {Amount:0.#} minute(s)",
        Until.Times => Amount == 1 ? "once" : $"{Amount:N0} times",
        Until.Spent => $"until {Amount:N0} {SubjectName} are used up",
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
        /// <summary>The idle action also runs while the queue is not running (paused, finished, never used).</summary>
        public bool IdleAlways { get; set; }
        /// <summary>The game's own Create button (on the recipe pages) adds the recipe to the queue.</summary>
        public bool GameCreate { get; set; }
        /// <summary>Where the queue stood when the game last saved (null: nothing to carry on).</summary>
        public Snapshot? Run { get; set; }
    }

    /// <summary>
    /// Where a run stood when the game saved itself. Written only then, so it always matches the save the game
    /// loads next time; the queue carries on from it when the character is opened again.
    /// </summary>
    sealed class Snapshot
    {
        public string SaveId { get; set; } = "";
        /// <summary>The step list it belongs to: edited since, it is not used.</summary>
        public string Steps { get; set; } = "";
        public bool Running { get; set; }
        public bool Resume { get; set; }
        public bool RanThisRound { get; set; }
        public bool IdleOwned { get; set; }
        public List<StepSnapshot> Progress { get; set; } = new();
    }

    sealed class StepSnapshot
    {
        public RunState Run { get; set; }
        public string State { get; set; } = "";
        public bool Ran { get; set; }
        public double Seconds { get; set; }
        /// <summary>"Make N more" / "N times" / "use up N": what was counted so far.</summary>
        public long Count { get; set; }
        /// <summary>"Until I own N" side by side: what it counts from (gains so far included).</summary>
        public long OwnedBase { get; set; }
        public bool CountGains { get; set; }
        public string Following { get; set; } = "";
    }

    /// <summary>Where the queue that was on screen goes when another one is loaded over unsaved changes.</summary>
    public const string AutoName = "Unsaved queue (auto)";

    public static string Dir = "";
    public static Action<string> Log { get; set; } = _ => { };
    static string FilePath => Path.Combine(Dir, "queue.json");

    static Game? s_game;
    static string s_character = "";
    static string s_saveId = "";
    static Dictionary<string, Item> s_items = new();
    static bool s_ranThisRound;
    static volatile bool s_died;   // set from the game's own thread too (a death while offline progress is computed)
    /// <summary>True after a pause: the next Start carries on (finished steps stay finished, wherever they are now).</summary>
    static bool s_resume;
    static double s_clock;
    /// <summary>Everything the bank has received, per item, since the character was opened (other steps may use it up again).</summary>
    static readonly Dictionary<string, long> s_gained = new();
    /// <summary>What each skill has used up, per "skill|item", since the character was opened (for "use up N"): only
    /// what the skill's own actions took, the way the game's own "Ingredients Spent" counts it, not what was sold.</summary>
    static readonly Dictionary<string, long> s_spent = new();
    /// <summary>How many times each skill has finished one action since the character was opened (for "N times").</summary>
    static readonly Dictionary<string, long> s_done = new();

    /// <summary>A waiting recipe starts again once there is enough for this many crafts, or the materials stopped growing for a while.</summary>
    const int PileSize = 10;
    const double SettleSeconds = 30, RetrySeconds = 2;
    /// <summary>Seconds the "always" idle action waits after a character is opened or offline progress was computed.</summary>
    const double SettleAfterLoad = 5;

    /// <summary>Run steps side by side when the Multitask mod has room for it (the player can switch this off).</summary>
    public static bool SideBySide { get; private set; } = true;
    /// <summary>How many steps may run at once right now.</summary>
    public static int Lanes => SideBySide || LaneMode ? Capacity() : 1;

    /// <summary>How many lanes a queue can have.</summary>
    public const int MaxLanes = Targets.MostActions;   // as many lanes as Multitask can run actions at once (was 5)
    static int LaneOf(Step step) => Math.Clamp(step.Lane, 1, MaxLanes);
    /// <summary>
    /// True when the steps are spread over more than one lane: then every lane does its own steps one after another,
    /// and the lanes run side by side. With every step in lane 1 the queue works as it always did.
    /// </summary>
    public static bool LaneMode => Steps.Any(s => LaneOf(s) > 1);
    /// <summary>The step a lane is at: its first one that is not over.</summary>
    static Step? Head(int lane) => Steps.FirstOrDefault(s => LaneOf(s) == lane && s.Run != RunState.Done);
    /// <summary>Lanes that started something in this round (with "start over", a lane that did nothing does not go round again).</summary>
    static readonly HashSet<int> s_laneRan = new();

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

    const string CombatId = "MelvorBase:Combat";

    /// <summary>
    /// "Something changed" for the window. On the game's own thread (offline progress) the window must not be
    /// touched: it is told later, on the main thread.
    /// </summary>
    static volatile bool s_changedLater;
    static void RaiseChanged()
    {
        if (!Targets.OnMainThread) { s_changedLater = true; return; }
        s_changedLater = false;
        Changed?.Invoke();
    }

    /// <summary>For tests and the window: offline progress the queue took part in (game seconds, steps started).</summary>
    public static double OfflineSeconds { get; private set; }
    public static int OfflineStarts { get; private set; }
    public static string OfflineLog { get; private set; } = "";

    /// <summary>
    /// The queue's turn while the game computes offline progress: called on the game's own thread, once a game
    /// second, between two of its ticks (so nothing else touches the game at that moment).
    /// </summary>
    static bool s_toldFightOffline;

    /// <summary>How many actions ran at once at the last look (online), or the most seen (offline).</summary>
    static int s_running;

    /// <summary>
    /// How many actions may run at once. Multitask tells it on the scene root, but only from its first frame on: while
    /// the game computes the time a character was closed (right after opening it), that note is still the one from
    /// before. The game itself has then loaded what ran (a fight and the queue's mining: two), which is proof enough.
    /// </summary>
    static int Capacity() => Math.Max(Targets.MultitaskCapacity(), Targets.OnMainThread ? 1 : s_running);

    /// <summary>True for a fight while the game computes offline progress: the queue must neither start nor end it then.</summary>
    static bool HandsOff(ActionComponent action) => action.Entity.Id == CombatId && !Targets.OnMainThread;

    /// <summary>Offline, with a fight running: a step may only start next to it (with a free Multitask slot), never in its place.</summary>
    static bool FightKeptOffline(Game game) => !Targets.OnMainThread && game.ActionManager.ActiveActions.Any(a => a.Entity.Id == CombatId);

    public static void OfflineTick(Game game, double seconds)
    {
        if (!ReferenceEquals(game, s_game)) return;
        OfflineSeconds += seconds;
        if (!Running) return;
        // A fight going on (a fight step, or the player's own fight Multitask keeps beside the queue): the queue does
        // not touch the fight offline - starting or ending one would wake the game's music player on this thread -
        // but its other steps go on (until 0.9.0 the whole queue stood still while any fight ran).
        if (game.ActionManager.ActiveActions.Any(a => a.Entity.Id == CombatId))
        {
            if (!s_toldFightOffline) Trace("offline: a fight is running; it is left alone, the other steps go on beside it");
            s_toldFightOffline = true;
        }
        string before = string.Join(",", Steps.Select(s => s.Run));
        Tick(game, seconds);
        string after = string.Join(",", Steps.Select(s => s.Run));
        if (before != after)
        {
            OfflineStarts++;
            OfflineLog += $"{OfflineSeconds / 3600:0.00}h: {after}; ";
            Trace($"offline, {OfflineSeconds / 3600:0.00} h in: {after}");
        }
    }

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

    /// <summary>The idle action also runs while the queue is not running: whenever the character does nothing at all.</summary>
    public static bool IdleAlways { get; private set; }
    /// <summary>The game's own Create button on the recipe pages adds the recipe to the queue (see <see cref="GameButtons"/>).</summary>
    public static bool GameCreate { get; private set; }
    /// <summary>Not before this moment (queue clock): just after a character is opened or offline progress ends, the game and Multitask settle first.</summary>
    static double s_idleNotBefore;
    /// <summary>What ran at the last look, to notice the player starting something by hand (that lifts a block).</summary>
    static HashSet<string> s_runningBefore = new();

    /// <summary>Skills whose action the player stopped with one of the game's own buttons, and when (Environment.TickCount64).</summary>
    static readonly Dictionary<string, long> s_handStops = new();

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
        IdleAlways = saved?.IdleAlways ?? false;
        GameCreate = saved?.GameCreate ?? false;
        IdleState = "";
        s_idleOwned = s_idleBlocked = s_idleRanOut = false;
        s_idleNotBefore = s_clock + SettleAfterLoad;
        s_runningBefore = new HashSet<string>();
        lock (s_handStops) s_handStops.Clear();
        s_died = false;
        lock (s_gained) { s_gained.Clear(); s_spent.Clear(); }
        if (Bank(game) is { } bank)
            bank.Inventory.ItemQuantityChanged += (_, e) =>
            {
                if (!ReferenceEquals(game, s_game) || e.QuantityChange <= 0) return;
                lock (s_gained) s_gained[e.Item.Id] = s_gained.GetValueOrDefault(e.Item.Id) + e.QuantityChange;
            };
        Running = false;
        s_resume = false;
        s_running = 0;
        Index = 0;
        Note = "";
        // A death must never turn into "walk straight back in": remember it, the runner pauses on it.
        if (game.Entities.TryGetObjectById("MelvorBase:Combat", out GameEntity? combat) && combat is CombatEntity fighter)
            fighter.Action.PlayerDied += (_, _) => { if (ReferenceEquals(game, s_game)) s_died = true; };
        CountActions(game);
        WatchStops(game);
        s_saveId = ActiveSaveId();
        Targets.PrimeMeta();
        try { Restore(game, saved?.Run); }
        catch (Exception e) { Log("The queue could not carry on from where it was: " + e.Message); }
        if (!s_savesWatched)
        {
            s_savesWatched = true;
            SaveManager.LocalSaved += (_, _) => { if (s_game is { IsSaveLoaded: true } && ActiveSaveId() == s_saveId) Save(withRun: true); };
        }
        RaiseChanged();
    }

    static bool s_savesWatched;

    /// <summary>For tests: what the queue picked up when the character was opened ("" when nothing).</summary>
    public static string Restored { get; private set; } = "";

    static string ActiveSaveId()
    {
        try { return typeof(SaveManager).GetProperty("ActiveSaveId", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null)?.ToString() ?? ""; }
        catch { return ""; }
    }

    static string Signature(bool lanes = true) => string.Join("\n", Steps.Select(s => $"{s.Entity}|{s.TargetId}|{s.Extra}|{s.Until}|{s.Amount}|{s.Subject}" + (lanes ? $"|{LaneOf(s)}" : "")));

    static Snapshot TakeSnapshot() => new()
    {
        SaveId = s_saveId,
        Steps = Signature(),
        Running = Running,
        Resume = s_resume,
        RanThisRound = s_ranThisRound,
        IdleOwned = s_idleOwned,
        Progress = Steps.Select(s => new StepSnapshot
        {
            Run = s.Run, State = s.State, Ran = s.Ran, Seconds = s.Seconds,
            // a paused step keeps what it had counted at the pause; a live one counts on until now
            Count = Running && s.Ran && s.Run is RunState.Running or RunState.Waiting ? Counter(s) : s.CountBefore,
            OwnedBase = s.OwnedAtStart + (s.CountGains && s.Ran ? Gained(s) - s.GainedAtStart : 0),
            CountGains = s.CountGains, Following = s.Following,
        }).ToList(),
    };

    /// <summary>
    /// The character was opened again: carry on where the queue stood when the game last saved itself. This runs
    /// before the game computes the time it was closed, so the steps go on through that time too.
    /// </summary>
    static void Restore(Game game, Snapshot? snap)
    {
        Restored = "";
        if (snap == null || !snap.Running && !snap.Resume) return;
        if (snap.SaveId != s_saveId || s_saveId == "" || snap.Steps != Signature() && (LaneMode || snap.Steps != Signature(lanes: false)) || snap.Progress.Count != Steps.Count) return;
        for (int i = 0; i < Steps.Count; i++)
        {
            Step s = Steps[i];
            StepSnapshot p = snap.Progress[i];
            s.Run = p.Run;
            s.State = p.State;
            s.Ran = p.Ran;
            s.Seconds = p.Seconds;
            s.CountBefore = p.Count;
            s.OwnedAtStart = (int)Math.Min(int.MaxValue, p.OwnedBase);
            s.CountGains = p.CountGains;
            s.Following = p.Following;
            // the counters start from nothing in a new session: what came before is in CountBefore / OwnedAtStart
            s.GainedAtStart = Gained(s);
            s.DoneAtStart = DoneBy(s.Entity);
            s.SpentAtStart = SpentOf(s);
            s.RetryAt = 0;
            s.GrewAt = s_clock;
            s.Pile = 0;
            // the game loads its actions with the save; one that is not going any more is simply started again
            if (snap.Running && s.Run == RunState.Running
                && !(Targets.Find(game, s.Entity) is { } action && Targets.IsDoing(action, new Target(s.TargetId, s.Name, s.Extra))))
                s.Run = RunState.Idle;
        }
        Running = snap.Running;
        s_resume = snap.Resume;
        s_ranThisRound = snap.RanThisRound;
        s_laneRan.Clear();
        foreach (Step s in Steps.Where(s => s.Ran)) s_laneRan.Add(LaneOf(s));
        s_idleOwned = snap.IdleOwned && Idle != null && Targets.Find(game, Idle.Entity) is { } idle && Targets.IsDoing(idle, IdleTarget(Idle));
        SetIndex();
        Restored = string.Join(",", Steps.Select(s => s.Run)) + (Running ? " running" : " paused");
        Trace($"opened again: carries on where it was at the last save ({Restored}): " + string.Join(" | ", Steps.Select(s => $"{s.Name} {s.Goal} [{s.Run}{(s.Run == RunState.Running ? " " + Progress(game, s) : "")}]")));
        Publish();
    }

    /// <param name="withRun">The game is saving itself: also write where the run stands (else what was written then stays).</param>
    static void Save(bool withRun = false)
    {
        if (s_character == "") return;
        try
        {
            // Only this character's entry is replaced in what is on disk right now.
            Dictionary<string, Saved> all = Read();
            Snapshot? run = withRun ? TakeSnapshot() : all.TryGetValue(s_character, out Saved? old) ? old.Run : null;
            all[s_character] = new Saved { Steps = Steps, Repeat = Repeat, Name = Name, Presets = new Dictionary<string, Preset>(s_presets), SideBySide = SideBySide, Idle = Idle, IdleAlways = IdleAlways, GameCreate = GameCreate, Run = run };
            File.WriteAllText(FilePath, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) { Log("The queue could not be saved: " + e.Message); }
    }

    // ---------------------------------------------------------------- editing

    public static void Add(Step step) { Steps.Add(step); Save(); RaiseChanged(); }

    public static void AddRange(IEnumerable<Step> steps) { Steps.AddRange(steps); Save(); RaiseChanged(); }

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
        // Not running: only an idle action the queue has started is its own (Multitask must not adopt it as the player's).
        if (!Running) { Targets.PublishDriving(s_idleOwned && Idle != null ? Idle.Entity : ""); return; }
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
        RaiseChanged();
    }

    /// <summary>
    /// Moves a step, also while the queue runs. What a step has done stays with the step, not with its row: a
    /// running step goes on running wherever it is moved (nothing is stopped), a finished one stays finished, and
    /// the new order decides what starts next.
    /// </summary>
    public static bool Move(int index, int by) => MoveTo(index, by) >= 0;

    /// <summary>Like <see cref="Move"/>; returns the step's new place, or -1 when it could not move.</summary>
    public static int MoveTo(int index, int by)
    {
        int to = index + by;
        if (index >= 0 && index < Steps.Count && LaneMode)
        {
            // In lanes the order that counts is the one inside the lane: swap with the next step of the same lane.
            int lane = LaneOf(Steps[index]);
            while (to >= 0 && to < Steps.Count && LaneOf(Steps[to]) != lane) to += Math.Sign(by);
        }
        if (index < 0 || index >= Steps.Count || to < 0 || to >= Steps.Count) return -1;
        (Steps[index], Steps[to]) = (Steps[to], Steps[index]);
        if (Running)
        {
            LineUpAgain();
            Trace($"moved a step while running: {string.Join(" | ", Steps.Select(s => $"{s.Name} [{s.Run}]"))}");
        }
        Save();
        RaiseChanged();
        return to;
    }

    /// <summary>Puts a step into another lane (1..MaxLanes). A waiting step lines up again; a running one goes on.</summary>
    public static void SetLane(int index, int lane)
    {
        if (index < 0 || index >= Steps.Count) return;
        lane = Math.Clamp(lane, 1, MaxLanes);
        Step step = Steps[index];
        if (LaneOf(step) == lane) return;
        step.Lane = lane;
        if (Running && step.Run == RunState.Waiting) { step.Run = RunState.Idle; step.State = ""; }
        if (step.Ran) s_laneRan.Add(lane);
        if (Running) LineUpAgain();
        Trace($"{Number(step)}: now in lane {lane}");
        Save();
        RaiseChanged();
    }

    /// <summary>
    /// After the order or the lanes changed while running. A waiting step holds a place: when a step that has not
    /// started yet now comes before it (in lanes: when it is no longer the step its lane is at), it lines up again
    /// (what it has counted is kept).
    /// </summary>
    static void LineUpAgain()
    {
        bool laneMode = LaneMode;
        for (int i = 0; i < Steps.Count; i++)
        {
            Step s = Steps[i];
            if (s.Run != RunState.Waiting) continue;
            bool behind = laneMode ? Head(LaneOf(s)) != s : Steps.Take(i).Any(o => o.Run == RunState.Idle);
            if (behind) { s.Run = RunState.Idle; s.State = ""; }
        }
        SetIndex();
    }

    public static void Clear()
    {
        foreach (Step step in Steps) StopOwn(step);
        Steps.Clear();
        Name = "";
        Finish(Running ? "The queue was emptied, so what it had started was stopped." : "");
        Save();
        RaiseChanged();
    }

    public static void SetRepeat(bool repeat) { Repeat = repeat; Save(); RaiseChanged(); }

    public static void SetSideBySide(bool on) { SideBySide = on; Save(); RaiseChanged(); }

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
        RaiseChanged();
    }

    /// <summary>"Also when the queue is not running": the idle action fills every moment the character does nothing.</summary>
    public static void SetIdleAlways(bool on)
    {
        if (IdleAlways == on) return;
        IdleAlways = on;
        s_idleBlocked = false;
        s_idleRetryAt = 0;
        IdleState = "";
        if (!on && !Running) StopIdle();   // switched off: an idle action it started goes too
        Trace(on ? "idle action: also when the queue is not running" : "idle action: only while the queue runs");
        Save();
        Publish();
        RaiseChanged();
    }

    public static void SetGameCreate(bool on)
    {
        if (GameCreate == on) return;
        GameCreate = on;
        Trace(on ? "the game's Create button adds to the queue" : "the game's Create button works as usual");
        Save();
        RaiseChanged();
    }

    /// <summary>
    /// A recipe from the game's own page ("+ Queue", or Create with the setting on). It always goes to the end of the
    /// queue. A running queue goes on with what it does; a queue with steps left that is not running stays as it is
    /// (it is the player's to start). With nothing left to do (empty, or every step finished), the queue is started
    /// with the new step, so the recipe begins at once and the next one lines up behind it. Returns what to tell.
    /// </summary>
    public static string AddFromGame(Step step)
    {
        bool left = Steps.Any(s => s.Run != RunState.Done);
        bool wasRunning = Running;
        Steps.Add(step);
        Save();
        int number = Steps.Count;
        Trace($"added from the game's page: {Number(step)}");
        if (wasRunning) { RaiseChanged(); return $"added as step {number}: {step.Name}, {step.Goal}."; }
        if (left)
        {
            RaiseChanged();
            return $"added as step {number}: {step.Name}, {step.Goal}. The queue is not running: press Start in its window ({ActionQueueMod.Instance?.Key.Spoken ?? "F6"}).";
        }
        // Nothing else to do: run it now. The finished steps stay finished (they are not done again).
        s_resume = Steps.Count > 1;
        Start();
        return $"started {step.Name}, {step.Goal} (step {number}). Add more and they follow it.";
    }

    /// <summary>The game is starting up or has just computed offline progress: the idle action waits a few seconds.</summary>
    public static void HoldIdle() => s_idleNotBefore = s_clock + SettleAfterLoad;

    // ---------------------------------------------------------------- the game's own Stop buttons

    /// <summary>
    /// Remembers the actions the player stops with one of the game's own buttons ("Stop Cutting", "Stop", "Run"...).
    /// Told apart from the game stopping an action by itself (out of materials, bank full, a stop condition met:
    /// those happen inside a game tick) and from another action being started (the game stops the old one first).
    /// </summary>
    static void WatchStops(Game game)
    {
        foreach ((GameEntity entity, ActionComponent action) in Targets.Skills(game))
        {
            string id = entity.Id;
            ((IInventoryWalletComponent)action).Inventory.ItemAdded += (_, e) =>
            {
                if (!ReferenceEquals(game, s_game) || e.Quantity >= 0 || e.Item == null) return;
                lock (s_gained) s_spent[$"{id}|{e.Item.Id}"] = s_spent.GetValueOrDefault($"{id}|{e.Item.Id}") - e.Quantity;
            };
            action.Stopped += (_, _) =>
            {
                if (!ReferenceEquals(game, s_game) || game.Flags.IsOffline) return;
                if (!StoppedByHand()) return;
                lock (s_handStops) s_handStops[id] = Environment.TickCount64;
            };
        }
    }

    /// <summary>
    /// True when the stop comes from a button of the game's own screen: a "...Pressed..." handler of one of the game's
    /// controls is on the call stack, and neither a game tick nor "stop everything to start something else" is.
    /// (Only button handlers count: picking a trap the bank has none of also stops hunting from the screen, but that
    /// is the game giving up, not the player pressing Stop.)
    /// </summary>
    static bool StoppedByHand()
    {
        bool screen = false;
        Assembly gameCode = typeof(Game).Assembly;
        foreach (StackFrame frame in new StackTrace(1, false).GetFrames())
        {
            MethodBase? method = frame.GetMethod();
            Type? type = method?.DeclaringType;
            if (type == null || type.Assembly != gameCode) continue;
            if (type == typeof(Game) && method!.Name == nameof(Game.PropagateTick)) return false;
            if (type == typeof(ActiveActionManager) && method!.Name.StartsWith("TryStopActive", StringComparison.Ordinal)) return false;
            // The game stopping it because something ran out or a requirement or stop condition changed (selling,
            // unequipping, buying...), or hunting giving up after a trap change: not the Stop button.
            if (method!.Name is nameof(ActionComponent.QueueOrTryStop) or "ActiveAnimalTraplessEnabledChangedHandler" or "ActiveAnimalSelectedTrapChangedHandler") return false;
            if (screen || !method!.Name.Contains("Pressed", StringComparison.Ordinal)) continue;
            for (Type? t = type; t != null && !screen; t = t.DeclaringType)
                if (typeof(Godot.Control).IsAssignableFrom(t)) screen = true;
        }
        return screen;
    }

    /// <summary>True (once) when the player stopped this skill's action by hand in the last few seconds.</summary>
    static bool TakeHandStop(string entityId)
    {
        lock (s_handStops)
        {
            if (!s_handStops.Remove(entityId, out long at)) return false;
            return Targets.OnMainThread && Environment.TickCount64 - at < 5000;   // (offline: the player is not there)
        }
    }

    // ---------------------------------------------------------------- saved queues

    public static List<string> PresetNames => s_presets.Keys.OrderBy(n => n == AutoName ? 1 : 0).ThenBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

    public static Preset? GetPreset(string name) => s_presets.TryGetValue(name, out Preset? preset) ? preset : null;

    static Step Copy(Step s) => new()
    {
        Entity = s.Entity, TargetId = s.TargetId, Extra = s.Extra, Name = s.Name,
        Until = s.Until, Amount = s.Amount, Subject = s.Subject, SubjectName = s.SubjectName, Lane = s.Lane, Mode = s.Mode,
    };

    static bool Same(Step a, Step b) => a.Entity == b.Entity && a.TargetId == b.TargetId && a.Extra == b.Extra
        && a.Until == b.Until && a.Amount == b.Amount && a.Subject == b.Subject && LaneOf(a) == LaneOf(b) && a.Mode == b.Mode;

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
        RaiseChanged();
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
        s_laneRan.Clear();
        Repeat = preset.Repeat;
        Name = s_presets.Keys.First(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
        Index = 0;
        s_ranThisRound = false;
        s_resume = false;
        Running = wasRunning && Steps.Count > 0;
        Note = "";
        Save();
        message = $"Loaded '{Name}' ({Steps.Count} step(s))" + (Running ? ", and it goes on running from its first step." : ".")
            + (kept ? $" What was here before is kept as '{AutoName}'." : "");
        RaiseChanged();
        return true;
    }

    public static bool DeletePreset(string name, out string message)
    {
        if (!s_presets.Remove(name)) { message = "That saved queue no longer exists."; return false; }
        if (string.Equals(Name, name, StringComparison.OrdinalIgnoreCase)) Name = "";
        Save();
        message = $"Deleted the saved queue '{name}'. The steps on screen are untouched.";
        RaiseChanged();
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
                if (step.IsSell) { if (s_items.ContainsKey(step.TargetId)) known++; continue; }
                ActionComponent? action = Targets.Find(s_game, step.Entity);
                if (action != null && Targets.All(action).Any(t => t.Id == step.TargetId && t.Extra == step.Extra)) known++;
            }
        message = $"Imported '{unique}': {steps.Count} step(s)"
            + (known == steps.Count ? "." : $", {steps.Count - known} of them for things your game does not have (those will be skipped).")
            + " Select it and press Load to use it.";
        RaiseChanged();
        return true;
    }

    // ---------------------------------------------------------------- running

    public static void Start()
    {
        if (Steps.Count == 0) { Note = "Add a step first."; RaiseChanged(); return; }
        // After a pause: carry on. What was finished stays finished, wherever it has been moved since; the rest
        // starts again in the order of the list. Otherwise every step starts afresh.
        bool resume = s_resume && Steps.Any(s => s.Run != RunState.Done);
        foreach (Step s in Steps)
        {
            if (resume && s.Run == RunState.Done) continue;
            if (s.State != "paused") s.CountBefore = 0;           // "make N more" / "N times" carry on after a pause, else count afresh
            s.State = "";
            s.Run = RunState.Idle;
            s.Ran = false;
            s.Seconds = s.RetryAt = s.GrewAt = 0;
            s.Pile = 0;
        }
        Running = true;
        s_resume = false;
        lock (s_handStops) s_handStops.Clear();
        s_ranThisRound = resume && Steps.Any(s => s.Run == RunState.Done && s.Ran);
        s_laneRan.Clear();
        if (resume) foreach (Step s in Steps.Where(s => s.Run == RunState.Done && s.Ran)) s_laneRan.Add(LaneOf(s));
        SetIndex();
        Note = "";
        // An idle action left running from the last time belongs to the player now: the queue does not take it over.
        // (One that "also when the queue is not running" started is still the queue's: it makes room for the steps.)
        if (!IdleAlways) s_idleOwned = false;
        s_idleBlocked = s_idleRanOut = false;
        s_idleRetryAt = 0;
        IdleState = "";
        Trace($"started at step {Index + 1} of {Steps.Count}, up to {Lanes} at once: " + string.Join(" | ", Steps.Select(s => $"{s.Name} {s.Goal}")));
        RaiseChanged();
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
        s_resume = true;
        foreach (Step s in Steps.Where(s => s.Run is RunState.Running or RunState.Waiting))
        {
            s.State = "paused";
            if (s.Ran) s.CountBefore = Counter(s);   // keep what "make N more" / "N times" has already counted
        }
        // Like the steps' own actions, an idle action that runs keeps running: the player decides from here.
        // With "also when the queue is not running" it stays the queue's own, and goes on as such.
        if (!IdleAlways) s_idleOwned = false;
        IdleState = "";
        Note = why;
        Publish();
        RaiseChanged();
    }

    static void Finish(string why)
    {
        if (Running) Trace($"ended: {why}");
        StopIdle();   // emptied or cut short: the idle action the queue started goes with it (a real finish lets go of it first)
        IdleState = "";
        Running = false;
        s_resume = false;
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
        if (!ReferenceEquals(game, s_game)) return;
        if (Targets.OnMainThread) { Targets.PrimeMeta(); s_toldFightOffline = false; s_running = game.ActionManager.ActiveActions.Count; }
        else s_running = Math.Max(s_running, game.ActionManager.ActiveActions.Count);
        if (s_changedLater && Targets.OnMainThread) RaiseChanged();
        s_clock += seconds;
        if (!Running)
        {
            if (game.Flags.IsOffline) return;
            string before = IdleState;
            bool owned = s_idleOwned, blocked = s_idleBlocked;
            try { KeepIdleOutside(game); }
            catch (Exception e) { Log("The idle action failed: " + e.Message); IdleState = "failed (see the log)"; }
            Publish();
            if (IdleState != before || owned != s_idleOwned || blocked != s_idleBlocked) RaiseChanged();
            return;
        }
        if (Steps.Count == 0) { Finish("The queue is empty."); RaiseChanged(); return; }

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

        // In lanes, with "start over" on, every lane starts over by itself as soon as it is through (a short lane
        // does not wait for a long one); a lane that did nothing in its round stays finished. Done before the
        // scheduling, so a lane that makes what another lane uses is never seen "finished" for a moment.
        if (Running && Repeat && LaneMode)
            foreach (int lane in Steps.Select(LaneOf).Distinct().ToList())
            {
                var mine = Steps.Where(s => LaneOf(s) == lane).ToList();
                if (!mine.All(s => s.Run == RunState.Done) || !s_laneRan.Remove(lane)) continue;
                foreach (Step s in mine) { s.State = ""; s.Run = RunState.Idle; s.Ran = false; s.Seconds = s.RetryAt = s.GrewAt = 0; s.Pile = s.CountBefore = 0; }
                Trace($"lane {lane} starts over");
            }

        // 2. Room for more? Waiting steps try again; then the next steps in order take the free places.
        //    In lanes: only the step each lane is at, and a waiting one holds no place (it is not doing anything).
        if (Running)
        {
            int lanes = Lanes;
            bool laneMode = LaneMode;
            int Busy() => laneMode ? Steps.Count(s => s.Run == RunState.Running)
                                   : Steps.Count(s => !s.IsSell && s.Run is RunState.Running or RunState.Waiting);
            foreach (Step step in Steps.ToList())
            {
                if (!Running) break;
                if (step.Run == RunState.Done || step.Run == RunState.Running) continue;
                if (laneMode && Head(LaneOf(step)) != step) continue;   // a lane does its steps one after another
                // Selling takes no place and is over at once; it only waits for what comes before it.
                if (step.IsSell) { Guard(step, () => Begin(game, step)); continue; }
                if (!laneMode && Steps.TakeWhile(s => s != step).Any(s => s.IsSell && s.Run != RunState.Done)) continue;   // nothing passes a sale
                // A skill does one thing at a time. In lanes only a running step holds its skill (starting would stop it).
                bool skillTaken = laneMode && Steps.Any(s => s != step && s.Run == RunState.Running && s.Entity == step.Entity);
                if (step.Run == RunState.Waiting)
                {
                    if (!laneMode || Busy() < lanes && !skillTaken) Guard(step, () => Begin(game, step));
                    continue;
                }
                if (Busy() >= lanes || skillTaken) continue;
                // In one list a later step for the same skill waits its turn.
                if (!laneMode && Steps.Any(s => s != step && s.Run is RunState.Running or RunState.Waiting && s.Entity == step.Entity)) continue;
                Guard(step, () => Begin(game, step));
            }
        }

        // 3. All done?
        if (Running && Steps.All(s => s.Run == RunState.Done))
        {
            if (Repeat && s_ranThisRound && !LaneMode)
            {
                s_ranThisRound = false;
                foreach (Step s in Steps) { s.State = ""; s.Run = RunState.Idle; s.Ran = false; s.Seconds = s.RetryAt = s.GrewAt = 0; s.Pile = s.CountBefore = 0; }
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
        RaiseChanged();
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
    static bool EarlierAtWork(Step step)
    {
        bool laneMode = LaneMode;
        return Steps.TakeWhile(s => s != step).Any(s => s.Run != RunState.Done && (!laneMode || LaneOf(s) == LaneOf(step)));
    }

    /// <summary>
    /// True while more of what this step uses may still come in: an earlier step is not over, or something
    /// running beside it (the player's own mining, an action Multitask keeps going) makes one of its materials.
    /// "Until it stops" takes the stop at its word when the supply is not the queue's own: the player may mine forever.
    /// </summary>
    static bool MoreComing(Game game, Step step, ActionComponent action, Target target) =>
        EarlierAtWork(step) || OtherLaneMakes(game, step, action, target) || (step.Until != Until.Stops && Supplied(game, action, target));

    /// <summary>In lanes: another lane still has a step to do that makes one of this step's materials (fish for the cooking).</summary>
    static bool OtherLaneMakes(Game game, Step step, ActionComponent action, Target target)
    {
        if (!LaneMode) return false;
        HashSet<string> needs = Targets.Ingredients(action, target);
        if (needs.Count == 0) return false;
        foreach (Step other in Steps)
        {
            if (other.Run == RunState.Done || other.IsSell || LaneOf(other) == LaneOf(step)) continue;
            if (Targets.Find(game, other.Entity) is { } a && Targets.Product(a, new Target(other.TargetId, other.Name, other.Extra)) is { } made && needs.Contains(made.Id))
                return true;
        }
        return false;
    }

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

    static void StopAction(ActionComponent action)
    {
        if (HandsOff(action)) return;   // a fight is never ended offline (see OfflineTick)
        if (!action.TryStop()) action.TryStop(force: true);
    }

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
        return Capacity() - used;
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
        StopIdleFightWithoutAmmo(game, action, ref doing);
        if (s_idleOwned && !doing)
        {
            s_idleOwned = false;
            if (TakeHandStop(Idle.Entity))
            {
                s_idleBlocked = true;
                IdleState = "stopped by you; it comes back when you start the queue again";
                Trace($"while idle: {Idle.Name} was stopped with the game's button, it is not started again in this run");
            }
            else if (Targets.Batches(game, action, target) == 0)
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
            if (s_idleOwned && doing && !HandsOff(action))
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

    static double s_idleNoAttackSince = -1;

    /// <summary>
    /// The idle action is a fight the queue started and the player can no longer attack in it (out of ammo, out of
    /// runes): after two seconds of it (not a moment's gear change) it is stopped, instead of standing there while the
    /// monster hits. It is not started again until the player can fight (see <see cref="TryStartIdle"/>).
    /// </summary>
    static void StopIdleFightWithoutAmmo(Game game, ActionComponent action, ref bool doing)
    {
        if (!doing || !s_idleOwned || action is not CombatActionComponent || HandsOff(action) || CannotAttack(game) is not { Length: > 0 } why)
        {
            s_idleNoAttackSince = -1;
            return;
        }
        if (s_idleNoAttackSince < 0) s_idleNoAttackSince = s_clock;
        if (s_clock - s_idleNoAttackSince < 2) return;
        s_idleNoAttackSince = -1;
        StopAction(action);
        s_idleOwned = false;
        doing = false;
        IdleState = $"stopped: {why}";
        Trace($"while idle: stopped {Idle?.Name} ({why})");
    }

    static void TryStartIdle(Game game, ActionComponent action, Target target)
    {
        if (s_clock < s_idleRetryAt) return;
        s_idleRetryAt = s_clock + RetrySeconds;
        if (!Targets.CanStart(action, target, out string problem)) { IdleState = "cannot start: " + problem; return; }
        if (HandsOff(action)) { IdleState = "waits for the game to be open (a fight)"; return; }
        if (action is CombatActionComponent && CannotAttack(game) is { Length: > 0 } cannot)
        {
            IdleState = $"not started: {cannot} (it starts once you can fight again)";
            return;
        }
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

    /// <summary>
    /// The queue is not running (paused, finished, emptied, never started) and "also when the queue is not running"
    /// is on: start the idle action whenever the character does nothing at all. It never pushes anything out and
    /// never takes a slot Multitask keeps for one of the player's own actions. After the player stops it (or stops
    /// anything else) with a game button, or after a death, it waits until the player starts something by hand.
    /// </summary>
    static void KeepIdleOutside(Game game)
    {
        var runningNow = game.ActionManager.ActiveActions.Select(a => $"{a.Entity.Id}|{Targets.Current(a)?.Id}").ToHashSet();
        HashSet<string> before = s_runningBefore;
        s_runningBefore = runningNow;
        bool died = s_died;
        s_died = false;
        if (!IdleAlways || Idle == null)
        {
            lock (s_handStops) s_handStops.Clear();
            if (!s_idleOwned) IdleState = "";
            return;
        }
        ActionComponent? action = Targets.Find(game, Idle.Entity);
        if (action == null) { IdleState = "this skill is not in the game"; return; }
        Target target = IdleTarget(Idle);
        bool doing = Targets.IsDoing(action, target);
        StopIdleFightWithoutAmmo(game, action, ref doing);
        string idleKey = $"{Idle.Entity}|{target.Id}";

        // Something the player stopped with a game button: hands off until they start something themselves.
        bool handStop;
        lock (s_handStops) { handStop = s_handStops.Count > 0; s_handStops.Clear(); }
        if (handStop && !doing)
        {
            s_idleOwned = false;
            s_idleBlocked = true;
            IdleState = "waiting: you stopped an action, so it starts again after your next action of your own";
            Trace("idle (queue not running): the player stopped an action, the idle action waits");
        }
        if (died && Idle.Entity == "MelvorBase:Combat")
        {
            s_idleOwned = false;
            s_idleBlocked = true;
            IdleState = "waiting: your character died; it starts again after your next action of your own";
            Trace("idle (queue not running): the character died, the fight is not started again");
        }
        // The player started something new by hand: a block is lifted (the idle action waits for that to end).
        if (s_idleBlocked && runningNow.Any(k => k != idleKey && !before.Contains(k)))
        {
            s_idleBlocked = false;
            IdleState = "";
        }

        if (s_idleOwned && !doing)
        {
            s_idleOwned = false;
            if (Targets.Batches(game, action, target) == 0)
            {
                s_idleRanOut = true;
                s_idlePile = 0;
                s_idleGrewAt = s_clock;
                Trace($"idle (queue not running): {Idle.Name} ran out of materials");
            }
            else if (game.ActionManager.ActiveActions.Count == 0)
            {
                // Not out of anything and nothing else took its place: the bank is full, or the game stopped it.
                s_idleBlocked = true;
                IdleState = "stopped (the bank may be full); it starts again after your next action of your own";
                Trace($"idle (queue not running): {Idle.Name} stopped, it is not started again for now");
            }
        }
        if (doing) { IdleState = s_idleOwned ? "running now" : "running now (started by you)"; return; }
        if (s_idleBlocked) return;
        if (s_clock < s_idleNotBefore) { IdleState = "starting in a moment"; return; }
        if (game.ActionManager.ActiveActions.Count > 0) { IdleState = "waits until your character does nothing"; return; }
        if (FreeSlots(game) <= 0) { IdleState = "waits: Multitask keeps the place for one of your actions"; return; }
        TryStartIdle(game, action, target);
    }

    /// <summary>A step is about to start: the idle action makes room when it uses the same skill or the last free slot.</summary>
    static void MakeRoomFor(Game game, Step step, bool beside)
    {
        if (!s_idleOwned || Idle == null) return;
        if (beside && Idle.Entity != step.Entity && FreeSlots(game) > 0) return;   // there is room for both
        ActionComponent? action = Targets.Find(game, Idle.Entity);
        if (action != null && HandsOff(action)) return;   // an idle fight is not ended offline
        s_idleOwned = false;
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
        if (HandsOff(action)) { Tell($"{Idle.Name} was not started while the game was closed (a fight waits for the game to be open)."); return; }
        if (action is CombatActionComponent && CannotAttack(game) is { Length: > 0 } cannot) { Tell($"{Idle.Name} was not started: {cannot}."); return; }
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
        if (step.IsSell) { Sell(game, step); return; }
        // Off the main thread (offline progress) a fight is neither started nor ended by the queue: the game's music
        // player and its notice would react to it right there. Such a step waits until the game is back.
        if (!Targets.OnMainThread && step.Entity == CombatId)
        {
            if (step.Run != RunState.Waiting) { step.Run = RunState.Waiting; step.State = "waits for the game to be open (a fight)"; }
            return;
        }
        ActionComponent? action = Targets.Find(game, step.Entity);
        if (action == null) { End(step, "skipped: this skill is not in the game"); return; }
        // A fight the player could not take part in (no ammo for the weapon, no runes for the spell) is not started:
        // the character would only stand there while the monster hits. With "start over" on, the fight that ended
        // for that reason is not started again and again either: a round where nothing could start ends the queue.
        if (action is CombatActionComponent && CannotAttack(game) is { Length: > 0 } cannot)
        {
            End(step, "skipped: " + cannot);
            return;
        }
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
        // Offline next to a fight: starting in its place would end the fight on the game's thread. Without a free
        // Multitask slot the step waits until the game is back.
        if (FightKeptOffline(game) && !(Capacity() > 1 && FreeSlots(game) > 0))
        {
            Wait(step, "waits for the game to be open (a fight is running)");
            return;
        }
        // With Multitask's room, what the player keeps running in another skill (a fight beside the queue's smithing)
        // stays running: starting in place of everything would stop it, and Multitask would start it again from the
        // beginning (a fight against a new monster). Without a free slot it is the plain game: the step takes over.
        if (!othersAtWork && Capacity() > 1 && FreeSlots(game) > 0
            && game.ActionManager.ActiveActions.Any(a => a.Entity.Id != step.Entity && (!(s_idleOwned && Idle?.Entity == a.Entity.Id) || HandsOff(a))))
        {
            othersAtWork = true;
            if (action.IsStarted) StopAction(action);   // the step's own skill does one thing at a time
        }
        MakeRoomFor(game, step, othersAtWork);
        // The step's own choice of the recipe's other way of making (the Sacrificial Anvil switch), as the game's switch sets it.
        if (step.Mode != null && !Targets.SetMode(action, target.Id, step.Mode))
            Trace($"{Number(step)}: the way of making '{step.Mode}' could not be switched (locked, or not for this recipe)");
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
            step.DoneAtStart = DoneBy(step.Entity);
            step.SpentAtStart = SpentOf(step);
            step.CountGains = Lanes > 1;
        }
        step.Ran = true;
        step.Run = RunState.Running;
        step.Following = Targets.Current(action)?.Id ?? "";
        step.State = "running";
        s_ranThisRound = true;
        s_laneRan.Add(LaneOf(step));
        s_died = false;
        Trace($"{Number(step)}: started{(othersAtWork ? " beside the others" : "")}{(step.Until is Until.Item or Until.ItemLeft or Until.Made ? $", bank holds {Owned(game, step):N0} {step.SubjectName}" : "")}{(step.Until is Until.Made or Until.Times or Until.Spent && step.CountBefore > 0 ? $", {step.CountBefore:N0} counted before the pause" : "")}");
    }

    /// <summary>
    /// A sell step: once what comes before it is over, sells all of the item but the number to keep, through the game's
    /// own sale code (like the bank's Sell button). A stack the player has locked in the bank is never sold.
    /// </summary>
    static void Sell(Game game, Step step)
    {
        if (EarlierAtWork(step)) { Wait(step, "waiting: for the steps before it to finish"); return; }
        string name = step.SubjectName != "" ? step.SubjectName : step.Name;
        if (Bank(game) is not { } bank || !s_items.TryGetValue(step.TargetId, out Item? item)) { End(step, "skipped: this item is not in the game"); return; }
        var stacks = bank.Inventory.Stacks.Where(st => st.Item == item && !st.IsPlaceholder && st.Quantity > 0).ToList();
        if (stacks.Any(st => st.IsLocked)) { End(step, $"skipped: {name} is locked in your bank"); return; }
        long owned = stacks.Sum(st => (long)st.Quantity), keep = (long)Math.Max(0, Math.Floor(step.Amount));
        if (owned <= keep) { End(step, $"done: nothing to sell (the bank holds {owned:N0})"); return; }
        long sold = 0, paid = 0;
        string currency = "gold";
        void OnSold(object? _, ItemSoldEventArgs e) { sold += e.Quantity; paid += e.CurrencyGain.Quantity; currency = e.CurrencyGain.Currency.DisplayName.Text; }
        bank.ItemSales.ItemSold += OnSold;
        try
        {
            ItemSaleComponentStats stats = bank.ItemSales.GetItemStats(item);
            try { bank.ItemSales.SellItem(item, stats, (int)Math.Min(int.MaxValue, owned - keep)); }
            finally { stats.Destroy(); }
        }
        finally { bank.ItemSales.ItemSold -= OnSold; }
        if (sold > 0) { step.Ran = true; s_ranThisRound = true; s_laneRan.Add(LaneOf(step)); }
        End(step, sold > 0 ? $"done: sold {sold:N0} for {paid:N0} {currency}" : "done: the game sold nothing");
    }

    static void Watch(Game game, Step step, double seconds)
    {
        ActionComponent? action = Targets.Find(game, step.Entity);
        if (action == null) { End(step, "skipped: this skill is not in the game"); return; }
        if (HandsOff(action)) return;   // a fight step is looked at again when the game is back
        var wanted = new Target(step.TargetId, step.Name, step.Extra);
        Target? now = Targets.Current(action);
        if (now == null)
        {
            if (s_died) { s_died = false; Pause("Your character died, so the queue is paused."); return; }
            // It stopped just as the goal was reached (the last materials made the last of it): that is a finish.
            if (Reached(game, step)) { End(step, "done"); return; }
            // Did the player start something else? Actions kept running by Multitask, and the queue's own
            // other steps, do not count.
            ActionComponent? main = Targets.Main(game);
            bool idleOfOurs = main != null && s_idleOwned && Idle != null && main.Entity.Id == Idle.Entity && Targets.IsDoing(main, IdleTarget(Idle));
            if (main != null && !idleOfOurs && !Steps.Any(s => s.Run == RunState.Running && s.Entity == main.Entity.Id))
            {
                Pause("You started something else, so the queue is paused.");
                return;
            }
            // Stopped with the game's own Stop button: the player is done with this step. It leaves the queue
            // (with "start over at the end" on it is only skipped this round) and the queue goes on.
            if (TakeHandStop(step.Entity)) { StoppedByPlayer(step); return; }
            // It stopped by itself. Out of materials while an earlier step, or something running beside it, is
            // still making them: wait for more instead of calling the step done.
            if (Targets.Batches(game, action, wanted) == 0 && MoreComing(game, step, action, wanted))
            {
                step.Pile = 0;
                step.GrewAt = s_clock;
                Wait(step, WaitingText(step));
                return;
            }
            End(step, action is HuntingActionComponent && Targets.Batches(game, action, wanted) == 0
                ? "done: out of traps (it stopped by itself)" : "done (it stopped by itself)");
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
        // A fight the player can no longer take part in (no ammo left for the weapon, not enough runes for the spell)
        // does not end by itself in the game: the monster keeps hitting while the player stands there. After two
        // seconds of it (not a moment's gear change) the step ends and the queue goes on.
        if (action is CombatActionComponent && CannotAttack(game) is { Length: > 0 } why)
        {
            double since = s_cannotAttack.GetValueOrDefault(step) + seconds;
            s_cannotAttack[step] = since;
            if (since >= 2)
            {
                s_cannotAttack.Remove(step);
                if (!action.TryStop()) action.TryStop(force: true);
                End(step, "done: " + why);
                return;
            }
        }
        else s_cannotAttack.Remove(step);
        step.State = "running  " + Progress(game, step);
    }

    static readonly Dictionary<Step, double> s_cannotAttack = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Why the player cannot attack in a fight right now, as the game itself decides it (PlayerComponent.CanAttack): the
    /// weapon needs ammo and none of its kind is equipped, or the stance casts spells and no spell is picked or the
    /// bank lacks its runes. "" when the player can attack.
    /// </summary>
    public static string CannotAttack(Game game)
    {
        if (!game.Entities.TryGetObjectById(CombatId, out GameEntity? e) || e is not CombatEntity combat) return "";
        try
        {
            var weapon = combat.Equipment.CurrentWeapon.Weapon;
            if (weapon.RequiredAmmoType is { } ammo && !combat.Equipment.IsAmmoEquipped(ammo)) return "out of ammo";
            if (combat.Player.GetSelectedStance(weapon.Type).CombatStyle.UsesSpells)
            {
                var spell = combat.MagicSpells.SelectedSpell;
                if (spell == null) return "no spell picked";
                if (spell.RuneStats.RuneCosts.Values.Any(v => combat.MagicSpells.Inventory.GetQuantity(v.Value.Item) < v.Value.Quantity))
                    return "out of runes";
            }
        }
        catch { }   // (a game version that reads differently: the fight goes on as before)
        return "";
    }

    /// <summary>
    /// The player stopped a running step with the game's own Stop button: they are done with it, so it is taken out of
    /// the queue (a saved queue keeps it) and the queue goes on with the next step.
    /// </summary>
    static void StoppedByPlayer(Step step)
    {
        bool othersLeft = Steps.Any(s => s != step && s.Run != RunState.Done);
        Trace($"{Number(step)}: stopped with the game's button, taken out of the queue");
        Steps.Remove(step);
        Save();
        string told = $"You stopped '{step.Name}', so it was taken out of the queue.";
        if (othersLeft || Repeat && Steps.Count > 0) { Note = told + " The queue goes on."; return; }
        // That was the last thing to do: the queue ends here, and the idle action does not jump in right away.
        Finish(told + " Nothing is left to do.");
        s_idleBlocked = true;
        IdleState = "not started: you stopped the last step";
    }

    static BankEntity? Bank(Game game) => game.Entities.TryGetObjectById("MelvorBase:Bank", out GameEntity? e) ? e as BankEntity : null;

    static int Owned(Game game, Step step) =>
        s_items.TryGetValue(step.Subject, out Item? item) ? Bank(game)?.Inventory.GetQuantity(item) ?? 0 : 0;

    static long Gained(Step step) { lock (s_gained) return s_gained.GetValueOrDefault(step.Subject); }

    static long SpentOf(Step step) { lock (s_gained) return s_spent.GetValueOrDefault($"{step.Entity}|{step.Subject}"); }

    /// <summary>"Use up N": how many of the item the step's skill has used since the step first started (pauses included).</summary>
    static long Spent(Step step) => step.CountBefore + (step.Ran ? SpentOf(step) - step.SpentAtStart : 0);

    /// <summary>
    /// What counts for "until I own N". One step at a time: what the bank holds. Side by side: what the bank
    /// held at the start plus what came in since - another step may be using the item up as fast as it comes.
    /// </summary>
    static long Counted(Game game, Step step) =>
        step.CountGains && step.Ran ? Math.Max(Owned(game, step), step.OwnedAtStart + (Gained(step) - step.GainedAtStart)) : Owned(game, step);

    /// <summary>"Make N more": how many of the item came into the bank since the step first started (pauses included).</summary>
    static long Made(Step step) => step.CountBefore + (step.Ran ? Gained(step) - step.GainedAtStart : 0);

    /// <summary>"N times": how many actions the step's skill has finished since the step first started (pauses included).</summary>
    static long Times(Step step) => step.CountBefore + (step.Ran ? DoneBy(step.Entity) - step.DoneAtStart : 0);

    /// <summary>What "make N more" or "N times" has counted so far (0 for the other goals).</summary>
    static long Counter(Step step) => step.Until switch { Until.Made => Made(step), Until.Times => Times(step), Until.Spent => Spent(step), _ => 0 };

    static long DoneBy(string entityId) { lock (s_done) return s_done.GetValueOrDefault(entityId); }

    static void Count(Game game, string entityId)
    {
        if (!ReferenceEquals(game, s_game)) return;
        lock (s_done) s_done[entityId] = s_done.GetValueOrDefault(entityId) + 1;
    }

    /// <summary>
    /// Listens for "one action finished", the way the game itself counts it for its own triggers: the game's event
    /// bus says so for most skills. Where that event fires for parts of an action, the whole is counted instead:
    /// artisan skills (smithing, crafting...) announce every step of a recipe, so a finished recipe counts; agility
    /// announces every obstacle, so a finished lap counts; hunting and herblore mark their parts as sub-actions.
    /// Combat has no such event: a monster killed counts. The events can come on the game's own thread while it
    /// computes offline progress, so they only add to a counter.
    /// </summary>
    static void CountActions(Game game)
    {
        lock (s_done) s_done.Clear();
        if (game.Entities.TryGetObjectById("MelvorBase:EventBus", out GameEntity? busEntity))
            foreach (EventBusComponent bus in busEntity.Components.Values.OfType<EventBusComponent>())
                bus.EventBus.ActionPerformed.Subscribe((sender, e) =>
                {
                    if (sender is not ActionComponent action || e.IsSubAction) return;
                    if (action is ArtisanActionComponent or AgilityActionComponent) return;   // counted below, as a whole
                    Count(game, action.Entity.Id);
                });
        foreach ((GameEntity entity, ActionComponent action) in Targets.Skills(game))
        {
            string id = entity.Id;
            if (action is ArtisanActionComponent artisan) artisan.RecipeCreated += (_, _) => Count(game, id);
            else if (action is AgilityActionComponent agility) agility.CourseCompleted += (_, _) => Count(game, id);
            else if (action is CombatActionComponent && entity is CombatEntity combat) combat.Enemy.Killed += (_, _) => Count(game, id);
        }
    }

    static int Level(Game game, Step step) =>
        game.LeveledEntities.FirstOrDefault(s => s.DisplayName.Text == step.Subject)?.Experience.Level ?? 0;

    static bool Reached(Game game, Step step) => step.Until switch
    {
        Until.Item => Counted(game, step) >= step.Amount,
        Until.ItemLeft => Owned(game, step) <= step.Amount,   // counts down: food being eaten, materials being used
        Until.Level => Level(game, step) >= step.Amount,
        Until.Minutes => step.Seconds >= step.Amount * 60,
        Until.Made => Made(step) >= step.Amount,
        Until.Times => Times(step) >= step.Amount,
        Until.Spent => Spent(step) >= step.Amount,
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
        Until.Times => $"{Times(step):N0} / {step.Amount:N0} times",
        Until.Spent => $"{Spent(step):N0} / {step.Amount:N0} used up",
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
