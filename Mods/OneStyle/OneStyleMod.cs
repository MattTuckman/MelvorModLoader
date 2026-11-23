using GamesByMalcs.Melvor2;
using MelvorModLoader;

namespace OneStyle;

/// <summary>Play with one combat style: pick Melee, Ranged or Magic per character (Mods button in the top bar).</summary>
public class OneStyleMod : IMod
{
    public static OneStyleMod? Instance { get; private set; }

    ModContext _ctx = null!;
    ClassWindow? _window;
    int _frame;

    public ClassWindow? Window => _window;
    internal WindowKey Key { get; private set; } = null!;

    public void OnLoad(ModContext ctx)
    {
        Instance = this;
        _ctx = ctx;
        Store.Dir = ctx.Directory;
        Store.Log = ctx.Log;
        Patches.Log = ctx.Log;
        // A character is opened: the game adds its screen, then works out the time it was closed on its own thread.
        // Bind right here, so the class already counts for that time.
        ctx.Tree.NodeAdded += node =>
        {
            if (node is not GamesByMalcs.Melvor2.GameMenus.GameView) return;
            try { if (GameAccess.CurrentGame is { IsSaveLoaded: true } game) Patches.Bind(game); }
            catch (System.Exception e) { ctx.Log("One Style: could not get ready early: " + e.Message); }
        };
        Key = new WindowKey(ctx, "One Style", ToggleWindow, Godot.Key.None);
        ctx.Frame += Tick;
        ctx.Log("Ready. Open a character and pick your class from the Mods button in the top bar.");
    }

    void Tick()
    {
        if (_ctx.Tree.Paused) return;   // the game is computing offline progress on its own thread: hands off
        _window?.Tick(_ctx.Tree.Root.GetProcessDeltaTime());
        if (++_frame % 20 == 0)
        {
            Game? game = GameAccess.CurrentGame;
            if (game is { IsSaveLoaded: true }) Patches.Bind(game);
            else
            {
                Patches.Unbind();
                if (_window is { Visible: true }) _window.Hide();
            }
        }
        Key.Poll();
    }

    public void ToggleWindow()
    {
        if (_window is { Visible: true }) { _window.Hide(); return; }
        Game? game = GameAccess.CurrentGame;
        if (game is not { IsSaveLoaded: true })
        {
            _ctx.Log("One Style: open a character first.");
            return;
        }
        Patches.Bind(game);
        _window ??= new ClassWindow(_ctx.Tree);
        _window.Show();
    }
}
