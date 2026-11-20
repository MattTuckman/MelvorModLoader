using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Core;
using GamesByMalcs.Melvor2.Data;
using GamesByMalcs.Melvor2.Entities;
using GamesByMalcs.Melvor2.Entities.Views;
using GamesByMalcs.Melvor2.Localization;
using Godot;

namespace ActionQueue;

/// <summary>
/// Lets the queue go on while the game computes offline progress. The game does that on a thread of its own, with
/// the scene tree paused and its pages deaf to the game's events; mods get no frames then. So the queue gets a
/// heartbeat inside the game's own tick instead: one small object in the game's list of things that tick, which
/// once a game second, and only while the game ticks away from the main thread, gives the queue its turn - on the
/// game's thread, between two ticks, so nothing else touches the game at that moment.
/// </summary>
static class Offline
{
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_TickingEntities")]
    static extern List<ITickingEntity> TickingEntities(Game game);

    static Game? s_game;
    static Heartbeat? s_beat;

    /// <summary>For tests: game seconds the heartbeat saw offline.</summary>
    public static double Seconds { get; private set; }

    /// <summary>Puts the heartbeat into this character's game (on the main thread, outside a tick).</summary>
    public static void Attach(Game game)
    {
        if (ReferenceEquals(s_game, game) && s_beat != null) return;
        try
        {
            if (s_game != null && s_beat != null) TickingEntities(s_game).Remove(s_beat);
            s_game = game;
            s_beat = new Heartbeat(game);
            List<ITickingEntity> list = TickingEntities(game);
            list.Add(s_beat);
        }
        catch (Exception e)
        {
            s_beat = null;
            Queue.Log?.Invoke("Offline progress for the queue is not available: " + e.Message);
        }
    }

    static void Beat(Game game)
    {
        // The game ticks on the main thread while it is played. Off it, it is catching up: offline progress at load,
        // after the computer slept, or a lag the game makes good. (Game.Flags.IsOffline is only set for some of
        // those, so the thread decides.) The queue gets a turn per game second, time measured in the game's ticks.
        if (Targets.OnMainThread) { s_pending = 0; return; }
        s_pending += Constants.TickInterval / 1000.0;
        if (s_pending < 1.0) return;
        double seconds = s_pending;
        s_pending = 0;
        Seconds += seconds;
        try { Queue.OfflineTick(game, seconds); }
        catch (Exception e) { Queue.Log?.Invoke("The queue failed during offline progress: " + e.Message); }
    }

    static double s_pending;

    /// <summary>The heartbeat: it borrows its name and namespace from the game's own time-of-day entity.</summary>
    sealed class Heartbeat(Game game) : ITickingEntity
    {
        readonly GameEntity _donor = game.TimeAndWeather;
        public void Tick() => Beat(game);
        public LocalizedString DisplayName => _donor.DisplayName;
        public string IconPath => "";
        public Texture2D Icon => null!;
        public Texture2D Background => null!;
        public bool RequiresAttention => false;
        public event EventHandler<ValueChangedEventArgs<bool>>? RequiresAttentionChanged { add { } remove { } }
        public BaseActiveEntityView? GetActiveView() => null;
        public void SelectCurrentAction() { }
        public string Id => "ActionQueue:OfflineHeartbeat";
        public DataNamespace Namespace => _donor.Namespace;
        public string LocalId => "OfflineHeartbeat";
    }
}
