using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Godot;

namespace MelvorModLoader;

/// <summary>A key, with or without Ctrl / Shift / Alt. <see cref="Key.None"/> = no key.</summary>
public readonly record struct KeyChord(Key Key, bool Ctrl = false, bool Shift = false, bool Alt = false)
{
    public static readonly KeyChord None = new(Key.None);
    public bool IsNone => Key == Key.None;

    /// <summary>"F8", "Ctrl+M", or "" for no key.</summary>
    public override string ToString() => IsNone ? ""
        : (Ctrl ? "Ctrl+" : "") + (Shift ? "Shift+" : "") + (Alt ? "Alt+" : "") + OS.GetKeycodeString(Key);

    public static KeyChord Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Trim().Equals("None", StringComparison.OrdinalIgnoreCase)) return None;
        bool ctrl = false, shift = false, alt = false;
        Key key = Key.None;
        foreach (string part in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl": ctrl = true; break;
                case "shift": shift = true; break;
                case "alt": alt = true; break;
                default: key = OS.FindKeycodeFromString(part); break;
            }
        }
        return key == Key.None ? None : new KeyChord(key, ctrl, shift, alt);
    }
}

/// <summary>
/// A mod window known to the loader: it is listed in the loader's Mods menu (top-bar "Mods" button, F1) and opened
/// by its key, which the player can change or switch off there. Made with <see cref="ModContext.AddWindow"/>.
/// </summary>
public sealed class ModWindow
{
    internal ModWindow(string id, string mod, string title, Action toggle, KeyChord defaultKey)
    {
        Id = id; Mod = mod; Title = title; Toggle = toggle; DefaultKey = defaultKey; Key = defaultKey;
    }

    /// <summary>"ModName:Title", the name it is saved under in mods\hotkeys.json.</summary>
    public string Id { get; }
    public string Mod { get; }
    public string Title { get; }
    public KeyChord DefaultKey { get; }
    public KeyChord Key { get; internal set; }
    internal Action Toggle { get; }
    internal bool WasDown;

    /// <summary>The key as text ("F8", "Ctrl+M"), or "" when the player switched it off. Show it in your window's
    /// title or Close button, and listen to <see cref="Changed"/> to update it.</summary>
    public string KeyLabel => Key.ToString();

    /// <summary>Raised (on the main thread) after the player changed this window's key.</summary>
    public event Action? Changed;
    internal void RaiseChanged() { try { Changed?.Invoke(); } catch (Exception e) { Log.Write($"[{Mod}] key change handler: {e.Message}"); } }

    /// <summary>Opens or closes the window, as its key would.</summary>
    public void Press()
    {
        try { Toggle(); } catch (Exception e) { Log.Write($"[{Mod}] opening '{Title}' failed: {e}"); }
    }
}

/// <summary>
/// The shared keys of every mod window, the Mods menu (top-bar button + F1) and mods\hotkeys.json.
/// Keys are checked once per frame. Letter and number keys are ignored while a text box has the keyboard,
/// F keys always work.
/// </summary>
public static class Hotkeys
{
    public const string MenuId = "MelvorModLoader:Mods";
    static readonly List<ModWindow> s_windows = new();
    static Dictionary<string, string> s_saved = new();
    static ModWindow? s_capturing;
    static Key s_ignoreUntilUp = Key.None;
    static MenuView? s_menu;
    static Button? s_topButton;
    static int s_frame;
    static bool s_escapeWasDown;

    /// <summary>The menu's own entry (key F1 by default, can be changed like the others).</summary>
    public static ModWindow Menu { get; private set; } = null!;
    public static IReadOnlyList<ModWindow> Windows => s_windows;
    public static bool MenuVisible => s_menu is { Visible: true };
    /// <summary>The window whose new key is being waited for ("Press a key..."), or null.</summary>
    public static ModWindow? Capturing => s_capturing;

    static string FilePath => Path.Combine(Bootstrap.ModsDir, "hotkeys.json");

