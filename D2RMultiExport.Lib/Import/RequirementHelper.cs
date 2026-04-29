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
            // Sum explicit increases: item_levelreq
            if (string.Equals(prop.PropertyCode, "item_levelreq", StringComparison.OrdinalIgnoreCase))
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
}
