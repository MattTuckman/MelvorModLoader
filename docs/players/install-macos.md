---
title: Installing on macOS
parent: For players
nav_order: 2
---

# Installing on macOS
{: .no_toc }

One script installs the loader. Mods then work as on Windows: unzip them into the game folder.
{: .fs-5 .fw-300 }

1. TOC
{:toc}

---

Works with the Steam version of Melvor Idle 2 on Apple Silicon and Intel Macs,
tested on game version 0.11.6, macOS 26.

## 1. Find the game folder

In Steam: right-click **Melvor Idle 2** in your library, then **Manage → Browse local files**. A Finder
window opens on a folder that contains `Melvor2.app`. That is the game folder. The usual place is:

```
~/Library/Application Support/Steam/steamapps/common/Melvor Idle 2
```

## 2. Install the loader

There are two ways; both end in the same place.

### From the macOS zip

1. Extract `MelvorModLoader-macos.zip` anywhere (your Downloads folder is fine).
2. Close the game.
3. Open **Terminal**, type `bash ` (with the space), drag `install.command` from the extracted folder into
   the Terminal window, and press Return.

{: .note }
> Double-clicking `install.command` also works, but macOS blocks scripts from a downloaded zip the first
> time: right-click it and choose **Open** instead.

### From source

You need the [.NET SDK 10](https://dotnet.microsoft.com/download) (with
[Homebrew](https://brew.sh): `brew install dotnet`).

```
git clone https://github.com/falorfrozen-cmd/MelvorModLoader
cd MelvorModLoader
./Dev/install-macos.sh
```

To build and install mods from source too, name them (each is a folder under `Mods`):

```
./Dev/install-macos.sh CheatMenu BulkSell ActionQueue
```

`./Dev/install-macos.sh --package` builds `dist/MelvorModLoader-macos.zip`, the zip described above,
instead of installing.

### What the install does

```
Melvor Idle 2/
  Melvor2.app/
    Contents/
      MacOS/
        Melvor2                             the game
        override.cfg                        NEW: tells the game to start the loader
      Resources/
        data_GamesByMalcs.Melvor2_macos_*   the game (arm64 and x86_64)
        data_MelvorModLoader_macos_*        NEW: the loader (arm64 and x86_64)
  mods/                                     your mods and their settings
  MelvorModLoader.log                       the loader's log
```

Only the two **NEW** entries go inside the app. Your mods, their settings and the log sit next to
`Melvor2.app`, exactly where they are on Windows.

## 3. Check that it works

Start the game **from Steam** as usual. For about 15 seconds a small green line appears in the top-left
corner, just like on Windows:

`Melvor Mod Loader 0.3.0 - 0 mod`

No green line? Look at `MelvorModLoader.log` in the game folder; if something went wrong, that file is the
one to attach when asking for help.

## 4. Install mods

Extract a mod's zip **into the game folder** (the one that holds `Melvor2.app`), so that it creates or fills
`mods`:

```
Melvor Idle 2/
  Melvor2.app
  mods/
    CheatMenu/CheatMenu.dll
    ActionQueue/ActionQueue.dll
```

The mod zips are the same ones Windows players use. Restart the game; the green line counts your mods.

{: .note }
> **F keys.** Most mods open with an F key. On a Mac keyboard the top row controls brightness and volume by
> default, so hold **fn** with it (fn+F8), or turn on *System Settings > Keyboard > Keyboard Shortcuts >
> Function Keys > Use F1, F2, etc. keys as standard function keys*. You can also give every window another
> key in the loader's **Mods** menu (fn+F1, or the **Mods** button in the top bar).

## Updating

- **After a game update** (or Steam's "verify integrity of game files"): Steam may replace the app, which
  removes the loader. Run the install again. Your mods and their settings are outside the app and stay.
- **The loader:** run the install from the new zip (or `git pull` and the script).
- **A mod:** extract the new zip over the old one.

## Removing

- **One mod:** delete its folder in `mods`.
- **The loader:** `bash install.command --uninstall`, or delete by hand, inside `Melvor2.app`:
  `Contents/MacOS/override.cfg` and the `Contents/Resources/data_MelvorModLoader_macos_*` folders.
  Steam's "verify integrity of game files" does the same.

[Your saves are safe either way.](install#is-it-safe)

## Things to know on macOS

- **Start the game from Steam.** Running the binary inside the bundle directly trips the game's Steam
  start-up check and it quits with its error screen.
- **`codesign --verify` reports the game as modified.** That is expected: `override.cfg` has to sit next to
  the executable, and a signed bundle does not allow extra files there. Steam still launches the game fine.
  Do not re-sign the bundle.
- **"App Management" permission.** macOS protects apps from being changed by other apps. The install
  script handles this on its own by making a fresh copy of the bundle. If it still cannot write, give your
  terminal *System Settings > Privacy & Security > App Management* and run it again.
- **"Melvor2 is damaged and can't be opened".** macOS checks an app it has never started before, and
  rejects it when files were added. This can happen when the loader is installed into a game that was never
  started, or after the script had to make a fresh copy of the bundle. Fix: run
  `bash install.command --uninstall`, start the game once from Steam, close it, give your terminal the
  App Management permission above, and install again.
- **Two architectures.** The macOS game ships one data folder per architecture, and its DLLs only load on
  their own architecture. The loader is installed for both, so it works whether the game runs native
  (Apple Silicon) or through Rosetta (Intel).
