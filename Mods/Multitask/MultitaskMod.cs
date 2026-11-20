using GamesByMalcs.Melvor2;
using Godot;
using MelvorModLoader;

namespace Multitask;

/// <summary>
/// F5 (or the loader's Mods menu) opens the Multitask window. With a slot switched on, the action before the latest
/// one keeps running.
/// </summary>
public class MultitaskMod : IMod
{
    public static MultitaskMod? Instance { get; private set; }

    ModContext _ctx = null!;
    SlotWindow? _window;
    Game? _game;
    int _frame;
    ulong _lastTick;

    public SlotWindow? Window => _window;
    internal WindowKey Key { get; private set; } = null!;
    /// <summary>For tests: the key shown for the window ("F5", or "" when switched off).</summary>
    public string KeyLabel => Key.Label;

    public void OnLoad(ModContext ctx)
    {
        Instance = this;
        _ctx = ctx;
        Slots.Dir = ctx.Directory;
        Slots.Log = ctx.Log;
        Key = new WindowKey(ctx, "Multitask", ToggleWindow, Godot.Key.F5);
        ctx.Frame += Tick;
        ctx.Log($"Ready. Press {Key.Spoken} while a character is open.");
    }

    void Tick()
    {
        // The game is computing offline progress on its own thread: hands off.
        if (_ctx.Tree.Paused) return;

        // Notice a character being opened or closed a couple of times per second.
        if (++_frame % 30 == 0)
        {
            Game? game = GameAccess.CurrentGame;
            _game = game is { IsSaveLoaded: true } ? game : null;
            if (_game != null) Slots.Bind(_game);
            else
            {
                Slots.Unbind();
                if (_window is { Visible: true }) _window.Hide();
            }
        }

        ulong now = Time.GetTicksMsec();
        if (now - _lastTick >= 500)
        {
            _lastTick = now;
            if (_game != null) Slots.Tick(_game, now / 1000.0);
        }

        Key.Poll();
    }

    public void ToggleWindow()
    {
        if (_window is { Visible: true }) { _window.Hide(); return; }
        Game? game = GameAccess.CurrentGame;
        if (game is not { IsSaveLoaded: true })
        {
            _ctx.Log("Multitask: open a character first (the window does not work on the main menu).");
            return;
        }
        _game = game;
        Slots.Bind(game);
        _window ??= new SlotWindow(_ctx.Tree);
        _window.Show(game);
    }
}
