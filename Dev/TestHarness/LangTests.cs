using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CheatMenu;
using GamesByMalcs.Melvor2;
using Godot;
using MelvorModLoader;

namespace TestHarness;

/// <summary>
/// Cheat Menu in every language (-Only lang, with -Window for the pictures): each table has every text and the same
/// {0} {1} places as the English, the language list, every page in each language, the texts that are still English,
/// Arabic mirrored, and back to Auto at the end.
/// </summary>
static partial class Tests
{
    static IEnumerator<int> LangTests(Game game, ModContext ctx)
    {
        Out("=== Cheat Menu: diller ===");
        Out($"  oyunun yuklu dilleri: {string.Join(", ", TranslationServer.GetLoadedLocales())}; su anki: {TranslationServer.GetLocale()}; yazi motoru: {TextServerManager.GetPrimaryInterface()?.GetName()}");
        CheatMenuMod cheat = CheatMenuMod.Instance!;
        string before = Lang.Choice;

        // ---- 1. the tables: nothing missing, nothing misspelled, the same {n} places
        List<string> keys = Lang.Keys.ToList();
        static string Places(string s) => string.Join(",", Regex.Matches(s, @"\{\d+(:[^}]*)?\}").Select(m => m.Value).OrderBy(x => x));
        foreach (Language language in Lang.Languages.Where(l => l.Code != Lang.English))
        {
            var missing = keys.Where(k => !language.Text.ContainsKey(k)).ToList();
            var extra = language.Text.Keys.Where(k => !keys.Contains(k)).ToList();
            var badPlaces = language.Text.Where(p => Places(p.Key) != Places(p.Value)).Select(p => p.Key).ToList();
            var skillsMissing = Features.SkillNames.Where(s => !language.Skills.ContainsKey(s)).ToList();
            var formatErrors = new List<string>();
            foreach ((string key, string text) in language.Text)
                try { string.Format(text, 1, 2, 3, 4, 5); } catch { formatErrors.Add(key); }
            Check($"{language.Name}: tabloda her metin var, fazlasi yok, {{0}} yerleri ayni, beceri adlari tam",
                  missing.Count == 0 && extra.Count == 0 && badPlaces.Count == 0 && skillsMissing.Count == 0 && formatErrors.Count == 0,
                  $"{language.Text.Count}/{keys.Count} metin; eksik: {string.Join(" | ", missing.Take(5))}; fazla: {string.Join(" | ", extra.Take(5))}; " +
                  $"yer farki: {string.Join(" | ", badPlaces.Take(5))}; bicim hatasi: {string.Join(" | ", formatErrors.Take(5))}; beceri eksik: {string.Join(",", skillsMissing)}");
        }

        // ---- 2. Auto follows the game: English here, and the right language for each game locale
        if (cheat.Menu is { Visible: true }) cheat.ToggleMenu();
        cheat.SetLanguage(Lang.Auto);
        cheat.ToggleMenu();
        yield return 10;
        var autoMap = new[] { ("en_GB", Lang.English), ("zh_CN", Lang.ChineseSimplified), ("zh_TW", Lang.ChineseTraditional), ("zh_HK", Lang.ChineseTraditional),
                              ("es_MX", Lang.Spanish), ("fr", Lang.French), ("ar_EG", Lang.Arabic), ("hi_IN", Lang.Hindi), ("tr_TR", Lang.English) };
        var wrong = autoMap.Where(p => Lang.FromGame(p.Item1) != p.Item2).Select(p => $"{p.Item1}->{Lang.FromGame(p.Item1)}").ToList();
        Check("'Auto' oyun Ingilizceyken Ingilizce; her oyun dili dogru dile gidiyor", Lang.Active == Lang.English && FindTexts(cheat.Menu!.Panel).Contains("CHEAT MENU") && wrong.Count == 0,
              $"etkin: {Lang.Active}; yanlis eslesme: {string.Join(", ", wrong)}");

        // The list reads as a language choice: the word "Language" in front, and "Auto" says what it gives now.
        List<string> autoTexts = FindTexts(cheat.Menu!.Panel);
        Check("dil listesinin onunde 'Language' yazisi, Auto ne verdigini soyluyor", autoTexts.Contains("Language") && autoTexts.Contains("Auto (\u2068English\u2069)"),
              string.Join(" | ", autoTexts.Take(8)));
        string dir = Path.Combine(Path.GetDirectoryName(System.Environment.GetEnvironmentVariable("MML_TEST_OUT")!)!, "shots");
        bool window = DisplayServer.GetName() != "headless";
        if (window)
        {
            Directory.CreateDirectory(dir);
            yield return 5;
            Out($"  goruntu cm-auto-0.png: {ctx.Tree.Root.GetTexture().GetImage().SavePng(Path.Combine(dir, "cm-auto-0.png"))}");
        }

        // ---- 3. every language in the window
        foreach (Language language in Lang.Languages.Where(l => l.Code != Lang.English))
        {
            cheat.Menu!.ShowPage(3);
            cheat.SetLanguage(language.Code);
            yield return 10;
            Menu menu = cheat.Menu!;
            bool reopened = menu.Visible && menu.CurrentPage == 3 && menu.BuiltIn == language.Code;
            string file = File.ReadAllText(Path.Combine(Lang.Dir, "language.txt")).Trim();
            Font? font = menu.Panel.Theme?.DefaultFont;
            string fonts = font == null ? "" : string.Join(",", font.Fallbacks.OfType<SystemFont>().SelectMany(f => f.FontNames));
            bool fontsOk = language.Fonts.Length == 0 ? true : fonts.StartsWith(language.Fonts[0]);
            bool mirrored = menu.Panel.LayoutDirection == Control.LayoutDirectionEnum.Rtl;
            // Left in English = shown exactly as the English original although the table translates it differently.
            var originals = new HashSet<string>();
            foreach (string k in keys.Concat(Features.SkillNames))
            {
                string mine = language.Text.TryGetValue(k, out string? v) ? v : language.Skills.TryGetValue(k, out string? s) ? s : k;
                if (mine == k) continue;
                originals.Add(k);
                originals.Add(k.ToUpperInvariant());
            }
            var english = new SortedSet<string>();
            for (int i = 0; i < menu.PageCount; i++)
            {
                menu.ShowPage(i);
                yield return 4;
                foreach (string t in FindTexts(menu.Panel))
                    if (originals.Contains(t.Trim())) english.Add(t);
                if (window && i is 0 or 3 or 6 or 8)
                {
                    yield return 8;
                    Out($"  goruntu cm-{language.Code}-{i}.png: {ctx.Tree.Root.GetTexture().GetImage().SavePng(Path.Combine(dir, $"cm-{language.Code}-{i}.png"))}");
                }
            }
            // The game's own skill names in the level list stay in the game's language.
            var left = english.Where(t => !Features.SkillNames.Contains(t)).ToList();
            Check($"{language.Name}: pencere bu dilde kuruluyor, ayni sayfada, secim kayitli, yazi tipleri {(language.RightToLeft ? "ve sagdan sola " : "")}dogru, Ingilizce kalan yok",
                  reopened && file == language.Code && fontsOk && mirrored == language.RightToLeft && left.Count == 0,
                  $"sayfa/dil: {reopened}; dosya: '{file}'; yazi tipi: {(fonts == "" ? "oyunun" : fonts.Split(',')[0])}; aynali: {mirrored}; " +
                  $"Ingilizce kalan: {string.Join(" | ", left.Take(8))}");
        }

        int perSkill = Enumerable.Range(0, cheat.Menu!.PageCount).First(i => cheat.Menu.PageTitle(i) == "Per skill");
        Check("sayfa adlari testler icin Ingilizce anahtar olarak kaliyor", cheat.Menu.PageTitle(perSkill) == "Per skill", cheat.Menu.PageTitle(perSkill));

        // ---- 4. back to how it was
        cheat.SetLanguage(before == "" ? Lang.Auto : before);
        yield return 5;
        Check("dil geri alininca Ingilizce", Lang.Active == Lang.English && FindTexts(cheat.Menu!.Panel).Contains("CHEAT MENU"), $"secim: {Lang.Choice}");
        if (cheat.Menu is { Visible: true }) cheat.ToggleMenu();
    }

    /// <summary>The words a window shows: labels, buttons, list entries, search boxes, table titles.</summary>
    static List<string> FindTexts(Node root)
    {
        var found = new List<string>();
        void Walk(Node n)
        {
            if (n is CanvasItem { Visible: false }) return;
            switch (n)
            {
                case OptionButton o:
                    for (int i = 0; i < o.ItemCount; i++) found.Add(o.GetItemText(i));
                    break;
                case Button b when b.Text != "": found.Add(b.Text); break;
                case Label l when l.Text != "": found.Add(l.Text); break;
                case LineEdit e when e.PlaceholderText != "": found.Add(e.PlaceholderText); break;
                case Godot.Tree t:
                    for (int c = 0; c < t.Columns; c++) if (t.GetColumnTitle(c) != "") found.Add(t.GetColumnTitle(c));
                    break;
            }
            foreach (Node c in n.GetChildren()) Walk(c);
        }
        Walk(root);
        return found;
    }
}
