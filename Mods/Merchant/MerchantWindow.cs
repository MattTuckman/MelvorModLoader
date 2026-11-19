using System;
using System.Collections.Generic;
using System.Linq;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Data.Items;
using Godot;

namespace Merchant;

/// <summary>
/// The merchant's page: what he sells this visit, what he buys, the standing order, and when he comes back with
/// new goods. It is shown as one of the game's own pages (see <see cref="MerchantPage"/>), in the place where the
/// Bank and the Shop appear, and is built of the same panels, letterings and buttons (see <see cref="Look"/>).
/// The game makes a page anew every time it is opened and throws it away when the player goes elsewhere; so does this.
/// </summary>
public sealed class MerchantWindow
{
    static readonly (string Text, int Count)[] Amounts = { ("1", 1), ("10", 10), ("100", 100), ("All", int.MaxValue) };
    const float CardWidth = 330;

    Control? _root;
    Label _gold = new(), _next = new(), _status = new(), _orderNote = new();
    OptionButton _order = new();
    GridContainer _sale = new(), _wanted = new();
    readonly List<Row> _saleRows = new(), _wantedRows = new();
    List<Item> _orderable = new();
    List<Offer>? _shownOffers;
    List<Want>? _shownWanted;
    Game? _game;

    sealed class Row
    {
        public required Label Name, Tag, Price, Left, Have;
        public required Button[] Buttons;
        public required PanelContainer Card;
    }

    /// <summary>True while the page is the one the game is showing.</summary>
    public bool Visible => _root != null && GodotObject.IsInstanceValid(_root) && _root.IsInsideTree();

    /// <summary>The picture the page and its button in the top bar go by.</summary>
    public static Texture2D? Picture(Game game) =>
        game.Data.GetDataRegistryOutput<Item>().GetAllObjects().FirstOrDefault(i => i.Id == "MelvorBase:SnakeskinCoinbag")?.Icon;

    // ---------------------------------------------------------------- building the page

