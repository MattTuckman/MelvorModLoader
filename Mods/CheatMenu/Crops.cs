using System;
using System.Linq;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Core.QueuedTimers;
using GamesByMalcs.Melvor2.Entities;
using GamesByMalcs.Melvor2.Entities.Farming;

namespace CheatMenu;

/// <summary>
/// The game fixes a crop's growing time at the moment it is planted: a speed setting changed afterwards only
/// counts for what is planted next. A player who turns the speed up and sees the old "2 h left" on his plots
/// takes that for the setting making things slower. So after a speed setting changed, the crops already in the
/// ground get the time they would get if planted now, keeping the share of it that has already passed.
/// </summary>
public static class Crops
{
    public static Action<string> Log = _ => { };

    /// <summary>Brings every growing crop's timer in line with its current growing time. Returns how many changed.</summary>
    public static int Retime(Game game)
    {
        int changed = 0;
        try
        {
            if (!game.Entities.TryGetObjectById("MelvorBase:Farming", out GameEntity? entity) || entity == null) return 0;
            foreach (FarmingPassiveComponent farm in entity.Components.Values.OfType<FarmingPassiveComponent>())
                foreach (ActiveFarmingPlot plot in farm.ActivePlots.Values)
                {
                    if (plot.State.Timer is not { IsRunning: true } timer || plot.PlantedRecipe is not { } crop) continue;
                    // Growing and growing again are sped up by the game's bonuses; the time a grown plant just stands there is not.
                    double time = plot.State.GetType().Name switch
                    {
                        "GrowingState" => crop.Stats.Interval.Value,
                        "RegrowingState" => crop.Stats.RegrowInterval.Value,
                        _ => 0,
                    };
                    if (time < 20) continue;                      // (one game tick is 16.7 ms; a timer cannot be shorter)
                    int full = GamesByMalcs.Melvor2.Core.TickTimer.TickTimer.MSToTicks(time);
                    int was = timer.WaitTicks;
                    if (full <= 0 || full == was) continue;
                    int left = Math.Max(1, (int)Math.Round((double)timer.TicksLeft / was * full));
                    // The timer's own way of being scheduled anew: started with what is left, then told its full length.
                    timer.WaitTicks = left;
                    timer.Start();
                    timer.WaitTicks = full;
                    changed++;
                }
        }
        catch (Exception e) { Log("The crops' growing times could not be brought up to date: " + (e.InnerException ?? e).Message); }
        return changed;
    }
}
