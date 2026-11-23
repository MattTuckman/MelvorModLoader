One Style 0.2.0  -  a mod for Melvor Idle 2
==========================================

Play the whole game with one combat style: melee, ranged or magic. Pick your class once
and the game stops asking you to train the other two.

Tested with game version 0.11.6-alpha.1 and Melvor Mod Loader 0.2.0.


REQUIRES
--------
Melvor Mod Loader 0.2.0 or newer. Install the loader first.


INSTALL
-------
1. Open the game folder
   (Steam > right-click Melvor Idle 2 > Manage > Browse local files).
2. Extract this zip there. The mod ends up in the "mods" folder:
      mods\OneStyle\OneStyle.dll
3. Start the game.


HOW TO USE
----------
Open a character, press the Mods button in the top bar and pick "One Style" (a key can be
given to it there). Click Melee, Ranged or Magic. Off gives you the normal game back.
The class is kept per character and can be changed at any time, for free.


WHAT YOUR CLASS DOES
--------------------
- Monsters immune to your style can be hit with it: Wind Drifter, the Amalgamations of
  the Sunken Tower, and the Blood Sanctum dungeon.
- The combat triangle is off for your class: no penalty and no bonus against any monster.
- Quest goals and monsters that ask for another style take yours. For example, the
  archery targets of the first main quest can be shot with melee or magic.
- Requirements on another style's level read your class level instead: Battle Loadout,
  thieving distractions, the four combat constellations in Astrology, item upgrades,
  Hunting by hand, and rings and amulets. For Ranged also the crossbows and throwing
  hammers that ask for Strength.
- Melee and Ranged: your Defence counts in full when you dodge spells. The game normally
  uses Magic for 70% of it.
- Class tiers at class level 20, 40, 60, 80 and 99, each with a title and bonuses:
    Melee:  +3% max hit, +4% minimum hit, -5% monster respawn time (per tier)
    Ranged: +8% chance to keep ammo, +4% accuracy, -2% attack time (per tier)
    Magic:  +8% chance to keep runes, +4% accuracy, +3% max hit (per tier)
  The class level is Ranged or Magic. For melee it is the higher of Attack and Strength.


EQUAL FOOTING (new in 0.2)
--------------------------
Whoever you fight, no class is worse off than the others:
- Weak spot: every monster has a weakest style (Wind Elemental dodges magic 7 times
  better than ranged, Talm dodges ranged 10 times better than melee). With a class,
  your style's stances meet the monster's lowest evasion of all nine stances. Worked
  out again for every monster that appears; area and darkness effects still count.
- Combat triangle off: no style is good or bad against another (see above). In 0.1
  the good matchups stayed, which favoured Magic against 78 monsters, Melee against 59
  and Ranged against 55.
- Elemental edge (Melee and Ranged): +15% max hit against the 135 monsters with an
  element (water, air, earth, fire), as a mage gets with the right spell.
- Melee shares its XP: whatever Attack gains, Strength gains too, and the other way
  round. Melee has two skills to train, Ranged and Magic have one: now all have one.
  XP from quests, lamps and other mods (a "set level" too) is shared as well.
- Guild and Astrology equivalents (Ranged and Magic): melee gets combat rewards from
  two guilds and two constellations. Your own guild ranks and constellation now give
  the same:
    Ranged/Magic guild rank 3: +6% ranged strength / +6% magic damage   (Strength rank 3)
    Ranged/Magic guild rank 4: -10% monster evasion, -10% monster armour (Attack and Strength rank 4)
    Ranged/Magic guild rank 5: +5% minimum hit, +15% power             (Attack and Strength rank 5)
    Ranged, Syllia major 3:    +4 Ranged levels                         (Terra major 4)
    Ranged, Syllia major 4 / Magic, Arachi major 4: +15% accuracy       (Iridan major 3)
  The window lists what you have earned.
- Class identity:
    Melee:  3% lifesteal with melee
    Ranged: +5% critical hit chance and +25% critical damage with ranged, +30 light
    Magic:  +3 ward and 25% chance of double ward damage with magic, +30 light
  (Ranged and Magic mostly use two-handed weapons, which leave no hand for a lantern.)
- Pets: Fen (+1% accuracy) and Bruno (+1% max hit) drop from Attack and Strength XP.
  For Ranged and Magic they drop from your class's XP.


SIX RING SLOTS
--------------
Every character gets six ring slots instead of one, whatever the class (also with Off):
- Ring Slot 2 is open. The game has it, locked behind Crafting level 150.
- Four more ring slots sit on the equipment grid: a new bottom row and the gap left of Legs.
Wear six different rings; the game counts each ring once. The rings stay on in every
loadout and after the game is closed: they are kept in your save like any worn item.


GOOD TO KNOW
------------
- Weapons and armour of other styles keep their own requirements: a class stays a class.
- The class is not written into your save. It lives in "onestyle.json" next to the mod.
  Remove the mod and the character is back to normal.
- Mastery Guild tasks that ask for another style are not changed yet. That is planned
  for a later version.
- Changing the class (or Off) takes every bonus of the old class away at once.


UNINSTALL
---------
1. Open the One Style window and press "Put rings 2-6 in the bank" (on every character
   that wears rings there, and in every loadout). Without the mod the game drops what
   those slots hold.
2. Delete the folder "mods\OneStyle".


HELP AND UPDATES
----------------
Discord: https://discord.gg/q6aexZZSAf
Wiki:    https://falorfrozen-cmd.github.io/MelvorModLoader/
GitHub:  https://github.com/falorfrozen-cmd/MelvorModLoader
When something does not work, look at "MelvorModLoader.log" in the game folder
and include it when you ask for help.


CHANGES
-------
0.2.0
- Equal footing: weak spot, combat triangle off, elemental edge for melee and ranged,
  melee XP sharing, guild and astrology equivalents, class identity, pets (see above)
- Six ring slots for every character, and a button that puts rings 2-6 in the bank
- The window scrolls when the screen is small

0.1.0
- First version: one class per character (immunity, triangle penalties, quest and
  level locks, magic evasion, class tiers)
