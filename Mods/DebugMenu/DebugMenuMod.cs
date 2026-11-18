using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.GameMenus;
using GamesByMalcs.Melvor2.GameMenus.DebugPopup;
using Godot;
using MelvorModLoader;

namespace DebugMenuMod;

/// <summary>
/// Opens the developer debug menu that ships inside the game but is never connected or shown.
/// F9 (or the game's own unused shortcut, Ctrl + the key left of 1) toggles it while a save is open.
/// </summary>
public class DebugMenuMod : IMod
{
    static readonly StringName OriginalAction = "open_debug_menu";

    ModContext _ctx = null!;
    bool _wasDown;
    Game? _boundGame;

    public void OnLoad(ModContext ctx)
    {
        _ctx = ctx;
        ctx.Frame += Tick;
        ctx.Log("Ready. Press F9 while a character is open.");
    }

    void Tick()
    {
        bool down = Input.IsKeyPressed(Key.F9)
                    || (InputMap.HasAction(OriginalAction) && Input.IsActionPressed(OriginalAction));
        if (down && !_wasDown)
            Toggle();
        _wasDown = down;
    }

    void Toggle()
    {
        GameView? view = GameAccess.FindGameView();
        Game? game = GameAccess.CurrentGame;
        if (view == null || game == null)
        {
            _ctx.Log("F9: open a character first (the menu does not work on the main menu).");
            return;
        }

        // "DebugPopup" is an exported node reference on GameView, so Godot's property system can read it.
        DebugMenu? menu = view.Get("DebugPopup").As<DebugMenu>();
        if (menu == null)
        {
            _ctx.Log("The debug menu was not found in the scene (the game may have been updated).");
            return;
        }

        if (!ReferenceEquals(_boundGame, game))
        {
            menu.Bind(game);   // the step the released game never performs
            _boundGame = game;
            MenuStyle.Apply(menu);
            // The menu read this limit in _Ready, before any game existed, so it is stuck at 0 hours.
            if (menu.Get("OfflineTime").As<SpinBox>() is { } offlineHours)
                offlineHours.MaxValue = game.OfflineProgress.Component.MaxOfflineTime / 3600000.0;
            _ctx.Log("The debug menu is connected to the game.");
        }

        if (menu.Visible) menu.Hide();
        else menu.PopupCentered();
    }
}
