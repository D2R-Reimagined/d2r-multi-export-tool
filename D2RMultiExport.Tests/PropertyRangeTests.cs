// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using D2RMultiExport.Lib;
using D2RMultiExport.Lib.Config;
using D2RMultiExport.Lib.ErrorHandling;
using D2RMultiExport.Lib.Exporters;
using D2RMultiExport.Lib.Import;
using D2RMultiExport.Lib.Models;
using D2RMultiExport.Lib.Translation;
using D2RReimaginedTools.TextFileParsers;

namespace D2RMultiExport.Tests;

/// <summary>Range semantics checked through the actual keyed JSON projection and serialization.</summary>
public sealed class PropertyRangeTests
{
    private static GameData NewData()
    {
        var config = new ExportConfig();
        config.PropertyCodeExpansions["dmg-elem"] = ["dmg-fire", "dmg-cold", "dmg-ltng"];
        config.BuildLookups();
        var data = new GameData { ExportConfig = config, StatOverrideConfig = new StatOverrideConfig(), Translations = new TranslationService() };
        AddStat(data, "sock", "item_numsockets", 14, 29, "Socketable");
        AddStat(data, "value", "valueStat", 1, 19, "valueKey");
        AddStat(data, "dmg-min", "mindamage", 5, 1, "minimumKey");
        AddStat(data, "dmg-max", "maxdamage", 6, 1, "maximumKey");
        AddStat(data, "fire-min", "firemindam", 1, 19, "fireMinimumKey");
        AddStat(data, "fire-max", "firemaxdam", 1, 19, "fireMaximumKey");
        AddStat(data, "cold-len", "coldlength", 1, 0, null);
        AddStat(data, "pois-min", "poisonmindam", 1, 19, "poisonMinimumKey");
        AddStat(data, "pois-max", "poisonmaxdam", 1, 19, "poisonMaximumKey");
        AddStat(data, "pois-len", "poisonlength", 1, 0, null);
        AddStat(data, "charged", "item_charged_skill", 19, 24, "chargesKey");
        AddStat(data, "hit-skill", "item_skillonhit", 11, 15, "eventKey");
        AddStat(data, "dmg/lvl", "item_maxdamage_perlevel", 17, 19, "growthKey");
        AddStat(data, "skill-rand", "item_singleskill", 12, 27, "skillKey");
        foreach (var (code, key) in new[] { ("dmg-fire", "strModFireDamageRange"), ("dmg-cold", "strModColdDamageRange"), ("dmg-ltng", "strModLightningDamageRange") })
        {
            data.Stats[code] = new StatEntry { Stat = code, IsSynthetic = true, DescriptionFunction = 31, DescStrPosKey = key };
        }
        data.Stats["dmg-pois"] = new StatEntry { Stat = "dmg-pois", IsSynthetic = true, DescriptionFunction = 30, DescStrPosKey = "strModPoisonDamageRange" };
        return data;
    }

    private static void AddStat(GameData data, string code, string stat, int function, int descriptionFunction, string? key)
    {
        data.Properties[code] = new PropertyEntry { Code = code, Stat1 = stat, Function1 = function };
        data.Stats[stat] = new StatEntry { Stat = stat, DescriptionFunction = descriptionFunction, DescStrPosKey = key, OpParam = 3 };
    }

