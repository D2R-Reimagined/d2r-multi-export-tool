// SPDX-License-Identifier: GPL-3.0-or-later
using D2RMultiExport.Lib.Models;
using D2RMultiExport.Lib.Translation;

namespace D2RMultiExport.Lib.Import;

/// <summary>
/// Ports the old doc-generator's AddDamageArmorString logic.
/// Computes enhanced weapon damage strings, armor defense strings with ED,
/// elemental damage types, smite/kick damage, and durability adjustments.
/// </summary>
public static class DamageArmorCalculator
{
    /// <summary>
    /// Applies damage/armor calculations to the equipment export, modifying DamageTypes, ArmorString,
    /// DamageString, DamageStringPrefix, and Durability based on item properties.
    /// Must be called AFTER properties have been mapped and before final export.
    /// </summary>
    public static void Apply(ExportEquipment equipment, EquipmentEntry baseEq, List<CubePropertyExport> properties, int requiredLevel, GameData? data = null)
    {
        if (equipment == null || baseEq == null) return;

        // Handle Ethereal property bonus to base damage/armor before other calculations
        var ethProp = properties.FirstOrDefault(x => x.PropertyCode == "ethereal");
        bool isEthereal = ethProp != null && (ethProp.Min ?? 0) >= 1;

        if (equipment.EquipmentType == 1) // Weapon
        {
            ApplyWeaponDamage(equipment, baseEq, properties, requiredLevel, isEthereal, data);
        }
        else if (equipment.EquipmentType == 0) // Armor
        {
            ApplyArmorDefense(equipment, baseEq, properties, requiredLevel, isEthereal, data);
        }

        // Handle durability adjustments
        var durProp = properties.FirstOrDefault(x => x.PropertyCode == "dur");
        if (durProp != null)
        {
            equipment.Durability = (equipment.Durability ?? 0) + (durProp.Min ?? 0);
        }

        bool indestructible = properties.Any(x => x.PropertyCode == "indestruct");
        if (indestructible)
        {
            equipment.Durability = 0;
        }

        // Phase 1B: append final durability + requirement KeyedLines once all
        // numerics have been settled. Property KeyedLines (already on each
        // CubePropertyExport.Lines) are emitted by the importer separately.
        EquipmentHelper.AppendFinalRequirementLines(equipment, indestructible);
    }

