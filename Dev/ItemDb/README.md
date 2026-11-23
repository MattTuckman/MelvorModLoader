# Item database (docs/db)

The wiki's item database is built from the game's own data file:

```
python Dev/ItemDb/build.py            # reads Data/Packages/MelvorBase.json straight out of Melvor2.exe
python Dev/ItemDb/build.py --json X   # or from an extracted copy
```

It writes `docs/db/data.json`. The page itself (`docs/db/index.html`, `app.js`, `glyphs.js`, `style.css`) is plain JS with no build step and no framework. The script also exports small WebP copies of the item and monster pictures to `docs/db/img/` (`--no-images` skips them). The user chose to show the game's pictures in this database; the page credits Games by Malcs and Jagex and offers to take them down on request. Our own SVG glyphs stand in where a picture is missing.

After a game update:
1. Run the script again.
2. Set `GAME_VERSION` in `build.py`.
3. Bump the `?v=` number in `docs/db/index.html`.
4. Read the "items with no known source" line the script prints: it should stay short (unused runes, Sacrificial Anvil leftovers and so on).

## How chances are computed

All chances are base values, with no player bonuses. Each formula was read from the game's code (decompiled `GamesByMalcs.Melvor2.dll`, 0.11.6):

- **Weighted table:** each entry has a chance of weight / total weight (`Random/WeightedTable.cs`).
- **Quantity:** uniform between min and max, both ends included.
- **Monsters:**
  - Fixed items always drop.
  - Each item table is rolled on its own with its `Chance`, then one entry is picked by weight (`CombatLootComponent.cs`).
  - The page shows the chance of at least one, and the expected amount.
  - **Dungeon monsters drop nothing** (`Dungeon.EnemiesDropLoot = false`). Only the dungeon's `Rewards` count, rolled once per clear.
- **Thieving:**
  - Per success: common is `BaseCommonLootChance × weight share`, rare is `BaseRareLootChance × weight share`.
  - Heists add a guaranteed completion pick every `Stages` successes.
  - Success chance = `(100 + level) / (100 + Perception)`.
- **Woodcutting:**
  - Per-hit products: rolled every hit.
  - Per-cut products: rolled when the tree falls.
  - Hits per tree = `ceil(BaseMaxHealth)`.
- **Mining:**
  - Each hit breaks the rock with chance `p` (`BaseProductChance`). Otherwise `p` is added to the progress, and the rock always breaks at 0.9999.
  - Average hits = `(1 − (1−p)^K) / p`.
  - Per-hit items (geodes) are rolled every hit.
- **Fishing:**
  - A fish with `FishChance × weight share`, otherwise junk.
  - Specials are sorted rarest first, and the first one that hits stops the rest: `c_i × Π(1 − c_j)` over the rarer ones.
- **Hunting:**
  - Products are rolled independently per catch. The quantity grows with the journal level, which is noted on the page.
  - Rare species: `BaseRareSpeciesChance × weight share × product chance`.
  - Survey finds are rolled per survey.
- **Openables:**
  - Exactly one weighted pick per open, plus the `AlwaysDrops`.
- **Artisan products:**
  - Each product is rolled independently.
  - Sacrificial Anvil collectables: `CollectableWeight / total × weight share` per step.
- **Cooking:** a failure gives nothing; success = `(69 + level) / (69 + Difficulty)`.
- **Agility:**
  - Path rewards are picked up on the next lap.
  - Only the fastest timed tier you beat is rolled.
- **Shop, quests, upgrades:** fixed amounts. Quest `Currencies` is ignored by the game, so it is ignored here too.
