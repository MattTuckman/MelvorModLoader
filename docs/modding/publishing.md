---
title: Publishing
parent: For mod authors
nav_order: 9
---

# Publishing your mod
{: .no_toc }

From a DLL on your computer to a mod others can install in one step.
{: .fs-5 .fw-300 }

1. TOC
{:toc}

---

## The zip

Players extract mods into the **game folder**, the same way as the loader. So the zip contains the path from
the game folder down:

```
YourMod-1.0.0.zip
  mods/
    YourMod/
      YourMod.dll
      README.txt
```

- One folder per mod, named like the mod.
- Your own libraries go next to your DLL, in the same folder. The loader finds them.
- A short `README.txt`: what it does, the key, requirements (loader and game version), how to uninstall.

{: .danger }
> Never include the game's files, Godot's files or the loader's files. Your project references them with
> `Private=false` so they are not copied; check the zip before you upload it.

{: .warning }
> No `.exe`, no `.bat`, no installer. A plain zip is what players trust, what virus scanners accept and what
> mod managers understand. "Extract into the game folder" is all the installing a mod needs.

## Versions

- Set `<Version>` in your project file. The loader writes it to the log (`Mod loaded: YourMod 1.0.0`), which
  helps when players report problems.
- Use the same number on the zip, the readme and the Nexus file.
- Say which **game version** and **loader version** you tested with.

## On Nexus Mods

Melvor Idle 2 has a [Nexus Mods section](https://www.nexusmods.com/games/melvoridle2/mods).

1. **Upload** the zip as the main file.
2. **Requirements:** add **Melvor Mod Loader** as a file requirement (minimum version 0.1.3). Players then see
   "1 additional file required" on your download.
3. **Description:** what it does, the key, a picture, install and uninstall steps, the versions you tested.
   If it is a cheat, say so.
4. **Source:** if your mod is open source, link the repository. Players and other modders appreciate it.

## Uninstalling must be easy

"Delete `mods\YourMod`" should remove everything. Keep all your files in your mod's folder, and never write
into save files. Bonuses you add through the modifier system go away with the mod by themselves.

## Get listed

Tell us on [Discord](https://discord.gg/q6aexZZSAf) when your mod is out, and it goes on the
[mods page](../players/mods) of this wiki.
