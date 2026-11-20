using System.Linq;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Entities;
using GamesByMalcs.Melvor2.Entities.Bank;
using Godot;
using MelvorModLoader;

namespace ExampleMod;

/// <summary>
/// A complete, small mod: F10 (or the top bar's Mods menu) shows a panel with the open character's skill levels and
/// bank use. It touches every part of the loader's API once, so it is a good file to copy and start from.
/// </summary>
public class ExampleMod : IMod          // the loader creates one instance of every public class that implements IMod
{
    ModContext _ctx = null!;
    ModWindow _window = null!;
    CanvasLayer? _layer;
    Label? _title;
    GridContainer? _grid;
    int _frame;

    // Called once, when the game starts (the main menu is not even visible yet). Keep it quick.
    public void OnLoad(ModContext ctx)
    {
        _ctx = ctx;
        // Listed in the loader's Mods menu; F10 opens it too, and the player can change or switch off that key.
        _window = ctx.AddWindow("Example: skill levels", Toggle, Key.F10);
        ctx.Frame += OnFrame;                                      // run OnFrame once per rendered frame
        ctx.Log($"Ready. Press {_window.KeyLabel} while a character is open.");   // goes to MelvorModLoader.log in the game folder
    }

    void OnFrame()
    {
        // Refresh the panel about once a second while it is visible.
        if (_layer is { Visible: true } && ++_frame % 60 == 0) Refresh();
    }

    void Toggle()
    {
        if (_layer is { Visible: true }) { _layer.Visible = false; return; }

        // The game object exists only while a character is open; on the main menu this is null.
        Game? game = GameAccess.CurrentGame;
        if (game is not { IsSaveLoaded: true })
        {
            _ctx.Log("Open a character first.");
            return;
        }
        if (_layer == null) BuildPanel();
        Refresh();
        _layer!.Visible = true;
    }

    // User interface is ordinary Godot: create nodes and add them to the scene tree.
    void BuildPanel()
    {
        _layer = new CanvasLayer { Layer = 100, Visible = false };     // a layer above the game's own screens
        var center = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        var panel = new PanelContainer();
        var box = new StyleBoxFlat { BgColor = new Color(0.08f, 0.09f, 0.11f, 0.97f), BorderColor = new Color(0.9f, 0.75f, 0.35f) };
        box.SetBorderWidthAll(1);
        box.SetCornerRadiusAll(6);
        box.SetContentMarginAll(16);
        panel.AddThemeStyleboxOverride("panel", box);                 // the game's own pop-up style is transparent

        var column = new VBoxContainer();
        _title = new Label();
        _grid = new GridContainer { Columns = 6 };                    // three "skill, level" pairs per row
        _grid.AddThemeConstantOverride("h_separation", 18);
        column.AddChild(_title);
        column.AddChild(new HSeparator());
        column.AddChild(_grid);
        panel.AddChild(column);
        center.AddChild(panel);
        _layer.AddChild(center);
        _ctx.Tree.Root.AddChild(_layer);
    }

    void Refresh()
    {
        Game? game = GameAccess.CurrentGame;
        if (game is not { IsSaveLoaded: true } || _grid == null) { if (_layer != null) _layer.Visible = false; return; }

        // Entities are looked up by id. The bank holds the items and the currencies.
        string bankLine = "";
        if (game.Entities.TryGetObjectById("MelvorBase:Bank", out GameEntity? entity) && entity is BankEntity bank)
            bankLine = $"   |   Bank: {bank.Inventory.Stacks.Count(s => s.Quantity > 0)} of {bank.Inventory.MaxStacks} slots used";
        string key = _window.KeyLabel == "" ? "" : $"   |   {_window.KeyLabel} closes";
        _title!.Text = $"{game.CharacterName}{bankLine}{key}";

        // Every skill is an entity with a level. The game's classes are used directly, with full type checking.
        foreach (Node old in _grid.GetChildren()) old.QueueFree();
        foreach (var skill in game.LeveledEntities)
        {
            _grid.AddChild(new Label { Text = skill.DisplayName.Text });
            _grid.AddChild(new Label { Text = skill.Experience.Level.ToString(), HorizontalAlignment = HorizontalAlignment.Right, Modulate = new Color(0.9f, 0.75f, 0.35f) });
        }
    }
}
