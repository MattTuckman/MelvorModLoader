Action Queue 0.9.0  -  a mod for Melvor Idle 2
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
Open a character and press F6 (or pick it from the Mods button in the top bar; the key can be changed there).
  1. Pick a skill and what to do (or press "Use what I am doing now"). Click the list and
     type to search; arrows and Enter work too. The list is grouped like the skill's own
     page, in the game's order. Things you have not unlocked yet are dimmed with the level
     they need (you can still queue them for later); quest-only targets appear only while
     their quest step is active, as in the game.
  2. Pick when the step ends: when it stops by itself, when you own a number of an item,
     when you have made a number more of an item (whatever the bank already holds),
     when it has been done a number of times, when a skill reaches a level, after a
     number of minutes, or when only a number of an item are left (for example: fight
     until only 5 of your food are left).
     "Done this many times" counts each finished action, the way mastery does: every
     catch at a fishing spot (whatever fish it gives), every hit on a tree or rock,
     every finished bar or potion, every lap of an agility course, every monster killed.
     For that goal the food you are eating is filled in (or, with none chosen, the food
     your bank has most of) and the number starts at 5.
     For the item goal, the item is filled in with what the chosen action makes
     (Smithing: Gold Bar -> Gold Bar). Type in the search box to count another item.
     "It has used up this many of an item" counts only what the step's own skill used
     (the game's "Ingredients Spent"); the item is filled in with what the recipe uses.
  3. "Add step". Repeat for the next steps, then "Start the queue".
  A step can also sell: choose "Sell from the bank" at the end of the skill list (see SELLING).
While the queue runs, one line at the top of the screen shows the current step.
Steps can be moved up and down at any time, also while the queue runs: a running step
is not interrupted, and the new order decides what comes next.


THE GAME'S OWN BUTTONS
----------------------
On the Smithing, Crafting, Fletching, Runecrafting, Beastcrafting, Cooking and Herblore
pages of the game:
  - "+ Queue" (under the game's Create button) puts the selected recipe at the end of the
    queue, with the stop option you chose on the page: Recipe Creations, Products Owned,
    Products Gained, Ingredients Spent, Ingredients Remaining or Reach Level (no stop
    option: until it stops by itself). It works even while you lack the materials.
  - With "The game's Create button adds to the queue" ticked in the queue window, Create
    reads "Add to queue" and does the same. With nothing left in the queue, the recipe is
    started through the queue at once, so the next one you add lines up behind it.
    A paused queue stays paused (press Start). Off by default.
  - The game's Stop button on a step the queue is running ("Stop Cutting", "Stop",
    "Run"...) takes that step out of the queue, and the queue goes on with the next one.
    A saved queue keeps the step; only the list on screen changes.
  - A stop option that counts money (not an item) cannot be queued yet; the mod says so.


PLAN AN ITEM
------------
Press "Plan an item" at the top of the window.
  1. Choose "Make" (this many more than you have) or "Own in total" (until the bank
     holds this many), type the number and pick the item.
  2. The planner follows the recipes back to what has to be gathered (armour <- bars <- ores),
     takes off what your bank already holds, and lists the steps with the time they take.
  3. "Add these steps to the queue" puts them at the end of the queue. They are ordinary
     steps: reorder, remove or mix them with steps you add by hand.
Several plans can be added one after another. A later plan knows what the earlier ones will
make and use up, so two plans never count on the same ore.
The planner only uses what you can do now: when a faster recipe needs something you have not
unlocked (Headless Bolts from Ironsteel Bars), it takes the one you can do (from Iron Bars).
Yellow lines are items to bring yourself: no skill makes them (monster drops, shop items),
or every recipe for them is still locked. An item you cannot make at all yet is not planned.
"then sell them" (next to the Add button) adds a last step that sells what the plan made;
with "Make", what you had before is kept.
Times are for normal game speed and are a forecast, not a promise.


SIDE BY SIDE (WITH THE MULTITASK MOD)
-------------------------------------
On its own the queue does one step after another. With the Multitask mod installed and
its slots switched on, the queue runs as many steps at once as Multitask has room for:
one extra slot = two steps, two = three, up to all 16 actions of the game.
  - The fish are cooked while more are being caught, and a third step can mine meanwhile.
  - A step that needs what an earlier step makes waits for it (in piles of about ten)
    instead of being skipped.
  - "Until I own N" then counts what was gathered, so a step that uses the item up as
    it comes does not make the gathering step run twice as long.
  - Two steps for the same skill still run one after the other.
  - The button "Side by side" at the bottom of the window switches this off again.
  - A step also waits when something you run yourself is making what it needs: mine the
    uncut gems by hand and the queue's crafting step waits for more instead of ending.


LANES (SEVERAL QUEUES AT ONCE, WITH THE MULTITASK MOD)
------------------------------------------------------
With Multitask slots switched on, a "Lane" list appears next to "Add step" (and next to the
planner's Add button). Lanes are queues of their own that run at the same time:
  - Every lane does its own steps one after another. For example lane 1 cuts logs and
    burns them, lane 2 fishes and cooks, lane 3 fights.
  - The list shows each lane under its own heading. "Lane 1 > 2" at the bottom moves the
    selected step into the next lane; Move up / Move down move it inside its lane.
  - A step that needs what another lane makes waits for it (cooking in lane 1 waits for
    the fishing in lane 2).
  - "Start over at the end" works per lane: a short lane starts again as soon as it is
    through and does not wait for a long one.
  - As many lanes run at once as Multitask has room for; two lanes that use the same skill
    take turns. With every step in lane 1 the queue works as one list, as before.
  - Up to 16 lanes: the list offers as many lanes as Multitask runs actions at once (at
    least five).
  - Lanes are kept in saved queues and share codes.


SELLING
-------
Choose "Sell from the bank" at the end of the skill list, pick the item and how many to keep
(0 sells all of them). For example: smith 1,000 Iron Bars, then sell all but 100.
  - The sale waits until the steps before it are finished (in lanes: the steps before it
    in its own lane), and nothing after it starts before it is done.
  - It sells through the game's own sale, at the game's price. A stack you have locked in
    the bank is never sold. With nothing to sell, the step simply ends.
  - It also sells while the game computes the time you were away.


WHEN THE QUEUE WAITS OR ENDS
----------------------------
At the bottom of the window: "When the queue waits or ends, do" [skill] [action].
Pick something there (or press "Use what I am doing now") and your character never stands
still:
  - While every step that is left is waiting (for materials, for its skill to be free),
    this action runs in the free slot. When a step can go on, it makes room again.
  - When the queue has finished, this action starts and simply keeps going.
  - It only ever takes a free slot. Your own actions are never stopped for it. Without the
    Multitask mod it only runs after the queue has finished.
  - It is not started when it would use up what a waiting step needs.
  - Press Stop on it in the game and the queue leaves it alone until you start the queue
    again. If it is a fight and your character dies, it does not walk back in.
  - "Also when the queue is not running" (a check box under it, off by default): the
    action then also runs while the queue is paused, finished or not used at all,
    whenever your character does nothing. Press Stop on it (or on anything else) and it
    waits until you start something yourself; when that ends, it comes back. It waits a
    few seconds after a character is opened.
  - "Nothing" switches it off. The choices are kept per character.


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
- The queue also runs while the game is closed: when you come back, the game works out the
  time you were away and the queue moves on through its steps in that time, as if you had
  watched. After the game is closed and opened again, a running queue carries on where it
  was at the game's last save. Fights are neither started nor ended in that time: such a
  step waits until the game is open.
- It never bypasses the game's requirements. A step you are not allowed to do yet, or have
  no materials for, is marked "skipped" and the next one starts.
- If you start something else by hand, the queue pauses instead of fighting you.
  If you press the game's Stop on a step, the queue moves on without that step.
- After a pause, "Start the queue" carries on: finished steps stay finished, wherever
  they have been moved.
- "Remove all" (or removing the step that is running) also stops what that step had started.
  "Pause" leaves your character doing what it was doing.
- If your character dies, the queue pauses and does not walk back into the fight.
- A fight step ends when you can no longer attack: your bow or crossbow has no ammo of its
  kind equipped, or you cast spells and the bank lacks the runes (or no spell is picked).
  The game itself would keep the fight going while the monster hits you. The step reads
  "done: out of ammo" (or "out of runes") and the queue goes on with the next step.
- Sacrificial Anvil: when the recipe you pick has the game's anvil switch (and you have it
  unlocked), a "Sacrificial Anvil" box shows next to it. Ticked, that step switches the
  anvil on when it starts; unticked, it switches it off. So one step can smith with the
  anvil and the next without. Steps show "(Sacrificial Anvil)" in their name.
- Hunting needs a trap and Agility needs a path chosen on the skill's own page first.
- The queue is remembered per character in "queue.json" next to the mod.
- With side by side switched off, the Multitask mod keeps what you were doing before
  running beside the queue instead.


UNINSTALL
---------
Delete the folder "mods\ActionQueue".


HELP AND UPDATES
----------------
Discord: https://discord.gg/q6aexZZSAf
Wiki:    https://falorfrozen-cmd.github.io/MelvorModLoader/  (guides for players and mod authors)
GitHub:  https://github.com/falorfrozen-cmd/MelvorModLoader  (source code; see LICENSE: free to use, not to re-upload)
When something does not work, look at "MelvorModLoader.log" in the game folder
(it is rewritten on every start) and include it when you ask for help.
