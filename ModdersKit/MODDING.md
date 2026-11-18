# Writing mods for Melvor Idle 2 with Melvor Mod Loader

This kit is for people who want to **make** mods. Players only need the loader and the mods themselves.

A mod is one C# class library (a `.dll`) that the loader loads when the game starts. Your code runs inside the
game, with direct, type-checked access to everything the game is made of: the bank, the skills, combat, every
screen. There is no scripting layer in between and nothing to learn except C# and the game's own classes.

Tested with game version 0.11.6-alpha.1 and Melvor Mod Loader 0.1.3.

---

## 1. Your first mod in five minutes

You need:

- the game with **Melvor Mod Loader** installed (start the game once and check that the green
  "Melvor Mod Loader" line appears in the top-left corner),
- the free **.NET 10 SDK** from https://dotnet.microsoft.com/download,
- any text editor. Visual Studio, Rider or VS Code give you auto-complete for the game's classes.

Steps:

1. Copy the `ExampleMod` folder from this kit anywhere you like.
2. If your game is not in `C:\Program Files (x86)\Steam\steamapps\common\Melvor Idle 2`, open
   `ExampleMod.csproj` and change the `GameDir` line.
3. Open a terminal in that folder and run:

   ```
   dotnet build -c Release
   ```

   The project builds straight into the game: `mods\ExampleMod\ExampleMod.dll`.
4. Start the game, open a character and press **F10**. A panel with your skill levels appears.

To make it your own: rename the folder, the `.csproj`, the `AssemblyName` and the namespace, then change the code.
Close the game before you rebuild (the game keeps your DLL open while it runs).

---

## 2. How loading works

- The loader looks at every `.dll` directly in `mods\` and in each folder one level below it.
  The convention is one folder per mod: `mods\YourMod\YourMod.dll`.
- In each DLL it creates one instance of every public, non-abstract class that implements `IMod` and calls
  `OnLoad` on it. That happens once, at game start, before the main menu is shown.
- A mod that fails to load is reported in the log and skipped; the game and the other mods carry on.
- The log is `MelvorModLoader.log` in the game folder. It is rewritten on every start. Look there first when
  something does not work.
- Other DLLs in your mod's folder (libraries you ship with your mod) are found automatically.

---

## 3. The API (all of it)

```csharp
namespace MelvorModLoader;

public interface IMod
{
    void OnLoad(ModContext ctx);
}

public sealed class ModContext
{
    public string Name { get; }        // your assembly name
    public string Directory { get; }   // the folder your DLL was loaded from: keep your files here
    public SceneTree Tree { get; }     // Godot's scene tree: add your nodes to Tree.Root
    public void Log(string message);   // writes "[YourMod] message" to MelvorModLoader.log
    public event Action Frame;         // once per rendered frame; a handler that throws is removed
}

public static class GameAccess
{
    public static Game? CurrentGame { get; }          // the open character's game; null on the main menu
    public static GameView? FindGameView();           // the in-game screen; null on the main menu
    public static T? FindNode<T>(Node? root = null);  // first node of a type in the scene (or below root)
    public static bool IsComputingOffline { get; }    // see "Rules" below
}
```

That is deliberately small. Everything else is the game itself (`GamesByMalcs.Melvor2.dll`) and Godot
(`GodotSharp.dll`), which your project references directly.

---

## 4. Finding your way around the game

The game is written in C# on Godot 4, so its classes can be browsed with any .NET decompiler (ILSpy is free):
open `data_GamesByMalcs.Melvor2_windows_x86_64\GamesByMalcs.Melvor2.dll`.

Where to look first:

| You want | Look at |
|---|---|
| the root object | `Game`: `Entities`, `Data`, `ActionManager`, `LeveledEntities`, `CharacterName`, `IsSaveLoaded` |
| the bank | entity `MelvorBase:Bank` (`BankEntity`): `Inventory`, `Wallet`, `ItemSales`, `ItemBurying`, `ItemOpening` |
| a skill | entity `MelvorBase:Woodcutting` etc.; each has an `Action` component and `Experience` |
| combat | entity `MelvorBase:Combat` (`CombatEntity`): `Player`, `Enemy`, `Areas`, `Loot`, `Action` |
| all items, monsters, recipes | `game.Data.GetDataRegistryOutput<Item>().GetAllObjects()` (same for `Enemy`, `CombatArea`, ...) |
| what is running now | `game.ActionManager.ActiveActions` |
| bonuses (what items and pets give) | the modifier system, `GamesByMalcs.Melvor2.Modifiers` |

Small recipes:

```csharp
// The bank
game.Entities.TryGetObjectById("MelvorBase:Bank", out GameEntity? e);
var bank = (BankEntity)e!;
int logs = bank.Inventory.GetQuantity(item);
bank.Inventory.AddQuantity(item, 10);

