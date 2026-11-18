using Godot;

namespace DebugMenuMod;

static class MenuStyle
{
    /// <summary>
    /// The game's theme gives popup panels no background (the game never shows one),
    /// so the menu would be drawn as loose text over the game. Give it a solid panel.
    /// </summary>
    public static void Apply(Window menu)
    {
        var panel = new StyleBoxFlat
        {
            BgColor = new Color(0.10f, 0.11f, 0.13f),
            BorderColor = new Color(0.45f, 0.40f, 0.28f)
        };
        panel.SetBorderWidthAll(2);
        panel.SetCornerRadiusAll(6);
        panel.SetContentMarginAll(14);
        menu.AddThemeStyleboxOverride("panel", panel);
    }
}
