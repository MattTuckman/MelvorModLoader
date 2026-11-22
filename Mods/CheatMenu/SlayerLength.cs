using System;
using System.Linq;
using System.Runtime.CompilerServices;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Components.SlayerTasks;
using GamesByMalcs.Melvor2.Core;
using GamesByMalcs.Melvor2.Entities;
using GamesByMalcs.Melvor2.Entities.Slayer;

namespace CheatMenu;

/// <summary>
/// "Slayer task: most kills": no slayer task asks for more kills than this (0 = the game's own lengths).
/// The game rolls a task's length between the master's base length and base + 3 per Slayer level, so tasks
/// grow with the level. A new, rerolled or already running task is shortened to the setting; one already
/// further along than that ends with its next kill. "Extend task" still adds the master's usual kills on top.
/// Nothing is written into the save except the shorter task the game itself keeps.
/// </summary>
public static class SlayerLength
{
    public const string Setting = "combat.slayerTaskMax";

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_TaskLength")]
    static extern void SetTaskLength(ActiveSlayerMaster master, int value);

    static Game? s_bound;
    /// <summary>True while this class sets a length itself: that change is not a newly rolled task.</summary>
    static bool s_setting;

    /// <summary>Call a few times per second with the open character.</summary>
    public static void Check(Game game)
    {
        SlayerTasksComponent? tasks = Tasks(game);
        if (tasks == null) return;
        if (!ReferenceEquals(s_bound, game))
        {
            s_bound = game;
            // At once when the game rolls a new task, so offline progress and Auto Slayer see the short one.
            foreach (ActiveSlayerMaster master in tasks.ActiveMasters.Values)
                master.TaskLengthChanged += (_, e) => { if (ReferenceEquals(s_bound, game)) Rolled(master, e); };
        }
        foreach (ActiveSlayerMaster master in tasks.ActiveMasters.Values) Shorten(master);
    }

    static SlayerTasksComponent? Tasks(Game game) =>
        game.Entities.TryGetObjectById("MelvorBase:Slayer", out GameEntity? e) && e is SlayerEntity slayer ? slayer.Tasks : null;

    static int Cap => (int)Math.Round(Engine.GetValue(Setting));

    static void SetLength(ActiveSlayerMaster master, int value)
    {
        s_setting = true;
        try { SetTaskLength(master, value); }
        catch (Exception ex) { Engine.Log("A slayer task could not be shortened: " + ex.Message); }
        finally { s_setting = false; }
    }

    static void Rolled(ActiveSlayerMaster master, ValueChangedEventArgs<int> e)
    {
        if (s_setting) return;
        // "Extend task" raises the length by the master's base length before it marks the task as extended:
        // that one is left to the regular check, which by then sees the task as extended.
        if (!master.IsTaskExtended && e.NewValue == e.OldValue + master.Data.BaseTaskLength && master.TaskProgress < e.OldValue) return;
        // A new task: the game sets its length first and only then puts the progress and "extended" back to
        // zero, so those still belong to the task before.
        int cap = Cap;
        if (cap <= 0 || master.CurrentTask == null || e.NewValue <= cap) return;
        SetLength(master, cap);
    }

    static void Shorten(ActiveSlayerMaster master)
    {
        int cap = Cap;
        if (cap <= 0 || master.CurrentTask == null) return;
        int most = cap + (master.IsTaskExtended ? master.Data.BaseTaskLength : 0);
        if (master.TaskLength <= most) return;
        // A task already further along than that ends with its next kill.
        SetLength(master, Math.Max(most, master.TaskProgress + 1));
    }

    /// <summary>For tests: every master's current task as "length/progress (extended)".</summary>
    public static string Shown(Game game) => Tasks(game) is { } tasks
        ? string.Join(", ", tasks.ActiveMasters.Values.Where(m => m.CurrentTask != null).Select(m => $"{m.Data.Id}: {m.TaskProgress}/{m.TaskLength}{(m.IsTaskExtended ? " ext" : "")}"))
        : "";
}
