using System;
using System.Linq;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Data.Currencies;
using GamesByMalcs.Melvor2.Data.Items;
using Godot;
using MelvorModLoader;

namespace Merchant;

/// <summary>
/// Adds a travelling merchant to the game. He gets his own button in the game's top bar, between Bank and Shop,
/// and his own page where the game shows the Bank and the Shop; F3 opens and leaves it as well.
/// </summary>
public class MerchantMod : IMod
{
    const string ButtonName = "MerchantNavigationButton";

    public static MerchantMod? Instance { get; private set; }

    ModContext _ctx = null!;
    MerchantWindow? _window;
    MerchantPage? _page;
    Game? _pageGame;
    GamesByMalcs.Melvor2.GameMenus.Pages.GamePage? _before;
    Game? _game;
    Button? _button;
    Panel? _dot;
    StyleBoxFlat? _dotGold, _dotGreen;
    string _markNow = "gold";
    bool _wasDown, _toldMissing;
    int _frame;
    ulong _lastTick;

    public MerchantWindow? Window => _window;
    /// <summary>The button in the game's top bar; null while it could not be placed.</summary>
    public Button? TopBarButton => _button != null && GodotObject.IsInstanceValid(_button) && _button.IsInsideTree() ? _button : null;
    /// <summary>The small mark on the button: null while it is off, otherwise "gold" (new goods) or "green" (something to sell him).</summary>
    public string? Mark => TopBarButton == null || _dot == null || !GodotObject.IsInstanceValid(_dot) || !_dot.Visible ? null
        : _markNow;

    static string Tip => $"Merchant  (F3)\nRaw materials for sale, and goods he buys for more than the bank pays.\nNew goods every {Market.HoursPerVisit} hours.";

    public void OnLoad(ModContext ctx)
    {
        Instance = this;
        _ctx = ctx;
        Market.Dir = ctx.Directory;
        Market.Log = ctx.Log;
        _window = new MerchantWindow();
        Market.Changed += () => { if (_window is { Visible: true }) _window.Refresh(); };
        ctx.Frame += Tick;
        ctx.Log("Ready. Open a character: the Merchant is in the top bar, between Bank and Shop (or press F3).");
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
            if (_game != null)
            {
                Market.Bind(_game);
                PlaceButton(_game);
            }
            else
            {
                Market.Unbind();
                _page = null;
                _pageGame = null;
                _before = null;
            }
        }

        ulong now = Time.GetTicksMsec();
        if (now - _lastTick >= 500)
        {
            _lastTick = now;
            if (_game != null)
            {
                Market.Tick(_game);
                if (_window is { Visible: true }) _window.Refresh();   // gold and what you own change on their own
                UpdateMark(_game);
            }
        }

