// SPDX-License-Identifier: GPL-3.0-or-later
using D2RMultiExport.Lib.Models;

namespace D2RMultiExport.Lib.Import;

/// <summary>
/// Ported from the old RequiredLevelReport.cs.
/// Calculates the final required level by considering both base requirements and implied
/// requirements from skill/oskill properties.
/// </summary>
public static class RequirementHelper
{
    public static int ComputeAdjustedRequiredLevel(
        GameData data,
        string itemType,
        string itemName,
        int baseReqLevel,
        IEnumerable<CubePropertyExport> properties,
        int? equipmentBaseReqLevel = null)
    {
        var currentReq = baseReqLevel;

        // 1. Ensure required level is at least the base equipment's requirement
        if (equipmentBaseReqLevel.HasValue && equipmentBaseReqLevel.Value > currentReq)
        {
            currentReq = equipmentBaseReqLevel.Value;
        }

        if (properties == null) return currentReq;

        int explicitIncrease = 0;
        int maxImplied = 0;

        foreach (var prop in properties)
        {
            // Sum explicit increases: item_levelreq. Source files often reference
            // this through a Properties.txt alias (for example Runes.txt uses
            // `levelreq`, which maps to stat1=`item_levelreq`), so resolve the
            // property code through the properties table instead of only matching
            // the final stat name literally.
            if (IsExplicitRequiredLevelIncrease(data, prop.PropertyCode))
            {
                explicitIncrease += prop.Max ?? prop.Min ?? 0;
                continue;
            }

            // Consider implied requirement only for skill/oskill
            var code = (prop.PropertyCode ?? string.Empty).ToLowerInvariant();
            if (code == "skill" || code == "oskill")
            {
                if (!string.IsNullOrEmpty(prop.Parameter))
                {
                    // Lookup skill by ID (parameter) or Name
                    SkillEntry? skill;
                    if (int.TryParse(prop.Parameter, out var skillId))
                    {
                        data.SkillsById.TryGetValue(skillId, out skill);
                    }
                    else
                    {
                        data.Skills.TryGetValue(prop.Parameter, out skill);
                    }

                    if (skill != null && skill.RequiredLevel > 0)
                    {
                        if (skill.RequiredLevel > maxImplied)
                        {
                            maxImplied = skill.RequiredLevel;
                        }
                    }
                }
            }
        }

        var afterExplicit = currentReq + explicitIncrease;
        return Math.Max(afterExplicit, maxImplied);
    }

    /// <summary>
    /// Adjusts an equipment's strength/dexterity requirements based on any "ease"
    /// (item_req_percent) properties on the item. In this mod, a positive ease value
    /// increases requirements by that percentage ("Requirements Increased By %d%%").
    ///
    /// This MUST be called before the requirement KeyedLines are baked (i.e. before
    /// <see cref="DamageArmorCalculator.Apply"/> invokes
    /// <see cref="EquipmentHelper.AppendFinalRequirementLines"/>); the keyed wire
    /// output is generated from those lines, not from the string field alone.
    /// </summary>
    public static void ApplyEaseAdjustment(ExportEquipment equipment, IEnumerable<CubePropertyExport> properties)
    {
        if (equipment == null || properties == null) return;

        var easeProps = properties.Where(p => p.IsEase).ToList();
        if (easeProps.Count == 0) return;

        var minEase = easeProps.Sum(p => p.Min ?? 0);
        var maxEase = easeProps.Sum(p => p.Max ?? 0);
        if (minEase == 0 && maxEase == 0) return;

        equipment.RequiredStrength = AdjustStat(equipment.RequiredStrength, minEase, maxEase);
        equipment.RequiredDexterity = AdjustStat(equipment.RequiredDexterity, minEase, maxEase);
    }

    private static string? AdjustStat(string? baseStatStr, int minEase, int maxEase)
    {
        if (string.IsNullOrEmpty(baseStatStr) || baseStatStr == "0") return baseStatStr;
        if (!int.TryParse(baseStatStr, out var baseStat)) return baseStatStr;

        var val1 = (int)Math.Floor(baseStat * (100.0 + minEase) / 100.0);
        var val2 = (int)Math.Floor(baseStat * (100.0 + maxEase) / 100.0);

        var finalMin = Math.Min(val1, val2);
        var finalMax = Math.Max(val1, val2);

        return finalMin == finalMax ? finalMin.ToString() : $"{finalMin}-{finalMax}";
    }

    private static bool IsExplicitRequiredLevelIncrease(GameData data, string? propertyCode)
    {
        if (string.IsNullOrWhiteSpace(propertyCode)) return false;

        if (string.Equals(propertyCode, "item_levelreq", StringComparison.OrdinalIgnoreCase))
            return true;

        return data.Properties.TryGetValue(propertyCode, out var property)
               && string.Equals(property.Stat1, "item_levelreq", StringComparison.OrdinalIgnoreCase);
    }
}
