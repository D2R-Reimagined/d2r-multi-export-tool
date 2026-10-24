// SPDX-License-Identifier: GPL-3.0-or-later
using D2RMultiExport.Lib.ErrorHandling;
using D2RMultiExport.Lib.Models;
using D2RReimaginedTools.TextFileParsers;

namespace D2RMultiExport.Lib.Import;

/// <summary>
/// Imports unique items from UniqueItems.txt via D2RReimaginedTools and maps
/// them into UniqueExport models with per-item error handling.
/// </summary>
public sealed class UniqueImporter
{
    private readonly GameData _data;
    private readonly SourceRoots _sources;

    public UniqueImporter(SourceRoots sources, GameData data)
    {
        _sources = sources;
        _data = data;
    }

    public async Task<ImportResult<UniqueExport>> ImportAsync()
    {
        var result = new ImportResult<UniqueExport>();
        var config = _data.ExportConfig;

        IList<D2RReimaginedTools.Models.UniqueItem> rawEntries;
        int?[] engineIndices;
        try
        {
            var path = _sources.Excel("uniqueitems.txt");
            rawEntries = await UniqueItemsParser.GetEntries(path);
            engineIndices = await EngineRowIndex.DeriveAsync(path, [.. rawEntries.Select(static entry => entry.Index)]);
        }
        catch (Exception ex)
        {
            result.AddError("Unique", "UniqueItems.txt", $"Failed to load file: {ex.Message}", ex);
            return result;
        }

        // Build raw line index for vanilla detection
        var rawIndexByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < rawEntries.Count; i++)
        {
            var idx = rawEntries[i].Index;
            if (!string.IsNullOrWhiteSpace(idx) && !rawIndexByName.ContainsKey(idx))
                rawIndexByName[idx] = i + 1; // 1-based including header
        }

