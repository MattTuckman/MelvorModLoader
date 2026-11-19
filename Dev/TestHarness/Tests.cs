extern alias mt;
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
            if (pick.ItemCount == 5 && pick.GetItemText(1).StartsWith("I own"))
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

    // ------------------------------------------------------------------------------------------------
    // Multitask 0.2: an on/off switch per slot, nothing to pick. With a slot on, the action before the latest
    // one keeps running. Everything below is done the way a player does it: the game's own start calls,
    // the game's own Stop, the window's switches.
    static IEnumerator<int> MultiTests(Game game, ModContext ctx)
    {
        Out("=== 5l. Multitask (son islerin birlikte calismasi) ===");
        Engine.Bind(game);
        Engine.ResetAll();
        Engine.SetValue("bank.slots", 10000);
        game.ActionManager.TryStopActive(force: true);
        ActionQueue.Queue.Bind(game);
        ActionQueue.Queue.Clear();
        mt::Multitask.Slots.Bind(game);
        mt::Multitask.MultitaskMod mod = mt::Multitask.MultitaskMod.Instance!;
        if (mod.Window is not { Visible: true }) mod.ToggleWindow();
        yield return 5;
        mt::Multitask.SlotWindow win = mod.Window!;
        for (int i = 0; i < mt::Multitask.Slots.Count; i++) if (mt::Multitask.Slots.IsOn(i)) win.PressSwitch(i);
        var makers = ActionQueue.Planner.Makers(game).Values.SelectMany(l => l).ToList();
        List<ActionQueue.Maker> Open(string skill) => makers.Where(m => m.Entity.DisplayName.Text == skill && m.Main && ActionQueue.Targets.CanStart(m.Action, m.Target, out _))
            .GroupBy(m => m.Target.Id).Select(g => g.First()).ToList();
        int Qty(Item i) => s_bank.Inventory.GetQuantity(i);
        void SetQty(Item i, int n) { int now = Qty(i); if (now > n) s_bank.Inventory.RemoveQuantity(i, now - n); else if (now < n) s_bank.Inventory.AddQuantity(i, n - now); }
        void Ticks(int n) { for (int i = 0; i < n; i++) game.PropagateTick(); }
        string Running() => game.ActionManager.ActiveActions.Count == 0 ? "hicbir sey"
            : string.Join(" + ", game.ActionManager.ActiveActions.Select(a => $"{a.Entity.DisplayName.Text}: {ActionQueue.Targets.Current(a)?.Name ?? "?"}"));
        string Kept() => string.Join(" > ", mt::Multitask.Slots.Recent.Select(k => $"{k.Title} [{k.State}]"));
        int Count() => game.ActionManager.ActiveActions.Count;
        // The mod looks twice a second; here that look is made by hand, with a clock that jumps ahead.
        double clock = Time.GetTicksMsec() / 1000.0;
        void Look(double seconds = 60) { clock += seconds; mt::Multitask.Slots.Tick(game, clock); }
        bool Start(ActionQueue.Maker m) => ActionQueue.Targets.Start(game, m.Action, m.Target, out _);   // what choosing it in the game does
        bool Doing(ActionQueue.Maker m) => ActionQueue.Targets.IsDoing(m.Action, m.Target);

        ActionQueue.Maker wood = Open("Woodcutting")[0], ore = Open("Mining")[0], fishSpot = Open("Fishing")[0];
        List<ActionQueue.Maker> rocks = Open("Mining");
        var mining = (GamesByMalcs.Melvor2.Entities.Mining.MiningActionComponent)ore.Action;
        const int T = 18000;   // five minutes of game time

        // ---- alone, for comparison
        Start(wood); int w0 = Qty(wood.Product); Ticks(T); int woodAlone = Qty(wood.Product) - w0;
        Start(ore); int o0 = Qty(ore.Product); Ticks(T); int oreAlone = Qty(ore.Product) - o0;
        game.ActionManager.TryStopActive(force: true);

        // ---- 1. both slots off: the plain game, nothing is started for the player
        Start(ore); Look();
        Start(wood); Look(); Look();
        Check("iki yuva da kapaliyken oyun aynen eskisi gibi", Count() == 1 && wood.Action.IsStarted && !ore.Action.IsStarted && mt::Multitask.Slots.Starts == 0 && win.LineText(0).Contains(wood.Target.Name),
              $"calisan: {Running()}; pencere: '{win.LineText(0)}' / '{win.StatusText}'");

        // ---- 2. slot 1 on: start something new and the one before keeps running
        win.PressSwitch(0);
        Look();
        Start(ore);                                  // the player goes mining
        bool stoppedByGame = !wood.Action.IsStarted && Count() == 1;
        Look();
        bool two = Count() == 2 && wood.Action.IsStarted && ore.Action.IsStarted;
        bool latestLast = two && ReferenceEquals(game.ActionManager.ActiveActions[^1], ore.Action);   // the bottom bar shows the last of the list
        w0 = Qty(wood.Product); o0 = Qty(ore.Product);
        Ticks(T);
        int woodBoth = Qty(wood.Product) - w0, oreBoth = Qty(ore.Product) - o0;
        Check("yuva 1 acikken yeni ise gecince onceki is de suruyor", stoppedByGame && two && latestLast && Near(woodBoth, woodAlone, 0.25) && Near(oreBoth, oreAlone, 0.5),
              $"{Kept()}; 5 dakikada {woodBoth} {wood.Product.DisplayName.GetPluralText(1)} (tek basina {woodAlone}) ve {oreBoth} {ore.Product.DisplayName.GetPluralText(1)} (tek basina {oreAlone}); alt cubukta son is: {latestLast}");
        yield return 10;

        // ---- 3. a third action with one slot: the oldest drops out
        Start(fishSpot);
        Look();
        Check("tek yuvayla ucuncu ise gecince en eski is dusuyor", Count() == 2 && fishSpot.Action.IsStarted && ore.Action.IsStarted && !wood.Action.IsStarted, Kept());

        // ---- 4. slot 2 on: the last three
        win.PressSwitch(1);
        Start(wood);
        Look();
        Check("iki yuva acikken son uc is birlikte calisiyor", Count() == 3 && wood.Action.IsStarted && fishSpot.Action.IsStarted && ore.Action.IsStarted, Kept());

        // ---- 5. the player's report: another rock is chosen on the mining page, then a new action is started.
        //         The skill does the new rock from then on; the old one is not forced back.
        if (rocks.Count >= 2)
        {
            mining.SelectRock(mining.Rocks.First(r => r.Key.Id == rocks[1].Target.Id).Value);   // what a click on the other rock does
            Look();
            bool switchedRock = Doing(rocks[1]) && Count() == 3 && mt::Multitask.Slots.Recent[0].TargetId == rocks[1].Target.Id;
            string afterClick = Kept();
            Start(wood);                                                                       // "a second task": the same tree again
            Look();
            bool stillNewRock = Doing(rocks[1]) && !Doing(ore) && Count() == 3;
            Check("baska kaya secilince o kaya kaliyor, eskisi geri gelmiyor", switchedRock && stillNewRock, $"{rocks[1].Name} secilince: {afterClick}; baska ise gecince: {Running()}");
            mining.SelectRock(mining.Rocks.First(r => r.Key.Id == ore.Target.Id).Value);         // back to the first rock for the checks below
            Look();
        }
        else Out("  (ikinci acik kaya yok, kaya degistirme denemesi atlandi)");

        // ---- 6. the game's own Stop on one of them drops it; the others go on
        fishSpot.Action.TryStop();
        Look(); Look();
        Check("oyundaki Stop dugmesi o isi birakiyor, digerleri suruyor", !fishSpot.Action.IsStarted && Count() == 2 && wood.Action.IsStarted && ore.Action.IsStarted && mt::Multitask.Slots.Recent.Count == 2, Kept());

        // ---- 7. starting the same action again must not lose the others
        Start(wood);
        Look();
        Check("ayni isi yeniden baslatmak digerlerini dusurmuyor", Count() == 2 && wood.Action.IsStarted && ore.Action.IsStarted, Kept());

        // ---- 8. switching the slots off stops what no longer fits
        win.PressSwitch(1);
        Look();
        bool stillTwo = Count() == 2;
        win.PressSwitch(0);
        Look();
        Check("yuvalar kapatilinca fazlalik duruyor", stillTwo && Count() == 1 && !mt::Multitask.Slots.IsOn(0) && !mt::Multitask.Slots.IsOn(1), $"{Running()}; pencere: '{win.StatusText}'");
        game.ActionManager.TryStopActive(force: true);
        Look();

        // ---- 9. fishing, then cooking the catch: the kitchen waits for fish and goes on by itself
        ActionQueue.Maker? cook = null, fish = null;
        foreach (ActionQueue.Maker c in makers.Where(m => m.Entity.DisplayName.Text == "Cooking" && m.Costs.Count == 1 && ActionQueue.Targets.CanStart(m.Action, m.Target, out _)))
        {
            fish = makers.FirstOrDefault(f => f.Entity.DisplayName.Text == "Fishing" && f.Product == c.Costs[0].Item && ActionQueue.Targets.CanStart(f.Action, f.Target, out _));
            if (fish != null) { cook = c; break; }
        }
        if (cook == null || fish == null) Out("  balik + pisirme: uygun tarif bulunamadi");
        else
        {
            Item rawFish = fish.Product;
            int rawBefore = Qty(rawFish), cookedBefore = Qty(cook.Product);
            SetQty(rawFish, 3);
            win.PressSwitch(0);
            Start(fish); Look();
            Start(cook); Look();                      // the player starts cooking the three they have
            bool bothAtFirst = Count() == 2 && fish.Action.IsStarted && cook.Action.IsStarted;
            int restarts = mt::Multitask.Slots.Starts, caught = 0, lastRaw = Qty(rawFish);
            var states = new List<string>();
            for (int second = 0; second < 600; second++)   // ten minutes, one look per second like the mod does
            {
                Ticks(60);
                int rawNow = Qty(rawFish);
                if (rawNow > lastRaw) caught += rawNow - lastRaw;
                Look(1);
                lastRaw = Qty(rawFish);
                string state = mt::Multitask.Slots.Recent.FirstOrDefault(k => k.Entity == cook.Entity.Id)?.State ?? "(dustu)";
                if (states.Count == 0 || states[^1] != state) states.Add(state);
            }
            restarts = mt::Multitask.Slots.Starts - restarts;
            int cooked = Qty(cook.Product) - cookedBefore, rawLeft = Qty(rawFish);
            Check("balik tutarken pisirme: malzeme bitince bekliyor, birikince devam ediyor", bothAtFirst && fish.Action.IsStarted && cooked > 3 && restarts >= 2 && restarts <= Math.Max(3, caught / 4) && rawLeft < 15
                  && states.Any(s => s.StartsWith("waiting")) && !states.Contains("(dustu)"),
                  $"{fish.Name} + {cook.Name}: 10 dakikada {caught} tutuldu, {cooked} pisti, {rawLeft} cig kaldi; pisirme {restarts} kez yeniden basladi; gorulen durumlar: {string.Join(" / ", states.Distinct().Take(5))}");
            game.ActionManager.TryStopActive(force: true);
            Look();
            win.PressSwitch(0);
            SetQty(rawFish, rawBefore);
        }

        // ---- 10. a fight is dropped when the character dies, and nothing walks back in
        game.Entities.TryGetObjectById("MelvorBase:Combat", out GameEntity? combatEntity);
        var combat = (CombatEntity)combatEntity!;
        CombatArea area = game.Data.GetDataRegistryOutput<CombatArea>().GetAllObjects().First(a => a.Enemies.Count > 0 && a.UnlockRequirements.AreMet);
        combat.Player.Health.Set(combat.Player.Health.Max);
        win.PressSwitch(0);
        Start(ore); Look();
        combat.Areas.SelectEnemy(area, area.Enemies[0]);
        if (!combat.Action.IsStarted) combat.Action.TryStart();
        Look();
        bool fighting = combat.Action.IsStarted && ore.Action.IsStarted && Count() == 2;
        Ticks(120);
        combat.Player.Health.Set(0);
        for (int i = 0; i < 20 && combat.Action.IsStarted; i++) Ticks(30);
        for (int i = 0; i < 6; i++) { Look(); Ticks(60); }
        string deathNote = mt::Multitask.Slots.Note;
        Check("karakter olunce savas birakiliyor, geri donulmuyor", fighting && !combat.Action.IsStarted && ore.Action.IsStarted && mt::Multitask.Slots.Recent.All(k => k.Entity != "MelvorBase:Combat"),
              $"savasiyordu: {fighting}; olumden sonra: {Running()}; '{deathNote}'");
        combat.Player.Health.Set(combat.Player.Health.Max);

        // ---- 11. the Action Queue beside it: the queue's steps come and go, what the player was doing stays
        ActionQueue.Step QStep(ActionQueue.Maker m, ActionQueue.Until until, double amount = 0) => new()
        {
            Entity = m.Entity.Id, TargetId = m.Target.Id, Extra = m.Target.Extra, Name = m.Name, Until = until, Amount = amount,
        };
        if (cook == null) Out("  is sirasi + yuva: pisirilecek tarif bulunamadi");
        else
        {
            Item fuel = cook.Costs[0].Item;
            int fuelBefore = Qty(fuel);
            SetQty(fuel, 30 * cook.Costs[0].Quantity);
            Start(ore); Look();                                    // the player is mining, then lets the queue work
            ActionQueue.Queue.SetSideBySide(false);                // one step after another, the player's own action beside them
            ActionQueue.Queue.Add(QStep(wood, ActionQueue.Until.Minutes, 0.05));
            ActionQueue.Queue.Add(QStep(cook, ActionQueue.Until.Stops));
            ActionQueue.Queue.Add(QStep(fishSpot, ActionQueue.Until.Minutes, 0.05));
            o0 = Qty(ore.Product);
            ActionQueue.Queue.Start();
            var seen = new List<string>();
            int loops = 0, most = 0;
            bool draggedAlong = false;
            for (; loops < 600 && ActionQueue.Queue.Running; loops++)
            {
                Ticks(60);
                ActionQueue.Queue.Tick(game, 2);
                Look();
                most = Math.Max(most, Count());
                string doing = Running();
                if (seen.Count == 0 || seen[^1] != doing) seen.Add(doing);
                if (ActionQueue.Queue.Index >= 1 && wood.Action.IsStarted) draggedAlong = true;   // a finished step must not be kept going
            }
            Look();
            string states = string.Join(" | ", ActionQueue.Queue.Steps.Select(s => s.State));
            bool finished = !ActionQueue.Queue.Running && ActionQueue.Queue.Note.Contains("finished") && ActionQueue.Queue.Steps.All(s => s.State.StartsWith("done"));
            ActionQueue.Queue.SetSideBySide(true);
            Check("yan yana KAPALIYKEN: adimlar sirayla, oyuncunun isi yaninda suruyor, biten adimlar suruklenmiyor", finished && ore.Action.IsStarted && Qty(ore.Product) > o0 && most == 2 && !draggedAlong && Count() == 1,
                  $"gorulenler: {string.Join(" -> ", seen.Take(7))}; adimlar: {states}; not: '{ActionQueue.Queue.Note}'; ayni anda en cok {most} is; bu sirada kazilan: {Qty(ore.Product) - o0}; sonunda: {Running()}");
            ActionQueue.Queue.Clear();
            SetQty(fuel, fuelBefore);
        }

        // ---- 11b. the player's report: "I planned herring and pressed Start. It wants 100 fish first and does not
        //           cook at the same time; I added a third step and nothing runs beside it." With slots on, the
        //           queue's own steps must run side by side: fish, cook the catch, and mine, all at once.
        if (cook != null && fish != null)
        {
            game.ActionManager.TryStopActive(force: true);
            Look();
            ActionQueue.Queue.Clear();
            if (!mt::Multitask.Slots.IsOn(1)) win.PressSwitch(1);          // both slots on: three at once
            Look();
            Item rawFish = fish.Product, cooked = cook.Product;
            int rawWas = Qty(rawFish), cookedWas = Qty(cooked), oreWas = Qty(ore.Product);
            SetQty(rawFish, 18);                                           // what the player had
            ActionQueue.ActionQueueMod queueMod = ActionQueue.ActionQueueMod.Instance!;
            if (queueMod.Window is not { Visible: true }) queueMod.ToggleWindow();
            yield return 5;
            ActionQueue.QueueWindow qw = queueMod.Window!;
            ActionQueue.PlanResult? herring = qw.PlanFor(cooked.Id, 60, more: true);   // "Plan an item", Make 60
            qw.PressAddPlan();
            long rawGoal = ActionQueue.Queue.Steps.FirstOrDefault(s => s.Subject == rawFish.Id)?.Amount is double g ? (long)g : 0;
            ActionQueue.Queue.Add(new ActionQueue.Step                    // the third step the player added by hand
            {
                Entity = ore.Entity.Id, TargetId = ore.Target.Id, Name = ore.Name, Until = ActionQueue.Until.Item,
                Amount = oreWas + 200, Subject = ore.Product.Id, SubjectName = ore.Product.DisplayName.GetPluralText(1),
            });
            string sideButton = qw.SideText;
            int lanes = ActionQueue.Queue.Lanes;
            ActionQueue.Queue.Start();
            int most = 0, secondsAllThree = 0, loops = 0, fishedTotal = 0, lastRaw = Qty(rawFish);
            var cookStates = new List<string>();
            string midHeadline = "";
            for (; loops < 1500 && ActionQueue.Queue.Running; loops++)   // one look per game second, like the mods do
            {
                Ticks(60);
                int rawNow = Qty(rawFish);
                if (rawNow > lastRaw) fishedTotal += rawNow - lastRaw;
                ActionQueue.Queue.Tick(game, 1);
                Look(1);
                lastRaw = Qty(rawFish);
                most = Math.Max(most, Count());
                if (fish.Action.IsStarted && cook.Action.IsStarted && ore.Action.IsStarted)
                {
                    secondsAllThree++;
                    if (midHeadline == "") midHeadline = ActionQueue.Queue.Headline;
                    if (secondsAllThree == 12 && DisplayServer.GetName() != "headless")
                    {
                        string shotDir = Path.Combine(Path.GetDirectoryName(System.Environment.GetEnvironmentVariable("MML_TEST_OUT")!)!, "shots");
                        Directory.CreateDirectory(shotDir);
                        yield return 20;
                        Out($"  goruntu sira-5-yan-yana.png: {ctx.Tree.Root.GetTexture().GetImage().SavePng(Path.Combine(shotDir, "sira-5-yan-yana.png"))}");
                    }
                }
                string state = ActionQueue.Queue.Steps.FirstOrDefault(s => s.Entity == cook.Entity.Id)?.State ?? "";
                state = System.Text.RegularExpressions.Regex.Replace(state, @"[\d.]+", "#");
                if (cookStates.Count == 0 || cookStates[^1] != state) cookStates.Add(state);
            }
            int cookedNow = Qty(cooked) - cookedWas, rawLeft = Qty(rawFish), oreGot = Qty(ore.Product) - oreWas;
            string endStates = string.Join(" | ", ActionQueue.Queue.Steps.Select(s => s.State));
            bool finished = !ActionQueue.Queue.Running && ActionQueue.Queue.Note.Contains("finished");
            // Fishing must stop at what the plan asked for, although the kitchen kept eating the fish: not twice as many.
            bool notTwice = fishedTotal <= (rawGoal - 18) + 12;
            Check("yan yana: balik tutarken pisiriyor, ucuncu adim da ayni anda calisiyor", herring != null && herring.Steps.Count == 2 && lanes == 3 && finished
                  && most == 3 && secondsAllThree > 20 && cookedNow >= 55 && oreGot >= 200 && notTwice && !game.ActionManager.HasActiveAction,
                  $"dugme: '{sideButton}'; ayni anda en cok {most} is, ucu birlikte {secondsAllThree} sn ('{midHeadline}'); {loops} sn surdu; " +
                  $"plan {rawGoal} cig balik istedi (bankada 18 vardi): {fishedTotal} tutuldu, {cookedNow} pisti, {rawLeft} cig kaldi; {oreGot} maden; pisirmenin durumlari: {string.Join(" / ", cookStates.Distinct().Take(6))}; sonunda: {endStates}");

            // Remove all while three steps run: all three stop.
            ActionQueue.Queue.Clear();
            SetQty(rawFish, 30);
            ActionQueue.Queue.Add(QStep(fish, ActionQueue.Until.Minutes, 30));
            ActionQueue.Queue.Add(QStep(cook, ActionQueue.Until.Stops));
            ActionQueue.Queue.Add(QStep(ore, ActionQueue.Until.Minutes, 30));
            ActionQueue.Queue.Start();
            for (int i = 0; i < 3; i++) { ActionQueue.Queue.Tick(game, 1); Look(1); Ticks(60); }
            int runningBefore = Count();
            // The player starts something by hand: the queue pauses instead of fighting.
            Start(wood);
            ActionQueue.Queue.Tick(game, 1); Look(1);
            bool pausedForPlayer = !ActionQueue.Queue.Running && wood.Action.IsStarted;
            string pauseNote = ActionQueue.Queue.Note;
            ActionQueue.Queue.Start();
            for (int i = 0; i < 3; i++) { ActionQueue.Queue.Tick(game, 1); Look(1); Ticks(60); }
            int runningAgain = Count();
            ActionQueue.Queue.Clear();                                     // "Remove all"
            Look(1);
            Check("yan yana: elle ise gecince sira duraklar; Remove all ucunu de durdurur", runningBefore == 3 && pausedForPlayer && runningAgain == 3 && !fish.Action.IsStarted && !cook.Action.IsStarted && !ore.Action.IsStarted,
                  $"baslayinca {runningBefore} is; elle odun kesince: '{pauseNote}'; yeniden baslatinca {runningAgain} is; Remove all sonrasi calisan: {Running()}");

            // Side by side switched off in the queue window: one step after another again.
            qw.PressSide();
            int lanesOff = ActionQueue.Queue.Lanes;
            string sideOff = qw.SideText;
            qw.PressSide();
            // Slots off: the button is not even shown and the queue is the plain one.
            win.PressSwitch(1); win.PressSwitch(0);
            Look();
            ActionQueue.Queue.Add(QStep(wood, ActionQueue.Until.Minutes, 1));   // any change redraws the window
            int lanesPlain = ActionQueue.Queue.Lanes;
            string sidePlain = qw.SideText;
            ActionQueue.Queue.Clear();
            Check("yan yana dugmesi: kapatilabiliyor; yuvalar kapaliyken hic gorunmuyor", sideButton.Contains("3 at once") && lanesOff == 1 && sideOff.Contains("OFF") && lanesPlain == 1 && sidePlain == "(hidden)",
                  $"acik: '{sideButton}'; kapatilinca: '{sideOff}' ({lanesOff} serit); yuvalar kapaliyken: {sidePlain} ({lanesPlain} serit)");
            win.PressSwitch(0);                                            // slot 1 back on for the checks below
            Look();
            qw.ShowStepForm();
            queueMod.ToggleWindow();
            game.ActionManager.TryStopActive(force: true);
            Look();
            SetQty(rawFish, rawWas);
        }

        // ---- 12. the queue needs the very skill the player was using: the queue gets it, nothing is forced back
        if (rocks.Count >= 2)
        {
            Start(wood); Look();
            Start(ore); Look();                                    // wood kept, mining the latest
            ActionQueue.Queue.Add(QStep(rocks[1], ActionQueue.Until.Minutes, 0.05));
            ActionQueue.Queue.Start();
            ActionQueue.Queue.Tick(game, 0.5);
            Look();
            bool queueHasIt = Doing(rocks[1]) && wood.Action.IsStarted && Count() == 2;
            for (int i = 0; i < 8 && ActionQueue.Queue.Running; i++) { Ticks(60); ActionQueue.Queue.Tick(game, 2); Look(); }
            Look(); Look();
            Check("sira oyuncunun kullandigi beceriyi isteyince: sira aliyor, bitince zorla geri gelen yok", queueHasIt && !ActionQueue.Queue.Running && !ore.Action.IsStarted && wood.Action.IsStarted && Count() == 1,
                  $"sira calisirken: {(queueHasIt ? rocks[1].Name + " + odun" : "YANLIS")}; sira bitince: {Running()}; {Kept()}");
            ActionQueue.Queue.Clear();
        }

        // ---- 13. the game's own offline calculation
        Start(ore); Look();
        Start(wood); Look();
        w0 = Qty(wood.Product); o0 = Qty(ore.Product);
        const double Hours = 0.25;
        game.RequestOfflineHours(Hours);
        yield return 30;   // frames only come back once the game has finished calculating
        for (int i = 0; i < 10; i++)
        {
            Button? close = VisibleButton(ctx.Tree.Root, "Confirm", "Close", "Continue", "OK", "Okay");
            if (close == null) break;
            close.EmitSignal(BaseButton.SignalName.Pressed);
            yield return 20;
        }
        int woodOff = Qty(wood.Product) - w0, oreOff = Qty(ore.Product) - o0;
        double scale = Hours * 3600 * 60 / T;
        Check("cevrimdisi hesapta iki is de ilerliyor", Near(woodOff, woodAlone * scale, 0.3) && Near(oreOff, oreAlone * scale, 0.5) && wood.Action.IsStarted && ore.Action.IsStarted,
              $"{Hours} saat: {woodOff} {wood.Product.DisplayName.GetPluralText(1)} (beklenen ~{woodAlone * scale:0}), {oreOff} {ore.Product.DisplayName.GetPluralText(1)} (beklenen ~{oreAlone * scale:0}); sonrasinda: {Running()}");

        // ---- 14. the window
        if (!win.Visible) mod.ToggleWindow();
        win.PressSwitch(1);
        Start(fishSpot);
        Look();
        yield return 10;
        Rect2 rect = win.Panel.GetGlobalRect();
        Vector2 screen = ctx.Tree.Root.GetVisibleRect().Size;
        string l0 = win.LineText(0), l1 = win.LineText(1), l2 = win.LineText(2);
        Check("Multitask penceresi", win.Visible && rect.Position.X >= 0 && rect.Position.Y >= 0 && rect.End.X <= screen.X && rect.End.Y <= screen.Y
              && win.SwitchText(0) == "ON" && win.SwitchText(1) == "ON" && l0.Contains(fishSpot.Target.Name) && l0.Contains("running") && l1.Contains("running") && l2.Contains("running") && Count() == 3,
              $"{rect.Size.X}x{rect.Size.Y}; 1: {l0} | 2: {l1} | 3: {l2}; alt yazi: '{win.StatusText}'");
        if (DisplayServer.GetName() != "headless")
        {
            string shotDir = Path.Combine(Path.GetDirectoryName(System.Environment.GetEnvironmentVariable("MML_TEST_OUT")!)!, "shots");
            Directory.CreateDirectory(shotDir);
            for (int i = 0; i < 120; i++) { game.PropagateTick(); if (i % 4 == 0) yield return 1; }
            Out($"  goruntu coklu-1-pencere.png: {ctx.Tree.Root.GetTexture().GetImage().SavePng(Path.Combine(shotDir, "coklu-1-pencere.png"))}");
        }

        string slotFile = Path.Combine(Path.GetDirectoryName(typeof(mt::Multitask.MultitaskMod).Assembly.Location)!, "multitask.json");
        Check("yuvalar dosyaya yaziliyor", File.Exists(slotFile) && File.ReadAllText(slotFile).Contains(ore.Target.Id) && File.ReadAllText(slotFile).Contains(Engine.Character), Path.GetFileName(slotFile));

        for (int i = 0; i < mt::Multitask.Slots.Count; i++) if (mt::Multitask.Slots.IsOn(i)) win.PressSwitch(i);
        mod.ToggleWindow();
        game.ActionManager.TryStopActive(force: true);
        Look();
        Engine.ResetAll();
        combat.Player.Health.Set(combat.Player.Health.Max);
    }

    // Closing the game with two actions running, and opening it again (two runs: MML_ONLY=multi-save, then multi-load).
    static IEnumerator<int> MultiSave(Game game)
    {
        Out("=== Multitask: iki is calisirken oyunu kapatma ===");
        Engine.Bind(game);
        Engine.ResetAll();
        game.ActionManager.TryStopActive(force: true);
        mt::Multitask.Slots.Bind(game);
        var makers = ActionQueue.Planner.Makers(game).Values.SelectMany(l => l).ToList();
        ActionQueue.Maker First(string skill) => makers.First(m => m.Entity.DisplayName.Text == skill && m.Main && ActionQueue.Targets.CanStart(m.Action, m.Target, out _));
        ActionQueue.Maker wood = First("Woodcutting"), ore = First("Mining"), fishing = First("Fishing");
        mt::Multitask.Slots.SetOn(0, true);
        mt::Multitask.Slots.SetOn(1, true);
        ActionQueue.Targets.Start(game, ore.Action, ore.Target, out _);
        yield return 60;   // the mod itself notices (no help from the test)
        ActionQueue.Targets.Start(game, wood.Action, wood.Target, out _);
        yield return 90;
        ActionQueue.Targets.Start(game, fishing.Action, fishing.Target, out _);
        yield return 90;
        Check("kapatmadan once uc is calisiyor (modun kendi saatiyle)", wood.Action.IsStarted && ore.Action.IsStarted && fishing.Action.IsStarted && game.ActionManager.ActiveActions.Count == 3,
              string.Join(" + ", game.ActionManager.ActiveActions.Select(a => a.Entity.DisplayName.Text)));
    }

    static IEnumerator<int> MultiLoad(Game game)
    {
        Out("=== Multitask: oyunu yeniden acinca ===");
        // Nothing is started here: what runs now is what the game itself brought back from the save.
        var running = game.ActionManager.ActiveActions.Select(a => $"{a.Entity.DisplayName.Text}: {ActionQueue.Targets.Current(a)?.Name ?? "?"}").ToList();
        int startsByMod = mt::Multitask.Slots.Starts;
        string kept = string.Join(" > ", mt::Multitask.Slots.Recent.Select(k => $"{k.Title} [{k.State}]"));
        Check("oyun yeniden acilinca uc is de suruyor", running.Count == 3 && mt::Multitask.Slots.IsOn(0) && mt::Multitask.Slots.IsOn(1) && mt::Multitask.Slots.Recent.Count == 3 && mt::Multitask.Slots.Recent.All(k => k.State == "running") && startsByMod == 0,
              $"calisan: {string.Join(" + ", running)}; mod: {kept}; modun yeniden baslatmasi gerekti mi: {(startsByMod == 0 ? "hayir, oyun kendi acti" : startsByMod + " kez")}");
        int before = s_bank.Inventory.Stacks.Sum(s => s.Quantity);
        for (int i = 0; i < 3600; i++) game.PropagateTick();
        Check("yeniden acildiktan sonra ucu de calisiyor", s_bank.Inventory.Stacks.Sum(s => s.Quantity) > before && game.ActionManager.ActiveActions.Count == 3, $"bir dakikada +{s_bank.Inventory.Stacks.Sum(s => s.Quantity) - before} esya");
        for (int i = 0; i < mt::Multitask.Slots.Count; i++) mt::Multitask.Slots.SetOn(i, false);
        game.ActionManager.TryStopActive(force: true);
        yield return 5;
    }

    // ------------------------------------------------------------------------------------------------
    // Action Queue 0.4.0: saved queues, share codes, and the goal that counts down.
    static string PackCode(string json)
    {
        using var buffer = new MemoryStream();
        using (var deflate = new System.IO.Compression.DeflateStream(buffer, System.IO.Compression.CompressionLevel.SmallestSize, leaveOpen: true))
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(json);
            deflate.Write(bytes, 0, bytes.Length);
        }
        return "AQ1:" + Convert.ToBase64String(buffer.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    static IEnumerator<int> SavedQueueTests(Game game, ModContext ctx)
    {
        Out("=== 5m. Kayitli siralar, paylasim kodu, asagi sayan hedef ===");
        Engine.Bind(game);
        Engine.ResetAll();
        Engine.SetValue("bank.slots", 10000);
        game.ActionManager.TryStopActive(force: true);
        ActionQueue.Queue.Bind(game);
        ActionQueue.Queue.Clear();
        foreach (string old in ActionQueue.Queue.PresetNames) ActionQueue.Queue.DeletePreset(old, out _);
        var makers = ActionQueue.Planner.Makers(game).Values.SelectMany(l => l).ToList();
        ActionQueue.Maker Open(string skill) => makers.First(m => m.Entity.DisplayName.Text == skill && m.Main && ActionQueue.Targets.CanStart(m.Action, m.Target, out _));
        int Qty(Item i) => s_bank.Inventory.GetQuantity(i);
        void SetQty(Item i, int n) { int now = Qty(i); if (now > n) s_bank.Inventory.RemoveQuantity(i, now - n); else if (now < n) s_bank.Inventory.AddQuantity(i, n - now); }
        void Ticks(int n) { for (int i = 0; i < n; i++) game.PropagateTick(); }
        ActionQueue.Step QStep(ActionQueue.Maker m, ActionQueue.Until until, double amount = 0, Item? subject = null) => new()
        {
            Entity = m.Entity.Id, TargetId = m.Target.Id, Extra = m.Target.Extra, Name = m.Name, Until = until, Amount = amount,
            Subject = subject?.Id ?? "", SubjectName = subject?.DisplayName.GetPluralText(1) ?? "",
        };
        ActionQueue.Maker wood = Open("Woodcutting"), ore = Open("Mining"), fishSpot = Open("Fishing");
        const string A = "Wood then stone", B = "Fishing loop";

        // ---- 0. the planner: "make N more" when the bank already holds plenty (what the player ran into)
        ActionQueue.ActionQueueMod planMod = ActionQueue.ActionQueueMod.Instance!;
        if (planMod.Window is not { Visible: true }) planMod.ToggleWindow();
        yield return 5;
        Item plenty = wood.Product;
        int plentyBefore = Qty(plenty);
        SetQty(plenty, 673);
        ActionQueue.PlanResult? total = planMod.Window!.PlanFor(plenty.Id, 100, more: false);
        int totalRows = planMod.Window.PlanRows;
        string totalNote = planMod.Window.PlanNote;
        ActionQueue.PlanResult? make = planMod.Window.PlanFor(plenty.Id, 100, more: true);
        int makeRows = planMod.Window.PlanRows;
        string makeNote = planMod.Window.PlanNote;
        yield return 5;
        Rect2 planRect = planMod.Window.Panel.GetGlobalRect();
        Vector2 planMin = planMod.Window.Panel.GetCombinedMinimumSize();
        Check("plan: bankada cok varken 'Make' ile ustune yapiliyor", total != null && total.Steps.Count == 0 && totalRows == 1 && totalNote.Contains("Make")
              && make != null && make.Steps.Count >= 1 && make.Own == 773 && make.Steps.Last().Missing == 100 && makeRows == make.Steps.Count && makeNote.StartsWith("To make 100 more")
              && planMin.X <= planRect.Size.X + 0.5f && planMin.Y <= planRect.Size.Y + 0.5f,
              $"bankada 673 varken 'Own in total 100': {total?.Steps.Count} adim, tabloda aciklama satiri: {totalRows == 1} ('{totalNote}'); 'Make 100': {make?.Steps.Count} adim, hedef {make?.Own} ('{makeNote}'); pencere {planRect.Size.X}x{planRect.Size.Y}, en kucuk {planMin.X}x{planMin.Y}");
        // The plan on screen is out of date (the bank changed a moment ago): pressing Add must use the bank as it is now.
        ActionQueue.Maker? staleCook = makers.FirstOrDefault(m => m.Entity.DisplayName.Text == "Cooking" && m.Costs.Count == 1 && m.Costs[0].Quantity == 1
            && makers.Any(f => f.Entity.DisplayName.Text == "Fishing" && f.Product == m.Costs[0].Item && ActionQueue.Targets.CanStart(f.Action, f.Target, out _))
            && ActionQueue.Targets.CanStart(m.Action, m.Target, out _));
        if (staleCook == null) Out("  bayat plan denemesi: uygun tarif bulunamadi");
        else
        {
            Item rawItem = staleCook.Costs[0].Item, cooked = staleCook.Product;
            int rawWas = Qty(rawItem);
            SetQty(rawItem, 0);
            ActionQueue.Queue.Clear();
            ActionQueue.PlanResult? withoutRaw = planMod.Window.PlanFor(cooked.Id, 10, more: true);
            int stepsShown = withoutRaw?.Steps.Count ?? 0;       // gather, then cook
            SetQty(rawItem, 500);                                // ...and now there is plenty, before the window has looked again
            planMod.Window.PressAddPlan();
            bool onlyCooking = ActionQueue.Queue.Steps.Count == 1 && ActionQueue.Queue.Steps[0].TargetId == staleCook.Target.Id;
            string coveredRow = planMod.Window.PlanRowText(1);
            ActionQueue.Queue.Start();
            ActionQueue.Queue.Tick(game, 0.5);
            bool cookingNow = staleCook.Action.IsStarted;
            ActionQueue.Queue.Clear();
            game.ActionManager.TryStopActive(force: true);
            // A step whose goal is already met says what it found.
            SetQty(rawItem, 500);
            ActionQueue.Queue.Add(new ActionQueue.Step { Entity = "MelvorBase:Fishing", TargetId = fishSpot.Target.Id, Name = fishSpot.Name, Until = ActionQueue.Until.Item, Amount = 200, Subject = rawItem.Id, SubjectName = rawItem.DisplayName.GetPluralText(1) });
            ActionQueue.Queue.Start();
            ActionQueue.Queue.Tick(game, 0.5);
            string skippedState = ActionQueue.Queue.Steps[0].State;
            string diary = Path.Combine(Path.GetDirectoryName(typeof(ActionQueue.ActionQueueMod).Assembly.Location)!, "queue-log.txt");
            bool diaryHasIt = File.Exists(diary) && File.ReadAllText(diary).Contains("already holds 500");
            Check("plan: ekrandaki plan bayatsa eklerken yeniden hesaplaniyor; gereksiz adim nedenini soyluyor",
                  stepsShown == 2 && onlyCooking && cookingNow && coveredRow.Contains("enough") && skippedState.StartsWith("done: the bank already holds 500") && diaryHasIt,
                  $"malzeme yokken plan {stepsShown} adim; malzeme gelince Add: sirada {(onlyCooking ? "yalniz pisirme" : "YANLIS")}; pencerede: '{coveredRow}'; hedefi zaten tamam adim: '{skippedState}'; kayit dosyasi: {diaryHasIt}");
            ActionQueue.Queue.Clear();
            game.ActionManager.TryStopActive(force: true);
            SetQty(rawItem, rawWas);
        }
        planMod.Window.ShowStepForm();
        planMod.ToggleWindow();
        SetQty(plenty, plentyBefore);

        // ---- 1. two queues kept under a name
        ActionQueue.Queue.Add(QStep(wood, ActionQueue.Until.Minutes, 5));
        ActionQueue.Queue.Add(QStep(ore, ActionQueue.Until.Minutes, 5));
        bool savedA = ActionQueue.Queue.SavePreset("   Wood then stone  ", out _);   // spaces around a name are dropped
        ActionQueue.Queue.Clear();
        bool emptyRefused = !ActionQueue.Queue.SavePreset("Empty", out string emptyWhy);
        ActionQueue.Queue.Add(QStep(fishSpot, ActionQueue.Until.Minutes, 5));
        ActionQueue.Queue.SetRepeat(true);
        bool savedB = ActionQueue.Queue.SavePreset(B, out _);
        bool noNameRefused = !ActionQueue.Queue.SavePreset("   ", out string noNameWhy);
        Check("siralar adla kaydediliyor", savedA && savedB && emptyRefused && noNameRefused && ActionQueue.Queue.PresetNames.SequenceEqual(new[] { B, A })
              && ActionQueue.Queue.Name == B && !ActionQueue.Queue.Modified,
              $"kayitli: {string.Join(", ", ActionQueue.Queue.PresetNames)}; yuklu olan: '{ActionQueue.Queue.Name}'; bos sira: '{emptyWhy}'; adsiz: '{noNameWhy}'");

        // ---- 2. loading brings the steps back exactly; unsaved changes are not lost
        ActionQueue.Queue.LoadPreset(A, out _);
        bool exact = ActionQueue.Queue.Steps.Count == 2 && ActionQueue.Queue.Steps[0].TargetId == wood.Target.Id && ActionQueue.Queue.Steps[1].TargetId == ore.Target.Id
                     && ActionQueue.Queue.Steps[0].Amount == 5 && !ActionQueue.Queue.Repeat && ActionQueue.Queue.Name == A && !ActionQueue.Queue.Modified;
        ActionQueue.Queue.Add(QStep(fishSpot, ActionQueue.Until.Minutes, 1));
        bool marked = ActionQueue.Queue.Modified;
        ActionQueue.Queue.LoadPreset(B, out string loadMessage);
        bool stashed = ActionQueue.Queue.GetPreset(ActionQueue.Queue.AutoName)?.Steps.Count == 3 && ActionQueue.Queue.GetPreset(A)!.Steps.Count == 2
                       && ActionQueue.Queue.Steps.Count == 1 && ActionQueue.Queue.Repeat && ActionQueue.Queue.Name == B;
        Check("yukleyince adimlar aynen geliyor; kaydedilmemis degisiklik kaybolmuyor", exact && marked && stashed, $"aynen geldi: {exact}; degisiklik isaretlendi: {marked}; '{loadMessage}'");

        // ---- 3. switching while the queue runs
        ActionQueue.Queue.Start();
        ActionQueue.Queue.Tick(game, 0.5);
        bool wasFishing = fishSpot.Action.IsStarted && ActionQueue.Queue.Running;
        ActionQueue.Queue.LoadPreset(A, out string flyMessage);
        bool keptRunning = ActionQueue.Queue.Running;
        ActionQueue.Queue.Tick(game, 0.5);
        bool switched = wood.Action.IsStarted && !fishSpot.Action.IsStarted && ActionQueue.Queue.Index == 0 && ActionQueue.Queue.Steps[0].State.StartsWith("running");
        Check("sira calisirken baska siraya geciliyor", wasFishing && keptRunning && switched, $"once balik tutuyordu: {wasFishing}; '{flyMessage}'; simdi odun kesiyor: {wood.Action.IsStarted}");
        ActionQueue.Queue.Pause("test");
        game.ActionManager.TryStopActive(force: true);

        // ---- 3b. the player's report: "Remove all" while a step runs. What the queue started must stop with it.
        ActionQueue.Queue.Clear();
        game.ActionManager.TryStopActive(force: true);
        ActionQueue.Queue.Add(QStep(ore, ActionQueue.Until.Item, 1000000, ore.Product));
        ActionQueue.Queue.Add(QStep(wood, ActionQueue.Until.Minutes, 5));
        ActionQueue.Queue.Start();
        ActionQueue.Queue.Tick(game, 0.5);
        bool minedForQueue = ore.Action.IsStarted;
        ActionQueue.Queue.Clear();                                   // the "Remove all" button
        bool stoppedWithIt = !ore.Action.IsStarted && !game.ActionManager.HasActiveAction && !ActionQueue.Queue.Running;
        string clearedNote = ActionQueue.Queue.Note;
        // Taking out only the running step: its action stops and the next step takes over.
        ActionQueue.Queue.Add(QStep(ore, ActionQueue.Until.Item, 1000000, ore.Product));
        ActionQueue.Queue.Add(QStep(wood, ActionQueue.Until.Minutes, 5));
        ActionQueue.Queue.Start();
        ActionQueue.Queue.Tick(game, 0.5);
        ActionQueue.Queue.RemoveAt(0);                                // "Remove" on the running step
        bool stepStopped = !ore.Action.IsStarted;
        ActionQueue.Queue.Tick(game, 0.5);
        bool nextTookOver = wood.Action.IsStarted && ActionQueue.Queue.Running && ActionQueue.Queue.Steps.Count == 1;
        // But what the player started by hand is theirs: emptying a paused queue leaves it alone.
        ActionQueue.Targets.Start(game, fishSpot.Action, fishSpot.Target, out _);
        ActionQueue.Queue.Tick(game, 0.5);                            // the queue notices and pauses
        bool pausedForPlayer = !ActionQueue.Queue.Running;
        ActionQueue.Queue.Clear();
        bool playersActionKept = fishSpot.Action.IsStarted;
        Check("sira bosaltilinca siranin baslattigi is de duruyor", minedForQueue && stoppedWithIt && stepStopped && nextTookOver && pausedForPlayer && playersActionKept,
              $"Remove all: kazma durdu: {stoppedWithIt} ('{clearedNote}'); calisan adim silinince durdu: {stepStopped}, sonraki adim basladi: {nextTookOver}; oyuncunun elle baslattigi ise dokunulmadi: {playersActionKept}");
        game.ActionManager.TryStopActive(force: true);
        ActionQueue.Queue.Clear();
        ActionQueue.Queue.SetRepeat(false);

        // ---- 4. a share code brings a queue back exactly
        string code = ActionQueue.Queue.ExportCode(B)!;
        ActionQueue.Queue.DeletePreset(B, out _);
        bool gone = ActionQueue.Queue.GetPreset(B) == null;
        string messy = "  " + code.Insert(20, "\n   ") + " \r\n";   // pasted from a chat: line breaks and spaces
        bool imported = ActionQueue.Queue.ImportCode(messy, out string importedName, out string importMessage);
        ActionQueue.Preset? back = ActionQueue.Queue.GetPreset(B);
        bool same = back != null && back.Repeat && back.Steps.Count == 1 && back.Steps[0].Entity == fishSpot.Entity.Id && back.Steps[0].TargetId == fishSpot.Target.Id
                    && back.Steps[0].Until == ActionQueue.Until.Minutes && back.Steps[0].Amount == 5 && back.Steps[0].Name == fishSpot.Name;
        bool twice = ActionQueue.Queue.ImportCode(code, out string secondName, out _) && secondName == B + " (2)";
        Check("paylasim kodu sirayi aynen geri getiriyor", code.StartsWith("AQ1:") && code.Length < 400 && gone && imported && importedName == B && same && twice,
              $"kod {code.Length} karakter: {code[..Math.Min(36, code.Length)]}...; '{importMessage}'; ikinci kez yuklenince adi: '{secondName}'");
        ActionQueue.Queue.DeletePreset(B + " (2)", out _);

        // ---- 5. codes that must be refused, and one for things this game does not have
        int before = ActionQueue.Queue.PresetNames.Count;
        string many = "{\"N\":\"x\",\"S\":[" + string.Join(",", Enumerable.Repeat("{\"E\":\"a\",\"T\":\"b\",\"U\":0,\"A\":0}", 300)) + "]}";
        var bad = new (string What, string Code)[]
        {
            ("bos", ""), ("duz yazi", "hello there"), ("yalniz onek", "AQ1:"), ("gecersiz harfler", "AQ1:!!!!$$$$"),
            ("yarisi kesik", code[..(code.Length / 2)]), ("baska onek", "AQ2:" + code[4..]), ("30.000 harf", new string('A', 30000)),
            ("adimsiz", PackCode("{\"N\":\"x\",\"S\":[]}")), ("bilinmeyen hedef turu", PackCode("{\"N\":\"x\",\"S\":[{\"E\":\"a\",\"T\":\"b\",\"U\":9,\"A\":1}]}")),
            ("eksi miktar", PackCode("{\"N\":\"x\",\"S\":[{\"E\":\"a\",\"T\":\"b\",\"U\":1,\"A\":-5}]}")), ("yanlis bicim", PackCode("[1,2,3]")),
            ("300 adim", PackCode(many)), ("sisirilmis (2 MB bosluk)", PackCode(new string(' ', 2_000_000) + "{}")),
        };
        var accepted = bad.Where(b => ActionQueue.Queue.ImportCode(b.Code, out _, out _)).Select(b => b.What).ToList();
        bool untouched = ActionQueue.Queue.PresetNames.Count == before;
        bool foreign = ActionQueue.Queue.ImportCode(PackCode("{\"N\":\"From another game\",\"S\":[{\"E\":\"Mod:Nope\",\"T\":\"x\",\"N\":\"Nope: x\",\"U\":0,\"A\":0}]}"), out string foreignName, out string foreignMessage);
        Check("bozuk kodlar reddediliyor", accepted.Count == 0 && untouched && foreign && foreignMessage.Contains("does not have"),
              $"{bad.Length} bozuk koddan kabul edilen: {(accepted.Count == 0 ? "yok" : string.Join(", ", accepted))}; kayitli siralar degismedi: {untouched}; bu oyunda olmayan is: '{foreignMessage}'");
        ActionQueue.Queue.DeletePreset(foreignName, out _);

        // ---- 6. the goal that counts down
        ActionQueue.Maker? cook = makers.FirstOrDefault(m => m.Entity.DisplayName.Text == "Cooking" && m.Costs.Count == 1 && m.Costs[0].Quantity == 1 && ActionQueue.Targets.CanStart(m.Action, m.Target, out _));
        game.Entities.TryGetObjectById("MelvorBase:Combat", out GameEntity? combatEntity);
        var combat = (CombatEntity)combatEntity!;
        if (cook == null) Out("  asagi sayan hedef: pisirilecek tarif bulunamadi");
        else
        {
            // (a) a recipe eats its ingredient down to the number asked for
            Item rawFood = cook.Costs[0].Item, food = cook.Product;
            int rawBefore = Qty(rawFood), foodBefore = Qty(food);
            SetQty(rawFood, 12);
            ActionQueue.Queue.Clear();
            ActionQueue.Queue.Add(QStep(cook, ActionQueue.Until.ItemLeft, 5, rawFood));
            ActionQueue.Queue.Add(QStep(cook, ActionQueue.Until.ItemLeft, 8, rawFood));   // already at or below 8 by then: skipped
            string goalText = ActionQueue.Queue.Steps[0].Goal;
            ActionQueue.Queue.Start();
            string progress = "";
            for (int i = 0; i < 400 && ActionQueue.Queue.Running; i++)
            {
                Ticks(30);
                ActionQueue.Queue.Tick(game, 0.5);
                if (ActionQueue.Queue.Running && ActionQueue.Queue.Index == 0 && ActionQueue.Queue.Steps[0].State.Contains("left")) progress = ActionQueue.Queue.Steps[0].State;
            }
            int rawLeft = Qty(rawFood);
            bool down = !ActionQueue.Queue.Running && rawLeft == 5 && ActionQueue.Queue.Steps[0].State == "done" && ActionQueue.Queue.Steps[1].State.Contains("already") && !cook.Action.IsStarted;
            Check("asagi sayan hedef: malzeme istenen sayiya inince duruyor", down,
                  $"{cook.Name}, '{goalText}': 12 {rawFood.DisplayName.GetPluralText(1)} -> {rawLeft} kaldi; calisirken: '{progress}'; adimlar: {ActionQueue.Queue.Steps[0].State} | {ActionQueue.Queue.Steps[1].State}");

            // (b) the case from the bug report: fight until only five of the food are left
            var cbSkill = ActionQueue.Targets.Skills(game).First(s => s.Entity.Id == "MelvorBase:Combat");
            CombatArea area = game.Data.GetDataRegistryOutput<CombatArea>().GetAllObjects().First(a => a.Enemies.Count > 0 && a.UnlockRequirements.AreMet);
            var monster = new ActionQueue.Target(area.Enemies[0].Id, $"{area.Enemies[0].DisplayName.Text}  ({area.DisplayName.Text})", area.Id);
            SetQty(food, 12);
            combat.Food.SelectFood(food);
            Engine.SetValue("combat.god", 1);
            ActionQueue.Queue.Clear();
            ActionQueue.Queue.Add(new ActionQueue.Step
            {
                Entity = cbSkill.Entity.Id, TargetId = monster.Id, Extra = monster.Extra, Name = $"Combat: {monster.Name}",
                Until = ActionQueue.Until.ItemLeft, Amount = 5, Subject = food.Id, SubjectName = food.DisplayName.GetPluralText(1),
            });
            ActionQueue.Queue.Start();
            ActionQueue.Queue.Tick(game, 0.5);
            bool fighting = combat.Action.IsStarted;
            int eaten = 0, loops = 0;
            for (; loops < 200 && ActionQueue.Queue.Running; loops++)
            {
                Ticks(60);
                // The character gets hurt and eats, the way auto eat or a click would.
                int had = Qty(food);
                combat.Player.Health.Set(Math.Max(1, combat.Player.Health.Max / 2));
                combat.Food.ManualEat();
                eaten += had - Qty(food);
                ActionQueue.Queue.Tick(game, 0.5);
            }
            int foodLeft = Qty(food);
            Check("hata bildirimindeki durum: yemek 5'e inene kadar savas", fighting && !ActionQueue.Queue.Running && !combat.Action.IsStarted && foodLeft == 5 && ActionQueue.Queue.Steps[0].State == "done",
                  $"12 {food.DisplayName.GetPluralText(1)} ile savas basladi: {fighting}; {eaten} tane yendi, {foodLeft} kalinca savas durdu; adim: {ActionQueue.Queue.Steps[0].State}; not: '{ActionQueue.Queue.Note}'");
            game.ActionManager.TryStopActive(force: true);
            Engine.ResetAll();
            Engine.SetValue("bank.slots", 10000);
            combat.Player.Health.Set(combat.Player.Health.Max);

            // (c) the window offers the food being eaten when the count-down goal is chosen for a fight
            ActionQueue.ActionQueueMod queueMod0 = ActionQueue.ActionQueueMod.Instance!;
            if (queueMod0.Window is not { Visible: true }) queueMod0.ToggleWindow();
            yield return 5;
            queueMod0.Window!.ShowStepForm();
            queueMod0.Window.ChooseUntil(ActionQueue.Until.Stops);
            bool choseFight = queueMod0.Window.Choose(cbSkill.Entity.Id, monster.Id);
            queueMod0.Window.ChooseUntil(ActionQueue.Until.ItemLeft);
            Item? offered = queueMod0.Window.CountedItem;
            Check("asagi sayan hedef secilince yenen yemek oneriliyor", offered == food && queueMod0.Window.AmountShown == 5,
                  $"savas secili: {choseFight}; onerilen esya: {(offered == null ? "-" : offered.DisplayName.GetPluralText(1))}; sayi: {queueMod0.Window.AmountShown}");
            // The player had typed 997 for another goal: the count-down goal still starts at 5, and 997 comes back afterwards.
            queueMod0.Window.ChooseUntil(ActionQueue.Until.Item);
            queueMod0.Window.SetAmount(997);
            queueMod0.Window.ChooseUntil(ActionQueue.Until.ItemLeft);
            double leftNumber = queueMod0.Window.AmountShown;
            queueMod0.Window.ChooseUntil(ActionQueue.Until.Item);
            double otherNumber = queueMod0.Window.AmountShown;
            // No food chosen for the fight: the food the bank has most of is offered.
            combat.Food.UnselectFood();
            Item? mostFood = s_bank.Inventory.Stacks.Where(s => s.Quantity > 0 && s.Item.Capabilities.TryGet<FoodCapability>(out _)).OrderByDescending(s => s.Quantity).FirstOrDefault()?.Item;
            queueMod0.Window.ChooseUntil(ActionQueue.Until.Stops);
            queueMod0.Window.ChooseUntil(ActionQueue.Until.ItemLeft);
            Item? offeredWithout = queueMod0.Window.CountedItem;
            // Goal chosen first, the fight afterwards (the order the player used).
            queueMod0.Window.Choose("MelvorBase:Woodcutting", ActionQueue.Targets.All(wood.Action)[0].Id);
            bool backToFight = queueMod0.Window.Choose(cbSkill.Entity.Id, monster.Id);
            Item? offeredAfterSkill = queueMod0.Window.CountedItem;
            Check("asagi sayan hedef: sayi ve yemek her durumda doluyor", leftNumber == 5 && otherNumber == 997 && mostFood != null && offeredWithout == mostFood && backToFight && offeredAfterSkill == mostFood,
                  $"baska hedefte 997 yaziliyken secince: {leftNumber}; geri donunce: {otherNumber}; savasta yemek secili degilken onerilen: {(offeredWithout == null ? "-" : offeredWithout.DisplayName.GetPluralText(1))} " +
                  $"(bankada en cok olan yemek: {(mostFood == null ? "-" : mostFood.DisplayName.GetPluralText(1))}); once hedef sonra savas secilince: {(offeredAfterSkill == null ? "-" : offeredAfterSkill.DisplayName.GetPluralText(1))}");
            queueMod0.Window.ChooseUntil(ActionQueue.Until.Stops);
            queueMod0.ToggleWindow();
            combat.Food.UnselectFood();
            SetQty(rawFood, rawBefore);
            SetQty(food, foodBefore);
        }

        // ---- 7. the window: the saved-queues face and the quick list in the header
        ActionQueue.Queue.Clear();
        ActionQueue.Queue.LoadPreset(A, out _);
        ActionQueue.ActionQueueMod queueMod = ActionQueue.ActionQueueMod.Instance!;
        queueMod.ToggleWindow();
        yield return 5;
        ActionQueue.QueueWindow w = queueMod.Window!;
        w.ShowSavedFace();
        yield return 5;
        Rect2 rect = w.Panel.GetGlobalRect();
        Vector2 min = w.Panel.GetCombinedMinimumSize(), screen = ctx.Tree.Root.GetVisibleRect().Size;
        bool fits = rect.Position.X >= 0 && rect.Position.Y >= 0 && rect.End.X <= screen.X && rect.End.Y <= screen.Y && min.X <= rect.Size.X + 0.5f && min.Y <= rect.Size.Y + 0.5f;
        int rowsAtStart = w.SavedRows;
        string quickAtStart = w.QuickSelected;
        w.PickQuick(B);                                    // the list in the header: one click switches
        bool quickLoaded = ActionQueue.Queue.Name == B && ActionQueue.Queue.Steps.Count == 1 && w.QuickSelected == "Queue: " + B;
        ActionQueue.Queue.Add(QStep(wood, ActionQueue.Until.Minutes, 2));
        string quickChanged = w.QuickSelected;             // a star marks unsaved changes
        w.TypeNameAndSave("Fish and wood");
        bool savedThird = ActionQueue.Queue.GetPreset("Fish and wood")?.Steps.Count == 2 && ActionQueue.Queue.Name == "Fish and wood";
        w.SelectSaved(A);
        string copied = w.PressCopy();
        w.SelectSaved("Fish and wood");
        w.PressDelete();
        bool notYet = ActionQueue.Queue.GetPreset("Fish and wood") != null;   // the first click only asks
        w.PressDelete();
        bool deleted = ActionQueue.Queue.GetPreset("Fish and wood") == null && ActionQueue.Queue.Steps.Count == 2;
        w.PasteAndImport(copied);
        bool pasted = ActionQueue.Queue.GetPreset(A + " (2)")?.Steps.Count == 2;
        string status = w.StatusText;
        w.SelectSaved(A);
        w.PressLoad();
        bool loadedByButton = ActionQueue.Queue.Name == A && ActionQueue.Queue.Steps.Count == 2;
        Check("kayitli siralar penceresi", fits && rowsAtStart >= 2 && quickAtStart == "Queue: " + A && quickLoaded && quickChanged.EndsWith("*") && savedThird && copied.StartsWith("AQ1:")
              && notYet && deleted && pasted && loadedByButton,
              $"pencere {rect.Size.X}x{rect.Size.Y} (en kucuk {min.X}x{min.Y}); {rowsAtStart} kayit; ustteki liste: '{quickAtStart}' -> secince '{ActionQueue.Queue.Name}' yuklendi: {quickLoaded}; degisince '{quickChanged}'; " +
              $"yeni adla kayit: {savedThird}; silme iki tikla: {notYet && deleted}; kopyala-yapistir: {pasted} ('{status}'); Load dugmesi: {loadedByButton}");
        if (DisplayServer.GetName() != "headless")
        {
            string shotDir = Path.Combine(Path.GetDirectoryName(System.Environment.GetEnvironmentVariable("MML_TEST_OUT")!)!, "shots");
            Directory.CreateDirectory(shotDir);
            ActionQueue.Queue.LoadPreset(B, out _);
            ActionQueue.Queue.SavePreset("Combat grind", out _);
            ActionQueue.Queue.LoadPreset(A, out _);
            w.SelectSaved(A);
            w.PressCopy();
            yield return 30;
            Out($"  goruntu sira-4-kayitli.png: {ctx.Tree.Root.GetTexture().GetImage().SavePng(Path.Combine(shotDir, "sira-4-kayitli.png"))}");
        }

        string queueFile = Path.Combine(Path.GetDirectoryName(typeof(ActionQueue.ActionQueueMod).Assembly.Location)!, "queue.json");
        string onDisk = File.ReadAllText(queueFile);
        Check("kayitli siralar dosyaya yaziliyor", onDisk.Contains("\"Presets\"") && onDisk.Contains(A) && onDisk.Contains(B), Path.GetFileName(queueFile));

        w.ShowStepForm();
        queueMod.ToggleWindow();
        foreach (string name in ActionQueue.Queue.PresetNames) ActionQueue.Queue.DeletePreset(name, out _);
        ActionQueue.Queue.Clear();
        game.ActionManager.TryStopActive(force: true);
        Engine.ResetAll();
        combat.Player.Health.Set(combat.Player.Health.Max);
    }

    // ------------------------------------------------------------------------------------------------
    // The player's report: "Make 200 Iron Bar" went straight to smithing. Rebuilt here with their numbers.
    static IEnumerator<int> PlanRepro(Game game, ModContext ctx)
    {
        Out("=== Plan: oyuncunun durumu (Make 200 Iron Bar) ===");
        Engine.Bind(game);
        Engine.ResetAll();
        Engine.SetValue("bank.slots", 10000);
        game.ActionManager.TryStopActive(force: true);
        ActionQueue.Queue.Bind(game);
        ActionQueue.Queue.Clear();
        var makers = ActionQueue.Planner.Makers(game);
        Item? bar = makers.Keys.FirstOrDefault(i => i.DisplayName.GetPluralText(1) == "Iron Bar");
        if (bar == null) { Out("  Iron Bar bulunamadi"); yield break; }
        ActionQueue.Maker smith = makers[bar].First(m => m.Main);
        Out($"  Iron Bar tarifi: {smith.Name}; islem basina {smith.PerAction} urun; malzeme: {string.Join(", ", smith.Costs.Select(c => $"{c.Quantity} {c.Item.DisplayName.GetPluralText(1)}"))}; " +
            $"baslatilabilir mi: {ActionQueue.Targets.CanStart(smith.Action, smith.Target, out string smithWhy)} {smithWhy}");
        Item ore = smith.Costs[0].Item;
        ActionQueue.Maker? mine = makers.GetValueOrDefault(ore)?.FirstOrDefault(m => m.Main);
        Out($"  {ore.DisplayName.GetPluralText(1)} kaynagi: {mine?.Name ?? "YOK"}; baslatilabilir mi: {(mine == null ? "-" : ActionQueue.Targets.CanStart(mine.Action, mine.Target, out string mineWhy) + " " + mineWhy)}");
        int Qty(Item i) => s_bank.Inventory.GetQuantity(i);
        void SetQty(Item i, int n) { int now = Qty(i); if (now > n) s_bank.Inventory.RemoveQuantity(i, now - n); else if (now < n) s_bank.Inventory.AddQuantity(i, n - now); }
        int barBefore = Qty(bar), oreBefore = Qty(ore);
        var others = smith.Costs.Skip(1).Select(c => (c.Item, Before: Qty(c.Item))).ToList();
        foreach (var (item, _) in others) SetQty(item, 100000);   // anything else the recipe needs is not the question here

        foreach (int oreAtStart in new[] { 72, 272 })
        {
            game.ActionManager.TryStopActive(force: true);
            ActionQueue.Queue.Clear();
            SetQty(bar, 106);
            SetQty(ore, oreAtStart);
            ActionQueue.PlanResult plan = ActionQueue.Planner.Compute(game, bar, 200, more: true);
            Out($"  [cevher {oreAtStart}] plan: {string.Join(" -> ", plan.Steps.Select(l => $"{l.Maker!.Name} [hedef {l.Need}, bankada {l.Have}]"))}{(plan.Problem == "" ? "" : " sorun: " + plan.Problem)}");
            ActionQueue.Planner.AddToQueue(plan);
            // What the window shows right after "Add": the next plan, counting what the queue will make and use.
            ActionQueue.PlanResult next = ActionQueue.Planner.Compute(game, bar, 200, more: true);
            Out($"  [cevher {oreAtStart}] eklendikten sonra pencerenin gosterdigi: {string.Join(" -> ", next.Steps.Select(l => $"{l.Maker!.Name} [hedef {l.Need}, bankada {l.Have}]"))}");
            ActionQueue.Queue.Start();
            var seen = new List<string>();
            for (int i = 0; i < 12; i++)
            {
                ActionQueue.Queue.Tick(game, 0.5);
                for (int k = 0; k < 30; k++) game.PropagateTick();
                string doing = game.ActionManager.ActiveActions.Count == 0 ? "hicbir sey" : string.Join("+", game.ActionManager.ActiveActions.Select(a => $"{a.Entity.DisplayName.Text}: {ActionQueue.Targets.Current(a)?.Name}"));
                if (seen.Count == 0 || seen[^1] != doing) seen.Add(doing);
            }
            Out($"  [cevher {oreAtStart}] ilk 6 saniye: {string.Join(" -> ", seen)}");
            Out($"  [cevher {oreAtStart}] adimlar: {string.Join(" | ", ActionQueue.Queue.Steps.Select(s => $"{s.Name}: {s.State}"))}; not: '{ActionQueue.Queue.Note}'");
        }

        // The same, but the character is already mining that ore by hand when the queue is started.
        game.ActionManager.TryStopActive(force: true);
        ActionQueue.Queue.Clear();
        SetQty(bar, 106);
        SetQty(ore, 72);
        if (mine != null)
        {
            ActionQueue.Targets.Start(game, mine.Action, mine.Target, out _);
            ActionQueue.PlanResult plan = ActionQueue.Planner.Compute(game, bar, 200, more: true);
            ActionQueue.Planner.AddToQueue(plan);
            for (int k = 0; k < 600; k++) game.PropagateTick();   // ten seconds pass before Start is pressed
            int oreAtStart = Qty(ore);
            ActionQueue.Queue.Start();
            ActionQueue.Queue.Tick(game, 0.5);
            Out($"  [elle kazarken] plan kurulurken cevher 72, Start'a basilinca {oreAtStart}; adimlar: {string.Join(" | ", ActionQueue.Queue.Steps.Select(s => $"{s.Name}: {s.State}"))}");
        }
        // Once more in real time, with the player's own cheat settings, driven by the mods' own clocks (no help from the test).
        string settingsFile = Path.Combine(Path.GetDirectoryName(typeof(CheatMenuMod).Assembly.Location)!, "settings.json");
        var savedSettings = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, double>>>(File.ReadAllText(settingsFile))!;
        savedSettings.TryGetValue("Falor", out Dictionary<string, double>? theirs);
        foreach (bool withSettings in new[] { false, true })
        {
            game.ActionManager.TryStopActive(force: true);
            ActionQueue.Queue.Clear();
            Engine.ResetAll();
            Engine.SetValue("bank.slots", 10000);
            if (withSettings && theirs != null) foreach (var v in theirs) if (Features.Find(v.Key) != null) Engine.SetValue(v.Key, v.Value);
            SetQty(bar, 106);
            SetQty(ore, 72);
            ActionQueue.ActionQueueMod queueMod = ActionQueue.ActionQueueMod.Instance!;
            if (queueMod.Window is not { Visible: true }) queueMod.ToggleWindow();
            yield return 10;
            ActionQueue.PlanResult? shown = queueMod.Window!.PlanFor(bar.Id, 200, more: true);
            queueMod.Window.PressAddPlan();
            yield return 10;
            ActionQueue.Queue.Start();
            var timeline = new List<string>();
            for (int i = 0; i < 40; i++)   // about twenty real seconds
            {
                yield return 30;
                string doing = game.ActionManager.ActiveActions.Count == 0 ? "hicbir sey" : string.Join("+", game.ActionManager.ActiveActions.Select(a => $"{a.Entity.DisplayName.Text}: {ActionQueue.Targets.Current(a)?.Name}"));
                string line = $"{doing} [cevher {Qty(ore)}, kulce {Qty(bar)}; {string.Join(" | ", ActionQueue.Queue.Steps.Select(s => s.State))}]";
                if (timeline.Count == 0 || !timeline[^1].StartsWith(doing + " [") || i % 10 == 9) timeline.Add(line);
            }
            Out($"  [gercek zaman, {(withSettings ? "oyuncunun hile ayarlariyla" : "ayarsiz")}] plan {shown?.Steps.Count} adim; akis:");
            foreach (string line in timeline) Out("      " + line);
            Out($"      not: '{ActionQueue.Queue.Note}', calisiyor: {ActionQueue.Queue.Running}");
            queueMod.Window.ShowStepForm();
            queueMod.ToggleWindow();
        }

        game.ActionManager.TryStopActive(force: true);
        ActionQueue.Queue.Clear();
        SetQty(bar, barBefore);
        SetQty(ore, oreBefore);
        foreach (var (item, before) in others) SetQty(item, before);
        Engine.ResetAll();
    }

    // ------------------------------------------------------------------------------------------------
    // The wiki: runs every Cookbook recipe against the real game and prints the facts the pages quote.
    static IEnumerator<int> DocsCheck(Game game, ModContext ctx)
    {
        Out("=== Wiki: tarifler ve oyun bilgileri ===");
        Engine.Bind(game);
        Engine.ResetAll();
        game.ActionManager.TryStopActive(force: true);

        // facts for the "Inside the game" page
        foreach (GameEntity e in game.Entities.GetAllObjects())
            Out($"  ENTITY {e.Id} ({e.GetType().Name}): {string.Join(", ", e.Components.Select(c => $"{c.Key}={c.Value.GetType().Name}"))}");
        Out($"  LEVELED: {string.Join(", ", game.LeveledEntities.Select(s => s.Id))}");
        Out($"  COUNTS: items {game.Data.GetDataRegistryOutput<Item>().GetAllObjects().Count()}, enemies {game.Data.GetDataRegistryOutput<Enemy>().GetAllObjects().Count()}, areas {game.Data.GetDataRegistryOutput<CombatArea>().GetAllObjects().Count()}, currencies {game.Data.GetDataRegistryOutput<Currency>().GetAllObjects().Count()}");
        Out($"  SAMPLE ITEMS: {string.Join(", ", game.Data.GetDataRegistryOutput<Item>().GetAllObjects().Take(6).Select(i => $"{i.Id} = {i.DisplayName.GetPluralText(1)}"))}");
        Out($"  TICKS PER SECOND: {GamesByMalcs.Melvor2.Core.Constants.TicksPerSecond}");
        Out($"  USER DIR: {ProjectSettings.GlobalizePath("user://")}");

        // the "first mod" tutorial, only when it was built into the game for this run; before any window opens
        if (DisplayServer.GetName() != "headless" && Loader.LoadedMods.Any(m => m.ToString()!.Contains("HelloMelvor")))
        {
            for (int i = 0; i < 180; i++) yield return 1;
            string shotDir = Path.Combine(Path.GetDirectoryName(System.Environment.GetEnvironmentVariable("MML_TEST_OUT")!)!, "shots");
            Directory.CreateDirectory(shotDir);
            Out($"  goruntu wiki-ilk-mod.png: {ctx.Tree.Root.GetTexture().GetImage().SavePng(Path.Combine(shotDir, "wiki-ilk-mod.png"))}");
        }

        BankEntity bank0() => DocsRecipes.Bank(game)!;
        // the bank button recipe, for real: open the bank the way the game's menu does and look for the button
        var bankRecipe = new DocsMod();
        bankRecipe.OnLoad(ctx);
        GameAccess.FindGameView()!.OpenBank();
        for (int i = 0; i < 60 && bankRecipe.BankButton == null; i++) yield return 2;
        Button? added = bankRecipe.BankButton;
        bool inTree = added != null && GodotObject.IsInstanceValid(added) && added.IsInsideTree() && added.IsVisibleInTree();
        string neighbours = added?.GetParent() is Node row ? string.Join(" | ", row.GetChildren().Select(c => c is Button b ? "Button '" + b.Text + "'" : c.GetType().Name)) : "-";
        Check("tarif: bankaya dugme", inTree, neighbours);
        // the window from the "Building windows" page, with five items of the bank
        var shown = bank0().Inventory.Stacks.Where(s => s.Quantity > 0).Select(s => s.Item).Distinct().Take(5).ToList();
        bankRecipe.ShowWindowForCheck(game, bank0(), shown);
        yield return 10;
        Check("pencere sayfasi ornegi", true, $"{shown.Count} satir");
        if (DisplayServer.GetName() != "headless")
        {
            yield return 10;
            string shotDir2 = Path.Combine(Path.GetDirectoryName(System.Environment.GetEnvironmentVariable("MML_TEST_OUT")!)!, "shots");
            Out($"  goruntu wiki-pencere.png: {ctx.Tree.Root.GetTexture().GetImage().SavePng(Path.Combine(shotDir2, "wiki-pencere.png"))}");
        }
        bankRecipe.HideWindowForCheck();
        if (DisplayServer.GetName() != "headless" && inTree)
        {
            yield return 20;
            string shotDir = Path.Combine(Path.GetDirectoryName(System.Environment.GetEnvironmentVariable("MML_TEST_OUT")!)!, "shots");
            Directory.CreateDirectory(shotDir);
            Out($"  goruntu wiki-banka-dugmesi.png: {ctx.Tree.Root.GetTexture().GetImage().SavePng(Path.Combine(shotDir, "wiki-banka-dugmesi.png"))}");
        }

        BankEntity bank = DocsRecipes.Bank(game)!;
        Item? byId = DocsRecipes.ItemById(game, "MelvorBase:OakLog");
        Item? byName = DocsRecipes.ItemByName(game, "oak log");
        Check("tarif: esya bulma", byId != null && byName == byId, $"id -> {byId?.DisplayName.GetPluralText(1)}; ad -> {byName?.Id}");
        Item item = byId ?? bank.Inventory.Stacks.First().Item;
        int had = bank.Inventory.GetQuantity(item);
        string io = DocsRecipes.ItemsInAndOut(bank, item);
        Check("tarif: esya ekle/cikar", bank.Inventory.GetQuantity(item) == had + 60, io);
        Check("tarif: para", true, DocsRecipes.Currencies(game, bank));

        var sold = new List<string>();
        bank.ItemSales.ItemSold += (_, e) => sold.Add($"{e.Quantity} {e.Item.Id} {e.CurrencyGain.Quantity}");
        DocsRecipes.ListenToEvents(game, ctx);
        DocsRecipes.Sell(bank, item, 10);
        Check("tarif: satis + olay", bank.Inventory.GetQuantity(item) == had + 50 && sold.Count == 1, string.Join("; ", sold));
        bank.Inventory.RemoveQuantity(item, 50);

        Out($"  SKILLS: {DocsRecipes.Skills(game)}");
        var thieving = game.LeveledEntities.First(s => s.Id == "MelvorBase:Thieving");
        int lvl = thieving.Experience.Level;
        DocsRecipes.SetLevel(game, "MelvorBase:Thieving", lvl + 1);
        bool raised = thieving.Experience.Level == lvl + 1;
        DocsRecipes.SetLevel(game, "MelvorBase:Thieving", lvl);
        Check("tarif: seviye ayarlama", raised && thieving.Experience.Level == lvl, $"{lvl} -> {lvl + 1} -> {thieving.Experience.Level}");

        bool started = DocsRecipes.StartWoodcutting(game);
        string running = DocsRecipes.WhatIsRunning(game);
        bool again = DocsRecipes.StartWoodcutting(game);   // calling it twice must not stop it
        Check("tarif: is baslatma / ne calisiyor", started && again && running.StartsWith("Woodcutting"), running);
        game.ActionManager.TryStopActive();
        Check("tarif: durdurma", !game.ActionManager.HasActiveAction, DocsRecipes.WhatIsRunning(game));

        int slots = bank.Inventory.MaxStacks;
        DocsRecipes.AddBankSlots(game, 100);
        int with = bank.Inventory.MaxStacks;
        DocsRecipes.AddBankSlots(game, 250);    // AddOrUpdate: changes the amount, does not stack
        int changed = bank.Inventory.MaxStacks;
        DocsRecipes.RemoveBankSlots(game);
        Check("tarif: bonus (modifier)", with == slots + 100 && changed == slots + 250 && bank.Inventory.MaxStacks == slots, $"{slots} -> {with} -> {changed} -> {bank.Inventory.MaxStacks}");

        var settings = DocsRecipes.LoadSettings(ctx, game);
        settings.Threshold = 123;
        DocsRecipes.SaveSettings(ctx, game, settings);
        Check("tarif: karakter basina ayar", DocsRecipes.LoadSettings(ctx, game).Threshold == 123, File.ReadAllText(Path.Combine(ctx.Directory, "settings.json")).Replace("\n", " ").Replace("\r", ""));
        File.Delete(Path.Combine(ctx.Directory, "settings.json"));

        Check("tarif: cevrimdisi kontrolu", !GameAccess.IsComputingOffline, $"IsComputingOffline = {GameAccess.IsComputingOffline}");
        Check("tarif: ozel uye (UnsafeAccessor)", true, DocsRecipes.PrivateMembers(game));
        Check("harita sayfasi ornekleri", true, DocsRecipes.MapSnippets(game));

        Out($"  LOADED MODS: {string.Join(", ", Loader.LoadedMods)}; loader {Loader.Version}");
        yield return 5;
    }

    // ------------------------------------------------------------------------------------------------
    // The player's report: "Skill action speed" x2 together with a skill's own speed x4, and the game stood
    // still. The game adds the two reductions up (-50% and -75%), which is a time below zero.
    static IEnumerator<int> SpeedTests(Game game, ModContext ctx)
    {
        Out("=== 5o. Hiz ayarlarinin birlesimi ===");
        Engine.Bind(game);
        Engine.ResetAll();
        Engine.SetValue("bank.slots", 10000);
        game.ActionManager.TryStopActive(force: true);
        ActionQueue.Queue.Bind(game);
        ActionQueue.Queue.Clear();
        var skills = ActionQueue.Targets.Skills(game);
        var wcSkill = skills.First(s => s.Entity.Id == "MelvorBase:Woodcutting");
        ActionQueue.Target tree = ActionQueue.Targets.All(wcSkill.Action)[0];
        Item log = ActionQueue.Targets.Product(wcSkill.Action, tree)!;
        int Logs() => s_bank.Inventory.GetQuantity(log);
        string crash = "";
        int Cut(int ticks)
        {
            int before = Logs();
            try { for (int i = 0; i < ticks; i++) game.PropagateTick(); }
            catch (Exception e) { crash = (e.InnerException ?? e).Message; }
            return Logs() - before;
        }
        const int T = 12000;
        ActionQueue.Targets.Start(game, wcSkill.Action, tree, out _);
        int plain = Cut(T);

        // 1. exactly what the player had set
        Engine.SetValue("speed.actions", 2);
        Engine.SetValue("skill.Woodcutting.speed", 4);
        string reading = string.Join(", ", Engine.Diagnose("skill.Woodcutting.speed"));
        int both = Cut(T);
        Check("genel x2 + beceri x4: oyun durmuyor, x8 oluyor", crash == "" && wcSkill.Action.IsStarted && Near(both, plain * 8.0, 0.15),
              $"normal {plain} odun, simdi {both} (beklenen ~{plain * 8}); oyunun okudugu sure indirimi: {reading}{(crash == "" ? "" : "; HATA: " + crash)}");

        // 2. the same on every skill, the way the "Apply to all" button does it
        var speedIds = Features.All.Where(f => f.Id.StartsWith("skill.") && f.Id.EndsWith(".speed")).Select(f => f.Id).ToList();
        Engine.SetValues(speedIds, 4);
        Engine.SetValue("skill.Smithing.speed", 2);
        int again = Cut(T);
        Check("oyuncunun ayarlarinin tamami", crash == "" && wcSkill.Action.IsStarted && Near(again, plain * 8.0, 0.15), $"{speedIds.Count} becerinin hizi ayarli; {again} odun (~{plain * 8}){(crash == "" ? "" : "; HATA: " + crash)}");

        // 3. everything at its highest: each skill must still work, with a sane time per action
        Engine.SetValue("speed.actions", Features.Get("speed.actions").Max);
        Engine.SetValues(speedIds, Features.Get("skill.Woodcutting.speed").Max);
        int top = Cut(T);
        var lent = new List<(Item Item, int Amount)>();
        foreach (Item any in Actions.Items(game))
        {
            int got = s_bank.Inventory.AddQuantity(any, 500);
            if (got > 0) lent.Add((any, got));
        }
        foreach (var (_, action) in skills)
        {
            if (action is GamesByMalcs.Melvor2.Entities.Hunting.HuntingActionComponent hunt)
                foreach (var animal in hunt.Locations.Values.SelectMany(l => l.Animals))
                    if (animal.SelectedTrap == null && !animal.TraplessEnabled && animal.CompatibleTraps.Count > 0) animal.SelectTrap(animal.CompatibleTraps[0]);
            if (action is GamesByMalcs.Melvor2.Entities.Agility.AgilityActionComponent agi)
                foreach (var course in agi.Courses.Values)
                    if (course.SelectedPath == null && course.ActivePaths.Count > 0) course.SelectPath(course.ActivePaths.Values.First());
        }
        var lines = new List<string>();
        var broken = new List<string>();
        var normallyStopped = new List<string>();
        double shortest = double.MaxValue;
        foreach (var (entity, action) in skills)
        {
            if (entity.Id == "MelvorBase:Combat") continue;
            ActionQueue.Target? started = null;
            foreach (ActionQueue.Target target in ActionQueue.Targets.All(action).Take(40))
                if (ActionQueue.Targets.Start(game, action, target, out _)) { started = target; break; }
            if (started == null) { lines.Add($"{entity.DisplayName.Text}: baslamadi"); continue; }
            string before = crash;
            double wait = double.MaxValue;
            int stoppedTicks = 0;
            try
            {
                for (int i = 0; i < 600; i++)
                {
                    game.PropagateTick();
                    if (action.IsStarted && action.ActionTimer is { } timer)
                    {
                        if (timer.IsStopped) stoppedTicks++; else wait = Math.Min(wait, timer.WaitTime);
                    }
                }
            }
            catch (Exception e) { crash = (e.InnerException ?? e).Message; }
            if (crash != before) broken.Add($"{entity.DisplayName.Text}: {crash}");
            if (stoppedTicks > 300) normallyStopped.Add(entity.DisplayName.Text);
            if (wait < double.MaxValue) shortest = Math.Min(shortest, wait);
            lines.Add($"{entity.DisplayName.Text} {(wait < double.MaxValue ? wait.ToString("0") + " ms" : "?")}");
            game.ActionManager.TryStopActive(force: true);
        }
        foreach (var (item, amount) in lent) s_bank.Inventory.RemoveQuantity(item, amount);
        Check("her sey en yuksekteyken hicbir beceri kilitlenmiyor", crash == "" && broken.Count == 0 && top > plain * 10 && shortest > 17,
              $"odun {top} (normal {plain}); islem sureleri: {string.Join(", ", lines)}; en kisa {shortest:0} ms (bir tik 16,7 ms){(broken.Count == 0 ? "" : "; KIRILANLAR: " + string.Join(" | ", broken))}");
        Out($"  saati normalde de duran beceriler (kurtarma bunlara dokunmamali): {(normallyStopped.Count == 0 ? "yok" : string.Join(", ", normallyStopped))}");

        // 4. an action whose timer died earlier comes back when a speed setting is changed
        Engine.ResetAll();
        Engine.SetValue("bank.slots", 10000);
        ActionQueue.Targets.Start(game, wcSkill.Action, tree, out _);
        Cut(600);
        wcSkill.Action.ActionTimer!.Stop();                      // what the game's error left behind: started, but nothing fires
        int dead = Cut(1200);
        Engine.SetValue("speed.actions", 2);
        int alive = Cut(1200);
        Check("saati olmus is, hiz ayari degisince yeniden calisiyor", dead == 0 && alive > 0 && crash == "", $"saat durmusken {dead} odun; ayar degisince {alive} odun");

        // 5. the settings on their own still do what they say
        Engine.ResetAll();
        Engine.SetValue("bank.slots", 10000);
        ActionQueue.Targets.Start(game, wcSkill.Action, tree, out _);
        Engine.SetValue("speed.actions", 5);
        int onlyAll = Cut(T);
        Engine.SetValue("speed.actions", 1);
        Engine.SetValue("skill.Woodcutting.speed", 5);
        int onlyOne = Cut(T);
        Check("tek basina ayarlar eskisi gibi", Near(onlyAll, plain * 5.0, 0.15) && Near(onlyOne, plain * 5.0, 0.15) && crash == "", $"yalniz genel x5: {onlyAll}; yalniz beceri x5: {onlyOne} (~{plain * 5})");

        // 6. the menu is at its highest, and then the game itself takes more time off (a better tool, a potion)
        Engine.ResetAll();
        Engine.SetValue("bank.slots", 10000);
        ActionQueue.Targets.Start(game, wcSkill.Action, tree, out _);
        Engine.SetValue("speed.actions", 20);
        Cut(600);
        crash = "";
        Action takeBack = Engine.AddOutside("skill.Woodcutting.speed", 0.10);
        string unguarded = string.Join(", ", Engine.Diagnose("skill.Woodcutting.speed"));
        Engine.Guard();                                          // what the mod does on its next frame
        string guarded = string.Join(", ", Engine.Diagnose("skill.Woodcutting.speed"));
        int afterBonus = Cut(3000);
        Check("oyunun kendi bonusu sonradan gelince de kilitlenmiyor", crash == "" && afterBonus > 0 && wcSkill.Action.IsStarted,
              $"bonus gelince: {unguarded}; mod duzeltince: {guarded}; sonra {afterBonus} odun{(crash == "" ? "" : "; HATA: " + crash)}");
        takeBack();
        Engine.Guard();

        // 6b. Hunting: its journal gives bonuses to "shortest tracking time", which the game adds to "tracking time".
        //     Everything at its highest, then such a bonus arrives: no animal may end up with no time at all.
        Engine.ResetAll();
        Engine.SetValue("speed.actions", 20);
        Engine.SetValue("skill.Hunting.speed", 20);
        crash = "";
        var hunting = skills.First(s => s.Entity.Id == "MelvorBase:Hunting").Action as GamesByMalcs.Melvor2.Entities.Hunting.HuntingActionComponent;
        var animals = hunting!.Locations.Values.SelectMany(l => l.Animals).ToList();
        double Shortest() => animals.Min(a => Math.Min(Math.Min(a.Stats.TrackingInterval.Value.Min, a.Stats.TrappingInterval.Value.Min), a.Stats.SurveyInterval.Value));
        double before6b = Shortest();
        var journal = new List<Action>
        {
            Engine.AddOutside(new StatRef("HuntingAction", "PercentMinTrackingInterval", "Hunting", "Action", -1), 0.30),
            Engine.AddOutside(new StatRef("HuntingAction", "PercentMaxTrackingInterval", "Hunting", "Action", -1), 0.20),
            Engine.AddOutside(new StatRef("HuntingAction", "PercentMinTrappingInterval", "Hunting", "Action", -1), 0.30),
        };
        double unguarded6b = Shortest();
        Engine.Guard();
        double guarded6b = Shortest();
        string companions = string.Join(", ", Engine.Companions("skill.Hunting.speed"));
        // and a hunt really runs through tracking and trapping without the game's error
        var lentAgain = new List<(Item Item, int Amount)>();          // (traps and bait, as in the run above)
        foreach (Item any in Actions.Items(game))
        {
            int got = s_bank.Inventory.AddQuantity(any, 500);
            if (got > 0) lentAgain.Add((any, got));
        }
        ActionQueue.Target? huntTarget = ActionQueue.Targets.All(hunting).Take(40).FirstOrDefault(target => ActionQueue.Targets.Start(game, hunting, target, out _));
        try { for (int i = 0; i < 3000; i++) game.PropagateTick(); }
        catch (Exception e) { crash = (e.InnerException ?? e).Message; }
        bool hunted = huntTarget != null && crash == "" && hunting.IsStarted;
        game.ActionManager.TryStopActive(force: true);
        foreach (var (item, amount) in lentAgain) s_bank.Inventory.RemoveQuantity(item, amount);
        foreach (Action back in journal) back();
        Engine.Guard();
        Check("Hunting: gunluk bonusu ust uste binince de sure sifira inmiyor", guarded6b >= 100 && crash == "" && companions.Contains("PercentTrackingInterval+2") && companions.Contains("PercentTrappingInterval+2"),
              $"{animals.Count} hayvanin en kisa suresi: once {before6b:0} ms, bonus gelince {unguarded6b:0} ms, mod duzeltince {guarded6b:0} ms; birlikte sayilanlar: {companions}; " +
              $"3000 tik av: {(huntTarget == null ? "test duzeni baslatamadi (yukaridaki en-yuksek kontrolunde Hunting calisiyor)" : hunted ? "calisti" : "HATA: " + crash)}");
        crash = "";

        // 7. the protection is for times only: "100% cheaper" stays 100%
        Engine.ResetAll();
        Engine.SetValue("skills.cheaper", 100);
        Engine.SetValue("combat.slayerFreeReroll", 1);
        string cheaper = string.Join(", ", Engine.Diagnose("skills.cheaper").Distinct());
        string reroll = string.Join(", ", Engine.Diagnose("combat.slayerFreeReroll"));
        Check("koruma yalniz surelere: malzeme indirimi %100 kaliyor", cheaper == "PercentIngredientCost=-1" && reroll.EndsWith("=-1"), $"{cheaper}; {reroll}");

        // 8. Astrology: Knowledge per study, set the way the player does it - in the menu's own box
        Engine.ResetAll();
        Engine.SetValue("bank.slots", 10000);
        game.ActionManager.TryStopActive(force: true);
        var astro = skills.First(s => s.Entity.Id == "MelvorBase:Astrology");
        var astroAction = (GamesByMalcs.Melvor2.Entities.Astrology.AstrologyActionComponent)astro.Action;
        bool studying = false;
        foreach (ActionQueue.Target target in ActionQueue.Targets.All(astro.Action).Take(40))
            if (ActionQueue.Targets.Start(game, astro.Action, target, out _)) { studying = true; break; }
        var star = astroAction.StudiedConstellation;
        double perStudy = star?.Stats.Knowledge.Value ?? 0;
        double had = star?.Knowledge ?? 0;
        Cut(3600);
        double gainedPlain = (star?.Knowledge ?? 0) - had;

        CheatMenuMod menuMod = CheatMenuMod.Instance!;
        if (menuMod.Menu is not { Visible: true }) menuMod.ToggleMenu();
        yield return 5;
        int skillsPage = Enumerable.Range(0, menuMod.Menu!.PageCount).First(i => menuMod.Menu.PageTitle(i) == "Skills");
        menuMod.Menu.ShowPage(skillsPage);
        yield return 3;
        Label? rowName = menuMod.Menu.PageControl(skillsPage).FindChildren("*", "Label", true, false).Cast<Label>().FirstOrDefault(l => l.Text == "Astrology: knowledge per study");
        SpinBox? box = null;
        for (Node? up = rowName?.GetParent(); up != null && box == null && up != menuMod.Menu.PageControl(skillsPage); up = up.GetParent())
            box = up.FindChildren("*", "SpinBox", true, false).Cast<SpinBox>().FirstOrDefault();
        if (box != null) box.Value = 10;
        yield return 3;
        double perStudy10 = star?.Stats.Knowledge.Value ?? 0;
        had = star?.Knowledge ?? 0;
        Cut(3600);
        double gained10 = (star?.Knowledge ?? 0) - had;
        Check("Astrology: menuden 'knowledge per study' x10", studying && rowName != null && box != null && Engine.GetValue("skills.astroKnowledge") == 10
              && perStudy > 0 && Near(perStudy10, perStudy * 10, 0.02) && Near(gained10, gainedPlain * 10, 0.15) && crash == "",
              $"menude satir {(rowName != null ? "var" : "YOK")}, kutu {(box != null ? "var" : "YOK")}; calisma basina {perStudy:0.##} -> {perStudy10:0.##}; bir dakikada {gainedPlain:0.#} -> {gained10:0.#} bilgi");
        menuMod.ToggleMenu();

        game.ActionManager.TryStopActive(force: true);
        Engine.ResetAll();
        yield return 5;
    }

    // ------------------------------------------------------------------------------------------------
    // The player's wish: "high bars need far too much coal - take the need for coal out of the game", as a list
    // of materials to choose from in the Cheat Menu.
    static IEnumerator<int> MaterialTests(Game game, ModContext ctx)
    {
        Out("=== 5p. Gereksiz maddeler (Materials sayfasi) ===");
        Engine.Bind(game);
        Engine.ResetAll();
        Engine.SetValue("bank.slots", 10000);
        Unneeded.Clear();
        game.ActionManager.TryStopActive(force: true);
        ActionQueue.Queue.Bind(game);
        ActionQueue.Queue.Clear();
        var skills = ActionQueue.Targets.Skills(game);
        var smith = skills.First(s => s.Entity.Id == "MelvorBase:Smithing");
        var items = Actions.Items(game);
        Item coal = items.First(i => i.Id == "MelvorBase:Coal");
        Item ironOre = items.First(i => i.Id == "MelvorBase:IronOre");
        Item steelBar = items.First(i => i.Id == "MelvorBase:SteelBar");
        Item silverBar = items.First(i => i.Id == "MelvorBase:SilverBar");
        int Qty(Item i) => s_bank.Inventory.GetQuantity(i);
        void SetQty(Item i, int n)
        {
            int d = n - Qty(i);
            if (d > 0) s_bank.Inventory.AddQuantity(i, d); else if (d < 0) s_bank.Inventory.RemoveQuantity(i, -d);
        }
        string crash = "";
        void Ticks(int n)
        {
            try { for (int i = 0; i < n; i++) game.PropagateTick(); }
            catch (Exception e) { crash = (e.InnerException ?? e).Message; }
        }
        var artisans = skills.Select(s => s.Action).OfType<GamesByMalcs.Melvor2.Components.ArtisanAction.ArtisanActionComponent>().ToList();
        var herblore = skills.Select(s => s.Action).OfType<GamesByMalcs.Melvor2.Entities.Herblore.HerbloreActionComponent>().First();
        int Asking(Item item) => artisans.Sum(c => c.ActiveRecipes.Values.Count(r => r.Stats.ItemCost.Values.Any(v => v.Value.Item == item)));
        int Potions(Item item) => herblore.ActiveRecipes.Values.Count(r => r.Stats.ItemCost.Values.Any(v => v.Value.Item == item));
        int Upgrades(Item item) => game.Data.GetDataRegistryOutput<GamesByMalcs.Melvor2.Components.ItemUpgrading.ItemUpgrade>().GetAllObjects().Count(u => u.Costs.Items.Any(q => q.Item == item));
        int PlanSteps()
        {
            int ore = Qty(ironOre), fuel = Qty(coal), bars = Qty(steelBar);
            SetQty(ironOre, 0); SetQty(coal, 0); SetQty(steelBar, 0);
            int steps = ActionQueue.Planner.Compute(game, steelBar, 50, null, more: true).Steps.Count;
            SetQty(ironOre, ore); SetQty(coal, fuel); SetQty(steelBar, bars);
            return steps;
        }

        // ---- before: a Steel Bar needs coal, and without coal nothing is smelted
        ActionQueue.Target steel = ActionQueue.Targets.All(smith.Action).First(x => x.Name == "Steel Bar");
        ActionQueue.Targets.CanStart(smith.Action, steel, out string levelProblem);
        int askingBefore = Asking(coal), potionsBefore = Potions(coal), planBefore = PlanSteps();
        SetQty(ironOre, 60); SetQty(coal, 0); SetQty(steelBar, 0);
        ActionQueue.Targets.Start(game, smith.Action, steel, out _);
        Ticks(600);
        int barsWithout = Qty(steelBar);
        game.ActionManager.TryStopActive(force: true);

        // ---- the player's path: F8, Materials, click Coal, "Not needed"
        CheatMenuMod menuMod = CheatMenuMod.Instance!;
        if (menuMod.Menu is not { Visible: true }) menuMod.ToggleMenu();
        yield return 5;
        Menu menu = menuMod.Menu!;
        int pageIndex = Enumerable.Range(0, menu.PageCount).FirstOrDefault(i => menu.PageTitle(i) == "Materials", -1);
        if (pageIndex < 0) { Check("Materials sayfasi", false, "menude boyle bir sayfa yok"); yield break; }
        menu.ShowPage(pageIndex);
        yield return 3;
        Control page = menu.PageControl(pageIndex);
        Tree tree = page.FindChildren("*", "Tree", true, false).Cast<Tree>().First();
        Button Press(string text) => page.FindChildren("*", "Button", true, false).Cast<Button>().First(b => b.Text == text);
        string Row(string name)
        {
            for (TreeItem? row = tree.GetRoot()?.GetFirstChild(); row != null; row = row.GetNext())
                if (row.GetText(0) == name) return $"{row.GetText(0)} | {row.GetText(1)} | {row.GetText(2)}";
            return "(yok)";
        }
        string firstRows = string.Join(", ", Enumerable.Range(0, 6).Select(i => tree.GetRoot()?.GetChild(i)?.GetText(0) ?? "?"));
        string rowBefore = Row("Coal");
        bool picked = menu.PickMaterial("Coal");
        Press("Not needed").EmitSignal(BaseButton.SignalName.Pressed);
        yield return 3;
        string rowAfter = Row("Coal");
        int askingAfter = Asking(coal), potionsAfter = Potions(coal), planAfter = PlanSteps();

        SetQty(ironOre, 60); SetQty(coal, 0); SetQty(steelBar, 0);
        bool started = ActionQueue.Targets.Start(game, smith.Action, steel, out string startProblem);
        Ticks(1500);
        int bars = Qty(steelBar), oreUsed = 60 - Qty(ironOre);
        SetQty(coal, 25);                                        // coal the player still has is left alone
        Ticks(600);
        int coalKept = Qty(coal);
        Check("Coal 'Not needed': komursuz Steel Bar dokuluyor", levelProblem == "" && barsWithout == 0 && picked && started && bars > 0 && oreUsed >= bars && askingBefore >= 9 && askingAfter == 0
              && potionsBefore > 0 && potionsAfter == 0 && coalKept == 25 && crash == "" && rowAfter.EndsWith("Not needed"),
              $"listenin basi: {firstRows}; satir once '{rowBefore}', sonra '{rowAfter}'; komur isteyen tarif {askingBefore} -> {askingAfter}, iksir {potionsBefore} -> {potionsAfter}; " +
              $"komursuz: once {barsWithout} kulce, sonra {bars} kulce ({oreUsed} demir cevheri gitti); bankadaki 25 komur: {coalKept}{(started ? "" : "; BASLAMADI: " + startProblem + levelProblem)}{(crash == "" ? "" : "; HATA: " + crash)}");
        Check("Is Sirasi plani da komuru istemiyor", planBefore >= 2 && planAfter == planBefore - 1, $"50 Steel Bar plani: {planBefore} adim -> {planAfter} adim");

        // ---- item upgrades use the same list (silver bars are asked for by a great many of them)
        int upgradesBefore = Upgrades(silverBar);
        Unneeded.Set(new[] { silverBar }, true);
        int upgradesAfter = Upgrades(silverBar);
        Unneeded.Set(new[] { silverBar }, false);
        int upgradesBack = Upgrades(silverBar);
        Check("esya yukseltmeleri de ayni listeye uyuyor", upgradesBefore > 0 && upgradesAfter == 0 && upgradesBack == upgradesBefore, $"Silver Bar isteyen yukseltme: {upgradesBefore} -> {upgradesAfter} -> geri alininca {upgradesBack}");

        // ---- the list is remembered: the character is opened again
        Unneeded.Bind(game, Engine.Character);
        int rememberedCount = Unneeded.Count, askingReopened = Asking(coal);

        // ---- and switched back: "Needed again"
        menu.ShowPage(pageIndex);
        yield return 3;
        bool pickedAgain = menu.PickMaterial("Coal");
        Press("Needed again").EmitSignal(BaseButton.SignalName.Pressed);
        yield return 3;
        int askingBack = Asking(coal), potionsBack = Potions(coal);
        SetQty(coal, 0);
        Ticks(900);
        bool stoppedForCoal = !smith.Action.IsStarted;
        Check("hatirlaniyor ve geri alinabiliyor", rememberedCount == 1 && askingReopened == 0 && pickedAgain && askingBack == askingBefore && potionsBack == potionsBefore && stoppedForCoal && Unneeded.Count == 0 && crash == "",
              $"yeniden acilinca listede {rememberedCount} madde, komur isteyen tarif {askingReopened}; 'Needed again' sonrasi {askingBack} tarif + {potionsBack} iksir yine komur istiyor; komur bitince dokum {(stoppedForCoal ? "durdu" : "DURMADI")}; satir: '{Row("Coal")}'");

        Unneeded.Clear();
        menuMod.ToggleMenu();
        game.ActionManager.TryStopActive(force: true);
        SetQty(ironOre, 0); SetQty(steelBar, 0);
        Engine.ResetAll();
        yield return 5;
    }

    // A look at the game's own top bar (Bank, Shop, Quests...), for mods that add a button to it.
    static IEnumerator<int> HeaderProbe(Game game, ModContext ctx)
    {
        Out("=== ust cubuk ===");
        yield return 10;
        Node? header = null;
        void Search(Node n) { if (header != null) return; if (n is GamesByMalcs.Melvor2.GameMenus.Header.GameHeader) { header = n; return; } foreach (Node c in n.GetChildren()) Search(c); }
        Search(ctx.Tree.Root);
        if (header == null) { Out("GameHeader bulunamadi"); yield break; }
        Out("header: " + header.GetPath());
        Node? bank = null;
        void Find(Node n) { if (bank != null) return; if (n is GamesByMalcs.Melvor2.GameMenus.Pages.BankNavigationButton) { bank = n; return; } foreach (Node c in n.GetChildren()) Find(c); }
        Find(header);
        if (bank == null) { Out("Bank dugmesi bulunamadi"); yield break; }
        Node row = bank.GetParent();
        Out($"satir: {row.GetPath()} ({row.GetClass()}), {row.GetChildCount()} cocuk");
        void Dump(Node n, int depth)
        {
            string extra = "";
            if (n is Control c) extra += $" size={c.Size} min={c.CustomMinimumSize} vis={c.Visible} mouse={c.MouseFilter}";
            if (n is Label l) extra += $" text='{l.Text}' font={l.GetThemeFontSize("font_size")} color={l.GetThemeColor("font_color")} settings={(l.LabelSettings != null ? l.LabelSettings.FontSize + "/" + l.LabelSettings.FontColor : "-")}";
            if (n is TextureRect tr) extra += $" tex={tr.Texture?.ResourcePath} expand={tr.ExpandMode} stretch={tr.StretchMode}";
            if (n is BaseButton b) extra += $" button flat={(n as Button)?.Flat} toggle={b.ToggleMode}";
            if (n is MarginContainer m) extra += $" margins={m.GetThemeConstant("margin_left")},{m.GetThemeConstant("margin_top")},{m.GetThemeConstant("margin_right")},{m.GetThemeConstant("margin_bottom")}";
            if (n is BoxContainer bc) extra += $" sep={bc.GetThemeConstant("separation")}";
            if (n is PanelContainer pc) extra += $" panel={pc.GetThemeStylebox("panel")?.GetClass()}";
            Out(new string(' ', depth * 2) + $"{n.Name} [{n.GetClass()} / {n.GetType().Name}]{extra}");
            if (depth < 6) foreach (Node child in n.GetChildren()) Dump(child, depth + 1);
        }
        foreach (Node child in row.GetChildren())
            if (child is GamesByMalcs.Melvor2.GameMenus.Pages.BankNavigationButton or GamesByMalcs.Melvor2.GameMenus.Pages.ShopNavigationButton or GamesByMalcs.Melvor2.GameMenus.Pages.SimpleNavigationButton)
                Dump(child, 1);
            else Out($"  {child.Name} [{child.GetType().Name}]");
        Out("bitti: ust cubuk");
    }

    // ------------------------------------------------------------------------------------------------
    // The Merchant mod: a button in the game's top bar between Bank and Shop, raw materials for sale at a
    // multiple of the bank price, goods bought above bank price, new goods every few hours.
    static IEnumerator<int> MerchantTests(Game game, ModContext ctx)
    {
        Out("=== 5r. Merchant ===");
        game.ActionManager.TryStopActive(force: true);
        Merchant.MerchantMod mod = Merchant.MerchantMod.Instance!;
        // A fixed clock, and ModTest's own page in merchant.json wiped (other characters' pages are left alone).
        DateTime clock = new DateTime(2026, 11, 19, 9, 30, 0, DateTimeKind.Utc);
        Merchant.Market.Now = () => clock;
        string file = Path.Combine(Merchant.Market.Dir, "merchant.json");
        if (File.Exists(file))
        {
            var all = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file))!.AsObject();
            all.Remove(game.CharacterName);
            File.WriteAllText(file, all.ToJsonString());
        }
        Merchant.Market.Unbind();
        Merchant.Market.Bind(game);

        var gold = Actions.Currencies(game).First(c => c.Id == "MelvorBase:GoldPieces");
        long Gold() => s_bank.Wallet.GetQuantity(gold);
        void SetGold(long n) { long d = n - Gold(); if (d > 0) s_bank.Wallet.AddQuantity(gold, d); else if (d < 0) s_bank.Wallet.RemoveQuantity(gold, -d); }
        int Qty(Item i) => s_bank.Inventory.GetQuantity(i);
        void SetQty(Item i, int n) { int d = n - Qty(i); if (d > 0) s_bank.Inventory.AddQuantity(i, d); else if (d < 0) s_bank.Inventory.RemoveQuantity(i, -d); }
        long goldAtStart = Gold();
        Engine.Bind(game);
        Engine.SetValue("bank.slots", 10000);

        // ---- 1. the button in the game's own top bar
        yield return 45;
        Button? top = mod.TopBarButton;
        string order = top == null ? "(dugme yok)" : string.Join(", ", top.GetParent().GetChildren().Select(n => n.Name.ToString().Replace("NavigationButton", "")));
        int at = top?.GetIndex() ?? -1;
        bool between = top != null && at > 0 && top.GetParent().GetChild(at - 1).Name == "BankNavigationButton" && top.GetParent().GetChild(at + 1).Name == "ShopNavigationButton";
        Control? bankButton = top?.GetParent().GetChild(at - 1) as Control;
        Check("ust cubukta Bank ile Shop arasinda Merchant dugmesi", between && top!.Visible && top.Size.X >= 60 && Math.Abs(top.Size.Y - bankButton!.Size.Y) < 1,
              $"sira: {order}; boyut {top?.Size} (Bank {bankButton?.Size})");
        if (top == null) yield break;

        // ---- 2. a click on it opens the window
        string? markBefore = mod.Mark;
        string tipBefore = top.TooltipText;
        top.EmitSignal(BaseButton.SignalName.Pressed);
        yield return 5;
        Merchant.MerchantWindow win = mod.Window!;
        string? markOpen = mod.Mark;
        var offers = Merchant.Market.Offers;
        var wanted = Merchant.Market.Wanted;
        Out($"  {win.GoldText}; {win.NextText}");
        for (int i = 0; i < win.SaleRows; i++) Out("  SATIS  " + win.SaleText(i));
        for (int i = 0; i < win.WantedRows; i++) Out("  ARANAN " + win.WantedText(i));
        bool pricesRight = offers.All(o => o.Price == (long)Math.Ceiling(Merchant.Market.BankPrice(o.Item) * (o.Rare ? 4m : o.Deal ? 1.6m : 2m)))
                           && wanted.All(w => w.Pays == (long)Math.Ceiling(Merchant.Market.BankPrice(w.Item) * 1.5m));
        var gameView = Merchant.MerchantPage.View(ctx.Tree)!;
        bool isPage = win.Root != null && gameView.EntityViewContainer.IsAncestorOf(win.Root) && gameView.EntityViewContainer.GetChildCount() == 1
                      && Merchant.MerchantPage.Current(gameView) is Merchant.MerchantPage;
        Label? pageTitle = gameView.Header.FindChildren("*", "Label", true, false).OfType<Label>().FirstOrDefault(l => l.Text == "Merchant" && l.IsVisibleInTree() && !top.IsAncestorOf(l));
        Check("Merchant oyunun kendi sayfasi olarak aciliyor (Bank/Shop'un yerinde, ust baslikta adi)", isPage && pageTitle != null,
              $"oyunun sayfa alaninda: {isPage}; ust baslik: {(pageTitle == null ? "YOK" : "'" + pageTitle.Text + "'")}; sayfa alanindaki gorunum sayisi {gameView.EntityViewContainer.GetChildCount()}");
        Check("pencere aciliyor: satilanlar ve arananlar listeleniyor", win.Visible && win.SaleRows == offers.Count && offers.Count >= 4 && win.WantedRows == wanted.Count && wanted.Count >= 1
              && pricesRight && offers.Count(o => o.Deal) == 1 && win.NextText.Contains("2h 30m"),
              $"{offers.Count} urun satista ({offers.Count(o => o.Rare)} nadir, firsat: {offers.FirstOrDefault(o => o.Deal)?.Item.Id}), {wanted.Count} aranan; fiyat kurali {(pricesRight ? "tutuyor" : "TUTMUYOR")}; '{win.NextText}'");
        Check("dugmedeki isaret: yeni mal gelince yaniyor, pencere acilinca sonuyor", markBefore != null && tipBefore.Contains("New goods have arrived") && markOpen == null,
              $"acmadan once: {markBefore ?? "yok"}, ipucu '{tipBefore.Replace("\n", " / ")}'; acinca: {markOpen ?? "sondu"}");
        string shotDir = Path.Combine(Path.GetDirectoryName(System.Environment.GetEnvironmentVariable("MML_TEST_OUT")!)!, "shots");
        Directory.CreateDirectory(shotDir);
        yield return 3;
        void Shot(string name)
        {
            Image? picture = ctx.Tree.Root.GetTexture()?.GetImage();          // there is none when the game runs without a window
            if (picture != null) Out($"  goruntu {name}: " + picture.SavePng(Path.Combine(shotDir, name)));
        }
        Shot("merchant-1-pencere.png");

        // ---- 3. buying, through the window's own buttons
        Merchant.Offer first = offers[0];
        SetGold(1_000_000);
        yield return 35;                                         // the window brings its numbers up to date twice a second
        int had = Qty(first.Item);
        long cost10 = Merchant.Market.Cost(first, 10);
        win.BuyButton(0, 1).EmitSignal(BaseButton.SignalName.Pressed);        // "10"
        string said10 = win.StatusText;
        bool bought10 = Qty(first.Item) == had + 10 && Gold() == 1_000_000 - cost10 && first.Bought == 10;
        // "All" with gold for exactly seven more
        long cost7 = Merchant.Market.Cost(first, 7);
        SetGold(cost7);
        yield return 35;
        string rowSeven = win.SaleText(0);
        win.BuyButton(0, 3).EmitSignal(BaseButton.SignalName.Pressed);        // "All"
        bool boughtSeven = Qty(first.Item) == had + 17 && Gold() == 0 && first.Bought == 17;
        yield return 35;
        string rowBroke = win.SaleText(0);
        bool allGreyed = Enumerable.Range(0, 4).All(b => win.BuyButton(0, b).Disabled);
        win.BuyButton(0, 0).EmitSignal(BaseButton.SignalName.Pressed);        // a click that slips through buys nothing
        bool nothingFree = Qty(first.Item) == had + 17 && Gold() == 0;
        string saidBroke = win.StatusText;
        // the whole stock of the last row
        int last = offers.Count - 1;
        Merchant.Offer small = offers[last];
        int hadSmall = Qty(small.Item);
        long costAll = Merchant.Market.Cost(small, small.Left);
        int stockSmall = small.Stock;
        SetGold(50_000_000);
        yield return 35;
        win.BuyButton(last, 3).EmitSignal(BaseButton.SignalName.Pressed);
        yield return 35;
        bool soldOut = small.Left == 0 && Qty(small.Item) == hadSmall + stockSmall && Gold() == 50_000_000 - costAll && win.SaleText(last).Contains("sold out")
                       && Enumerable.Range(0, 4).All(b => win.BuyButton(last, b).Disabled);
        double dearer = (double)costAll / (small.Price * stockSmall);
        Check("satin alma: para dusuyor, banka artiyor, stok azaliyor; fiyat hic degismiyor", bought10 && boughtSeven && allGreyed && nothingFree && soldOut && dearer == 1.0 && cost10 == first.Price * 10,
              $"'10': '{said10}'; 7'lik parayla 'All': satir '{rowSeven}' -> {(boughtSeven ? "7 aldi, altin 0" : "YANLIS")}; parasizken satir '{rowBroke}', tiklaninca: '{saidBroke}'; " +
              $"son satirin tamami ({stockSmall} adet): liste fiyatinin {dearer:0.00} kati tuttu, sonra '{win.SaleText(last)}'");

        // ---- 4. selling what he is looking for
        Merchant.Want want = wanted[0];
        int hadWant = Qty(want.Item);
        SetQty(want.Item, 25);
        SetGold(1000);
        yield return 35;
        string wantRow = win.WantedText(0);
        win.SellButton(0, 1).EmitSignal(BaseButton.SignalName.Pressed);       // "10"
        string saidSell = win.StatusText;
        bool sold10 = Qty(want.Item) == 15 && Gold() == 1000 + want.Pays * 10 && want.Sold == 10;
        SetQty(want.Item, want.Wants + 40);                                    // far more than he wants
        yield return 35;
        long goldBefore = Gold();
        int leftBefore = want.Left;
        win.SellButton(0, 3).EmitSignal(BaseButton.SignalName.Pressed);       // "All"
        yield return 35;
        bool onlyWhatHeWants = want.Left == 0 && Qty(want.Item) == want.Wants + 40 - leftBefore && Gold() == goldBefore + want.Pays * leftBefore
                               && win.WantedText(0).Contains("has enough") && Enumerable.Range(0, 4).All(b => win.SellButton(0, b).Disabled);
        Check("aranan urunu satma: bankanin 1,5 kati, yalniz istedigi kadar", sold10 && onlyWhatHeWants && want.Pays == (long)Math.Ceiling(Merchant.Market.BankPrice(want.Item) * 1.5m),
              $"satir '{wantRow}'; '10': '{saidSell}'; fazlasiyla 'All': {leftBefore} tane aldi, sonra '{win.WantedText(0)}'");
        SetQty(want.Item, hadWant);

        // ---- 5. the game is closed and opened again in the same visit: everything is as it was left
        string before = string.Join(";", offers.Select(o => $"{o.Item.Id}:{o.Bought}/{o.Stock}@{o.Price}")) + "|" + string.Join(";", wanted.Select(w => $"{w.Item.Id}:{w.Sold}/{w.Wants}"));
        Merchant.Market.Unbind();
        Merchant.Market.Bind(game);
        string after = string.Join(";", Merchant.Market.Offers.Select(o => $"{o.Item.Id}:{o.Bought}/{o.Stock}@{o.Price}")) + "|" + string.Join(";", Merchant.Market.Wanted.Select(w => $"{w.Item.Id}:{w.Sold}/{w.Wants}"));
        // ---- and four hours later he is back with new goods
        clock = clock.AddHours(4);
        Merchant.Market.Tick(game);
        yield return 35;
        var fresh = Merchant.Market.Offers;
        string freshNames = string.Join(", ", fresh.Select(o => Merchant.Market.Name(o.Item)));
        Check("ayni ziyarette hatirlaniyor; 4 saat sonra yeni mallar", before == after && fresh.All(o => o.Bought == 0) && Merchant.Market.Wanted.All(w => w.Sold == 0)
              && win.SaleRows == fresh.Count && string.Join(";", fresh.Select(o => o.Item.Id)) != string.Join(";", offers.Select(o => o.Item.Id)) && win.NextText.Contains("2h 30m")
              && mod.Mark == null,
              $"yeniden acinca ayni: {before == after}; yeni ziyaret: {freshNames}; '{win.NextText}'; pencere acikken isaret: {mod.Mark ?? "yanmiyor"}");

        // ---- 6. three hundred visits: never a money machine
        var madeOf = new Dictionary<Item, HashSet<Item>>();
        foreach (var recipe in game.Data.GetDataRegistryOutput<GamesByMalcs.Melvor2.Components.ArtisanAction.ArtisanRecipe>().GetAllObjects()
                     .Concat(game.Data.GetDataRegistryOutput<GamesByMalcs.Melvor2.Entities.Cooking.CookingRecipe>().GetAllObjects()))
        {
            Item? product = recipe.Products.FirstOrDefault()?.Item;
            if (product == null) continue;
            if (!madeOf.TryGetValue(product, out HashSet<Item>? parts)) madeOf[product] = parts = new HashSet<Item>();
            foreach (var part in recipe.Ingredients.Items) parts.Add(part.Item);
        }
        int loops = 0, sellBack = 0, visitsWithCoal = 0, fewest = int.MaxValue, most = 0;
        var seen = new HashSet<Item>();
        var seenWanted = new HashSet<Item>();
        string example = "";
        for (long visit = 500000; visit < 500300; visit++)
        {
            var (sale, looking) = Merchant.Market.Goods(game, game.CharacterName, visit);
            fewest = Math.Min(fewest, sale.Count); most = Math.Max(most, sale.Count);
            foreach (var o in sale) seen.Add(o.Item);
            foreach (var w in looking) seenWanted.Add(w.Item);
            if (sale.Any(o => o.Item.Id == "MelvorBase:Coal")) visitsWithCoal++;
            // buying something and selling it straight back, to him or to the bank
            // (his own buying price is the highest anyone pays; equal is no gain)
            sellBack += sale.Count(o => o.Price < (long)Math.Ceiling(Merchant.Market.BankPrice(o.Item) * 1.5m) || o.Price <= Merchant.Market.BankPrice(o.Item) || looking.Any(w => w.Item == o.Item));
            // buying the materials of something he is looking for
            foreach (var w in looking)
            {
                var needs = new HashSet<Item>();
                var open = new Queue<Item>();
                open.Enqueue(w.Item);
                while (open.Count > 0)
                    if (madeOf.TryGetValue(open.Dequeue(), out HashSet<Item>? parts))
                        foreach (Item part in parts)
                            if (needs.Add(part)) open.Enqueue(part);
                var hit = sale.FirstOrDefault(o => needs.Contains(o.Item));
                if (hit != null) { loops++; if (example == "") example = $"{Merchant.Market.Name(w.Item)} araniyor ama {Merchant.Market.Name(hit.Item)} satiliyor"; }
            }
        }
        Check("300 ziyaret: al-sat ya da al-isle-sat ile para basilamiyor", loops == 0 && sellBack == 0 && fewest >= 4,
              $"ziyaret basina {fewest}-{most} urun; 300 ziyarette {seen.Count} farkli urun satildi, {seenWanted.Count} farkli urun arandi; komur {visitsWithCoal} ziyarette vardi{(example == "" ? "" : "; ORNEK: " + example)}");

        // A way to earn money in one visit, in words; "" when there is none.
        string Hole(List<Merchant.Offer> sale, List<Merchant.Want> looking)
        {
            foreach (var o in sale)
                if (o.Price < (long)Math.Ceiling(Merchant.Market.BankPrice(o.Item) * 1.5m) || looking.Any(w => w.Item == o.Item))
                    return $"{Merchant.Market.Name(o.Item)} al-geri sat";
            foreach (var w in looking)
            {
                var needs = new HashSet<Item>();
                var open = new Queue<Item>();
                open.Enqueue(w.Item);
                while (open.Count > 0)
                    if (madeOf.TryGetValue(open.Dequeue(), out HashSet<Item>? parts))
                        foreach (Item part in parts)
                            if (needs.Add(part)) open.Enqueue(part);
                var hit = sale.FirstOrDefault(o => needs.Contains(o.Item));
                if (hit != null) return $"{Merchant.Market.Name(w.Item)} araniyor ama {Merchant.Market.Name(hit.Item)} satiliyor";
            }
            return "";
        }

        // ---- 7. the standing order, placed through the window's own list
        List<Item> orderable = Merchant.Market.Orderable(game);
        Item pick = orderable.FirstOrDefault(i => i.Id == "MelvorBase:Coal") ?? orderable[0];
        string pickName = Merchant.Market.Name(pick);
        var shelfBefore = Merchant.Market.Offers;
        bool listRight = win.OrderBox.ItemCount == orderable.Count + 1 && win.OrderBox.Selected == 0 && win.OrderBox.GetItemText(0) == "Nothing ordered";
        bool listFitsScreen = false;
        string noteNone = win.OrderNote;
        win.OrderBox.ShowPopup();                                 // what the player sees after a click on the list
        yield return 8;
        Shot("merchant-5-siparis-listesi.png");
        PopupMenu orderList = win.OrderBox.GetPopup();
        Rect2 orderButton = win.OrderBox.GetGlobalRect();
        float screenBottom = win.OrderBox.GetViewportRect().End.Y;
        listFitsScreen = orderList.Visible && orderList.Position.Y >= (int)orderButton.End.Y - 1 && orderList.Position.Y + orderList.Size.Y <= screenBottom && orderList.Size.Y >= 200;
        Out($"  siparis listesi acik: {orderList.Visible}, yer {orderList.Position}, boyut {orderList.Size}; dugmenin alti {orderButton.End.Y:0}, ekranin alti {screenBottom:0}");
        orderList.Hide();
        yield return 3;
        bool picked = win.PickOrder(pickName);
        string saidOrder = win.StatusText;
        yield return 35;
        string noteWaiting = win.OrderNote;
        bool placed = picked && Merchant.Market.Order == pick && ReferenceEquals(shelfBefore, Merchant.Market.Offers) && saidOrder.StartsWith("Ordered") && noteWaiting.Contains("from the next one on");
        Merchant.Market.Unbind();                                 // the game is closed and opened again
        Merchant.Market.Bind(game);
        bool kept = Merchant.Market.Order == pick;
        clock = clock.AddHours(4);
        Merchant.Market.Tick(game);
        yield return 35;
        var withOrder = Merchant.Market.Offers;
        Merchant.Offer? brought = withOrder.FirstOrDefault();
        long pickPrice = (long)Math.Ceiling(Merchant.Market.BankPrice(pick) * 2m);
        bool arrived = brought != null && brought.Item == pick && brought.Ordered && !brought.Deal && brought.Price == pickPrice && brought.Stock >= 20
                       && withOrder.Count(o => o.Item == pick) == 1 && withOrder.Count(o => !o.Rare) == 8 && win.SaleText(0).Contains("[ORDERED]")
                       && win.OrderNote == $"He brings {pickName} on every visit." && win.OrderBox.GetItemText(win.OrderBox.Selected) == pickName;
        Out("  SIPARIS " + win.SaleText(0) + "   |   " + win.OrderNote);
        Shot("merchant-2-siparis.png");
        SetGold(1_000_000);
        yield return 35;
        int hadPick = Qty(pick);
        win.BuyButton(0, 2).EmitSignal(BaseButton.SignalName.Pressed);        // "100"
        bool boughtOrder = brought != null && (brought.Stock < 100 || (Qty(pick) == hadPick + 100 && Gold() == 1_000_000 - pickPrice * 100));
        string saidBought = win.StatusText;
        clock = clock.AddHours(4);                                // a standing order: there again on the visit after
        Merchant.Market.Tick(game);
        yield return 35;
        bool again = Merchant.Market.Offers.FirstOrDefault() is { Ordered: true, Bought: 0 } second && second.Item == pick;
        bool cancelled = win.PickOrder("Nothing ordered") && Merchant.Market.Order == null && win.StatusText.StartsWith("Order cancelled");
        string saidCancel = win.StatusText;
        clock = clock.AddHours(4);
        Merchant.Market.Tick(game);
        yield return 35;
        bool gone = Merchant.Market.Offers.All(o => !o.Ordered) && win.OrderBox.Selected == 0;
        Check("siparis: listeden secilen madde sonraki her ziyarette rafta", listRight && listFitsScreen && placed && kept && arrived && boughtOrder && again && cancelled && gone,
              $"listede {win.OrderBox.ItemCount - 1} madde, acilinca ekrana sigiyor: {listFitsScreen}; once: '{noteNone}'; '{pickName}' secilince: '{saidOrder}' / '{noteWaiting}'; kapatip acinca duruyor: {kept}; " +
              $"sonraki ziyaret: {(arrived ? "en ustte, ORDERED" : "YOK/YANLIS")}; '100': '{saidBought}'; bir sonraki ziyarette yine: {again}; iptal: '{saidCancel}', sonraki ziyarette yok: {gone}");

        // every material that can be ordered, forty visits each: always there, never wanted, never a money machine
        int orderVisits = 0, missing = 0, wantedToo = 0;
        string orderHole = "";
        foreach (Item item in orderable)
            for (long visit = 600000; visit < 600040; visit++)
            {
                var (sale, looking) = Merchant.Market.Goods(game, game.CharacterName, visit, item);
                orderVisits++;
                if (sale.Count(o => o.Item == item && o.Ordered) != 1 || sale[0].Item != item) missing++;
                if (looking.Any(w => w.Item == item)) wantedToo++;
                string hole = Hole(sale, looking);
                if (hole != "" && orderHole == "") orderHole = $"{Merchant.Market.Name(item)} siparisiyle: {hole}";
            }
        Check("siparisle de para basilamiyor", missing == 0 && wantedToo == 0 && orderHole == "",
              $"{orderable.Count} madde x 40 ziyaret = {orderVisits} ziyaret; siparis rafta degil: {missing}; siparis ayni anda araniyor: {wantedToo}{(orderHole == "" ? "" : "; ORNEK: " + orderHole)}");

        // ---- 8. F3 leaves the page for the one the player came from; a click on another page of the game leaves it too
        mod.ToggleWindow();
        yield return 5;
        var cameFrom = Merchant.MerchantPage.Current(gameView);
        bool backWhereWeWere = !win.Visible && cameFrom is GamesByMalcs.Melvor2.GameMenus.Pages.EntityPage && gameView.EntityViewContainer.GetChildCount() == 1;
        mod.ToggleWindow();
        yield return 5;
        bool openAgain = win.Visible && win.SaleRows == Merchant.Market.Offers.Count;
        gameView.SetCurrentPage(game.Data.GetObject<GamesByMalcs.Melvor2.GameMenus.Pages.EntityPage>("MelvorBase:Bank"));   // what a click on Bank does
        yield return 5;
        bool bankTookOver = !win.Visible && Merchant.MerchantPage.Current(gameView)?.Id == "MelvorBase:Bank" && gameView.EntityViewContainer.GetChildCount() == 1;
        Shot("merchant-6-sonra-banka.png");
        if (cameFrom != null) gameView.SetCurrentPage(cameFrom);
        yield return 5;
        Check("sayfadan cikis: F3 gelinen sayfaya donuyor, Bank'a tiklayinca Bank aciliyor", backWhereWeWere && openAgain && bankTookOver,
              $"F3: {(backWhereWeWere ? "geri dondu (" + cameFrom!.Id + ")" : "DONMEDI")}; yeniden acildi: {openAgain}; Bank'a gecince Merchant kapandi: {bankTookOver}");

        // ---- 9. the mark on the top bar button, with the window closed
        // The next visit's wanted goods are taken out of the bank first, so only "new goods" is news.
        long nextVisit = Merchant.Market.Visit + 1;
        var nextWanted = Merchant.Market.Goods(game, game.CharacterName, nextVisit).Wanted;
        var hadBefore = nextWanted.ToDictionary(w => w.Item, w => Qty(w.Item));
        foreach (var w in nextWanted) SetQty(w.Item, 0);
        yield return 45;
        string? markQuiet = mod.Mark;                              // seen, nothing to sell: off
        clock = clock.AddHours(4);
        yield return 45;                                           // the mod itself notices the new visit
        string? markNew = mod.Mark;
        string tipNew = top.TooltipText;
        bool sameWanted = Merchant.Market.Visit == nextVisit && Merchant.Market.Wanted.Select(w => w.Item).SequenceEqual(nextWanted.Select(w => w.Item));
        Shot("merchant-3-isaret.png");
        top.EmitSignal(BaseButton.SignalName.Pressed);             // looked at
        yield return 5;
        mod.ToggleWindow();
        yield return 45;
        string? markLooked = mod.Mark;
        Merchant.Market.Unbind();                                  // closing and opening the game does not light it again
        yield return 45;
        string? markRestart = mod.Mark;
        Merchant.Want sought = Merchant.Market.Wanted[0];
        SetQty(sought.Item, 5);                                    // something he is looking for turns up in the bank
        yield return 45;
        string? markSell = mod.Mark;
        string tipSell = top.TooltipText;
        Shot("merchant-4-isaret-yesil.png");
        top.EmitSignal(BaseButton.SignalName.Pressed);
        yield return 5;
        mod.ToggleWindow();
        yield return 45;
        string? markTold = mod.Mark;
        Check("isaret: yeni mal altin, satilacak mal yesil; bakinca sonuyor, yeniden acinca yanmiyor",
              markQuiet == null && markNew == "gold" && tipNew.Contains("New goods have arrived") && sameWanted && markLooked == null && markRestart == null
              && markSell == "green" && tipSell.Contains(Merchant.Market.Name(sought.Item)) && tipSell.Contains("you have 5") && markTold == null,
              $"sakin: {markQuiet ?? "yok"}; 4 saat sonra: {markNew ?? "yok"}; bakinca: {markLooked ?? "yok"}; oyunu kapatip acinca: {markRestart ?? "yok"}; " +
              $"bankaya 5 {Merchant.Market.Name(sought.Item)} gelince: {markSell ?? "yok"}, ipucu '{tipSell.Replace("\n", " / ")}'; bakinca: {markTold ?? "yok"}");
        foreach (var (item, count) in hadBefore) SetQty(item, count);

        Merchant.Market.Now = () => DateTime.UtcNow;
        SetGold(goldAtStart);
        Merchant.Market.Unbind();
        yield return 5;
    }

    // ------------------------------------------------------------------------------------------------
    // A player's report: with "Skill action speed" a crop takes longer to grow, not shorter.
    static IEnumerator<int> FarmTests(Game game, ModContext ctx)
    {
        Out("=== 5s. Farming: hiz ayari ve ekili urunun suresi ===");
        Engine.Bind(game);
        Engine.ResetAll();
        Engine.SetValue("bank.slots", 10000);
        game.Entities.TryGetObjectById("MelvorBase:Farming", out GameEntity? farmEntity);
        var farm = farmEntity!.Components.Values.OfType<GamesByMalcs.Melvor2.Entities.Farming.FarmingPassiveComponent>().First();
        var gold = Actions.Currencies(game).First(c => c.Id == "MelvorBase:GoldPieces");
        long goldAtStart = s_bank.Wallet.GetQuantity(gold);
        s_bank.Wallet.AddQuantity(gold, 50_000_000);
        var lent = new List<(Item Item, int Amount)>();
        foreach (Item any in Actions.Items(game))
        {
            int got = s_bank.Inventory.AddQuantity(any, 500);
            if (got > 0) lent.Add((any, got));
        }

        // A plot and something the character is able to plant in it.
        var recipes = game.Data.GetDataRegistryOutput<GamesByMalcs.Melvor2.Entities.Farming.FarmingRecipe>().GetAllObjects()
            .Where(r => r.Requirements.AreMet && (r.DisplayRequirements == null || r.DisplayRequirements.AreMet) && r.BaseInterval >= 600000).ToList();
        var crop = recipes.FirstOrDefault(r => r.Id == "MelvorBase:Lettuce") ?? recipes.FirstOrDefault();
        var plot = crop == null ? null : farm.ActivePlots.Values.FirstOrDefault(p => p.Plot.Category == crop.Category && (p.IsPurchased || p.Plot.Requirements.AreMet));
        if (crop == null || plot == null) { Check("ekilebilir bir urun ve tarla var", false, $"urun: {crop?.Id ?? "yok"}, tarla: {plot?.Plot.Id ?? "yok"}"); yield break; }
        bool boughtNow = !plot.IsPurchased;
        if (boughtNow) plot.Purchase();
        if (plot.CanReset) plot.Reset();
        plot.SelectRecipe(crop);
        if (plot.CanPlant) plot.Plant();
        yield return 2;
        var timer = plot.State.Timer;
        double Minutes() => timer!.WaitTime / 60000.0;
        double LeftMinutes() => timer!.TicksLeft * (1000.0 / 60) / 60000.0;
        double baseMinutes = crop.BaseInterval / 60000.0;
        Check("urun ekildi, suresi oyunun normal suresi", timer is { IsRunning: true } && Near(Minutes(), baseMinutes, 0.01),
              $"{crop.DisplayName.Text}, tarla {plot.Plot.Id}{(boughtNow ? " (simdi satin alindi)" : "")}; sure {(timer == null ? "yok" : Minutes().ToString("0.0"))} dk (normal {baseMinutes:0.0} dk)");
        if (timer is not { IsRunning: true }) yield break;

        // A tenth of the time passes, then the player turns the speed up.
        int tenth = timer.WaitTicks / 10;
        for (int i = 0; i < tenth; i++) game.PropagateTick();
        double progress = timer.Progress;
        string before = $"{Minutes():0.0} dk'lik surenin {LeftMinutes():0.0} dk'si kaldi";
        Engine.SetValue("speed.actions", 20);
        yield return 3;
        string at20 = $"{Minutes():0.00} dk'lik surenin {LeftMinutes():0.00} dk'si kaldi";
        bool faster = Near(Minutes(), baseMinutes / 20, 0.01) && Near(LeftMinutes(), baseMinutes / 20 * 0.9, 0.01) && Math.Abs(timer.Progress - progress) < 0.001 && timer.IsRunning;
        Engine.SetValue("speed.actions", 5);
        yield return 3;
        string at5 = $"{Minutes():0.0} dk'lik surenin {LeftMinutes():0.0} dk'si kaldi";
        bool five = Near(Minutes(), baseMinutes / 5, 0.01) && Near(LeftMinutes(), baseMinutes / 5 * 0.9, 0.01);
        Engine.SetValue("speed.actions", 1);
        yield return 3;
        string off = $"{Minutes():0.0} dk'lik surenin {LeftMinutes():0.0} dk'si kaldi";
        bool back = Near(Minutes(), baseMinutes, 0.01) && Near(LeftMinutes(), baseMinutes * 0.9, 0.01) && Math.Abs(timer.Progress - progress) < 0.001;
        Engine.SetValue("skill.Farming.speed", 20);              // the skill's own speed setting does the same
        yield return 3;
        bool own = Near(Minutes(), baseMinutes / 20, 0.01) && Near(LeftMinutes(), baseMinutes / 20 * 0.9, 0.01);
        Check("hiz ayari degisince tarladaki urun de hizlaniyor, gecen sure korunuyor", faster && five && back && own,
              $"ayarsiz: {before}; x20: {at20}; x5: {at5}; kapatinca: {off}; Farming'in kendi hizi x20: {Minutes():0.00} dk'lik surenin {LeftMinutes():0.00} dk'si kaldi");

        // It is really ready when the shown time is over - not earlier, not later.
        int left = timer.TicksLeft;
        for (int i = 0; i < left - 60; i++) game.PropagateTick();
        bool notYet = !plot.CanHarvest && ReferenceEquals(plot.State.Timer, timer) && timer.IsRunning;
        for (int i = 0; i < 120; i++) game.PropagateTick();
        bool ready = plot.CanHarvest || !ReferenceEquals(plot.State.Timer, timer);
        Check("gosterilen sure dolunca urun gercekten hazir", notYet && ready,
              $"bitmesine 1 saniye kala hazir degil: {notYet}; sure dolunca: {(plot.CanHarvest ? "hasada hazir" : plot.State.GetType().Name)}");

        // A game bonus arriving on its own leaves the crops as the game has them.
        if (plot.CanReset) plot.Reset();
        Engine.ResetAll();
        yield return 40;                                          // (the half second after a change is over)
        plot.SelectRecipe(crop);
        if (plot.CanPlant) plot.Plant();
        yield return 2;
        var second = plot.State.Timer;
        Action takeBack = Engine.AddOutside("skill.Farming.speed", 0.5);
        yield return 5;
        bool untouched = second != null && Near(second.WaitTime / 60000.0, baseMinutes, 0.01);
        takeBack();
        Check("oyunun kendi bonusu ekili urune dokunmuyor (oyunun normal davranisi)", untouched, $"baska yerden %50 hiz bonusu gelince ekili urunun suresi: {(second == null ? "yok" : (second.WaitTime / 60000.0).ToString("0.0"))} dk");

        if (plot.CanReset) plot.Reset();
        Engine.ResetAll();
        foreach (var (item, amount) in lent) s_bank.Inventory.RemoveQuantity(item, amount);
        long extra = s_bank.Wallet.GetQuantity(gold) - goldAtStart;
        if (extra > 0) s_bank.Wallet.RemoveQuantity(gold, extra);
        yield return 2;
    }

    // ------------------------------------------------------------------------------------------------
    // Developer probe: what the game's own Bank and Shop pages are built of (nodes, panels, fonts), so a mod's
    // page can be built of the same things.
    static string Box(StyleBox? box) => box switch
    {
        null => "-",
        StyleBoxFlat f => $"Flat(bg {f.BgColor.ToHtml()}, border {f.BorderColor.ToHtml()} {f.BorderWidthTop}, radius {f.CornerRadiusTopLeft}, pad {f.ContentMarginLeft:0}/{f.ContentMarginTop:0})",
        StyleBoxTexture x => $"Texture({x.Texture?.ResourcePath}, margins {x.TextureMarginLeft:0}/{x.TextureMarginTop:0}/{x.TextureMarginRight:0}/{x.TextureMarginBottom:0}, pad {x.ContentMarginLeft:0}/{x.ContentMarginTop:0}, modulate {x.ModulateColor.ToHtml()})",
        StyleBoxEmpty => "Empty",
        _ => box.GetType().Name,
    };

    static void DumpNode(Node node, int depth, int maxDepth, ref int lines, int maxLines)
    {
        if (lines >= maxLines) return;
        string line = new string(' ', depth * 2) + node.Name + " <" + node.GetClass() + (node.GetType().Name != node.GetClass() ? "/" + node.GetType().Name : "") + ">";
        if (node is Control c)
        {
            line += $" {c.Size.X:0}x{c.Size.Y:0}" + (c.Visible ? "" : " HIDDEN");
            if (c.ThemeTypeVariation != "") line += " variation=" + c.ThemeTypeVariation;
            if (c.Theme != null) line += " theme=" + c.Theme.ResourcePath;
            if (c.Modulate != Colors.White) line += " modulate=" + c.Modulate.ToHtml();
            if (c.SelfModulate != Colors.White) line += " self=" + c.SelfModulate.ToHtml();
            if (c.Material != null) line += " material=" + c.Material.GetType().Name;
        }
        switch (node)
        {
            case Label l:
                Font? font = l.LabelSettings?.Font ?? l.GetThemeFont("font");
                line += $" '{(l.Text.Length > 28 ? l.Text[..28] : l.Text)}' size {(l.LabelSettings?.FontSize ?? l.GetThemeFontSize("font_size"))} color {(l.LabelSettings?.FontColor ?? l.GetThemeColor("font_color")).ToHtml()}"
                        + $" font {font?.ResourcePath}{(l.LabelSettings != null ? " settings=" + l.LabelSettings.ResourcePath + $" outline {l.LabelSettings.OutlineSize} shadow {l.LabelSettings.ShadowSize}" : "")}";
                break;
            case RichTextLabel r:
                line += $" rich '{(r.Text.Length > 28 ? r.Text[..28] : r.Text)}' size {r.GetThemeFontSize("normal_font_size")}";
                break;
            case BaseButton b:
                line += $" '{(b is Button bb ? bb.Text : "")}' normal={Box(b.GetThemeStylebox("normal"))} hover={Box(b.GetThemeStylebox("hover"))} size {b.GetThemeFontSize("font_size")} color {b.GetThemeColor("font_color").ToHtml()}";
                break;
            case PanelContainer or Panel:
                line += " panel=" + Box(((Control)node).GetThemeStylebox("panel"));
                break;
            case TextureRect tr:
                line += $" tex={tr.Texture?.ResourcePath} stretch={tr.StretchMode}";
                break;
            case NinePatchRect np:
                line += $" ninepatch={np.Texture?.ResourcePath} margins {np.PatchMarginLeft}/{np.PatchMarginTop}/{np.PatchMarginRight}/{np.PatchMarginBottom}";
                break;
            case LineEdit le:
                line += $" lineedit normal={Box(le.GetThemeStylebox("normal"))}";
                break;
            case BoxContainer bc:
                line += $" sep {bc.GetThemeConstant("separation")}";
                break;
            case MarginContainer mc:
                line += $" margins {mc.GetThemeConstant("margin_left")}/{mc.GetThemeConstant("margin_top")}/{mc.GetThemeConstant("margin_right")}/{mc.GetThemeConstant("margin_bottom")}";
                break;
            case ScrollContainer sc:
                line += $" scroll";
                break;
        }
        Out(line);
        lines++;
        if (depth >= maxDepth) return;
        int shown = 0;
        string lastKind = "";
        int sameKind = 0;
        foreach (Node child in node.GetChildren(true))
        {
            string kind = child.GetType().Name + "/" + child.GetChildCount();
            sameKind = kind == lastKind ? sameKind + 1 : 0;
            lastKind = kind;
            if (sameKind >= 2) continue;                          // a third identical sibling tells nothing new
            if (++shown > 14) { Out(new string(' ', depth * 2 + 2) + $"... ({node.GetChildCount(true)} children)"); lines++; break; }
            DumpNode(child, depth + 1, maxDepth, ref lines, maxLines);
        }
    }

    static IEnumerator<int> PagesProbe(Game game, ModContext ctx)
    {
        var view = ctx.Tree.Root.GetNode("Main/ApplicationViewContainer/GameView") as GamesByMalcs.Melvor2.GameMenus.GameView;
        if (view == null) { Out("GameView yok"); yield break; }
        string shotDir = Path.Combine(Path.GetDirectoryName(System.Environment.GetEnvironmentVariable("MML_TEST_OUT")!)!, "shots");
        Directory.CreateDirectory(shotDir);
        Out("ana tema: " + (ctx.Tree.Root.Theme?.ResourcePath ?? "(yok)") + "; proje temasi: " + ProjectSettings.GetSetting("gui/theme/custom") + "; font: " + ProjectSettings.GetSetting("gui/theme/custom_font"));
        Out($"EntityViewContainer: {view.EntityViewContainer.GetPath()} {view.EntityViewContainer.Size}, margins {view.EntityViewContainer.GetThemeConstant("margin_left")}/{view.EntityViewContainer.GetThemeConstant("margin_top")}");
        Out($"BlurPanel: gorunur {view.BlurPanel.Visible}, panel {Box(view.BlurPanel.GetThemeStylebox("panel"))}, material {view.BlurPanel.Material?.GetType().Name}; DimTheLights: {view.DimTheLightsPanel.Visible} self {view.DimTheLightsPanel.SelfModulate}");
        foreach (string id in new[] { "MelvorBase:Bank", "MelvorBase:Shop" })
        {
            var page = game.Data.GetObject<GamesByMalcs.Melvor2.GameMenus.Pages.EntityPage>(id);
            view.SetCurrentPage(page);
            yield return 40;
            Out($"=== {id}: blur {page.ApplyBackgroundBlur}, arka plan sayisi {page.TimeOfDayBackgrounds.Count}; widgets gorunur {view.Widgets.Visible} ===");
            Image? picture = ctx.Tree.Root.GetTexture()?.GetImage();
            if (picture != null) Out("  goruntu: " + picture.SavePng(Path.Combine(shotDir, "sayfa-" + id.Split(':')[1] + ".png")));
            int lines = 0;
            if (System.Environment.GetEnvironmentVariable("MML_PAGES") != "cards") DumpNode(view.EntityViewContainer, 0, id.EndsWith("Bank") ? 11 : 13, ref lines, 330);
        }
        // one shop category opened: what a purchase card is built of
        game.Entities.TryGetObjectById("MelvorBase:Shop", out GameEntity? shopEntity);
        var shop = shopEntity!.Components.Values.OfType<GamesByMalcs.Melvor2.Components.Shop.ShopComponent>().First();
        var category = shop.PurchasesByCategory.Keys.FirstOrDefault(c => c.Id.Contains("Item")) ?? shop.PurchasesByCategory.Keys.Skip(1).First();
        shop.SelectCategory(category);
        yield return 50;
        Out($"=== Shop kategorisi {category.Id} ===");
        Image? cards = ctx.Tree.Root.GetTexture()?.GetImage();
        if (cards != null) Out("  goruntu: " + cards.SavePng(Path.Combine(shotDir, "sayfa-Shop-kategori.png")));
        Node? list = view.EntityViewContainer.FindChild("SubcategoryContainer", true, false);
        int cardLines = 0;
        if (list != null) DumpNode(list, 0, 16, ref cardLines, 170);
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
        if (System.Environment.GetEnvironmentVariable("MML_ONLY") == "repro")
        {
            IEnumerator<int> repro = PlanRepro(game, ctx);
            while (repro.MoveNext()) yield return repro.Current;
            yield break;
        }
        if (System.Environment.GetEnvironmentVariable("MML_ONLY") == "docs")
        {
            IEnumerator<int> docs = DocsCheck(game, ctx);
            while (docs.MoveNext()) yield return docs.Current;
            yield break;
        }
        if (System.Environment.GetEnvironmentVariable("MML_ONLY") == "speed")
        {
            IEnumerator<int> speedOnly = SpeedTests(game, ctx);
            while (speedOnly.MoveNext()) yield return speedOnly.Current;
            yield break;
        }
        if (System.Environment.GetEnvironmentVariable("MML_ONLY") == "farm")
        {
            IEnumerator<int> farmOnly = FarmTests(game, ctx);
            while (farmOnly.MoveNext()) yield return farmOnly.Current;
            yield break;
        }
        if (System.Environment.GetEnvironmentVariable("MML_ONLY") == "pages")
        {
            IEnumerator<int> pages = PagesProbe(game, ctx);
            while (pages.MoveNext()) yield return pages.Current;
            yield break;
        }
        if (System.Environment.GetEnvironmentVariable("MML_ONLY") == "merchant")
        {
            IEnumerator<int> merchantOnly = MerchantTests(game, ctx);
            while (merchantOnly.MoveNext()) yield return merchantOnly.Current;
            yield break;
        }
        if (System.Environment.GetEnvironmentVariable("MML_ONLY") == "header")
        {
            IEnumerator<int> probe = HeaderProbe(game, ctx);
            while (probe.MoveNext()) yield return probe.Current;
            yield break;
        }
        if (System.Environment.GetEnvironmentVariable("MML_ONLY") == "materials")
        {
            IEnumerator<int> materialsOnly = MaterialTests(game, ctx);
            while (materialsOnly.MoveNext()) yield return materialsOnly.Current;
            yield break;
        }
        if (System.Environment.GetEnvironmentVariable("MML_ONLY") == "saved")
        {
            IEnumerator<int> savedOnly = SavedQueueTests(game, ctx);
            while (savedOnly.MoveNext()) yield return savedOnly.Current;
            yield break;
        }
        if (System.Environment.GetEnvironmentVariable("MML_ONLY") is "multi" or "multi-save" or "multi-load")
        {
            string which = System.Environment.GetEnvironmentVariable("MML_ONLY")!;
            IEnumerator<int> multi = which == "multi" ? MultiTests(game, ctx) : which == "multi-save" ? MultiSave(game) : MultiLoad(game);
            while (multi.MoveNext()) yield return multi.Current;
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
        const int PlTicks = 108000;   // thirty minutes of game time: mining pays out in lumps of 20, a short run is too noisy
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
            bool plClose = plGot > 0 && plGot / plExpected >= 0.8 && plGot / plExpected <= 1.5;
            if (plClose) plRateOk++;
            plRates.Add($"{plM.Name}: tahmin {plExpected:0}, gercek {plGot} ({plGot / plExpected:0.00}x){(plClose ? "" : " <-- UYMUYOR")}");
            game.ActionManager.TryStopActive(force: true);
            PlRestore();
        }
        Check("plan: sure tahmini gercege yakin (30 dakikalik oyun)", plRateTried >= 4 && plRateOk == plRateTried, string.Join("; ", plRates));

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
            Check("plan penceresi", plShown != null && plRows == plPlan.Steps.Count + plPlan.Covered.Count && plNote.Contains("step") && plRect.Position.X >= 0 && plRect.Position.Y >= 0
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

        // ---------- 5o. speed settings together ----------
        IEnumerator<int> speedTests = SpeedTests(game, ctx);
        while (speedTests.MoveNext()) yield return speedTests.Current;
        IEnumerator<int> materialTests = MaterialTests(game, ctx);
        while (materialTests.MoveNext()) yield return materialTests.Current;
        IEnumerator<int> merchantTests = MerchantTests(game, ctx);
        while (merchantTests.MoveNext()) yield return merchantTests.Current;
        Engine.Bind(game);

        // ---------- 5l. Multitask ----------
        IEnumerator<int> multiTests = MultiTests(game, ctx);
        while (multiTests.MoveNext()) yield return multiTests.Current;
        Engine.Bind(game);

        // ---------- 5m. saved queues, share codes, the count-down goal ----------
        IEnumerator<int> savedTests = SavedQueueTests(game, ctx);
        while (savedTests.MoveNext()) yield return savedTests.Current;
        Engine.Bind(game);

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
        Check("menu sayfalari", mod.Menu.PageCount == 9, string.Join(", ", Enumerable.Range(0, mod.Menu.PageCount).Select(mod.Menu.PageTitle)));
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
        // The whole suite no longer fits in ten minutes: "-Only main" stops here, "-Only offline" runs the rest.
        if (System.Environment.GetEnvironmentVariable("MML_ONLY") == "main") yield break;
        IEnumerator<int> offlineRun = OfflineSpeed(game, ctx);
        while (offlineRun.MoveNext()) yield return offlineRun.Current;
    }
}
