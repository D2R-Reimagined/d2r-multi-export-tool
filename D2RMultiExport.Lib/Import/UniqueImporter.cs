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
    private readonly string _excelPath;

    public UniqueImporter(string excelPath, GameData data)
    {
        _excelPath = excelPath;
        _data = data;
    }

    public async Task<ImportResult<UniqueExport>> ImportAsync()
    {
        var result = new ImportResult<UniqueExport>();
        var config = _data.ExportConfig;

        IList<D2RReimaginedTools.Models.UniqueItem> rawEntries;
        try
        {
            rawEntries = await UniqueItemsParser.GetEntries(Path.Combine(_excelPath, "UniqueItems.txt"));
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

        foreach (var entry in rawEntries)
        {
            var name = entry.Index ?? "";
            try
            {
                // Skip disabled / level-0 / ignored items
                if (entry.Disabled) continue;
                if (entry.Level == 0) continue;
                if (config.IgnoredUniqueItemsSet.Any(ignored => name.Contains(ignored, StringComparison.OrdinalIgnoreCase)))
                    continue;

                var code = entry.Code ?? "";

                // Resolve base equipment
                var baseEq = EquipmentHelper.GetBaseEquipment(code, _data);

                // Map properties
                var properties = MapProperties(entry, name, result, entry.Level);

                var equipment = EquipmentHelper.MapToExport(baseEq, name, properties, _data, config, entry.Level);

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
                    DamageArmorEnhanced = properties.Any(p => p.PropertyCode == "ac%" || p.PropertyCode == "dmg%"),
                    Equipment = equipment?.ToSlim()
                };

                // Adjust requirements based on "ease" properties
                AdjustRequirements(unique);

                result.AddItem(unique);
            }
            catch (Exception ex)
            {
                result.AddError("Unique", name, $"Failed to process: {ex.Message}", ex);
            }
        }

        // Sort items by RequiredLevel to match old tool output
        result.Items = result.Items.OrderBy(x => x.RequiredLevel).ToList();

        return result;
    }


    private List<CubePropertyExport> MapProperties(D2RReimaginedTools.Models.UniqueItem entry, string itemName, ImportResult<UniqueExport> result, int itemLevel)
    {
        var properties = new List<CubePropertyExport>();
        if (entry.Properties == null) return [];

        for (int i = 0; i < entry.Properties.Count; i++)
        {
            var prop = entry.Properties[i];
            if (string.IsNullOrEmpty(prop.Property)) continue;
            if (PropertyMapper.IsIgnored(prop.Property, _data.ExportConfig)) continue;

            try
            {
                // Check if this property code is a property group (e.g. "Gelid-Affix1")
                if (_data.PropertyGroups.TryGetValue(prop.Property, out var directGroup))
                {
                    properties.AddRange(ExpandPropertyGroup(directGroup, i, itemLevel));
                    continue;
                }

                // charm-property resolves directly via Properties.txt → stat charm_weight
                // (no property group expansion needed)

                // Expand dmg-elem into fire, cold, lightning (matching old code's GetProperties)
                if (string.Equals(prop.Property, "dmg-elem", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var elem in new[] { "dmg-fire", "dmg-cold", "dmg-ltng" })
                    {
                        var elemResolved = PropertyMapper.Map(elem, prop.Parameter, prop.Min, prop.Max, _data, itemLevel);
                        properties.Add(new CubePropertyExport
                        {
                            Index = i,
                            PropertyCode = elem,
                            Priority = elemResolved.Priority,
                            Min = prop.Min,
                            Max = prop.Max,
                            Parameter = prop.Parameter,
                            Lines = elemResolved.Lines
                        });
                    }
                    continue;
                }

                // Expand multi-stat elemental properties into separate min/max/len entries
                // so MergeElementalTriplets can aggregate them properly
                if (PropertyMapper.TryExpandElemental(prop.Property, prop.Parameter, prop.Min, prop.Max, _data, itemLevel, i, out var expanded))
                {
                    properties.AddRange(expanded);
                    continue;
                }

                var resolved = PropertyMapper.Map(prop.Property, prop.Parameter, prop.Min, prop.Max, _data, itemLevel);

                // Skip properties that didn't resolve to any keyed line AND aren't
                // recognised in Properties.txt — i.e. truly unknown property codes
                // (the legacy English path used to render these as bare numbers).
                // Exceptions:
                //  • Split elemental damage codes (pois-min/max/len, cold-min/max/len,
                //    fire-min/max, ltng-min/max) resolve to no keyed line because their
                //    ItemStatCost rows have empty descstrpos (descfunc 0); they must
                //    reach PropertyCleanup.MergeElementalTriplets to be aggregated
                //    into dmg-pois / dmg-cold / dmg-fire / dmg-ltng entries.
                //  • Known property codes (e.g. "dur", "indestruct") that have no
                //    keyed-line template are still consumed by DamageArmorCalculator
                //    and stripped afterwards via IsPostCalcFiltered.
                if (resolved.Lines.Count == 0
                    && !_data.Properties.ContainsKey(prop.Property)
                    && !PropertyCleanup.IsSplitElementalCode(prop.Property, _data.ExportConfig))
                    continue;

                properties.Add(new CubePropertyExport
                {
                    Index = i,
                    PropertyCode = prop.Property,
                    Priority = resolved.Priority,
                    Min = prop.Min,
                    Max = prop.Max,
                    Parameter = prop.Parameter,
                    IsEase = prop.Property == "ease",
                    Lines = resolved.Lines
                });
            }
            catch (Exception ex)
            {
                result.AddWarning("Unique", itemName, $"Failed to map property '{prop.Property}': {ex.Message}");
            }
        }

        // Merge duplicate properties (e.g. two dmg-min entries → "Adds X-Y Weapon Damage")
        properties = PropertyCleanup.CleanupDuplicates(properties, _data, itemLevel);

        return properties;
    }

    /// <summary>
    /// Extracts only "charm-weight" from property groups as a flat property.
    /// Other sub-properties are not shown in the main Properties list (matching old code).
    /// </summary>
    private List<CubePropertyExport> ExpandPropertyGroup(PropertyGroupEntry group, int index, int itemLevel)
    {
        var result = new List<CubePropertyExport>();

        foreach (var sub in group.SubProperties)
        {
            if (string.IsNullOrEmpty(sub.Property)) continue;

            // Only extract charm-weight as a visible property
            if (!string.Equals(sub.Property, "charm-weight", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                var resolved = PropertyMapper.Map(sub.Property, sub.Parameter, sub.Min, sub.Max, _data, itemLevel);
                result.Add(new CubePropertyExport
                {
                    Index = index,
                    PropertyCode = sub.Property,
                    Priority = resolved.Priority,
                    Min = sub.Min,
                    Max = sub.Max,
                    Parameter = sub.Parameter,
                    Lines = resolved.Lines
                });
            }
            catch
            {
                // Skip unresolvable
            }
        }

        return result;
    }

    private static void AdjustRequirements(UniqueExport unique)
    {
        if (unique.Equipment == null || unique.Properties.Count == 0) return;

        var easeProps = unique.Properties
            .Where(p => p.IsEase)
            .ToList();
        if (easeProps.Count == 0) return;

        var minEase = easeProps.Sum(p => p.Min ?? 0);
        var maxEase = easeProps.Sum(p => p.Max ?? 0);
        if (minEase == 0 && maxEase == 0) return;

        unique.Equipment.RequiredStrength = AdjustStat(unique.Equipment.RequiredStrength, minEase, maxEase);
        unique.Equipment.RequiredDexterity = AdjustStat(unique.Equipment.RequiredDexterity, minEase, maxEase);
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
}
