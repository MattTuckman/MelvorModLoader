using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Godot;

namespace CheatMenu;

/// <summary>One language of the window: its texts, its skill names, the fonts it needs, its writing direction.</summary>
public sealed record Language(
    string Code, string Name, Dictionary<string, string> Text, Dictionary<string, string> Skills,
    string[] Fonts, bool RightToLeft = false);

/// <summary>
/// The language of the Cheat Menu window. The English text is the key: <see cref="T"/> looks it up in the table of
/// the chosen language and falls back to English for anything not translated. Text with numbers or names in it is a
/// whole format string ("{0} of {1} items shown"), so each language can put the words in its own order.
///
/// The choice ("auto" or a language code) is kept for every character in language.txt next to the mod. "auto"
/// follows the game's own language when the window has it, English otherwise.
/// </summary>
public static class Lang
{
    public const string Auto = "auto", English = "en", ChineseTraditional = "zh-TW", ChineseSimplified = "zh-CN",
        Spanish = "es", French = "fr", Arabic = "ar", Hindi = "hi";

    /// <summary>Every language the window has, in the order of the list, with its name in its own language.</summary>
    public static readonly Language[] Languages =
    {
        new(English, "English", new(), new(), Array.Empty<string>()),
        new(ChineseSimplified, "简体中文", ZhCn.Text, ZhCn.Skills,
            new[] { "Microsoft YaHei UI", "Microsoft YaHei", "PingFang SC", "Noto Sans CJK SC", "Noto Sans SC" }),
        new(ChineseTraditional, "繁體中文", ZhTw.Text, ZhTw.Skills,
            new[] { "Microsoft JhengHei UI", "Microsoft JhengHei", "PingFang TC", "Noto Sans CJK TC", "Noto Sans TC" }),
        new(Spanish, "Español", Es.Text, Es.Skills, Array.Empty<string>()),
        new(French, "Français", Fr.Text, Fr.Skills, Array.Empty<string>()),
        new(Arabic, "العربية", Ar.Text, Ar.Skills,
            new[] { "Segoe UI", "Tahoma", "Arial", "Noto Sans Arabic", "Noto Naskh Arabic" }, RightToLeft: true),
        new(Hindi, "हिन्दी", Hi.Text, Hi.Skills, new[] { "Nirmala UI", "Mangal", "Noto Sans Devanagari" }),
    };

    /// <summary>The choices in the order of the list in the window: Auto first, then every language.</summary>
    public static readonly (string Code, string Name)[] Choices =
        new[] { (Auto, "Auto") }.Concat(Languages.Select(l => (l.Code, l.Name))).ToArray();

    public static string Dir = "";
    static string FilePath => Path.Combine(Dir, "language.txt");
    static string? s_choice;

    /// <summary>What the player chose ("auto" until something else is chosen).</summary>
    public static string Choice
    {
        get
        {
            if (s_choice != null) return s_choice;
            try { s_choice = File.Exists(FilePath) ? File.ReadAllText(FilePath).Trim() : Auto; }
            catch { s_choice = Auto; }
            if (Array.FindIndex(Choices, c => c.Code == s_choice) < 0) s_choice = Auto;
            return s_choice;
        }
    }

    /// <summary>The language the window is shown in right now (a code from <see cref="Languages"/>).</summary>
    public static string Active => Choice != Auto ? Choice : FromGame(GameLocale());

    /// <summary>The window's language for a game locale: the same language when the window has it, else English.</summary>
    public static string FromGame(string locale)
    {
        string l = locale.Replace('-', '_').ToLowerInvariant();
        if (l.StartsWith("zh"))
            return l.Contains("tw") || l.Contains("hk") || l.Contains("mo") || l.Contains("hant") ? ChineseTraditional : ChineseSimplified;
        foreach (Language language in Languages)
            if (l == language.Code || l.StartsWith(language.Code + "_")) return language.Code;
        return English;
    }

    static string GameLocale()
    {
        try { return TranslationServer.GetLocale(); }
        catch { return "en"; }
    }

    public static Language Current => Languages.FirstOrDefault(l => l.Code == Active) ?? Languages[0];

    public static void Set(string code)
    {
        if (Array.FindIndex(Choices, c => c.Code == code) < 0) code = Auto;
        s_choice = code;
        try { File.WriteAllText(FilePath, code); } catch { }
    }

    /// <summary>The text in the active language (the English text when it has no translation).</summary>
    public static string T(string english) =>
        Current.Text.TryGetValue(english, out string? text) ? text : english;

    /// <summary>A format string in the active language, filled in ("{0} of {1} items shown").</summary>
    public static string F(string englishFormat, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, T(englishFormat), args);

    /// <summary>A skill's name ("Woodcutting") in the active language.</summary>
    public static string Skill(string english) =>
        Current.Skills.TryGetValue(english, out string? text) ? text : english;

    /// <summary>The name of a setting in the active language.</summary>
    public static string Name(Feature f) =>
        f.Skill == null ? T(f.En) : F(f.Column == "xp" ? "{0} XP" : "{0} speed", Skill(f.Skill));

    /// <summary>True while the active language is written from right to left (Arabic).</summary>
    public static bool RightToLeft => Current.RightToLeft;

    /// <summary>
    /// The window's theme for the active language. Scripts the game's own font does not have (Chinese, Arabic,
    /// Devanagari) get the right system fonts first: left to itself, Windows may pick one that draws them wrongly
    /// (a mainland Chinese font for Traditional characters, for example). The game's font stays the first choice
    /// for everything else.
    /// </summary>
    public static Theme ThemeFor(Theme theme)
    {
        string[] names = Current.Fonts;
        if (names.Length == 0) return theme;
        try
        {
            Font? game = ThemeDB.GetProjectTheme()?.DefaultFont ?? ThemeDB.FallbackFont;
            var system = new SystemFont { FontNames = names };
            var font = new FontVariation { BaseFont = game, Fallbacks = new Godot.Collections.Array<Font> { system } };
            var copy = (Theme)theme.Duplicate();
            copy.DefaultFont = font;
            return copy;
        }
        catch { return theme; }
    }

    /// <summary>For tests: the English keys every translation must have (the Traditional Chinese table, which is complete).</summary>
    public static IEnumerable<string> Keys => ZhTw.Text.Keys;
}
