// SPDX-License-Identifier: GPL-3.0-or-later
using D2RMultiExport.Lib.Config;
using D2RMultiExport.Lib.Models;

namespace D2RMultiExport.Lib.Import;

public static class PropertyMapper
{
    /// <summary>
    /// Returns true if the given property code should be ignored (not exported).
    /// The ignore set is sourced from <see cref="ExportConfig.IgnoredPropertyCodes"/>
    /// (see <c>Config/export-config.json</c>).
    /// </summary>
    public static bool IsIgnored(string code, ExportConfig config) =>
        config.IgnoredPropertyCodesSet.Contains(code);

    /// <summary>
    /// Returns true if the property should be removed from the export list
    /// after damage/armor calculations are done. Backed by
    /// <see cref="ExportConfig.PropertyPostCalcFilteredCodesSet"/>
    /// (<c>export-config.json → propertyPostCalcFilteredCodes</c>).
    /// </summary>
    public static bool IsPostCalcFiltered(string code, ExportConfig config) =>
        config.PropertyPostCalcFilteredCodesSet.Contains(code);

    public static bool TryExpandElemental(string code, string? param, int? min, int? max, GameData data, int itemLevel, int index, out List<CubePropertyExport> results)
    {
        results = [];
        if (!data.ExportConfig.PropertyElementalExpansions.TryGetValue(code, out var expansion))
            return false;

        var minResolved = Map(expansion.Min, param, min, null, data, itemLevel);
        results.Add(new CubePropertyExport
        {
            Index = index,
            PropertyCode = expansion.Min,
            Priority = minResolved.Priority,
            Min = min,
            Max = min,
            Parameter = param,
            Lines = minResolved.Lines
        });

        var maxResolved = Map(expansion.Max, param, max, null, data, itemLevel);
        results.Add(new CubePropertyExport
        {
            Index = index,
            PropertyCode = expansion.Max,
            Priority = maxResolved.Priority,
            Min = max,
            Max = max,
            Parameter = param,
            Lines = maxResolved.Lines
        });

        if (!string.IsNullOrEmpty(expansion.Len) && int.TryParse(param, out var lenFrames) && lenFrames > 0)
        {
            results.Add(new CubePropertyExport
            {
                Index = index,
                PropertyCode = expansion.Len,
                Priority = 0,
                Min = lenFrames,
                Max = lenFrames,
                Parameter = param
            });
        }

        return true;
    }

    public static ExportProperty Map(string code, string? param, int? min, int? max, GameData data, int itemLevel)
    {
        // When the item-level parameter is empty/zero, fall back to Properties.txt val1
        // (e.g. "nec" property has val1=2 for Necromancer class index)
        // Note: param may be "0" for class-specific properties where the game defaults empty to 0
        if ((string.IsNullOrEmpty(param) || param == "0") && data.Properties.TryGetValue(code, out var propLookup) && propLookup.Value1.HasValue)
        {
            param = propLookup.Value1.Value.ToString();
        }

        // ── Class-skill / random-tab parameter normalization ──────────────────────────
        // Ports the legacy doc-generator's ItemProperty constructor fix: per-class
        // skill bonuses (ama/sor/nec/pal/bar/dru/ass/war/...) and the random skill
        // tab (tab-rand) arrive with parameters that the descfunc 13/14 renderers
        // can't disambiguate (val1 is often 0/1, so every per-class skill bonus
        // rendered as Amazon, and tab-rand encodes the class in Min/8). Reshape the
        // (param, min, max) tuple here so PropertyKeyResolver downstream sees
        // consistent inputs and emits the correct per-class ItemStatCost descstr
        // keys (ModStr3a..ModStrge9).
        string? earlyStatKey = null;
        if (data.Properties.TryGetValue(code, out var earlyPropEntry) && !string.IsNullOrEmpty(earlyPropEntry.Stat1))
            earlyStatKey = earlyPropEntry.Stat1;

        if (string.Equals(earlyStatKey, "item_addclassskills", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(code, "randclassskill", StringComparison.OrdinalIgnoreCase))
            {
                // Random class skill: the parameter carries the count of skills
                // granted. Lift it into Min/Max so the resolver shortcut at
                // PropertyKeyResolver case "randclassskill" and FormatClassSkill
                // see the right value. Leave Parameter alone so the sentinel
                // string still drives the random-class branch.
                if (int.TryParse(param, out var pv))
                {
                    min = pv;
                    max = pv;
                }
            }
            else
            {
                // Per-class properties (ama/sor/nec/pal/bar/dru/ass/war/...): the
                // val1 fallback collapses Parameter to the wrong class index, so
                // every per-class skill bonus rendered as Amazon. Feed both
                // descfunc 13 renderers (BuildClassSkill / FormatClassSkill) the
                // property code itself — both already accept a 3-letter
                // CharStats code — and the resolver picks up the correct
                // per-class ItemStatCost row's descstrpos (ModStr3a..ModStrge9).
                param = code;
            }
        }

        if (string.Equals(earlyStatKey, "item_addskill_tab", StringComparison.OrdinalIgnoreCase)
            && string.Equals(code, "tab-rand", StringComparison.OrdinalIgnoreCase))
        {
            // tab-rand (random skill tab) encodes the class in (Min / 8). Translate
            // to the 3-letter CharStats code via ClassRangeConfig (data-driven so
            // mods that add or shift classes need no recompile), then move the
            // original Parameter value into Min/Max for the renderer.
            var parValue = int.TryParse(param, out var parsedTabVal) ? parsedTabVal : 0;
            var tabIndex = (min ?? 0) / 8;
            if (tabIndex >= 0 && tabIndex < ClassRangeConfig.ClassOrder.Count)
            {
                param = ClassRangeConfig.ClassOrder[tabIndex];
                min = parValue;
                max = parValue;
            }
        }

        var exportProp = new ExportProperty
        {
            PropertyCode = code,
            Parameter = param,
            Min = min,
            Max = max
        };

        // Resolve priority from stat data
        string? statKey = null;
        if (data.Properties.TryGetValue(code, out var propEntry) && !string.IsNullOrEmpty(propEntry.Stat1))
        {
            statKey = propEntry.Stat1;
        }

        // Apply manual stat overrides for codes with missing/wrong stat mappings
        if (statKey == null && data.ExportConfig.PropertyStatOverrides.TryGetValue(code, out var overrideStat))
        {
            statKey = overrideStat;
        }

        if (statKey != null && data.Stats.TryGetValue(statKey, out var stat))
        {
            exportProp.Priority = stat.DescriptionPriority ?? 0;
        }
        // Fallback: check if the property code itself is a synthetic stat (e.g. dmg%, ac%, ac, dmg-min, dmg-max)
        else if (data.Stats.TryGetValue(code, out var directStat))
        {
            exportProp.Priority = directStat.DescriptionPriority ?? 0;
        }

        // Apply priority overrides for codes with missing stat priority
        if (exportProp.Priority == 0 && data.ExportConfig.PropertyPriorityOverrides.TryGetValue(code, out var prioOverride))
        {
            exportProp.Priority = prioOverride;
        }

        // Emit the key-based wire rows. These are what KeyedJsonExporter
        // serializes — the only rendering of a property that reaches the wire.
        try
        {
            exportProp.Lines = Translation.PropertyKeyResolver.Resolve(exportProp, data, itemLevel);
        }
        catch
        {
            // Resolver failures must never break the import. The import-report
            // already records property errors via the regular pipeline; here we
            // just keep the Lines list empty so the wire is well-formed.
            exportProp.Lines = [];
        }

        return exportProp;
    }
}

