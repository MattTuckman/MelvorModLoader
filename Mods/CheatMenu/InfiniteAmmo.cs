using System;
using System.Linq;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Components.Equipment;
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.Entities;
using GamesByMalcs.Melvor2.Entities.Combat;

namespace CheatMenu;

/// <summary>
/// "Infinite ammo": arrows, bolts, javelins, boomerangs and throwing hammers are never used up.
///
/// The game spends ammo in one place only, after each attack of the player:
/// <c>if (!RNG.RollChance(AmmoPreservationChance)) Equipment.ConsumeAmmo(type)</c>. The game's roll answers
/// "kept" without rolling at all when the chance is 100% or more, so the setting's bonus (see
/// <see cref="Features"/>) is far above that: no other bonus of the game can pull it back under 100%.
///
/// On top of that the game's own "ammo was used" event is watched: should one ever be spent while the setting is
/// on (a game update that spends ammo somewhere else, say), it is put straight back. The last one as well: the game
/// takes an empty stack off, so it is put back on in the same slot. The death penalty's share of an ammo stack is
/// put back as well.
/// </summary>
public static class InfiniteAmmo
{
    public const string Setting = "combat.infiniteAmmo";

    static EquipmentComponent? s_equipment;
    static CombatActionComponent? s_fight;

    /// <summary>For tests: how many spent or lost pieces of ammo were put back since the character was opened.</summary>
    public static int PutBack { get; private set; }

    /// <summary>Call when a character is opened.</summary>
    public static void Bind(Game game)
    {
        if (s_equipment != null) s_equipment.AmmoConsumed -= AmmoConsumedHandler;
        if (s_fight != null) s_fight.PlayerDied -= PlayerDiedHandler;
        s_equipment = null;
        s_fight = null;
        PutBack = 0;
        try
        {
            if (game.Entities.TryGetObjectById("MelvorBase:Combat", out GameEntity? entity) && entity is CombatEntity combat)
            {
                s_equipment = combat.Equipment;
                s_equipment.AmmoConsumed += AmmoConsumedHandler;
                s_fight = combat.Action;
                s_fight.PlayerDied += PlayerDiedHandler;
            }
        }
        catch (Exception e) { Engine.Log("Infinite ammo: the equipment could not be watched: " + e.Message); }
    }

    static void AmmoConsumedHandler(object? sender, AmmoConsumedEventArgs e)
    {
        if (Engine.IsActive(Setting) && sender is EquipmentComponent equipment) Restore(equipment, e.Item, 1, e.Slot.Slot);
    }

    /// <summary>
    /// The game's death penalty takes a tenth of one worn stack (at least one) without going through the ammo
    /// code. When that stack is ammo, it is put back too: the death screen still names it, the stack stays.
    /// </summary>
    static void PlayerDiedHandler(object? sender, PlayerDiedEventArgs e)
    {
        if (!Engine.IsActive(Setting) || s_equipment == null || e.LostItem is not { } lost) return;
        if (!lost.Item.Capabilities.TryGet(out EquipmentCapability? capability) || capability?.AmmoType == null) return;
        EquipmentSlot? slot = s_equipment.GetAvailableSlotsForItem(lost.Item).FirstOrDefault()?.Slot.Slot;
        Restore(s_equipment, lost.Item, lost.Quantity, slot);
    }

    /// <summary>Puts ammo back: onto the stack, or, when the game took the empty stack off, back on in its slot.</summary>
    static void Restore(EquipmentComponent equipment, Item item, int quantity, EquipmentSlot? slot)
    {
        try
        {
            if (equipment.IsItemEquipped(item)) equipment.TryAddQuantity(item, quantity);
            else if (slot == null || !equipment.TryEquipItem(item, quantity, slot))
            {
                Engine.Log($"Infinite ammo: {item.Id} could not be put back on.");
                return;
            }
            PutBack += quantity;
        }
        catch (Exception ex) { Engine.Log("Infinite ammo: the ammo could not be put back: " + ex.Message); }
    }
}
