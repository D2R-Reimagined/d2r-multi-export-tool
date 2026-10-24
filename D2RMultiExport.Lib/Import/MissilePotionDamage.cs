// SPDX-License-Identifier: GPL-3.0-or-later
using D2RMultiExport.Lib.Models;
using D2RMultiExport.Lib.Translation;
using Missiles = D2RReimaginedTools.Models.Missiles;

namespace D2RMultiExport.Lib.Import;

/// <summary>
/// Damage of the base throwing potions (<c>ExportConfig.MissilePotionDamage.ItemTypes</c>).
/// Their weapons.txt damage columns are placeholders; the game deals the damage of the
/// <c>missiles.txt</c> row their <c>missiletype</c> names by engine record index, so the
/// missile names (swapped relative to the items in vanilla) and <c>*ID</c> are not used.
///
/// Poison comes from the missile the projectile spawns on impact (its <c>HitSubMissile1</c>,
/// the gas cloud), which combat logs confirm is what applies the poison; the projectile's own
/// poison values are never shown or added. Physical and fire damage are the projectile's.
/// </summary>
public static class MissilePotionDamage
{
    /// <summary>The elemental type whose rate is a per-frame poison bitrate with a length.</summary>
    private const string PoisonElement = "pois";

    public static bool IsMissilePotion(EquipmentEntry baseEq, GameData data) =>
        baseEq.EquipmentType == EquipmentType.Weapon
        && data.ExportConfig.MissilePotionDamage.ItemTypesSet.Contains(baseEq.Type ?? "");

    /// <summary>
    /// Replaces the (empty) weapon damage types of a base throwing potion with its missile
    /// row's damage and drops the Indestructible line its <c>nodurability</c> produces.
    /// Any other equipment is left untouched.
    /// </summary>
    public static void Apply(ExportEquipment export, EquipmentEntry baseEq, GameData data)
    {
        if (!IsMissilePotion(baseEq, data)) return;

        export.Lines.RemoveAll(static line => line.Key == SyntheticStringRegistry.Keys.Indestructible);
        if (baseEq.MissileType is not { } index || !data.MissilesByIndex.TryGetValue(index, out var row)) return;
        export.DamageTypes = BuildDamageTypes(row, data);
    }

    private static List<DamageTypeExport> BuildDamageTypes(Missiles row, GameData data)
    {
        var config = data.ExportConfig.MissilePotionDamage;
        // Thrown items carry no skill level; these rows have no per-level damage columns.
        var scope = new MissileCalcContext { Row = row, Data = data, Levels = new CalcLevels(1, 1) };
        var damageTypes = new List<DamageTypeExport>();

        var physicalMin = Read(scope, "damn");
        var physicalMax = Read(scope, "damx");
        if (physicalMax > 0)
        {
            var physical = Create(2, physicalMin, physicalMax);
            EquipmentLineBuilder.AppendWeaponDamage(physical.Lines, physical.Type, physicalMin, physicalMin, physicalMax, physicalMax);
            damageTypes.Add(physical);
        }

        if (string.IsNullOrWhiteSpace(row.ElementalType)
            || !config.ElementalDamageKeys.TryGetValue(row.ElementalType.Trim(), out var elementalKey))
        {
            return damageTypes;
        }

        var elementalMin = Read(scope, "edmn");
        var elementalMax = Read(scope, "edmx");
        if (elementalMax <= 0) return damageTypes;

        if (!string.Equals(row.ElementalType.Trim(), PoisonElement, StringComparison.OrdinalIgnoreCase))
        {
            var elemental = Create(4, elementalMin, elementalMax);
            elemental.Lines.Add(KeyedLine.Of(elementalKey, elementalMin, elementalMax));
            damageTypes.Add(elemental);
            return damageTypes;
        }

        if (!string.IsNullOrWhiteSpace(row.HitSubMissile1))
        {
            // A named cloud that is missing or carries no poison exports no poison line
            // rather than falling back to the projectile's values, which are not applied.
            if (!data.Missiles.TryGetValue(row.HitSubMissile1.Trim(), out var cloud)
                || !string.Equals(cloud.ElementalType?.Trim(), PoisonElement, StringComparison.OrdinalIgnoreCase))
            {
                return damageTypes;
            }
            scope = new MissileCalcContext { Row = cloud, Data = data, Levels = new CalcLevels(1, 1) };
            elementalMin = Read(scope, "edmn");
            elementalMax = Read(scope, "edmx");
            if (elementalMax <= 0) return damageTypes;
        }

        var frames = Read(scope, "edln");
        if (frames <= 0) return damageTypes;
        var poisonMin = AppliedPoisonDamage(elementalMin, frames);
        var poisonMax = AppliedPoisonDamage(elementalMax, frames);
        var seconds = PropertyKeyResolver.PoisonDurationSeconds(frames);
        var poison = Create(4, poisonMin, poisonMax);
        poison.Lines.Add(KeyedLine.Of(elementalKey, poisonMin, poisonMax));
        poison.Lines.Add(KeyedLine.Of(seconds == 1 ? config.DurationSingularKey : config.DurationKey, seconds));
        damageTypes.Add(poison);
        return damageTypes;
    }

    /// <summary>
    /// Whole hit points a per-frame poison rate (256ths of a hit point) removes over
    /// <paramref name="frames"/> frames. Hit points are 8-bit fixed point, so the accumulated
    /// <c>rate × frames</c> 256ths are truncated, unlike the rounded-up item-stat tooltip
    /// (<see cref="PropertyKeyResolver.PoisonTotalDamage"/>).
    /// </summary>
    internal static int AppliedPoisonDamage(int rate, int frames) => (int)((long)rate * frames / 256);

    private static int Read(MissileCalcContext scope, string symbol) =>
        scope.TryResolveIntrinsic(symbol, out var value) ? (int)value : 0;

    private static DamageTypeExport Create(int type, int min, int max) => new()
    {
        Type = type,
        DamageString = min == max ? max.ToString() : $"{min} to {max}",
        AverageDamage = (min + max) / 2.0
    };
}
