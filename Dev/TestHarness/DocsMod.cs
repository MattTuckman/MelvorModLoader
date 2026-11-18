// The Cookbook's mod-shaped recipes (hotkey, timer, character change, a button in the bank, two mods talking),
// written exactly as on the page. Compiled with the test rig; not loaded as a mod (no IMod here: the class
// below only mirrors one).
using System;
using System.Collections.Generic;
using System.Linq;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.Entities.Bank;
using GamesByMalcs.Melvor2.SearchBars;
using Godot;
using MelvorModLoader;

namespace TestHarness;

sealed class DocsMod
{
    ModContext _ctx = null!;

    // ---- a hotkey
    bool _wasDown;

    void HotkeyFrame()
    {
        bool down = Input.IsKeyPressed(Key.F10);
        if (down && !_wasDown) Toggle();   // once per press, not every frame it is held
        _wasDown = down;
    }

    void Toggle() => _ctx.Log("F10");

    // ---- every second
    ulong _last;

    void TimerFrame()
    {
        ulong now = Time.GetTicksMsec();
        if (now - _last < 1000) return;
        _last = now;
        // ... once a second
    }

    // ---- a character was opened or closed
    Game? _game;
    int _frame;

    void CharacterFrame()
    {
        if (++_frame % 30 != 0) return;                       // twice a second is enough
        Game? now = GameAccess.CurrentGame;
        if (now is not { IsSaveLoaded: true }) now = null;
        if (ReferenceEquals(now, _game)) return;               // nothing changed
        _game = now;
        if (now != null) OnCharacterOpened(now);
        else OnCharacterClosed();
    }

    void OnCharacterOpened(Game game) => _ctx.Log($"{game.CharacterName} opened");
    void OnCharacterClosed() => _ctx.Log("back on the main menu");

    // ---- a button in the bank
    BankInventoryComponentView? _bankView;
    Button? _button;

    public void OnLoad(ModContext ctx)
    {
        _ctx = ctx;
        // The bank page is created the first time the player opens it: catch it when it enters the scene.
        ctx.Tree.NodeAdded += node => { if (node is BankInventoryComponentView view) _bankView = view; };
        ctx.Frame += BankFrame;
    }

    void BankFrame()
    {
        if (_bankView == null) return;
        if (!GodotObject.IsInstanceValid(_bankView)) { _bankView = null; return; }   // the page was freed
        if (!_bankView.IsNodeReady()) return;                                         // not built yet
        if (_button != null && GodotObject.IsInstanceValid(_button)) return;          // already there

        SearchBar? search = GameAccess.FindNode<SearchBar>(_bankView);
        if (search?.GetParent() is not BoxContainer row)
        {
            _ctx.Log("The bank's search box is not where it used to be (the game was updated?).");
            _bankView = null;
            return;
        }
        _button = new Button { Text = "My button", TooltipText = "Added by my mod" };
        _button.Pressed += () => _ctx.Log("Pressed!");
        row.AddChild(_button);
        row.MoveChild(_button, search.GetIndex());   // just before the search box
    }

    public Button? BankButton => _button;   // for the check only

    // ---- the "Building windows" page: a window
    CanvasLayer? _layer;
    VBoxContainer _rows = null!;

    void BuildWindow()
    {
        _layer = new CanvasLayer { Layer = 110, Visible = false };       // above the game's screens

        // A full-screen container that centres the window but lets clicks through everywhere else.
        var center = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _layer.AddChild(center);

        // The window itself: a panel with its own background (the game's panel styles are see-through).
        var panel = new PanelContainer { CustomMinimumSize = new Vector2(420, 0) };
        var frame = new StyleBoxFlat { BgColor = new Color(0.08f, 0.09f, 0.11f, 0.97f), BorderColor = new Color(0.9f, 0.75f, 0.35f) };
        frame.SetBorderWidthAll(1);
        frame.SetCornerRadiusAll(8);
        frame.SetContentMarginAll(16);
        panel.AddThemeStyleboxOverride("panel", frame);
        center.AddChild(panel);

        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 8);
        panel.AddChild(column);

        var title = new Label { Text = "MY MOD" };
        title.AddThemeFontSizeOverride("font_size", 20);
        title.AddThemeColorOverride("font_color", new Color(0.93f, 0.75f, 0.35f));
        column.AddChild(title);

        _rows = new VBoxContainer();
        column.AddChild(_rows);

        var close = new Button { Text = "Close", SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd };
        close.Pressed += () => _layer.Visible = false;
        column.AddChild(close);

        _ctx.Tree.Root.AddChild(_layer);
    }

    // One row per item: its picture, its name and how many the bank holds.
    void ShowItems(Game game, BankEntity bank, IEnumerable<Item> items)
    {
        foreach (Node old in _rows.GetChildren()) old.QueueFree();
        foreach (Item item in items)
        {
            var row = new HBoxContainer();
            row.AddChild(new TextureRect
            {
                Texture = item.Icon, CustomMinimumSize = new Vector2(32, 32),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            });
            row.AddChild(new Label { Text = item.DisplayName.GetPluralText(1), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
            row.AddChild(new Label { Text = bank.Inventory.GetQuantity(item).ToString("N0") });
            _rows.AddChild(row);
        }
    }

    public void ShowWindowForCheck(Game game, BankEntity bank, IEnumerable<Item> items)   // for the check only
    {
        if (_layer == null) BuildWindow();
        ShowItems(game, bank, items);
        _layer!.Visible = true;
    }

    public void HideWindowForCheck() { if (_layer != null) _layer.Visible = false; }

    // ---- two mods working together
    void Publish(int value) => _ctx.Tree.Root.SetMeta("MyMod_Value", value);

    int ReadFromOtherMod()
    {
        Node root = _ctx.Tree.Root;
        return root.HasMeta("OtherMod_Value") ? root.GetMeta("OtherMod_Value").AsInt32() : 0;
    }

    bool OtherModInstalled() => Loader.LoadedMods.Contains("ActionQueue");

    // ---- a Godot timer or signal of your own: check for offline progress yourself
    void OwnTimer()
    {
        _ctx.Tree.CreateTimer(5.0).Timeout += () =>
        {
            if (GameAccess.IsComputingOffline) return;   // the game runs on another thread right now: hands off
            // ... safe to touch the game here
        };
    }
}
