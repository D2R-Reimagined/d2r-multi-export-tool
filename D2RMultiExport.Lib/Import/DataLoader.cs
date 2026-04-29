// SPDX-License-Identifier: GPL-3.0-or-later
using D2RMultiExport.Lib.Config;
using D2RMultiExport.Lib.ErrorHandling;
using D2RMultiExport.Lib.Models;
using D2RMultiExport.Lib.Translation;
using D2RReimaginedTools.TextFileParsers;

namespace D2RMultiExport.Lib.Import;

/// <summary>
/// Loads all game data files using D2RReimaginedTools parsers and maps them into our GameData context.
/// Replaces the old Importer.LoadData() method.
/// </summary>
public sealed class DataLoader
{
    private readonly string _excelPath;
    private readonly GameData _data;
    private readonly PipelineResult _pipeline;

    public DataLoader(string excelPath, GameData data, PipelineResult pipeline)
    {
        _excelPath = excelPath;
        _data = data;
        _pipeline = pipeline;
    }

    public async Task LoadAllAsync()
    {
        await LoadItemStatCostsAsync();
        await LoadPropertiesAsync();
        await LoadItemTypesAsync();
        await LoadSkillDescsAsync();
        await LoadSkillsAsync();
        await LoadCharStatsAsync();
        await LoadMonStatsAsync();
        await LoadPropertyGroupsAsync();
        await LoadArmorsAsync();
        await LoadWeaponsAsync();
        await LoadMiscAsync();
        await LoadGemsAsync();
        await LoadMagicAffixesAsync();
        await LoadSetsAsync();
        await LoadSetItemsAsync();
        ApplyStatOverrides();
    }

    private async Task LoadItemStatCostsAsync()
    {
        try
        {
            var entries = await ItemStatCostParser.GetEntries(Path.Combine(_excelPath, "itemstatcost.txt"));
            foreach (var entry in entries)
            {
                if (string.IsNullOrEmpty(entry.Stat)) continue;

                var stat = new StatEntry
                {
                    Stat = entry.Stat,
                    DescriptionPriority = ParseInt(entry.DescPriority),
                    DescriptionFunction = ParseInt(entry.DescFunc),
                    DescriptionValue = ParseInt(entry.DescVal),
                    DescStrPosKey = entry.DescStrPos,
                    DescStrNegKey = entry.DescStrNeg,
                    DescStr2Key = entry.DescStr2,
                    DescriptionStringPositive = _data.Translations.GetValue(entry.DescStrPos),
                    DescriptionStringNegative = _data.Translations.GetValue(entry.DescStrNeg),
                    DescriptionString2 = _data.Translations.GetValue(entry.DescStr2),
                    GroupDescription = ParseInt(entry.Dgrp),
                    GroupDescriptionFunction = ParseInt(entry.DgrpFunc),
                    GroupDescriptionValue = ParseInt(entry.DgrpVal),
                    GroupDescriptionStringPositive = _data.Translations.GetValue(entry.DgrpStrPos),
                    GroupDescriptionStringNegative = _data.Translations.GetValue(entry.DgrpStrNeg),
                    GroupDescriptionString2 = _data.Translations.GetValue(entry.DgrpStr2),
                    Op = ParseInt(entry.Op),
                    OpParam = ParseInt(entry.OpParam),
                    OpBase = entry.OpBase,
                    OpStat1 = entry.OpStat1,
                    OpStat2 = entry.OpStat2,
                    OpStat3 = entry.OpStat3,
                    SaveBits = ParseInt(entry.SaveBits),
                    SaveAdd = ParseInt(entry.SaveAdd),
                    SaveParam = ParseInt(entry.SaveParamBits),
                };
                _data.Stats[entry.Stat] = stat;
            }
        }
        catch (Exception ex)
        {
            _pipeline.AddError("ItemStatCost", "itemstatcost.txt", $"Failed to load: {ex.Message}", ex);
        }
    }

