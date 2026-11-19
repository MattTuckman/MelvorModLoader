using System;
using Godot;

namespace Merchant;

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

    public static TextureRect Icon(Texture2D? picture, float size) => new()
    {
        Texture = picture, CustomMinimumSize = new Vector2(size, size), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
        StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
        MouseFilter = Control.MouseFilterEnum.Ignore,
    };
}
