---
title: For mod authors
nav_order: 3
has_children: true
permalink: /modding/
---

# For mod authors

How to write mods for Melvor Idle 2 with Melvor Mod Loader.
{: .fs-5 .fw-300 }

## What a mod is

A mod is a **C# class library** (a `.dll`). The loader loads it when the game starts and calls one method on
it. From then on your code runs inside the game process, next to the game's own code, with the same access the
game has to itself: the bank, every skill, combat, the screens, the save data.

You do not learn a modding language. You learn two things:

1. **The loader's API.** It is deliberately tiny: one interface, one context object, a few helpers.
   [The whole thing fits on one page.](api)
2. **The game's own classes.** The game is not obfuscated, so a free decompiler shows all of it.
   [Inside the game](inside-the-game) is the map, and the [Cookbook](cookbook) has tested code for the
   everyday tasks.

## What you need

- Melvor Idle 2 on Steam (Windows), with [Melvor Mod Loader installed](../players/install).
- The free **.NET 10 SDK** from [dotnet.microsoft.com](https://dotnet.microsoft.com/download). The game runs
  on .NET 10, so your mod must target `net10.0`.
- An editor. **Visual Studio**, **Rider** and **VS Code** (with the C# extension) all give you
  auto-completion for the game's classes. Notepad works too; you just do not get the help.
- Some C#. If you have written a little C# or Java before, you are fine.

## A suggested path

| Step | Page | Time |
|:--|:--|:--|
| 1 | [Your first mod](first-mod): build one, see it in the game | 10 min |
| 2 | [How loading works](how-it-works): what happens at start, where files go | 5 min |
| 3 | [The API](api): everything the loader gives you | 5 min |
| 4 | [Inside the game](inside-the-game): entities, components, actions, data, bonuses | 20 min |
| 5 | [Cookbook](cookbook): copy-paste code for items, skills, events, bonuses, settings | reference |
| 6 | [Building windows](user-interface): your own UI with Godot, and buttons in the game's screens | 15 min |
| 7 | [Rules and pitfalls](rules): what keeps mods and saves healthy | 5 min |
| 8 | [Tools and debugging](tools): decompiler, log, finding things | reference |
| 9 | [Publishing](publishing): zip layout, Nexus Mods, versions | 5 min |

## Learn from real mods

Every mod on [the mods page](../players/mods) is open source. They are the best worked examples there are:

| Mod | Good example of |
|:--|:--|
| [CheatMenu](https://github.com/falorfrozen-cmd/MelvorModLoader/tree/main/Mods/CheatMenu) | the modifier (bonus) system, a large settings window, per-character settings |
| [BulkSell](https://github.com/falorfrozen-cmd/MelvorModLoader/tree/main/Mods/BulkSell) | adding buttons to one of the game's own screens, selling and burying through the game |
| [ActionQueue](https://github.com/falorfrozen-cmd/MelvorModLoader/tree/main/Mods/ActionQueue) | starting and watching every kind of skill action, reading recipes, combat and slayer |
| [Multitask](https://github.com/falorfrozen-cmd/MelvorModLoader/tree/main/Mods/Multitask) | reaching a private part of the game safely, two mods working together |
| [SaveBackup](https://github.com/falorfrozen-cmd/MelvorModLoader/tree/main/Mods/SaveBackup) | the smallest useful mod: a few lines at start |
| [ExampleMod](https://github.com/falorfrozen-cmd/MelvorModLoader/tree/main/ModdersKit/ExampleMod) | a hotkey and a small panel, commented line by line |