    private async Task LoadPropertiesAsync()
    {
        try
        {
            var entries = await PropertiesParser.GetEntries(Path.Combine(_excelPath, "properties.txt"));
            foreach (var entry in entries)
            {
                if (string.IsNullOrEmpty(entry.Code)) continue;

                _data.Properties[entry.Code] = new PropertyEntry
                {
                    Code = entry.Code,
                    Stat1 = entry.Stat1,
                    Function1 = entry.Func1,
                    Set1 = entry.Set1,
                    Value1 = ParseInt(entry.Val1),
                };
            }
        }
        catch (Exception ex)
        {
            _pipeline.AddError("Properties", "properties.txt", $"Failed to load: {ex.Message}", ex);
        }
    }

    private async Task LoadItemTypesAsync()
    {
        try
        {
            var entries = await ItemTypeParser.GetEntries(Path.Combine(_excelPath, "itemtypes.txt"));
            foreach (var entry in entries)
            {
                if (string.IsNullOrEmpty(entry.Code)) continue;

                var code = entry.Code.Trim();
                var originalName = (entry.ItemTypeName ?? "").Trim();
                var normalizedName = NormalizeItemTypeName(originalName);

                var itemType = new ItemTypeEntry
                {
                    Code = code,
                    // Canonical, language-agnostic identifier used by exporters and the
                    // website filter graph. Derived from the unique Code column in
                    // itemtypes.txt with an "itype" suffix so the resulting translation
                    // key is unambiguous (e.g. itemtypes.txt code "axe" → key "axeitype")
                    // and cannot collide with item-base codes, skill names, or other
                    // namespaces in the language bundle. The suffix also stops i18next
                    // pluralization heuristics from rewriting bare codes like "axe" to
                    // "Axe [N]" on the rendered page.
                    // Collapse the duplicate hand-to-hand item-type bucket onto the
                    // canonical h2hitype index so exports never carry a separate
                    // h2h2itype identifier or translation key.
                    Index = code.Equals("h2h2", StringComparison.OrdinalIgnoreCase) ? "h2hitype" : code + "itype",
                    Class = entry.Class,
                    Equiv1 = entry.Equiv1,
                    Equiv2 = entry.Equiv2,
                    BodyLoc1 = entry.BodyLoc1,
                    MaxSockets1 = entry.MaxSockets1,
                    MaxSocketsLevelThreshold1 = entry.MaxSocketsLevelThreshold1,
                    MaxSockets2 = entry.MaxSockets2,
                    MaxSocketsLevelThreshold2 = entry.MaxSocketsLevelThreshold2,
                    MaxSockets3 = entry.MaxSockets3,
                    Name = normalizedName
                };
                _data.ItemTypes[code] = itemType;

                // Track by both original and normalized names
                _data.ItemTypesByName[originalName] = itemType;
                if (!string.Equals(originalName, normalizedName, StringComparison.OrdinalIgnoreCase))
                {
                    _data.ItemTypesByName[normalizedName] = itemType;
                }
            }
        }
        catch (Exception ex)
        {
            _pipeline.AddError("ItemTypes", "itemtypes.txt", $"Failed to load: {ex.Message}", ex);
        }
    }

    private async Task LoadSkillDescsAsync()
    {
        try
        {
            var entries = await SkillDescParser.GetEntries(Path.Combine(_excelPath, "skilldesc.txt"));
            foreach (var entry in entries)
            {
                if (string.IsNullOrEmpty(entry.SkillName)) continue;

                _data.SkillDescs[entry.SkillName] = new SkillDescEntry
                {
                    SkillDesc = entry.SkillName,
                    NameString = entry.NameString
                };
            }
        }
        catch (Exception ex)
        {
            _pipeline.AddError("SkillDesc", "skilldesc.txt", $"Failed to load: {ex.Message}", ex);
        }
    }

