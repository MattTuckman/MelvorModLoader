using System;
using System.Linq;
using System.Runtime.CompilerServices;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Entities;
using GamesByMalcs.Melvor2.Entities.Ranching;

namespace CheatMenu;

/// <summary>
/// "Ranching: animals never die": every animal's lifetime stands still while the setting is on.
///
/// The game counts an animal's lifetime down by one each game minute (ActiveRanchingAnimal.TickLifespan): it takes
/// one off, tells the Ranching component "a minute of life went by", and only then checks for 0 and lets the animal
/// die. That message is answered here, before the check: every animal's remaining lifetime is put back to what it
/// was when the setting first saw it. So nothing dies, and the lifetime shown on the Ranching page stands still.
/// Switched off, the animals age on from where they stood.
/// </summary>
public static class RanchingForever
{
    public const string Setting = "skills.ranchingImmortal";

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_LifespanTicks")]
    static extern void SetLifespan(ActiveRanchingAnimal animal, double ticks);

    static RanchingPassiveComponent? s_ranching;
    static ConditionalWeakTable<ActiveRanchingAnimal, StrongBox<double>> s_frozen = new();

    /// <summary>For tests: how many times a minute of an animal's life was given back since the character was opened.</summary>
    public static int Kept { get; private set; }

    /// <summary>Call when a character is opened.</summary>
    public static void Bind(Game game)
    {
        if (s_ranching != null) s_ranching.AnimalLifespanTimerProgressed -= LifespanProgressedHandler;
        s_ranching = null;
        s_frozen = new();
        Kept = 0;
        try
        {
            if (game.Entities.TryGetObjectById("MelvorBase:Ranching", out GameEntity? entity) && entity != null)
            {
                s_ranching = entity.Components.Values.OfType<RanchingPassiveComponent>().FirstOrDefault();
                if (s_ranching != null) s_ranching.AnimalLifespanTimerProgressed += LifespanProgressedHandler;
            }
        }
        catch (Exception e) { Engine.Log("Ranching: the animals' lifetime could not be watched: " + e.Message); }
    }

    static void LifespanProgressedHandler(object? sender, RanchingAnimalTimeSpentAliveEventArgs e)
    {
        if (sender is not RanchingPassiveComponent ranching) return;
        if (!Engine.IsActive(Setting))
        {
            s_frozen.Clear();   // switched on again later: the lifetime stands still from that moment
            return;
        }
        try
        {
            foreach (ActiveRanchingLand land in ranching.ActiveLand.Values)
                foreach (ActiveRanchingAnimal animal in land.ActiveEnclosure?.ActiveAnimals ?? Array.Empty<ActiveRanchingAnimal>())
                {
                    // The animal that just aged is one minute down already; at least one minute is always left,
                    // so the game's "0 = dies" check right after this never fires.
                    StrongBox<double> frozen = s_frozen.GetValue(animal, a => new StrongBox<double>(
                        Math.Max(1, a.Recipe == e.Animal ? a.LifespanTicks + 1 : a.LifespanTicks)));
                    if (animal.LifespanTicks < frozen.Value)
                    {
                        SetLifespan(animal, frozen.Value);
                        Kept++;
                    }
                }
        }
        catch (Exception ex) { Engine.Log("Ranching: an animal's lifetime could not be kept: " + ex.Message); }
    }
}
