using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Components.Actions;
using GamesByMalcs.Melvor2.Components.Experience;
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.Entities;
using GamesByMalcs.Melvor2.Entities.Bank;
using GamesByMalcs.Melvor2.Entities.Combat;
using GamesByMalcs.Melvor2.Data;
using GamesByMalcs.Melvor2.Data.Currencies;
using GamesByMalcs.Melvor2.Components.CombatFood;
using ActionQueue;

namespace PhoneScreen;

/// <summary>
/// What the phone shows, read from the game on its own thread and written as JSON: the actions on, combat, the
/// Action Queue and the Adventurers (when those mods are there), the bank, alerts and the answers to commands.
/// The other mods are read by name, so the phone page works with or without them.
/// </summary>
public sealed class Snapshot
{
    readonly List<(long Seq, long Time, string Text, string Kind)> _alerts = new();
    readonly List<(string Id, bool Ok, string Text)> _answers = new();
    long _seq;
    // what the alerts compare with
    int _lastActive = -1;
    bool _bankFull, _queueRunning;
    int _foodLevel;                       // 0 fine, 1 low, 2 none left (each step warns once)
    // the problems that hold right now (not the history): rebuilt with every snapshot
    readonly List<(string Key, string Level, string Text, string Fix)> _problems = new();
    readonly HashSet<string> _stoppedAdventurers = new();
    string _character = "";
    // recent gains: bank counts every few seconds, the increases kept for 10 minutes
    Dictionary<Item, long>? _counts;
    readonly List<(DateTime At, Item Item, long Gain)> _gains = new();
    DateTime _lastCount;

    public string Character => _character;

