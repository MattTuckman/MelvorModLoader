using System;
using Godot;

namespace Adventurers;

/// <summary>
/// The game's own look, borrowed while the game runs: the panels, fonts and buttons its Shop and Bank pages are
/// built of, loaded from the installed game by their resource paths. Nothing of it is shipped with the mod. A
/// piece a later game version no longer has is replaced by a plain dark one, so the page still works.
/// </summary>
public static class Look
{
    const string Textures = "res://Assets/UI/Textures/";
    const string Letters = "res://Assets/Styles/LabelSettings/";

    public static readonly Color Cream = new(0.886f, 0.859f, 0.804f);
    public static readonly Color Grey = new(0.71f, 0.71f, 0.71f);
    public static readonly Color Good = new(0.2f, 1f, 0.2f);
    public static readonly Color Bad = new(1f, 0.35f, 0.25f);
    public static readonly Color Gold = new(1f, 0.82f, 0.43f);

    static T? Load<T>(string path) where T : Resource
    {
        try { return ResourceLoader.Exists(path) ? ResourceLoader.Load<T>(path) : null; }
        catch { return null; }
    }

    /// <summary>One of the game's panel pictures as a panel background (the names are the game's own files).</summary>
    public static StyleBox Panel(string texture, float padX, float padY, Color? modulate = null)
    {
        Texture2D? picture = Load<Texture2D>(Textures + texture);
        if (picture == null) return Flat(new Color(0.114f, 0.098f, 0.086f), new Color(0.32f, 0.286f, 0.263f), padX, padY);
        var box = new StyleBoxTexture { Texture = picture, ModulateColor = modulate ?? Colors.White };
        box.ContentMarginLeft = box.ContentMarginRight = padX;
        box.ContentMarginTop = box.ContentMarginBottom = padY;
        return box;
    }

    public static StyleBoxFlat Flat(Color background, Color border, float padX, float padY, int radius = 0)
    {
        var box = new StyleBoxFlat { BgColor = background, BorderColor = border };
        box.SetBorderWidthAll(1);
        box.SetCornerRadiusAll(radius);
        box.ContentMarginLeft = box.ContentMarginRight = padX;
        box.ContentMarginTop = box.ContentMarginBottom = padY;
        return box;
    }

    /// <summary>The page's big dark panel, the strip across its top, a card, the side panel and its paper tab.</summary>
    public static StyleBox Board => Panel("Combat/TEX_Combat_21.png", 16, 20, new Color(0.51f, 0.51f, 0.51f));
    public static StyleBox Strip(float padX = 16) => Panel("Crafting/TEX_Crafting_13.png", padX, 4);
    public static StyleBox Card => Panel("Crafting/TEX_Crafting_2.png", 14, 14);
    public static StyleBox Side => Panel("Crafting/TEX_Crafting_2.png", 12, 20);
    public static StyleBox Tab => Panel("Crafting/TEX_Crafting_0.png", 16, 8);
    /// <summary>The small framed square the game puts an item's picture in.</summary>
    public static StyleBox IconFrame => Flat(new Color(0.141f, 0.122f, 0.11f), new Color(0.306f, 0.271f, 0.251f), 4, 4);

    /// <summary>A label in one of the game's own letterings (a file name without ".tres"); the size is used when that one is gone.</summary>
    public static Label Text(string text, string lettering, int size = 14, Color? colour = null)
    {
        var label = new Label { Text = text, VerticalAlignment = VerticalAlignment.Center };
        LabelSettings? settings = Load<LabelSettings>(lettering.StartsWith("res://") ? lettering : Letters + lettering + ".tres");
        if (settings != null) label.LabelSettings = settings;
        else label.AddThemeFontSizeOverride("font_size", size);
        // (the game's letterings are shared by all its labels: a colour goes on the label, never into them)
        if (colour != null) label.Modulate = colour.Value;
        return label;
    }

    public static Label Body(string text, int size = 14) => Text(text, "fs" + size + "_bold", size);
    public static Label Caption(string text) => Text(text, "fs12_bold_grey", 12);
    public static Label Value(string text) => Text(text, "fs14_semibold_shop_quantity", 14);
    public static Label Heading(string text) => Text(text, "shop_subcategory", 18);
    public static Label OnPaper(string text) => Text(text, "res://Assets/UI/artisan_header_fs14_bold.tres", 14);

    /// <summary>A button looking like the game's ("1", "10", "Max" in the Shop).</summary>
    public static Button Push(string text) => new()
    {
        Text = text, ThemeTypeVariation = "TexturedButton8x", FocusMode = Control.FocusModeEnum.None,
        MouseDefaultCursorShape = Control.CursorShape.PointingHand,
    };

    static readonly Color Wood = new(0.114f, 0.098f, 0.086f);
    static readonly Color WoodLight = new(0.176f, 0.153f, 0.137f);
    static readonly Color Edge = new(0.32f, 0.286f, 0.263f);
    static readonly Color GoldDim = new(0.36f, 0.29f, 0.135f);

    static Theme? s_pickTheme;

    /// <summary>
    /// The look of the searchable lists' pop-ups, in the page's browns (the game's theme draws a pop-up panel
    /// see-through and has no list styles of its own).
    /// </summary>
    public static Theme PickTheme => s_pickTheme ??= BuildPickTheme();

