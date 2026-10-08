// SPDX-License-Identifier: GPL-3.0-or-later
using D2RMultiExport.Lib.Config;
using D2RMultiExport.Lib.Import;
using D2RMultiExport.Lib.Models;
using D2RMultiExport.Lib.Translation;
using Skills = D2RReimaginedTools.Models.Skills;

namespace D2RMultiExport.Tests;

/// <summary>Alias-based scaling discovery, evaluator diagnostics and the effective-level grid ceiling.</summary>
public sealed class SkillRegressionTests
{
    private static async Task<GameData> NewDataAsync()
    {
        var config = new ExportConfig { SkillDescriptionMaxLevel = 100 };
        config.BuildLookups();
        return new GameData
        {
            ExportConfig = config,
            StatOverrideConfig = new StatOverrideConfig(),
            Translations = new TranslationService(),
            SkillCalcCodes = await CalcCodeTable.LoadAsync(Path.Combine(AppContext.BaseDirectory, "Config", "CASC_DATA", "skillcalc.txt"))
        };
    }

    private static void AddSkill(GameData data, string name, Skills row, string calcA, string? calcB = null)
    {
        data.Skills[name] = new SkillEntry
        {
            Id = data.Skills.Count + 1, Skill = name, CharClass = "pal", SkillDesc = name,
            MaxLevel = 25, SourceRow = row
        };
        data.SkillDescs[name] = new SkillDescEntry
        {
            SkillDesc = name,
            DescriptionLines = [new SkillDescLine { Function = 75, TextA = "damage", CalcA = calcA, CalcB = calcB }]
        };
    }

    [Theory]
    [InlineData("1=1", 1)]
    [InlineData("1==1", 1)]
    [InlineData("1!=2", 1)]
    [InlineData("1=2", 0)]
    [InlineData("1==2", 0)]
    [InlineData("1!=1", 0)]
    [InlineData("12=12", 1)]
    [InlineData("1 = 1", 1)]
    [InlineData("1>2", 0)]
    [InlineData("1<2", 1)]
    [InlineData("2>=2", 1)]
    [InlineData("2<=1", 0)]
    [InlineData("1 ? 10 : (1 / 0)", 10)]
    [InlineData("0 ? (1 / 0) : 20", 20)]
    [InlineData("-1 ? 10 : 20", 10)]
    [InlineData("1 ? (0 ? (1 / 0) : 30) : (1 / 0)", 30)]
    [InlineData("0 ? (1 / 0) : 1 ? 40 : (1 / 0)", 40)]
    [InlineData("(0 ? 1 : 2) + 3", 5)]
    [InlineData("0 ? (1 ? 1/0 : 2/0) : 50", 50)]
    [InlineData("1 ? 10 : min(1/0, 2%0)", 10)]
    [InlineData("1 ? 10 : 2^-1", 10)]
    [InlineData("1 ? 10 : rand(1,2)", 10)]
    [InlineData("1 ? 10 : sctk", 10)]
    [InlineData("0 ? skill('Missing'.clc1) : 20", 20)]
    [InlineData("1 ? 10 : cond('Desecrated')", 10)]
    [InlineData("-2^2 + 7/2 + 7%2", 0)]
    public async Task ComparisonsAndSelectedBranchesEvaluateThroughSkillColumns(string expression, long expected)
    {
        var data = await NewDataAsync();
        AddSkill(data, "Expression", new Skills { PassiveCalc1 = expression }, "pst1");
        var context = new SkillCalcContext { Skill = data.Skills["Expression"], Data = data, Levels = new CalcLevels(1, 1) };

        Assert.True(SkillCalculator.TryEvaluate("pst1", context, out var value), context.Trace.Reason);
        Assert.Equal(expected, value);
        var result = await new SkillDescriptionImporter(data).ImportAsync();
        Assert.Equal(expected, Assert.Single(Assert.Single(result.Items).Stats).Values![0][0]);
        Assert.Empty(result.Errors);
    }