    internal static void Start()
    {
        try { if (File.Exists(FilePath)) s_saved = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath)) ?? new(); }
        catch (Exception e) { Log.Write("hotkeys.json could not be read, default keys are used: " + e.Message); s_saved = new(); }
        Menu = Add("MelvorModLoader", "Mods", ToggleMenu, new KeyChord(Key.F1));
    }

    internal static ModWindow Add(string mod, string title, Action toggle, KeyChord defaultKey)
    {
        string id = $"{mod}:{title}";
        ModWindow? old = s_windows.FirstOrDefault(w => w.Id == id);
        if (old != null) s_windows.Remove(old);
        var window = new ModWindow(id, mod, title, toggle, defaultKey);
        if (s_saved.TryGetValue(id, out string? saved)) window.Key = KeyChord.Parse(saved);
        s_windows.Add(window);
        foreach (ModWindow other in s_windows.Where(w => w != window && !w.Key.IsNone && w.Key == window.Key))
            Log.Write($"Key {window.KeyLabel} is used by both '{other.Title}' ({other.Mod}) and '{window.Title}' ({window.Mod}). Change one in the Mods menu (F1).");
        s_menu?.Rebuild();
        return window;
    }

    /// <summary>Gives a window a new key (<see cref="KeyChord.None"/> switches it off) and saves the choice.</summary>
    public static void SetKey(ModWindow window, KeyChord key)
    {
        window.Key = key;
        window.WasDown = true;   // the press that chose it must not also open it
        if (key == window.DefaultKey) s_saved.Remove(window.Id); else s_saved[window.Id] = key.IsNone ? "None" : key.ToString();
        Save();
        window.RaiseChanged();
        s_menu?.Rebuild();
    }

    /// <summary>Every key back to what its mod chose.</summary>
    public static void ResetAll()
    {
        s_capturing = null;
        foreach (ModWindow w in s_windows.Where(w => w.Key != w.DefaultKey).ToList())
        {
            w.Key = w.DefaultKey;
            w.RaiseChanged();
        }
        s_saved.Clear();
        Save();
        s_menu?.Rebuild();
    }

    /// <summary>The windows whose key is also used by another window in the list.</summary>
    public static IEnumerable<ModWindow> SharingKey(ModWindow window) =>
        window.Key.IsNone ? Enumerable.Empty<ModWindow>() : s_windows.Where(w => w != window && w.Key == window.Key);

    /// <summary>Starts waiting for a key for this window (the menu shows "Press a key..."). Escape cancels.</summary>
    public static void BeginCapture(ModWindow window)
    {
        s_capturing = window;
        s_menu?.Rebuild();
    }

    public static void CancelCapture()
    {
        s_capturing = null;
        s_ignoreUntilUp = Key.Escape;
        s_menu?.Rebuild();
    }

    static void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(s_saved, new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception e) { Log.Write("hotkeys.json could not be saved: " + e.Message); }
    }

    public static void ToggleMenu()
    {
        if (s_menu is { Visible: true }) { s_menu.Close(); return; }
        s_menu ??= new MenuView();
        s_menu.Open();
    }

    // ---- once per frame (from the loader, before the mods' own frame handlers)

    internal static void OnFrame(SceneTree tree)
    {
        if (++s_frame % 60 == 0) PlaceButton(tree);
        if (s_capturing != null) { Capture(); return; }
        if (s_ignoreUntilUp != Key.None)
        {
            if (Input.IsKeyPressed(s_ignoreUntilUp)) return;
            s_ignoreUntilUp = Key.None;
        }
        bool escape = Input.IsKeyPressed(Key.Escape);
        if (escape && !s_escapeWasDown && s_menu is { Visible: true }) s_menu.Close();
        s_escapeWasDown = escape;
        bool typing = tree.Root.GuiGetFocusOwner() is LineEdit or TextEdit;
        bool ctrl = Input.IsKeyPressed(Key.Ctrl), shift = Input.IsKeyPressed(Key.Shift), alt = Input.IsKeyPressed(Key.Alt);
        // a copy: opening a window may make a mod add one
        foreach (ModWindow w in s_windows.ToList())
        {
            if (w.Key.IsNone) { w.WasDown = false; continue; }
            bool down = Input.IsKeyPressed(w.Key.Key) && w.Key.Ctrl == ctrl && w.Key.Shift == shift && w.Key.Alt == alt;
            bool pressed = down && !w.WasDown;
            w.WasDown = down;
            if (!pressed) continue;
            if (typing && !IsFunctionKey(w.Key.Key) && !w.Key.Ctrl && !w.Key.Alt) continue;
            w.Press();
        }
    }

    static bool IsFunctionKey(Key key) => key >= Key.F1 && key <= Key.F35;

    /// <summary>The keys a window can be given (modifiers come on top).</summary>
    static readonly Key[] s_choosable = BuildChoosable();
    static Key[] BuildChoosable()
    {
        var keys = new List<Key>();
        for (Key k = Key.F1; k <= Key.F12; k++) keys.Add(k);
        for (Key k = Key.A; k <= Key.Z; k++) keys.Add(k);
        for (Key k = Key.Key0; k <= Key.Key9; k++) keys.Add(k);
        for (Key k = Key.Kp0; k <= Key.Kp9; k++) keys.Add(k);
        keys.AddRange(new[] { Key.Insert, Key.Delete, Key.Home, Key.End, Key.Pageup, Key.Pagedown, Key.Pause, Key.Scrolllock,
            Key.Quoteleft, Key.Minus, Key.Equal, Key.Bracketleft, Key.Bracketright, Key.Backslash, Key.Semicolon,
            Key.Apostrophe, Key.Comma, Key.Period, Key.Slash, Key.KpAdd, Key.KpSubtract, Key.KpMultiply, Key.KpDivide });
        return keys.ToArray();
    }

    static void Capture()
    {
        if (s_ignoreUntilUp != Key.None)
        {
            if (Input.IsKeyPressed(s_ignoreUntilUp)) return;
            s_ignoreUntilUp = Key.None;
        }
        if (Input.IsKeyPressed(Key.Escape)) { CancelCapture(); return; }
        foreach (Key k in s_choosable)
        {
            if (!Input.IsKeyPressed(k)) continue;
            ModWindow window = s_capturing!;
            s_capturing = null;
            s_ignoreUntilUp = k;
            SetKey(window, new KeyChord(k, Input.IsKeyPressed(Key.Ctrl), Input.IsKeyPressed(Key.Shift), Input.IsKeyPressed(Key.Alt)));
            return;
        }
    }

    // ---- the "Mods" button in the game's top bar (only while a character is open)

    static void PlaceButton(SceneTree tree)
    {
        try
        {
            if (s_topButton != null && GodotObject.IsInstanceValid(s_topButton) && s_topButton.IsInsideTree()) { UpdateButton(); return; }
            s_topButton = null;
            // Next to the game's Guide button; otherwise at the end of the row that holds the Bank button.
            Control? guide = tree.Root.FindChild("GuideNavigationButton", true, false) as Control;
            if (guide?.GetParent() is not BoxContainer)
                guide = tree.Root.FindChild("BankNavigationButton", true, false) is Control bank && bank.GetParent() is BoxContainer bankRow
                    ? bankRow.GetChildren().OfType<Control>().LastOrDefault(c => c.Visible) : null;
            if (guide == null || guide.GetParent() is not BoxContainer row) return;
            if (row.FindChild("ModsNavigationButton", false, false) is Button old) { row.RemoveChild(old); old.QueueFree(); }
            LabelSettings? letters = (guide.FindChild("Title", true, false) as Label)?.LabelSettings;
            var button = new Button
            {
                Name = "ModsNavigationButton", Flat = true, FocusMode = Control.FocusModeEnum.None, CustomMinimumSize = new Vector2(56, 36),
                SizeFlagsVertical = guide.SizeFlagsVertical, MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            };
            var hover = new StyleBoxFlat { BgColor = new Color(1, 1, 1, 0.07f) };
            hover.SetCornerRadiusAll(4);
            button.AddThemeStyleboxOverride("normal", new StyleBoxEmpty());
            button.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
            button.AddThemeStyleboxOverride("hover", hover);
            button.AddThemeStyleboxOverride("pressed", hover);
            var column = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore, Alignment = BoxContainer.AlignmentMode.Center };
            column.AddThemeConstantOverride("separation", 0);
            column.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            // four little tiles: "many mods" (no picture for it in the game)
            var icon = new Control { CustomMinimumSize = new Vector2(24, 24), SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter, MouseFilter = Control.MouseFilterEnum.Ignore };
            icon.Draw += () =>
            {
                var gold = new Color(0.93f, 0.75f, 0.35f);
                var dark = new Color(0.15f, 0.13f, 0.11f);
                foreach (var (x, y) in new[] { (3, 3), (13, 3), (3, 13), (13, 13) })
                {
                    icon.DrawRect(new Rect2(x, y, 8, 8), dark);
                    icon.DrawRect(new Rect2(x, y, 8, 8), gold, false, 1.5f);
                }
            };
            column.AddChild(icon);
            var title = new Label { Text = "Mods", HorizontalAlignment = HorizontalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Ignore };
            if (letters != null) title.LabelSettings = letters;
            column.AddChild(title);
            button.AddChild(column);
            button.Pressed += ToggleMenu;
            row.AddChild(button);
            row.MoveChild(button, guide.GetIndex() + 1);
            s_topButton = button;
            UpdateButton();
        }
        catch { }
    }

    static void UpdateButton()
    {
        if (s_topButton == null) return;
        string key = Menu.KeyLabel;
        bool clash = s_windows.Any(w => SharingKey(w).Any());
        s_topButton.TooltipText = "Mods" + (key == "" ? "" : $"  ({key})") + "\nOpen any mod window, or change or switch off its key."
            + (clash ? "\nTwo mods use the same key: open this to fix it." : "");
        s_topButton.Modulate = clash ? new Color(1f, 0.8f, 0.5f) : Colors.White;
    }

    // ---- the menu window

    sealed class MenuView
    {
        static readonly Color Back = new(0.075f, 0.082f, 0.102f);
        static readonly Color Card = new(0.118f, 0.129f, 0.157f);
        static readonly Color Ctl = new(0.176f, 0.192f, 0.235f);
        static readonly Color CtlHover = new(0.235f, 0.255f, 0.310f);
        static readonly Color Line = new(0.255f, 0.275f, 0.330f);
        static readonly Color Text = new(0.905f, 0.915f, 0.935f);
        static readonly Color Muted = new(0.560f, 0.590f, 0.650f);
        static readonly Color Accent = new(0.925f, 0.745f, 0.345f);
        static readonly Color AccentDim = new(0.360f, 0.290f, 0.135f);
        static readonly Color Warn = new(0.960f, 0.600f, 0.300f);

        readonly CanvasLayer _layer;
        readonly PanelContainer _panel;
        readonly VBoxContainer _rows;
        readonly Button _close;
        public bool Visible => _layer.Visible;

        public MenuView()
        {
            var tree = (SceneTree)Engine.GetMainLoop();
            _layer = new CanvasLayer { Layer = 110, Name = "MelvorModLoaderMods", Visible = false };
            var dim = new ColorRect { Color = new Color(0, 0, 0, 0.45f), MouseFilter = Control.MouseFilterEnum.Stop };
            dim.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            dim.GuiInput += e => { if (e is InputEventMouseButton { Pressed: true }) Close(); };
            _layer.AddChild(dim);

            _panel = new PanelContainer { Theme = BuildTheme(), CustomMinimumSize = new Vector2(620, 0) };
            _panel.AddThemeStyleboxOverride("panel", Box(Back, 8, Line, 1, 18, 14));
            _layer.AddChild(_panel);

            var body = new VBoxContainer();
            body.AddThemeConstantOverride("separation", 10);
            _panel.AddChild(body);

            var head = new HBoxContainer();
            head.AddThemeConstantOverride("separation", 12);
            var title = new Label { Text = "MODS", VerticalAlignment = VerticalAlignment.Center };
            title.AddThemeColorOverride("font_color", Accent);
            title.AddThemeFontSizeOverride("font_size", 22);
            head.AddChild(title);
            var sub = new Label { Text = "Open a mod window, or change its key.", VerticalAlignment = VerticalAlignment.Center, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            sub.AddThemeColorOverride("font_color", Muted);
            head.AddChild(sub);
            _close = new Button { Text = "Close" };
            _close.Pressed += Close;
            head.AddChild(_close);
            body.AddChild(head);

            var card = new PanelContainer();
            card.AddThemeStyleboxOverride("panel", Box(Card, 6, Card, 0, 12, 10));
            var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(0, 120), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
            _rows = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _rows.AddThemeConstantOverride("separation", 6);
            scroll.AddChild(_rows);
            card.AddChild(scroll);
            body.AddChild(card);

            var foot = new HBoxContainer();
            var note = new Label
            {
                Text = "Click a key to change it, then press the new key (Ctrl, Shift or Alt can come with it; Esc cancels).\n"
                       + "\"Off\" frees the key, the window still opens from here. Saved in mods\\hotkeys.json.",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            };
            note.AddThemeColorOverride("font_color", Muted);
            note.AddThemeFontSizeOverride("font_size", 12);
            foot.AddChild(note);
            var reset = new Button { Text = "Reset all keys", TooltipText = "Every key back to what its mod uses by default." };
            reset.Pressed += ResetAll;
            foot.AddChild(reset);
            body.AddChild(foot);

            tree.Root.AddChild(_layer);
        }

        public void Open()
        {
            Rebuild();
            _layer.Visible = true;
            Place();
        }

        public void Close()
        {
            s_capturing = null;
            _layer.Visible = false;
        }

        void Place()
        {
            Vector2 screen = _layer.GetViewport().GetVisibleRect().Size;
            int rows = s_windows.Count;
            float list = Math.Min(screen.Y - 220, rows * 37 + 4);
            if (_rows.GetParent() is ScrollContainer scroll) scroll.CustomMinimumSize = new Vector2(0, Math.Max(120, list));
            _panel.CustomMinimumSize = new Vector2(Math.Min(700, screen.X - 32), 0);
            _panel.ResetSize();   // as tall as its content needs
            _panel.Position = ((screen - _panel.Size) / 2).Round();
        }

        public void Rebuild()
        {
            foreach (Node child in _rows.GetChildren()) { _rows.RemoveChild(child); child.QueueFree(); }
            _close.Text = Menu.KeyLabel == "" ? "Close" : $"Close  ({Menu.KeyLabel})";
            var ordered = s_windows.Where(w => w != Menu)
                .OrderBy(w => w.DefaultKey.IsNone ? 1 : 0).ThenBy(w => w.DefaultKey.Key).ThenBy(w => w.Title, StringComparer.OrdinalIgnoreCase)
                .Append(Menu);
            foreach (ModWindow w in ordered) _rows.AddChild(Row(w));
            if (s_windows.Count == 1)
            {
                var none = new Label { Text = "No mod has a window here yet." };
                none.AddThemeColorOverride("font_color", Muted);
                _rows.AddChild(none);
            }
            UpdateButton();
        }

        Control Row(ModWindow w)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 8);
            bool isMenu = w == Menu;
            var open = new Button
            {
                Text = isMenu ? "This menu" : w.Title, Alignment = HorizontalAlignment.Left,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, Disabled = isMenu,
                TooltipText = isMenu ? "The key that opens this menu." : $"Open {w.Title} ({w.Mod})",
            };
            if (!isMenu) open.Pressed += () => { Close(); w.Press(); };
            row.AddChild(open);

            var clash = SharingKey(w).ToList();
            if (clash.Count > 0)
            {
                var warn = new Label { Text = "also used by " + string.Join(", ", clash.Select(c => c == Menu ? "this menu" : c.Title)), VerticalAlignment = VerticalAlignment.Center };
                warn.AddThemeColorOverride("font_color", Warn);
                warn.AddThemeFontSizeOverride("font_size", 12);
                row.AddChild(warn);
            }

            bool waiting = s_capturing == w;
            var key = new Button
            {
                Text = waiting ? "Press a key..." : w.KeyLabel == "" ? "no key" : w.KeyLabel,
                CustomMinimumSize = new Vector2(130, 0),
                TooltipText = waiting ? "Press the new key now. Esc cancels." : "Click, then press the new key." + (w.DefaultKey.IsNone ? "" : $" Default: {w.DefaultKey}."),
            };
            if (clash.Count > 0 && !waiting) key.AddThemeColorOverride("font_color", Warn);
            key.Pressed += () => { if (s_capturing == w) CancelCapture(); else BeginCapture(w); };
            row.AddChild(key);

            var off = new Button { Text = "Off", Disabled = w.Key.IsNone, TooltipText = "No key for this window." };
            off.Pressed += () => { s_capturing = null; SetKey(w, KeyChord.None); };
            row.AddChild(off);
            return row;
        }

        static StyleBoxFlat Box(Color bg, int radius, Color? border = null, int width = 0, int padX = 0, int padY = 0)
        {
            var b = new StyleBoxFlat { BgColor = bg };
            b.SetCornerRadiusAll(radius);
            if (border is { } c && width > 0) { b.BorderColor = c; b.SetBorderWidthAll(width); }
            b.ContentMarginLeft = b.ContentMarginRight = padX;
            b.ContentMarginTop = b.ContentMarginBottom = padY;
            return b;
        }

        static Theme BuildTheme()
        {
            var t = new Theme { DefaultFontSize = 15 };
            t.SetStylebox("normal", "Button", Box(Ctl, 5, Line, 1, 12, 5));
            t.SetStylebox("hover", "Button", Box(CtlHover, 5, Accent, 1, 12, 5));
            t.SetStylebox("pressed", "Button", Box(AccentDim, 5, Accent, 1, 12, 5));
            t.SetStylebox("hover_pressed", "Button", Box(AccentDim, 5, Accent, 1, 12, 5));
            t.SetStylebox("disabled", "Button", Box(Card, 5, Card, 1, 12, 5));
            t.SetStylebox("focus", "Button", new StyleBoxEmpty());
            t.SetColor("font_color", "Button", Text);
            t.SetColor("font_hover_color", "Button", Colors.White);
            t.SetColor("font_pressed_color", "Button", Colors.White);
            t.SetColor("font_hover_pressed_color", "Button", Colors.White);
            t.SetColor("font_focus_color", "Button", Text);
            t.SetColor("font_disabled_color", "Button", Muted);
            t.SetColor("font_color", "Label", Text);
            t.SetStylebox("panel", "TooltipPanel", Box(Card, 5, Line, 1, 8, 5));
            t.SetColor("font_color", "TooltipLabel", Text);
            var rail = Box(new Color(0, 0, 0, 0), 4);
            t.SetStylebox("scroll", "VScrollBar", rail);
            t.SetStylebox("grabber", "VScrollBar", Box(Line, 4));
            t.SetStylebox("grabber_highlight", "VScrollBar", Box(Muted, 4));
            t.SetStylebox("grabber_pressed", "VScrollBar", Box(Accent, 4));
            return t;
        }
    }
}