    static Theme BuildPickTheme()
    {
        var t = new Theme();
        t.SetStylebox("panel", "PopupPanel", Flat(Wood, Edge, 8, 8, 6));
        t.SetStylebox("normal", "LineEdit", Flat(new Color(0.07f, 0.06f, 0.053f), Edge, 8, 5, 4));
        t.SetStylebox("focus", "LineEdit", Flat(new Color(0.07f, 0.06f, 0.053f), Gold, 8, 5, 4));
        t.SetColor("font_color", "LineEdit", Cream);
        t.SetColor("font_placeholder_color", "LineEdit", Grey);
        t.SetColor("caret_color", "LineEdit", Gold);
        t.SetColor("selection_color", "LineEdit", GoldDim);
        t.SetStylebox("panel", "Tree", Flat(new Color(0.07f, 0.06f, 0.053f), Edge, 4, 4, 4));
        t.SetStylebox("focus", "Tree", new StyleBoxEmpty());
        t.SetStylebox("selected", "Tree", Flat(GoldDim, GoldDim, 0, 0, 3));
        t.SetStylebox("selected_focus", "Tree", Flat(GoldDim, GoldDim, 0, 0, 3));
        t.SetStylebox("hovered", "Tree", Flat(WoodLight, WoodLight, 0, 0, 3));
        t.SetStylebox("cursor", "Tree", new StyleBoxEmpty());
        t.SetStylebox("cursor_unfocused", "Tree", new StyleBoxEmpty());
        t.SetColor("font_color", "Tree", Cream);
        t.SetColor("font_selected_color", "Tree", Colors.White);
        t.SetConstant("v_separation", "Tree", 5);
        t.SetConstant("draw_guides", "Tree", 0);
        t.SetConstant("draw_relationship_lines", "Tree", 0);
        t.SetColor("font_color", "Label", Grey);
        foreach (string type in new[] { "VScrollBar", "HScrollBar" })
        {
            StyleBoxFlat rail = Flat(Wood, Wood, 4, 4, 4);
            rail.SetBorderWidthAll(0);
            t.SetStylebox("scroll", type, rail);
            t.SetStylebox("scroll_focus", type, rail);
            t.SetStylebox("grabber", type, Flat(Edge, Edge, 0, 0, 4));
            t.SetStylebox("grabber_highlight", type, Flat(Grey, Grey, 0, 0, 4));
            t.SetStylebox("grabber_pressed", type, Flat(Gold, Gold, 0, 0, 4));
        }
        return t;
    }

    /// <summary>An equipment tile's frames: dark, gold-edged under the mouse, green-edged when something is worn.</summary>
    public static void Tile(Button tile, bool filled)
    {
        Color edge = filled ? new Color(0.45f, 0.40f, 0.30f) : Edge;
        StyleBoxFlat normal = Flat(new Color(0.141f, 0.122f, 0.11f), edge, 6, 6, 4);
        normal.SetBorderWidthAll(2);
        StyleBoxFlat hover = Flat(WoodLight, Gold, 6, 6, 4);
        hover.SetBorderWidthAll(2);
        foreach (string s in new[] { "normal", "disabled", "focus" }) tile.AddThemeStyleboxOverride(s, s == "focus" ? new StyleBoxEmpty() : normal);
        foreach (string s in new[] { "hover", "pressed", "hover_pressed" }) tile.AddThemeStyleboxOverride(s, hover);
    }

    /// <summary>A list's button in the page's browns: dark field, gold edge under the mouse.</summary>
    public static void Field(Button b)
    {
        StyleBoxFlat normal = Flat(new Color(0.07f, 0.06f, 0.053f), Edge, 10, 5, 4);
        StyleBoxFlat hover = Flat(new Color(0.10f, 0.087f, 0.077f), Gold, 10, 5, 4);
        foreach (string s in new[] { "normal", "disabled" }) b.AddThemeStyleboxOverride(s, normal);
        foreach (string s in new[] { "hover", "pressed", "hover_pressed" }) b.AddThemeStyleboxOverride(s, hover);
        b.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        b.AddThemeColorOverride("font_hover_color", Colors.White);
        b.AddThemeColorOverride("font_pressed_color", Colors.White);
    }

    /// <summary>A small switch button of a row of choices (the stances): gold when it is the one chosen.</summary>
    public static Button Choice(string text, bool on)
    {
        var b = new Button { Text = text, ToggleMode = true, ButtonPressed = on, FocusMode = Control.FocusModeEnum.None, MouseDefaultCursorShape = Control.CursorShape.PointingHand };
        StyleBoxFlat off = Flat(new Color(0.141f, 0.122f, 0.11f), Edge, 10, 5, 4);
        StyleBoxFlat lit = Flat(GoldDim, Gold, 10, 5, 4);
        StyleBoxFlat hover = Flat(WoodLight, Gold, 10, 5, 4);
        b.AddThemeStyleboxOverride("normal", off);
        b.AddThemeStyleboxOverride("hover", hover);
        b.AddThemeStyleboxOverride("pressed", lit);
        b.AddThemeStyleboxOverride("hover_pressed", lit);
        b.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        b.AddThemeStyleboxOverride("disabled", off);
        b.AddThemeColorOverride("font_color", Cream);
        b.AddThemeColorOverride("font_pressed_color", Colors.White);
        b.AddThemeColorOverride("font_hover_pressed_color", Colors.White);
        b.AddThemeColorOverride("font_hover_color", Colors.White);
        return b;
    }

    public static TextureRect Icon(Texture2D? picture, float size) => new()
    {
        Texture = picture, CustomMinimumSize = new Vector2(size, size), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
        StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
        MouseFilter = Control.MouseFilterEnum.Ignore,
    };
}