    private static void ApplyWeaponDamage(ExportEquipment equipment, EquipmentEntry baseEq, List<CubePropertyExport> properties, int requiredLevel, bool isEthereal, GameData? data = null)
    {
        int lowLevel = Math.Max(1, requiredLevel);
        int highLevel = 100;

        // Phase 1: Enhanced Damage
        float edLowMin = 0, edHighMin = 0, edLowMax = 0, edHighMax = 0;
        // Phase 2: Normal Flat Damage
        int flatLowMin = 0, flatHighMin = 0, flatLowMax = 0, flatHighMax = 0;
        // Phase 3: Per-Level Flat Damage
        int plLowMin = 0, plHighMin = 0, plLowMax = 0, plHighMax = 0;

        foreach (var prop in properties)
        {
            switch (prop.PropertyCode)
            {
                case "dmg%":
                {
                    float valMin = prop.Min ?? 0;
                    float valMax = prop.Max ?? prop.Min ?? 0;
                    edLowMin += valMin; edHighMin += valMax;
                    edLowMax += valMin; edHighMax += valMax;
                    break;
                }
                case "dmg%/lvl":
                {
                    (float growthMin, float growthMax) = GetGrowthValues(prop);
                    float opDiv = GetOpDiv("item_maxdamage_percent", data);
                    float valLow = growthMin / opDiv * lowLevel;
                    float valHigh = growthMax / opDiv * highLevel;
                    edLowMax += valLow; edHighMax += valHigh;
                    break;
                }
                case "dmg-norm":
                {
                    int valMin = prop.Min ?? 0;
                    int valMax = prop.Max ?? 0;
                    flatLowMin += valMin; flatHighMin += valMin;
                    flatLowMax += valMax; flatHighMax += valMax;
                    break;
                }
                case "dmg-min":
                {
                    int valMin = prop.Min ?? 0;
                    int valMax = prop.Max ?? prop.Min ?? 0;
                    flatLowMin += valMin; flatHighMin += valMax;
                    break;
                }
                case "dmg-max":
                {
                    flatLowMax += prop.Min ?? 0;
                    flatHighMax += prop.Max ?? prop.Min ?? 0;
                    break;
                }
                case "dmg/lvl":
                case "dmg-max/lvl":
                {
                    (float gMin, float gMax) = GetGrowthValues(prop);
                    float div = GetOpDiv("maxdamage", data);
                    int valLow = (int)(gMin / div * lowLevel);
                    int valHigh = (int)(gMax / div * highLevel);
                    plLowMax += valLow; plHighMax += valHigh;
                    break;
                }
                case "dmg-min/lvl":
                {
                    (float gMin, float gMax) = GetGrowthValues(prop);
                    float div = GetOpDiv("mindamage", data);
                    int valLow = (int)(gMin / div * lowLevel);
                    int valHigh = (int)(gMax / div * highLevel);
                    plLowMin += valLow; plHighMin += valHigh;
                    break;
                }
                case "flat-dmg/lvl":
                {
                    (float gMin, float gMax) = GetGrowthValues(prop);
                    float div = GetOpDiv("maxdamage", data);
                    int valLowMin = (int)(gMin / div * lowLevel);
                    int valHighMin = (int)(gMin / div * highLevel);
                    int valLowMax = (int)(gMax / div * lowLevel);
                    int valHighMax = (int)(gMax / div * highLevel);
                    plLowMin += valLowMin; plHighMin += valHighMin;
                    plLowMax += valLowMax; plHighMax += valHighMax;
                    break;
                }
            }
        }

        // Apply to each damage type
        if (equipment.DamageTypes != null)
        {
            foreach (var dt in equipment.DamageTypes)
            {
                int baseMin = ParseDamageComponent(dt.DamageString, true);
                int baseMax = ParseDamageComponent(dt.DamageString, false);

                if (isEthereal)
                {
                    baseMin = (int)(baseMin * 1.5f);
                    baseMax = (int)(baseMax * 1.5f);
                }

                // 1. Apply ED
                int minDam1 = (int)(baseMin * (100f + edLowMin) / 100f);
                int minDam2 = (int)(baseMin * (100f + edHighMin) / 100f);
                int maxDam1 = (int)(baseMax * (100f + edLowMax) / 100f);
                int maxDam2 = (int)(baseMax * (100f + edHighMax) / 100f);

                // 2. Apply Flat + Per-Level
                minDam1 += flatLowMin + plLowMin;
                minDam2 += flatHighMin + plHighMin;
                maxDam1 += flatLowMax + plLowMax;
                maxDam2 += flatHighMax + plHighMax;

                maxDam1 = Math.Max(maxDam1, minDam1 + 1);
                maxDam2 = Math.Max(maxDam2, minDam2 + 1);

                if (minDam1 == minDam2 && maxDam1 == maxDam2)
                {
                    dt.DamageString = $"{minDam1} to {maxDam1}";
                }
                else
                {
                    string minStr = minDam1 == minDam2 ? minDam1.ToString() : $"({minDam1}-{minDam2})";
                    string maxStr = maxDam1 == maxDam2 ? maxDam1.ToString() : $"({maxDam1}-{maxDam2})";
                    dt.DamageString = $"{minStr} to {maxStr}";
                }

                dt.AverageDamage = (minDam1 + minDam2 + maxDam1 + maxDam2) / 4.0;

                // Phase 1B: emit a KeyedLine row mirroring the post-math damage values.
                // The KeyedLine lives on the per-DamageType row only — the website renders
                // both DamageTypes[].Lines and Equipment.Lines, so duplicating here would
                // produce a double "Damage: X to Y" entry in the rendered tooltip.
                EquipmentLineBuilder.AppendWeaponDamage(dt.Lines, dt.Type, minDam1, minDam2, maxDam1, maxDam2);
            }
        }

        // Add elemental damage types from properties
        AddElementalDamageTypes(properties, equipment.DamageTypes ??= [], data);
    }

