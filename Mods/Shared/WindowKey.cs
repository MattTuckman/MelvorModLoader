using System;
using System.Runtime.CompilerServices;
using Godot;
using MelvorModLoader;

// Linked into every mod with a window (internal, so the copies never clash).

/// <summary>
/// A mod window's key. With loader 0.2.0 or later the window is listed in the loader's Mods menu (top-bar button, F1)
/// and the player can change or switch off its key there; with an older loader the mod listens to its own fixed key,
/// as before. Use <see cref="Label"/> / <see cref="With"/> wherever the key is shown.
/// </summary>
internal sealed class WindowKey
{
    readonly Key _fixed;
    readonly Action _toggle;
    readonly object? _window;   // a MelvorModLoader.ModWindow; kept as object so this class still loads with an older loader
    bool _wasDown;

    /// <summary>Raised when the player gave the window another key (update the labels that show it).</summary>
    public event Action? Changed;

    /// <summary>True when the loader handles the key (Mods menu); false with an older loader.</summary>
    public bool Shared => _window != null;

    public WindowKey(ModContext ctx, string title, Action toggle, Key key)
    {
        _fixed = key;
        _toggle = toggle;
        try { _window = Register(ctx, title, toggle, key); }
        catch (MissingMethodException) { }
        catch (TypeLoadException) { }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    object Register(ModContext ctx, string title, Action toggle, Key key)
    {
        ModWindow window = ctx.AddWindow(title, toggle, key);
        window.Changed += () => Changed?.Invoke();
        return window;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static string LabelOf(object window) => ((ModWindow)window).KeyLabel;

    /// <summary>"F5", "Ctrl+M", or "" when the player switched the key off.</summary>
    public string Label => _window != null ? LabelOf(_window) : _fixed == Key.None ? "" : OS.GetKeycodeString(_fixed);

    /// <summary>"Close  (F5)", or just "Close" without a key.</summary>
    public string With(string text) => Label == "" ? text : $"{text}  ({Label})";

    /// <summary>"F5" for sentences such as "press F5"; "the Mods menu" when there is no key.</summary>
    public string Spoken => Label != "" ? Label : "the Mods menu";

    /// <summary>Call once per frame. Only does something with an older loader (then it listens to the fixed key).</summary>
    public void Poll()
    {
        if (_window != null || _fixed == Key.None) return;
        bool down = Input.IsKeyPressed(_fixed);
        if (down && !_wasDown) _toggle();
        _wasDown = down;
    }
}
