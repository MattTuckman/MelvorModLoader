using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.MainMenu;
using GamesByMalcs.Melvor2.Saves;
using Godot;
using MelvorModLoader;

namespace TestHarness;

/// <summary>
/// Plays the game from code: opens (or creates) the test character, runs Tests.Run, saves and quits.
/// Active only when the process has MML_TEST=1, so a normal game start ignores it completely.
/// </summary>
public class Harness : IMod
{
    public const string TestCharacter = "ModTest";

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "GamemodeSelectionNewSaveRequestedHandler")]
    static extern void RequestNewSave(MainMenuView menu, object? sender, CreateSaveEventArgs e);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "SaveSelectSlotSelectedHandler")]
    static extern void RequestLoadSave(MainMenuView menu, object? sender, SaveSelectedEventArgs e);

    ModContext _ctx = null!;
    IEnumerator<int>? _flow;
    int _wait;
    static string s_out = "";

    public static void Out(string line)
    {
        try { File.AppendAllText(s_out, line + "\n"); } catch { }
    }

    public void OnLoad(ModContext ctx)
    {
        if (System.Environment.GetEnvironmentVariable("MML_TEST") != "1") return;
        s_out = System.Environment.GetEnvironmentVariable("MML_TEST_OUT") ?? Path.Combine(ctx.Directory, "test-out.txt");
        File.WriteAllText(s_out, "");
        _ctx = ctx;
        ctx.Frame += Tick;
        Out("harness: aktif");
    }

    void Tick()
    {
        if (_wait > 0) { _wait--; return; }
        try
        {
            _flow ??= Flow();
            if (_flow.MoveNext()) _wait = _flow.Current;
            else _ctx.Frame -= Tick;
        }
        catch (Exception e)
        {
            Out("HARNESS HATA: " + e);
            _ctx.Frame -= Tick;
            _ctx.Tree.Quit(1);
        }
    }

    static Button? FindButton(Node node, string text)
    {
        if (node is Button b && b.Text == text && b.IsVisibleInTree()) return b;
        foreach (Node child in node.GetChildren())
        {
            Button? found = FindButton(child, text);
            if (found != null) return found;
        }
        return null;
    }

    IEnumerator<int> Flow()
    {
        // 1. Main menu
        MainMenuView? menu = null;
        for (int i = 0; ; i++)
        {
            menu = GameAccess.FindNode<MainMenuView>();
            if (menu != null && menu.IsInsideTree()) break;
            if (i > 400) { Out("harness: ana menu gelmedi"); _ctx.Tree.Quit(2); yield break; }
            yield return 10;
        }
        yield return 90;

        // 2. Open the test character, creating it the first time
        Out("harness: kayitlar = " + string.Join(", ", SaveManager.StoredSaves.Values.Where(s => s.HasSave).Select(s => s.GetMostRecentSave().Save.Header.CharacterName)));
        StoredSaves? existing = SaveManager.StoredSaves.Values.FirstOrDefault(s => s.HasSave && s.GetMostRecentSave().Save.Header.CharacterName == TestCharacter);
        if (existing != null)
        {
            Out("harness: test karakteri yukleniyor");
            RequestLoadSave(menu, null, new SaveSelectedEventArgs(existing.Id, existing.GetMostRecentSave().Source));
        }
        else
        {
            if (SaveManager.IsAtMaxSaves) { Out("harness: kayit yuvasi dolu, test karakteri olusturulamadi"); _ctx.Tree.Quit(3); yield break; }
            Out("harness: test karakteri olusturuluyor");
            RequestNewSave(menu, null, new CreateSaveEventArgs(new NewSaveConfig { GamemodeId = 0, CharacterName = TestCharacter, SkipOnboarding = true }));
        }

        // 3. Wait until the save is running
        Game? game = null;
        for (int i = 0; ; i++)
        {
            game = GameAccess.CurrentGame;
            if (game is { IsSaveLoaded: true }) break;
            if (i > 600) { Out("harness: kayit acilmadi"); _ctx.Tree.Quit(4); yield break; }
            yield return 10;
        }
        yield return 120;
        // The game greets a returning player with a "Welcome Back" window; confirm it like a player would.
        for (int i = 0; i < 20; i++)
        {
            Button? confirm = FindButton(_ctx.Tree.Root, "Confirm");
            if (confirm == null) break;
            confirm.EmitSignal(BaseButton.SignalName.Pressed);
            yield return 15;
        }
        Out("harness: oyun acik");

        // 4. Tests
        IEnumerator<int> tests = Tests.Run(game!, _ctx);
        while (true)
        {
            bool more;
            try { more = tests.MoveNext(); }
            catch (Exception e) { Out("TEST HATA: " + e); break; }
            if (!more) break;
            yield return tests.Current;
        }
        Out("harness: testler bitti");

        // 5. Leave the way a player does (window close request), so the game saves itself
        _ctx.Tree.Root.PropagateNotification((int)Node.NotificationWMCloseRequest);
        yield return 900;
        Out("harness: oyun kendi kapanmadi, zorla kapatiliyor");
        _ctx.Tree.Quit(5);
    }
}