    [Theory]
    [InlineData("1 ? 1/0 : 10")]
    [InlineData("0 ? 10 : 1/0")]
    [InlineData("1 ? 10 : (2 + )")]
    [InlineData("0 ? (2 + ) : 10")]
    [InlineData("1 ? 10")]
    [InlineData("1 ? : 10")]
    [InlineData("0 ? 10 :")]
    [InlineData("1 ? 10 : min(1,)")]
    [InlineData("1 ? 10 : mystery(1,2)")]
    [InlineData("0 ? mystery('Missing'.lvl) : 10")]
    [InlineData("1 ? 10 : skill('Missing'.)")]
    [InlineData("1 ? 10 : cond('Difficulty',)")]
    [InlineData("1 ? 10 : skill('Missing.lvl)")]
    [InlineData("1 ? 10 : 0 ? 20")]
    [InlineData("1 ? 10 : 20)")]
    public async Task SelectedFailuresAndSyntaxErrorsInEitherBranchAreRejected(string expression)
    {
        var data = await NewDataAsync();
        var context = new SkillCalcContext
        {
            Skill = new SkillEntry { Id = 1, Skill = "Expression", SourceRow = new Skills() },
            Data = data, Levels = new CalcLevels(1, 1)
        };

        Assert.False(SkillCalculator.TryEvaluate(expression, context, out var value));
        Assert.Equal(0, value);
        Assert.False(string.IsNullOrWhiteSpace(context.Trace.Reason));
    }

    [Theory]
    [InlineData("Holy Fire", 1, 3, false)]
    [InlineData("Holy Freeze", 1, 2, false)]
    [InlineData("Holy Fire", 1, 3, true)]
    [InlineData("Holy Freeze", 1, 2, true)]
    public async Task PassiveDamageAliasesCarryElementalScaling(string name, int min, int max, bool guarded)
    {
        var data = await NewDataAsync();
        AddSkill(data, name, new Skills
        {
            EMin = min, EMax = max, HitShift = 8, Param5 = "4",
            PassiveCalc1 = guarded ? "lvl>1 ? enms*par5/256 : 0" : "enms*par5/256",
            PassiveCalc2 = guarded ? "lvl>1 ? exms*par5/256 : 0" : "exms*par5/256",
            EDmgSymPerCalc = "skill('Synergy'.blvl)*16"
        }, "pst1", "pst2");
        var result = await new SkillDescriptionImporter(data).ImportAsync();
        var line = Assert.Single(Assert.Single(result.Items).Stats);
        Assert.Equal("elemental", line.Scale);
        Assert.Null(line.ScaleCalc);
        Assert.Equal([guarded ? 0.0 : min * 4.0, guarded ? 0.0 : max * 4.0],
            line.Values!.Select(static v => v[0]));
        if (guarded)
        {
            Assert.Equal([min * 4.0, max * 4.0], line.Values!.Select(static v => v[1]));
        }
        Assert.Empty(result.Errors);
    }