    /// <summary>Builds the page for the game to put into its page area. Called each time the page is opened.</summary>
    public Control Build(Game game)
    {
        _game = game;
        _saleRows.Clear();
        _wantedRows.Clear();
        _shownOffers = null;
        _shownWanted = null;
        _orderable = new();

        var root = new MarginContainer { Name = "MerchantView" };
        root.AddThemeConstantOverride("margin_left", 12);
        root.AddThemeConstantOverride("margin_top", 12);
        root.AddThemeConstantOverride("margin_right", 12);
        root.AddThemeConstantOverride("margin_bottom", 8);
        var columns = new HBoxContainer();
        columns.AddThemeConstantOverride("separation", 8);
        root.AddChild(columns);
        columns.AddChild(BuildSide(game));

        // ---- the board: a strip across the top, then the goods
        var board = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        board.AddThemeStyleboxOverride("panel", Look.Board);
        columns.AddChild(board);
        var body = new VBoxContainer();
        body.AddThemeConstantOverride("separation", 12);
        board.AddChild(body);

        var strip = new PanelContainer();
        strip.AddThemeStyleboxOverride("panel", Look.Strip());
        var top = new HBoxContainer { CustomMinimumSize = new Vector2(0, 36) };
        top.AddThemeConstantOverride("separation", 10);
        top.AddChild(Look.Icon(Market.GoldIcon(game), 22));
        _gold = Look.Body("");
        top.AddChild(_gold);
        top.AddChild(new VSeparator { CustomMinimumSize = new Vector2(4, 22), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter });
        _status = Look.Value("");
        _status.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _status.ClipText = true;
        top.AddChild(_status);
        _next = Look.Body("");
        _next.Modulate = Look.Gold;
        top.AddChild(_next);
        strip.AddChild(top);
        body.AddChild(strip);

        var scroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        var inner = new MarginContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        foreach (string m in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" }) inner.AddThemeConstantOverride(m, 4);
        var goods = new VBoxContainer();
        goods.AddThemeConstantOverride("separation", 10);
        inner.AddChild(goods);
        scroll.AddChild(inner);
        body.AddChild(scroll);

        goods.AddChild(Section("For sale this visit", Picture(game)));
        _sale = Grid();
        goods.AddChild(_sale);
        goods.AddChild(new Control { CustomMinimumSize = new Vector2(0, 4) });
        goods.AddChild(Section("He is looking for", Market.GoldIcon(game)));
        _wanted = Grid();
        goods.AddChild(_wanted);

        // As many cards side by side as there is room for.
        GridContainer sale = _sale, wanted = _wanted;
        void Fit()
        {
            int fit = Math.Clamp((int)((goods.Size.X + 8) / (CardWidth + 8)), 1, 6);
            if (sale.Columns != fit) { sale.Columns = fit; Pad(sale); }
            if (wanted.Columns != fit) { wanted.Columns = fit; Pad(wanted); }
        }
        goods.Resized += Fit;

        _root = root;
        FillOrder(game);
        Refresh();
        return root;
    }

    /// <summary>The panel at the side: who he is, and the standing order.</summary>
    Control BuildSide(Game game)
    {
        var holder = new PanelContainer { CustomMinimumSize = new Vector2(290, 0) };
        holder.AddThemeStyleboxOverride("panel", new StyleBoxEmpty());
        var down = new MarginContainer();
        down.AddThemeConstantOverride("margin_top", 16);
        holder.AddChild(down);
        var side = new PanelContainer();
        side.AddThemeStyleboxOverride("panel", Look.Side);
        down.AddChild(side);

        // the paper tab sitting on the panel's upper edge, as on the Shop's category list
        var over = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        holder.AddChild(over);
        var tab = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        tab.AddThemeStyleboxOverride("panel", Look.Tab);
        Label title = Look.OnPaper("Travelling Merchant");
        title.HorizontalAlignment = HorizontalAlignment.Center;
        tab.AddChild(title);
        over.AddChild(tab);
        tab.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.CenterTop);
        tab.GrowHorizontal = Control.GrowDirection.Both;

        var list = new VBoxContainer();
        list.AddThemeConstantOverride("separation", 10);
        side.AddChild(list);
        list.AddChild(new Control { CustomMinimumSize = new Vector2(0, 8) });
        TextureRect face = Look.Icon(Picture(game), 72);
        face.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;
        list.AddChild(face);

        Label Wrapped(string text)
        {
            Label label = Look.Value(text);
            label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            label.CustomMinimumSize = new Vector2(200, 0);
            return label;
        }
        list.AddChild(Divider("What he sells"));
        list.AddChild(Wrapped($"Raw materials you are already able to gather, for {Factor(Market.Markup)} times what the bank pays."));
        list.AddChild(Divider("What he buys"));
        list.AddChild(Wrapped($"What he is looking for, at {Factor(Market.WantedBonus)} times the bank price."));
        list.AddChild(Divider("When"));
        list.AddChild(Wrapped($"He comes back with new goods every {Market.HoursPerVisit} hours."));

        // ---- the standing order: one raw material he always brings
        list.AddChild(Divider("Your order"));
        _order = new OptionButton { FitToLongestItem = false, ClipText = true, CustomMinimumSize = new Vector2(0, 36), FocusMode = Control.FocusModeEnum.None };
        _order.AddThemeConstantOverride("icon_max_width", 20);
        _order.TooltipText = "Choose one raw material: from the next visit on he always has it with him.";
        _order.ItemSelected += index =>
        {
            if (_game == null) return;
            Say(Market.SetOrder(_game, index <= 0 || index > _orderable.Count ? null : _orderable[(int)index - 1]));
        };
        _order.GetPopup().AboutToPopup += PlaceOrderList;
        list.AddChild(_order);
        _orderNote = Wrapped("");
        list.AddChild(_orderNote);
        return holder;
    }

