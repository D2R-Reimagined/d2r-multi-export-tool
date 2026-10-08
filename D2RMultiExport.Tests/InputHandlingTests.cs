// SPDX-License-Identifier: GPL-3.0-or-later
using D2RMultiExport.Lib;
using D2RMultiExport.Lib.Config;
using D2RMultiExport.Lib.Exporters;
using D2RMultiExport.Lib.Import;
using D2RMultiExport.Lib.Models;
using D2RMultiExport.Lib.Translation;

namespace D2RMultiExport.Tests;

/// <summary>Regression coverage for optional inputs, duplicate rows, config lookups and condition checks.</summary>
public sealed class InputHandlingTests
{
    private static GameData NewData(ExportConfig? config = null) => new()
    {
        ExportConfig = config ?? new ExportConfig(),
        StatOverrideConfig = new StatOverrideConfig(),
        Translations = new TranslationService()
    };

    [Fact]
    public void MissingOptionalInputsAreReportedNotThrown()
    {
        var directory = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var excel = Directory.CreateDirectory(Path.Combine(directory, "global", "excel")).FullName;
            Directory.CreateDirectory(Path.Combine(directory, "local", "lng", "strings"));
            File.WriteAllText(Path.Combine(excel, "present.txt"), "");
            var sources = SourceRoots.Create(directory, null);
            var warnings = new List<string>();

            Assert.False(ExcelInputs.RequireAll(sources, "keyed/x.json", warnings.Add, "present.txt", "absent.txt"));
            var warning = Assert.Single(warnings);
            Assert.Contains("absent.txt", warning);
            Assert.DoesNotContain("present.txt", warning);
            Assert.Contains(Path.Combine(excel, "absent.txt"), warning);
            Assert.True(ExcelInputs.RequireAll(sources, "keyed/x.json", warnings.Add, "present.txt"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void DuplicateCodesKeepFirstRowAndAreReported()
    {
        var warnings = new List<string>();
        var rows = new[] { ("axe", 1), ("AXE", 2), ("", 3), ("bow", 4) };

        var index = ExcelInputs.IndexFirst(rows, static r => r.Item1, "itemtypes.txt", "Code", warnings.Add);

        Assert.Equal(2, index.Count);
        Assert.Equal(1, index["Axe"].Item2);
        var warning = Assert.Single(warnings);
        Assert.Contains("itemtypes.txt", warning);
        Assert.Contains("AXE", warning);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("cond('Desecrated', 1) ? 1 : 0")]
    [InlineData("cond('Difficulty', hell)")]
    [InlineData("(1 + 2) * 3 >= 9")]
    [InlineData("1=1")]
    [InlineData("1==1")]
    [InlineData("1!=2")]
    [InlineData("1 ? 10 : 1/0")]
    [InlineData("0 ? 1/0 : 10")]
    // The shared grammar closes terminal unmatched '(' (CalcExpression.BalanceTerminalParentheses),
    // so drop conditions get exactly the leniency skill cells get — no second dialect.
    [InlineData("(1 + 2")]
    public void WellFormedDropConditionsValidate(string? expression)
    {
        Assert.True(DropConditionValidator.TryValidate(expression, NewData(), out var reason), reason);
    }

    [Theory]
    [InlineData("cond('Desecratd', 1)", "not a condition")]
    [InlineData("stat('nosuchstat'.accr) > 0", "nosuchstat")]
    [InlineData("1 + 2)", null)]
    [InlineData("1 +", null)]
    [InlineData("lvl > 3", "lvl")]
    [InlineData("1 ? 10 : stat('nosuchstat'.accr)", "nosuchstat")]
    [InlineData("0 ? cond('Desecratd', 1) : 10", "not a condition")]
    [InlineData("1 ? 10 : (2 + )", null)]
    [InlineData("0 ? (2 + ) : 10", null)]
    public void MalformedDropConditionsReportTheirCause(string expression, string? expectedFragment)
    {
        Assert.False(DropConditionValidator.TryValidate(expression, NewData(), out var reason));
        Assert.False(string.IsNullOrWhiteSpace(reason));
        if (expectedFragment is not null) Assert.Contains(expectedFragment, reason);
    }

    [Theory]
    [InlineData("cond('Difficulty', hell)=1", new int[] { 2 })]
    [InlineData("cond('Difficulty', hell)==1", new int[] { 2 })]
    [InlineData("cond('Difficulty', hell)!=1", new int[] { 0, 1 })]
    [InlineData("cond('Difficulty', hell) ? 10 : 0", new int[] { 2 })]
    [InlineData("1 ? 10 : 1/0", new int[] { 0, 1, 2 })]
    [InlineData("0 ? 1/0 : 10", new int[] { 0, 1, 2 })]
    [InlineData("1 ? 10 : cond('Desecrated')", null)]
    [InlineData("0 ? stat('nosuchstat'.accr) : 10", null)]
    public void DropDifficultyResolutionUsesComparisonsAndChecksBothBranches(string expression, int[]? expected)
    {
        var success = DropConditionValidator.TryResolveDifficulties(expression, NewData(), out var difficulties);
        Assert.Equal(expected is not null, success);
        Assert.Equal(expected ?? [], difficulties);
    }

    [Fact]
    public async Task BundledConfigCarriesTheRelocatedConstants()
    {
        var config = await ExportConfig.LoadAsync(Path.Combine(AppContext.BaseDirectory, "Config", "export-config.json"));

        Assert.Equal(3, config.DropGeneratedTreasureClassLevelStep);
        Assert.Equal(1, config.DropItemRatioVersion);
        Assert.Equal(2, config.DropMonsterLevelBonuses["CHAMPION"]);
        Assert.Equal(3, config.DropMonsterLevelBonuses["superunique"]);
        Assert.Equal("h2hitype", config.ItemTypeIndexAliases["H2H2"]);
        Assert.Equal(8, config.SkillIconSheets.Count);
        Assert.Equal("soskillicon.sprite", config.SkillIconSheets["SOR"].File);
        Assert.Equal(["dmg-fire", "dmg-cold", "dmg-ltng"], config.PropertyCodeExpansions["DMG-ELEM"]);
        Assert.True(config.DamageArmorEnhancedCodesSet.SetEquals(["ac%", "dmg%"]));
        Assert.Contains("EASE", config.RequirementPercentCodesSet);
        Assert.Equal(6, config.ItemStatCompositeKeys.Count);
        Assert.Equal(100, config.AttackSpeed.CurveLevels);
        Assert.Equal((1, 4, 7), (config.MonsterHealth.MinionBonusModifierId,
            config.MonsterHealth.ChampionBonusModifierId, config.MonsterHealth.UniqueBonusModifierId));
        Assert.Contains("RUNE", config.DropRuneTypesSet);
        // Case-insensitive lookups survive JSON deserialization (they did not before BuildLookups rebuilt them).
        Assert.True(config.DropMonsterAreas.ContainsKey(config.DropMonsterAreas.Keys.First().ToUpperInvariant()));
    }
}