    [Theory]
    [InlineData("ast3")]
    [InlineData("ps10")]
    [InlineData("clc0")]
    [InlineData("clc1")]
    public async Task AuraIrregularPassiveAndCalcAliasesUseTheReferenceTable(string alias)
    {
        var data = await NewDataAsync();
        AddSkill(data, "Alias skill", new Skills
        {
            EMin = 4, HitShift = 8, AuraStatCalc3 = "enma", PassiveCalc10 = "enma",
            Calc10 = "enma", Calc1 = "pst1", PassiveCalc1 = "enma"
        }, alias);
        var result = await new SkillDescriptionImporter(data).ImportAsync();
        var line = Assert.Single(Assert.Single(result.Items).Stats);
        Assert.Equal("elemental", line.Scale);
        Assert.Equal(4, line.Values![0][0]);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task AliasCyclesAndUnknownSymbolsKeepTheirFailureDiagnostics()
    {
        var data = await NewDataAsync();
        AddSkill(data, "Cycle", new Skills { Calc1 = "pst1", PassiveCalc1 = "clc1" }, "clc1");
        AddSkill(data, "Unknown", new Skills { PassiveCalc1 = "unknownquantity" }, "pst1");
        var result = await new SkillDescriptionImporter(data).ImportAsync();
        Assert.Empty(result.Items);
        Assert.Equal(2, result.Errors.Count);
        Assert.Contains("expands to itself", result.Errors[0].Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("unknownquantity", result.Errors[1].Message);
        Assert.Contains("passivecalc1", result.Errors[1].Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0, 100, 55)]
    [InlineData(1, 99, 55)]
    [InlineData(10, 90, 60)]
    [InlineData(25, 75, 67)]
    [InlineData(0, 0, 5)]
    [InlineData(25, 0, 30)]
    public async Task TitansCovenantCoversEachRowsEntireEffectiveLevelRange(int hard, int bonus, double expected)
    {
        var data = await NewDataAsync();
        AddSkill(data, "Titans Covenant", new Skills { Param3 = "5", Param4 = "2", PassiveCalc1 = "par3+blvl+((lvl-blvl)/par4)" }, "pst1");
        var result = await new SkillDescriptionImporter(data).ImportAsync();
        var set = Assert.Single(result.Items);
        var grid = Assert.Single(set.Stats).Values2D![0];
        var row = grid[Math.Min(hard, grid.Length - 1)];
        Assert.Equal(expected, row[Math.Min(bonus, row.Length - 1)]);
        Assert.Equal(100, set.MaxLevel);
        Assert.Equal(25, set.HardPointCap);
        Assert.InRange(row.Length, 1, 101 - hard);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task ConstantGridTailsAreStillTrimmed()
    {
        var data = await NewDataAsync();
        AddSkill(data, "Constant", new Skills { PassiveCalc1 = "blvl*0+5" }, "pst1");
        var result = await new SkillDescriptionImporter(data).ImportAsync();
        var grid = Assert.Single(Assert.Single(result.Items).Stats).Values2D![0];
        Assert.Equal(5, Assert.Single(Assert.Single(grid)));
    }

    [Fact]
    public async Task QuotedSkillAliasesDoNotApplyAnotherRowsPercentageInTheWrongContext()
    {
        var data = await NewDataAsync();
        AddSkill(data, "Described", new Skills { Param8 = "20" }, "skill('Referenced'.pst1)");
        data.Skills["Referenced"] = new SkillEntry
        {
            Id = 2, Skill = "Referenced", SourceRow = new Skills
            {
                EMin = 4, HitShift = 8, PassiveCalc1 = "enma", Param8 = "10",
                EDmgSymPerCalc = "skill('Synergy'.blvl)*par8"
            }
        };
        var result = await new SkillDescriptionImporter(data).ImportAsync();
        var line = Assert.Single(Assert.Single(result.Items).Stats);
        Assert.Equal("elemental", line.Scale);
        Assert.Equal("skill('Synergy'.blvl)*(10)", line.ScaleCalc);
        Assert.Empty(result.Errors);
    }


    [Theory]
    [InlineData("par8*2", "(10)*2")]
    [InlineData("max(par8,pa10)", "max((10),(4))")]
    [InlineData("lvl*par8", null)]
    [InlineData("clc1", null)]
    [InlineData("1 ? par8 : pa10", "1 ? (10) : (4)")]
    [InlineData("0 ? par8 : pa10", "0 ? (10) : (4)")]
    [InlineData("1 ? par8 : lvl", null)]
    [InlineData("0 ? clc1 : par8", null)]
    public async Task ReferencedPercentageParametersBindThroughTheTableWithoutGuessingOtherContexts(string expression, string? expected)
    {
        var data = await NewDataAsync();
        var skill = new SkillEntry { Id = 1, Skill = "Source", SourceRow = new Skills { Param8 = "10", Param10 = "4", Calc1 = "par8" } };
        var context = new SkillCalcContext { Skill = skill, Data = data, Levels = new CalcLevels(1, 1) };
        var success = CalcExpression.TryBindParameters(expression, context, out var bound);
        Assert.Equal(expected is not null, success);
        if (success) Assert.Equal(expected, bound);
    }

}
