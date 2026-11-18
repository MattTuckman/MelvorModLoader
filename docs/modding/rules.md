---
title: Rules and pitfalls
parent: For mod authors
nav_order: 7
---

# Rules and pitfalls
{: .no_toc }

What keeps mods, the game and players' saves healthy. Each rule here was learned the hard way.
{: .fs-5 .fw-300 }

1. TOC
{:toc}

---

## Hands off while the game computes offline progress

When the player returns after a break, the game shows **"Loading Offline Progress..."** and simulates the
missed time: it runs `Game.PropagateTick()` in a plain loop **on a background thread**, with the scene tree
paused, as fast as it can.

If anything ticks or changes the game from the main thread at the same time, two threads work on the same data.
The calculation crawls ("Ticks per Second: 0") or the state is damaged.

- The loader does not raise `ctx.Frame` during this time, so code in `Frame` is safe.
- Your **own** Godot timers, signals and `Tree` events are still raised. Check
  `GameAccess.IsComputingOffline` (or `ctx.Tree.Paused`) in them and do nothing while it is true.
- Game events you subscribed to **are** raised during the calculation, on the background thread. Changing game
  data there is fine, it is the game's own thread. Do not touch the user interface from them.

{: .danger }
> Never call `Game.PropagateTick()` yourself while `IsComputingOffline` is true. A speed-up mod that did exactly
> that made offline progress take minutes instead of a fraction of a second.

## `CurrentGame` changes

`GameAccess.CurrentGame` is `null` on the main menu and becomes a **new object** every time a character is
opened. Check `game is { IsSaveLoaded: true }` before use, compare with `ReferenceEquals` to notice a switch,
and subscribe to events per game. See [Notice a character being opened or closed](cookbook#notice-a-character-being-opened-or-closed).

## Go through the game's own functions

Sell with `ItemSales.SellItem`, start actions with each skill's select call, give XP through the experience
component. The game's functions keep statistics, events, bonuses and the screens right. Writing numbers
straight into fields skips all of that.

But remember what the game's screens do for the player and its functions do not: the **select calls do not check
requirements** (check `Requirements.AreMet` yourself) and **selecting what already runs stops it**.

## Respect the player's marks

- A **locked** bank stack (`stack.IsLocked`) means "never sell this". Automatic selling must skip it.
- **Placeholders** (`stack.IsPlaceholder`) are empty stacks the player keeps on purpose. Leave them.
- Ask before you destroy anything: a second click, or a setting the player switched on.

## Release what you create

Some game methods create objects that must be released. `bank.ItemSales.GetItemStats(item)` and the burying
stats are the ones you will meet: call `.Destroy()` on them when you are done (in a `finally`), the way the game
does. Otherwise they keep reacting to every bonus change for the rest of the session.

## Bonuses are not saved

Modifiers you add are not part of the save. Add them again when a character is opened, and remove them when
your feature is switched off. Build each `ModifierValues` once and keep it: the game recognises it by the
object.

## Keep your files in your folder

Settings, lists, logs: in `ctx.Directory`. Then "delete the mod's folder" really removes the mod. Never write
into the game's save files, and never into the game's own folders.

When your file holds data for several characters, read it again before writing and replace only the current
character's entry. Two copies of the game can run at the same time.

## Be kind to the frame

`Frame` runs 60 times a second, for every mod. Searching the scene tree (`CurrentGame`, `FindNode`), walking
all 1,811 items or rebuilding a window are fine a few times a second, not every frame. Keep a counter
(`if (++_frame % 30 != 0) return;`).

## Fail quietly, and log

A mod that throws in `Frame` is switched off by the loader. Catch errors where something can go wrong (files,
private members, the game's layout) and write a clear line with `ctx.Log`. Players send the log when they ask
for help; make it tell the story.

## Private members break first

`[UnsafeAccessor]` reaches private members and is the right tool when you need one. They are also the first
thing to change in an update. Keep their use small and in one place, and catch `MissingMethodException` /
`MissingFieldException` with a log line that says what broke.

## Game updates

Melvor Idle 2 is in Early Access. Any update can rename or move what your mod uses. Say in your description
which game version you tested with, and expect to rebuild your mod after updates. When the game's DLL changes,
your mod might load fine and fail later, the first time it reaches the changed part; that is why the log
messages above matter.

## Cheats should say so

If your mod makes the game easier than it was designed to be, say "cheat" on the page. Players who want it
will find it, and players who do not will thank you.
