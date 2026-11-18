---
title: How loading works
parent: For mod authors
nav_order: 2
---

# How loading works
{: .no_toc }

What happens between "the player starts the game" and "your `OnLoad` runs", and why it matters for your mod.
{: .fs-5 .fw-300 }

1. TOC
{:toc}

---

## The short version

1. Godot starts and reads `override.cfg` next to the game. That file names the loader's DLL as the game's
   script assembly.
2. The loader starts the game **exactly** the way the game would start itself, with the game's own DLL,
   untouched.
3. On the first frame, the loader loads every mod and calls `OnLoad` on each. This is before the main menu is
   shown.
4. From then on the loader raises your `Frame` event every rendered frame, except while the game computes
   offline progress.

## Step by step

### 1. `override.cfg`

Godot reads an `override.cfg` that sits next to the executable and lets it override the game's project
settings. The loader's file sets one value: the name of the .NET assembly Godot starts.

```ini
[dotnet]
project/assembly_name="MelvorModLoader"
```

Godot then looks for that assembly in a data folder named after it:
`data_MelvorModLoader_windows_x86_64\MelvorModLoader.dll`. That folder also holds a complete copy of the .NET
10 runtime, the same version the game ships, so nothing outside the game folder is needed or changed.

### 2. Starting the game

Godot calls the loader where it would normally call the game. The loader:

- starts its log, `MelvorModLoader.log` in the game folder (rewritten on every start);
- finds the game's data folder, `data_GamesByMalcs.Melvor2_windows_x86_64`;
- teaches .NET where to find the game's DLLs and, later, each mod's folder;
- performs the four standard steps every Godot C# game performs at start, with the game's own assembly as the
  one that holds the scripts.

At this point the game runs normally. It does not know the loader exists.

### 3. Loading mods

On the first frame (when Godot's scene tree exists), the loader:

1. Lists every `*.dll` directly in `mods\` **and** in each folder one level below it, sorted by path.
   The convention is one folder per mod: `mods\YourMod\YourMod.dll`.
2. Adds every one of those folders to the places .NET looks for DLLs. A library your mod ships next to it
   (`mods\YourMod\SomeLibrary.dll`) is found automatically.
3. For each DLL, finds every **public, non-abstract class that implements `IMod`**, creates one instance with
   its parameterless constructor, and calls `OnLoad(ctx)` on it.
4. Writes `Mod loaded: <name> <version> (<file>)` to the log, or `Mod FAILED to load: <file> -> <error>`.

{: .note }
> Load order is the alphabetical order of the paths. Do not rely on it. If two mods must talk to each other,
> let them find each other at runtime (see [two mods working together](cookbook#two-mods-working-together)).

{: .warning }
> `OnLoad` runs **before the main menu**. No character is open yet, so `GameAccess.CurrentGame` is `null`.
> Use `OnLoad` to set things up (subscribe to `Frame`, build your window, read your settings file) and do
> the game-related work later.

### 4. Every frame

The loader subscribes to Godot's `ProcessFrame` and calls every `Frame` handler in turn. Two rules:

- **A handler that throws is switched off.** The error goes to the log as
  `[YourMod] The frame handler threw an error and was switched off: ...`, and the other mods carry on.
- **No frames during offline progress.** When the player comes back after a break, the game computes the
  missed time on a thread of its own and pauses its scene tree. The loader raises no `Frame` events then. See
  [Rules: hands off while the game computes offline progress](rules#hands-off-while-the-game-computes-offline-progress).

### 5. The green line

After loading, the loader shows `Melvor Mod Loader <version> - <n> mod` in the top-left corner for 15 seconds,
so players can see at a glance that it works and how many mods were loaded.

## What this means for your mod

| Fact | Consequence |
|:--|:--|
| One instance per `IMod` class, made once | Keep state in fields. There is no "unload". |
| `OnLoad` is before the main menu | Do not look for a character there; poll in `Frame`. |
| Mods are loaded into the game's own .NET context | You can use every public type of the game and of Godot directly. |
| Your folder is on the DLL search path | Ship the libraries you need next to your DLL. Never ship the game's or Godot's DLLs. |
| A broken mod is skipped | One failing mod does not take the others down, but test yours: a silent skip is easy to miss. |
| The DLL is locked while the game runs | Close the game before you rebuild. |

## Things a mod cannot do (yet)

- **Define Godot node classes with overridden virtual methods** (`_Process`, `_Ready`, `_Input`...). Godot
  connects those through source generators that run when the game is built, not when a mod is loaded. Use
  plain nodes plus events instead: `ctx.Frame`, Godot signals such as `Button.Pressed`, `Tree.NodeAdded`.
- **Change the game's code.** The loader does not patch methods. You work with what the game's classes expose,
  plus .NET's `[UnsafeAccessor]` for the occasional private member (see the [Cookbook](cookbook#reach-a-private-member)).
- **Run before the game.** The loader starts the game first, then the mods.
