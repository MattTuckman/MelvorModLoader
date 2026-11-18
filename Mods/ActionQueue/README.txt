Action Queue 0.2.2  -  a mod for Melvor Idle 2
==============================================

Chains actions: do one thing until a goal is reached, then start the next. For example:
cut oak until you own 1,000 logs, burn them all, fish until level 30, then fight for 45 minutes.
Works with 16 skills including combat, dungeons and slayer tasks. Not a cheat.

Tested with game version 0.11.6-alpha.1 and Melvor Mod Loader 0.1.3.


REQUIRES
--------
Melvor Mod Loader 0.1.3 or newer. Install the loader first.


INSTALL
-------
1. Open the game folder
   (Steam > right-click Melvor Idle 2 > Manage > Browse local files).
2. Extract this zip there. The mod ends up in the "mods" folder:
      mods\ActionQueue\ActionQueue.dll
3. Start the game.


HOW TO USE
----------
Open a character and press F6.
  1. Pick a skill and what to do (or press "Use what I am doing now").
  2. Pick when the step ends: when it stops by itself, when you own a number of an item,
     when a skill reaches a level, or after a number of minutes.
     For the item goal, the item is filled in with what the chosen action makes
     (Smithing: Gold Bar -> Gold Bar). Type in the search box to count another item.
  3. "Add step". Repeat for the next steps, then "Start the queue".
While the queue runs, one line at the top of the screen shows the current step.


GOOD TO KNOW
------------
- The queue runs only while the game is open. While the game is closed, the game itself
  continues the last action only (its normal offline progress).
- It never bypasses the game's requirements. A step you are not allowed to do yet, or have
  no materials for, is marked "skipped" and the next one starts.
- If you start something else by hand, the queue pauses instead of fighting you.
- If your character dies, the queue pauses and does not walk back into the fight.
- Hunting needs a trap and Agility needs a path chosen on the skill's own page first.
- The queue is remembered per character in "queue.json" next to the mod.


UNINSTALL
---------
Delete the folder "mods\ActionQueue".


HELP AND UPDATES
----------------
Discord: https://discord.gg/q6aexZZSAf
GitHub:  https://github.com/falorfrozen-cmd
When something does not work, look at "MelvorModLoader.log" in the game folder
(it is rewritten on every start) and include it when you ask for help.