    private static IEnumerable<JsonElement> Lines(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (value.TryGetProperty("key", out _))
            {
                yield return value;
            }
            foreach (var property in value.EnumerateObject())
            {
                foreach (var line in Lines(property.Value))
                {
                    yield return line;
                }
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in value.EnumerateArray())
            {
                foreach (var line in Lines(child))
                {
                    yield return line;
                }
            }
        }
    }

    private static async Task AssertFinalLineAsync(GameData data, string key, params double[] expected)
    {
        var output = Directory.CreateTempSubdirectory("d2r-range-").FullName;
        try
        {
            var outputs = new ExportOutputs(output);
            await KeyedJsonExporter.ExportAsync(outputs, data, true);
            var matching = new List<JsonElement>();
            foreach (var file in outputs.Files)
            {
                using var json = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(output, file.Path)));
                matching.AddRange(Lines(json.RootElement).Where(l => l.GetProperty("key").GetString() == key).Select(l => l.Clone()));
            }
            Assert.NotEmpty(matching);
            Assert.All(matching, line => Assert.Equal(expected, line.GetProperty("args").EnumerateArray().Select(v => v.GetDouble())));
        }
        finally
        {
            Directory.Delete(output, recursive: true);
        }
    }

    [Theory]
    [InlineData("sock", 1, 4)]
    [InlineData("sock", 0, 2)]
    [InlineData("sock", 2, 2)]
    [InlineData("sock", null, 3)]
    [InlineData("sock", 3, null)]
    [InlineData("value", -20, -5)]
    [InlineData("value", -5, 0)]
    [InlineData("value", 0, 8)]
    [InlineData("value", null, 0)]
    public async Task EveryFamilyPreservesRollsThroughFinalJson(string code, int? min, int? max)
    {
        var data = NewData();
        List<CubePropertyExport> Rows() => PropertyMapper.MapRows(code, "9", min, max, data, 50, 0);
        data.Uniques = [new UniqueExport { Index = "unique", Properties = Rows() }];
        data.Sets = [new SetExport
        {
            Index = "set", PartialBonuses = Rows(), FullBonuses = Rows(),
            SetItems = [new SetItemExport { Index = "setItem", Properties = Rows(), SetBonuses = [Rows()] }]
        }];
        data.Runewords = [new RunewordExport { Index = "runeword", Properties = Rows() }];
        data.CubeRecipes = [new CubeRecipeExport()];
        data.CubeRecipes[0].Outputs.A = new CubeOutputExport { Properties = Rows() };
        var affix = new MagicAffixEntry { Name = "affix", Group = 1, Properties = [new ItemPropertyValue { Code = code, Parameter = "9", Min = min, Max = max }] };
        data.MagicPrefixes.Add(affix);
        data.MagicSuffixes.Add(affix);
        data.AutoMagics.Add(affix);
        data.Weapons["base"] = new EquipmentEntry { Code = "base", NameStr = "base", EquipmentType = EquipmentType.Weapon, AutoPrefix = 1 };
        var expected = new[] { min, max }.Where(v => v.HasValue).Select(v => (double)v!.Value).Distinct().ToArray();
        await AssertFinalLineAsync(data, code == "sock" ? "strSocketedCount" : "valueKey", expected);
    }

    [Fact]
    public async Task ElementalShorthandKeepsDamageComponentsEvenWhenEqual()
    {
        var data = NewData();
        data.Uniques = [new UniqueExport { Properties = PropertyMapper.MapRows("dmg-elem", null, 7, 7, data, 1, 0) }];
        foreach (var key in new[] { "strModFireDamageRange", "strModColdDamageRange", "strModLightningDamageRange" })
        {
            await AssertFinalLineAsync(data, key, 7, 7);
        }
    }

    [Fact]
    public async Task PhysicalDuplicatesSumBeforePairingAndRetainBothRawContributions()
    {
        var data = NewData();
        var rows = new List<CubePropertyExport>();
        rows.AddRange(PropertyMapper.MapRows("dmg-min", null, 5, 10, data, 1, 0));
        rows.AddRange(PropertyMapper.MapRows("dmg-max", null, 20, 30, data, 1, 1));
        rows.AddRange(PropertyMapper.MapRows("DMG-MIN", null, 1, 2, data, 1, 2));
        var cleaned = PropertyCleanup.CleanupDuplicates(rows, data, 1);
        Assert.Equal(20, cleaned.Single(p => p.PropertyCode == "dmg-max").Min);
        Assert.Equal(30, cleaned.Single(p => p.PropertyCode == "dmg-max").Max);
        data.Uniques = [new UniqueExport { Properties = cleaned }];
        await AssertFinalLineAsync(data, "strDamageMergedRange", 6, 12, 20, 30);
    }

    [Fact]
    public async Task ElementalComponentsRetainFourEndpointsAndActivationMetadata()
    {
        var data = NewData();
        var rows = PropertyMapper.MapRows("fire-min", null, 10, 12, data, 1, 0);
        rows.AddRange(PropertyMapper.MapRows("fire-max", null, 15, 25, data, 1, 1));
        foreach (var row in rows)
        {
            row.Suffix = "(Weapon)";
            row.NumberOfItemsRequired = 2;
            row.Chance = 17;
            row.Lines[0].Qualifier = "weapon";
        }
        var cleaned = PropertyCleanup.CleanupDuplicates(rows, data, 1);
        var line = Assert.Single(Assert.Single(cleaned).Lines);
        Assert.Equal("weapon", line.Qualifier); Assert.Equal(2, line.ItemsRequired); Assert.Equal(17, line.Chance);
        data.Uniques = [new UniqueExport { Properties = cleaned }];
        await AssertFinalLineAsync(data, "strFireDamageRolls", 10, 12, 15, 25);
    }

    [Fact]
    public async Task PoisonConversionRunsOnceAndKeepsFractionalDuration()
    {
        var data = NewData();
        var rows = PropertyMapper.MapRows("pois-min", null, 128, 128, data, 1, 0);
        rows.AddRange(PropertyMapper.MapRows("pois-max", null, 256, 256, data, 1, 1));
        rows.AddRange(PropertyMapper.MapRows("pois-len", null, 38, 38, data, 1, 2));
        var cleaned = PropertyCleanup.CleanupDuplicates(rows, data, 1);
        Assert.Equal(128, Assert.Single(cleaned).Min);
        data.Uniques = [new UniqueExport { Properties = cleaned }];
        await AssertFinalLineAsync(data, "strModPoisonDamageRange", 19, 38, 1.52);
    }

    [Fact]
    public async Task DurabilityEndpointsSurviveConsumptionIntoEquipment()
    {
        var data = NewData();
        var equipment = new ExportEquipment { EquipmentType = 1, Durability = 250 };
        var source = new EquipmentEntry { Code = "base", NameStr = "base", EquipmentType = EquipmentType.Weapon };
        var rows = PropertyMapper.MapRows("dur", null, 20, 40, data, 1, 0);
        DamageArmorCalculator.Apply(equipment, source, rows, 1, data);
        data.Uniques = [new UniqueExport { Equipment = equipment, Properties = [] }];
        await AssertFinalLineAsync(data, "strDurabilityRolls", 270, 290, 270, 290);
    }

    [Fact]
    public async Task PairedElementalRollsAlsoSurviveEquipmentAggregation()
    {
        var data = NewData();
        data.ExportConfig.ElementalDamageCodes = ["dmg-fire"];
        data.ExportConfig.BuildLookups();
        var rows = PropertyMapper.MapRows("fire-min", null, 10, 12, data, 1, 0);
        rows.AddRange(PropertyMapper.MapRows("fire-max", null, 15, 25, data, 1, 1));
        rows = PropertyCleanup.CleanupDuplicates(rows, data, 1);
        var equipment = new ExportEquipment { EquipmentType = 1 };
        DamageArmorCalculator.Apply(equipment, new EquipmentEntry { Code = "base", NameStr = "base", EquipmentType = EquipmentType.Weapon }, rows, 1, data);
        Assert.Equal(15.5, Assert.Single(equipment.DamageTypes!).AverageDamage);
        data.Uniques = [new UniqueExport { Equipment = equipment, Properties = rows }];
        await AssertFinalLineAsync(data, "strElementalDamageRange", 10, 12, 15, 25);
    }

    [Theory]
    [InlineData(null, 3)]
    [InlineData(3, null)]
    public void FixedSkillSingleEndpointDoesNotInventAZeroBonus(int? min, int? max)
    {
        var data = NewData();
        AddStat(data, "skill", "item_singleskill", 22, 27, "skillKey");
        var line = Assert.Single(PropertyMapper.Map("skill", "42", min, max, data, 1).Lines);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(line));
        Assert.Equal(3, json.RootElement.GetProperty("args")[0].GetInt32());
        Assert.Equal(3, json.RootElement.GetProperty("args")[1].GetInt32());
    }

    [Theory]
    [InlineData("3", null, null, 0.375)]
    [InlineData(null, 8, 12, 1)]
    public void PerLevelPrecisionAndFallbackEndpointsSurvive(string? param, int? min, int? max, double first)
    {
        var line = Assert.Single(PropertyMapper.Map("dmg/lvl", param, min, max, NewData(), 1).Lines);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(line));
        Assert.Equal(first, json.RootElement.GetProperty("args")[0].GetDouble());
        if (max.HasValue)
        {
            Assert.Equal(1.5, json.RootElement.GetProperty("args")[1].GetDouble());
        }
    }

    [Fact]
    public void IndependentSkillFieldsAreNotSummedAsRollsAndSelectionIdsAreNotBonusRanges()
    {
        var data = NewData();
        var rows = PropertyMapper.MapRows("charged", "42", 20, 3, data, 40, 0);
        rows.AddRange(PropertyMapper.MapRows("charged", "42", 30, 5, data, 40, 1));
        Assert.Equal(2, PropertyCleanup.CleanupDuplicates(rows, data, 40).Count);
        var random = Assert.Single(PropertyMapper.Map("skill-rand", "2", 36, 65, data, 1).Lines);
        Assert.Equal([2, "Sorceress"], random.Args);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(random));
        Assert.Equal(36, json.RootElement.GetProperty("selection").GetProperty("min").GetInt32());
        Assert.Equal(65, json.RootElement.GetProperty("selection").GetProperty("max").GetInt32());
    }

    [Fact]
    public async Task AutomagicDeduplicationKeepsDifferentGroupedRangesAndSelectionBounds()
    {
        var data = NewData();
        foreach (var (code, max) in new[] { ("groupA", 4), ("groupB", 8) })
        {
            data.PropertyGroups[code] = new PropertyGroupEntry
            {
                Code = code,
                PickMode = 2,
                SubProperties = [new PropertyGroupSubEntry { Property = "value", Min = 0, Max = max }]
            };
            data.AutoMagics.Add(new MagicAffixEntry { Name = "bucket", Group = 1, Properties = [new ItemPropertyValue { Code = code }] });
        }
        foreach (var (min, max) in new[] { (36, 65), (40, 50) })
        {
            data.AutoMagics.Add(new MagicAffixEntry
            {
                Name = "bucket",
                Group = 1,
                Properties = [new ItemPropertyValue { Code = "skill-rand", Parameter = "2", Min = min, Max = max }]
            });
        }
        data.Weapons["base"] = new EquipmentEntry { Code = "base", NameStr = "base", EquipmentType = EquipmentType.Weapon, AutoPrefix = 1 };
        var output = Directory.CreateTempSubdirectory("d2r-bucket-").FullName;
        try
        {
            await KeyedJsonExporter.ExportAsync(new ExportOutputs(output), data, true);
            using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(output, "keyed", "weapons.json")));
            var allLines = Lines(document.RootElement).ToList();
            Assert.Equal(2, allLines.Count(l => l.TryGetProperty("children", out _)));
            Assert.Equal(2, allLines.Count(l => l.TryGetProperty("selection", out _)));
            Assert.Contains(allLines, l => l.GetProperty("key").GetString() == "valueKey" && l.GetProperty("args")[1].GetInt32() == 8);
        }
        finally
        {
            Directory.Delete(output, recursive: true);
        }
    }

    [Fact]
    public void GroupedTabAliasPreservesModifierAndIdentifierRanges()
    {
        var data = NewData();
        AddStat(data, "skilltab", "item_addskill_tab", 10, 14, "tabKey");
        data.ExportConfig.CustomSkillTabAliases["tabGroup"] = "war";
        data.CharStatsByCode["war"] = new CharStatEntry { Class = "Warlock" };
        var group = new PropertyGroupEntry
        {
            Code = "tabGroup",
            SubProperties =
            [new PropertyGroupSubEntry { Property = "skilltab", Parameter = "56-58", Min = 1, Max = 3 }]
        };
        var parent = Assert.Single(Assert.Single(PropertyGroupExpander.Expand(group, 0, 1, data)).Lines);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(parent));
        var child = json.RootElement.GetProperty("children")[0];
        Assert.Equal([1, 3], child.GetProperty("args").EnumerateArray().Select(v => v.GetInt32()));
        Assert.Equal(56, child.GetProperty("selection").GetProperty("min").GetInt32());
        Assert.Equal(58, child.GetProperty("selection").GetProperty("max").GetInt32());
    }

    [Theory]
    [InlineData(-20, null)]
    [InlineData(null, -20)]
    public void SingleRequirementEndpointDoesNotCreateASecondRoll(int? min, int? max)
    {
        var equipment = new ExportEquipment { RequiredStrength = "100" };
        RequirementHelper.ApplyEaseAdjustment(equipment, [new CubePropertyExport { IsEase = true, Min = min, Max = max }]);
        Assert.Equal("80", equipment.RequiredStrength);
    }

    [Fact]
    public void ResolverAndGroupFailuresCannotBecomeEmptySuccessfulMappings()
    {
        var data = NewData();
        data.Stats["valueStat"].DescriptionFunction = 23;
        // Deliberately invalid registry state fails inside the resolver, after
        // priority lookup. This used to be swallowed into an empty line list.
        data.MonStats["broken"] = null!;
        Assert.Throws<NullReferenceException>(() => PropertyMapper.Map("value", "broken", 1, 3, data, 1));
        var group = new PropertyGroupEntry
        {
            Code = "failureGroup",
            SubProperties = [new PropertyGroupSubEntry { Property = "value", Parameter = "broken", Min = 1, Max = 3 }]
        };
        var exception = Assert.Throws<InvalidOperationException>(() => PropertyGroupExpander.Expand(group, 0, 1, data));
        Assert.Contains("propertygroups.txt 'failureGroup', property 'value'", exception.Message);
        Assert.IsType<NullReferenceException>(exception.InnerException);
    }

    [Fact]
    public async Task RealSourceSnapshotsAreParsedImportedAndSerializedWithTheirRanges()
    {
        var data = NewData();
        AddStat(data, "dur", "maxdurability", 1, 1, "strDurabilityBonus");
        var sources = SourceRoots.Create(Path.Combine(AppContext.BaseDirectory, "Fixtures", "PropertyRanges"), null);
        foreach (var unique in await UniqueItemsParser.GetEntries(sources.Excel("uniqueitems.txt")))
        {
            data.Weapons[unique.Code!] = new EquipmentEntry
            {
                Code = unique.Code!,
                NameStr = unique.Code!,
                EquipmentType = EquipmentType.Weapon,
                Durability = 250
            };
        }
        var uniques = await new UniqueImporter(sources, data).ImportAsync();
        Assert.Empty(uniques.Errors);
        data.Uniques = uniques.Items;
        foreach (var set in await SetsParser.GetEntries(sources.Excel("sets.txt")))
        {
            data.SetEntries[set.Index!] = set;
        }
        foreach (var item in await SetItemParser.GetEntries(sources.Excel("setitems.txt")))
        {
            data.SetItemEntries[item.Index!] = item;
        }
        var sets = await new SetImporter(sources, data).ImportAsync();
        Assert.Empty(sets.Errors);
        data.Sets = sets.Items;
        foreach (var word in await RunesParser.GetEntries(sources.Excel("runes.txt")))
        {
            foreach (var rune in new[] { word.Rune1, word.Rune2, word.Rune3, word.Rune4, word.Rune5, word.Rune6 }.OfType<string>())
            {
                data.MiscItems[rune] = new MiscEntry { Code = rune, NameStr = rune, Level = 50, LevelReq = 50 };
            }
        }
        var runewords = await new RunewordImporter(sources, data).ImportAsync();
        Assert.DoesNotContain(runewords.Errors, e => e.Severity == ImportErrorSeverity.Error);
        data.Runewords = runewords.Items;
        var cube = await new CubeRecipeImporter(sources, data) { EarlyStopSentinelEnabled = false }.ImportAsync();
        Assert.Empty(cube.Errors);
        data.CubeRecipes = cube.Items;
        foreach (var row in await MagicPrefixParser.GetEntries(sources.Excel("magicprefix.txt")))
        {
            if (row.Spawnable != true) continue;
            data.MagicPrefixes.Add(new MagicAffixEntry
            {
                Name = row.Name!,
                Level = row.Level ?? 0,
                Properties = [new ItemPropertyValue { Code = row.Mod1Code, Parameter = row.Mod1Param, Min = row.Mod1Min, Max = row.Mod1Max }]
            });
        }
        foreach (var row in await MagicSuffixParser.GetEntries(sources.Excel("magicsuffix.txt")))
        {
            if (row.Spawnable != true) continue;
            data.MagicSuffixes.Add(new MagicAffixEntry
            {
                Name = row.Name!,
                Properties =
            [
                new ItemPropertyValue { Code = row.Mod1Code, Parameter = row.Mod1Param, Min = row.Mod1Min, Max = row.Mod1Max },
                new ItemPropertyValue { Code = row.Mod2Code, Parameter = row.Mod2Param, Min = row.Mod2Min, Max = row.Mod2Max },
                new ItemPropertyValue { Code = row.Mod3Code, Parameter = row.Mod3Param, Min = row.Mod3Min, Max = row.Mod3Max }
            ]
            });
        }
        AddStat(data, "hp", "maxhp", 1, 19, "lifeKey");
        foreach (var row in await AutoMagicParser.GetEntries(sources.Excel("automagic.txt")))
        {
            data.AutoMagics.Add(new MagicAffixEntry
            {
                Name = row.Name!,
                Group = row.Group,
                Properties =
                [new ItemPropertyValue { Code = row.Mod1Code, Parameter = row.Mod1Param, Min = row.Mod1Min, Max = row.Mod1Max }]
            });
        }
        data.Weapons["arrows"] = new EquipmentEntry { Code = "arrows", NameStr = "arrows", EquipmentType = EquipmentType.Weapon, AutoPrefix = 303 };
        var output = Directory.CreateTempSubdirectory("d2r-source-ranges-").FullName;
        try
        {
            await KeyedJsonExporter.ExportAsync(new ExportOutputs(output), data, true);
            async Task<JsonElement> Read(string file)
            {
                using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(output, "keyed", file)));
                return document.RootElement.Clone();
            }
            void Check(JsonElement record, string key, params double[] expected) =>
                Assert.Contains(Lines(record), l => l.GetProperty("key").GetString() == key
                    && l.GetProperty("args").EnumerateArray().All(v => v.ValueKind == JsonValueKind.Number)
                    && l.GetProperty("args").EnumerateArray().Select(v => v.GetDouble()).SequenceEqual(expected));
            var uniqueJson = await Read("uniques.json");
            Check(uniqueJson.EnumerateArray().Single(r => r.GetProperty("Index").GetString() == "Hone Sundan"), "strSocketedCount", 3, 6);
            Check(uniqueJson.EnumerateArray().Single(r => r.GetProperty("Index").GetString() == "Griswold's Edge"), "strFireDamageRolls", 10, 12, 15, 25);
            Check(uniqueJson.EnumerateArray().Single(r => r.GetProperty("Index").GetString() == "Steelgoad"), "strDurabilityRolls", 270, 290, 270, 290);
            Check(await Read("sets.json"), "strSocketedCount", 1, 2);
            Check(await Read("sets.json"), "strModFireDamageRange", 15, 20);
            Check(await Read("runewords.json"), "strDamageMergedRange", 40, 50, 80, 100);
            Check(await Read("cube-recipes.json"), "strSocketedCount", 1, 6);
            Check(await Read("magicprefix.json"), "strSocketedCount", 1, 2);
            Check(await Read("magicsuffix.json"), "StrSkill13", 1);
            Check(await Read("weapons.json"), "lifeKey", 1, 5);
        }
        finally
        {
            Directory.Delete(output, recursive: true);
        }
    }

    [Fact]
    public void ValueTemplatesWithoutASourceValueAreOmittedAndReported()
    {
        var data = NewData();
        data.TemplateLookup = key => key switch
        {
            "valueKey" => "%+d to Value",
            "flagKey" => "Cannot Be Frozen",
            "escapedKey" => "100%% Flag",
            _ => null
        };
        AddStat(data, "flag", "flagStat", 1, 1, "flagKey");
        AddStat(data, "escaped", "escapedStat", 1, 1, "escapedKey");

        // A parameter is not a function-1 value; the website would print the raw "%+d".
        Assert.Empty(PropertyMapper.Map("value", "25", null, null, data, 1).Lines);
        var omission = Assert.Single(PropertyMapper.TakeOmittedLines(data));
        Assert.Contains("Property 'value' (param '25', min '', max '')", omission);
        Assert.Contains("'valueKey'", omission);
        Assert.Empty(data.OmittedPropertyLines);

        // One endpoint is a value; value-less and escaped-percent templates need none.
        Assert.Equal(new object[] { 5 }, Assert.Single(PropertyMapper.Map("value", null, 5, null, data, 1).Lines).Args);
        Assert.Single(PropertyMapper.Map("flag", null, null, null, data, 1).Lines);
        Assert.Single(PropertyMapper.Map("escaped", null, null, null, data, 1).Lines);
        Assert.Empty(data.OmittedPropertyLines);
    }

    [Fact]
    public async Task AnUnrenderablePropertyIsOmittedFromItsRowWithoutDroppingTheItem()
    {
        var data = NewData();
        data.TemplateLookup = key => key == "valueKey" ? "%+d to Value" : null;
        var root = Directory.CreateTempSubdirectory("d2r-unresolved-").FullName;
        try
        {
            var excel = Directory.CreateDirectory(Path.Combine(root, "global", "excel")).FullName;
            Directory.CreateDirectory(Path.Combine(root, "local", "lng", "strings"));
            var fixture = await File.ReadAllLinesAsync(Path.Combine(AppContext.BaseDirectory,
                "Fixtures", "PropertyRanges", "data", "global", "excel", "uniqueitems.txt"));
            var header = fixture[0].Split('\t');
            var row = fixture.Single(static l => l.StartsWith("Hone Sundan\t", StringComparison.Ordinal)).Split('\t');
            row[Array.IndexOf(header, "prop3")] = "value";
            row[Array.IndexOf(header, "par3")] = "25";
            row[Array.IndexOf(header, "min3")] = "";
            row[Array.IndexOf(header, "max3")] = "";
            await File.WriteAllLinesAsync(Path.Combine(excel, "uniqueitems.txt"), [fixture[0], string.Join('\t', row)]);
            data.Weapons["9st"] = new EquipmentEntry { Code = "9st", NameStr = "9st", EquipmentType = EquipmentType.Weapon, Durability = 250 };

            var uniques = await new UniqueImporter(SourceRoots.Create(root, null), data).ImportAsync();
            var warning = Assert.Single(uniques.Errors);
            Assert.Equal(ImportErrorSeverity.Warning, warning.Severity);
            Assert.Equal("Hone Sundan", warning.ItemIdentifier);
            Assert.Contains("'valueKey'", warning.Message);
            Assert.Empty(data.OmittedPropertyLines);

            data.Uniques = uniques.Items;
            var output = Directory.CreateDirectory(Path.Combine(root, "out")).FullName;
            await KeyedJsonExporter.ExportAsync(new ExportOutputs(output), data, true);
            using var json = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(output, "keyed", "uniques.json")));
            var item = Assert.Single(json.RootElement.EnumerateArray());
            var keys = Lines(item).Select(static l => l.GetProperty("key").GetString()).ToList();
            Assert.Contains("strSocketedCount", keys);
            Assert.DoesNotContain("valueKey", keys);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