    static string Factor(decimal factor) => factor.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>A small grey caption between two lines, as on the Shop's cards ("Provides").</summary>
    static Control Divider(string text)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        row.AddChild(new HSeparator { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter });
        row.AddChild(Look.Caption(text));
        row.AddChild(new HSeparator { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter });
        return row;
    }

    /// <summary>A heading across the board, as the Shop has above each group of purchases.</summary>
    static Control Section(string title, Texture2D? picture)
    {
        var strip = new PanelContainer();
        strip.AddThemeStyleboxOverride("panel", Look.Strip(8));
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        row.AddChild(Look.Icon(picture, 24));
        row.AddChild(Look.Heading(title));
        row.AddChild(new HSeparator { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter, SelfModulate = new Color(1, 1, 1, 0.5f) });
        strip.AddChild(row);
        return strip;
    }

    static GridContainer Grid()
    {
        var grid = new GridContainer { Columns = 4 };
        grid.AddThemeConstantOverride("h_separation", 8);
        grid.AddThemeConstantOverride("v_separation", 8);
        return grid;
    }

    /// <summary>One card: picture and name, the price, how many there are, and the buttons.</summary>
    Row MakeRow(GridContainer list, Item item, string leftCaption, Action<int> pressed)
    {
        var card = new PanelContainer { CustomMinimumSize = new Vector2(CardWidth, 0), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        card.AddThemeStyleboxOverride("panel", Look.Card);
        var lines = new VBoxContainer();
        lines.AddThemeConstantOverride("separation", 8);
        card.AddChild(lines);

        // picture, name and price
        var head = new HBoxContainer();
        head.AddThemeConstantOverride("separation", 10);
        var frame = new PanelContainer { CustomMinimumSize = new Vector2(46, 46), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter, TooltipText = Market.Name(item) };
        frame.AddThemeStyleboxOverride("panel", Look.IconFrame);
        frame.AddChild(Look.Icon(item.Icon, 36));
        head.AddChild(frame);
        var words = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        words.AddThemeConstantOverride("separation", 2);
        var first = new HBoxContainer();
        first.AddThemeConstantOverride("separation", 6);
        Label name = Look.Text(Market.Name(item), "fs16_bold_shop_quantity", 16);
        name.ClipText = true;
        name.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        first.AddChild(name);
        Label tag = Look.Text("", "fs12_bold", 12);
        first.AddChild(tag);
        words.AddChild(first);
        var cost = new HBoxContainer();
        cost.AddThemeConstantOverride("separation", 6);
        cost.AddChild(Look.Icon(_game == null ? null : Market.GoldIcon(_game), 20));
        Label price = Look.Body("");
        cost.AddChild(price);
        words.AddChild(cost);
        head.AddChild(words);
        lines.AddChild(head);

        // how many there are, how many the player has
        var counts = new HBoxContainer();
        counts.AddThemeConstantOverride("separation", 6);
        counts.AddChild(Look.Caption(leftCaption));
        Label left = Look.Value("");
        left.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        counts.AddChild(left);
        counts.AddChild(Look.Caption("You have"));
        Label have = Look.Value("");
        counts.AddChild(have);
        lines.AddChild(counts);

        var buttons = new Button[Amounts.Length];
        var group = new HBoxContainer();
        group.AddThemeConstantOverride("separation", 4);
        for (int i = 0; i < Amounts.Length; i++)
        {
            int count = Amounts[i].Count;
            Button button = Look.Push(Amounts[i].Text);
            button.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            button.Pressed += () => pressed(count);
            buttons[i] = button;
            group.AddChild(button);
        }
        lines.AddChild(group);
        list.AddChild(card);
        return new Row { Name = name, Tag = tag, Price = price, Left = left, Have = have, Buttons = buttons, Card = card };
    }

    /// <summary>
    /// Fills a grid's last line up with empty places, so three cards are as wide as the cards of a full line
    /// above them and do not stretch.
    /// </summary>
    static void Pad(GridContainer grid)
    {
        foreach (Node child in grid.GetChildren())
            if (child.Name.ToString().StartsWith("Empty")) { grid.RemoveChild(child); child.QueueFree(); }
        int cards = grid.GetChildCount(), columns = Math.Max(1, grid.Columns);
        if (cards == 0 || grid.GetChild(0) is not PanelContainer) return;      // (nothing, or the line saying there is nothing)
        for (int i = cards % columns; i > 0 && i < columns; i++)
            grid.AddChild(new Control { Name = "Empty" + i, CustomMinimumSize = new Vector2(CardWidth, 0), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore });
    }

    static void Empty(GridContainer list, List<Row> rows, string text)
    {
        foreach (Node child in list.GetChildren()) { list.RemoveChild(child); child.QueueFree(); }
        rows.Clear();
        if (text == "") return;
        Label none = Look.Value(text);
        none.CustomMinimumSize = new Vector2(0, 40);
        list.AddChild(none);
    }

    // ---------------------------------------------------------------- the order list

    /// <summary>
    /// The order list is long: left to itself it can run off the bottom of the screen. So it gets the room there
    /// is under its button (or above it, when that is more) and scrolls inside.
    /// </summary>
    void PlaceOrderList()
    {
        OptionButton order = _order;
        PopupMenu list = order.GetPopup();
        Rect2 button = order.GetGlobalRect();
        Rect2 screen = order.GetViewportRect();
        int below = (int)(screen.End.Y - button.End.Y) - 8, above = (int)(button.Position.Y - screen.Position.Y) - 8;
        bool under = below >= 220 || below >= above;
        int room = Math.Max(80, Math.Min(420, under ? below : above));
        list.MaxSize = new Vector2I(360, room);
        Callable.From(() =>
        {
            if (!GodotObject.IsInstanceValid(list) || !list.Visible) return;
            int height = Math.Min(list.Size.Y, room);
            list.Size = new Vector2I(list.Size.X, height);
            list.Position = new Vector2I((int)button.Position.X, under ? (int)button.End.Y : (int)button.Position.Y - height);
        }).CallDeferred();
    }

    /// <summary>Fills the order list with what the character is able to gather by now.</summary>
    void FillOrder(Game game)
    {
        List<Item> now = Market.Orderable(game);
        if (_order.ItemCount == now.Count + 1 && now.SequenceEqual(_orderable)) return;
        _orderable = now;
        _order.Clear();
        _order.AddItem("Nothing ordered");
        PopupMenu list = _order.GetPopup();
        foreach (Item item in now)
        {
            _order.AddIconItem(item.Icon, Market.Name(item));
            list.SetItemIconMaxWidth(_order.ItemCount - 1, 20);
        }
        _order.Disabled = now.Count == 0;
    }

    // ---------------------------------------------------------------- showing the numbers

    /// <summary>The game took the page away (the player went elsewhere).</summary>
    public void Closed()
    {
        if (_root != null && GodotObject.IsInstanceValid(_root)) _root.QueueFree();
        _root = null;
    }

    void Say(string text)
    {
        _status.Text = text;
        Refresh();
    }

    /// <summary>Brings every number up to date; builds the cards anew when the merchant has new goods.</summary>
    public void Refresh()
    {
        if (_game == null || !Visible) return;
        Game game = _game;
        if (!ReferenceEquals(_shownOffers, Market.Offers))
        {
            if (_shownOffers != null) _status.Text = "He is back with new goods.";
            _shownOffers = Market.Offers;
            FillOrder(game);                                      // a new visit: something new may have been unlocked meanwhile
            Empty(_sale, _saleRows, _shownOffers.Count == 0 ? "He has nothing for you yet: gather something first (cut a tree, mine a rock), then he will know what to bring." : "");
            foreach (Offer offer in _shownOffers)
                _saleRows.Add(MakeRow(_sale, offer.Item, "In stock", count => Say(Market.Buy(game, offer, count))));
            Pad(_sale);
        }
        if (!ReferenceEquals(_shownWanted, Market.Wanted))
        {
            _shownWanted = Market.Wanted;
            Empty(_wanted, _wantedRows, _shownWanted.Count == 0 ? "He is not looking for anything this visit." : "");
            foreach (Want want in _shownWanted)
                _wantedRows.Add(MakeRow(_wanted, want.Item, "Still wants", count => Say(Market.Sell(game, want, count))));
            Pad(_wanted);
        }

        long gold = Market.GoldOwned(game);
        _gold.Text = $"You have {Market.Number(gold)} gold";
        TimeSpan until = Market.UntilNext;
        _next.Text = "New goods in " + Market.In(until);

        Item? ordered = Market.Order;
        int at = ordered == null ? 0 : _orderable.IndexOf(ordered) + 1;
        if (_order.ItemCount > 0 && _order.Selected != at) _order.Select(at);
        _orderNote.Text = ordered == null ? "Choose a raw material and he always has it with him, from the next visit on."
            : _shownOffers!.Any(o => o.Ordered && o.Item == ordered) ? $"He brings {Market.Name(ordered)} on every visit."
            : $"He brings {Market.Name(ordered)} on every visit, from the next one on (in {Market.In(until)}).";

        for (int i = 0; i < _saleRows.Count; i++)
        {
            Offer offer = _shownOffers![i];
            Row row = _saleRows[i];
            int affordable = Market.Affordable(offer, gold);
            row.Tag.Text = offer.Ordered ? "ORDERED" : offer.Deal ? "BARGAIN" : offer.Rare ? "RARE" : "";
            row.Tag.Modulate = offer.Deal ? Look.Good : Look.Gold;
            row.Price.Text = offer.Left > 0 ? Market.Number(offer.Price) : "-";
            row.Price.Modulate = offer.Left > 0 && affordable == 0 ? Look.Bad : Colors.White;
            row.Left.Text = offer.Left > 0 ? $"{Market.Number(offer.Left)} of {Market.Number(offer.Stock)}" : "sold out";
            row.Left.Modulate = offer.Left > 0 ? Colors.White : Look.Grey;
            row.Have.Text = Market.Number(Market.Owned(game, offer.Item));
            for (int b = 0; b < Amounts.Length; b++)
            {
                int count = Math.Min(Math.Min(Amounts[b].Count, offer.Left), affordable);
                bool whole = Amounts[b].Count == int.MaxValue || count == Amounts[b].Count;
                row.Buttons[b].Disabled = count <= 0 || !whole;
                row.Buttons[b].TooltipText = count <= 0
                    ? (offer.Left <= 0 ? "Sold out until the next visit." : "Not enough gold.")
                    : !whole ? (offer.Left < Amounts[b].Count ? "He does not have that many left." : "Not enough gold for that many.")
                    : $"Buy {Market.Number(count)} for {Market.Number(Market.Cost(offer, count))} gold";
            }
        }
        for (int i = 0; i < _wantedRows.Count; i++)
        {
            Want want = _shownWanted![i];
            Row row = _wantedRows[i];
            int have = Market.Owned(game, want.Item);
            row.Tag.Text = "";
            row.Tag.Visible = false;
            row.Price.Text = $"{Market.Number(want.Pays)}  (bank {Market.Number(Market.BankPrice(want.Item))})";
            row.Price.Modulate = Look.Good;
            row.Left.Text = want.Left > 0 ? Market.Number(want.Left) : "has enough";
            row.Left.Modulate = want.Left > 0 ? Colors.White : Look.Grey;
            row.Have.Text = Market.Number(have);
            for (int b = 0; b < Amounts.Length; b++)
            {
                int count = Math.Min(Math.Min(Amounts[b].Count, want.Left), have);
                bool whole = Amounts[b].Count == int.MaxValue || count == Amounts[b].Count;
                row.Buttons[b].Disabled = count <= 0 || !whole;
                row.Buttons[b].TooltipText = count <= 0
                    ? (want.Left <= 0 ? "He has enough of it until the next visit." : "You have none.")
                    : !whole ? (have < Amounts[b].Count ? "You do not have that many." : "He does not want that many.")
                    : $"Sell {Market.Number(count)} for {Market.Number(want.Pays * count)} gold";
            }
        }
    }

    // ---------------------------------------------------------------- for tests: the page's own controls

    public Control? Root => Visible ? _root : null;
    public string StatusText => _status.Text;
    public string GoldText => _gold.Text;
    public string NextText => _next.Text;
    public int SaleRows => _saleRows.Count;
    public int WantedRows => _wantedRows.Count;
    public OptionButton OrderBox => _order;
    public string OrderNote => _orderNote.Text;
    /// <summary>Picks an entry of the order list the way a click on it does. False when there is no such entry.</summary>
    public bool PickOrder(string name)
    {
        for (int i = 0; i < _order.ItemCount; i++)
        {
            if (_order.GetItemText(i) != name) continue;
            _order.Select(i);
            _order.EmitSignal(OptionButton.SignalName.ItemSelected, (long)i);
            return true;
        }
        return false;
    }
    public Button BuyButton(int row, int which) => _saleRows[row].Buttons[which];
    public Button SellButton(int row, int which) => _wantedRows[row].Buttons[which];
    public string SaleText(int row) => Line(_saleRows[row]);
    public string WantedText(int row) => Line(_wantedRows[row]);
    static string Line(Row r) => $"{r.Name.Text}{(r.Tag.Visible && r.Tag.Text != "" ? " [" + r.Tag.Text + "]" : "")} | {r.Price.Text} | {r.Left.Text} | {r.Have.Text} | " +
                                 string.Join(" ", r.Buttons.Select(b => b.Disabled ? "(" + b.Text + ")" : b.Text));
}
