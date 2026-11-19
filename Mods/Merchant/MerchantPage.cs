using System;
using System.Reflection;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Components.MasteryGuild;
using GamesByMalcs.Melvor2.Components.Milestones;
using GamesByMalcs.Melvor2.Components.Statistics;
using GamesByMalcs.Melvor2.Data;
using GamesByMalcs.Melvor2.GameMenus;
using GamesByMalcs.Melvor2.GameMenus.Footer;
using GamesByMalcs.Melvor2.GameMenus.Header;
using GamesByMalcs.Melvor2.GameMenus.Pages;
using GamesByMalcs.Melvor2.GameMenus.Widgets;
using Godot;

namespace Merchant;

/// <summary>
/// The merchant as one of the game's own pages. The game shows a page by asking it for its parts (the main view,
/// the background, what the top bar says); going to another page takes them away again. So the merchant appears
/// where the Bank and the Shop appear, under the same top bar and over the same background, and leaving him works
/// like leaving any page: a click on anything else.
/// </summary>
public sealed class MerchantPage : GamePage
{
    readonly Game _game;
    readonly MerchantWindow _window;

    public MerchantPage(DataNamespace space, Game game, MerchantWindow window) : base(space, "MerchantModPage")
    {
        _game = game;
        _window = window;
    }

    public override ActiveGamePage GetActivePage() => new Shown(_game, _window);

    /// <summary>The game's view of a character, when one is open.</summary>
    public static GameView? View(SceneTree tree) => tree.Root.GetNodeOrNull("Main/ApplicationViewContainer/GameView") as GameView;

    /// <summary>The page the game is showing right now (it keeps that to itself).</summary>
    public static GamePage? Current(GameView view)
    {
        try
        {
            object? pair = typeof(GameView).GetProperty("CurrentPage", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(view);
            return pair?.GetType().GetField("Item1")?.GetValue(pair) as GamePage;
        }
        catch { return null; }
    }

    sealed class Shown(Game game, MerchantWindow window) : ActiveGamePage
    {
        Control? _view;
        bool _background;

        public override bool HasStatistics => false;
        public override bool HasTutorial => false;

        protected override void SetMainView(GameView gameView)
        {
            _view = window.Build(game);
            gameView.EntityViewContainer.AddChild(_view);
            window.Refresh();
        }

        protected override void CleanupMainView(GameView gameView)
        {
            window.Closed();
            _view = null;
        }

        // The Shop's background and blur: he has set up his stall next to it.
        protected override void SetBackground(GameView gameView)
        {
            try
            {
                EntityPage shop = game.Data.GetObject<EntityPage>("MelvorBase:Shop");
                gameView.TimeOfDayBackground.Enable(shop.TimeOfDayBackgrounds);
                _background = true;
                gameView.BlurPanel.Visible = shop.ApplyBackgroundBlur;
            }
            catch (Exception e) { Market.Log("The page's background could not be set: " + (e.InnerException ?? e).Message); }
        }

        protected override void CleanupBackground(GameView gameView)
        {
            if (!_background) return;
            _background = false;
            try
            {
                gameView.TimeOfDayBackground.Disable();
                // (what the game's own pages do next: ask its background for an empty one)
                object? background = gameView.GetType().GetProperty("Background", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(gameView);
                background?.GetType().GetMethod("RequestEmptyBackground", Type.EmptyTypes)?.Invoke(background, null);
            }
            catch (Exception e) { Market.Log("The page's background could not be taken away: " + (e.InnerException ?? e).Message); }
        }

        protected override void SetHeader(GameHeader header)
        {
            header.SetTitle("Merchant");
            if (MerchantWindow.Picture(game) is { } picture) header.SetPageIcon(picture);
            header.MilestonesButtonDisabled = true;
            header.MasteryButtonDisabled = true;
            header.StatisticsButtonDisabled = true;
            header.TutorialButtonDisabled = true;
        }

        protected override void CleanupHeader(GameHeader header) { }

        protected override void SetWidgets(WidgetsView widgets) => widgets.Visible = false;
        protected override void CleanupWidgets(WidgetsView widgets) { }
        protected override void SetFooter(GameFooter footer) { }
        protected override void CleanupFooter(GameFooter footer) { }
        protected override void SetMilestones(MilestonesMenu milestones) { }
        protected override void CleanupMilestones(MilestonesMenu milestones) { }
        protected override void SetMasteryGuild(MasteryGuildMenu masteryGuild) { }
        protected override void CleanupMasteryGuild(MasteryGuildMenu masteryGuild) { }
        public override void SetStatistics(StatisticsMenu statistics) { }
        public override void OpenTutorial() { }
        public override void CloseExclusiveModals() { }
    }
}
