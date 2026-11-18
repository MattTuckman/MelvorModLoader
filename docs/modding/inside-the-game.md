---
title: Inside the game
parent: For mod authors
nav_order: 4
---

# Inside the game
{: .no_toc }

A map of Melvor Idle 2's own classes: where the bank, the skills, combat, the data and the bonuses live.
{: .fs-5 .fw-300 }

{: .note }
> Everything on this page was read from game version **0.11.6-alpha.1** and checked by running code against
> it. Class and member names can change when the game updates. When something here no longer compiles, the
> decompiler (see [Tools](tools)) shows what it became.

1. TOC
{:toc}

---

## The big picture

```
Game                                  GameAccess.CurrentGame: one per open character
├── Entities          (by id)         the big parts: the bank, every skill, combat, the shop...
│   └── GameEntity
│       └── Components (by name)      what a part can do: "Inventory", "Action", "Experience"...
├── LeveledEntities                   every skill that has a level
├── Data              (DataManager)   the content: items, monsters, areas, currencies, recipes...
├── ActionManager                     what the character is doing right now
└── (Modifiers)                       every bonus in the game (private: see below)
```

The game is built from **entities** (things) made of **components** (abilities). The bank is an entity whose
components are an inventory, a wallet, item sales, burying and so on. Woodcutting is an entity with an
experience component, an action component, a mastery component... Learn this shape once and every part of the
game reads the same way.

## `Game`

`GamesByMalcs.Melvor2.Game`, from `GameAccess.CurrentGame`.