    private async Task LoadSkillsAsync()
    {
        try
        {
            var entries = await SkillsParser.GetEntries(Path.Combine(_excelPath, "skills.txt"));
            foreach (var entry in entries)
            {
                if (string.IsNullOrEmpty(entry.Skill)) continue;

                var id = ParseInt(entry.Id) ?? 0;

                // Resolve the translation-lookup key by following:
                //   skills.txt `skilldesc` -> skilldesc.txt row -> `str name` column.
                // This `nameKey` is what we want to emit into KeyedLine.Args so the
                // website can run t(nameKey) against strings/<lang>.json. The
                // English-resolved `Name` is retained as an in-process diagnostic
                // (e.g. for the import-report skill listings) and never reaches
                // the wire.
                string? nameKey = null;
                if (!string.IsNullOrEmpty(entry.SkillDesc) && _data.SkillDescs.TryGetValue(entry.SkillDesc, out var desc))
                {
                    if (!string.IsNullOrEmpty(desc.NameString))
                    {
                        nameKey = desc.NameString;
                    }
                }

                string? localizedName = !string.IsNullOrEmpty(nameKey)
                    ? _data.Translations.GetValue(nameKey)
                    : null;

                if (string.IsNullOrEmpty(localizedName))
                {
                    localizedName = _data.Translations.GetValue(entry.Skill);
                }

                var skill = new SkillEntry
                {
                    Id = id,
                    Skill = entry.Skill,
                    CharClass = entry.CharClass,
                    SkillDesc = entry.SkillDesc,
                    RequiredLevel = int.TryParse(entry.ReqLevel, out var rl) ? rl : 0,
                    Name = localizedName ?? entry.Skill,
                    NameKey = !string.IsNullOrEmpty(nameKey) ? nameKey : entry.Skill
                };
                _data.Skills[entry.Skill] = skill;
                _data.SkillsById[id] = skill;
            }
        }
        catch (Exception ex)
        {
            _pipeline.AddError("Skills", "skills.txt", $"Failed to load: {ex.Message}", ex);
        }
    }

    private async Task LoadCharStatsAsync()
    {
        try
        {
            var entries = await CharStatsParser.GetEntries(Path.Combine(_excelPath, "charstats.txt"));
            foreach (var entry in entries)
            {
                if (string.IsNullOrEmpty(entry.Class)) continue;

                var charStat = new CharStatEntry
                {
                    Class = entry.Class,
                    StrAllSkills = entry.StrAllSkills,
                    StrSkillTab1 = entry.StrSkillTab1,
                    StrSkillTab2 = entry.StrSkillTab2,
                    StrSkillTab3 = entry.StrSkillTab3,
                    StrClassOnly = entry.StrClassOnly,
                };
                _data.CharStats[entry.Class] = charStat;
                // Also index by first 3 characters (lowercase) for MagicAffix ClassSpecific resolution
                if (entry.Class.Length >= 3)
                    _data.CharStatsByCode[entry.Class.Substring(0, 3)] = charStat;
            }
        }
        catch (Exception ex)
        {
            _pipeline.AddError("CharStats", "charstats.txt", $"Failed to load: {ex.Message}", ex);
        }
    }

    private async Task LoadMonStatsAsync()
    {
        try
        {
            var entries = await MonStatsParser.GetEntries(Path.Combine(_excelPath, "monstats.txt"));
            foreach (var entry in entries)
            {
                if (string.IsNullOrEmpty(entry.Id)) continue;

                var monEntry = new MonStatEntry
                {
                    Id = entry.Id,
                    HcIdx = entry.HcIdx,
                    NameStr = entry.NameStr,
                    Name = _data.Translations.GetValue(entry.NameStr)
                };
                _data.MonStats[entry.Id] = monEntry;
                _data.MonStatsByIndex[entry.HcIdx] = monEntry;
            }
        }
        catch (Exception ex)
        {
            _pipeline.AddError("MonStats", "monstats.txt", $"Failed to load: {ex.Message}", ex);
        }
    }