    public void Alert(string text, string kind = "info")
    {
        _alerts.Add((++_seq, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), text, kind));
        if (_alerts.Count > 30) _alerts.RemoveAt(0);
    }

    public void Answer(string id, bool ok, string text)
    {
        _answers.Add((id, ok, text));
        if (_answers.Count > 20) _answers.RemoveAt(0);
    }

    public bool Answered(string id) => _answers.Any(a => a.Id == id);

    public byte[] NotReady(string why)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteBoolean("ready", false);
            w.WriteString("why", why);
            w.WriteNumber("taken", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            Alerts(w);
            w.WriteEndObject();
        }
        return ms.ToArray();
    }

    int _farmReady;

    public byte[] Take(Game game)
    {
        if (game.CharacterName != _character)
        {
            _character = game.CharacterName;
            _lastActive = -1;
            _counts = null;
            _gains.Clear();
            _stoppedAdventurers.Clear();
        }
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteBoolean("ready", true);
            w.WriteString("character", game.CharacterName);
            w.WriteNumber("taken", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            _problems.Clear();
            w.WriteStartObject("has");
            w.WriteBoolean("queue", s_queue != null);
            w.WriteBoolean("team", s_roster != null);
            w.WriteBoolean("advisor", FindType("CombatAdvisor.Forecast") != null);
            w.WriteNumber("slots", Targets.MultitaskCapacity());
            Parts.Has(w);
            w.WriteEndObject();
            Actions(w, game);
            Combat(w, game);
            try
            {
                int ready = Parts.Glance(w, game);
                if (ready > _farmReady && ready > 0) Alert($"Farming: {ready} plot{(ready == 1 ? " is" : "s are")} ready to harvest.", "info");
                if (ready > 0) _problems.Add(("farm", "info", $"Farming: {ready} ready to harvest.", "farm"));
                _farmReady = ready;
            }
            catch { }
            Queue(w);
            Adventurers(w, game);
            Bank(w, game);
            Alerts(w);
            w.WriteStartArray("problems");
            foreach (var (key, level, text, fix) in _problems)
            {
                w.WriteStartObject();
                w.WriteString("key", key);
                w.WriteString("level", level);
                w.WriteString("text", text);
                w.WriteString("fix", fix);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("answers");
            foreach (var (id, ok, text) in _answers)
            {
                w.WriteStartObject();
                w.WriteString("id", id);
                w.WriteBoolean("ok", ok);
                w.WriteString("text", text);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return ms.ToArray();
    }

    void Alerts(Utf8JsonWriter w)
    {
        w.WriteStartArray("alerts");
        foreach (var (seq, time, text, kind) in _alerts)
        {
            w.WriteStartObject();
            w.WriteNumber("seq", seq);
            w.WriteNumber("time", time);
            w.WriteString("text", text);
            w.WriteString("kind", kind);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    // ------------------------------------------------------------------------------------------- the parts

    void Actions(Utf8JsonWriter w, Game game)
    {
        var active = game.ActionManager.ActiveActions.Where(a => a is { IsStarted: true }).ToList();
        if (_lastActive > 0 && active.Count == 0) Alert("Everything stopped: no action is running.", "warn");
        if (active.Count == 0) _problems.Add(("idle", "warn", "Nothing is running.", "skills"));
        _lastActive = active.Count;
        string driving = Targets.QueueDriving();
        w.WriteStartArray("actions");
        // combat first, then the others in a steady order (cards do not jump around)
        foreach (ActionComponent a in active.OrderBy(a => a.Entity is CombatEntity ? 0 : 1).ThenBy(a => a.Entity.DisplayName.Text))
        {
            w.WriteStartObject();
            w.WriteString("skill", a.Entity.DisplayName.Text);
            w.WriteString("skillId", a.Entity.Id);
            w.WriteString("target", a.CurrentDescription?.DisplayName.Text ?? "");
            bool combat = a.Entity is CombatEntity;
            Target? now = null;
            try { now = Targets.Current(a); } catch { }
            if (now != null) { w.WriteString("targetId", now.Id); w.WriteString("extra", now.Extra); }
            try
            {
                string icon = combat && a.Entity is CombatEntity ce && ce.Enemy.Enemy is { } en ? "enemy/" + en.Id
                    : now != null && !combat && Targets.Product(a, now) is { } product ? "item/" + product.Id : "";
                if (icon != "") w.WriteString("icon", icon);
            }
            catch { }
            w.WriteBoolean("byQueue", driving == a.Entity.Id);
            w.WriteBoolean("combat", combat);
            if (!a.IsTimerIndeterminate && a.ActionTimer is { IsRunning: true } t && t.WaitTicks > 0)
            {
                w.WriteNumber("progress", Math.Round(Math.Clamp(t.Progress, 0, 1), 3));
                w.WriteNumber("seconds", Math.Round(t.WaitTicks / 60.0, 2));
            }
            if (!combat && a.Entity.Components.Values.OfType<ExperienceComponent>().FirstOrDefault() is { } xp)
            {
                w.WriteNumber("level", xp.Level);
                double here = StandardExperienceTable.Default.GetXpForLevel(xp.Level);
                double next = StandardExperienceTable.Default.GetXpForLevel(Math.Min(xp.Level + 1, 120));
                w.WriteNumber("levelProgress", next > here ? Math.Round(Math.Clamp((xp.Xp - here) / (next - here), 0, 1), 3) : 1);
                w.WriteNumber("toNext", Math.Max(0, Math.Round(xp.XpToNextLevel)));
            }
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    void Combat(Utf8JsonWriter w, Game game)
    {
        if (!game.Entities.TryGetObjectById("MelvorBase:Combat", out GameEntity? e) || e is not CombatEntity combat || !combat.Action.IsStarted)
        {
            w.WriteNull("combat");
            _foodLevel = 0;
            return;
        }
        w.WriteStartObject("combat");
        w.WriteNumber("hp", combat.Player.Health.Current);
        w.WriteNumber("maxHp", combat.Player.Health.Max);
        if (combat.Enemy.Enemy is { } enemy)
        {
            w.WriteString("enemy", enemy.DisplayName.Text);
            w.WriteString("enemyId", enemy.Id);
            w.WriteNumber("enemyHp", combat.Enemy.Health.Current);
            w.WriteNumber("enemyMaxHp", combat.Enemy.Health.Max);
        }
        if (combat.Areas.CurrentArea?.Area is { } area) w.WriteString("area", area.DisplayName.Text);
        try
        {
            var type = combat.Equipment.CurrentWeapon.Weapon.Type;
            if (combat.Player.GetSelectedStance(type) is { } stance) { w.WriteString("stance", stance.DisplayName.Text); w.WriteString("stanceId", stance.Id); }
        }
        catch { }
        Item? food = combat.Food.SelectedFood?.Item;
        if (food != null)
        {
            int count = combat.Food.Inventory.GetQuantity(food);
            w.WriteString("food", food.DisplayName.GetPluralText(2));
            w.WriteString("foodId", food.Id);
            w.WriteNumber("foodCount", count);
            if (food.Capabilities.TryGet(out FoodCapability? meal)) w.WriteNumber("heal", meal.BaseHealing);
            if (combat.Food.Stats != null) w.WriteNumber("eatAt", Math.Round(combat.Food.Stats.AutoEatThreshold.Value * combat.Player.Health.Max));
            int level = count == 0 ? 2 : count < 20 ? 1 : 0;
            if (level > _foodLevel) Alert(level == 2 ? $"Out of food ({food.DisplayName.GetPluralText(2)}): combat goes on without healing." : $"Food is low: {count} {food.DisplayName.GetPluralText(count)} left.", level == 2 ? "bad" : "warn");
            _foodLevel = level;
            if (level == 2) _problems.Add(("food", "bad", $"Out of {food.DisplayName.GetPluralText(2)}: combat goes on without healing.", "food"));
            else if (level == 1) _problems.Add(("food", "warn", $"Food is low: {count} {food.DisplayName.GetPluralText(count)} left.", "food"));
        }
        else _problems.Add(("food", "bad", "No food selected in combat: nothing will heal you.", "food"));
        w.WriteEndObject();
    }

    // The Action Queue, read by name (the mod may not be installed)
    static readonly Type? s_queue = FindType("ActionQueue.Queue");

    void Queue(Utf8JsonWriter w)
    {
        if (s_queue == null) { w.WriteNull("queue"); return; }
        try
        {
            bool running = (bool)(Static(s_queue, "Running") ?? false);
            var steps = (Static(s_queue, "Steps") as IEnumerable)?.Cast<object>().ToList() ?? new List<object>();
            if (_queueRunning && !running && steps.Count > 0 && steps.All(s => Member(s, "Run")?.ToString() == "Done")) Alert("The queue has finished.", "info");
            _queueRunning = running;
            w.WriteStartObject("queue");
            w.WriteBoolean("running", running);
            w.WriteString("note", Static(s_queue, "Note")?.ToString() ?? "");
            w.WriteNumber("count", steps.Count);
            w.WriteStartArray("steps");
            int index = 0;
            foreach (object s in steps.Take(60))
            {
                string run = Member(s, "Run")?.ToString() ?? "", state = Member(s, "State")?.ToString() ?? "";
                w.WriteStartObject();
                w.WriteNumber("index", index++);
                w.WriteString("name", Member(s, "Name")?.ToString() ?? "");
                w.WriteString("goal", Member(s, "Goal")?.ToString() ?? "");
                w.WriteString("state", state);
                w.WriteString("run", run);
                w.WriteString("skill", Member(s, "Entity")?.ToString() ?? "");
                w.WriteEndObject();
                if (running && run == "Waiting" && state != "" && !_problems.Any(p => p.Key == "queue"))
                    _problems.Add(("queue", "warn", $"Queue waiting: {state}", "queue"));
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        catch { w.WriteNull("queue"); }
    }

    /// <summary>Pauses or goes on with the Action Queue (by name). Returns what happened, for the phone.</summary>
    public static (bool Ok, string Text) QueueCommand(bool pause)
    {
        if (s_queue == null) return (false, "The Action Queue mod is not installed.");
        bool running = (bool)(Static(s_queue, "Running") ?? false);
        if (pause)
        {
            if (!running) return (false, "The queue is not running.");
            s_queue.GetMethod("Pause", BindingFlags.Public | BindingFlags.Static, new[] { typeof(string) })?.Invoke(null, new object[] { "paused from the phone" });
            return (true, "Queue paused. The action on now goes on; the queue just does not move to the next step.");
        }
        if (running) return (false, "The queue is already running.");
        MethodInfo? start = s_queue.GetMethod("Start", BindingFlags.Public | BindingFlags.Static, Type.EmptyTypes);
        if (start == null) return (false, "The queue cannot be started from here.");
        start.Invoke(null, null);
        return (true, "Queue started again.");
    }

    // The Adventurers, read by name
    static readonly Type? s_roster = FindType("Adventurers.Roster");

    void Adventurers(Utf8JsonWriter w, Game game)
    {
        if (s_roster == null) { w.WriteNull("adventurers"); return; }
        try
        {
            var team = (Static(s_roster, "Team") as IEnumerable)?.Cast<object>().ToList() ?? new List<object>();
            var enemies = game.Data.GetDataRegistryOutput<Enemy>();
            w.WriteStartObject("adventurers");
            w.WriteStartArray("team");
            foreach (object a in team.Take(30))
            {
                string name = Member(a, "Name")?.ToString() ?? "";
                string stopped = Member(a, "Stopped")?.ToString() ?? "";
                bool working = Member(a, "Working") is true;
                string enemyId = Member(a, "Enemy")?.ToString() ?? "", dungeon = Member(a, "Dungeon")?.ToString() ?? "";
                string target = "";
                try { target = dungeon != "" ? game.Data.GetObject<GamesByMalcs.Melvor2.Components.CombatAreas.Dungeon>(dungeon).DisplayName.Text : enemyId != "" ? enemies.GetObjectById(enemyId).DisplayName.Text : ""; }
                catch { target = dungeon != "" ? dungeon : enemyId; }
                long recover = Member(a, "RecoverUntil") is long r ? r : 0;
                bool resting = recover > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                string status = stopped != "" ? "Stopped: " + stopped : resting ? "Recovering" : working ? "Hunting" : "Idle";
                if (stopped != "" && _stoppedAdventurers.Add(name)) Alert($"{name} stopped: {stopped}", "warn");
                if (stopped != "") _problems.Add(("team:" + name, "warn", $"{name} stopped: {stopped}", "team"));
                if (stopped == "") _stoppedAdventurers.Remove(name);
                long carried = Member(a, "Bag") is IDictionary bag ? bag.Values.Cast<object>().Sum(v => Convert.ToInt64(v)) : 0;
                w.WriteStartObject();
                w.WriteString("name", name);
                w.WriteString("target", target);
                w.WriteString("status", status);
                w.WriteNumber("kills", Member(a, "Kills") is long k ? k : 0);
                w.WriteNumber("carried", carried);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        catch { w.WriteNull("adventurers"); }
    }

    void Bank(Utf8JsonWriter w, Game game)
    {
        if (!game.Entities.TryGetObjectById("MelvorBase:Bank", out GameEntity? e) || e is not BankEntity bank) { w.WriteNull("bank"); return; }
        int used = bank.Inventory.Stacks.Count, max = bank.Inventory.MaxStacks;
        bool full = used >= max;
        if (full && !_bankFull) Alert("The bank is full.", "warn");
        if (full) _problems.Add(("bank", "bad", $"The bank is full ({used:N0} / {max:N0}): new items have nowhere to go.", "bank"));
        _bankFull = full;
        var gold = game.Data.GetDataRegistryOutput<Currency>().GetAllObjects().FirstOrDefault(c => c.Id == "MelvorBase:GoldPieces");
        DateTime now = DateTime.UtcNow;
        if (_counts == null || now - _lastCount > TimeSpan.FromSeconds(5))
        {
            var counts = new Dictionary<Item, long>();
            foreach (var s in bank.Inventory.Stacks) counts[s.Item] = counts.GetValueOrDefault(s.Item) + s.Quantity;
            if (_counts != null)
                foreach (var (item, n) in counts)
                {
                    long gain = n - _counts.GetValueOrDefault(item);
                    if (gain > 0) _gains.Add((now, item, gain));
                }
            _counts = counts;
            _lastCount = now;
            _gains.RemoveAll(g => now - g.At > TimeSpan.FromMinutes(10));
        }
        w.WriteStartObject("bank");
        w.WriteNumber("gold", gold == null ? 0 : bank.Wallet.GetQuantity(gold));
        w.WriteNumber("used", used);
        w.WriteNumber("max", max);
        w.WriteStartArray("recent");
        foreach (var g in _gains.GroupBy(g => g.Item).Select(g => (Item: g.Key, Sum: g.Sum(x => x.Gain))).OrderByDescending(g => g.Sum).Take(12))
        {
            w.WriteStartObject();
            w.WriteString("item", g.Item.DisplayName.GetPluralText(g.Sum > 1 ? 2 : 1));
            w.WriteString("id", g.Item.Id);
            w.WriteNumber("qty", g.Sum);
            w.WriteNumber("have", _counts!.GetValueOrDefault(g.Item));
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    // ------------------------------------------------------------------------------------------- reading other mods by name

    static Type? FindType(string name)
    {
        foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            if (a.GetName().Name != "PhoneScreen" && a.GetType(name, false) is { } t) return t;
        return null;
    }

    static object? Static(Type t, string name) =>
        t.GetProperty(name, BindingFlags.Public | BindingFlags.Static)?.GetValue(null) ?? t.GetField(name, BindingFlags.Public | BindingFlags.Static)?.GetValue(null);

    static object? Member(object o, string name)
    {
        Type t = o.GetType();
        return t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(o) ?? t.GetField(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(o);
    }
}
