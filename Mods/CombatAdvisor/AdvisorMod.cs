using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Components.CombatAreas;
using GamesByMalcs.Melvor2.Entities.Combat;
using Godot;
using MelvorModLoader;

namespace CombatAdvisor;

/// <summary>
/// Combat Advisor: in the game's combat area menu every monster card gets a line with the advisor's verdict and
/// the kills, XP and food per hour for your setup; its "Advisor" button opens the full window. Nothing in the game
/// is changed: the fights are worked out on a private copy of your bonuses.
/// </summary>
public class AdvisorMod : IMod
{
    public static AdvisorMod? Instance { get; private set; }

    ModContext _ctx = null!;
    AdvisorWindow? _window;
    CombatAreasMenu? _menu;
    readonly Dictionary<EnemyOptionsPanel, Strip> _strips = new();
    int _frame;
    string _signature = "";
    Setup? _setup;

    static readonly PropertyInfo? s_enemy = typeof(EnemyOptionsPanel).GetProperty("Enemy", BindingFlags.Instance | BindingFlags.NonPublic);
    static readonly PropertyInfo? s_area = typeof(EnemyOptionsPanel).GetProperty("Area", BindingFlags.Instance | BindingFlags.NonPublic);
    static readonly PropertyInfo? s_dungeon = typeof(EnemyOptionsPanel).GetProperty("Dungeon", BindingFlags.Instance | BindingFlags.NonPublic);
    static readonly PropertyInfo? s_unlocked = typeof(EnemyOptionsPanel).GetProperty("UnlockedContainer", BindingFlags.Instance | BindingFlags.NonPublic);

    public AdvisorWindow? Window => _window;
    /// <summary>The strips on the cards now on show, by the monster they show (for the tests).</summary>
    public IEnumerable<Strip> Strips => _strips.Values.Where(s => GodotObject.IsInstanceValid(s.Root) && s.Root.IsVisibleInTree());

    public void OnLoad(ModContext ctx)
    {
        Instance = this;
        _ctx = ctx;
        Forecast.Log = ctx.Log;
        _window = new AdvisorWindow(ctx.Tree);
        ctx.Frame += Tick;
        ctx.Log("Ready. Open the combat area menu: each monster card shows the advisor's line.");
    }

    void Tick()
    {
        if (_ctx.Tree.Paused) return;
        Game? game = GameAccess.CurrentGame;
        if (game is not { IsSaveLoaded: true })
        {
            if (_window is { Visible: true }) _window.Close();
            _strips.Clear();
            _menu = null;
            return;
        }
        Forecast.Update();
        if (_window is { Visible: true })
        {
            if (Input.IsActionJustPressed("ui_cancel")) _window.Close();
            else _window.Tick();
        }
        _frame++;
        if (_menu == null || !GodotObject.IsInstanceValid(_menu))
        {
            if (_frame % 30 != 0) return;
            _menu = GameAccess.FindNode<CombatAreasMenu>();
            if (_menu == null) return;
        }
        if (!_menu.IsVisibleInTree()) return;

        if (_setup == null || _frame % 20 == 0)
        {
            _setup = Forecast.Current(game);
            if (_setup == null) return;
            if (_setup.Signature != _signature)
            {
                _signature = _setup.Signature;
                foreach (Strip s in _strips.Values) s.Bind("", s.Area!, s.Enemy!);   // everything again with the new setup
            }
        }
        if (_frame % 5 == 0) FindCards(_menu);

        // the strips that wait for their numbers: as many as fit in a few milliseconds of this frame
        ulong start = Time.GetTicksMsec();
        foreach (Strip strip in _strips.Values)
        {
            if (strip.Filled || strip.Area == null || strip.Enemy == null || !GodotObject.IsInstanceValid(strip.Root) || !strip.Root.IsVisibleInTree()) continue;
            Outlook o = Forecast.For(game, _setup, strip.Area, strip.Enemy);
            if (o.Ready) strip.Show(o, _setup);          // (one played out blow by blow fills in a moment later)
            if (Time.GetTicksMsec() - start > 8) break;
        }
    }

    /// <summary>The cards of the area on show: each gets its strip, bound to the monster the card shows now.</summary>
    void FindCards(Node root)
    {
        foreach (EnemyOptionsPanel card in All<EnemyOptionsPanel>(root))
        {
            if (!card.IsVisibleInTree()) continue;
            var enemy = s_enemy?.GetValue(card) as Enemy;
            var area = s_area?.GetValue(card) as CombatArea;
            bool dungeon = s_dungeon?.GetValue(card) != null;
            if (!_strips.TryGetValue(card, out Strip? strip) || !GodotObject.IsInstanceValid(strip.Root))
            {
                if (enemy == null || area == null || dungeon) continue;
                strip = new Strip();
                strip.Opened += (a, e) => _window?.Open(GameAccess.CurrentGame!, a, e);
                if (!Attach(card, strip.Root)) { strip.Root.QueueFree(); continue; }
                _strips[card] = strip;
            }
            // a dungeon's waves get no strip (the advisor works out single monsters)
            strip.Root.Visible = enemy != null && area != null && !dungeon;
            if (!strip.Root.Visible) continue;
            strip.Bind(_signature + "@" + area!.Id + "/" + enemy!.Id, area, enemy);
        }
    }

    /// <summary>Puts the strip at the bottom of the card's content (the column the card's lines are in).</summary>
    static bool Attach(EnemyOptionsPanel card, Control strip)
    {
        if (s_unlocked?.GetValue(card) is not Control unlocked) return false;
        // the card's main column (name, style, drops) sits left of the Fight buttons: the strip goes last in it
        Node? column = unlocked.FindChild("MainContainer", true, false) as VBoxContainer
                       ?? unlocked.GetChildren().OfType<VBoxContainer>().FirstOrDefault();
        if (column == null)
        {
            Node? only = unlocked.GetChildCount() == 1 ? unlocked.GetChild(0) : null;
            if (only is not Control inner) return false;
            var wrap = new VBoxContainer { Name = "CombatAdvisorColumn", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            wrap.AddThemeConstantOverride("separation", 6);
            unlocked.RemoveChild(inner);
            wrap.AddChild(inner);
            unlocked.AddChild(wrap);
            column = wrap;
        }
        column.AddChild(strip);
        return true;
    }

    static IEnumerable<T> All<T>(Node root) where T : Node
    {
        if (root is T t) yield return t;
        foreach (Node c in root.GetChildren())
            foreach (T x in All<T>(c)) yield return x;
    }
}