    private async Task LoadPropertyGroupsAsync()
    {
        try
        {
            var filePath = Path.Combine(_excelPath, "propertygroups.txt");
            if (!File.Exists(filePath)) return;

            var entries = await PropertyGroupParser.GetEntries(filePath);
            foreach (var entry in entries)
            {
                if (string.IsNullOrEmpty(entry.Code)) continue;

                var subProps = new List<PropertyGroupSubEntry>();
                var props = new string?[] { entry.Prop1, entry.Prop2, entry.Prop3, entry.Prop4, entry.Prop5, entry.Prop6, entry.Prop7, entry.Prop8 };
                var parMins = new string?[] { entry.ParMin1, entry.ParMin2, entry.ParMin3, entry.ParMin4, entry.ParMin5, entry.ParMin6, entry.ParMin7, entry.ParMin8 };
                var parMaxs = new string?[] { entry.ParMax1, entry.ParMax2, entry.ParMax3, entry.ParMax4, entry.ParMax5, entry.ParMax6, entry.ParMax7, entry.ParMax8 };
                var modMins = new int?[] { entry.ModMin1, entry.ModMin2, entry.ModMin3, entry.ModMin4, entry.ModMin5, entry.ModMin6, entry.ModMin7, entry.ModMin8 };
                var modMaxs = new int?[] { entry.ModMax1, entry.ModMax2, entry.ModMax3, entry.ModMax4, entry.ModMax5, entry.ModMax6, entry.ModMax7, entry.ModMax8 };
                var chances = new int?[] { entry.Chance1, entry.Chance2, entry.Chance3, entry.Chance4, entry.Chance5, entry.Chance6, entry.Chance7, entry.Chance8 };

                for (int i = 0; i < 8; i++)
                {
                    if (string.IsNullOrWhiteSpace(props[i])) continue;

                    // Build parameter: use parMin, or "parMin-parMax" if both differ
                    string? parameter = parMins[i];
                    if (!string.IsNullOrEmpty(parMins[i]) && !string.IsNullOrEmpty(parMaxs[i]) && parMins[i] != parMaxs[i])
                        parameter = parMins[i] + "-" + parMaxs[i];
                    else if (string.IsNullOrEmpty(parMins[i]))
                        parameter = parMaxs[i];

                    subProps.Add(new PropertyGroupSubEntry
                    {
                        Property = props[i]!,
                        Parameter = parameter,
                        Min = modMins[i],
                        Max = modMaxs[i],
                        Chance = chances[i],
                    });
                }

                var pgEntry = new PropertyGroupEntry
                {
                    Code = entry.Code,
                    Id = entry.Id ?? 0,
                    PickMode = entry.PickMode,
                    SubProperties = subProps,
                };
                _data.PropertyGroups[entry.Code] = pgEntry;
                _data.PropertyGroupsById[pgEntry.Id] = pgEntry;
            }
        }
        catch (Exception ex)
        {
            _pipeline.AddError("PropertyGroups", "propertygroups.txt", $"Failed to load: {ex.Message}", ex);
        }
    }

    private async Task LoadArmorsAsync()
    {
        try
        {
            var entries = await ArmorParser.GetEntries(Path.Combine(_excelPath, "armor.txt"));
            foreach (var entry in entries)
            {
                if (string.IsNullOrEmpty(entry.Code)) continue;

                _data.Armors[entry.Code] = new EquipmentEntry
                {
                    Code = entry.Code,
                    NameStr = entry.NameStr ?? "",
                    EquipmentType = EquipmentType.Armor,
                    Type = entry.Type,
                    Type2 = entry.Type2,
                    MinAC = entry.MinAC,
                    MaxAC = entry.MaxAC,
                    Block = entry.Block,
                    MinDamage = entry.MinDam,
                    MaxDamage = entry.MaxDam,
                    ReqStr = entry.ReqStr,
                    ReqDex = entry.ReqDex,
                    Durability = entry.Durability,
                    Speed = entry.Speed,
                    MaxSockets = entry.GemSockets,
                    Level = entry.Level,
                    LevelReq = entry.LevelReq,
                    NormCode = entry.NormCode,
                    UberCode = entry.UberCode,
                    UltraCode = entry.UltraCode,
                    AutoPrefix = entry.AutoPrefix,
                    Name = _data.Translations.GetValue(entry.NameStr)
                };
            }
        }
        catch (Exception ex)
        {
            _pipeline.AddError("Armor", "armor.txt", $"Failed to load: {ex.Message}", ex);
        }
    }

