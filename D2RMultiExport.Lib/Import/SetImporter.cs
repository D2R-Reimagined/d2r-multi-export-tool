// SPDX-License-Identifier: GPL-3.0-or-later
using D2RMultiExport.Lib.ErrorHandling;
using D2RMultiExport.Lib.Models;
using D2RReimaginedTools.TextFileParsers;
using D2RReimaginedTools.Models;

namespace D2RMultiExport.Lib.Import;

public sealed class SetImporter
{
    private readonly GameData _data;
    private readonly string _excelPath;
    private readonly Config.ExportConfig _config;

    public SetImporter(string excelPath, GameData data)
    {
        _excelPath = excelPath;
        _data = data;
        _config = data.ExportConfig;
    }

    public async Task<ImportResult<SetExport>> ImportAsync()
    {
        var result = new ImportResult<SetExport>();

        // 1. Process all Set Items first to build a lookup by set name
        var setItemMap = await ProcessSetItems(result);

        // 2. Process Sets
        foreach (var entry in _data.SetEntries.Values)
        {
            try
            {
                if (string.IsNullOrEmpty(entry.Index)) continue;

                var setExport = new SetExport
                {
                    Name = entry.Name ?? entry.Index,
                    Index = entry.Index,
                };

                if (setItemMap.TryGetValue(setExport.Index, out var items))
                {
                    setExport.SetItems.AddRange(items);
                    // The legacy English post-processing that rewrote bonus
                    // suffixes from " (Other Set Item)" to " (<other item name>)"
                    // mutated only the never-serialized PropertyString; the wire
                    // already encodes the per-item linkage via the keyed
                    // `OtherSetItemIndex` arg, so no fix-up is needed here.
                }
                else
                {
                    // Skip empty sets
                    continue;
                }

                setExport.Vanilla = setExport.SetItems.Any(i => i.Vanilla == "Y") ? "Y" : "N";

                // Map set-wide partial and full bonuses
                MapSetBonuses(entry, setExport);

                // Sort full bonuses by priority descending to match old tool
                setExport.FullBonuses = setExport.FullBonuses.OrderByDescending(p => p.Priority).ToList();

                result.AddItem(setExport);
            }
            catch (Exception ex)
            {
                result.AddError("Set", entry.Index ?? "unknown", $"Failed to process set: {ex.Message}", ex);
            }
        }

        // Sort items by Level to match old tool output
        result.Items = result.Items.OrderBy(x => x.ItemLevel).ToList();

        return result;
    }

    private async Task<Dictionary<string, List<SetItemExport>>> ProcessSetItems(ImportResult<SetExport> result)
    {
        var setItemMap = new Dictionary<string, List<SetItemExport>>(StringComparer.OrdinalIgnoreCase);

        // Load raw entries for vanilla detection
        var rawEntries = await SetItemParser.GetEntries(Path.Combine(_excelPath, "SetItems.txt"));
        var rawIndexByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < rawEntries.Count; i++)
        {
            var idx = rawEntries[i].Index;
            if (!string.IsNullOrEmpty(idx) && !rawIndexByName.ContainsKey(idx))
                rawIndexByName[idx] = i + 1;
        }

