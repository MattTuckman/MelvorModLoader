using GamesByMalcs.Melvor2;
using Godot;
using MelvorModLoader;

namespace CheatMenu;

/// <summary>
/// F8 opens a menu of cheats and tuning options for the open character.
/// Values are remembered per character (settings.json) and never written into the save file.
/// </summary>
public class CheatMenuMod : IMod
{
    public static CheatMenuMod? Instance { get; private set; }

    ModContext _ctx = null!;
    Menu? _menu;
    bool _wasDown;
    int _frame;

    public Menu? Menu => _menu;

    public void OnLoad(ModContext ctx)
    {
        Instance = this;
        _ctx = ctx;
        Settings.Dir = ctx.Directory;
        Lang.Dir = ctx.Directory;
        PickListText.T = Lang.T;
        Engine.Log = ctx.Log;
        Crops.Log = ctx.Log;
        ctx.Frame += Tick;
        GameSpeed.Tree = ctx.Tree;
        ctx.Tree.PhysicsFrame += GameSpeed.OnPhysicsFrame;
        ctx.Log("Ready. Press F8 while a character is open.");
    }

    void Tick()
    {
        if (_ctx.Tree.Paused) return;   // the game is computing offline progress on its own thread: hands off
        Engine.Guard();                 // costs nothing unless the game changed an action time

        // Notice a character being opened (or closed) a few times per second.
        if (++_frame % 20 == 0)
        {
            Game? game = GameAccess.CurrentGame;
            if (game is { IsSaveLoaded: true })
            {
                Engine.Bind(game);
                Settings.Flush();                               // writes settings.json only if something changed
                if (_frame % 60 == 0) AutoSell.RunOnce(game);   // about once a second
            }
            else if (game == null && _menu is { Visible: true }) _menu.Hide();
        }

        bool down = Input.IsKeyPressed(Key.F8);
        if (down && !_wasDown) ToggleMenu();
        _wasDown = down;
    }

    public void ToggleMenu()
    {
        if (_menu is { Visible: true }) { _menu.Hide(); return; }
        Game? game = GameAccess.CurrentGame;
        if (game is not { IsSaveLoaded: true })
        {
            _ctx.Log("F8: open a character first (the menu does not work on the main menu).");
            return;
        }
        Engine.Bind(game);
        // "Auto" and the game's language was changed since the window was built: build it again in the new one.
        if (_menu != null && _menu.BuiltIn != Lang.Active) { _menu.Dispose(); _menu = null; }
        _menu ??= NewMenu();
        _menu.Show(game);
    }

    Menu NewMenu()
    {
        var menu = new Menu(_ctx.Tree);
        menu.LanguageChosen += SetLanguage;
        return menu;
    }

    /// <summary>Another language: the window is built again in it, on the same page.</summary>
    public void SetLanguage(string code)
    {
        Lang.Set(code);
        if (_menu == null) return;
        bool open = _menu.Visible;
        int page = _menu.CurrentPage;
        _menu.Dispose();
        _menu = NewMenu();
        if (open && GameAccess.CurrentGame is { IsSaveLoaded: true } game)
        {
            _menu.Show(game);
            _menu.ShowPage(page);
        }
        _ctx.Log($"Language: {code} (shown in {Lang.Active}).");
    }
}
