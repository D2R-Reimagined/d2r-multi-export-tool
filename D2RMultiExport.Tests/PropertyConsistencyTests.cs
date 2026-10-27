// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using D2RMultiExport.Lib;
using D2RMultiExport.Lib.Config;
using D2RMultiExport.Lib.Import;
using D2RMultiExport.Lib.Models;
using D2RMultiExport.Lib.Translation;
using D2RMultiExport.TestSupport;

namespace D2RMultiExport.Tests;

/// <summary>
/// One interpretation of a property code, whatever row it comes from. Unit tests cover the
/// shared expansion; the opt-in end-to-end test (<c>D2R_MOD_DATA</c>/<c>D2R_CASC_DATA</c>)
/// compares the keyed lines that uniques, sets, runewords and cube recipes actually ship.
/// </summary>
public sealed class PropertyConsistencyTests
{
    private static GameData NewData()
    {
        var config = new ExportConfig();
        config.PropertyCodeExpansions["dmg-elem"] = ["dmg-fire", "dmg-cold", "dmg-ltng"];
        config.BuildLookups();
        return new GameData
        {
            ExportConfig = config,
            StatOverrideConfig = new StatOverrideConfig(),
            Translations = new TranslationService()
        };
    }

    [Fact]
    public void ShorthandCodesExpandIdenticallyAndKeepTheirColumnValues()
    {
        var data = NewData();
        var rows = PropertyMapper.MapRows("DMG-ELEM", "5", 10, 20, data, 50, 3);

        Assert.Equal(["dmg-fire", "dmg-cold", "dmg-ltng"], rows.Select(static r => r.PropertyCode));
        Assert.All(rows, row =>
        {
            Assert.Equal(3, row.Index);
            Assert.Equal("5", row.Parameter);
            Assert.Equal(10, row.Min);
            Assert.Equal(20, row.Max);
        });
        Assert.Equal(rows.Select(static r => r.PropertyCode),
            PropertyMapper.MapExpanded("dmg-elem", "5", 10, 20, data, 50).Select(static p => p.PropertyCode));
    }

    [Fact]
    public void UnexpandedCodesMapToThemselves()
    {
        var data = NewData();
        var row = Assert.Single(PropertyMapper.MapRows("dmg-fire", null, 1, 2, data, 1, 0));
        Assert.Equal("dmg-fire", row.PropertyCode);
        Assert.Equal(["dmg-fire"], PropertyMapper.ExpandCode("dmg-fire", data.ExportConfig));
    }

    [Fact]
    public void RequirementPercentFlagIsSetTheSameWayForEverySource()
    {
        var data = NewData();
        data.ExportConfig.RequirementPercentCodes = ["ease"];
        data.ExportConfig.BuildLookups();
        Assert.True(Assert.Single(PropertyMapper.MapRows("ease", null, -20, -20, data, 1, 0)).IsEase);
    }

    private static IEnumerable<JsonElement> Lines(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("key", out var key) && key.ValueKind == JsonValueKind.String) yield return element;
            foreach (var p in element.EnumerateObject())
            foreach (var l in Lines(p.Value)) yield return l;
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            foreach (var l in Lines(item)) yield return l;
        }
    }

    [RealDataFact]
    [Trait("Category", "RealData")]
    public async Task EveryExportShipsTheSameElementalDamageLinesForTheSameColumn()
    {
        var sources = RealDataInputs.RequireSources();
        var sharedOutput = RealDataInputs.SharedExport;
        if (sharedOutput is not null && (File.Exists(sharedOutput) || Directory.Exists(sharedOutput)))
        {
            throw new InvalidOperationException("D2R_VALIDATION_EXPORT must be a new path for this validation session.");
        }

        var output = sharedOutput ?? Directory.CreateTempSubdirectory("d2r-consistency-").FullName;
        try
        {
            var pipeline = new D2RMultiExportPipeline(sources.ModDataRoot, sources.CascDataRoot, output)
            {
                EarlyStopSentinelEnabled = false
            };
            await pipeline.RunAsync();
            var data = pipeline.Data!;
            Assert.False(pipeline.Result.HasErrors, pipeline.Result.GenerateReport());

            // No exported document still carries the unexpanded shorthand as a property.
            foreach (var group in new IEnumerable<CubePropertyExport>[]
                     {
                         data.Uniques.SelectMany(static u => u.Properties),
                         data.Sets.SelectMany(static s => s.SetItems).SelectMany(static i => i.Properties),
                         data.Sets.SelectMany(static s => s.SetItems).SelectMany(static i => i.SetBonuses).SelectMany(static b => b),
                         data.Sets.SelectMany(static s => s.PartialBonuses.Concat(s.FullBonuses)),
                         data.Runewords.SelectMany(static r => r.Properties),
                         data.CubeRecipes.SelectMany(static c => new[] { c.Outputs.A, c.Outputs.B, c.Outputs.C })
                             .SelectMany(static o => o?.Properties ?? [])
                     })
            {
                Assert.DoesNotContain(group, static p => string.Equals(p.PropertyCode, "dmg-elem", StringComparison.OrdinalIgnoreCase));
            }

            // The same (param, min, max) column yields identical keyed lines on a unique and a set item.
            var expected = PropertyMapper.MapExpanded("dmg-elem", null, 10, 20, data, 50)
                .SelectMany(static p => p.Lines).Select(static l => JsonSerializer.Serialize(l)).ToList();
            Assert.Equal(3, PropertyMapper.ExpandCode("dmg-elem", data.ExportConfig).Count);
            Assert.NotEmpty(expected);
            Assert.Equal(expected, PropertyMapper.MapRows("dmg-elem", null, 10, 20, data, 50, 0)
                .SelectMany(static p => p.Lines).Select(static l => JsonSerializer.Serialize(l)));

            using var sets = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(output, "keyed", "sets.json")));
            Assert.NotEmpty(Lines(sets.RootElement));

            // Final-audit regressions against the actual mod's typed rows and emitted data.
            foreach (var name in new[] { "Holy Fire", "Holy Freeze", "FireGolem" })
            {
                var descriptions = data.SkillDescriptions[data.Skills[name].Id];
                var key = name switch
                {
                    "Holy Fire" => "StrSkillFireDamageToYourAttack",
                    "Holy Freeze" => "StrSkillColdDamageToYourAttack",
                    _ => "StrSkill5"
                };
                Assert.Equal("elemental", Assert.Single(descriptions.Stats, line => line.Key == key).Scale);
            }
            var titanLine = data.SkillDescriptions[data.Skills["Titans Covenant"].Id].Stats
                .Single(static line => line.Key == "ModStrPlayerDmgPercent");
            var zeroHard = titanLine.Values2D![0][0];
            Assert.Equal(55, zeroHard[Math.Min(100, zeroHard.Length - 1)]);
            var phoenix = data.Runewords.Single(static row => row.Index == "Phoenix");
            Assert.Equal("weapon", Assert.Single(phoenix.Properties.Single(static row => row.PropertyCode == "manasteal").Lines).Qualifier);
            Assert.Equal("shield", Assert.Single(phoenix.Properties.Single(static row => row.PropertyCode == "res-fire-max").Lines).Qualifier);
            Assert.Equal(0, pipeline.ReferencedMissingKeyCount);

        }
        finally
        {
            if (sharedOutput is null)
            {
                Directory.Delete(output, recursive: true);
            }
        }
    }
}