        foreach (var entry in _data.SetItemEntries.Values)
        {
            try
            {
                if (string.IsNullOrEmpty(entry.Item)) continue;
                // Skip rows whose `spawnable` column != 1 AND `disabled` column = 1
                // (i.e. the row is both unreachable in-game and explicitly disabled).
                if (entry.Spawnable != true && entry.Disabled == true) continue;

                var isVanilla = (rawIndexByName.TryGetValue(entry.Index ?? "", out var rawIdx) && rawIdx <= _config.VanillaSetMaxRow);
                entry.Vanilla = isVanilla ? 1 : 0;

                var export = new SetItemExport
                {
                    Name = _data.Translations.GetValue(entry.Index ?? ""),
                    Index = entry.Index ?? "",
                    SetName = entry.Set ?? "",
                    Code = entry.Item,
                    Rarity = entry.Rarity ?? 0,
                    ItemLevel = entry.Level ?? 0,
                    RequiredLevel = entry.LevelRequirement ?? 0,
                    Vanilla = isVanilla ? "Y" : "N"
                };

                var baseEq = EquipmentHelper.GetBaseEquipment(entry.Item, _data);

                // Base properties
                var properties = new List<CubePropertyExport>();
                if (entry.Properties != null)
                {
                    for (int i = 0; i < entry.Properties.Count; i++)
                    {
                        var mod = entry.Properties[i];
                        if (string.IsNullOrEmpty(mod.Code)) continue;
                        if (PropertyMapper.IsIgnored(mod.Code, _data.ExportConfig)) continue;
                        // propertygroups.txt reference (e.g. crafted-affix groups
                        // assigned to a set item) → expand into the parent
                        // KeyedLine carrying strPropertyGroupsProperty.
                        if (PropertyGroupExpander.TryExpand(mod.Code, i, export.ItemLevel, _data, out var groupExpansion))
                        {
                            properties.AddRange(groupExpansion);
                            continue;
                        }
                        var prop = PropertyMapper.Map(mod.Code, mod.Param, mod.Min, mod.Max, _data, export.ItemLevel);
                        properties.Add(new CubePropertyExport
                        {
                            Index = i,
                            PropertyCode = mod.Code,
                            Priority = prop.Priority,
                            Min = mod.Min,
                            Max = mod.Max,
                            Parameter = mod.Param,
                            IsEase = mod.Code == "ease",
                            Lines = prop.Lines
                        });
                    }
                }

                var equipment = EquipmentHelper.MapToExport(baseEq, export.Name, properties, _data, _config, export.ItemLevel);

                // Adjust str/dex requirements based on "ease" properties BEFORE
                // DamageArmorCalculator.Apply bakes the requirement KeyedLines.
                if (equipment != null)
                {
                    RequirementHelper.ApplyEaseAdjustment(equipment, properties);
                }

                // Apply damage/armor calculations (enhanced damage, defense, elemental, smite, durability)
                if (equipment != null && baseEq != null)
                {
                    DamageArmorCalculator.Apply(equipment, baseEq, properties, export.RequiredLevel, _data);
                }

                // Remove properties consumed by DamageArmorCalculator (e.g. "dur")
                properties.RemoveAll(p => PropertyMapper.IsPostCalcFiltered(p.PropertyCode ?? "", _config));

                export.Equipment = equipment?.ToSlim();
                // Use the canonical itype index (e.g. "h2hitype") instead of
                // the English ItemTypeName so the website resolves the type
                // label via strings/<lang>.json the same way weapons and
                // armors do — never carrying the raw English value.
                export.Type = equipment?.Type?.Index ?? "";
                export.Vanilla = entry.Vanilla == 1 ? "Y" : "N";
                export.DamageArmorEnhanced = properties.Any(p => p.PropertyCode == "ac%" || p.PropertyCode == "dmg%");
                export.Properties = properties;

                // Additional properties (Set bonuses on the item)
                //
                // The upstream parser collapses the aprop{N}{a|b} columns into a
                // gap-free list, discarding which physical column each bonus came
                // from. For `add func = 2` the item count that unlocks a bonus is
                // dictated by its column pair (aprop1 → 2 items, aprop2 → 3, …,
                // aprop5 → 6), so we must read the typed columns directly here:
                // iterating the collapsed list and deriving the count from its list
                // index mis-reports sparse rows (e.g. Trang-Oul's Scales only fills
                // aprop2a and aprop4a, which collapsed to indices 0/1 and both
                // showed "2 items required" instead of 3 and 5).
                int addFunc = entry.AddFunc ?? 0;
                foreach (var (mod, columnPair) in EnumerateAdditionalMods(entry))
                {
                    if (PropertyMapper.IsIgnored(mod.Code!, _data.ExportConfig)) continue;
                    // aprop column-pair N is granted once (N + 1) set items are worn.
                    int? numberOfItemsRequired = addFunc == 2 ? columnPair + 1 : null;
                    // propertygroups.txt reference on a set-item additional
                    // property → expand into the parent KeyedLine carrying
                    // strPropertyGroupsProperty. Stamp the per-N-items / full-set
                    // metadata on the parent's children below.
                    if (PropertyGroupExpander.TryExpand(mod.Code!, export.Properties.Count, export.ItemLevel, _data, out var groupExpansion))
                    {
                        foreach (var groupProp in groupExpansion)
                        {
                            if (numberOfItemsRequired.HasValue)
                            {
                                foreach (var parent in groupProp.Lines)
                                foreach (var child in parent.Children ?? new List<KeyedLine>())
                                    child.ItemsRequired = numberOfItemsRequired;
                            }
                            if (addFunc == 0)
                            {
                                export.Properties.Add(groupProp);
                            }
                            else
                            {
                                groupProp.NumberOfItemsRequired = numberOfItemsRequired;
                                export.SetBonuses.Add(new List<CubePropertyExport> { groupProp });
                            }
                        }
                        continue;
                    }

                    var prop = PropertyMapper.Map(mod.Code!, mod.Param, mod.Min, mod.Max, _data, export.ItemLevel);

                    if (addFunc == 0)
                    {
                        export.Properties.Add(new CubePropertyExport
                        {
                            Index = export.Properties.Count,
                            PropertyCode = mod.Code,
                            Priority = prop.Priority,
                            Lines = prop.Lines
                        });
                    }
                    else
                    {
                        // Stamp the KeyedLines for set-item per-N-items bonuses so the
                        // wire format carries the required item count.
                        if (numberOfItemsRequired.HasValue)
                        {
                            foreach (var line in prop.Lines)
                            {
                                line.ItemsRequired = numberOfItemsRequired;
                            }
                        }
                        var bonusProp = new CubePropertyExport
                        {
                            Index = 0, // Since it's a list of lists with one item each?
                            PropertyCode = mod.Code,
                            Priority = prop.Priority,
                            Lines = prop.Lines,
                            NumberOfItemsRequired = numberOfItemsRequired
                        };
                        export.SetBonuses.Add(new List<CubePropertyExport> { bonusProp });
                    }
                }

                // Merge duplicate properties and sort by priority descending
                export.Properties = PropertyCleanup.CleanupDuplicates(export.Properties, _data, export.ItemLevel);

                export.RequiredLevel = RequirementHelper.ComputeAdjustedRequiredLevel(_data, "SetItem", export.Name, export.RequiredLevel, export.Properties, equipment?.RequiredLevel);

                if (!setItemMap.ContainsKey(export.SetName)) setItemMap[export.SetName] = new List<SetItemExport>();
                setItemMap[export.SetName].Add(export);
            }
            catch (Exception ex)
            {
                result.AddError("SetItem", entry.Index ?? "unknown", $"Failed to process: {ex.Message}", ex);
            }
        }

