using System;
using System.Collections.Generic;
using System.Linq;
using CheatMenu;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Entities;
using GamesByMalcs.Melvor2.Components.CombatAreas;
using GamesByMalcs.Melvor2.Entities.Bank;
using GamesByMalcs.Melvor2.Entities.Combat;
using GamesByMalcs.Melvor2.Components.CombatFood;
using GamesByMalcs.Melvor2.Components.Experience;
using GamesByMalcs.Melvor2.Components.Actions;
using GamesByMalcs.Melvor2.Components.ItemBurying;
using GamesByMalcs.Melvor2.Data.Currencies;
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.Entities.Firemaking;
using GamesByMalcs.Melvor2.Entities.Slayer;
using GamesByMalcs.Melvor2.Entities.Smithing;
using GamesByMalcs.Melvor2.Entities.Thieving;
using GamesByMalcs.Melvor2.Entities.Woodcutting;
using Godot;
using MelvorModLoader;
using Engine = CheatMenu.Engine;
using System.IO;

namespace TestHarness;

/// <summary>Automated checks for the Cheat Menu mod, run on the ModTest character.</summary>
static class Tests
{
    static void Out(string s) => Harness.Out(s);

    static Game s_game = null!;
    static BankEntity s_bank = null!;
    static WoodcuttingEntity s_wc = null!;

    static int BankItems() => s_bank.Inventory.Stacks.Sum(s => s.Quantity);

    /// <summary>Fast-forwards the game by running ticks directly; returns XP and items gained.</summary>
    static (double Xp, int Items) Measure(int ticks)
    {
        double xp = s_wc.Experience.Xp;
        int items = BankItems();
        for (int i = 0; i < ticks; i++) s_game.PropagateTick();
        return (s_wc.Experience.Xp - xp, BankItems() - items);
    }

    record FightResult(int Taken, int Dealt, int Hits, int Kills, int Deaths);

    static int s_hits, s_kills, s_deaths;
    static CombatArea s_area = null!;
    static Enemy s_enemy = null!;
    static bool s_fightHooked;

    /// <summary>Runs combat ticks; hits, kills and deaths are counted from the game's own events.</summary>
    static FightResult Fight(CombatEntity c, int ticks)
    {
        if (!s_fightHooked)
        {
            s_fightHooked = true;
            c.Enemy.Health.Damaged += (_, _) => s_hits++;
            c.Enemy.Killed += (_, _) => s_kills++;
            c.Action.PlayerDied += (_, _) => s_deaths++;
        }
        s_hits = s_kills = s_deaths = 0;
        c.Player.Health.Set(c.Player.Health.Max);   // every measurement starts at full health
        int taken = 0, php = c.Player.Health.Current;
        for (int i = 0; i < ticks; i++)
        {
            s_game.PropagateTick();
            if (!c.Action.IsStarted)
            {
                // After a death the game leaves the area: walk back in and continue.
                c.Player.Health.Set(c.Player.Health.Max);
                c.Areas.SelectEnemy(s_area, s_enemy);          // selecting an enemy starts the fight by itself
                if (!c.Action.IsStarted) c.Action.TryStart();   // (TryStart on a running fight would stop it first)
                php = c.Player.Health.Current;
                continue;
            }
            int p = c.Player.Health.Current;
            if (p < php) taken += php - p;
            php = p;
        }
        return new FightResult(taken, 0, s_hits, s_kills, s_deaths);
    }

    static string Show(FightResult r) => $"alinan hasar {r.Taken}, vurus {r.Hits}, oldurme {r.Kills}, olum {r.Deaths}";

    static void Check(string name, bool ok, string detail) => Out($"{(ok ? "GECTI " : "KALDI ")} {name}: {detail}");

    static bool Near(double actual, double expected, double tolerance = 0.25) =>
        expected == 0 ? actual == 0 : Math.Abs(actual / expected - 1) <= tolerance;

    /// <summary>Saves pictures of the game, every tab of the cheat menu and the XP line (needs a rendering run).</summary>
    static IEnumerator<int> Screenshots(Game game, ModContext ctx)
    {
        string dir = Path.Combine(Path.GetDirectoryName(System.Environment.GetEnvironmentVariable("MML_TEST_OUT")!)!, "shots");
        Directory.CreateDirectory(dir);
        foreach (string f in Directory.GetFiles(dir, "*.png")) File.Delete(f);
        void Shot(string name)
        {
            Error e = ctx.Tree.Root.GetTexture().GetImage().SavePng(Path.Combine(dir, name));
            Out($"  goruntu {name}: {e}");
        }
        Out($"=== Goruntuler === pencere {DisplayServer.WindowGetSize()} @ {DisplayServer.WindowGetPosition()}, mod {DisplayServer.WindowGetMode()}");

        Engine.Bind(game);
        Engine.ResetAll();
        Engine.SetValue("xp.multiplier", 5);
        Engine.SetValue("bank.slots", 500);
        Engine.SetValue("combat.god", 1);
        Engine.SetValue("skill.Woodcutting.xp", 3);
        game.ActionManager.TryStopActive(force: true);
        s_wc.Action.SelectTree(s_wc.Action.Trees.Values.First());
        s_wc.Action.TryStart();
        yield return 90;
        Shot("0-oyun.png");

        // A few loot rules and a selected monster, so the loot page has something to show.
        game.Entities.TryGetObjectById("MelvorBase:Combat", out GameEntity? shotCombat);
        CombatArea shotArea = game.Data.GetDataRegistryOutput<CombatArea>().GetAllObjects().First(x => x.Enemies.Count > 0);
        ((CombatEntity)shotCombat!).Areas.SelectEnemy(shotArea, shotArea.Enemies[0]);
        List<Item> drops = LootFilter.Droppable(game);
        LootFilter.Clear();
        LootFilter.Set(drops.Take(2), LootRule.Sell);
        LootFilter.Set(drops.Skip(3).Take(1), LootRule.Destroy);
        Engine.SetValue("combat.damage", 25);
        Engine.SetValue("combat.lootDouble", 50);

        CheatMenuMod mod = CheatMenuMod.Instance!;
        mod.ToggleMenu();
        yield return 30;
        for (int i = 0; i < mod.Menu!.PageCount; i++)
        {
            mod.Menu.ShowPage(i);
            yield return 20;
            Shot($"menu-{i + 1}-{mod.Menu.PageTitle(i).Replace(' ', '-').ToLowerInvariant()}.png");
        }
        mod.Menu.ShowPage(0);
        LootFilter.Clear();
        mod.ToggleMenu();

        BulkSell.BulkSellMod bulk = BulkSell.BulkSellMod.Instance!;
        BulkSell.Junk.Bind(game.CharacterName);
        BulkSell.Junk.Set(s_bank.Inventory.Stacks.Where(s => !s.IsLocked && s.Quantity > 0).Take(3).Select(s => s.Item));
        GameAccess.FindGameView()!.OpenBank();
        yield return 60;
        Shot("banka-1-dugmeler.png");
        bulk.ToggleWindow();
        yield return 20;
        for (int i = 0; i < Math.Min(6, bulk.Window!.Shown.Count); i += 2) bulk.Window.ClickRow(i);
        yield return 20;
        Shot("banka-2-toplu-satis.png");
        bulk.ToggleWindow();
        BulkSell.Junk.Set(Array.Empty<Item>());

        ActionQueue.Queue.Bind(game);
        ActionQueue.Queue.Clear();
        var qSkills = ActionQueue.Targets.Skills(game);
        void QAdd(string entityId, int targetIndex, ActionQueue.Until until, double amount, string subject, string subjectName)
        {
            var skill = qSkills.First(s => s.Entity.Id == entityId);
            ActionQueue.Target target = ActionQueue.Targets.All(skill.Action)[targetIndex];
            ActionQueue.Queue.Add(new ActionQueue.Step { Entity = entityId, TargetId = target.Id, Extra = target.Extra, Name = $"{skill.Entity.DisplayName.Text}: {target.Name}",
                Until = until, Amount = amount, Subject = subject, SubjectName = subjectName });
        }
        Item firstLog = s_bank.Inventory.Stacks.First(s => s.Quantity > 0).Item;
        QAdd("MelvorBase:Woodcutting", 0, ActionQueue.Until.Item, 1000000, firstLog.Id, Actions.ItemName(firstLog));
        QAdd("MelvorBase:Firemaking", 0, ActionQueue.Until.Stops, 0, "", "");
        QAdd("MelvorBase:Fishing", 0, ActionQueue.Until.Level, 30, "Fishing", "Fishing");
        QAdd("MelvorBase:Combat", ActionQueue.Targets.All(qSkills.First(s => s.Entity.Id == "MelvorBase:Combat").Action).FindIndex(x => x.Name.StartsWith("Golbin")), ActionQueue.Until.Minutes, 45, "", "");
        ActionQueue.Queue.Start();
        ActionQueue.Queue.Tick(game, 0.5);
        yield return 90;
        Shot("sira-1-ekran-seridi.png");
        ActionQueue.ActionQueueMod.Instance!.ToggleWindow();
        yield return 30;
        // Show the "until I own N of an item" goal with the item filled in from the chosen action.
        ActionQueue.QueueWindow queueWindow = ActionQueue.ActionQueueMod.Instance.Window!;
        var smithing = qSkills.First(s => s.Entity.Id == "MelvorBase:Smithing");
        ActionQueue.Target? bar = ActionQueue.Targets.All(smithing.Action).FirstOrDefault(x => x.Name.EndsWith(" Bar"));
        if (bar != null) queueWindow.Choose(smithing.Entity.Id, bar.Id);
        foreach (Node n in queueWindow.Panel.FindChildren("*", "OptionButton", true, false))
        {
            var pick = (OptionButton)n;
            if (pick.ItemCount == 4 && pick.GetItemText(1).StartsWith("I own"))
            {
                pick.Select(1);
                pick.EmitSignal(OptionButton.SignalName.ItemSelected, 1);
            }
        }
        yield return 20;
        Shot("sira-2-pencere.png");
        // The planner: the longest plan that is not locked, for 100 of the item.
        ActionQueue.PlanResult? shotPlan = null;
        foreach (Item candidate in ActionQueue.Planner.Makeable(game))
        {
            ActionQueue.PlanResult plan = ActionQueue.Planner.Compute(game, candidate, 100);
            if (plan.Steps.Count == 0 || plan.Steps.Any(l => l.Problem != "")) continue;
            if (shotPlan == null || plan.Steps.Count > shotPlan.Steps.Count) shotPlan = plan;
        }
        if (shotPlan != null) queueWindow.PlanFor(shotPlan.Item.Id, 100);
        yield return 20;
        Shot("sira-3-plan.png");
        queueWindow.ShowStepForm();
        ActionQueue.ActionQueueMod.Instance.ToggleWindow();
        ActionQueue.Queue.Clear();
        game.ActionManager.TryStopActive(force: true);

        // The modder's kit example (only when it has been built into the mods folder): its hotkey is F10.
        // The key press is created inside this test game only; nothing is sent to the real keyboard.
        if (Loader.LoadedMods.Contains("ExampleMod"))
        {
            Input.ParseInputEvent(new InputEventKey { Keycode = Key.F10, PhysicalKeycode = Key.F10, Pressed = true });
            yield return 20;
            Input.ParseInputEvent(new InputEventKey { Keycode = Key.F10, PhysicalKeycode = Key.F10, Pressed = false });
            yield return 80;
            Shot("ornek-mod.png");
            Input.ParseInputEvent(new InputEventKey { Keycode = Key.F10, PhysicalKeycode = Key.F10, Pressed = true });
            yield return 10;
            Input.ParseInputEvent(new InputEventKey { Keycode = Key.F10, PhysicalKeycode = Key.F10, Pressed = false });
            yield return 10;
        }
        mod.ToggleMenu();
        mod.ToggleMenu();

        Engine.ResetAll();
        game.ActionManager.TryStopActive(force: true);
    }

    static int s_ticksOnMain, s_ticksElsewhere, s_mainThread;
    static long s_firstOfflineTick, s_lastOfflineTick;
    static readonly List<long> s_mainTickTimes = new();

