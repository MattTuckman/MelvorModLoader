using Godot;

namespace CheatMenu;

/// <summary>
/// The menu's own look: one theme for the whole window, so it does not depend on the game's theme
/// (which has transparent pop-ups, no slider track and oversized check boxes).
/// </summary>
static class Style
{
    public static readonly Color Window = new(0.075f, 0.082f, 0.102f);
    public static readonly Color Sidebar = new(0.055f, 0.060f, 0.075f);
    public static readonly Color Card = new(0.118f, 0.129f, 0.157f);
    public static readonly Color Field = new(0.060f, 0.066f, 0.082f);
    public static readonly Color Control = new(0.176f, 0.192f, 0.235f);
    public static readonly Color ControlHover = new(0.235f, 0.255f, 0.310f);
    public static readonly Color Line = new(0.255f, 0.275f, 0.330f);
    public static readonly Color Text = new(0.905f, 0.915f, 0.935f);
    public static readonly Color Muted = new(0.560f, 0.590f, 0.650f);
    public static readonly Color Accent = new(0.925f, 0.745f, 0.345f);
    public static readonly Color AccentDim = new(0.360f, 0.290f, 0.135f);
    public static readonly Color Good = new(0.380f, 0.800f, 0.490f);
    public static readonly Color GoodDim = new(0.130f, 0.300f, 0.185f);
    public static readonly Color Bad = new(0.900f, 0.420f, 0.400f);

    public static StyleBoxFlat Box(Color bg, int radius = 5, Color? border = null, int borderWidth = 0, int padX = 0, int padY = 0)
    {
        var box = new StyleBoxFlat { BgColor = bg, BorderColor = border ?? Line };
        box.SetCornerRadiusAll(radius);
        box.SetBorderWidthAll(borderWidth);
        box.ContentMarginLeft = box.ContentMarginRight = padX;
        box.ContentMarginTop = box.ContentMarginBottom = padY;
        return box;
    }

    /// <summary>A row card; the active variant carries a coloured bar on its left edge.</summary>
    public static StyleBoxFlat Row(bool active)
    {
        StyleBoxFlat box = Box(Card, 5, active ? Accent : Card, 0, 12, 5);
        box.BorderWidthLeft = 3;
        return box;
    }

    static void Buttons(Theme t, string type)
    {
        t.SetStylebox("normal", type, Box(Control, 5, Line, 1, 12, 5));
        t.SetStylebox("hover", type, Box(ControlHover, 5, Accent, 1, 12, 5));
        t.SetStylebox("pressed", type, Box(AccentDim, 5, Accent, 1, 12, 5));
        t.SetStylebox("hover_pressed", type, Box(AccentDim, 5, Accent, 1, 12, 5));
        t.SetStylebox("disabled", type, Box(Card, 5, Card, 1, 12, 5));
        t.SetStylebox("focus", type, new StyleBoxEmpty());
        t.SetColor("font_color", type, Text);
        t.SetColor("font_hover_color", type, Colors.White);
        t.SetColor("font_pressed_color", type, Colors.White);
        t.SetColor("font_hover_pressed_color", type, Colors.White);
        t.SetColor("font_focus_color", type, Text);
        t.SetColor("font_disabled_color", type, new Color(Muted, 0.55f));
    }

    public static Theme Build()
    {
        var t = new Theme();

        Buttons(t, "Button");
        Buttons(t, "OptionButton");

        t.SetColor("font_color", "Label", Text);

        foreach (string type in new[] { "LineEdit" })
        {
            t.SetStylebox("normal", type, Box(Field, 4, Line, 1, 8, 5));
            t.SetStylebox("focus", type, Box(Field, 4, Accent, 1, 8, 5));
            t.SetStylebox("read_only", type, Box(Field, 4, Card, 1, 8, 5));
            t.SetColor("font_color", type, Text);
            t.SetColor("font_placeholder_color", type, Muted);
            t.SetColor("caret_color", type, Accent);
            t.SetColor("selection_color", type, AccentDim);
        }

        // Slider: a visible track with a filled part.
        StyleBoxFlat track = Box(Control, 3);
        track.ContentMarginTop = track.ContentMarginBottom = 3;
        StyleBoxFlat fill = Box(Accent, 3);
        fill.ContentMarginTop = fill.ContentMarginBottom = 3;
        t.SetStylebox("slider", "HSlider", track);
        t.SetStylebox("grabber_area", "HSlider", fill);
        t.SetStylebox("grabber_area_highlight", "HSlider", fill);

        // Drop-down lists.
        t.SetStylebox("panel", "PopupMenu", Box(Card, 5, Line, 1, 4, 4));
        t.SetStylebox("hover", "PopupMenu", Box(AccentDim, 3));
        t.SetColor("font_color", "PopupMenu", Text);
        t.SetColor("font_hover_color", "PopupMenu", Colors.White);

        // The loot table.
        t.SetStylebox("panel", "Tree", Box(Field, 5, Line, 1, 4, 4));
        t.SetStylebox("focus", "Tree", new StyleBoxEmpty());
        t.SetStylebox("selected", "Tree", Box(AccentDim, 3));
        t.SetStylebox("selected_focus", "Tree", Box(AccentDim, 3));
        t.SetStylebox("hovered", "Tree", Box(Control, 3));
        t.SetStylebox("cursor", "Tree", new StyleBoxEmpty());
        t.SetStylebox("cursor_unfocused", "Tree", new StyleBoxEmpty());
        foreach (string s in new[] { "title_button_normal", "title_button_hover", "title_button_pressed" })
            t.SetStylebox(s, "Tree", Box(Card, 3, Card, 0, 6, 4));
        t.SetColor("font_color", "Tree", Text);
        t.SetColor("font_selected_color", "Tree", Colors.White);
        t.SetColor("title_button_color", "Tree", Muted);
        t.SetConstant("v_separation", "Tree", 5);
        t.SetConstant("draw_guides", "Tree", 0);
        t.SetConstant("draw_relationship_lines", "Tree", 0);

        // Thin scroll bars.
        foreach (string type in new[] { "VScrollBar", "HScrollBar" })
        {
            StyleBoxFlat rail = Box(Field, 4);
            rail.ContentMarginLeft = rail.ContentMarginRight = rail.ContentMarginTop = rail.ContentMarginBottom = 4;
            t.SetStylebox("scroll", type, rail);
            t.SetStylebox("scroll_focus", type, rail);
            t.SetStylebox("grabber", type, Box(Line, 4));
            t.SetStylebox("grabber_highlight", type, Box(Muted, 4));
            t.SetStylebox("grabber_pressed", type, Box(Accent, 4));
        }

        t.SetStylebox("separator", "HSeparator", new StyleBoxLine { Color = Line, Thickness = 1 });
        t.SetStylebox("separator", "VSeparator", new StyleBoxLine { Color = Line, Thickness = 1, Vertical = true });
        return t;
    }

    public static Label Label(string text, Color? color = null, int size = 0)
    {
        var label = new Label { Text = text };
        if (color != null) label.AddThemeColorOverride("font_color", color.Value);
        if (size > 0) label.AddThemeFontSizeOverride("font_size", size);
        return label;
    }
}
