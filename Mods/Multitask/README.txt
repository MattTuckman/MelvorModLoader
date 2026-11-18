Multitask 0.1.0  -  a mod for Melvor Idle 2
===========================================

Do more than one thing at a time: fish while you cook the catch, mine while you fight,
cut wood while you smith. Two slots keep extra actions running beside the one you choose
in the game.

This is a cheat: the game is balanced around one action at a time, and this mod lets up
to three run together. Use it if that is the game you want to play.

Tested with game version 0.11.6-alpha.1 and Melvor Mod Loader 0.1.3.


REQUIRES
--------
Melvor Mod Loader 0.1.3 or newer. Install the loader first.


INSTALL
-------
1. Open the game folder
   (Steam > right-click Melvor Idle 2 > Manage > Browse local files).
2. Extract this zip there. The mod ends up in the "mods" folder:
      mods\Multitask\Multitask.dll
3. Start the game.


HOW TO USE
----------
Open a character and press F5.
  1. In a slot, pick a skill and what to do, then switch the slot ON.
     Or do something in the game and press "Use what I am doing now": that action
     moves into the slot.
  2. Start your main action in the game as usual. The slot keeps going next to it.
  3. Switch the slot OFF to stop its action.
Each slot shows what it is doing: running, waiting (and why), or turned off.


GOOD TO KNOW
------------
- Whenever you start something in the game, the game stops everything for a moment.
  The slots come back by themselves within a second.
- A slot that makes something (cooking, smithing...) waits when its materials run out
  and goes on when there are some again. It works in small piles, about ten at a time,
  so the game does not report "action stopped" for every single fish.
- A slot that fights turns itself off if your character dies. It never walks back in.
- If you use a slot's skill for something else in the game, the slot waits until that
  skill is free again.
- The two slots cannot use the same skill.
- Offline progress: everything that was running when you closed the game goes on. A slot
  that runs out of materials while the game is closed stays stopped until you are back.
- It never bypasses the game's requirements: a slot cannot do what you cannot do yet.
- Hunting needs a trap and Agility needs a path chosen on the skill's own page first.
- Works together with Action Queue 0.3.1 or newer: the queue runs your main action, the
  slots keep running beside it.
- The game's bottom bar shows your main action. The slots are shown in this window.
- The slots are remembered per character in "multitask.json" next to the mod.


UNINSTALL
---------
Switch the slots OFF, then delete the folder "mods\Multitask".
(If you delete it with a slot still on, the extra actions simply stop the next time you
start anything in the game.)


HELP AND UPDATES
----------------
Discord: https://discord.gg/q6aexZZSAf
GitHub:  https://github.com/falorfrozen-cmd
When something does not work, look at "MelvorModLoader.log" in the game folder
(it is rewritten on every start) and include it when you ask for help.
