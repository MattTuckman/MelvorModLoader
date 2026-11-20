using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Components.Actions;
using GamesByMalcs.Melvor2.Components.Actions.StopOptions;
using GamesByMalcs.Melvor2.Components.ActionUntilX;
using GamesByMalcs.Melvor2.Components.ArtisanAction;
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.Entities.Cooking;
using GamesByMalcs.Melvor2.Entities.Herblore;
using Godot;
using MelvorModLoader;

namespace ActionQueue;

/// <summary>
/// Ties the queue to the game's own recipe pages (Smithing, Crafting, Fletching, Runecrafting, Beastcrafting,
/// Cooking, Herblore). Next to the game's Create button sits a "+ Queue" button that puts the chosen recipe, with the
/// stop option chosen on the page ("Recipe Creations 50", "Reach Level 30"...), at the end of the queue. With the
/// setting "The game's Create button adds to the queue" on, Create itself does the same.
///
/// How: the page's Create button calls the page's own handler through its "pressed" signal. That one connection is
/// taken off and put back wrapped: the wrapper either queues the recipe or calls the game's handler unchanged, so
/// when it queues, the game starts nothing and stops nothing.
/// </summary>
public static class GameButtons
{
    const string HookedMeta = "ActionQueueHooked";
    const string GameTextMeta = "ActionQueueGameText";
    const string HandlerName = "CreateRecipeButtonPressedHandler";
    public const string QueueButtonName = "ActionQueueAddButton";

    static readonly List<Control> s_pending = new();
    static readonly List<(Control Page, Button Create, Button Add)> s_hooked = new();

    /// <summary>Shows a short line to the player (the mod puts it on screen).</summary>
    public static Action<string> Tell { get; set; } = _ => { };

    static bool IsRecipePage(Node node) =>
        node is ArtisanSelectedRecipeContainer or CookingSelectedRecipeContainer or HerbloreSelectedRecipeContainer;

    public static void Watch(SceneTree tree)
    {
        tree.NodeAdded += node => { if (IsRecipePage(node)) lock (s_pending) s_pending.Add((Control)node); };
    }

    /// <summary>For tests: the recipe pages hooked so far (Create button, "+ Queue" button).</summary>
    public static IReadOnlyList<(Control Page, Button Create, Button Add)> Hooked
    {
        get { Refresh(); return s_hooked; }
    }

    /// <summary>
    /// Hooks pages that are ready by now (the game connects its handler in _Ready) and keeps the Create button's
    /// text in step with the setting. Called a few times per second.
    /// </summary>
    public static void Refresh()
    {
        lock (s_pending)
        {
            for (int i = s_pending.Count - 1; i >= 0; i--)
            {
                Control page = s_pending[i];
                if (!GodotObject.IsInstanceValid(page)) { s_pending.RemoveAt(i); continue; }
                if (!page.IsNodeReady()) continue;
                s_pending.RemoveAt(i);
                try { Hook(page); }
                catch (Exception e) { Queue.Log("Could not attach to a recipe page: " + e.Message); }
            }
        }
        s_hooked.RemoveAll(h => !GodotObject.IsInstanceValid(h.Page) || !GodotObject.IsInstanceValid(h.Create));
        foreach ((Control page, Button create, Button add) in s_hooked)
        {
            if (!create.HasMeta(GameTextMeta)) continue;
            string gameText = create.GetMeta(GameTextMeta).AsString();
            string want = Queue.GameCreate ? "Add to queue" : gameText;
            if (create.Text != want)
            {
                // The game may have put its own (translated) text back: that becomes the text to restore.
                if (create.Text != "Add to queue") create.SetMeta(GameTextMeta, create.Text);
                create.Text = Queue.GameCreate ? "Add to queue" : create.GetMeta(GameTextMeta).AsString();
            }
            add.Disabled = SelectedTarget(page) == null;
        }
    }

    static void Hook(Control page)
    {
        Button? create = page.Get("CreateRecipeButton").As<Button>();
        if (create == null || create.HasMeta(HookedMeta)) return;

        // Find the game's own connection (the page's handler) and keep it.
        Action? original = null;
        Callable originalCallable = default;
        foreach (Godot.Collections.Dictionary connection in create.GetSignalConnectionList(BaseButton.SignalName.Pressed))
        {
            Callable callable = connection["callable"].AsCallable();
            if (callable.Delegate is Action handler && handler.Method.Name == HandlerName && ReferenceEquals(handler.Target, page))
            {
                original = handler;
                originalCallable = callable;
                break;
            }
        }
        if (original == null) { Queue.Log($"{page.GetType().Name}: the Create button's handler was not found; Create is left alone."); return; }

        create.Disconnect(BaseButton.SignalName.Pressed, originalCallable);
        Action game = original;
        create.Pressed += () => CreatePressed(page, game);
        create.SetMeta(HookedMeta, true);
        create.SetMeta(GameTextMeta, create.Text);

        var add = new Button
        {
            Name = QueueButtonName,
            Text = "+ Queue",
            ThemeTypeVariation = create.ThemeTypeVariation,
            SizeFlagsVertical = create.SizeFlagsVertical,
            TooltipText = "Action Queue: put this recipe at the end of the queue, with the stop option chosen above.\n"
                + "Works even while you lack the materials: the queue waits for them or skips it when its turn comes.",
            FocusMode = Control.FocusModeEnum.None,
        };
        add.Pressed += () => QueuePressed(page);
        create.AddSibling(add);
        s_hooked.Add((page, create, add));
    }

