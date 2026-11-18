using CheatMenu;
using GamesByMalcs.Melvor2;
using Godot;
using MelvorModLoader;

namespace ActionQueue;

/// <summary>
/// F6 opens the action queue. While it runs, one line at the top of the screen says which step is active.
/// </summary>
public class ActionQueueMod : IMod
{
    public static ActionQueueMod? Instance { get; private set; }

    ModContext _ctx = null!;
    QueueWindow? _window;
    Label? _chip;
    Game? _game;
    bool _wasDown;
    int _frame;
    ulong _lastTick;

    public QueueWindow? Window => _window;

    public void OnLoad(ModContext ctx)
    {
        Instance = this;
        _ctx = ctx;
        Queue.Dir = ctx.Directory;
        Queue.Log = ctx.Log;
        ctx.Frame += Tick;
        ctx.Log("Ready. Press F6 while a character is open.");
    }

    void Tick()
    {
        // The game is computing offline progress on its own thread: hands off, and do not count that time.
        if (_ctx.Tree.Paused) { _lastTick = 0; return; }

        // Notice a character being opened or closed a couple of times per second.
        if (++_frame % 30 == 0)
        {
            Game? game = GameAccess.CurrentGame;
            _game = game is { IsSaveLoaded: true } ? game : null;
            if (_game != null) Queue.Bind(_game);
            else if (_window is { Visible: true }) _window.Hide();
            if (_frame % 60 == 0) _window?.Pulse();
        }

        ulong now = Time.GetTicksMsec();
        if (now - _lastTick >= 500)
        {
            double seconds = _lastTick == 0 ? 0.5 : (now - _lastTick) / 1000.0;
            _lastTick = now;
            if (_game != null && Queue.Running) Queue.Tick(_game, System.Math.Min(seconds, 5));
            UpdateChip();
        }

        bool down = Input.IsKeyPressed(Key.F6);
        if (down && !_wasDown) ToggleWindow();
        _wasDown = down;
    }

    public void ToggleWindow()
    {
        if (_window is { Visible: true }) { _window.Hide(); return; }
        Game? game = GameAccess.CurrentGame;
        if (game is not { IsSaveLoaded: true })
        {
            _ctx.Log("F6: open a character first (the window does not work on the main menu).");
            return;
        }
        _game = game;
        Queue.Bind(game);
        _window ??= new QueueWindow(_ctx.Tree);
        _window.Show(game);
    }

    /// <summary>A small reminder at the top of the screen while the queue is running and its window is closed.</summary>
    void UpdateChip()
    {
        bool show = _game != null && Queue.Running && _window is not { Visible: true };
        if (!show)
        {
            if (_chip != null) _chip.Visible = false;
            return;
        }
        if (_chip == null)
        {
            var layer = new CanvasLayer { Layer = 117, Name = "ActionQueueChip" };
            _chip = new Label { MouseFilter = Control.MouseFilterEnum.Ignore, HorizontalAlignment = HorizontalAlignment.Center };
            _chip.AddThemeStyleboxOverride("normal", Style.Box(new Color(Style.Window, 0.9f), 6, Style.AccentDim, 1, 12, 4));
            _chip.AddThemeColorOverride("font_color", Style.Text);
            _chip.AddThemeFontSizeOverride("font_size", 13);
            _chip.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.CenterTop);
            _chip.GrowHorizontal = Control.GrowDirection.Both;
            _chip.OffsetTop = 56;
            layer.AddChild(_chip);
            _ctx.Tree.Root.AddChild(layer);
        }
        _chip.Text = Queue.Headline + "   (F6)";
        _chip.Visible = true;
    }
}