    private async Task LoadWeaponsAsync()
    {
        try
        {
            var entries = await WeaponParser.GetEntries(Path.Combine(_excelPath, "weapons.txt"));
            foreach (var entry in entries)
            {
                if (string.IsNullOrEmpty(entry.Code)) continue;

                _data.Weapons[entry.Code] = new EquipmentEntry
                {
                    Code = entry.Code,
                    NameStr = entry.NameStr ?? "",
                    EquipmentType = EquipmentType.Weapon,
                    Type = entry.Type,
                    Type2 = entry.Type2,
                    MinDamage = entry.MinDam,
                    MaxDamage = entry.MaxDam,
                    TwoHandMinDamage = entry.TwoHandMinDam,
                    TwoHandMaxDamage = entry.TwoHandMaxDam,
                    MissileMinDamage = entry.MinMisDam,
                    MissileMaxDamage = entry.MaxMisDam,
                    StrBonus = entry.StrBonus,
                    DexBonus = entry.DexBonus,
                    TwoHandedWClass = entry.TwoHandedWClass,
                    ReqStr = entry.ReqStr,
                    ReqDex = entry.ReqDex,
                    Durability = entry.Durability,
                    NoDurability = entry.NoDurability,
                    Speed = entry.Speed,
                    MaxSockets = entry.GemSockets,
                    Level = entry.Level,
                    LevelReq = entry.LevelReq,
                    NormCode = entry.NormCode,
                    UberCode = entry.UberCode,
                    UltraCode = entry.UltraCode,
                    AutoPrefix = entry.AutoPrefix,
                    Name = _data.Translations.GetValue(entry.NameStr)
                };
            }
        }
        catch (Exception ex)
        {
            _pipeline.AddError("Weapons", "weapons.txt", $"Failed to load: {ex.Message}", ex);
        }
    }

    private async Task LoadMiscAsync()
    {
        try
        {
            var entries = await MiscParser.GetEntries(Path.Combine(_excelPath, "misc.txt"));
            foreach (var entry in entries)
            {
                if (string.IsNullOrEmpty(entry.Code)) continue;

                _data.MiscItems[entry.Code] = new MiscEntry
                {
                    Code = entry.Code,
                    NameStr = entry.NameStr ?? "",
                    Type = entry.Type,
                    Type2 = entry.Type2,
                    Level = entry.Level ?? 0,
                    LevelReq = entry.LevelReq ?? 0,
                    Name = _data.Translations.GetValue(entry.NameStr)
                };
            }
        }
        catch (Exception ex)
        {
            _pipeline.AddError("Misc", "misc.txt", $"Failed to load: {ex.Message}", ex);
        }
    }

    private async Task LoadGemsAsync()
    {
        try
        {
            var entries = await GemParser.GetEntries(Path.Combine(_excelPath, "gems.txt"));
            foreach (var entry in entries)
            {
                if (string.IsNullOrEmpty(entry.Name) || string.IsNullOrEmpty(entry.Code)) continue;

                _data.Gems[entry.Code] = new GemEntry
                {
                    Name = entry.Name,
                    Code = entry.Code,
                    Letter = entry.Letter,
                    WeaponProperties = MapNumberedProps(entry, "Weapon"),
                    HelmProperties = MapNumberedProps(entry, "Helm"),
                    ShieldProperties = MapNumberedProps(entry, "Shield"),
                };
            }
        }
        catch (Exception ex)
        {
            _pipeline.AddError("Gems", "gems.txt", $"Failed to load: {ex.Message}", ex);
        }
    }