    static Button? VisibleButton(Node node, params string[] texts)
    {
        if (node is Button b && texts.Contains(b.Text) && b.IsVisibleInTree()) return b;
        foreach (Node child in node.GetChildren())
        {
            Button? found = VisibleButton(child, texts);
            if (found != null) return found;
        }
        return null;
    }

    /// <summary>
    /// How fast the game computes offline progress, and whether anything of ours ticks the game from the main
    /// thread while the game is doing that on its own thread.
    /// </summary>
    static IEnumerator<int> OfflineSpeed(Game game, ModContext ctx)
    {
        Out("=== Cevrimdisi hesap hizi ===");
        s_mainThread = System.Environment.CurrentManagedThreadId;
        Engine.Bind(game);
        Engine.ResetAll();
        game.ActionManager.TryStopActive(force: true);
        foreach (string name in new[] { "Attack", "Strength", "Defence", "Hitpoints" })
            Actions.SetLevel(Actions.Skills(game).First(s => s.DisplayName.Text == name), 40);
        game.Entities.TryGetObjectById("MelvorBase:Combat", out GameEntity? combatEntity);
        var combat = (CombatEntity)combatEntity!;
        CombatArea area = game.Data.GetDataRegistryOutput<CombatArea>().GetAllObjects().First(a => a.Enemies.Count > 0);
        combat.Action.Ticked += (_, _) =>
        {
            if (System.Environment.CurrentManagedThreadId == s_mainThread) { s_ticksOnMain++; s_mainTickTimes.Add(System.Diagnostics.Stopwatch.GetTimestamp()); return; }
            // The game's own offline thread: time it from its first tick to its last.
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (s_firstOfflineTick == 0) s_firstOfflineTick = now;
            s_lastOfflineTick = now;
            System.Threading.Interlocked.Increment(ref s_ticksElsewhere);
        };
        void StartFight()
        {
            game.ActionManager.TryStopActive(force: true);
            combat.Player.Health.Set(combat.Player.Health.Max);
            combat.Areas.SelectEnemy(area, area.Enemies[0]);
            if (!combat.Action.IsStarted) combat.Action.TryStart();
        }

        // Falor's own values (numbers only, read from the settings file; that character is not opened).
        string settingsFile = Path.Combine(Path.GetDirectoryName(typeof(CheatMenuMod).Assembly.Location)!, "settings.json");
        var saved = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, double>>>(File.ReadAllText(settingsFile))!;
        saved.TryGetValue("Falor", out Dictionary<string, double>? falor);
        falor ??= new Dictionary<string, double>();
        Out($"  Falor'un acik ayarlari: {falor.Count} ({string.Join(", ", falor.Where(v => !v.Key.StartsWith("skill.")).Select(v => $"{v.Key}={v.Value}"))}, +{falor.Count(v => v.Key.StartsWith("skill."))} beceri ayari)");

        var scenarios = new List<(string Name, Action Setup)>
        {
            ("ayar yok", () => { }),
            ("sadece oyun hizi x3", () => Engine.SetValue("speed.game", 3)),
            ("Falor'un ayarlari, oyun hizi haric", () => { foreach (var v in falor) if (v.Key != "speed.game" && Features.Find(v.Key) != null) Engine.SetValue(v.Key, v.Value); }),
            ("Falor'un butun ayarlari", () => { foreach (var v in falor) if (Features.Find(v.Key) != null) Engine.SetValue(v.Key, v.Value); }),
        };
        const double Hours = 0.5;
        foreach (var (name, setup) in scenarios)
        {
            Engine.ResetAll();
            Engine.SetValue("combat.god", 1);   // the test character must survive every run the same way
            setup();
            StartFight();
            yield return 30;

            // (a) the plain cost of a tick, on the main thread
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < 30000; i++) game.PropagateTick();
            double plain = 30000 / sw.Elapsed.TotalSeconds;
            if (!combat.Action.IsStarted) StartFight();
            yield return 240;   // let the engine catch up before the timed run

            // (b) the real thing: the game's own offline calculation. Mods get no frames while it runs
            // (the loader holds them back), so this waits until the game's thread has done all its ticks.
            s_ticksOnMain = s_ticksElsewhere = 0;
            s_firstOfflineTick = s_lastOfflineTick = 0;
            s_mainTickTimes.Clear();
            int expected = (int)(Hours * 3600 * 60);
            bool heldBack = true;
            game.RequestOfflineHours(Hours);   // the game pauses the scene tree right inside this call
            for (int waited = 0; s_ticksElsewhere < expected - 120 && waited < 60 * 60 * 5; waited++)
            {
                yield return 1;
                if (ctx.Tree.Paused) heldBack = false;   // a frame reached a mod while the game was still calculating
            }
            double seconds = System.Diagnostics.Stopwatch.GetElapsedTime(s_firstOfflineTick, s_lastOfflineTick).TotalSeconds;
            // Ticks made from the main thread while the game's own thread was between its first and last tick.
            int atTheSameTime = s_mainTickTimes.Count(ts => ts > s_firstOfflineTick && ts < s_lastOfflineTick);
            Check($"cevrimdisi hesap [{name}]", s_ticksElsewhere >= expected - 120 && atTheSameTime == 0 && heldBack && seconds < 30,
                  $"{Hours} saat = {s_ticksElsewhere:N0} tik, {seconds:0.00} sn surdu ({s_ticksElsewhere / Math.Max(0.001, seconds):N0} tik/sn); hesapla AYNI ANDA ana koldan {atTheSameTime} tik " +
                  $"(oncesi/sonrasi normal oyun: {s_ticksOnMain}); hesap sirasinda modlara kare gitmedi: {heldBack}; duz tik hizi {plain:N0}/sn");
            yield return 60;
            for (int i = 0; i < 10; i++)
            {
                Button? close = VisibleButton(ctx.Tree.Root, "Confirm", "Close", "Continue", "OK", "Okay");
                if (close == null) break;
                close.EmitSignal(BaseButton.SignalName.Pressed);
                yield return 20;
            }
        }
        Engine.ResetAll();
        game.ActionManager.TryStopActive(force: true);
    }

    /// <summary>Checks that a language added at run time shows up the way the game's own languages do.</summary>
    static IEnumerator<int> LanguageProbe()
    {
        Out("=== Dil denemesi ===");
        string old = TranslationServer.GetLocale();
        Out($"yuklu diller: {string.Join(", ", TranslationServer.GetLoadedLocales())} | secili: {old}");
        var tr = new Translation { Locale = "tr" };
        tr.AddMessage("Woodcutting", "Odunculuk");
        tr.AddMessage("Bank", "Banka");
        TranslationServer.AddTranslation(tr);
        string[] locales = TranslationServer.GetLoadedLocales();
        Check("Turkce dil listesine giriyor", locales.Contains("tr"), $"{string.Join(", ", locales)} | listede gorunecek ad: {TranslationServer.GetLocaleName("tr")}");
        string before = s_wc.DisplayName.Text;
        TranslationServer.SetLocale("tr");
        yield return 5;
        Check("ceviri uygulaniyor", TranslationServer.Translate("Woodcutting") == "Odunculuk" && s_wc.DisplayName.Text == "Odunculuk",
              $"beceri adi: {before} -> {s_wc.DisplayName.Text}; cevrilmemis yazi oldugu gibi kaliyor: {TranslationServer.Translate("Mining")}");
        Check("sayi bicimi bozulmuyor", double.TryParse("1.5", System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _),
              $"oyunun kultur ayari: {GamesByMalcs.Melvor2.Localization.Locale.Current.Name}, ornek: {(1234.5).ToString("N1", GamesByMalcs.Melvor2.Localization.Locale.Current)} / {(0.25).ToString("P0", GamesByMalcs.Melvor2.Localization.Locale.Current)}");
        for (int i = 0; i < 600; i++) s_game.PropagateTick();
        Out("Turkce secili iken 600 tik sorunsuz calisti");
        TranslationServer.SetLocale(old);
        TranslationServer.RemoveTranslation(tr);
        yield return 5;
        Out($"geri alindi: {string.Join(", ", TranslationServer.GetLoadedLocales())} | secili: {TranslationServer.GetLocale()} | {s_wc.DisplayName.Text}");
    }

    public static IEnumerator<int> Run(Game game, ModContext ctx)
    {
        s_game = game;
        game.Entities.TryGetObjectById("MelvorBase:Bank", out GameEntity? bankEntity);
        s_bank = (BankEntity)bankEntity!;
        game.Entities.TryGetObjectById("MelvorBase:Woodcutting", out GameEntity? wcEntity);
        s_wc = (WoodcuttingEntity)wcEntity!;

        if (System.Environment.GetEnvironmentVariable("MML_ONLY") == "shots")
        {
            IEnumerator<int> shots = Screenshots(game, ctx);
            while (shots.MoveNext()) yield return shots.Current;
            yield break;
        }
        if (System.Environment.GetEnvironmentVariable("MML_ONLY") == "offline")
        {
            IEnumerator<int> offline = OfflineSpeed(game, ctx);
            while (offline.MoveNext()) yield return offline.Current;
            yield break;
        }
        if (System.Environment.GetEnvironmentVariable("MML_ONLY") == "lang")
        {
            IEnumerator<int> probe = LanguageProbe();
            while (probe.MoveNext()) yield return probe.Current;
            yield break;
        }

        Engine.Bind(game);
        Engine.ResetAll();
        yield return 5;

        // ---------- 1. every feature is accepted by the game ----------
        Out("=== 1. Ayarlarin oyunca kabul edilmesi ===");
        foreach (Feature f in Features.All)
        {
            double v = f.Kind == FeatureKind.Toggle ? 1 : f.Off == 1 ? Math.Min(f.Max, 5) : Math.Min(f.Max, f.Min + Math.Max(f.Step, 50));
            try
            {
                Engine.SetValue(f.Id, v);
                Out($"  {f.Id} = {v}  ->  {string.Join(", ", Engine.Diagnose(f.Id))}");
            }
            catch (Exception e) { Out($"  KALDI {f.Id}: {(e.InnerException ?? e).Message}"); }
        }
        Check("bonus tanimlari", Engine.Problems.Count == 0, Engine.Problems.Count == 0 ? "hepsi kabul edildi" : string.Join(" | ", Engine.Problems));
        Engine.ResetAll();
        yield return 5;

        // ---------- 2. bank ----------
        Out("=== 2. Banka ===");
        int slots = s_bank.Inventory.MaxStacks;
        Engine.SetValue("bank.slots", 500);
        Check("banka slotu +500", s_bank.Inventory.MaxStacks == slots + 500, $"{slots} -> {s_bank.Inventory.MaxStacks}");
        Engine.SetValue("bank.slots", 0);
        Check("banka slotu sifirlama", s_bank.Inventory.MaxStacks == slots, $"{s_bank.Inventory.MaxStacks}");

        // ---------- 3. woodcutting played by code ----------
        Out("=== 3. Odunculuk (oyun kodla oynatiliyor) ===");
        game.ActionManager.TryStopActive(force: true);
        WoodcuttingTree tree = s_wc.Action.Trees.Values.First();
        s_wc.Action.SelectTree(tree);
        bool started = s_wc.Action.IsStarted || s_wc.Action.TryStart();
        Check("odun kesmeyi baslatma", started, $"agac: {tree.Recipe.DisplayName.Text}");
        const int Ticks = 40000;
        var baseline = Measure(Ticks);
        Out($"  normal: {Ticks} tikte XP +{baseline.Xp:0.#}, esya +{baseline.Items}");
        Check("normal oyun ilerliyor", baseline.Xp > 0 && baseline.Items > 0, "XP ve odun kazanildi");

        Engine.SetValue("xp.multiplier", 10);
        var xp10 = Measure(Ticks);
        Check("XP carpani x10", Near(xp10.Xp, baseline.Xp * 10) && Near(xp10.Items, baseline.Items), $"XP +{xp10.Xp:0.#} (beklenen ~{baseline.Xp * 10:0.#}), esya +{xp10.Items}");
        Engine.ResetAll();

        Engine.SetValue("speed.actions", 5);
        var fast = Measure(Ticks);
        Check("islem hizi x5", Near(fast.Xp, baseline.Xp * 5) && Near(fast.Items, baseline.Items * 5), $"XP +{fast.Xp:0.#} (~{baseline.Xp * 5:0.#}), esya +{fast.Items} (~{baseline.Items * 5})");
        Engine.ResetAll();

        Engine.SetValue("skills.doubling", 100);
        var doubled = Measure(Ticks);
        Check("cift urun %100", Near(doubled.Items, baseline.Items * 2) && Near(doubled.Xp, baseline.Xp), $"esya +{doubled.Items} (~{baseline.Items * 2}), XP +{doubled.Xp:0.#}");
        Engine.ResetAll();

        Engine.SetValue("skills.quantityFlat", 10);
        int perCut = tree.Recipe.Product.Quantity;
        var extra = Measure(Ticks);
        Check("islem basina +10 urun", tree.Stats.ProductQuantity.Value == perCut + 10 && Near(extra.Items, baseline.Items * (perCut + 10.0) / perCut),
              $"kesim basina {perCut} -> {tree.Stats.ProductQuantity.Value}, esya +{extra.Items} (~{baseline.Items * (perCut + 10) / perCut})");
        Engine.ResetAll();

        Engine.SetValue("skills.instantCut", 100);
        var instant = Measure(Ticks);
        Check("tek vurusta kesme %100", instant.Items > baseline.Items, $"esya +{instant.Items} (normal {baseline.Items})");
        Engine.ResetAll();

        Engine.SetValue("skill.Woodcutting.xp", 3);
        var ownXp = Measure(Ticks);
        Check("beceri bazinda XP (Odunculuk x3)", Near(ownXp.Xp, baseline.Xp * 3, 0.1), $"XP +{ownXp.Xp:0.#} (~{baseline.Xp * 3:0.#})");
        Engine.ResetAll();

        Engine.SetValue("skill.Woodcutting.speed", 2);
        var ownSpeed = Measure(Ticks);
        Check("beceri bazinda hiz (Odunculuk x2)", Near(ownSpeed.Items, baseline.Items * 2.0, 0.1), $"esya +{ownSpeed.Items} (~{baseline.Items * 2})");
        Engine.SetValue("skill.Woodcutting.speed", 1);
        Engine.SetValue("skill.Mining.speed", 10);
        var otherSkill = Measure(Ticks);
        Check("baska becerinin hizi odunculugu etkilemiyor", Near(otherSkill.Items, baseline.Items, 0.1), $"esya +{otherSkill.Items} (~{baseline.Items})");
        Engine.ResetAll();

        // ---------- 4. whole-game speed (real time) ----------
        Out("=== 4. Oyun hizi (gercek zamanli) ===");
        Engine.SetValue("skills.instantCut", 100);   // one item per hit: a fine-grained progress counter
        ulong settle = Time.GetTicksMsec();          // let the engine catch up after the long tick loops above
        while (Time.GetTicksMsec() - settle < 4000) yield return 1;
        int itemsA = BankItems();
        ulong t0 = Time.GetTicksMsec();
        while (Time.GetTicksMsec() - t0 < 5000) yield return 1;
        int normalRate = BankItems() - itemsA;
        Engine.SetValue("speed.game", 10);
        itemsA = BankItems();
        t0 = Time.GetTicksMsec();
        while (Time.GetTicksMsec() - t0 < 5000) yield return 1;
        int fastRate = BankItems() - itemsA;
        Check("oyun hizi x10", normalRate > 0 && Near(fastRate, normalRate * 10.0, 0.3), $"5 sn'de esya: normal +{normalRate}, x10 +{fastRate}");
        Engine.ResetAll();

        // ---------- 5. tools ----------
        Out("=== 5. Araclar ===");
        var currency = Actions.Currencies(game).First();
        long money = s_bank.Wallet.GetQuantity(currency);
        Actions.AddCurrency(game, currency, 12345);
        Check("para verme", s_bank.Wallet.GetQuantity(currency) == money + 12345, $"{currency.DisplayName.Text}: {money} -> {s_bank.Wallet.GetQuantity(currency)}");

        var item = Actions.Items(game).First();
        int had = s_bank.Inventory.GetQuantity(item);
        Actions.AddItem(game, item, 7);
        Check("esya verme", s_bank.Inventory.GetQuantity(item) == had + 7, $"{Actions.ItemName(item)}: {had} -> {s_bank.Inventory.GetQuantity(item)}");

        var mining = Actions.Skills(game).First(s => s.DisplayName.Text == "Mining");
        Actions.SetLevel(mining, 50);
        Check("seviye ayarlama", mining.Experience.Level == 50, $"Mining = {mining.Experience.Level}");

        // ---------- 5a. auto-sell and save backup ----------
        Out("=== 5a. Otomatik satis ve kayit yedegi ===");
        AutoSell.Clear();
        Actions.AddItem(game, item, 5);
        int stock = s_bank.Inventory.GetQuantity(item);
        long before = s_bank.Wallet.GetQuantity(currency);
        int soldNothing = AutoSell.RunOnce(game);
        AutoSell.Add(item);
        int sold = AutoSell.RunOnce(game);
        Check("otomatik satis", soldNothing == 0 && sold == stock && s_bank.Inventory.GetQuantity(item) == 0 && s_bank.Wallet.GetQuantity(currency) > before,
              $"{Actions.ItemName(item)}: {stock} adet satildi, para {before} -> {s_bank.Wallet.GetQuantity(currency)}");
        AutoSell.Clear();
        Actions.AddItem(game, item, 3);

        string backup = SaveBackup.SaveBackupMod.LastBackupDir;
        int backedUp = backup != "" && Directory.Exists(backup) ? Directory.GetFiles(backup, "*.sav", SearchOption.AllDirectories).Length : 0;
        Check("kayit yedegi", backedUp > 0, $"{backedUp} kayit dosyasi: {backup}");

        // ---------- 5b. sell price and offline limit ----------
        Out("=== 5b. Satis fiyati ve cevrimdisi sure ===");
        long gp0 = s_bank.Wallet.GetQuantity(currency);
        s_bank.ItemSales.SellItem(item, s_bank.ItemSales.GetItemStats(item), 1);
        long normalPrice = s_bank.Wallet.GetQuantity(currency) - gp0;
        Engine.SetValue("bank.sellPrice", 5);
        gp0 = s_bank.Wallet.GetQuantity(currency);
        s_bank.ItemSales.SellItem(item, s_bank.ItemSales.GetItemStats(item), 1);
        long boostedPrice = s_bank.Wallet.GetQuantity(currency) - gp0;
        Check("satis fiyati x5", normalPrice > 0 && Near(boostedPrice, normalPrice * 5.0, 0.02), $"{Actions.ItemName(item)}: {normalPrice} -> {boostedPrice}");
        Engine.ResetAll();

        double cap0 = game.OfflineProgress.Component.MaxOfflineTime / 3600000.0;
        Engine.SetValue("speed.offlineHours", 48);
        double cap1 = game.OfflineProgress.Component.MaxOfflineTime / 3600000.0;
        Check("cevrimdisi sinir +48 saat", Near(cap1, cap0 + 48, 0.01), $"{cap0:0.#} sa -> {cap1:0.#} sa");
        Engine.ResetAll();

        // ---------- 5c. combat played by code ----------
        Out("=== 5c. Savas (oyun kodla oynatiliyor) ===");
        game.ActionManager.TryStopActive(force: true);
        foreach (string name in new[] { "Attack", "Strength", "Defence", "Hitpoints" })
            Actions.SetLevel(Actions.Skills(game).First(s => s.DisplayName.Text == name), 40);
        game.Entities.TryGetObjectById("MelvorBase:Combat", out GameEntity? combatEntity);
        var combat = (CombatEntity)combatEntity!;
        CombatArea area = game.Data.GetDataRegistryOutput<CombatArea>().GetAllObjects().First(a => a.Enemies.Count > 0);
        s_area = area;
        s_enemy = area.Enemies[0];
        combat.Player.Health.Set(combat.Player.Health.Max);
        combat.Areas.SelectEnemy(area, area.Enemies[0]);
        bool fighting = combat.Action.IsStarted || combat.Action.TryStart();
        Check("savasi baslatma", fighting, $"{area.DisplayName.Text} / {area.Enemies[0].DisplayName.Text}, can {combat.Player.Health.Current}/{combat.Player.Health.Max}");
        const int FightTicks = 12000;
        FightResult normal = Fight(combat, FightTicks);
        Out("  normal: " + Show(normal));
        Check("normal savas ilerliyor", normal.Hits > 0 && normal.Taken > 0, "iki taraf da hasar aliyor");

        Engine.SetValue("combat.god", 1);
        FightResult god = Fight(combat, FightTicks);
        Check("olumsuzluk", god.Taken == 0 && god.Deaths == 0 && god.Hits > 0, Show(god));

        Engine.SetValue("combat.attackSpeed", 5);
        FightResult quick = Fight(combat, FightTicks);
        Check("saldiri hizi x5", quick.Hits > god.Hits * 2, $"vurus {god.Hits} -> {quick.Hits}; " + Show(quick));
        Engine.SetValue("combat.attackSpeed", 1);

        Engine.SetValue("combat.damage", 50);
        FightResult strong = Fight(combat, FightTicks);
        Check("hasar x50", strong.Kills > 0 && strong.Hits == strong.Kills && god.Hits > god.Kills,
              $"oldurme basina vurus {(double)god.Hits / Math.Max(1, god.Kills):0.##} -> {(double)strong.Hits / Math.Max(1, strong.Kills):0.##}, oldurme {god.Kills} -> {strong.Kills}");

        // Kill counts vary from run to run, so this pair is measured over a longer stretch.
        FightResult slowSpawn = Fight(combat, FightTicks * 3);
        Engine.SetValue("speed.spawn", 10);
        FightResult spawn = Fight(combat, FightTicks * 3);
        Check("canavar cikma hizi x10", spawn.Kills > slowSpawn.Kills * 1.3, $"oldurme {slowSpawn.Kills} -> {spawn.Kills}");
        Engine.SetValue("speed.spawn", 1);
        Engine.SetValue("combat.damage", 1);

        Engine.SetValue("combat.accuracy", 100);
        FightResult accurate = Fight(combat, FightTicks);
        // The test character already hits almost every time, so this only shows that nothing gets worse (counts vary a little per run).
        Check("isabet x100", accurate.Hits >= god.Hits * 0.85, $"vurus {god.Hits} -> {accurate.Hits}");
        Engine.SetValue("combat.accuracy", 1);

        int hp0 = combat.Player.Health.Max;
        Engine.SetValue("combat.maxHp", 5);
        Check("azami can x5", Near(combat.Player.Health.Max, hp0 * 5.0, 0.02), $"{hp0} -> {combat.Player.Health.Max}");
        Engine.ResetAll();
        game.ActionManager.TryStopActive(force: true);

        // ---------- 5d. the remaining settings, each played or read from the game's own numbers ----------
        Out("=== 5d. Kalan ayarlar ===");
        Engine.ResetAll();
        game.ActionManager.TryStopActive(force: true);

        // Firemaking: materials are not used up
        game.Entities.TryGetObjectById("MelvorBase:Firemaking", out GameEntity? fmEntity);
        var fm = ((FiremakingEntity)fmEntity!).Action;
        var fmRecipe = fm.ActiveRecipes.Values.OrderBy(a => a.Recipe.Requirements.Conditions.OfType<LevelCondition>().Select(c => c.Level).DefaultIfEmpty(0).Max()).First();
        Item log = fmRecipe.Stats.ItemCost.Values[0].Value.Item;
        Actions.AddItem(game, log, 5000);
        fm.SelectRecipe(fmRecipe);
        bool burning = fm.TryStart();
        int burned = 0;
        fm.RecipeCreated += (_, _) => burned++;
        (int Used, int Made) Burn(int ticks)
        {
            burned = 0;
            int q = s_bank.Inventory.GetQuantity(log);
            for (int i = 0; i < ticks; i++) { game.PropagateTick(); if (!fm.IsStarted) fm.TryStart(); }
            return (q - s_bank.Inventory.GetQuantity(log), burned);
        }
        var burn0 = Burn(20000);
        Check("ates yakmayi baslatma", burning && burn0.Made > 0 && burn0.Used > 0, $"{Actions.ItemName(log)}: {burn0.Made} islem, {burn0.Used} malzeme harcandi");
        Engine.SetValue("skills.preservation", 100);
        var burn1 = Burn(20000);
        Check("malzeme harcamama %100", burn1.Made > 0 && burn1.Used == 0, $"{burn1.Made} islem, {burn1.Used} malzeme harcandi");
        Engine.ResetAll();
        game.ActionManager.TryStopActive(force: true);

        // Smithing: cost and quantity, read from the numbers the game uses for the recipe
        game.Entities.TryGetObjectById("MelvorBase:Smithing", out GameEntity? smEntity);
        var sm = ((SmithingEntity)smEntity!).Action;
        var smRecipe = sm.ActiveRecipes.Values.First(a => a.Stats.ItemCost.Values.Any(v => v.Value.Quantity >= 4));
        int cost0 = smRecipe.Stats.ItemCost.Values.Max(v => v.Value.Quantity);
        int made0 = smRecipe.Stats.Products.Values[0].Value.Quantity;
        Engine.SetValue("skills.cheaper", 50);
        Engine.SetValue("skills.quantityPercent", 3);
        int cost1 = smRecipe.Stats.ItemCost.Values.Max(v => v.Value.Quantity);
        int made1 = smRecipe.Stats.Products.Values[0].Value.Quantity;
        Check("malzeme indirimi %50", Near(cost1, cost0 * 0.5, 0.15), $"{smRecipe.Recipe.DisplayName.Text}: malzeme {cost0} -> {cost1}");
        Check("urun miktari x3", made1 == made0 * 3, $"urun {made0} -> {made1}");
        Engine.ResetAll();

        // Burying
        Item bone = Actions.Items(game).First(i => i.Capabilities.TryGet<BuryCapability>(out _));
        Actions.AddItem(game, bone, 10);
        long buried = 0;
        s_bank.ItemBurying.ItemBuried += (_, a) => buried = a.CurrencyGained.Quantity;
        s_bank.ItemBurying.BuryItem(bone, s_bank.ItemBurying.GetItemStats(bone), 1);
        long bury0 = buried;
        Engine.SetValue("bank.buryReward", 5);
        s_bank.ItemBurying.BuryItem(bone, s_bank.ItemBurying.GetItemStats(bone), 1);
        Check("kemik gomme odulu x5", bury0 > 0 && Near(buried, bury0 * 5.0, 0.05), $"{Actions.ItemName(bone)}: {bury0} -> {buried}");
        Engine.ResetAll();

        // Food
        Item food = Actions.Items(game).First(i => i.Capabilities.TryGet<FoodCapability>(out _));
        Actions.AddItem(game, food, 10);
        combat.Food.SelectFood(food);
        int eatenTimes = 0;
        combat.Food.FoodEaten += (_, _) => eatenTimes++;
        combat.Player.Health.Set(1);
        int foodBefore = s_bank.Inventory.GetQuantity(food);
        combat.Food.ManualEat();
        int used0 = foodBefore - s_bank.Inventory.GetQuantity(food);
        Engine.SetValue("combat.keepSupplies", 100);
        combat.Player.Health.Set(1);
        foodBefore = s_bank.Inventory.GetQuantity(food);
        combat.Food.ManualEat();
        int used1 = foodBefore - s_bank.Inventory.GetQuantity(food);
        Check("yemek harcamama %100", eatenTimes >= 2 && used0 == 1 && used1 == 0, $"{Actions.ItemName(food)}: {eatenTimes} kez yendi, harcanan {used0} -> {used1}");
        Engine.ResetAll();

        // Slayer
        game.Entities.TryGetObjectById("MelvorBase:Slayer", out GameEntity? slEntity);
        var master = ((SlayerEntity)slEntity!).Tasks.ActiveMasters.Values.First();
        if (master.CurrentTask == null) master.StartInitialTask();
        long reroll0 = master.Stats.RerollCost.Value.Quantity;
        Engine.SetValue("combat.slayerFreeReroll", 1);
        long reroll1 = master.Stats.RerollCost.Value.Quantity;
        Check("slayer gorev degistirme ucreti", reroll0 > 1 && reroll1 == 1, $"{reroll0} -> {reroll1}");
        Engine.ResetAll();

        // Reward per kill as the game computes it for a 1000-hitpoint task monster
        master.Stats.SetInput("EnemyHitpoints", 1000);
        long reward0 = master.Stats.RewardCurrency.Value.Quantity;
        Engine.SetValue("combat.slayerReward", 5);
        long reward1 = master.Stats.RewardCurrency.Value.Quantity;
        Check("slayer odulu x5", reward0 > 10 && Near(reward1, reward0 * 5.0, 0.02), $"1000 canli gorev canavari icin oldurme basina {reward0} -> {reward1}");
        Engine.ResetAll();
        game.ActionManager.TryStopActive(force: true);
        combat.Player.Health.Set(combat.Player.Health.Max);
        combat.Areas.SelectEnemy(s_area, s_enemy);
        if (!combat.Action.IsStarted) combat.Action.TryStart();

        // Combat loot doubling
        int lootItems = 0, lootKills = 0;
        combat.Loot.EnemyLooted += (_, a) => { lootItems += a.Items.Sum(i => i.Quantity); lootKills++; };
        Engine.SetValue("combat.god", 1);
        Engine.SetValue("combat.damage", 50);
        Engine.SetValue("speed.spawn", 10);
        Fight(combat, 20000);
        double perKill0 = (double)lootItems / Math.Max(1, lootKills);
        int kills0 = lootKills;
        lootItems = lootKills = 0;
        Engine.SetValue("combat.lootDouble", 100);
        Fight(combat, 20000);
        double perKill1 = (double)lootItems / Math.Max(1, lootKills);
        Check("cift ganimet %100", kills0 > 20 && perKill1 >= perKill0 * 1.9, $"oldurme basina esya {perKill0:0.##} -> {perKill1:0.##} ({kills0} ve {lootKills} oldurme; kemikler iki bonustan da pay aldigi icin 2 kattan fazla)");
        Engine.ResetAll();

        // Death penalty
        bool diedNow = false, lostSomething = false;
        combat.Action.PlayerDied += (_, a) => { diedNow = true; lostSomething = a.LostItem != null; };
        Engine.SetValue("combat.noDeathPenalty", 1);
        double penaltyOff = combat.Action.Stats.NoDeathPenalty.Total.Value;
        Fight(combat, 300);   // make sure a fight is running
        combat.Player.Health.Set(0);
        for (int i = 0; i < 30 && !diedNow; i++) game.PropagateTick();
        Check("olum cezasi yok", penaltyOff > 0 && diedNow && !lostSomething, $"ayar oyunda etkin ({penaltyOff}), karakter oldu: {diedNow}, esya kaybi: {lostSomething} (karakterin kusanili esyasi yok, kayip zaten olusamazdi)");
        Engine.ResetAll();
        game.ActionManager.TryStopActive(force: true);
        combat.Player.Health.Set(combat.Player.Health.Max);

        // Thieving: never stunned
        game.Entities.TryGetObjectById("MelvorBase:Thieving", out GameEntity? thEntity);
        var th = ((ThievingEntity)thEntity!).Action;
        int stuns = 0, avoided = 0;
        th.Stunned += (_, _) => stuns++;
        th.StunAvoided += (_, _) => avoided++;
        // The test character's Thieving level climbs with every run; at level 1 pickpockets fail often enough to measure.
        Actions.SetLevel(Actions.Skills(game).First(s => s.DisplayName.Text == "Thieving"), 1);
        var victim = th.ActiveRecipes.Values.First(r => th.CanThieveRecipe(r));
        void Steal(int ticks)
        {
            stuns = avoided = 0;
            for (int i = 0; i < ticks; i++)
            {
                if (!th.IsStarted) { combat.Player.Health.Set(combat.Player.Health.Max); th.TryStartActiveRecipe(victim); }
                game.PropagateTick();
            }
        }
        Steal(30000);
        int stuns0 = stuns;
        Engine.SetValue("skills.noStun", 1);
        Steal(30000);
        Check("hirsizlikta sersemlememe", stuns0 > 0 && stuns == 0 && avoided > 0, $"sersemleme {stuns0} -> {stuns}, onlenen {avoided}");
        Engine.ResetAll();
        game.ActionManager.TryStopActive(force: true);
        combat.Player.Health.Set(combat.Player.Health.Max);

        // ---------- 5e. loot filter ----------
        Out("=== 5e. Ganimet filtresi ===");
        Engine.ResetAll();
        LootFilter.Clear();
        s_area = area; s_enemy = area.Enemies[0];
        Item drop = s_enemy.Drops.FixedItems.Count > 0 ? s_enemy.Drops.FixedItems[0].Item : s_enemy.Drops.ItemTables[0].Table.Items[0].Item;
        List<Item> droppable = LootFilter.Droppable(game);
        var (enemyName, enemyDrops) = LootFilter.CurrentEnemy(game);
        Check("dusebilen esya listesi", droppable.Count > 20 && droppable.Contains(drop), $"{droppable.Count} esya; ornek: {Actions.ItemName(drop)}");
        Actions.AddItem(game, drop, 77);   // stock that was there before: the filter must leave it alone
        int dropped = 0; long soldFor = 0; int soldCount = 0;
        combat.Loot.EnemyLooted += (_, a) => dropped += a.Items.Where(i => i.Item == drop).Sum(i => i.Quantity);
        s_bank.ItemSales.ItemSold += (_, a) => { if (a.Item == drop) { soldFor += a.CurrencyGain.Quantity; soldCount += a.Quantity; } };
        Engine.SetValue("combat.god", 1);
        Engine.SetValue("combat.damage", 50);
        Engine.SetValue("speed.spawn", 10);
        (int Dropped, int BankChange) Loot(int ticks)
        {
            dropped = 0;
            int q = s_bank.Inventory.GetQuantity(drop);
            Fight(combat, ticks);
            return (dropped, s_bank.Inventory.GetQuantity(drop) - q);
        }
        var keep = Loot(8000);
        (enemyName, enemyDrops) = LootFilter.CurrentEnemy(game);
        Check("secili canavarin ganimetleri", enemyName == s_enemy.DisplayName.Text && enemyDrops.Contains(drop), $"{enemyName}: {enemyDrops.Count} esya");
        Check("filtre yokken ganimet bankaya giriyor", keep.Dropped > 0 && keep.BankChange == keep.Dropped, $"{Actions.ItemName(drop)}: {keep.Dropped} dustu, banka +{keep.BankChange}");

        int stockBefore = s_bank.Inventory.GetQuantity(drop);
        LootFilter.Set(new[] { drop }, LootRule.Destroy);
        long destroyed0 = LootFilter.Destroyed;
        var gone = Loot(8000);
        Check("ganimet filtresi: yok et", gone.Dropped > 0 && gone.BankChange == 0 && LootFilter.Destroyed - destroyed0 == gone.Dropped && s_bank.Inventory.GetQuantity(drop) == stockBefore,
              $"{gone.Dropped} dustu, {LootFilter.Destroyed - destroyed0} yok edildi, bankadaki {stockBefore} adet aynen duruyor");

        LootFilter.Set(new[] { drop }, LootRule.Sell);
        long sold0 = LootFilter.Sold; soldFor = 0; soldCount = 0;
        long price = s_bank.ItemSales.GetItemStats(drop).PricePerItem.Value;
        var sell = Loot(8000);
        Check("ganimet filtresi: sat", sell.Dropped > 0 && sell.BankChange == 0 && LootFilter.Sold - sold0 == sell.Dropped && soldCount == sell.Dropped && soldFor == price * sell.Dropped,
              $"{sell.Dropped} dustu, {soldCount} satildi, {soldFor} para (tanesi {price}), bankadaki stok degismedi");

        string lootFile = Path.Combine(Path.GetDirectoryName(typeof(CheatMenuMod).Assembly.Location)!, "lootfilter.json");
        Check("ganimet kurallari dosyaya yaziliyor", File.Exists(lootFile) && File.ReadAllText(lootFile).Contains(drop.Id), Path.GetFileName(lootFile));

        LootFilter.Set(new[] { drop }, LootRule.Keep);
        var again = Loot(8000);
        Check("ganimet filtresi: kural kalkinca normale donuyor", again.Dropped > 0 && again.BankChange == again.Dropped && LootFilter.Count == 0, $"{again.Dropped} dustu, banka +{again.BankChange}");
        Engine.ResetAll();
        game.ActionManager.TryStopActive(force: true);
        combat.Player.Health.Set(combat.Player.Health.Max);

        // ---------- 5g. bulk sell (its own mod) and locked stacks ----------
        Out("=== 5g. Toplu satis ve kilitli esyalar ===");
        Engine.ResetAll();
        game.ActionManager.TryStopActive(force: true);
        BulkSell.BulkSellMod bulk = BulkSell.BulkSellMod.Instance!;
        Item sellA = item, sellB = log;
        s_bank.Inventory.SetItemLocked(sellA, false);
        s_bank.Inventory.SetItemLocked(sellB, false);
        Actions.AddItem(game, sellA, 50);
        Actions.AddItem(game, sellB, 30);
        long priceA = BulkSell.Bank.Price(s_bank, sellA), priceB = BulkSell.Bank.Price(s_bank, sellB);
        Currency coinA = sellA.SellsFor.Currency;

        bulk.ToggleWindow();
        yield return 5;
        BulkSell.Window win = bulk.Window!;
        int sellableStacks = s_bank.Inventory.Stacks.Count(s => !s.IsLocked && !s.IsPlaceholder && s.Quantity > 0);
        Check("toplu satis penceresi", win.Visible && win.Shown.Count == sellableStacks && win.Shown.Count > 3, $"{win.Shown.Count} satilabilir yigin listelendi (bankada {s_bank.Inventory.Stacks.Count})");
        Rect2 winRect = win.Panel.GetGlobalRect();
        Vector2 winScreen = ctx.Tree.Root.GetVisibleRect().Size;
        Check("toplu satis penceresi ekrana sigiyor", winRect.Position.X >= 0 && winRect.Position.Y >= 0 && winRect.End.X <= winScreen.X && winRect.End.Y <= winScreen.Y, $"{winRect.Size.X}x{winRect.Size.Y}");
        win.SetSearch(Actions.ItemName(sellA));
        int rowA = win.Shown.ToList().FindIndex(e => e.Item == sellA);
        win.ClickRow(rowA);
        win.SetKeep(10);
        int qtyA = s_bank.Inventory.GetQuantity(sellA);
        long coins0 = s_bank.Wallet.GetQuantity(coinA);
        Out($"  ozet: {win.Summary}");
        win.PressSell();
        bool askedFirst = s_bank.Inventory.GetQuantity(sellA) == qtyA;
        Out($"  onay: {win.Status}");
        win.PressSell();
        long gotA = s_bank.Wallet.GetQuantity(coinA) - coins0;
        Check("toplu satis: isaretle, 10 tane birak, iki tikla sat", win.Marked.Count == 0 && askedFirst && s_bank.Inventory.GetQuantity(sellA) == 10 && gotA == priceA * (qtyA - 10),
              $"{Actions.ItemName(sellA)}: {qtyA} -> {s_bank.Inventory.GetQuantity(sellA)}, {gotA} para (tanesi {priceA}); ilk tik sadece sordu: {askedFirst}; durum: {win.Status}");

        s_bank.Inventory.SetItemLocked(sellB, true);
        int qtyB = s_bank.Inventory.GetQuantity(sellB);
        win.SetSearch("");
        win.Pulse();   // the window re-reads the bank once a second
        bool hiddenWhenLocked = win.Shown.All(e => e.Item != sellB);
        var lockedSale = BulkSell.Bank.Sell(s_bank, new[] { sellB }, 0);
        AutoSell.Clear();
        AutoSell.Add(sellB);
        int autoSoldLocked = AutoSell.RunOnce(game);
        AutoSell.Clear();
        Check("kilitli esya satilmiyor (toplu satis ve otomatik satis)", hiddenWhenLocked && lockedSale.Items == 0 && autoSoldLocked == 0 && s_bank.Inventory.GetQuantity(sellB) == qtyB,
              $"{Actions.ItemName(sellB)} kilitli: listede yok: {hiddenWhenLocked}, satilan {lockedSale.Items} + {autoSoldLocked}, bankada {s_bank.Inventory.GetQuantity(sellB)}");

        BulkSell.Junk.Set(new[] { sellB });
        var junkLocked = BulkSell.Junk.InBank(s_bank);
        s_bank.Inventory.SetItemLocked(sellB, false);
        var junkOpen = BulkSell.Junk.InBank(s_bank);
        coins0 = s_bank.Wallet.GetQuantity(sellB.SellsFor.Currency);
        var junkSale = bulk.SellJunkNow(game);
        long gotB = s_bank.Wallet.GetQuantity(sellB.SellsFor.Currency) - coins0;
        string junkFile = Path.Combine(Path.GetDirectoryName(typeof(BulkSell.BulkSellMod).Assembly.Location)!, "bulksell.json");
        Check("cop listesi ve 'Sell junk'", junkLocked.Count == 0 && junkOpen.Count == qtyB && junkSale.Sold == qtyB && s_bank.Inventory.GetQuantity(sellB) == 0 && gotB == priceB * qtyB
              && File.Exists(junkFile) && File.ReadAllText(junkFile).Contains(sellB.Id),
              $"{Actions.ItemName(sellB)}: kilitliyken {junkLocked.Count}, acikken {junkOpen.Count} adet; {junkSale.Sold} satildi, {gotB} para (tanesi {priceB}); dosya: {Path.GetFileName(junkFile)}");
        BulkSell.Junk.Set(Array.Empty<Item>());
        bulk.ToggleWindow();

        // The loot filter also leaves a locked stack alone.
        LootFilter.Set(new[] { drop }, LootRule.Destroy);
        s_bank.Inventory.SetItemLocked(drop, true);
        Engine.SetValue("combat.god", 1);
        Engine.SetValue("combat.damage", 50);
        Engine.SetValue("speed.spawn", 10);
        var lockedLoot = Loot(6000);
        s_bank.Inventory.SetItemLocked(drop, false);
        LootFilter.Clear();
        Check("ganimet filtresi kilitli esyaya dokunmuyor", lockedLoot.Dropped > 0 && lockedLoot.BankChange == lockedLoot.Dropped, $"{Actions.ItemName(drop)} kilitli: {lockedLoot.Dropped} dustu, banka +{lockedLoot.BankChange}");
        Engine.ResetAll();
        game.ActionManager.TryStopActive(force: true);
        combat.Player.Health.Set(combat.Player.Health.Max);

        // The two buttons appear in the bank page itself.
        GameAccess.FindGameView()!.OpenBank();
        for (int i = 0; i < 40 && bulk.BankButtons == null; i++) yield return 3;
        yield return 5;
        Control? bankButtons = bulk.BankButtons;
        Check("bankadaki dugmeler", bankButtons != null && bankButtons.IsVisibleInTree() && bankButtons.GetChildCount() == 4,
              bankButtons == null ? "dugmeler eklenmedi" : $"'{string.Join("', '", bankButtons.GetChildren().Cast<Button>().Select(x => x.Text))}' @ {bankButtons.GetGlobalRect().Position}, ust oge: {bankButtons.GetParent().GetType().Name}");

        // ---------- 5h. bank: bury all, open all ----------
        Out("=== 5h. Bankada tek tus: gom ve ac ===");
        List<Item> buriables = Actions.Items(game).Where(i => i.Capabilities.TryGet<BuryCapability>(out _)).Take(2).ToList();
        foreach (Item b in buriables) { s_bank.Inventory.SetItemLocked(b, false); Actions.AddItem(game, b, 25); }
        s_bank.Inventory.SetItemLocked(buriables[1], true);
        int lockedBones = s_bank.Inventory.GetQuantity(buriables[1]);
        var canBury = BulkSell.Bank.Buriable(s_bank);
        Currency buryCoin = canBury.Reward.Keys.First();
        long pray0 = s_bank.Wallet.GetQuantity(buryCoin);
        var didBury = BulkSell.Bank.BuryAll(s_bank);
        long prayGot = s_bank.Wallet.GetQuantity(buryCoin) - pray0;
        Check("hepsini gom", canBury.Count > 0 && didBury.Buried == canBury.Count && prayGot == canBury.Reward[buryCoin] && s_bank.Inventory.GetQuantity(buriables[0]) == 0
              && s_bank.Inventory.GetQuantity(buriables[1]) == lockedBones && BulkSell.Bank.Buriable(s_bank).Count == 0,
              $"{didBury.Buried} adet gomuldu, +{prayGot} {buryCoin.DisplayName.Text} (beklenen {canBury.Reward[buryCoin]}); kilitli {Actions.ItemName(buriables[1])}: {s_bank.Inventory.GetQuantity(buriables[1])} adet duruyor");
        s_bank.Inventory.SetItemLocked(buriables[1], false);

        Item? chest = Actions.Items(game).FirstOrDefault(i => i.Capabilities.TryGet<OpenableCapability>(out OpenableCapability? oc) && (oc.Requirements == null || oc.Requirements.AreMet) && (oc.DropTable.Items.Count > 0 || oc.AlwaysDrops.Count > 0));
        if (chest == null) Out("  acilabilir esya bulunamadi");
        else
        {
            Engine.SetValue("bank.slots", 2000);
            s_bank.Inventory.SetItemLocked(chest, false);
            int chestsBefore = s_bank.Inventory.GetQuantity(chest);
            Actions.AddItem(game, chest, 5);
            int stacksBefore = s_bank.Inventory.Stacks.Count;
            var canOpen = BulkSell.Bank.Openable(s_bank);
            var didOpen = BulkSell.Bank.OpenAll(s_bank);
            Check("hepsini ac", canOpen.Count >= chestsBefore + 5 && didOpen.Opened == canOpen.Count && didOpen.Kinds > 0 && s_bank.Inventory.GetQuantity(chest) == 0,
                  $"{Actions.ItemName(chest)} ve digerleri: {didOpen.Opened} adet acildi, {didOpen.Kinds} cesit esya cikti, banka yigini {stacksBefore} -> {s_bank.Inventory.Stacks.Count}, banka doldu: {didOpen.BankFull}");
        }
        Actions.AddItem(game, buriables[0], 12);
        for (int i = 0; i < 30; i++) yield return 3;   // the buttons refresh about once a second
        Check("'Bury all' dugmesi gerekince gorunuyor", bulk.BuryButton is { } bb && bb.IsVisibleInTree() && bb.Text.StartsWith("Bury all") && bulk.OpenButton is { } ob && !ob.Visible,
              $"gom: '{bulk.BuryButton?.Text}' gorunur: {bulk.BuryButton?.IsVisibleInTree()}; ac dugmesi gizli: {bulk.OpenButton?.Visible == false}");
        bulk.BuryButton!.EmitSignal(BaseButton.SignalName.Pressed);
        string buryQuestion = bulk.BuryButton.Text;
        bool buryWaited = s_bank.Inventory.GetQuantity(buriables[0]) > 0;
        bulk.BuryButton.EmitSignal(BaseButton.SignalName.Pressed);
        Check("'Bury all' iki tik istiyor", buryWaited && s_bank.Inventory.GetQuantity(buriables[0]) == 0, $"ilk tik: '{buryQuestion}', ikinci tik: '{bulk.BuryButton.Text}'");
        Engine.ResetAll();

        // ---------- 5i. action queue ----------
        Out("=== 5i. Is sirasi ===");
        game.ActionManager.TryStopActive(force: true);
        ActionQueue.Queue.Bind(game);
        ActionQueue.Queue.Clear();
        var skills = ActionQueue.Targets.Skills(game);
        Out($"  siranin yonetebildigi beceriler ({skills.Count}): {string.Join(", ", skills.Select(s => $"{s.Entity.DisplayName.Text} {ActionQueue.Targets.All(s.Action).Count}"))}");

        // Give the character plenty of everything for a moment, so every skill has materials to start with.
        Engine.SetValue("bank.slots", 10000);
        var lent = new List<(Item Item, int Amount)>();
        foreach (Item any in Actions.Items(game))
        {
            int got = s_bank.Inventory.AddQuantity(any, 200);
            if (got > 0) lent.Add((any, got));
        }
        // Hunting and Agility need a choice the player makes on the skill page (a trap, a path); make it the way they would.
        int trapsChosen = 0, pathsChosen = 0;
        foreach (var (_, action) in skills)
        {
            if (action is GamesByMalcs.Melvor2.Entities.Hunting.HuntingActionComponent hunt)
                foreach (var animal in hunt.Locations.Values.SelectMany(l => l.Animals))
                    if (animal.SelectedTrap == null && !animal.TraplessEnabled && animal.CompatibleTraps.Count > 0) { animal.SelectTrap(animal.CompatibleTraps[0]); if (animal.SelectedTrap != null) trapsChosen++; }
            if (action is GamesByMalcs.Melvor2.Entities.Agility.AgilityActionComponent agi)
                foreach (var course in agi.Courses.Values)
                    if (course.SelectedPath == null && course.ActivePaths.Count > 0) { course.SelectPath(course.ActivePaths.Values.First()); pathsChosen++; }
        }
        Out($"  oyuncunun yapacagi secimler yapildi: {trapsChosen} tuzak, {pathsChosen} parkur yolu");
        int startedSkills = 0, blocked = 0;
        var notStarted = new List<string>();
        foreach (var (entity, action) in skills)
        {
            List<ActionQueue.Target> all = ActionQueue.Targets.All(action);
            ActionQueue.Target? firstStarted = null;
            string lastProblem = "";
            foreach (ActionQueue.Target target in all.Take(60))
            {
                if (ActionQueue.Targets.Start(game, action, target, out lastProblem)) { firstStarted = target; break; }
                if (lastProblem.Contains("requirements")) blocked++;
            }
            bool sameNow = firstStarted != null && action.IsStarted && ActionQueue.Targets.IsDoing(action, firstStarted)
                           && game.ActionManager.ActiveActions.Count == 1 && ReferenceEquals(game.ActionManager.ActiveActions[0], action);
            for (int i = 0; i < 120; i++) game.PropagateTick();
            bool stillRuns = firstStarted != null && action.IsStarted;
            if (sameNow && stillRuns) startedSkills++;
            else notStarted.Add($"{entity.DisplayName.Text} ({(firstStarted == null ? lastProblem : "basladi ama surmedi")})");
            Out($"    {entity.DisplayName.Text}: {(firstStarted == null ? "BASLAMADI: " + lastProblem : firstStarted.Name)}");
            game.ActionManager.TryStopActive(force: true);
        }
        Check("her beceri siradan baslatilabiliyor", startedSkills == skills.Count && skills.Count >= 12,
              $"{startedSkills}/{skills.Count} beceri basladi ve ne yaptigi dogru okundu{(notStarted.Count > 0 ? "; olmayanlar: " + string.Join(", ", notStarted) : "")}");
        foreach (var (lentItem, amount) in lent) s_bank.Inventory.RemoveQuantity(lentItem, amount);
        Engine.ResetAll();
        combat.Player.Health.Set(combat.Player.Health.Max);

        // Requirements are respected: something the character may not do yet is refused and nothing starts.
        string refusedBy = "";
        foreach (var (entity, action) in skills)
        {
            foreach (ActionQueue.Target target in Enumerable.Reverse(ActionQueue.Targets.All(action)).Take(8))
            {
                if (ActionQueue.Targets.Start(game, action, target, out string why) || !why.Contains("requirements")) { game.ActionManager.TryStopActive(force: true); continue; }
                refusedBy = $"{entity.DisplayName.Text}: {target.Name} -> {why}";
                break;
            }
            if (refusedBy != "") break;
        }
        Check("sira oyunun sartlarini asmiyor", refusedBy != "" && !game.ActionManager.HasActiveAction, refusedBy == "" ? "sarti karsilanmayan bir is bulunamadi" : refusedBy + "; hicbir is baslamadi");

        // A queue of four steps, driven the way the mod drives it.
        var wcSkill = skills.First(s => s.Entity.Id == "MelvorBase:Woodcutting");
        var fmSkill = skills.First(s => s.Entity.Id == "MelvorBase:Firemaking");
        var mnSkill = skills.First(s => s.Entity.Id == "MelvorBase:Mining");
        var cbSkill = skills.First(s => s.Entity.Id == "MelvorBase:Combat");
        ActionQueue.Target queueTree = ActionQueue.Targets.All(wcSkill.Action)[0];
        ActionQueue.Target fire = ActionQueue.Targets.All(fmSkill.Action).First(x => x.Name == fmRecipe.Recipe.DisplayName.Text);
        ActionQueue.Target rock = ActionQueue.Targets.All(mnSkill.Action)[0];
        ActionQueue.Target monster = ActionQueue.Targets.All(cbSkill.Action).First(x => x.Id == area.Enemies[0].Id && x.Extra == area.Id);   // an area this character has unlocked
        s_bank.Inventory.RemoveQuantity(log, s_bank.Inventory.GetQuantity(log));
        Actions.AddItem(game, log, 12);
        ActionQueue.Step MakeStep((GameEntity Entity, ActionComponent Action) skill, ActionQueue.Target target, ActionQueue.Until until, double amount = 0, string subject = "") => new()
        {
            Entity = skill.Entity.Id, TargetId = target.Id, Extra = target.Extra, Name = $"{skill.Entity.DisplayName.Text}: {target.Name}",
            Until = until, Amount = amount, Subject = subject, SubjectName = subject,
        };
        ActionQueue.Queue.Add(MakeStep(wcSkill, queueTree, ActionQueue.Until.Minutes, 0.05));
        ActionQueue.Queue.Add(MakeStep(fmSkill, fire, ActionQueue.Until.Stops));
        ActionQueue.Queue.Add(MakeStep(mnSkill, rock, ActionQueue.Until.Level, 1, "Mining"));
        ActionQueue.Queue.Add(MakeStep(cbSkill, monster, ActionQueue.Until.Minutes, 0.05));
        var story = new List<string>();
        void Drive(double seconds, int gameTicks)
        {
            for (int i = 0; i < gameTicks; i++) game.PropagateTick();
            ActionQueue.Queue.Tick(game, seconds);
        }
        string Doing() => game.ActionManager.ActiveActions.Count == 0 ? "hicbir sey"
            : $"{game.ActionManager.ActiveActions[0].Entity.DisplayName.Text}: {ActionQueue.Targets.Current(game.ActionManager.ActiveActions[0])?.Name}";

        ActionQueue.Queue.Start();
        Drive(0.5, 0);
        bool step1 = wcSkill.Action.IsStarted && ActionQueue.Queue.Index == 0;
        story.Add($"1) {Doing()}");
        Drive(2, 120); Drive(2, 120);                       // 4 "seconds" pass: the 3-second goal is reached
        bool step1Done = !wcSkill.Action.IsStarted && ActionQueue.Queue.Index == 1;
        Drive(0.5, 0);
        bool step2 = fmSkill.Action.IsStarted;
        story.Add($"2) {Doing()}");
        for (int i = 0; i < 400 && fmSkill.Action.IsStarted; i++) Drive(0.5, 60);   // burns its 12 logs, then stops by itself
        bool step2Done = !fmSkill.Action.IsStarted && s_bank.Inventory.GetQuantity(log) == 0;
        Drive(0.5, 0);                                      // notices the stop
        Drive(0.5, 0);                                      // step 3: goal already reached, skipped without starting
        bool step3Skipped = !mnSkill.Action.IsStarted && ActionQueue.Queue.Steps[2].State.StartsWith("done");
        Drive(0.5, 0);
        bool step4 = cbSkill.Action.IsStarted && ActionQueue.Queue.Index == 3;
        story.Add($"3) atlandi ({ActionQueue.Queue.Steps[2].State}); 4) {Doing()} [{ActionQueue.Queue.Steps[3].State} | {ActionQueue.Queue.Note} | sira {ActionQueue.Queue.Index}, calisiyor {ActionQueue.Queue.Running}, can {combat.Player.Health.Current}/{combat.Player.Health.Max}]");
        Drive(2, 120); Drive(2, 120);
        bool finished = !ActionQueue.Queue.Running && !game.ActionManager.HasActiveAction;
        Check("is sirasi dort adimi sirayla yurutuyor", step1 && step1Done && step2 && step2Done && step3Skipped && step4 && finished,
              $"{string.Join(" -> ", story)} -> bitti: '{ActionQueue.Queue.Note}' [{step1},{step1Done},{step2},{step2Done},{step3Skipped},{step4},{finished}]");

        // The player takes over by hand: the queue steps aside instead of fighting them.
        ActionQueue.Queue.Start();
        Drive(0.5, 0);
        ActionQueue.Targets.Start(game, mnSkill.Action, rock, out _);
        Drive(0.5, 30);
        Check("elle baska ise gecince sira duraklar", !ActionQueue.Queue.Running && mnSkill.Action.IsStarted, $"'{ActionQueue.Queue.Note}'; karakter: {Doing()}");
        game.ActionManager.TryStopActive(force: true);

        string queueFile = Path.Combine(Path.GetDirectoryName(typeof(ActionQueue.ActionQueueMod).Assembly.Location)!, "queue.json");
        Check("sira dosyaya yaziliyor", File.Exists(queueFile) && File.ReadAllText(queueFile).Contains(queueTree.Id) && File.ReadAllText(queueFile).Contains(Engine.Character), Path.GetFileName(queueFile));

        ActionQueue.ActionQueueMod queueMod = ActionQueue.ActionQueueMod.Instance!;
        queueMod.ToggleWindow();
        yield return 5;
        Rect2 qRect = queueMod.Window!.Panel.GetGlobalRect();
        Vector2 qScreen = ctx.Tree.Root.GetVisibleRect().Size;
        Check("is sirasi penceresi", queueMod.Window.Visible && qRect.Position.X >= 0 && qRect.Position.Y >= 0 && qRect.End.X <= qScreen.X && qRect.End.Y <= qScreen.Y,
              $"{qRect.Size.X}x{qRect.Size.Y}, gereken en kucuk {queueMod.Window.Panel.GetCombinedMinimumSize().X}x{queueMod.Window.Panel.GetCombinedMinimumSize().Y}");
        // "Until I own N of an item" is filled in with what the chosen action makes.
        var smSkill = skills.First(s => s.Entity.Id == "MelvorBase:Smithing");
        ActionQueue.Target smTarget = ActionQueue.Targets.All(smSkill.Action).First(x => x.Id == smRecipe.Recipe.Id);
        Item smProduct = smRecipe.Stats.Products.Values[0].Value.Item;
        bool choseSmith = queueMod.Window.Choose(smSkill.Entity.Id, smTarget.Id);
        Item? countedSmith = queueMod.Window.CountedItem;
        bool choseTree = queueMod.Window.Choose(wcSkill.Entity.Id, queueTree.Id);
        Item? countedTree = queueMod.Window.CountedItem;
        Item? treeProduct = ActionQueue.Targets.Product(wcSkill.Action, queueTree);
        bool choseRock = queueMod.Window.Choose(mnSkill.Entity.Id, rock.Id);
        Item? countedRock = queueMod.Window.CountedItem;
        int withProduct = skills.Count(s => ActionQueue.Targets.All(s.Action).Take(40).Any(x => ActionQueue.Targets.Product(s.Action, x) != null));
        Check("sayilacak esya isin urunuyle doluyor", choseSmith && countedSmith == smProduct && choseTree && treeProduct != null && countedTree == treeProduct
              && choseRock && countedRock != null && countedRock == ActionQueue.Targets.Product(mnSkill.Action, rock) && ActionQueue.Targets.Product(cbSkill.Action, monster) == null,
              $"{smTarget.Name} -> {(countedSmith == null ? "-" : Actions.ItemName(countedSmith))}; {queueTree.Name} -> {(countedTree == null ? "-" : Actions.ItemName(countedTree))}; " +
              $"{rock.Name} -> {(countedRock == null ? "-" : Actions.ItemName(countedRock))}; savasta urun yok; urunu bilinen beceri sayisi: {withProduct}/{skills.Count}");
        queueMod.ToggleWindow();
        ActionQueue.Queue.Clear();
        combat.Player.Health.Set(combat.Player.Health.Max);

        // ---------- 5k. the planner: from "I want N of this item" to queue steps ----------
        Out("=== 5k. Plan (esyadan adimlara) ===");
        game.ActionManager.TryStopActive(force: true);
        ActionQueue.Queue.Clear();
        Engine.SetValue("bank.slots", 10000);
        var plMakers = ActionQueue.Planner.Makers(game);
        var plBySkill = plMakers.Values.SelectMany(l => l).GroupBy(m => m.Entity.DisplayName.Text).Select(g => $"{g.Key} {g.Count()}").ToList();
        Check("plan: neyin neyden yapildigi okunuyor", plMakers.Count >= 30 && plBySkill.Count >= 6,
              $"{plMakers.Count} esya, {ActionQueue.Planner.Makeable(game).Count} tanesi istenebilir; {string.Join(", ", plBySkill)}");

        var plBefore = new Dictionary<Item, int>();
        void PlKeep(Item i) { if (!plBefore.ContainsKey(i)) plBefore[i] = s_bank.Inventory.GetQuantity(i); }
        void PlRestore()
        {
            foreach (var (i, n) in plBefore)
            {
                int now = s_bank.Inventory.GetQuantity(i);
                if (now > n) s_bank.Inventory.RemoveQuantity(i, now - n);
                else if (now < n) s_bank.Inventory.AddQuantity(i, n - now);
            }
            plBefore.Clear();
        }

        // The time shown for a step is a forecast. Play ten minutes of each kind of action and compare.
        var plRates = new List<string>();
        int plRateOk = 0, plRateTried = 0;
        const int PlTicks = 36000;
        foreach (var plGroup in plMakers.Values.SelectMany(l => l).Where(m => m.Seconds > 0 && m.Main).GroupBy(m => m.Entity.Id))
        {
            ActionQueue.Maker? plM = plGroup.FirstOrDefault(m => ActionQueue.Targets.CanStart(m.Action, m.Target, out _));
            if (plM == null) { plRates.Add($"{plGroup.First().Entity.DisplayName.Text}: acik is yok"); continue; }
            PlKeep(plM.Product);
            foreach (var (plCost, plQty) in plM.Costs) { PlKeep(plCost); s_bank.Inventory.AddQuantity(plCost, plQty * 4000); }
            if (!ActionQueue.Targets.Start(game, plM.Action, plM.Target, out string plWhy))
            {
                plRates.Add($"{plM.Name}: baslamadi ({plWhy})");
                PlRestore();
                continue;
            }
            int plHad = s_bank.Inventory.GetQuantity(plM.Product);
            for (int i = 0; i < PlTicks; i++) game.PropagateTick();
            int plGot = s_bank.Inventory.GetQuantity(plM.Product) - plHad;
            double plExpected = PlTicks / 60.0 / plM.Seconds * plM.PerAction;
            plRateTried++;
            // The forecast may be a little cautious (bonus damage, doubling), but it must not promise more than the game gives.
            bool plClose = plGot > 0 && plGot / plExpected >= 0.8 && plGot / plExpected <= 1.6;
            if (plClose) plRateOk++;
            plRates.Add($"{plM.Name}: tahmin {plExpected:0}, gercek {plGot} ({plGot / plExpected:0.00}x){(plClose ? "" : " <-- UYMUYOR")}");
            game.ActionManager.TryStopActive(force: true);
            PlRestore();
        }
        Check("plan: sure tahmini gercege yakin (10 dakikalik oyun)", plRateTried >= 4 && plRateOk == plRateTried, string.Join("; ", plRates));

        // A plan to try: three or more steps, everything unlocked, nothing the player has to bring, the quickest such.
        ActionQueue.PlanResult? plPlan = null;
        int plCandidates = 0;
        foreach (Item plItem in ActionQueue.Planner.Makeable(game))
        {
            ActionQueue.PlanResult plTry = ActionQueue.Planner.Compute(game, plItem, s_bank.Inventory.GetQuantity(plItem) + 3);
            if (plTry.Steps.Count < 3 || plTry.Supply.Count > 0 || plTry.Steps.Any(l => l.Problem != "" || l.Maker!.Seconds <= 0 || !l.Maker.Certain)) continue;
            plCandidates++;
            if (plPlan == null || plTry.Seconds < plPlan.Seconds) plPlan = plTry;
        }
        if (plPlan == null) Check("plan: denenecek bir plan bulundu", false, "uc adimli, kilitsiz bir plan yok");
        else
        {
            string PlShow(ActionQueue.PlanResult r) => string.Join(" -> ", r.Steps.Select(l => $"{l.Maker!.Name} [{l.Need} {l.ItemName}, bankada {l.Have}, {l.Actions} islem]"));
            Out($"  secilen plan ({plCandidates} aday icinden): {plPlan.Own} {Actions.ItemName(plPlan.Item)} = {PlShow(plPlan)}; tahmin {ActionQueue.Planner.Time(plPlan.Seconds)}");

            // The arithmetic, redone here from the recipes.
            var plExpect = new Dictionary<Item, long>();
            foreach (ActionQueue.PlanLine l in plPlan.Steps)
                foreach (var (plCost, plQty) in l.Maker!.Costs) plExpect[plCost] = plExpect.GetValueOrDefault(plCost) + l.Actions * plQty;
            bool plRootLast = plPlan.Steps.Last().Item == plPlan.Item && plPlan.Steps.Last().Need == plPlan.Own;
            bool plCounts = plPlan.Steps.All(l => l.Missing == Math.Max(0, l.Need - l.Have) && l.Actions == (long)Math.Ceiling(l.Missing / l.Maker!.PerAction));
            bool plNeeds = plPlan.Steps.Where(l => l.Item != plPlan.Item).All(l => plExpect.GetValueOrDefault(l.Item) == l.Need);
            bool plOrder = Enumerable.Range(0, plPlan.Steps.Count).All(i => plPlan.Steps[i].Maker!.Costs.All(c => plPlan.Steps.FindIndex(x => x.Item == c.Item) < i));
            Check("plan: miktarlar ve sira dogru", plRootLast && plCounts && plNeeds && plOrder,
                  $"istenen esya en sonda: {plRootLast}; eksik ve islem sayilari: {plCounts}; malzeme toplamlari tarifle ayni: {plNeeds}; malzeme, kullanildigi adimdan once: {plOrder}");

            // What is already in the bank is taken off.
            ActionQueue.PlanLine plMid = plPlan.Steps[^2];
            PlKeep(plMid.Item);
            s_bank.Inventory.AddQuantity(plMid.Item, (int)plMid.Missing);
            ActionQueue.PlanResult plWithStock = ActionQueue.Planner.Compute(game, plPlan.Item, plPlan.Own);
            bool plStock = plWithStock.Steps.All(l => l.Item != plMid.Item) && plWithStock.Steps.Count < plPlan.Steps.Count && plWithStock.Steps.Last().Item == plPlan.Item;
            PlRestore();
            Check("plan: bankadaki esya dusuluyor", plStock, $"bankaya {plMid.Missing} {plMid.ItemName} konunca: {plPlan.Steps.Count} adim -> {plWithStock.Steps.Count} adim ({PlShow(plWithStock)})");

            // The window: show the plan, add it, then ask again.
            queueMod.ToggleWindow();
            yield return 5;
            ActionQueue.PlanResult? plShown = queueMod.Window!.PlanFor(plPlan.Item.Id, plPlan.Own);
            yield return 5;
            Rect2 plRect = queueMod.Window.Panel.GetGlobalRect();
            Vector2 plMin = queueMod.Window.Panel.GetCombinedMinimumSize();
            Vector2 plScreen = ctx.Tree.Root.GetVisibleRect().Size;
            int plRows = queueMod.Window.PlanRows;
            string plNote = queueMod.Window.PlanNote;
            Check("plan penceresi", plShown != null && plRows == plPlan.Steps.Count && plNote.Contains("step") && plRect.Position.X >= 0 && plRect.Position.Y >= 0
                  && plRect.End.X <= plScreen.X && plRect.End.Y <= plScreen.Y && plMin.X <= plRect.Size.X + 0.5f && plMin.Y <= plRect.Size.Y + 0.5f,
                  $"{plRows} satir; '{plNote}'; pencere {plRect.Size.X}x{plRect.Size.Y}, gereken en kucuk {plMin.X}x{plMin.Y}");

            queueMod.Window.PressAddPlan();
            bool plQueued = ActionQueue.Queue.Steps.Count == plPlan.Steps.Count && ActionQueue.Queue.Steps.Zip(plPlan.Steps).All(x =>
                x.First.Until == ActionQueue.Until.Item && x.First.Subject == x.Second.Item.Id && x.First.Amount == x.Second.Need
                && x.First.TargetId == x.Second.Maker!.Target.Id && x.First.Entity == x.Second.Maker.Entity.Id);
            ActionQueue.PlanResult? plAgain = queueMod.Window.PlanFor(plPlan.Item.Id, plPlan.Own);
            bool plNoDouble = plAgain != null && plAgain.Steps.Count == 0 && plAgain.Problem != "";
            // A second plan must not count materials the first plan is going to use up.
            ActionQueue.PlanLine plRaw = plPlan.Steps[0];
            PlKeep(plRaw.Item);
            s_bank.Inventory.AddQuantity(plRaw.Item, (int)plRaw.Missing);
            ActionQueue.PlanResult plMore = ActionQueue.Planner.Compute(game, plPlan.Item, plPlan.Own + 3);
            ActionQueue.PlanLine? plMoreRaw = plMore.Steps.FirstOrDefault(l => l.Item == plRaw.Item);
            bool plShared = plMore.Steps.Count > 0 && plMore.Steps.Last().Missing == 3 && plMoreRaw != null && plMoreRaw.Have < plRaw.Need;
            PlRestore();
            Check("plan siraya ekleniyor; ikinci plan birincinin yapacagini ve harcayacagini sayiyor", plQueued && plNoDouble && plShared,
                  $"siradaki adim: {ActionQueue.Queue.Steps.Count}; ayni plan tekrar istenince: '{plAgain?.Problem}'; 3 fazlasi istenince eksik {plMore.Steps.LastOrDefault()?.Missing}, " +
                  $"bankada {plRaw.Need} {plRaw.ItemName} varken ikinci planin saydigi: {plMoreRaw?.Have.ToString() ?? "-"}");
            queueMod.Window.ShowStepForm();
            queueMod.ToggleWindow();

            // And the queue really gets there.
            foreach (ActionQueue.PlanLine l in plPlan.Steps) PlKeep(l.Item);
            int plXpLevel = 0;
            ActionQueue.Queue.Start();
            int plLoops = 0;
            for (; plLoops < 6000 && ActionQueue.Queue.Running; plLoops++) Drive(0.5, 60);
            int plOwned = s_bank.Inventory.GetQuantity(plPlan.Item);
            string plStates = string.Join(" | ", ActionQueue.Queue.Steps.Select(s => $"{s.Name}: {s.State}"));
            Check("plan bastan sona yuruyor", !ActionQueue.Queue.Running && plOwned >= plPlan.Own && plXpLevel == 0,
                  $"bankada {plOwned}/{plPlan.Own} {Actions.ItemName(plPlan.Item)}; oyun suresi {plLoops} sn (tahmin {plPlan.Seconds:0} sn); {plStates}; '{ActionQueue.Queue.Note}'");
            game.ActionManager.TryStopActive(force: true);
            ActionQueue.Queue.Clear();
            PlRestore();
        }
        Engine.ResetAll();
        combat.Player.Health.Set(combat.Player.Health.Max);

        // ---------- 5j. slayer tasks as a queue step; a death pauses the queue ----------
        Out("=== 5j. Slayer gorevi adimi ve olum ===");
        Engine.ResetAll();
        game.ActionManager.TryStopActive(force: true);
        List<ActionQueue.Target> slayerTargets = ActionQueue.Targets.All(cbSkill.Action).Where(x => x.Extra == ActionQueue.Targets.Slayer).ToList();
        ActionQueue.Target slayerTarget = slayerTargets.First(x => x.Id == master.Data.Id);
        int tasksDone = 0;
        ((SlayerEntity)slEntity!).Tasks.TaskCompleted += (_, _) => tasksDone++;
        bool gameAutoSlayer = master.AutoSlayerEnabled;
        master.AutoSlayerEnabled = false;   // the game's own switch is off: the queue has to follow the task by itself
        Engine.SetValue("combat.god", 1);
        Engine.SetValue("combat.damage", 1000);
        Engine.SetValue("combat.accuracy", 100);
        Engine.SetValue("speed.spawn", 20);
        ActionQueue.Queue.Clear();
        ActionQueue.Queue.Add(MakeStep(cbSkill, slayerTarget, ActionQueue.Until.Minutes, 600));
        ActionQueue.Queue.Start();
        Drive(0.5, 0);
        bool onTask = cbSkill.Action.IsStarted && master.CurrentTask != null && combat.Areas.SelectedEnemy == master.CurrentTask;
        var fought = new List<string> { combat.Areas.SelectedEnemy?.DisplayName.Text ?? "-" };
        for (int i = 0; i < 6000 && tasksDone < 3 && ActionQueue.Queue.Running; i++)
        {
            Drive(0.5, 30);
            string name = combat.Areas.SelectedEnemy?.DisplayName.Text ?? "-";
            if (fought[^1] != name) fought.Add(name);
        }
        string slayerState = ActionQueue.Queue.Steps[0].State;
        bool followed = ActionQueue.Queue.Running ? combat.Areas.SelectedEnemy == master.CurrentTask : slayerState.StartsWith("done:");
        Check("slayer gorevi adimi gorevi takip ediyor", slayerTargets.Count > 0 && onTask && tasksDone >= 1 && followed,
              $"{slayerTarget.Name}: {tasksDone} gorev bitti, savasilanlar: {string.Join(" -> ", fought)}; simdiki gorev: {master.CurrentTask?.DisplayName.Text}; sira calisiyor: {ActionQueue.Queue.Running}; adim: {slayerState}");
        master.AutoSlayerEnabled = gameAutoSlayer;
        ActionQueue.Queue.Clear();
        Engine.ResetAll();
        game.ActionManager.TryStopActive(force: true);
        combat.Player.Health.Set(combat.Player.Health.Max);

        // Death: even with "start over at the end" switched on, the queue must not walk back into the fight.
        ActionQueue.Queue.Add(MakeStep(cbSkill, monster, ActionQueue.Until.Minutes, 600));
        ActionQueue.Queue.SetRepeat(true);
        ActionQueue.Queue.Start();
        Drive(0.5, 0);
        Drive(0.5, 120);
        bool wasFighting = cbSkill.Action.IsStarted;
        combat.Player.Health.Set(0);
        for (int i = 0; i < 40 && ActionQueue.Queue.Running; i++) Drive(0.5, 30);
        for (int i = 0; i < 8; i++) Drive(0.5, 30);
        Check("olunce sira duraklar, savasa geri donmez", wasFighting && !ActionQueue.Queue.Running && !cbSkill.Action.IsStarted && ActionQueue.Queue.Note.Contains("died"),
              $"savasiyordu: {wasFighting}; sira calisiyor: {ActionQueue.Queue.Running}; savas suruyor: {cbSkill.Action.IsStarted}; not: '{ActionQueue.Queue.Note}'");
        ActionQueue.Queue.SetRepeat(false);
        ActionQueue.Queue.Clear();
        combat.Player.Health.Set(combat.Player.Health.Max);

        // ---------- 5f. applying at once is safe: hammer the settings and compare with the game's own numbers ----------
        Out("=== 5f. Aninda uygulamanin guvenligi ===");
        var rng = new Random(7);
        int hpBase = combat.Player.Health.Max, slotsBase = s_bank.Inventory.MaxStacks;
        for (int i = 0; i < 500; i++)
        {
            Engine.SetValue("combat.maxHp", rng.Next(1, 101));
            Engine.SetValue("bank.slots", rng.Next(0, 201) * 50);
            Engine.SetValue("combat.god", i % 2);
            if (i % 50 == 0) game.PropagateTick();
        }
        Engine.SetValue("combat.maxHp", 5);
        Engine.SetValue("bank.slots", 500);
        Check("500 hizli degisiklikten sonra deger tam", Near(combat.Player.Health.Max, hpBase * 5.0, 0.02) && s_bank.Inventory.MaxStacks == slotsBase + 500,
              $"can {hpBase} -> {combat.Player.Health.Max} (x5), banka slotu {slotsBase} -> {s_bank.Inventory.MaxStacks} (+500)");
        Engine.ResetAll();
        Check("sifirlayinca iz kalmiyor", combat.Player.Health.Max == hpBase && s_bank.Inventory.MaxStacks == slotsBase && Engine.ActiveCount() == 0,
              $"can {combat.Player.Health.Max}, banka slotu {s_bank.Inventory.MaxStacks}, acik ayar {Engine.ActiveCount()}");
        for (int i = 0; i < 600; i++) game.PropagateTick();

        List<string> xpIds = Features.All.Where(f => f.Column == "xp").Select(f => f.Id).ToList();
        List<string> speedIds = Features.All.Where(f => f.Column == "speed").Select(f => f.Id).ToList();
        Engine.SetValues(xpIds, 3);
        Check("toplu XP ayari", xpIds.Count == 25 && xpIds.All(id => Engine.GetValue(id) == 3) && speedIds.All(id => Engine.GetValue(id) == 1),
              $"{xpIds.Count} becerinin XP carpani 3, hizlar degismedi");
        Engine.SetValues(speedIds, 4);
        Check("toplu hiz ayari", speedIds.Count == 17 && speedIds.All(id => Engine.GetValue(id) == 4) && xpIds.All(id => Engine.GetValue(id) == 3),
              $"{speedIds.Count} becerinin hizi 4, XP carpanlari degismedi");
        Engine.ResetAll();

        // ---------- 6. the menu itself ----------
        Out("=== 6. F8 menusu ===");
        CheatMenuMod mod = CheatMenuMod.Instance!;
        mod.ToggleMenu();
        yield return 10;
        Check("menu acilma", mod.Menu is { Visible: true }, "pencere olusturuldu ve gorunur");
        Vector2 screen = ctx.Tree.Root.GetVisibleRect().Size;
        Control panel = mod.Menu!.Panel;
        Rect2 rect = panel.GetGlobalRect();
        Check("menu ekrana sigiyor", rect.Position.X >= 0 && rect.Position.Y >= 0 && rect.End.X <= screen.X && rect.End.Y <= screen.Y,
              $"ekran {screen.X}x{screen.Y}, pencere {rect.Size.X}x{rect.Size.Y} @ ({rect.Position.X},{rect.Position.Y}), en kucuk boyut {panel.GetCombinedMinimumSize().X}x{panel.GetCombinedMinimumSize().Y}");
        Check("menu sayfalari", mod.Menu.PageCount == 8, string.Join(", ", Enumerable.Range(0, mod.Menu.PageCount).Select(mod.Menu.PageTitle)));
        for (int i = 0; i < mod.Menu.PageCount; i++)
        {
            mod.Menu.ShowPage(i);
            yield return 3;
            Control page = mod.Menu.PageControl(i);
            Rect2 pr = page.GetGlobalRect();
            bool inside = pr.Position.X >= rect.Position.X && pr.End.X <= rect.End.X + 0.5f && pr.End.Y <= rect.End.Y + 0.5f;
            float need = page is ScrollContainer sc ? sc.GetChild<Control>(0).GetCombinedMinimumSize().X : page.GetCombinedMinimumSize().X;
            Check($"sayfa '{mod.Menu.PageTitle(i)}' pencereye sigiyor", page.Visible && inside && need <= pr.Size.X + 0.5f,
                  $"{page.FindChildren("*", "PanelContainer", true, false).Count} satir, gereken genislik {need:0} / mevcut {pr.Size.X:0}");
        }
        Out("  metinler: " + string.Join(" | ", panel.FindChildren("*", "Button", true, false).Cast<Button>().Select(x => x.Text).Where(x => x != "").Distinct().Take(40)));

        // Controls hand their value to the game at once (there is no Apply button).
        mod.Menu.ShowPage(0);
        SpinBox slotsBox = (SpinBox)mod.Menu.PageControl(0).FindChildren("*", "SpinBox", true, false)[0];
        int slotsNow = s_bank.Inventory.MaxStacks;
        slotsBox.Value = 100;
        Check("kutuya yazilan deger aninda uygulaniyor", Engine.GetValue("bank.slots") == 100 && s_bank.Inventory.MaxStacks == slotsNow + 100, $"banka slotu {slotsNow} -> {s_bank.Inventory.MaxStacks}");
        HSlider slotsSlider = (HSlider)mod.Menu.PageControl(0).FindChildren("*", "HSlider", true, false)[0];
        slotsSlider.EmitSignal(Slider.SignalName.DragStarted);
        slotsSlider.Value = 300;
        bool heldBack = Engine.GetValue("bank.slots") == 100 && slotsBox.Value == 300;
        slotsSlider.EmitSignal(Slider.SignalName.DragEnded, true);
        Check("kaydirici birakilinca uygulaniyor", heldBack && Engine.GetValue("bank.slots") == 300 && s_bank.Inventory.MaxStacks == slotsNow + 300,
              $"surukleme sirasinda oyundaki deger degismedi: {heldBack}; birakinca banka slotu {s_bank.Inventory.MaxStacks}");
        Button resetOne = mod.Menu.PageControl(0).FindChildren("*", "Button", true, false).Cast<Button>().First(x => x.Text == "Reset");
        resetOne.EmitSignal(BaseButton.SignalName.Pressed);
        Check("satirdaki Reset dugmesi", Engine.GetValue("bank.slots") == 0 && s_bank.Inventory.MaxStacks == slotsNow && slotsBox.Value == 0, $"banka slotu {s_bank.Inventory.MaxStacks}");

        int perSkill = Enumerable.Range(0, mod.Menu.PageCount).First(i => mod.Menu.PageTitle(i) == "Per skill");
        List<Button> applyAll = mod.Menu.PageControl(perSkill).FindChildren("*", "Button", true, false).Cast<Button>().Where(x => x.Text == "Apply to all").ToList();
        applyAll[0].EmitSignal(BaseButton.SignalName.Pressed);
        bool xpOnly = xpIds.All(id => Engine.GetValue(id) == 2) && speedIds.All(id => Engine.GetValue(id) == 1);
        applyAll[1].EmitSignal(BaseButton.SignalName.Pressed);
        Check("'Apply to all' dugmeleri (XP ve hiz ayri)", applyAll.Count == 2 && xpOnly && speedIds.All(id => Engine.GetValue(id) == 2),
              $"once yalniz XP: {xpOnly}; sonra hiz: {speedIds.Count(id => Engine.GetValue(id) == 2)}/17");
        Engine.ResetAll();

        // Settings file: written a moment later, and other characters in it are left alone.
        string settingsFile = Path.Combine(Path.GetDirectoryName(typeof(CheatMenuMod).Assembly.Location)!, "settings.json");
        var onDisk = File.Exists(settingsFile)
            ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, double>>>(File.ReadAllText(settingsFile))!
            : new Dictionary<string, Dictionary<string, double>>();
        onDisk["__test_other"] = new Dictionary<string, double> { ["xp.multiplier"] = 7 };
        File.WriteAllText(settingsFile, System.Text.Json.JsonSerializer.Serialize(onDisk));
        Engine.SetValue("xp.multiplier", 9);
        mod.Menu.Hide();
        onDisk = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, double>>>(File.ReadAllText(settingsFile))!;
        bool merged = onDisk.TryGetValue("__test_other", out var other) && other["xp.multiplier"] == 7
                      && onDisk.TryGetValue(Engine.Character, out var mine) && mine.GetValueOrDefault("xp.multiplier") == 9;
        Check("ayar dosyasi: diger karakterlere dokunulmuyor", merged, $"dosyadaki karakterler: {string.Join(", ", onDisk.Keys)}");
        onDisk.Remove("__test_other");
        File.WriteAllText(settingsFile, System.Text.Json.JsonSerializer.Serialize(onDisk, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        Engine.ResetAll();
        mod.ToggleMenu();
        yield return 3;
        mod.ToggleMenu();
        Check("menu kapanma", mod.Menu is { Visible: false }, "gizlendi");

        Engine.ResetAll();
        game.ActionManager.TryStopActive(force: true);

        // ---------- 7. the game's offline calculation is left alone ----------
        IEnumerator<int> offlineRun = OfflineSpeed(game, ctx);
        while (offlineRun.MoveNext()) yield return offlineRun.Current;
    }
}
