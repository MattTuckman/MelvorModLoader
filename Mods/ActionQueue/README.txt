Action Queue 0.4.0  -  a mod for Melvor Idle 2
==============================================

Chains actions: do one thing until a goal is reached, then start the next. For example:
cut oak until you own 1,000 logs, burn them all, fish until level 30, then fight for 45 minutes.
Works with 16 skills including combat, dungeons and slayer tasks. Not a cheat.
It also plans for you ("I want 100 Steel Platebodies" -> the steps), keeps several queues
under a name, and turns a queue into a short code you can share.

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
     when a skill reaches a level, after a number of minutes, or when only a number of an
     item are left (for example: fight until only 5 of your food are left).
     For the item goal, the item is filled in with what the chosen action makes
     (Smithing: Gold Bar -> Gold Bar). Type in the search box to count another item.
  3. "Add step". Repeat for the next steps, then "Start the queue".
While the queue runs, one line at the top of the screen shows the current step.


PLAN AN ITEM
------------
Press "Plan an item" at the top of the window.
  1. Type how many you want to own and pick the item.
  2. The planner follows the recipes back to what has to be gathered (armour <- bars <- ores),
     takes off what your bank already holds, and lists the steps with the time they take.
  3. "Add these steps to the queue" puts them at the end of the queue. They are ordinary
     steps: reorder, remove or mix them with steps you add by hand.
Several plans can be added one after another. A later plan knows what the earlier ones will
make and use up, so two plans never count on the same ore.
Red lines are actions you have not unlocked yet. Yellow lines are items no skill makes
(monster drops, shop items): bring those yourself.
Times are for normal game speed and are a forecast, not a promise.


SAVED QUEUES AND SHARE CODES
----------------------------
Press "Saved queues" at the top of the window.
  - Type a name and press Save: the queue below is kept under that name.
  - Select a saved queue and press Load (or double-click it) to put it in place of the
    queue below. The list at the very top of the window does the same in one click.
  - A queue that is running keeps running when you switch: it goes on from the first step
    of the new one.
  - If the queue on screen was never saved, or was changed, it is kept as
    "Unsaved queue (auto)" when you load another one. Nothing is lost by switching.
  - "Copy share code" turns a queue into a short text (AQ1:...). Paste it in a chat.
  - To use somebody's code: paste it into the box and press Import, then Load it.
    A code only holds names and numbers. Steps for things your game does not have are
    skipped, and so are steps you have not unlocked yet.


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
- Works together with the Multitask mod: actions its slots keep running go on beside
  the queue, and the queue does not mistake them for you taking over.


UNINSTALL
---------
Delete the folder "mods\ActionQueue".


HELP AND UPDATES
----------------
Discord: https://discord.gg/q6aexZZSAf
GitHub:  https://github.com/falorfrozen-cmd/MelvorModLoader  (source code, MIT license)
When something does not work, look at "MelvorModLoader.log" in the game folder
(it is rewritten on every start) and include it when you ask for help.