        return setItemMap;
    }

    /// <summary>
    /// Yields each populated SetItems.txt additional-property column together with
    /// its 1-based column-pair index (aprop1a/1b → 1, aprop2a/2b → 2, …, aprop5a/5b → 5).
    /// The upstream parser exposes <see cref="D2RReimaginedTools.Models.SetItem.AdditionalProperties"/>
    /// as a gap-collapsed list that loses the originating column, so the typed
    /// AProp{N}{A|B} fields are read directly to preserve the item-count mapping
    /// used for <c>add func = 2</c> bonuses.
    /// </summary>
    private static IEnumerable<(D2RReimaginedTools.Models.SetItemMod Mod, int ColumnPair)> EnumerateAdditionalMods(D2RReimaginedTools.Models.SetItem entry)
    {
        var columns = new (int Pair, string? Code, string? Param, int? Min, int? Max)[]
        {
            (1, entry.AProp1A, entry.APar1A, entry.AMin1A, entry.AMax1A),
            (1, entry.AProp1B, entry.APar1B, entry.AMin1B, entry.AMax1B),
            (2, entry.AProp2A, entry.APar2A, entry.AMin2A, entry.AMax2A),
            (2, entry.AProp2B, entry.APar2B, entry.AMin2B, entry.AMax2B),
            (3, entry.AProp3A, entry.APar3A, entry.AMin3A, entry.AMax3A),
            (3, entry.AProp3B, entry.APar3B, entry.AMin3B, entry.AMax3B),
            (4, entry.AProp4A, entry.APar4A, entry.AMin4A, entry.AMax4A),
            (4, entry.AProp4B, entry.APar4B, entry.AMin4B, entry.AMax4B),
            (5, entry.AProp5A, entry.APar5A, entry.AMin5A, entry.AMax5A),
            (5, entry.AProp5B, entry.APar5B, entry.AMin5B, entry.AMax5B),
        };

        foreach (var col in columns)
        {
            if (string.IsNullOrEmpty(col.Code)) continue;
            yield return (new D2RReimaginedTools.Models.SetItemMod
            {
                Code = col.Code,
                Param = col.Param,
                Min = col.Min,
                Max = col.Max
            }, col.Pair);
        }
    }

    private void MapSetBonuses(D2RReimaginedTools.Models.Sets entry, SetExport set)
    {
        // Partial bonuses (2, 3, 4, 5 items)
        AddBonus(set.PartialBonuses, entry.PCode2a, entry.PParam2a, entry.PMin2a, entry.PMax2a, 2);
        AddBonus(set.PartialBonuses, entry.PCode2b, entry.PParam2b, entry.PMin2b, entry.PMax2b, 2);
        AddBonus(set.PartialBonuses, entry.PCode3a, entry.PParam3a, entry.PMin3a, entry.PMax3a, 3);
        AddBonus(set.PartialBonuses, entry.PCode3b, entry.PParam3b, entry.PMin3b, entry.PMax3b, 3);
        AddBonus(set.PartialBonuses, entry.PCode4a, entry.PParam4a, entry.PMin4a, entry.PMax4a, 4);
        AddBonus(set.PartialBonuses, entry.PCode4b, entry.PParam4b, entry.PMin4b, entry.PMax4b, 4);
        AddBonus(set.PartialBonuses, entry.PCode5a, entry.PParam5a, entry.PMin5a, entry.PMax5a, 5);
        AddBonus(set.PartialBonuses, entry.PCode5b, entry.PParam5b, entry.PMin5b, entry.PMax5b, 5);

        // Full bonuses
        AddBonus(set.FullBonuses, entry.FCode1, entry.FParam1, entry.FMin1, entry.FMax1, fullSet: true);
        AddBonus(set.FullBonuses, entry.FCode2, entry.FParam2, entry.FMin2, entry.FMax2, fullSet: true);
        AddBonus(set.FullBonuses, entry.FCode3, entry.FParam3, entry.FMin3, entry.FMax3, fullSet: true);
        AddBonus(set.FullBonuses, entry.FCode4, entry.FParam4, entry.FMin4, entry.FMax4, fullSet: true);
        AddBonus(set.FullBonuses, entry.FCode5, entry.FParam5, entry.FMin5, entry.FMax5, fullSet: true);
        AddBonus(set.FullBonuses, entry.FCode6, entry.FParam6, entry.FMin6, entry.FMax6, fullSet: true);
        AddBonus(set.FullBonuses, entry.FCode7, entry.FParam7, entry.FMin7, entry.FMax7, fullSet: true);
        AddBonus(set.FullBonuses, entry.FCode8, entry.FParam8, entry.FMin8, entry.FMax8, fullSet: true);
    }

    private void AddBonus(List<CubePropertyExport> target, string? code, string? param, int? min, int? max, int? numberOfItemsRequired = null, bool fullSet = false)
    {
        if (string.IsNullOrEmpty(code)) return;
        if (PropertyMapper.IsIgnored(code, _data.ExportConfig)) return;
        // propertygroups.txt reference on a set partial/full bonus → expand
        // into the parent KeyedLine. The (N Items) / (full set) metadata is
        // stamped on the parent's children so the website still receives the
        // bucket scope label.
        if (PropertyGroupExpander.TryExpand(code, target.Count, 0, _data, out var groupExpansion))
        {
            foreach (var groupProp in groupExpansion)
            {
                foreach (var parent in groupProp.Lines)
                foreach (var child in parent.Children ?? new List<KeyedLine>())
                {
                    if (numberOfItemsRequired.HasValue) child.ItemsRequired = numberOfItemsRequired;
                    if (fullSet) child.FullSet = true;
                }
                groupProp.NumberOfItemsRequired = numberOfItemsRequired;
                target.Add(groupProp);
            }
            return;
        }
        var prop = PropertyMapper.Map(code, param, min, max, _data, 0);
        // Stamp each KeyedLine so consumers can render their own localized
        // "(N Items)" / "(full set)" labels.
        foreach (var line in prop.Lines)
        {
            if (numberOfItemsRequired.HasValue) line.ItemsRequired = numberOfItemsRequired;
            if (fullSet) line.FullSet = true;
        }
        target.Add(new CubePropertyExport
        {
            Index = target.Count,
            PropertyCode = code,
            Priority = prop.Priority,
            Min = min,
            Max = max,
            Parameter = param,
            IsEase = code == "ease",
            Lines = prop.Lines,
            NumberOfItemsRequired = numberOfItemsRequired
        });
    }
}
