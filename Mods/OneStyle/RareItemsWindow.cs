using System.Collections.Generic;
using GamesByMalcs.Melvor2.Entities.Combat;
using Godot;

namespace OneStyle;

/// <summary>What a monster's Normal, Champion and Unique versions drop of rare items, for the Elites window.</summary>
public static partial class RareItems
{
    public static void HookWindow() => Elites.DropLines = DropLines;

    static IEnumerable<(string Text, Color Color, Texture2D? Icon)> DropLines(Enemy enemy, EliteTier tier)
    {
        int iLvl = enemy.CombatLevel;
        int best = BestTier(iLvl);
        switch (tier)
        {
            case EliteTier.Normal:
                yield return ($"Rare items: 0.1% a kill (a Rare item has 3 affixes 80% of the time, 4 affixes 20%). Item level {iLvl}: tiers up to T{best}.", Colors[Rarity.Rare], null);
                break;
            case EliteTier.Champion:
                yield return ($"1 item: Magic (2 affixes) 60%, Rare (3 affixes) 33%, Rare (4 affixes) 7%. Item level {iLvl} (+5 on your slayer task or as a dungeon's last boss): tiers up to T{best}.",
                    Colors[Rarity.Magic], null);
                break;
            case EliteTier.Unique:
                yield return ("2 items: a Rare (3 affixes, or 4 affixes 20% of the time), then one like a Champion's.", Colors[Rarity.Rare], null);
                if (Uniques.ForMonster(enemy.Id) is { } u)
                {
                    double chance = Uniques.ChanceNow(u);
                    yield return ($"Unique item: {u.Name} ({u.Slot}). Chance: {chance * 100:0}% on every Unique kill.",
                        Colors[Rarity.Unique], Uniques.IconOf(u));
                    foreach (UniqueStat s in u.Stats) yield return ("   " + s.Text, new Color(1f, 0.78f, 0.55f), null);
                    if (u.MechanicName != "") yield return ($"   {u.MechanicName}: {u.Mechanic}", new Color(1f, 0.78f, 0.55f), null);
                }
                break;
        }
    }
}
