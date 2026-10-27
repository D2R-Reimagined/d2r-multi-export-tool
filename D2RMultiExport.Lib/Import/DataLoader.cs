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
    private readonly SourceRoots _sources;
    private readonly GameData _data;
    private readonly PipelineResult _pipeline;

    public DataLoader(SourceRoots sources, GameData data, PipelineResult pipeline)
    {
        _sources = sources;
        _data = data;
        _pipeline = pipeline;
    }

    public async Task LoadAllAsync()
    {
        await LoadCalcCodeTablesAsync();
        await LoadItemStatCostsAsync();
        await LoadPropertiesAsync();
        await LoadItemTypesAsync();
        await LoadSkillDescsAsync();
        await LoadSkillsAsync();
        await LoadMissilesAsync();
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

    /// <summary>
    /// Reads the <c>skillcalc.txt</c> / <c>misscalc.txt</c> symbol vocabularies, preferring
    /// the mod's own copies and falling back to the canonical ones bundled under
    /// <c>Config/CASC_DATA/</c>. A missing table is a warning, not a failure: the
    /// calculators still resolve every quantity they define in code, they just lose the
    /// table-driven column aliases and progression formulas.
    /// </summary>
    private async Task LoadCalcCodeTablesAsync()
    {
        _data.SkillCalcCodes = await LoadCalcCodeTableAsync("skillcalc.txt");
        _data.MissileCalcCodes = await LoadCalcCodeTableAsync("misscalc.txt");
    }

    private async Task<CalcCodeTable> LoadCalcCodeTableAsync(string fileName)
    {
        var source = _sources.ResolveReferenceTable(fileName);
        var path = source.Path;
        if (string.IsNullOrEmpty(path))
        {
            _pipeline.AddWarning("CalcCodes", fileName,
                $"Reference table not found (searched {source.DescribeSearch()}); calc symbols defined only by that table will not resolve.");
            return CalcCodeTable.Empty;
        }

        try
        {
            return await CalcCodeTable.LoadAsync(path, _data.ExportConfig.CalcFormulaOverrides);
        }
        catch (Exception ex)
        {
            _pipeline.AddWarning("CalcCodes", fileName, $"Failed to load: {ex.Message}");
            return CalcCodeTable.Empty;
        }
    }

    /// <summary>
    /// Loads <c>missiles.txt</c>, which backs the <c>miss('name'.field)</c> references and
    /// the <c>m1en</c>/<c>m2ex</c>/<c>m3rn</c> families in skill descriptions. Optional:
    /// a mod tree without the file simply leaves those lines unresolved.
    /// </summary>
    private async Task LoadMissilesAsync()
    {
        try
        {
            var path = _sources.Excel("missiles.txt");
            if (!File.Exists(path))
            {
                _pipeline.AddWarning("Missiles", "missiles.txt",
                    "File not found; skill descriptions that reference a missile will be dropped.");
                return;
            }

            var entries = await MissilesParser.GetEntries(path);
            // A weapon's numeric missiletype is the engine record index, never *ID or the
            // name. Name lookups for skill descriptions survive an unalignable file.
            int?[] indices = [];
            try
            {
                indices = await EngineRowIndex.DeriveAsync(path, [.. entries.Select(static entry => entry.MissileName)]);
            }
            catch (InvalidDataException ex)
            {
                _pipeline.AddWarning("Missiles", "missiles.txt",
                    $"{ex.Message} Weapon missiletype references will not resolve, so missile potions export no damage.");
            }
            var rows = new RowTracker<string, D2RReimaginedTools.Models.Missiles>("Missiles", _data.Missiles.Comparer,
                static missile => $"Missile='{missile.MissileName}'", identityIncludesKey: true);
            var row = 0;
            foreach (var entry in entries)
            {
                row++;
                if (indices.Length > 0 && indices[row - 1] is { } index)
                {
                    _data.MissilesByIndex[index] = entry;
                }
                if (string.IsNullOrEmpty(entry.MissileName)) continue;
                Put(_data.Missiles, entry.MissileName, entry, "missiles.txt", "Missile", rows, row);
            }
        }
        catch (Exception ex)
        {
            _pipeline.AddError("Missiles", "missiles.txt", $"Failed to load: {ex.Message}", ex);
        }
    }

    private async Task LoadItemStatCostsAsync()
    {
        try
        {
            var entries = await ItemStatCostParser.GetEntries(_sources.Excel("itemstatcost.txt"));
            var rows = new RowTracker<string, StatEntry>("Stats", _data.Stats.Comparer,
                static stat => $"Stat='{stat.Stat}'", identityIncludesKey: true);
            var row = 0;
            foreach (var entry in entries)
            {
                row++;
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
                    GroupDescStrPosKey = entry.DgrpStrPos,
                    GroupDescStrNegKey = entry.DgrpStrNeg,
                    GroupDescStr2Key = entry.DgrpStr2,
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
                Put(_data.Stats, entry.Stat, stat, "itemstatcost.txt", "Stat", rows, row);
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
            var entries = await PropertiesParser.GetEntries(_sources.Excel("properties.txt"));
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
                    Slots = ReadPropertySlots(entry),
                };
            }
        }
        catch (Exception ex)
        {
            _pipeline.AddError("Properties", "properties.txt", $"Failed to load: {ex.Message}", ex);
        }
    }

    // properties.txt repeats the func/stat/set/val group seven times; the typed row DTO
    // flattens them into numbered properties. The engine applies slots in order and stops
    // at the first empty function, so no later column is read after a gap.
    private static List<PropertySlot> ReadPropertySlots(D2RReimaginedTools.Models.Property entry)
    {
        List<PropertySlot> slots = [];
        foreach (var (function, stat, set, value) in new[]
                 {
                     (entry.Func1, entry.Stat1, entry.Set1, entry.Val1),
                     (entry.Func2, entry.Stat2, entry.Set2, entry.Val2),
                     (entry.Func3, entry.Stat3, entry.Set3, entry.Val3),
                     (entry.Func4, entry.Stat4, entry.Set4, entry.Val4),
                     (entry.Func5, entry.Stat5, entry.Set5, entry.Val5),
                     (entry.Func6, entry.Stat6, entry.Set6, entry.Val6),
                     (entry.Func7, entry.Stat7, entry.Set7, entry.Val7)
                 })
        {
            if (function is not > 0) break;
            slots.Add(new PropertySlot
            {
                Function = function.Value,
                Stat = string.IsNullOrWhiteSpace(stat) ? null : stat.Trim(),
                Set = set,
                Value = ParseInt(value)
            });
        }
        return slots;
    }

    private async Task LoadItemTypesAsync()
    {
        try
        {
            var entries = await ItemTypeParser.GetEntries(_sources.Excel("itemtypes.txt"));
            var rows = new RowTracker<string, ItemTypeEntry>("ItemTypes", _data.ItemTypes.Comparer,
                static itemType => $"Code='{itemType.Code}'", identityIncludesKey: true);
            var row = 0;
            foreach (var entry in entries)
            {
                row++;
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
                    // export-config.json itemTypeIndexAliases redirects a duplicate bucket
                    // (e.g. h2h2 → h2hitype) onto its canonical key so exports never carry
                    // a separate identifier or translation key for it.
                    Index = _data.ExportConfig.ItemTypeIndexAliases.TryGetValue(code, out var alias) ? alias : code + "itype",
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
                Put(_data.ItemTypes, code, itemType, "itemtypes.txt", "Code", rows, row);

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
            var entries = await SkillDescParser.GetEntries(_sources.Excel("skilldesc.txt"));
            foreach (var entry in entries)
            {
                if (string.IsNullOrEmpty(entry.SkillName)) continue;

                _data.SkillDescs[entry.SkillName] = new SkillDescEntry
                {
                    SkillDesc = entry.SkillName,
                    NameString = entry.NameString,
                    ShortString = entry.ShortString,
                    LongString = entry.LongString,
                    Page = entry.SkillPage ?? 0,
                    Row = entry.SkillRow ?? 0,
                    Column = entry.SkillColumn ?? 0,
                    IconCel = entry.IconCel,
                    DescriptionLines = ReadDescriptionLines(entry),
                    DetailLines = ReadDetailLines(entry),
                    SynergyLines = ReadSynergyLines(entry),
                    DescriptionMissile1 = entry.DescriptionMissile1,
                    DescriptionMissile2 = entry.DescriptionMissile2,
                    DescriptionMissile3 = entry.DescriptionMissile3
                };
            }
        }
        catch (Exception ex)
        {
            _pipeline.AddError("SkillDesc", "skilldesc.txt", $"Failed to load: {ex.Message}", ex);
        }
    }

    // skilldesc.txt repeats the same five-column group (function, textA, textB, calcA,
    // calcB) across three differently-sized blocks. The typed row DTO flattens them into
    // numbered properties, so each block is read by hand rather than by index.

    private static List<SkillDescLine> ReadDescriptionLines(D2RReimaginedTools.Models.SkillDesc entry) =>
    [
        .. new[]
        {
            MakeDescLine(entry.DescriptionLine1, entry.DescriptionTextA1, entry.DescriptionTextB1, entry.DescriptionCalculationA1, entry.DescriptionCalculationB1),
            MakeDescLine(entry.DescriptionLine2, entry.DescriptionTextA2, entry.DescriptionTextB2, entry.DescriptionCalculationA2, entry.DescriptionCalculationB2),
            MakeDescLine(entry.DescriptionLine3, entry.DescriptionTextA3, entry.DescriptionTextB3, entry.DescriptionCalculationA3, entry.DescriptionCalculationB3),
            MakeDescLine(entry.DescriptionLine4, entry.DescriptionTextA4, entry.DescriptionTextB4, entry.DescriptionCalculationA4, entry.DescriptionCalculationB4),
            MakeDescLine(entry.DescriptionLine5, entry.DescriptionTextA5, entry.DescriptionTextB5, entry.DescriptionCalculationA5, entry.DescriptionCalculationB5),
            MakeDescLine(entry.DescriptionLine6, entry.DescriptionTextA6, entry.DescriptionTextB6, entry.DescriptionCalculationA6, entry.DescriptionCalculationB6)
        }.OfType<SkillDescLine>()
    ];

    private static List<SkillDescLine> ReadDetailLines(D2RReimaginedTools.Models.SkillDesc entry) =>
    [
        .. new[]
        {
            MakeDescLine(entry.Dsc2Line1, entry.Dsc2TextA1, entry.Dsc2TextB1, entry.Dsc2CalculationA1, entry.Dsc2CalculationB1),
            MakeDescLine(entry.Dsc2Line2, entry.Dsc2TextA2, entry.Dsc2TextB2, entry.Dsc2CalculationA2, entry.Dsc2CalculationB2),
            MakeDescLine(entry.Dsc2Line3, entry.Dsc2TextA3, entry.Dsc2TextB3, entry.Dsc2CalculationA3, entry.Dsc2CalculationB3),
            MakeDescLine(entry.Dsc2Line4, entry.Dsc2TextA4, entry.Dsc2TextB4, entry.Dsc2CalculationA4, entry.Dsc2CalculationB4),
            MakeDescLine(entry.Dsc2Line5, entry.Dsc2TextA5, entry.Dsc2TextB5, entry.Dsc2CalculationA5, entry.Dsc2CalculationB5)
        }.OfType<SkillDescLine>()
    ];

    private static List<SkillDescLine> ReadSynergyLines(D2RReimaginedTools.Models.SkillDesc entry) =>
    [
        .. new[]
        {
            MakeDescLine(entry.Dsc3Line1, entry.Dsc3TextA1, entry.Dsc3TextB1, entry.Dsc3CalculationA1, entry.Dsc3CalculationB1),
            MakeDescLine(entry.Dsc3Line2, entry.Dsc3TextA2, entry.Dsc3TextB2, entry.Dsc3CalculationA2, entry.Dsc3CalculationB2),
            MakeDescLine(entry.Dsc3Line3, entry.Dsc3TextA3, entry.Dsc3TextB3, entry.Dsc3CalculationA3, entry.Dsc3CalculationB3),
            MakeDescLine(entry.Dsc3Line4, entry.Dsc3TextA4, entry.Dsc3TextB4, entry.Dsc3CalculationA4, entry.Dsc3CalculationB4),
            MakeDescLine(entry.Dsc3Line5, entry.Dsc3TextA5, entry.Dsc3TextB5, entry.Dsc3CalculationA5, entry.Dsc3CalculationB5),
            MakeDescLine(entry.Dsc3Line6, entry.Dsc3TextA6, entry.Dsc3TextB6, entry.Dsc3CalculationA6, entry.Dsc3CalculationB6),
            MakeDescLine(entry.Dsc3Line7, entry.Dsc3TextA7, entry.Dsc3TextB7, entry.Dsc3CalculationA7, entry.Dsc3CalculationB7)
        }.OfType<SkillDescLine>()
    ];

    private static SkillDescLine? MakeDescLine(string? function, string? textA, string? textB, string? calcA, string? calcB)
    {
        if (!int.TryParse(function, out var parsed) || parsed == 0) return null;
        if (string.IsNullOrWhiteSpace(textA)) return null;

        return new SkillDescLine
        {
            Function = parsed,
            TextA = textA.Trim(),
            TextB = string.IsNullOrWhiteSpace(textB) ? null : textB.Trim(),
            CalcA = string.IsNullOrWhiteSpace(calcA) ? null : calcA.Trim(),
            CalcB = string.IsNullOrWhiteSpace(calcB) ? null : calcB.Trim()
        };
    }

    private async Task LoadSkillsAsync()
    {
        try
        {
            var path = _sources.Excel("skills.txt");
            var entries = await SkillsParser.GetEntries(path);
            // The skill id is the engine record index, never the informational *Id column:
            // appended rows routinely leave *Id blank, and every blank cell used to read as
            // 0 and replace Attack. A row without a skill name still consumes its index.
            var ids = await EngineRowIndex.DeriveAsync(path, [.. entries.Select(static entry => entry.Skill)]);
            var rowsBySkill = new RowTracker<string, SkillEntry>("Skills", _data.Skills.Comparer,
                static skill => $"skill='{skill.Skill}'", identityIncludesKey: true);
            List<(string Row, string? Informational, int Engine)> informational = [];
            var row = 0;
            foreach (var entry in entries)
            {
                row++;
                if (ids[row - 1] is not { } id) continue;
                informational.Add(($"skill '{entry.Skill}'", entry.Id, id));
                if (string.IsNullOrEmpty(entry.Skill)) continue;

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

                // A declared `str name` that doesn't resolve is genuine translator work,
                // so that probe stays on GetValue (it records the miss). The fallback
                // probe is on the skills.txt code, which is an identifier and never a
                // key — a miss there is expected, so it must not be recorded.
                string? localizedName = !string.IsNullOrEmpty(nameKey)
                    ? _data.Translations.GetValue(nameKey)
                    : null;

                if (string.IsNullOrEmpty(localizedName) || localizedName == nameKey)
                {
                    localizedName = _data.Translations.TryGetValue(entry.Skill, out var byCode)
                        ? byCode
                        : entry.Skill;
                }

                var skill = new SkillEntry
                {
                    Id = id,
                    Skill = entry.Skill,
                    CharClass = entry.CharClass,
                    SkillDesc = entry.SkillDesc,
                    RequiredLevel = int.TryParse(entry.ReqLevel, out var rl) ? rl : 0,
                    MaxLevel = int.TryParse(entry.MaxLvl, out var maxLevel) ? maxLevel : 0,
                    Prerequisites = new[] { entry.ReqSkill1, entry.ReqSkill2, entry.ReqSkill3 }
                        .Where(static prerequisite => !string.IsNullOrWhiteSpace(prerequisite))
                        .Select(static prerequisite => prerequisite!)
                        .ToList(),
                    Name = localizedName ?? entry.Skill,
                    // Deliberately left null when no `str name` exists: see SkillEntry.NameKey.
                    NameKey = !string.IsNullOrEmpty(nameKey) ? nameKey : null,
                    SourceRow = entry
                };
                Put(_data.Skills, entry.Skill, skill, "skills.txt", "skill", rowsBySkill, row);
                _data.SkillsById[id] = skill;
            }
            ReportInformationalIndexDrift("skills.txt", "*Id", informational);
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
            var entries = await CharStatsParser.GetEntries(_sources.Excel("charstats.txt"));
            var rows = new RowTracker<string, CharStatEntry>("CharStats", _data.CharStats.Comparer,
                static charStat => $"class='{charStat.Class}'", identityIncludesKey: true);
            var row = 0;
            foreach (var entry in entries)
            {
                row++;
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
                Put(_data.CharStats, entry.Class, charStat, "charstats.txt", "class", rows, row);
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
            var path = _sources.Excel("monstats.txt");
            var entries = await MonStatsParser.GetEntries(path);
            // The monster class index (what a numeric reanimate parameter names) is the
            // engine record index: the shipped monstats.bin holds one record per line after
            // the header except the Expansion section header. The informational *hcIdx
            // column is never read. Physical line N (1-based, header on line 1) is index
            // N - 2 above the Expansion line and N - 3 below it.
            List<string?> ids = [.. entries.Select(static entry => entry.Id)];
            var indices = await EngineRowIndex.DeriveAsync(path, ids);
            var rowsById = new RowTracker<string, MonStatEntry>("MonStats", _data.MonStats.Comparer,
                static monster => $"Id='{monster.Id}', engine index {monster.Index}"
                    + (string.IsNullOrEmpty(monster.NameStr) ? "" : $", NameStr='{monster.NameStr}'")
                    + (string.IsNullOrEmpty(monster.Name) || monster.Name == monster.NameStr ? "" : $" (\"{monster.Name}\")"),
                identityIncludesKey: true);
            List<(string Row, string? Informational, int Engine)> informational = [];
            var row = 0;
            foreach (var entry in entries)
            {
                row++;
                if (indices[row - 1] is not { } index) continue;
                // The typed *hcIdx reads a blank cell as 0, so 0 is treated as unmaintained.
                informational.Add(($"Id '{entry.Id}'", entry.HcIdx == 0 ? null : entry.HcIdx.ToString(), index));
                if (string.IsNullOrEmpty(entry.Id)) continue;

                var monEntry = new MonStatEntry
                {
                    Id = entry.Id,
                    Index = index,
                    NameStr = entry.NameStr,
                    Name = _data.Translations.GetValue(entry.NameStr)
                };
                Put(_data.MonStats, entry.Id, monEntry, "monstats.txt", "Id", rowsById, row);
                _data.MonStatsByIndex[index] = monEntry;
            }
            ReportInformationalIndexDrift("monstats.txt", "*hcIdx", informational);
            if (EngineRowIndex.FindNameNumberDivergence(ids, indices) is { } divergence)
            {
                _pipeline.AddWarning("DataLoader", "monstats.txt",
                    $"Id '{divergence.Name}' at engine index {divergence.Index} repeats the Id numbered "
                    + $"{divergence.NameNumber} (case-insensitive; blank Ids and blank lines share one name). "
                    + "The game numbers distinct Ids, not rows, so from here on its name references (BaseId, "
                    + "NextInClass, spawn, minion, by-name reanimate) no longer reach the row carrying the name. "
                    + "Numeric indices are unaffected; this export resolves a name to the row that carries it.");
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
            var filePath = _sources.Excel("propertygroups.txt");
            if (!File.Exists(filePath)) return;

            var entries = await PropertyGroupParser.GetEntries(filePath);
            // The group id is the engine record index, not the informational *Id column.
            var ids = await EngineRowIndex.DeriveAsync(filePath, [.. entries.Select(static entry => entry.Code)]);
            List<(string Row, string? Informational, int Engine)> informational = [];
            for (var row = 0; row < entries.Count; row++)
            {
                var entry = entries[row];
                if (ids[row] is not { } id) continue;
                informational.Add(($"code '{entry.Code}'", entry.Id?.ToString(), id));
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
                    Id = id,
                    PickMode = entry.PickMode,
                    SubProperties = subProps,
                };
                _data.PropertyGroups[entry.Code] = pgEntry;
                _data.PropertyGroupsById[pgEntry.Id] = pgEntry;
            }
            ReportInformationalIndexDrift("propertygroups.txt", "*Id", informational);
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
            var entries = await ArmorParser.GetEntries(_sources.Excel("armor.txt"));
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
                    InventoryWidth = entry.InvWidth ?? 1,
                    InventoryHeight = entry.InvHeight ?? 1,
                    InventoryFile = entry.InvFile,
                    UniqueInventoryFile = entry.UniqueInvFile,
                    SetInventoryFile = entry.SetInvFile,
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
            var entries = await WeaponParser.GetEntries(_sources.Excel("weapons.txt"));
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
                    MissileType = entry.MissileType,
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
                    InventoryWidth = entry.InvWidth ?? 1,
                    InventoryHeight = entry.InvHeight ?? 1,
                    InventoryFile = entry.InvFile,
                    UniqueInventoryFile = entry.UniqueInvFile,
                    SetInventoryFile = entry.SetInvFile,
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
            var entries = await MiscParser.GetEntries(_sources.Excel("misc.txt"));
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
                    InventoryWidth = entry.InvWidth ?? 1,
                    InventoryHeight = entry.InvHeight ?? 1,
                    InventoryFile = entry.InvFile,
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
            var entries = await GemParser.GetEntries(_sources.Excel("gems.txt"));
            var row = 0;
            foreach (var entry in entries)
            {
                row++;
                if (string.IsNullOrEmpty(entry.Name) || string.IsNullOrEmpty(entry.Code)) continue;

                _data.Gems[entry.Code] = new GemEntry
                {
                    Name = entry.Name,
                    Code = entry.Code,
                    Letter = entry.Letter,
                    WeaponProperties = MapNumberedProps(entry, "Weapon", "gems.txt", entry.Code, row),
                    HelmProperties = MapNumberedProps(entry, "Helm", "gems.txt", entry.Code, row),
                    ShieldProperties = MapNumberedProps(entry, "Shield", "gems.txt", entry.Code, row),
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
            var prefixes = await MagicPrefixParser.GetEntries(_sources.Excel("magicprefix.txt"));
            var row = 0;
            foreach (var entry in prefixes)
            {
                row++;
                if (string.IsNullOrEmpty(entry.Name)) continue;
                // Skip rows whose `spawnable` column != 1 — these affixes are
                // disabled in the source `.txt` and never roll in-game.
                if (entry.Spawnable != true) continue;
                _data.MagicPrefixes.Add(MapMagicAffix(entry, "magicprefix.txt", row));
            }

            var suffixes = await MagicSuffixParser.GetEntries(_sources.Excel("magicsuffix.txt"));
            row = 0;
            foreach (var entry in suffixes)
            {
                row++;
                if (string.IsNullOrEmpty(entry.Name)) continue;
                // Skip rows whose `spawnable` column != 1 — these affixes are
                // disabled in the source `.txt` and never roll in-game.
                if (entry.Spawnable != true) continue;
                _data.MagicSuffixes.Add(MapMagicAffix(entry, "magicsuffix.txt", row));
            }

            var autoMagics = await AutoMagicParser.GetEntries(_sources.Excel("automagic.txt"));
            row = 0;
            foreach (var entry in autoMagics)
            {
                row++;
                if (string.IsNullOrEmpty(entry.Name)) continue;
                _data.AutoMagics.Add(MapMagicAffix(entry, "automagic.txt", row));
            }
        }
        catch (Exception ex)
        {
            _pipeline.AddError("MagicAffixes", "magicprefix/suffix/automagic.txt", $"Failed to load: {ex.Message}", ex);
        }
    }

    private MagicAffixEntry MapMagicAffix(dynamic entry, string file, int parsedRow)
    {
        // Statically typed handle for the reflection probes, so CallerMemberName binds.
        object row = entry;
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
            Properties = MapNumberedProps(row, "", file, (string?)entry.Name, parsedRow)
        };

        for (int i = 1; i <= 7; i++)
        {
            var itype = ImportReflection.GetString(row, $"IType{i}");
            if (!string.IsNullOrEmpty(itype)) affix.Types.Add(itype);
        }

        for (int i = 1; i <= 5; i++)
        {
            var etype = ImportReflection.GetString(row, $"EType{i}");
            if (!string.IsNullOrEmpty(etype)) affix.ETypes.Add(etype);
        }

        return affix;
    }

    private async Task LoadSetsAsync()
    {
        try
        {
            var entries = await SetsParser.GetEntries(_sources.Excel("sets.txt"));
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
            var entries = await SetItemParser.GetEntries(_sources.Excel("setitems.txt"));
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
    /// affix rows pass <c>""</c>). A malformed Min/Max cell is reported against
    /// <paramref name="file"/>/<paramref name="rowId"/>, and each raw slot is checked by
    /// <see cref="PropertySourceValidator"/> against its source column
    /// (<c>weaponMod1Code</c>, <c>mod1code</c>, …) at <paramref name="parsedRow"/>.
    /// </summary>
    private List<ItemPropertyValue> MapNumberedProps(object entry, string slotPrefix, string file, string? rowId, int parsedRow, int count = 3)
    {
        void Warn(string message) => _pipeline.AddWarning("DataLoader", file, $"Row '{rowId}': {message}");
        var props = new List<ItemPropertyValue>();
        for (var i = 1; i <= count; i++)
        {
            var code = ImportReflection.GetString(entry, $"{slotPrefix}Mod{i}Code");
            if (string.IsNullOrEmpty(code)) continue;
            var prop = new ItemPropertyValue
            {
                Code      = code,
                Parameter = ImportReflection.GetString(entry, $"{slotPrefix}Mod{i}Param"),
                Min       = ImportReflection.GetInt(entry,    $"{slotPrefix}Mod{i}Min", Warn),
                Max       = ImportReflection.GetInt(entry,    $"{slotPrefix}Mod{i}Max", Warn),
            };
            // Source headers: gems.txt "weaponMod1Code"/"weaponMod1Param"/…, affix tables "mod1code"/"mod1param"/….
            var column = slotPrefix.Length == 0 ? $"mod{i}" : $"{slotPrefix.ToLowerInvariant()}Mod{i}";
            var (codeSuffix, paramSuffix, minSuffix, maxSuffix) = slotPrefix.Length == 0
                ? ("code", "param", "min", "max")
                : ("Code", "Param", "Min", "Max");
            PropertySourceValidator.Report(_pipeline, rowId ?? "", file, parsedRow,
                new SourcePropertySlot(column + codeSuffix, column + paramSuffix, column + minSuffix, column + maxSuffix,
                    prop.Code, prop.Parameter, prop.Min, prop.Max),
                _data);
            props.Add(prop);
        }
        return props;
    }

    /// <summary>
    /// Stores <paramref name="value"/> under its primary key. A repeated key keeps the
    /// historical last-row-wins result (so exports are unchanged) but is reported with the
    /// file, column and key, plus the identity and parsed row of both the overwritten and
    /// the replacing row (see <see cref="RowTracker{TKey,TValue}"/>).
    /// </summary>
    private void Put<TKey, TValue>(Dictionary<TKey, TValue> lookup, TKey key, TValue value, string file, string column,
        RowTracker<TKey, TValue> rows, int parsedRow)
        where TKey : notnull
    {
        if (lookup.TryGetValue(key, out var original))
            _pipeline.AddWarning("DataLoader", file, rows.DescribeDuplicate(column, key, original, value, parsedRow));
        lookup[key] = value;
        rows.Record(key, parsedRow);
    }

    /// <summary>
    /// Reports, once per table, informational <c>*</c> index cells that disagree with the engine
    /// record index the loader used instead (see <see cref="EngineRowIndex"/>). Blank or
    /// non-numeric cells are unmaintained metadata and are not reported.
    /// </summary>
    private void ReportInformationalIndexDrift(string file, string column,
        IReadOnlyList<(string Row, string? Informational, int Engine)> rows)
    {
        var drift = rows.Where(static row => ParseInt(row.Informational) is { } value && value != row.Engine).ToList();
        if (drift.Count == 0) return;
        var examples = string.Join("; ", drift.Take(3).Select(row =>
            $"{row.Row}: {column}='{row.Informational!.Trim()}', engine index {row.Engine}"));
        _pipeline.AddWarning("DataLoader", file,
            $"{drift.Count} row(s) carry an informational {column} that differs from the engine row index "
            + $"({examples}{(drift.Count > 3 ? "; …" : "")}). {column} is ignored; identifiers come from the row position.");
    }

    /// <summary>
    /// Duplicate-key diagnostics for one <see cref="Put{TKey,TValue}"/> lookup: remembers the
    /// parsed row that supplied each stored key and renders a duplicate as the original and
    /// replacement rows. A parsed row is the 1-based position among the data rows the parser
    /// returned (header excluded, blank lines not counted), not a physical line number.
    /// <paramref name="describe"/> names a row (including its key cell when
    /// <paramref name="identityIncludesKey"/>).
    /// </summary>
    private sealed class RowTracker<TKey, TValue>(
        string lookupName,
        IEqualityComparer<TKey> comparer,
        Func<TValue, string> describe,
        bool identityIncludesKey = false)
        where TKey : notnull
    {
        private readonly Dictionary<TKey, int> _rows = new(comparer);

        public void Record(TKey key, int parsedRow) => _rows[key] = parsedRow;

        public string DescribeDuplicate(string column, TKey key, TValue original, TValue replacement, int parsedRow)
        {
            var originalRow = _rows.TryGetValue(key, out var stored) ? stored.ToString() : "unknown";
            var keyText = key.ToString() ?? "";
            return $"Duplicate {column} '{keyText}':"
                + $"\n    Original:    {Describe(original, column, keyText)}, parsed row {originalRow}"
                + $"\n    Replacement: {Describe(replacement, column, keyText)}, parsed row {parsedRow}"
                + $"\n    Action: the replacement overwrote the original entry in {lookupName}.";
        }

        private string Describe(TValue value, string column, string keyText) =>
            identityIncludesKey ? describe(value) : $"{describe(value)}, {column}='{keyText}'";
    }

    private static int? ParseInt(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return int.TryParse(value, out var result) ? result : null;
    }
}