| Member | What it is |
|:--|:--|
| `string CharacterName` | The open character's name. Use it to keep settings per character. |
| `bool IsSaveLoaded` | `true` once the character is fully loaded. Check it before anything else. |
| `DataRegistry<GameEntity> Entities` | All entities, by id: `Entities.TryGetObjectById("MelvorBase:Bank", out GameEntity? e)`. |
| `ReadOnlyCollection<ILeveledEntity<ExperienceComponent>> LeveledEntities` | Every skill with a level (25 in this version), in the game's own order. |
| `DataManager Data` | Items, monsters, areas, currencies, recipes: see [Data](#data-items-monsters-currencies). |
| `ActiveActionManager ActionManager` | The running action(s): see [Actions](#actions-what-the-character-is-doing). |
| `PassiveActionManager PassiveActionManager` | Passive skills that run on their own (Farming, Ranching). |
| `QuestsEntity? Quests`, `CharacterStatisticsEntity CharacterStatistics`, `OfflineProgressEntity OfflineProgress` | Shortcuts to those entities. |
| `void PropagateTick()` | Advances the game by one tick. The game calls it 60 times a second. |
| `void RequestOfflineHours(double hours)` | Runs the offline calculation as if the player had been away. |
| `event SaveLoaded`, `event CharacterNameChanged` | When those happen. |

## Entities

The ids below are every entity in the game, as found at runtime. Every id starts with the namespace
`MelvorBase:`.

| Entity id | Class | Main components (property or name) |
|:--|:--|:--|
| `Bank` | `BankEntity` | `Inventory`, `Wallet`, `ItemSales`, `ItemOpening`, `ItemUpgrading`, `ItemBurying`, `Statistics` |
| `Combat` | `CombatEntity` | `Player`, `Enemy`, `Areas`, `Loot`, `Action`, `Food`, `Equipment`, `MagicSpells`, `Prayer`... |
| `Woodcutting`, `Mining`, `Fishing`, `Firemaking`, `Cooking`, `Herblore`, `Thieving`, `Agility`, `Astrology`, `Hunting` | `WoodcuttingEntity`... | `Experience`, `Action`, `MasteryGuild`, `Pets`, `Toolbelt`, `Tracker`, `Statistics` |
| `Smithing`, `Crafting`, `Runecrafting`, `Fletching`, `Beastcrafting` | `SmithingEntity`... | the same, with an `ArtisanActionComponent` as `Action` |
| `Farming`, `Ranching` | `FarmingEntity`, `RanchingEntity` | `Experience`, `Passive` (they run on their own) |
| `Attack`, `Strength`, `Defence`, `Ranged`, `Magic`, `Hitpoints`, `Prayer` | `AttackEntity`... | `Experience` (combat skills: the fighting itself is the `Combat` entity) |
| `Slayer` | `SlayerEntity` | `Experience`, `Tasks` |
| `Shop`, `Quests`, `CompletionLog`, `CharacterStatistics` | | what their names say |
| `OfflineProgress`, `TimeAndWeather`, `Layout`, `EventBus` | | game systems |

Two ways to reach a component:

```csharp
// typed: the entity class has a property for it
game.Entities.TryGetObjectById("MelvorBase:Bank", out GameEntity? e);
BankEntity bank = (BankEntity)e!;
int logs = bank.Inventory.GetQuantity(oakLog);

// generic: every entity has a Components dictionary, by name
foreach (var (name, component) in e!.Components)
    ctx.Log($"{name} = {component.GetType().Name}");
```

## Data: items, monsters, currencies

All content comes from the game's data package and is reached through `game.Data`:

```csharp
IReadOnlyDataRegistry<Item> items = game.Data.GetDataRegistryOutput<Item>();
Item oak = items.GetObjectById("MelvorBase:OakLog");           // throws if there is no such item
bool found = items.TryGetObjectById("MelvorBase:OakLog", out Item? maybe);
foreach (Item item in items.GetAllObjects()) { /* 1,811 items */ }
```

The same works for `Enemy` (192), `CombatArea` (64), `Currency` (3: `GoldPieces`, `SlayerCoins`,
`PrayerPoints`) and the game's other data classes. `GetObjectById` throws when the id does not exist; use
`TryGetObjectById` when you are not sure.

| Item member | What it is |
|:--|:--|
| `string Id` | `MelvorBase:OakLog`. Stable: use it in your settings files. |
| `PluralString DisplayName` | `DisplayName.GetPluralText(1)` = "Oak Log", `GetPluralText(5)` = "Oak Logs". |
| `LocalizedString? Description` | `.Text` for the current language. |
| `Texture2D Icon` | The item's picture, ready for a Godot `TextureRect`. |
| `CurrencyQuantity SellsFor` | The base price (`.Currency`, `.Quantity`). |
| `Capabilities` | What the item can do: `item.Capabilities.TryGet<FoodCapability>(out var food)`, also `BuryCapability`, `OpenableCapability`... |
| `BankTags` | The item's kinds ("Logs", "Ore"...): `tag.BankTag.DisplayName`. |

{: .tip }
> Text in the game is translated. `LocalizedString.Text` and `PluralString.GetPluralText(n)` give you the
> player's language. Compare **ids**, never names.

## The bank

`BankEntity` (`MelvorBase:Bank`).

| Member | Use |
|:--|:--|
| `Inventory.GetQuantity(item)` | How many the bank holds. |
| `Inventory.AddQuantity(item, n)` | Adds; returns how many fitted (a full bank takes fewer). |
| `Inventory.RemoveQuantity(item, n)` | Removes; returns how many were taken. |
| `Inventory.Stacks` | Every stack: `.Item`, `.Quantity`, `.IsLocked` (the player's "never sell"), `.IsPlaceholder`. |
| `Inventory.MaxStacks`, `Inventory.InventoryTabs` | Bank size, the tabs. |
| `Inventory.ItemQuantityChanged` | Event for every change (`.Item`, `.OldValue`, `.NewValue`, `.QuantityChange`). |
| `Wallet.GetQuantity(currency)`, `Wallet.AddQuantity(currency, n)`, `Wallet.AddGoldPieces(n)` | Currencies. |
| `ItemSales.GetItemStats(item)` + `ItemSales.SellItem(item, stats, n)` | Selling, with every bonus. Release the stats with `.Destroy()`. |
| `ItemSales.ItemSold` | Event: `.Item`, `.Quantity`, `.CurrencyGain`. |
| `ItemBurying.BuryItem(item, stats, n)`, `ItemOpening.OpenItem(item, n)` | Burying bones, opening chests, the game's way. |

{: .note }
> Food is eaten **from the bank**: there is no separate food inventory. The combat food component points at the
> bank inventory, so counting food means counting the bank.

## Skills and experience

```csharp
foreach (var skill in game.LeveledEntities)
{
    ExperienceComponent xp = skill.Experience;
    ctx.Log($"{skill.DisplayName.Text}: level {xp.Level} of {xp.LevelCap}, {xp.Xp:N0} XP");
}
```

| `ExperienceComponent` | |
|:--|:--|
| `Level`, `LevelCap`, `Xp` | The obvious. |
| `Table.GetXpForLevel(level)` | XP needed for a level. |
| `SetXp(value)` | Sets XP (and with it the level). |
| `XpChanged`, `LevelChanged` | Events (`.OldValue`, `.NewValue`). |

## Actions: what the character is doing

Every active skill has an **action component** (`ActionComponent`), for example `WoodcuttingActionComponent`.
`game.ActionManager` keeps the list of running actions.

| Member | Use |
|:--|:--|
| `ActionManager.ActiveActions` | The running action(s). Normally one. |
| `ActionManager.HasActiveAction` | Whether anything runs. |
| `ActionManager.TryStopActive(force: false)` | Stops everything. |
| `ActionManager.ActionStarted`, `ActionStopped` | Events. |
| `action.IsStarted`, `action.TryStart()`, `action.TryStop(force)` | One action. |
| `action.CurrentDescription` | What it works on (`.DisplayName.Text`): the tree, the recipe, the monster. |

**One at a time.** When an action starts, the game first stops every running action. That is the game's
"one thing at a time" rule, and it lives in exactly one place (`ActionComponent.TryStart`).

**Starting a specific thing** goes through each skill's own "select" call, the same call the game's buttons
use:

| Skill | Call |
|:--|:--|
| Woodcutting | `action.SelectTree(tree)` (from `action.Trees`) |
| Mining | `action.SelectRock(rock)` (from `action.Rocks`) |
| Fishing | `action.SetCurrentLocation(...)` then `action.SelectRecipe(recipe)` |
| Smithing, Crafting, Runecrafting, Fletching, Beastcrafting, Cooking, Firemaking, Herblore | `action.SelectRecipe(recipe)` then `action.TryStart()` |
| Thieving | `action.TryStartActiveRecipe(recipe)` |
| Agility | `action.SelectCourse(course)` (the player must have chosen a path) |
| Astrology | `action.StudyConstellation(constellation)` |
| Hunting | `SelectLocation`, `SelectAnimal`, then `StartTracking()` (a trap must be chosen) |
| Combat | `combat.Areas.SelectEnemy(area, enemy)` or `SelectDungeon(dungeon)` |

{: .warning }
> Two surprises, both because these calls are made for buttons:
> 1. **Selecting what already runs stops it** (the button toggles). Check `IsStarted` and what runs before you
>    select.
> 2. **The select calls do not check requirements.** The game's screens hide what the player may not do yet;
>    a mod has to check `recipe.Requirements.AreMet` (combat: `area.UnlockRequirements`, `enemy.Requirements`)
>    itself.

[ActionQueue's `Targets.cs`](https://github.com/falorfrozen-cmd/MelvorModLoader/blob/main/Mods/ActionQueue/Targets.cs)
does all of this for every skill, with the checks, if you need a complete reference.

## Combat

`CombatEntity` (`MelvorBase:Combat`).

| Member | Use |
|:--|:--|
| `Areas.SelectEnemy(area, enemy)` | Starts a fight (the game starts it by itself). |
| `Areas.SelectedEnemy`, `Areas.CurrentArea` | What is being fought. |
| `Player.Health`, `Enemy.Health` | `.Current`, `.Max`, `.Set(n)`, event `Damaged`. |
| `Enemy.Killed`, `Action.PlayerDied` | Events. |
| `Loot.EnemyLooted`, `Loot.DungeonLooted` | Events after the drops went into the bank: `.Enemy`, `.Items`, `.Currencies`. |
| `Food.SelectedFood`, `Food.SelectFood(item)`, `Food.ManualEat()` | Food. |

Slayer is its own entity: `SlayerEntity.Tasks.ActiveMasters` (each with `CurrentTask` and the game's own
`AutoSlayerEnabled` switch) and the event `Tasks.TaskCompleted`.

## Ticks and time

- The game advances in **ticks**: **60 per second**. `GameDriver` calls `Game.PropagateTick()` once per Godot
  physics frame.
- Timers inside the game (an action's interval, a monster's attack) count ticks, not real time. Speeding up
  Godot's `Engine.TimeScale` speeds up the game; calling `PropagateTick()` yourself advances it instantly.
- **Offline progress** runs the same ticks in a plain loop on a background thread, with the scene tree paused,
  when the player returns. See the [rules](rules#hands-off-while-the-game-computes-offline-progress).

## Bonuses: the modifier system

Every bonus in the game (equipment, pets, potions, upgrades, mastery) is a **modifier**: "this stat on this
component changes by this much, from this source". A mod can add its own the same way, and the game then treats
it like any other bonus: it shows up in every number, works offline and disappears when it is removed.

- The system is `Game.Modifiers` (`ModifierSystem`), a **private** property; reach it with
  `[UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Modifiers")]`.
- `Add(source, values, multiplier)`, `Update(...)`, `AddOrUpdate(...)`, `Remove(source)`.
- A **stat** is named `MelvorBase:<StatsType>` + `Name`, for example `MelvorBase:BankInventory` +
  `FlatMaxStacks` (extra bank slots), `MelvorBase:WoodcuttingAction` + `PercentInterval`, ...
  Around 300 exist; the decompiler lists them (search for `new ModifiableStat(`).
- **Global** groups affect many stats at once: `GlobalPercentXpGained`, `GlobalPercentActionInterval`.
- The values are built from the same JSON the game's data files use.

A complete, tested example: [Cookbook: add a bonus](cookbook#add-a-bonus-modifier).

{: .warning }
> The game recognises a set of modifier values **by the object**, not by what is in it. Build your
> `ModifierValues` once and keep it; calling `AddOrUpdate` with a freshly built copy adds a second bonus
> instead of changing the first.

## Saves and files

| What | Where |
|:--|:--|
| The game's user data (`user://`) | `%APPDATA%\Games By Malcs\Melvor Idle 2` |
| Saves | `...\steam\<steam id>\saves\save_<id>.sav`: Brotli-compressed binary, no checksum |
| Your mod's files | `ctx.Directory` (your mod's folder). Keep them there so "delete the folder" removes everything. |
| The game's content | `res://Data/Packages/MelvorBase.json` inside the game's package (read it in game through `game.Data`) |
| Translations | `res://Locale/*.po`, used through Godot's `TranslationServer` |

{: .danger }
> Never write into the save file yourself. Change the game through its own classes, and let the game save.

## The screens

The game's user interface is ordinary Godot nodes under `Tree.Root`. `GameView`
(`GameAccess.FindGameView()`) is the in-game screen; pages such as the bank (`BankInventoryComponentView`) are
created when they are first opened. How to add your own windows, and buttons to the game's: [Building
windows](user-interface).

## Hidden things worth knowing

- The developers left a complete **debug menu** in the game (give items, set levels, add offline time). It is
  never opened by the game itself; the [Debug Menu mod](../players/mods#debug-menu) binds it to F9.
- The game already shows **XP per hour and time to the next level** in its bottom bar, and has its own
  **auto-eat** (a shop upgrade) and **Auto Slayer** switch. Check the game before you build a mod for those.
