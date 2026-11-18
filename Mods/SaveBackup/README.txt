Save Backup 0.1.1  -  a mod for Melvor Idle 2
=============================================

Keeps dated copies of your save files, so a bad day (or a bad mod) cannot cost you a character.

Tested with game version 0.11.6-alpha.1 and Melvor Mod Loader 0.1.3.


REQUIRES
--------
Melvor Mod Loader 0.1.3 or newer. Install the loader first.


INSTALL
-------
1. Open the game folder
   (Steam > right-click Melvor Idle 2 > Manage > Browse local files).
2. Extract this zip there. The mod ends up in the "mods" folder:
      mods\SaveBackup\SaveBackup.dll
3. Start the game.


HOW TO USE
----------
Nothing to press. Every time the game starts, all save files are copied to
   %APPDATA%\Games By Malcs\Melvor Idle 2\mod_save_backups\<date_time>\
To restore: close the game and copy the .sav file from a backup folder over the file
with the same name in the "saves" folder.


GOOD TO KNOW
------------
- The newest 20 backups are kept; older ones are removed.
- No new backup is made when nothing has changed since the last one.


UNINSTALL
---------
Delete the folder "mods\SaveBackup".


HELP AND UPDATES
----------------
Discord: https://discord.gg/q6aexZZSAf
GitHub:  https://github.com/falorfrozen-cmd
When something does not work, look at "MelvorModLoader.log" in the game folder
(it is rewritten on every start) and include it when you ask for help.