    private static void ApplyArmorDefense(ExportEquipment equipment, EquipmentEntry baseEq, List<CubePropertyExport> properties, int requiredLevel, bool isEthereal, GameData? data = null)
    {
        int minAc = baseEq.MinAC ?? 0;
        int maxAc = baseEq.MaxAC ?? 0;

        if (isEthereal)
        {
            minAc = (int)(minAc * 1.5f);
            maxAc = (int)(maxAc * 1.5f);
        }

        int lowLevel = Math.Max(1, requiredLevel);
        int highLevel = 100;

        float edLowMin = 0, edHighMin = 0, edLowMax = 0, edHighMax = 0;
        int flatLowMin = 0, flatHighMin = 0, flatLowMax = 0, flatHighMax = 0;
        bool hasEd = false;

        foreach (var prop in properties)
        {
            switch (prop.PropertyCode)
            {
                case "ac%":
                {
                    float valMin = prop.Min ?? 0;
                    float valMax = prop.Max ?? prop.Min ?? 0;
                    edLowMin += valMin; edHighMin += valMin;
                    edLowMax += valMax; edHighMax += valMax;
                    hasEd = true;
                    break;
                }
                case "ac%/lvl":
                {
                    (float gMin, float gMax) = GetGrowthValues(prop);
                    float div = GetOpDiv("item_armor_percent", data);
                    float valLowMin = gMin / div * lowLevel;
                    float valHighMin = gMin / div * highLevel;
                    float valLowMax = gMax / div * lowLevel;
                    float valHighMax = gMax / div * highLevel;
                    edLowMin += valLowMin; edHighMin += valHighMin;
                    edLowMax += valLowMax; edHighMax += valHighMax;
                    hasEd = true;
                    break;
                }
                case "ac":
                {
                    int valMin = prop.Min ?? 0;
                    int valMax = prop.Max ?? prop.Min ?? 0;
                    flatLowMin += valMin; flatHighMin += valMin;
                    flatLowMax += valMax; flatHighMax += valMax;
                    break;
                }
                case "ac/lvl":
                {
                    (float gMin, float gMax) = GetGrowthValues(prop);
                    float div = GetOpDiv("armorclass", data);
                    int valLowMin = (int)(gMin / div * lowLevel);
                    int valHighMin = (int)(gMin / div * highLevel);
                    int valLowMax = (int)(gMax / div * lowLevel);
                    int valHighMax = (int)(gMax / div * highLevel);
                    flatLowMin += valLowMin; flatHighMin += valHighMin;
                    flatLowMax += valLowMax; flatHighMax += valHighMax;
                    break;
                }
            }
        }

        int minAc1, minAc2, maxAc1, maxAc2;
        if (hasEd)
        {
            // In D2, if item has ED%, base AC is fixed at MaxAc + 1
            minAc1 = (int)Math.Floor((maxAc + 1) * (100f + edLowMin) / 100f) + flatLowMin;
            minAc2 = (int)Math.Floor((maxAc + 1) * (100f + edHighMin) / 100f) + flatHighMin;
            maxAc1 = (int)Math.Floor((maxAc + 1) * (100f + edLowMax) / 100f) + flatLowMax;
            maxAc2 = (int)Math.Floor((maxAc + 1) * (100f + edHighMax) / 100f) + flatHighMax;
        }
        else
        {
            minAc1 = minAc + flatLowMin;
            minAc2 = minAc + flatHighMin;
            maxAc1 = maxAc + flatLowMax;
            maxAc2 = maxAc + flatHighMax;
        }

        string minStr = minAc1 == minAc2 ? minAc1.ToString() : $"({minAc1}-{minAc2})";
        string maxStr = maxAc1 == maxAc2 ? maxAc1.ToString() : $"({maxAc1}-{maxAc2})";

        equipment.ArmorString = minStr == maxStr ? minStr : $"{minStr}-{maxStr}";

        // Phase 1B: emit a KeyedLine row mirroring the post-math defense values
        EquipmentLineBuilder.AppendDefense(equipment.Lines, minAc1, minAc2, maxAc1, maxAc2);

        // Handle smite/kick damage (Armor.txt mindam/maxdam → MinDamage/MaxDamage)
        int? smiteMin = baseEq.MinDamage;
        int? smiteMax = baseEq.MaxDamage;

        if (isEthereal && smiteMin.HasValue)
        {
            smiteMin = (int)(smiteMin.Value * 1.5f);
            smiteMax = smiteMax.HasValue ? (int)(smiteMax.Value * 1.5f) : smiteMin;
        }

        if (smiteMin.HasValue && smiteMin.Value > 0)
        {
            equipment.DamageStringPrefix = GetDamagePrefix(baseEq, data);
            equipment.DamageString = smiteMin == smiteMax
                ? $"{smiteMin.Value}"
                : $"{smiteMin.Value} to {smiteMax ?? smiteMin.Value}";

            // Phase 1B: emit smite or kick KeyedLine
            string kind = string.Equals(equipment.DamageStringPrefix, "Kick Damage", StringComparison.OrdinalIgnoreCase)
                ? "kick" : "smite";
            EquipmentLineBuilder.AppendSmiteOrKick(equipment.Lines, kind, smiteMin.Value, smiteMax ?? smiteMin.Value);
        }
    }