// React to something happening (events are everywhere in the game's classes)
game.Entities.TryGetObjectById("MelvorBase:Combat", out GameEntity? c);
((CombatEntity)c!).Loot.EnemyLooted += (_, drop) => { /* drop.Items, drop.Currencies */ };

// Put a button into one of the game's own screens: wait for the screen to be created
ctx.Tree.NodeAdded += node => { if (node is BankInventoryComponentView view) { /* add your button to it */ } };

// Keep per-character data: one small JSON file next to your DLL
string file = Path.Combine(ctx.Directory, "mydata.json");
```

The source of our own mods (Cheat Menu, Bulk Sell, Action Queue, Save Backup) is the best worked example of all
of this. See the links at the end.

---

## 5. Rules that keep mods (and saves) healthy

1. **`CurrentGame` is null on the main menu** and changes when the player opens another character.
   Check `game is { IsSaveLoaded: true }` and re-read it instead of keeping it forever.
2. **Hands off while the game computes offline progress.** When the player returns after a break the game shows
   "Loading Offline Progress..." and runs its ticks on a thread of its own. The loader does not raise `Frame`
   during that time. If you use Godot signals or timers of your own, check `GameAccess.IsComputingOffline` and do
   nothing while it is true. Two threads inside the game at once will stall or corrupt it.
3. **Your event handlers can run on the game's thread.** Game events you subscribed to (a monster was looted, an
   item was sold) are also raised during the offline calculation. Changing game data there is fine, it is the
   game's own thread, but do not touch the user interface from such a handler.
4. **Go through the game's own functions.** Selling with `ItemSales.SellItem`, starting an action with the skill's
   own select call, and so on, keeps statistics, events and bonuses correct.
5. **Respect the player's marks.** A locked bank stack (`stack.IsLocked`) means "never sell this".
6. **Release what you create.** Stat objects such as `bank.ItemSales.GetItemStats(item)` must be given back with
   `.Destroy()` when you are done, the way the game does it.
7. **The game's theme has quirks.** Its pop-up panels are transparent and its check boxes are huge. Give your
   panels a `StyleBoxFlat` (the example does) or use your own `Theme`.
8. **Private members.** Prefer public API. If you must reach a private one, use .NET's `[UnsafeAccessor]`
   (typed, no reflection) and expect it to break when the game updates.
9. **A game update can break any mod.** The game is in alpha. Say in your description which game version you
   tested with.

---

## 6. Publishing your mod

- Pack it as a zip that is extracted into the game folder: `mods\YourMod\YourMod.dll` (plus a readme).
- No installers, no `.exe`, no `.bat`. A plain zip is what players trust and what virus scanners like.
- List **Melvor Mod Loader** as a requirement and say which loader and game version you tested with.
- Never include the game's own files.
- Uninstalling must be "delete the folder". Do not write into the save file what you cannot take back.

---

## 7. Help, source code, contact

- Discord (questions, releases, feedback): https://discord.gg/q6aexZZSAf
- GitHub: https://github.com/falorfrozen-cmd
- The loader's log: `MelvorModLoader.log` in the game folder. Include it when you ask for help.