        bool down = Input.IsKeyPressed(Key.F3);
        if (down && !_wasDown) ToggleWindow();
        _wasDown = down;
    }

    /// <summary>Opens the merchant's page; when it is open already, goes back to the page the player came from.</summary>
    public void ToggleWindow()
    {
        Game? game = GameAccess.CurrentGame;
        if (game is not { IsSaveLoaded: true } || MerchantPage.View(_ctx.Tree) is not { } view)
        {
            _ctx.Log("Open a character first (the merchant does not come to the main menu).");
            return;
        }
        try
        {
            if (_window is { Visible: true })
            {
                var back = _before ?? game.Data.GetObject<GamesByMalcs.Melvor2.GameMenus.Pages.EntityPage>("MelvorBase:Bank");
                view.SetCurrentPage(back);
                return;
            }
            _game = game;
            Market.Bind(game);
            _window ??= new MerchantWindow();
            if (_page == null || !ReferenceEquals(_pageGame, game))
            {
                // A page belongs to a game's data; any of the game's own things knows which.
                var space = game.Data.GetDataRegistryOutput<Item>().GetAllObjects().First().Namespace;
                _page = new MerchantPage(space, game, _window);
                _pageGame = game;
            }
            if (MerchantPage.Current(view) is { } current && !ReferenceEquals(current, _page)) _before = current;
            view.SetCurrentPage(_page);
            UpdateMark(game);
        }
        catch (Exception e) { _ctx.Log("The merchant's page could not be opened: " + (e.InnerException ?? e).Message); }
    }

    /// <summary>
    /// The mark on the top bar button: gold when he has arrived with goods the player has not looked at, green
    /// when something he is looking for has turned up in the bank. Opening the window puts it out.
    /// </summary>
    public void UpdateMark(Game game)
    {
        if (TopBarButton is not { } button || _dot == null || !GodotObject.IsInstanceValid(_dot)) return;
        if (_window is { Visible: true }) Market.MarkSeen(game);
        var (newGoods, toSell) = Market.News(game);
        _dot.Visible = newGoods || toSell.Count > 0;
        string mark = toSell.Count > 0 ? "green" : "gold";
        if (_dot.Visible && mark != _markNow)
        {
            _markNow = mark;
            _dot.AddThemeStyleboxOverride("panel", mark == "green" ? _dotGreen! : _dotGold!);
        }
        string tip = Tip;
        if (newGoods) tip += "\n\nNew goods have arrived.";
        if (toSell.Count > 0)
            tip += "\n\nHe is looking for " + string.Join(", ", toSell.Select(w => $"{Market.Name(w.Item)} (you have {Market.Number(Market.Owned(game, w.Item))})")) + ".";
        if (button.TooltipText != tip) button.TooltipText = tip;
    }

    /// <summary>
    /// Puts the merchant's button into the game's top bar, right after the Bank button, looking like its
    /// neighbours. The bar is the game's own; when its layout is not what this was written for, the button is
    /// simply left out and F3 remains.
    /// </summary>
    void PlaceButton(Game game)
    {
        try
        {
            if (_button != null && GodotObject.IsInstanceValid(_button) && _button.IsInsideTree())
            {
                Node home = _button.GetParent();
                if (home.FindChild("BankNavigationButton", false, false) is { } neighbour)
                {
                    int mine = _button.GetIndex(), bankAt = neighbour.GetIndex();
                    if (mine != bankAt + 1) home.MoveChild(_button, mine < bankAt ? bankAt : bankAt + 1);
                }
                return;
            }
            _button = null;

            if (_ctx.Tree.Root.FindChild("BankNavigationButton", true, false) is not Control bank || bank.GetParent() is not BoxContainer row)
            {
                if (!_toldMissing) { _toldMissing = true; _ctx.Log("The game's top bar was not found; the merchant opens with F3 only."); }
                return;
            }
            if (row.FindChild(ButtonName, false, false) is Button old) { row.RemoveChild(old); old.QueueFree(); }

            Texture2D? icon = game.Data.GetDataRegistryOutput<Item>().GetAllObjects().FirstOrDefault(i => i.Id == "MelvorBase:SnakeskinCoinbag")?.Icon
                              ?? game.Data.GetDataRegistryOutput<Currency>().GetAllObjects().FirstOrDefault(c => c.Id == "MelvorBase:GoldPieces")?.Icon;
            LabelSettings? letters = (bank.FindChild("Title", true, false) as Label)?.LabelSettings;

            var button = new Button
            {
                Name = ButtonName, Flat = true, FocusMode = Control.FocusModeEnum.None, CustomMinimumSize = new Vector2(72, 36),
                SizeFlagsVertical = bank.SizeFlagsVertical, MouseDefaultCursorShape = Control.CursorShape.PointingHand,
                TooltipText = Tip,
            };
            var hover = new StyleBoxFlat { BgColor = new Color(1, 1, 1, 0.07f) };
            hover.SetCornerRadiusAll(4);
            var pressed = new StyleBoxFlat { BgColor = new Color(1, 1, 1, 0.13f) };
            pressed.SetCornerRadiusAll(4);
            button.AddThemeStyleboxOverride("normal", new StyleBoxEmpty());
            button.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
            button.AddThemeStyleboxOverride("hover", hover);
            button.AddThemeStyleboxOverride("pressed", pressed);
            button.AddThemeStyleboxOverride("hover_pressed", pressed);

            var column = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore, Alignment = BoxContainer.AlignmentMode.Center };
            column.AddThemeConstantOverride("separation", 0);
            column.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            column.AddChild(new TextureRect
            {
                Texture = icon, CustomMinimumSize = new Vector2(24, 24), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            });
            var title = new Label { Text = "Merchant", HorizontalAlignment = HorizontalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Ignore };
            if (letters != null) title.LabelSettings = letters;
            column.AddChild(title);
            button.AddChild(column);

            // The mark: a small dot at the upper right of the icon.
            StyleBoxFlat Dot(Color colour)
            {
                var box = new StyleBoxFlat { BgColor = colour, BorderColor = new Color(0, 0, 0, 0.7f) };
                box.SetCornerRadiusAll(5);
                box.SetBorderWidthAll(1);
                return box;
            }
            _dotGold = Dot(new Color(0.925f, 0.745f, 0.345f));
            _dotGreen = Dot(new Color(0.380f, 0.800f, 0.490f));
            _dot = new Panel { Name = "Mark", Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
            _dot.AnchorLeft = _dot.AnchorRight = 0.5f;
            _dot.AnchorTop = _dot.AnchorBottom = 0;
            _dot.OffsetLeft = 7; _dot.OffsetRight = 17; _dot.OffsetTop = 4; _dot.OffsetBottom = 14;
            _dot.AddThemeStyleboxOverride("panel", _dotGold);
            _markNow = "gold";
            button.AddChild(_dot);
            button.Pressed += ToggleWindow;

            row.AddChild(button);
            row.MoveChild(button, bank.GetIndex() + 1);
            _button = button;
            UpdateMark(game);
            _ctx.Log("The Merchant button is in the top bar, between Bank and Shop.");
        }
        catch (Exception e)
        {
            if (!_toldMissing) { _toldMissing = true; _ctx.Log("The Merchant button could not be placed (F3 still works): " + e.Message); }
        }
    }
}
