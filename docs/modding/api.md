---
title: The API
parent: For mod authors
nav_order: 3
---

# The API
{: .no_toc }

Everything the loader gives you. It is small on purpose: the rest is the game itself.
{: .fs-5 .fw-300 }

1. TOC
{:toc}

---

All types are in the namespace **`MelvorModLoader`** (`using MelvorModLoader;`), in
`data_MelvorModLoader_windows_x86_64\MelvorModLoader.dll`.

## `IMod`

```csharp
public interface IMod
{
    void OnLoad(ModContext ctx);
}
```

Implement it in a **public, non-abstract class with a parameterless constructor**. The loader creates one
instance of every such class in your DLL and calls `OnLoad` once, at game start, before the main menu.

You can have more than one `IMod` class in a DLL. Each gets its own `ModContext`.

## `ModContext`

Your mod's handle to the loader. Keep it in a field.

| Member | What it is |
|:--|:--|
| `string Name` | Your assembly's name, for example `HelloMelvor`. |
| `string Directory` | The folder your DLL was loaded from, for example `...\mods\HelloMelvor`. Keep your own files (settings, data) here. |
| `SceneTree Tree` | Godot's scene tree. Add your nodes to `Tree.Root`; check `Tree.Paused`; listen to `Tree.NodeAdded`. |
| `void Log(string message)` | Writes `[YourMod] message` to `MelvorModLoader.log`, with the time. |
| `event Action Frame` | Raised once per rendered frame. Not raised while the game computes offline progress. A handler that throws is removed. |

```csharp
public void OnLoad(ModContext ctx)
{
    string settingsFile = Path.Combine(ctx.Directory, "settings.json");
    ctx.Log($"Loaded from {ctx.Directory}");
    ctx.Frame += () => { /* runs every frame */ };
}
```

{: .tip }
> `Frame` runs about 60 times a second. Do cheap checks every frame (a key press) and expensive ones every
> few frames: `if (++_frame % 30 == 0) { ... }` is twice a second.

## `GameAccess`

Shortcuts into the running game.

| Member | What it is |
|:--|:--|
| `Game? CurrentGame` | The open character's `Game`: bank, skills, combat, data. `null` on the main menu. |
| `GameView? FindGameView()` | The in-game screen (`GamesByMalcs.Melvor2.GameMenus.GameView`). `null` on the main menu. |
| `T? FindNode<T>(Node? root = null)` | The first node of type `T` in the scene tree, or below `root`. |
| `bool IsComputingOffline` | `true` while the game computes offline progress. Do not touch the game from the main thread then. |

```csharp
Game? game = GameAccess.CurrentGame;
if (game is { IsSaveLoaded: true })
{
    // a character is open and fully loaded
}
```

{: .warning }
> `CurrentGame` searches the scene tree. Call it a few times a second, not on every frame, and do not keep
> the result for long: the player can return to the menu and open another character, and you would be
> holding the old one. Compare with `ReferenceEquals(oldGame, newGame)` to notice a change of character.

## `Loader`

| Member | What it is |
|:--|:--|
| `string Version` | The loader's version, for example `0.1.3`. |
| `IReadOnlyList<string> LoadedMods` | The names of all mods loaded so far, in load order. |

Useful to check whether another mod is installed, or to write the loader version into your own log.

## `Log`

| Member | What it is |
|:--|:--|
| `string FilePath` | Where the log is: the game folder, or the temp folder if the game folder is read-only. |
| `void Write(string message)` | Writes a line without your mod's name in front. Prefer `ctx.Log`. |

## Everything else

The rest is the game and the engine, which your project references directly:

| Assembly | Namespaces | Contains |
|:--|:--|:--|
| `GamesByMalcs.Melvor2.dll` | `GamesByMalcs.Melvor2.*` | the game: `Game`, entities, components, data, modifiers, screens |
| `GodotSharp.dll` | `Godot` | the engine: nodes, controls, input, timers, files |

Where to start in the game's classes: [Inside the game](inside-the-game).

## Versioning

The loader's assembly version is fixed at `0.1.0.0` on purpose, so a mod built against one loader keeps
loading with the next. New members may be added; existing ones are not removed or changed. The user-facing
version (`Loader.Version`, the green line, Nexus) is what changes from release to release.
