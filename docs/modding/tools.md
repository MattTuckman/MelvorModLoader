---
title: Tools and debugging
parent: For mod authors
nav_order: 8
---

# Tools and debugging
{: .no_toc }

How to find your way in the game's code, and how to find out what your mod is doing.
{: .fs-5 .fw-300 }

1. TOC
{:toc}

---

## A decompiler: reading the game's code

The game's code is in one file, `data_GamesByMalcs.Melvor2_windows_x86_64\GamesByMalcs.Melvor2.dll`. It is not
obfuscated (apart from a few online-service classes), so a .NET decompiler turns it back into readable C#.

- **[ILSpy](https://github.com/icsharpcode/ILSpy/releases)** is free and open source. Use a recent version:
  the game is built with .NET 10, and older ILSpy versions cannot read it.
- **dotPeek** (JetBrains, free) and **Rider**'s built-in decompiler work too.

Open the DLL and browse by namespace, or use the search to find a class or member by name.

### Finding things

| You want | Search for |
|:--|:--|
| the class behind a screen | a text shown on that screen, then the `...View` class that uses it |
| a skill's logic | `<Skill>ActionComponent`, for example `MiningActionComponent` |
| what a button does | the `...Pressed` / `...Handler` method in the view, then follow the call |
| a stat for a bonus | `new ModifiableStat("` (about 300 of them) |
| an event | `public event` in the component you care about |
| an entity id | `"MelvorBase:` |

{: .tip }
> Text you see in the game comes from translation files, so searching the DLL for it does not always work.
> Search for the English text in the game's `.po` file if you have it, or for a nearby word that is a class
> or member name.

## Your editor: auto-completion

With the project file from [Your first mod](first-mod), Visual Studio, Rider and VS Code (C# extension) read
the game's DLL and complete its classes as you type. Hover a type or press F12 / Ctrl+click to see its members;
Rider and Visual Studio decompile on the spot.

## The log

`ctx.Log("...")` writes to `MelvorModLoader.log` in the game folder, with the time and your mod's name. It is
rewritten on every start.

- Log what your mod decided and why, not only errors. "Skipped X because Y" saves hours.
- Log once, not every frame.
- Watch the file while the game runs: in PowerShell,
  `Get-Content "...\Melvor Idle 2\MelvorModLoader.log" -Wait` shows new lines as they come.

## Looking around at runtime

Small throwaway mods are the fastest way to learn something about the game:

```csharp
// every entity and its components
foreach (GameEntity e in game.Entities.GetAllObjects())
    ctx.Log($"{e.Id}: {string.Join(", ", e.Components.Keys)}");

// the children of a screen node, to find where to add a button
foreach (Node child in someNode.GetChildren())
    ctx.Log($"{child.Name} ({child.GetType().Name})");
```

## The build-start loop

1. Close the game (the DLL is locked while it runs).
2. `dotnet build -c Release` (the project builds straight into `mods\YourMod`).
3. Start the game from Steam.
4. Read the log.

Keep a test character for this. Mods change real data; do not try new code on the character you care about.
The [Save Backup](../players/mods#save-backup) mod is a good safety net while you develop.

## Speeding things up while testing

- `game.RequestOfflineHours(1)` runs one hour of offline progress at once.
- Calling `game.PropagateTick()` in a loop advances the game instantly (60 ticks = one second of game time).
  Only from the main thread, and never while the game computes offline progress.
- The game's own debug menu (the [Debug Menu mod](../players/mods#debug-menu), F9) gives items, sets levels and
  completes quests.
