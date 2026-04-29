// SPDX-License-Identifier: GPL-3.0-or-later
using D2RMultiExport.Lib.Models;
using static D2RMultiExport.Lib.Translation.SyntheticStringRegistry.Keys;

namespace D2RMultiExport.Lib.Translation;

/// <summary>
/// Phase 1B: builds <see cref="KeyedLine"/> rows for the equipment block
/// (defense, weapon damage, durability, requirements, sockets, block chance,
/// elemental damage). All values handed in here MUST already be post-math.
///
/// Synthetic keys live in <see cref="SyntheticStringRegistry"/>. Single-value
/// shapes use the simple template; level-scaled "(low-high) to (low-high)"
/// shapes use the *Range variants.
/// </summary>
public static class EquipmentLineBuilder
{
    /// <summary>Append a defense line covering scalar / range / range-range cases.</summary>
    public static void AppendDefense(List<KeyedLine> target, int minLow, int minHigh, int maxLow, int maxHigh)
    {
        // Drop noise: collapse equal endpoints
        if (minLow == minHigh && maxLow == maxHigh)
        {
            if (minLow == maxLow)
            {
                target.Add(KeyedLine.Of(Defense, minLow));
            }
            else
            {
                target.Add(KeyedLine.Of(DefenseRange, minLow, maxLow));
            }
        }
        else
        {
            target.Add(KeyedLine.Of(DefenseRangeRange, minLow, minHigh, maxLow, maxHigh));
        }
    }

    /// <summary>Append a weapon-damage line for a given damage type slot (0=1H, 1=2H, 2=throw, 3=generic, 4=elemental).</summary>
    public static void AppendWeaponDamage(List<KeyedLine> target, int type, int minLow, int minHigh, int maxLow, int maxHigh)
    {
        if (minLow == minHigh && maxLow == maxHigh)
        {
            string key = type switch
            {
                0 => WeaponDamageOneHand,
                1 => WeaponDamageTwoHand,
                2 => WeaponDamageThrow,
                4 => ElementalDamage,
                _ => WeaponDamageGeneric
            };
            target.Add(KeyedLine.Of(key, minLow, maxLow));
        }
        else
        {
            string key = type switch
            {
                0 => WeaponDamageOneHandRange,
                1 => WeaponDamageTwoHandRange,
                2 => WeaponDamageThrowRange,
                _ => WeaponDamageGenericRange
            };
            target.Add(KeyedLine.Of(key, minLow, minHigh, maxLow, maxHigh));
        }
    }

    /// <summary>Smite or kick damage line. <paramref name="kind"/> = "smite" or "kick".</summary>
    public static void AppendSmiteOrKick(List<KeyedLine> target, string kind, int min, int max)
    {
        var key = kind == "kick" ? KickDamage : SmiteDamage;
        target.Add(KeyedLine.Of(key, min, max));
    }

    /// <summary>Block chance line (% of blocking).</summary>
    public static void AppendBlock(List<KeyedLine> target, int chance)
    {
        target.Add(KeyedLine.Of(ChanceToBlock, chance));
    }

    /// <summary>Durability "X of Y" or "Indestructible" line.</summary>
    public static void AppendDurability(List<KeyedLine> target, int? durability, bool indestructible)
    {
        if (indestructible || durability == 0)
        {
            target.Add(KeyedLine.Of(Indestructible));
            return;
        }
        if (durability.HasValue && durability.Value > 0)
        {
            target.Add(KeyedLine.Of(Durability, durability.Value, durability.Value));
        }
    }

    /// <summary>Required strength / dexterity / level numerics.</summary>
    public static void AppendRequirements(List<KeyedLine> target, int? reqStr, int? reqDex, int? reqLevel, string? reqClass)
    {
        if (reqStr.HasValue && reqStr.Value > 0)
            target.Add(KeyedLine.Of(RequiredStrength, reqStr.Value));
        if (reqDex.HasValue && reqDex.Value > 0)
            target.Add(KeyedLine.Of(RequiredDexterity, reqDex.Value));
        if (reqLevel.HasValue && reqLevel.Value > 1)
            target.Add(KeyedLine.Of(RequiredLevel, reqLevel.Value));
        if (!string.IsNullOrEmpty(reqClass))
            target.Add(KeyedLine.Of(RequiredClass, reqClass));
    }
}