    private static string? GetDamagePrefix(EquipmentEntry baseEq, GameData? data)
    {
        if (data == null || string.IsNullOrEmpty(baseEq.Type)) return null;
        if (!data.ItemTypes.TryGetValue(baseEq.Type, out var typeEntry)) return null;

        if (string.Equals(typeEntry.Code, "shld", StringComparison.OrdinalIgnoreCase)
            || string.Equals(typeEntry.Equiv1, "shld", StringComparison.OrdinalIgnoreCase)
            || string.Equals(typeEntry.Code, "ashd", StringComparison.OrdinalIgnoreCase))
            return "Smite Damage";

        if (string.Equals(typeEntry.Code, "boot", StringComparison.OrdinalIgnoreCase)
            || string.Equals(typeEntry.Equiv1, "boot", StringComparison.OrdinalIgnoreCase))
            return "Kick Damage";

        return null;
    }

    private static void AddElementalDamageTypes(List<CubePropertyExport> properties, List<DamageTypeExport> damageTypes, GameData? data)
    {
        // Elemental damage codes + min/max pairings live in export-config.json
        // (elementalDamageCodes / elementalMinMaxPairs). Fall back to empty
        // sets when no config has been loaded — matches the previous behavior
        // of "do nothing if no elemental data is wired up".
        var elementalCodes = data?.ExportConfig.ElementalDamageCodesSet
                             ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var elementalPairs = data?.ExportConfig.ElementalMinMaxPairs
                             ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        int elemMinSum = 0, elemMaxSum = 0;
        bool hasElemental = false;

        foreach (var prop in properties)
        {
            if (prop.PropertyCode == null) continue;

            if (elementalCodes.Contains(prop.PropertyCode))
            {
                int minVal = prop.Min ?? 0;
                int maxVal = prop.Max ?? 0;

                // Poison: dmg-pois from MergeElementalTriplets already has converted values.
                // Only convert raw bitrate if pois-min/pois-max were NOT merged (no dmg-pois yet).
                if (string.Equals(prop.PropertyCode, "dmg-pois", StringComparison.OrdinalIgnoreCase))
                {
                    // Values are already total damage (converted by MergeElementalTriplets).
                    // Convert total damage to per-second for elemental DPS display.
                    int lenFrames = 0;
                    if (int.TryParse(prop.Parameter, out var f)) lenFrames = f;
                    if (lenFrames > 0)
                    {
                        double seconds = lenFrames / 25.0;
                        minVal = (int)Math.Round(minVal / seconds);
                        maxVal = (int)Math.Round(maxVal / seconds);
                    }
                }

                elemMinSum += minVal;
                elemMaxSum += maxVal;
                hasElemental = true;
            }
        }

        // Handle separate min/max codes
        foreach (var pair in elementalPairs)
        {
            var minProp = properties.FirstOrDefault(p => string.Equals(p.PropertyCode, pair.Key, StringComparison.OrdinalIgnoreCase));
            var maxProp = properties.FirstOrDefault(p => string.Equals(p.PropertyCode, pair.Value, StringComparison.OrdinalIgnoreCase));

            if (minProp != null || maxProp != null)
            {
                elemMinSum += minProp?.Min ?? 0;
                elemMaxSum += maxProp?.Max ?? maxProp?.Min ?? 0;
                hasElemental = true;
            }
        }

        if (hasElemental)
        {
            var elem = new DamageTypeExport
            {
                Type = 4, // Elemental
                DamageString = $"{elemMinSum} to {elemMaxSum}",
                AverageDamage = (elemMinSum + elemMaxSum) / 2.0
            };
            EquipmentLineBuilder.AppendWeaponDamage(elem.Lines, 4, elemMinSum, elemMinSum, elemMaxSum, elemMaxSum);
            damageTypes.Add(elem);
        }
    }

    private static int ParseDamageComponent(string damageString, bool isMin)
    {
        if (string.IsNullOrEmpty(damageString)) return 0;
        var parts = damageString.Split(" to ");
        if (parts.Length == 2)
        {
            return int.TryParse(isMin ? parts[0].Trim() : parts[1].Trim(), out var v) ? v : 0;
        }
        return int.TryParse(damageString.Trim(), out var single) ? single : 0;
    }

    private static (float min, float max) GetGrowthValues(CubePropertyExport prop)
    {
        float gMin = prop.Min ?? (float.TryParse(prop.Parameter, out var p) ? p : 0);
        float gMax = prop.Max ?? gMin;
        return (gMin, gMax);
    }

    private static float GetOpDiv(string statKey, GameData? data)
    {
        // Look up OpParam from ItemStatCost for the given stat
        if (data != null && data.Stats.TryGetValue(statKey, out var stat) && stat.OpParam.HasValue)
        {
            return (float)Math.Pow(2, stat.OpParam.Value);
        }
        // Fallback: most D2 per-level stats use OpParam=3 (divisor=8)
        return 8f;
    }
}