    static void CreatePressed(Control page, Action game)
    {
        if (!Queue.GameCreate || GameAccess.CurrentGame is not { IsSaveLoaded: true })
        {
            game();
            return;
        }
        if (!TryMakeStep(page, out Step? step, out string why))
        {
            // Not something the queue can hold. With work in the queue nothing is started (the button says "Add to
            // queue": starting it would push the queue's own step out). With an empty queue the game does its usual.
            if (Queue.Running || Queue.Steps.Any(s => s.Run != RunState.Done)) { Tell($"Action Queue: not added: {why}"); return; }
            game();
            if (why != "") Tell($"Action Queue: {why} The game started it as usual.");
            return;
        }
        Tell("Action Queue: " + Queue.AddFromGame(step!));
    }

    static void QueuePressed(Control page)
    {
        if (GameAccess.CurrentGame is not { IsSaveLoaded: true } game) return;
        Queue.Bind(game);
        Tell("Action Queue: " + (TryMakeStep(page, out Step? step, out string why) ? Queue.AddFromGame(step!) : why));
    }

    // ---------------------------------------------------------------- reading the page

    static T? Private<T>(object page, string name) where T : class =>
        page.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(page) as T;

    /// <summary>The page's skill and the recipe selected on it (null when none is selected).</summary>
    static (ActionComponent Action, Target Target)? SelectedTarget(Control page)
    {
        ActionComponent? action = Private<ActionComponent>(page, "Action");
        string? id = action switch
        {
            ArtisanActionComponent c => c.SelectedRecipe?.Recipe.Id,
            CookingActionComponent c => c.SelectedRecipe?.Recipe.Id,
            HerbloreActionComponent c => c.SelectedRecipe?.Recipe.Id,
            _ => null,
        };
        if (action == null || id == null) return null;
        Target? target = Targets.All(action).FirstOrDefault(t => t.Id == id);
        return target == null ? null : (action, target);
    }

    /// <summary>
    /// Turns the page's choice into a queue step: the selected recipe, and the stop option chosen on the page.
    /// Read before anything is started, as the game resets the option when the selected recipe changes.
    /// </summary>
    public static bool TryMakeStep(Control page, out Step? step, out string why)
    {
        step = null;
        why = "";
        if (SelectedTarget(page) is not var (action, target)) { why = "choose a recipe first."; return false; }
        string skill = action.Entity.DisplayName.Text;
        var made = new Step { Entity = action.Entity.Id, TargetId = target.Id, Extra = target.Extra, Name = $"{skill}: {target.Name}", Until = Until.Stops };
        IStopOption? option = Private<IStopOptionProvider>(page, "UntilX")?.SelectedStopOption;
        switch (option)
        {
            case null:
                break;
            case BaseRecipeCreationsStopOption o:
                made.Until = Until.Times;
                made.Amount = o.Count;
                break;
            case LevelStopOption o:
                made.Until = Until.Level;
                made.Amount = o.Level;
                made.Subject = made.SubjectName = skill;
                break;
            case ItemCurrencyQuantityStopOption o:
                if (!o.IsItemSelected || o.SelectedItemIndex >= o.AvailableItems.Count)
                {
                    why = "a stop option that counts money cannot be queued yet.";
                    return false;
                }
                Item item = o.SelectedItem;
                made.Amount = o.Quantity;
                made.Subject = item.Id;
                made.SubjectName = item.DisplayName.GetPluralText(1);
                made.Until = option switch
                {
                    OwnProductStopOption => Until.Item,
                    GainProductStopOption => Until.Made,
                    IngredientsRemainingStopOption => Until.ItemLeft,
                    SpendIngredientStopOption => Until.Spent,
                    _ => Until.Stops,
                };
                if (made.Until == Until.Stops) { why = $"the stop option '{option.DisplayName.Text}' is new to the queue."; return false; }
                break;
            default:
                why = $"the stop option '{option.DisplayName.Text}' is new to the queue.";
                return false;
        }
        step = made;
        return true;
    }
}