        // The binary unique ID is the engine record index (EngineRowIndex): the
        // zero-based UniqueItems.txt row position after the game's legacy Expansion
        // marker is removed. Other section-header rows (Rings, Armor, ...) still
        // consume IDs, so neither the physical row nor the informational *ID column
        // is authoritative once the mod adds section headers.
        var saveIndexByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < rawEntries.Count; i++)
        {
            var idx = rawEntries[i].Index;
            if (engineIndices[i] is { } saveIndex && !string.IsNullOrWhiteSpace(idx))
                saveIndexByName.TryAdd(idx, saveIndex);
        }

        // 1-based position among the parsed data rows (header excluded, blank lines not
        // counted), reported with property-line diagnostics; not a physical line number.
        var parsedRow = 0;
        foreach (var entry in rawEntries)
        {
            parsedRow++;
            var name = entry.Index ?? "";
            try
            {
                // Skip disabled / level-0 / ignored items.
                // A row is dropped only when it is both not spawnable AND explicitly
                // disabled (`spawnable` column != 1 AND `disabled` column = 1) — rows
                // that are disabled but still spawnable are kept for the export.
                if (!entry.Spawnable && entry.Disabled) continue;
                if (entry.Level == 0) continue;
                if (config.IgnoredUniqueItemsSet.Any(ignored => name.Contains(ignored, StringComparison.OrdinalIgnoreCase)))
                    continue;

                var code = entry.Code ?? "";

                // Resolve base equipment
                var baseEq = EquipmentHelper.GetBaseEquipment(code, _data);

                // Map properties
                var properties = MapProperties(entry, name, result, entry.Level, parsedRow);

                var equipment = EquipmentHelper.MapToExport(baseEq, name, properties, _data, config, entry.Level);

                // Adjust str/dex requirements based on "ease" properties BEFORE
                // DamageArmorCalculator.Apply bakes the requirement KeyedLines
                // (via EquipmentHelper.AppendFinalRequirementLines). Adjusting
                // afterwards only mutated the string field, which the keyed wire
                // output ignores, so the ease modifier never reached the export.
                if (equipment != null)
                {
                    RequirementHelper.ApplyEaseAdjustment(equipment, properties);
                }

                // Apply damage/armor calculations (enhanced damage, defense, elemental, smite, durability)
                if (equipment != null && baseEq != null)
                {
                    DamageArmorCalculator.Apply(equipment, baseEq, properties, entry.LevelRequirement, _data);
                }

                // Remove properties that were consumed by DamageArmorCalculator (e.g. "dur")
                properties.RemoveAll(p => PropertyMapper.IsPostCalcFiltered(p.PropertyCode ?? "", _data.ExportConfig));

                // Determine vanilla flag (config-driven, replaces hardcoded row check + sunder names)
                var isVanilla = (rawIndexByName.TryGetValue(name, out var rawRow) && rawRow <= config.VanillaUniqueMaxRow)
                                || config.VanillaUniqueOverridesSet.Contains(name);

                var unique = new UniqueExport
                {
                    FileIndex = saveIndexByName[name],
                    InventoryFile = entry.InvFile,
                    // Use the canonical itype index (e.g. "h2hitype") instead
                    // of the English ItemTypeName so the website resolves the
                    // type label via strings/<lang>.json the same way weapons
                    // and armors do — never carrying the raw English value.
                    Type = equipment?.Type?.Index ?? "",
                    Vanilla = isVanilla ? "Y" : "N",
                    Name = _data.Translations.GetValue(name),
                    Index = name,
                    Enabled = entry.Spawnable || entry.Enabled,
                    Rarity = entry.Rarity,
                    ItemLevel = entry.Level,
                    RequiredLevel = entry.LevelRequirement,
                    Code = code,
                    Properties = properties,
                    DamageArmorEnhanced = properties.Any(p => config.DamageArmorEnhancedCodesSet.Contains(p.PropertyCode ?? "")),
                    Equipment = equipment?.ToSlim()
                };

                result.AddItem(unique);
            }
            catch (Exception ex)
            {
                result.AddError("Unique", name, $"Failed to process: {ex.Message}", ex);
            }
            finally
            {
                // Omissions MapProperties did not attribute to a slot (e.g. a later re-map).
                foreach (var omission in PropertyMapper.TakeOmittedLines(_data))
                    result.AddWarning("Unique", name, $"uniqueitems.txt parsed row {parsedRow}: {omission}");
            }
        }

        // Sort items by RequiredLevel to match old tool output
        result.Items = result.Items.OrderBy(x => x.RequiredLevel).ToList();

        return result;
    }


    private List<CubePropertyExport> MapProperties(D2RReimaginedTools.Models.UniqueItem entry, string itemName, ImportResult<UniqueExport> result, int itemLevel, int parsedRow)
    {
        var properties = new List<CubePropertyExport>();
        if (entry.Properties == null) return [];

        for (int i = 0; i < entry.Properties.Count; i++)
        {
            var prop = entry.Properties[i];
            if (string.IsNullOrEmpty(prop.Property)) continue;
            var slot = i + 1;
            PropertySourceValidator.Report(result, itemName, "uniqueitems.txt", parsedRow,
                new SourcePropertySlot($"prop{slot}", $"par{slot}", $"min{slot}", $"max{slot}", prop.Property, prop.Parameter, prop.Min, prop.Max),
                _data);
            if (PropertyMapper.IsIgnored(prop.Property, _data.ExportConfig)) continue;

            try
            {
                // Check if this property code is a property group (e.g. "Gelid-Affix1")
                if (PropertyGroupExpander.TryExpand(prop.Property, i, itemLevel, _data, out var groupExpansion))
                {
                    properties.AddRange(groupExpansion);
                    continue;
                }

                // Keep raw values through the shared expansion path, including
                // direct damage components and poison rates.
                foreach (var row in PropertyMapper.MapRows(prop.Property, prop.Parameter, prop.Min, prop.Max, _data, itemLevel, i))
                {
                    // Retain known calculation-only and split component rows even
                    // when they have no tooltip until cleanup/equipment calculation.
                    if (row.Lines.Count == 0
                        && !_data.Properties.ContainsKey(row.PropertyCode!)
                        && !PropertyCleanup.IsSplitElementalCode(row.PropertyCode, _data.ExportConfig))
                        continue;

                    properties.Add(row);
                }
            }
            catch (Exception ex)
            {
                result.AddWarning("Unique", itemName, $"Failed to map property '{prop.Property}': {ex.Message}");
            }
            finally
            {
                foreach (var omission in PropertyMapper.TakeOmissions(_data))
                    result.AddWarning("Unique", itemName, DescribeSlotOmission(omission, prop, i + 1, parsedRow));
            }
        }

        // Merge duplicate properties (e.g. two dmg-min entries → "Adds X-Y Weapon Damage")
        properties = PropertyCleanup.CleanupDuplicates(properties, _data, itemLevel);

        return properties;
    }

    /// <summary>
    /// Reports an omitted property line against the <c>uniqueitems.txt</c>
    /// <c>prop/par/min/max</c> slot that produced it, with the raw cells and, when mapping
    /// changed them (expansion, property group, parameter fallback), the mapped values.
    /// </summary>
    private static string DescribeSlotOmission(OmittedPropertyLine omission, D2RReimaginedTools.Models.ItemProperty prop, int slot, int parsedRow)
    {
        var unchanged = string.Equals(omission.PropertyCode, prop.Property, StringComparison.OrdinalIgnoreCase)
                        && (omission.Parameter ?? "") == (prop.Parameter ?? "")
                        && omission.Min == prop.Min && omission.Max == prop.Max;
        var reason = unchanged ? omission.Reason($"min{slot}", $"max{slot}") : omission.Reason();
        return $"Property line omitted for prop{slot} '{prop.Property}':"
               + $"\n    Source:   uniqueitems.txt parsed row {parsedRow}; par{slot}={OmittedPropertyLine.Cell(prop.Parameter)}, "
               + $"min{slot}={OmittedPropertyLine.Cell(prop.Min)}, max{slot}={OmittedPropertyLine.Cell(prop.Max)}"
               + (unchanged
                   ? ""
                   : $"\n    Mapped:   '{omission.PropertyCode}' param {OmittedPropertyLine.Cell(omission.Parameter)}, "
                     + $"min {OmittedPropertyLine.Cell(omission.Min)}, max {OmittedPropertyLine.Cell(omission.Max)}")
               + $"\n    Template: {omission.DescribeTemplate()} needs a value, but {reason}."
               + "\n    Action: the line was omitted; no value was invented.";
    }

    // The propertygroups.txt expansion helper used to live here as
    // ExpandPropertyGroup; it now lives in PropertyGroupExpander so every
    // importer (uniques, sets, runewords, cube recipes, magic affixes,
    // automagic) emits the same strPropertyGroupsProperty parent line
    // whenever a property code resolves to a group entry.

}
