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
    /// Expands a <c>propertygroups.txt</c> entry referenced from a unique item's
    /// property list (e.g. crafted charms reference <c>Magnetic-Affix1..6</c> /
    /// <c>Gelid-Affix*</c> groups) into a single parent <see cref="CubePropertyExport"/>
    /// whose <see cref="CubePropertyExport.Lines"/> contains one parent
    /// <see cref="KeyedLine"/>. The parent line carries the group's raw English
    /// <see cref="KeyedLine.Code"/> (one of the documented English-passthrough
    /// exceptions, alongside <c>PType</c> and <c>RequiredClass</c>) and the
    /// group's <see cref="KeyedLine.PickMode"/>. Each resolved sub-property is
    /// emitted as a child <see cref="KeyedLine"/> on
    /// <see cref="KeyedLine.Children"/>, carrying its own <see cref="KeyedLine.Chance"/>
    /// (the per-row <c>ChanceN</c> column verbatim).
    ///
    /// The previous "<c>charm-weight</c> only" filter was a regression that
    /// silently dropped the probabilistic affixes on items like
    /// <c>Crafted Crack of the Heavens</c>. The earlier flat reshape (every
    /// sub-property emitted as a sibling line stamped with chance+pickMode)
    /// lost the parent/child hierarchy that the website renders against; this
    /// implementation preserves it explicitly.
    /// </summary>
    private List<CubePropertyExport> ExpandPropertyGroup(PropertyGroupEntry group, int index, int itemLevel)
    {
        // Stringify the group's PickMode once so it can live on the parent line.
        // Null when the group has no PickMode column populated (rare — treated
        // as "always-on", no pickMode/chance written).
        var pickModeStr = group.PickMode?.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var children = new List<KeyedLine>();

        foreach (var sub in group.SubProperties)
        {
            if (string.IsNullOrEmpty(sub.Property)) continue;
            if (PropertyMapper.IsIgnored(sub.Property, _data.ExportConfig)) continue;

            try
            {
                var resolved = PropertyMapper.Map(sub.Property, sub.Parameter, sub.Min, sub.Max, _data, itemLevel);

                // Skip rows that produced no keyed line and aren't recognised in
                // Properties.txt — same gate as the main MapProperties loop, so
                // unknown sub-property codes don't leak through silently.
                if (resolved.Lines.Count == 0
                    && !_data.Properties.ContainsKey(sub.Property)
                    && !PropertyCleanup.IsSplitElementalCode(sub.Property, _data.ExportConfig))
                    continue;

                // Stamp the per-row ChanceN value on every line resolved for
                // this sub-property. The parent's PickMode tells the consumer
                // how to interpret the value; we don't repeat it on each child.
                // chance==0 / null means "always-on" — leave the field unset.
                foreach (var line in resolved.Lines)
                {
                    if (sub.Chance is > 0)
                        line.Chance = sub.Chance;

                    // Mod-added class-restricted random-skill-tab groups
                    // (e.g. `skilltab-war` on Wraithstep) wrap a `skilltab`
                    // sub-property whose Min/Max parameter range doesn't
                    // resolve to a single tab id, so BuildSkillTab falls back
                    // to strSkillTabBonusClassOnly with an empty class slot.
                    // Mirror the doc-generator's hardcoded Wraithstep
                    // workaround by rewriting such children into a
                    // "+N Random Skill Tab (<Class> Only)" line scoped to
                    // the configured class. The mapping lives in
                    // export-config.json → customSkillTabAliases (parent
                    // group code → 3-letter CharStats class code).
                    if (_data.ExportConfig.CustomSkillTabAliases.TryGetValue(group.Code, out var aliasClassCode)
                        && _data.CharStatsByCode.ContainsKey(aliasClassCode)
                        && (line.Key == Translation.SyntheticStringRegistry.Keys.SkillTabBonusClassOnly
                            || line.Key == Translation.SyntheticStringRegistry.Keys.SkillTabRandomClassOnly))
                    {
                        var bonus = (line.Args.Length > 0 && line.Args[0] is int v)
                            ? v
                            : (sub.Min ?? sub.Max ?? 0);
                        line.Key = Translation.SyntheticStringRegistry.Keys.SkillTabRandomClassOnly;
                        line.Args = [bonus];
                        line.ClassOnly = Translation.PropertyKeyResolver.TryGetClassOnlyKey(aliasClassCode);
                    }

                    children.Add(line);
                }
            }
            catch
            {
                // Skip unresolvable sub-properties; the import-report's main
                // MapProperties loop catches the rest.
            }
        }

        // Empty group → emit nothing (matches doc-generator: a property group
        // with no resolvable sub-properties produces no on-wire entry).
        if (children.Count == 0) return [];

        var parent = new KeyedLine
        {
            // Parent has no translation key/args of its own; the consumer
            // detects parent-ness by the presence of `code` + `children`.
            Code = group.Code,
            PickMode = pickModeStr,
            Children = children
        };

        return
        [
            new CubePropertyExport
            {
                Index = index,
                PropertyCode = group.Code,
                // Force propertygroup affixes to sort AFTER all standard
                // properties (which use real ItemStatCost descPriority values,
                // always >= 0). FlattenPropertiesByPriority sorts by Priority
                // descending with source-index as a stable tiebreaker, so
                // int.MinValue here pushes every group parent to the bottom
                // while preserving the on-item order between groups.
                Priority = int.MinValue,
                PickMode = pickModeStr,
                Lines = [parent]
            }
        ];
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
