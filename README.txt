Melvor Mod Loader 0.2.0  -  for Melvor Idle 2 (tested on game version 0.11.6-alpha.1)
====================================================================================

Loads C# mods into Melvor Idle 2 without changing any of the game's own files.


INSTALL
-------
1. Open the game folder
   (Steam > right-click Melvor Idle 2 > Manage > Browse local files).
2. Extract this zip there, so that "override.cfg" sits next to "Melvor2.exe".
3. Start the game from Steam as usual.
   A green "Melvor Mod Loader" line shows in the top-left corner for 15 seconds.

After extracting, the game folder contains:
   Melvor2.exe                               (game)
   data_GamesByMalcs.Melvor2_windows_x86_64  (game)
   override.cfg                              (mod loader)
   data_MelvorModLoader_windows_x86_64       (mod loader)
   mods                                      (your mods; create it if missing)


INSTALLING MODS
---------------
Each mod is a zip that you extract into the game folder the same way.
It ends up in its own folder under "mods":
   mods\CheatMenu\CheatMenu.dll


THE MODS MENU AND KEYS
----------------------
With a character open, the "Mods" button in the game's top bar (or F1) lists
every mod window. Click a name to open it. Click its key to give it another one
(press the new key; Ctrl, Shift or Alt can come with it; Esc cancels), or press
"Off" to free the key; the window still opens from the menu. When two windows
want the same key, both rows say so in orange. "Reset all keys" puts every key
back. Your choices are kept in mods\hotkeys.json (delete it to start over).
Only the mods you installed are listed. Their usual keys stay as they were
(for example F5 Multitask, F6 Action Queue, F8 Cheat Menu).


UNINSTALL
---------
Delete "override.cfg" and the folder "data_MelvorModLoader_windows_x86_64".
The game then starts exactly as before. The "mods" folder and the log file
"MelvorModLoader.log" can stay or go.


TROUBLESHOOTING
---------------
The loader writes a log on every start, next to Melvor2.exe:
   MelvorModLoader.log
If the game does not start after a game update, delete "override.cfg" to run
without mods and look for a newer loader version.
One broken mod does not stop the others: it is reported in the log and skipped.


HOW IT WORKS
------------
"override.cfg" is a standard Godot feature. It tells the engine to load
MelvorModLoader.dll first. The loader then loads the game's own DLL untouched,
starts the game normally, and afterwards loads every DLL in "mods".
No game file is modified and nothing is installed outside the game folder.
There is no .exe and no script in this package.

The loader folder includes the Microsoft .NET 10 runtime (MIT license),
which the engine needs in order to start the loader.


FOR MOD AUTHORS
---------------
Mods are C# class libraries (net10.0) that implement MelvorModLoader.IMod.
Get the "Modder's Kit" (a guide and a complete example mod you can build in
five minutes) from the same place you downloaded this loader.


HELP AND UPDATES
----------------
Discord: https://discord.gg/q6aexZZSAf
Wiki:    https://falorfrozen-cmd.github.io/MelvorModLoader/  (guides for players and mod authors)
GitHub:  https://github.com/falorfrozen-cmd/MelvorModLoader  (source code; see LICENSE: free to use, not to re-upload)
When something does not work, look at "MelvorModLoader.log" in the game folder
(it is rewritten on every start) and include it when you ask for help.