    private async Task LoadMagicAffixesAsync()
    {
        try
        {
            var prefixes = await MagicPrefixParser.GetEntries(Path.Combine(_excelPath, "magicprefix.txt"));
            foreach (var entry in prefixes)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;
                _data.MagicPrefixes.Add(MapMagicAffix(entry));
            }

            var suffixes = await MagicSuffixParser.GetEntries(Path.Combine(_excelPath, "magicsuffix.txt"));
            foreach (var entry in suffixes)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;
                _data.MagicSuffixes.Add(MapMagicAffix(entry));
            }

            var autoMagics = await AutoMagicParser.GetEntries(Path.Combine(_excelPath, "automagic.txt"));
            foreach (var entry in autoMagics)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;
                _data.AutoMagics.Add(MapMagicAffix(entry));
            }
        }
        catch (Exception ex)
        {
            _pipeline.AddError("MagicAffixes", "magicprefix/suffix/automagic.txt", $"Failed to load: {ex.Message}", ex);
        }
    }

    private MagicAffixEntry MapMagicAffix(dynamic entry)
    {
        bool isClassSpecific = false;
        if (entry.ClassSpecific is bool b) isClassSpecific = b;
        else if (entry.ClassSpecific is string s) isClassSpecific = s == "1" || s.Equals("Y", StringComparison.OrdinalIgnoreCase);

        string rawClass = entry.Class ?? "";

        var affix = new MagicAffixEntry
        {
            Name = entry.Name,
            Level = entry.Level ?? 0,
            MaxLevel = entry.MaxLevel ?? 0,
            RequiredLevel = entry.LevelReq ?? 0,
            Group = entry.Group ?? 0,
            ClassSpecific = isClassSpecific ? rawClass : "",
            Class = rawClass,
            ClassLevelReq = entry.ClassLevelReq ?? 0,
            Properties = MapNumberedProps(entry, "")
        };

        for (int i = 1; i <= 7; i++)
        {
            var itype = ImportReflection.GetString(entry, $"IType{i}");
            if (!string.IsNullOrEmpty(itype)) affix.Types.Add(itype);
        }

        for (int i = 1; i <= 5; i++)
        {
            var etype = ImportReflection.GetString(entry, $"EType{i}");
            if (!string.IsNullOrEmpty(etype)) affix.ETypes.Add(etype);
        }

        return affix;
    }

    private async Task LoadSetsAsync()
    {
        try
        {
            var entries = await SetsParser.GetEntries(Path.Combine(_excelPath, "sets.txt"));
            foreach (var entry in entries)
            {
                if (string.IsNullOrEmpty(entry.Index)) continue;
                _data.SetEntries[entry.Index] = entry;
            }
        }
        catch (Exception ex)
        {
            _pipeline.AddError("Sets", "sets.txt", $"Failed to load: {ex.Message}", ex);
        }
    }

    private async Task LoadSetItemsAsync()
    {
        try
        {
            var entries = await SetItemParser.GetEntries(Path.Combine(_excelPath, "setitems.txt"));
            foreach (var entry in entries)
            {
                if (string.IsNullOrEmpty(entry.Index)) continue;
                _data.SetItemEntries[entry.Index] = entry;
            }
        }
        catch (Exception ex)
        {
            _pipeline.AddError("SetItems", "setitems.txt", $"Failed to load: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Applies stat overrides and fixes from stat-overrides.json after all file data is loaded.
    /// </summary>
    private void ApplyStatOverrides()
    {
        foreach (var ov in _data.StatOverrideConfig.StatOverrides)
        {
            var priority = ov.DescPriority;
            if (!priority.HasValue && !string.IsNullOrEmpty(ov.DescPriorityFromStat))
            {
                if (_data.Stats.TryGetValue(ov.DescPriorityFromStat, out var refStat))
                    priority = refStat.DescriptionPriority;
            }

            // Inherit descstrpos/neg from a referenced ItemStatCost stat when requested
            // (e.g. `ac` inherits `armorclass.descstrpos` = ModStr1i, mod-overridable).
            var posKey = ov.DescStrPosKey;
            if (string.IsNullOrEmpty(posKey) && !string.IsNullOrEmpty(ov.DescStrPosKeyFromStat)
                && _data.Stats.TryGetValue(ov.DescStrPosKeyFromStat, out var refPos))
                posKey = refPos.DescStrPosKey;

            var negKey = ov.DescStrNegKey;
            if (string.IsNullOrEmpty(negKey) && !string.IsNullOrEmpty(ov.DescStrNegKeyFromStat)
                && _data.Stats.TryGetValue(ov.DescStrNegKeyFromStat, out var refNeg))
                negKey = refNeg.DescStrNegKey;

            _data.Stats[ov.Stat] = new StatEntry
            {
                Stat = ov.Stat,
                DescriptionPriority = priority,
                DescriptionFunction = ov.DescFunc,
                DescriptionValue = ov.DescVal,
                DescStrPosKey = posKey,
                DescStrNegKey = negKey,
                DescriptionStringPositive = _data.Translations.GetValue(posKey),
                DescriptionStringNegative = _data.Translations.GetValue(negKey),
                IsSynthetic = true,
            };
        }

        foreach (var fix in _data.StatOverrideConfig.StatFixes)
        {
            if (_data.Stats.TryGetValue(fix.Stat, out var existing))
            {
                if (fix.DescPriority.HasValue) existing.DescriptionPriority = fix.DescPriority;
                if (fix.DescFunc.HasValue) existing.DescriptionFunction = fix.DescFunc;
                if (fix.DescVal.HasValue) existing.DescriptionValue = fix.DescVal;
                if (fix.DescStrPosKey != null) { existing.DescStrPosKey = fix.DescStrPosKey; existing.DescriptionStringPositive = _data.Translations.GetValue(fix.DescStrPosKey); }
                if (fix.DescStrNegKey != null) { existing.DescStrNegKey = fix.DescStrNegKey; existing.DescriptionStringNegative = _data.Translations.GetValue(fix.DescStrNegKey); }
            }
        }
    }

    private string NormalizeItemTypeName(string index)
    {
        var value = index.Trim();
        return _data.ExportConfig.ItemTypeNameNormalizations.TryGetValue(value, out var mapped) ? mapped : value;
    }

    /// <summary>
    /// Reads up to <paramref name="count"/> numbered property slots
    /// (<c>{slotPrefix}Mod{i}Code/Param/Min/Max</c>) off a parser DTO via
    /// reflection. Used by both gem-row and magic-affix-row loaders, which
    /// only differ by the slot prefix (gems pass <c>"Helm"</c>/<c>"Magic"</c>/<c>"Rare"</c>/<c>"Set"</c>;
    /// affix rows pass <c>""</c>).
    /// </summary>
    private static List<ItemPropertyValue> MapNumberedProps(object entry, string slotPrefix, int count = 3)
    {
        var props = new List<ItemPropertyValue>();
        for (var i = 1; i <= count; i++)
        {
            var code = ImportReflection.GetString(entry, $"{slotPrefix}Mod{i}Code");
            if (string.IsNullOrEmpty(code)) continue;
            props.Add(new ItemPropertyValue
            {
                Code      = code,
                Parameter = ImportReflection.GetString(entry, $"{slotPrefix}Mod{i}Param"),
                Min       = ImportReflection.GetInt(entry,    $"{slotPrefix}Mod{i}Min"),
                Max       = ImportReflection.GetInt(entry,    $"{slotPrefix}Mod{i}Max"),
            });
        }
        return props;
    }

    private static int? ParseInt(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return int.TryParse(value, out var result) ? result : null;
    }
}
