---
title: Home
layout: home
nav_order: 1
description: "Install mods for Melvor Idle 2 and write your own: the guide to Melvor Mod Loader."
permalink: /
---

# Melvor Idle 2 Modding Wiki
{: .fs-9 }

Mods for Melvor Idle 2: how to install them, and how to make your own.
{: .fs-6 .fw-300 }

[I want to play with mods](players/){: .btn .btn-primary .fs-5 .mb-4 .mb-md-0 .mr-2 }
[I want to make a mod](modding/){: .btn .fs-5 .mb-4 .mb-md-0 }

---

## What this is

**Melvor Mod Loader** lets Melvor Idle 2 load mods. You unzip it into the game folder once, and from then on
the game loads every mod you put in its `mods` folder. None of the game's own files are changed, nothing is
installed anywhere else, and taking it out again means deleting two items.

This wiki is the manual for both sides:

| You are | Start here | You will learn |
|:--|:--|:--|
| a **player** | [Installing the loader and mods](players/install) | where to unzip what, how to check it works, how to remove it |
| a **player** with a problem | [When something goes wrong](players/troubleshooting) | the log file, game updates, the usual mistakes |
| a **mod author** | [Your first mod](modding/first-mod) | from an empty folder to your own text on the game screen, in ten minutes |
| a mod author who **knows C#** | [Inside the game](modding/inside-the-game) and the [Cookbook](modding/cookbook) | where the bank, skills, combat and bonuses live, and tested code for everyday tasks |

## Why mods for this game are pleasant to write

Melvor Idle 2 is written in **C#** on the **Godot** engine. A mod is an ordinary C# library that runs *inside*
the game, next to the game's own code. There is no scripting layer and no wrapper to learn: you use the game's
real classes, your editor auto-completes them, and the compiler tells you when something is wrong.

```csharp
public class HelloMelvor : IMod
{
    public void OnLoad(ModContext ctx) => ctx.Log("Hello, Melvor!");
}
```

That is a complete mod. [Your first mod](modding/first-mod) builds it and grows it into something you can see.

## Mods you can install today

| Mod | What it does |
|:--|:--|
| [Cheat Menu](https://www.nexusmods.com/melvoridle2/mods/3) | 60+ settings in one window: XP and speed multipliers, bank slots, combat, a loot filter |
| [Bulk Sell](https://www.nexusmods.com/melvoridle2/mods/4) | "Bulk sell", "Sell junk", "Bury all", "Open all" buttons in the bank |
| [Action Queue](https://www.nexusmods.com/melvoridle2/mods/5) | Chain actions with goals, plan "100 of this item" in one click, save and share queues |
| [Multitask](https://www.nexusmods.com/melvoridle2/mods/8) | Keep your last two or three actions running at the same time |
| [Save Backup](https://www.nexusmods.com/melvoridle2/mods/6) | A dated copy of your saves every time the game starts |

More on the [mods page](players/mods). Made a mod yourself? Tell us on
[Discord](https://discord.gg/q6aexZZSAf) and it goes on the list.

## Status

{: .note }
> Melvor Idle 2 is in **Early Access**. Everything here was written for and tested with **game version
> 0.11.6-alpha.1** and **Melvor Mod Loader 0.1.3**. A game update can break mods until they are updated; the
> loader tells you in its log when that happens.

This wiki is community-made and unofficial. It is not affiliated with Games by Malcs or Jagex.
The loader and every mod listed above are open source: [github.com/falorfrozen-cmd/MelvorModLoader](https://github.com/falorfrozen-cmd/MelvorModLoader).
