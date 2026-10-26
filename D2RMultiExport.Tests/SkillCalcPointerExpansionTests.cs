// SPDX-License-Identifier: GPL-3.0-or-later
using D2RMultiExport.Lib.Config;
using D2RMultiExport.Lib.Import;
using D2RMultiExport.Lib.Models;
using D2RMultiExport.Lib.Translation;
using Skills = D2RReimaginedTools.Models.Skills;

namespace D2RMultiExport.Tests;

public sealed class SkillCalcPointerExpansionTests
{
    private static async Task<GameData> NewDataAsync(string difficulty = "hell")
    {
        var config = new ExportConfig
        {
            SkillDescriptionDifficulty = difficulty,
            SkillDescriptionMaxLevel = 12
        };
        config.BuildLookups();
        return new GameData
        {
            ExportConfig = config,
            StatOverrideConfig = new StatOverrideConfig(),
            Translations = new TranslationService(),
            SkillCalcCodes = await CalcCodeTable.LoadAsync(
                Path.Combine(AppContext.BaseDirectory, "Config", "CASC_DATA", "skillcalc.txt"))
        };
    }

    private static SkillEntry AddSkill(GameData data, string name, Skills row, string? calc = null, bool exported = true)
    {
        var id = data.Skills.Count + 1;
        var desc = $"{name} description";
        var skill = new SkillEntry
        {
            Id = id,
            Skill = name,
            CharClass = exported ? "sor" : null,
            SkillDesc = desc,
            MaxLevel = 3,
            SourceRow = row
        };
        data.Skills[name] = skill;
        data.SkillsById[id] = skill;
        if (exported)
        {
            data.SkillDescs[desc] = new SkillDescEntry
            {
                SkillDesc = desc,
                Page = 1,
                Row = 1,
                Column = 1,
                DescriptionLines = calc is null
                    ? []
                    : [new SkillDescLine { Function = 74, TextA = name, CalcA = calc }]
            };
        }
        return skill;
    }

    private static long Evaluate(SkillEntry skill, GameData data, string expression, int level, int hardPoints)
    {
        var context = new SkillCalcContext
        {
            Skill = skill,
            Data = data,
            Levels = new CalcLevels(level, hardPoints)
        };
        Assert.True(SkillCalculator.TryEvaluate(expression, context, out var value), context.Trace.Reason);
        return value;
    }

    [Fact]
    public async Task SameSkillPassiveAndAuraPointersExpandAndKeepTheSolvedTables()
    {
        var data = await NewDataAsync();
        var described = AddSkill(data, "Described", new Skills
        {
            Param1 = "3",
            Param2 = "2",
            PassiveCalc1 = "lvl*par1+blvl",
            AuraStatCalc1 = "lvl+par2"
        }, "pst1+ast1+skill('Synergy'.blvl)");
        AddSkill(data, "Synergy", new Skills());
        var characterLevelSkill = AddSkill(data, "Character Level", new Skills(),
            "ulvl+skill('Synergy'.blvl)");

        var result = await new SkillDescriptionImporter(data).ImportAsync();
        Assert.Empty(result.Errors);
        var item = Assert.Single(result.Items, entry => entry.SkillId == described.Id);
        var line = Assert.Single(item.Stats);
        var expanded = Assert.Single(line.ValuesCalc!)!;

        Assert.DoesNotContain("pst1", expanded, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ast1", expanded, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("skill('Synergy'.blvl)", expanded, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(7, line.Values![0][0]);
        Assert.Equal(27, line.Values![0][4]);
        Assert.Equal(16, line.Values2D![0][2][1]);
        Assert.Equal(24, line.Values2D![0][2][3]);
        Assert.Null(line.ValuesUlvl);

        var characterLine = Assert.Single(
            Assert.Single(result.Items, entry => entry.SkillId == characterLevelSkill.Id).Stats);
        Assert.Equal("ulvl+skill('Synergy'.blvl)", Assert.Single(characterLine.ValuesCalc!));
        Assert.Equal(1, characterLine.ValuesUlvl![0][0][0]);
        var characterContext = new SkillCalcContext
        {
            Skill = characterLevelSkill,
            Data = data,
            Levels = new CalcLevels(1, 1, 1)
        };
        Assert.True(SkillCalculator.TryEvaluate("ulvl+skill('Synergy'.blvl)", characterContext, out var value),
            characterContext.Trace.Reason);
        Assert.Equal(value, characterLine.ValuesUlvl[0][0][0]);

        foreach (var (level, hardPoints) in new[] { (1, 0), (1, 1), (3, 2), (5, 2), (5, 0) })
        {
            Assert.Equal(
                Evaluate(described, data, "pst1+ast1+skill('Synergy'.blvl)", level, hardPoints),
                Evaluate(described, data, expanded, level, hardPoints));
        }
    }

    [Fact]
    public async Task IrregularPsTenThroughFourteenAliasesExpandFromTheirOwnSourceCells()
    {
        var data = await NewDataAsync();
        var described = AddSkill(data, "Described", new Skills
        {
            Param1 = "2",
            Param2 = "4",
            PassiveCalc10 = "lvl*par1+blvl",
            PassiveCalc11 = "par2+blvl",
            PassiveCalc12 = "lvl+skill('Synergy'.blvl)",
            PassiveCalc13 = "skill('Synergy'.blvl)*par2",
            PassiveCalc14 = "lvl*2"
        }, "ps10+ps11+ps12+ps13+ps14");
        AddSkill(data, "Synergy", new Skills());

        var result = await new SkillDescriptionImporter(data).ImportAsync();
        Assert.Empty(result.Errors);
        var line = Assert.Single(Assert.Single(result.Items, item => item.SkillId == described.Id).Stats);
        var expanded = Assert.Single(line.ValuesCalc!)!;

        foreach (var index in Enumerable.Range(10, 5))
            Assert.DoesNotContain($"ps{index}", expanded, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("skill('Synergy'.blvl)", expanded, StringComparison.OrdinalIgnoreCase);

        foreach (var (level, hardPoints) in new[] { (1, 0), (5, 2), (10, 4) })
        {
            Assert.Equal(
                Evaluate(described, data, "ps10+ps11+ps12+ps13+ps14", level, hardPoints),
                Evaluate(described, data, expanded, level, hardPoints));
        }
    }

    [Fact]
    public async Task CrossSkillPointersKeepTheReferencedRowParametersAndLevels()
    {
        var data = await NewDataAsync();
        var described = AddSkill(data, "Described", new Skills { Param1 = "99" },
            "skill('Referenced'.pst1)+skill('Described'.blvl)");
        AddSkill(data, "Referenced", new Skills
        {
            Param1 = "7",
            PassiveCalc1 = "lvl*par1+blvl+skill('Third'.blvl)"
        });
        AddSkill(data, "Third", new Skills());

        var result = await new SkillDescriptionImporter(data).ImportAsync();
        Assert.Empty(result.Errors);
        var expanded = Assert.Single(Assert.Single(result.Items, item => item.SkillId == described.Id).Stats)
            .ValuesCalc![0]!;

        Assert.Contains("skill('Referenced'.lvl)", expanded, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("skill('Referenced'.blvl)", expanded, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("(7)", expanded, StringComparison.Ordinal);
        Assert.Contains("skill('Third'.blvl)", expanded, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("+par1", expanded, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("skill('Described'.blvl)", expanded, StringComparison.OrdinalIgnoreCase);

        foreach (var (level, hardPoints) in new[] { (1, 0), (3, 1), (5, 3), (5, 0) })
        {
            Assert.Equal(
                Evaluate(described, data, "skill('Referenced'.pst1)+skill('Described'.blvl)", level, hardPoints),
                Evaluate(described, data, expanded, level, hardPoints));
        }
    }

    [Fact]
    public async Task RecursiveCalcAndAuraPointersPreserveNestedLiveSynergies()
    {
        var data = await NewDataAsync();
        var described = AddSkill(data, "Described", new Skills(),
            "skill('Referenced'.clc1)+skill('Synergy'.blvl)");
        AddSkill(data, "Referenced", new Skills
        {
            Param2 = "4",
            Calc1 = "ast1*2+skill('Third'.blvl)",
            AuraStatCalc1 = "lvl+blvl+par2"
        });
        AddSkill(data, "Third", new Skills());
        AddSkill(data, "Synergy", new Skills());

        var result = await new SkillDescriptionImporter(data).ImportAsync();
        Assert.Empty(result.Errors);
        var expanded = Assert.Single(Assert.Single(result.Items, item => item.SkillId == described.Id).Stats)
            .ValuesCalc![0]!;

        Assert.DoesNotContain("clc1", expanded, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ast1", expanded, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("skill('Referenced'.lvl)", expanded, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("skill('Referenced'.blvl)", expanded, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("(4)", expanded, StringComparison.Ordinal);
        Assert.Contains("skill('Third'.blvl)", expanded, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("skill('Synergy'.blvl)", expanded, StringComparison.OrdinalIgnoreCase);

        foreach (var (level, hardPoints) in new[] { (1, 0), (3, 1), (5, 3) })
        {
            Assert.Equal(
                Evaluate(described, data, "skill('Referenced'.clc1)+skill('Synergy'.blvl)", level, hardPoints),
                Evaluate(described, data, expanded, level, hardPoints));
        }
    }

    [Fact]
    public async Task PointerCyclesAndUnavailableDependenciesKeepTheOriginalExpression()
    {
        var data = await NewDataAsync();
        var described = AddSkill(data, "Described", new Skills());
        AddSkill(data, "A", new Skills { Calc1 = "skill('B'.clc1)" });
        AddSkill(data, "B", new Skills { Calc1 = "skill('A'.clc1)" });
        AddSkill(data, "Not Exported", new Skills(), exported: false);
        AddSkill(data, "Empty", new Skills());

        Assert.Equal("skill('A'.clc1)", SkillCalcPointerExpander.Expand("skill('A'.clc1)", described, data));
        Assert.Equal("pnma+skill('A'.blvl)", SkillCalcPointerExpander.Expand("pnma+skill('A'.blvl)", described, data));
        Assert.Equal("skill('Missing'.blvl)", SkillCalcPointerExpander.Expand("skill('Missing'.blvl)", described, data));
        Assert.Equal("skill('Not Exported'.blvl)",
            SkillCalcPointerExpander.Expand("skill('Not Exported'.blvl)", described, data));
        Assert.Equal("ast1+skill('A'.blvl)", SkillCalcPointerExpander.Expand("ast1+skill('A'.blvl)", described, data));
        Assert.Equal("pst1+skill('A'.blvl)", SkillCalcPointerExpander.Expand("pst1+skill('A'.blvl)", described, data));
        Assert.Equal("skill('Empty'.pst1)", SkillCalcPointerExpander.Expand("skill('Empty'.pst1)", described, data));
        Assert.Equal("skill('Missing'.edmn)", SkillCalcPointerExpander.Expand("skill('Missing'.edmn)", described, data));
        Assert.Equal("miss('Missing Missile'.edmn)",
            SkillCalcPointerExpander.Expand("miss('Missing Missile'.edmn)", described, data));
        Assert.Equal("sklvl('A'.lvl.edmn)",
            SkillCalcPointerExpander.Expand("sklvl('A'.lvl.edmn)", described, data));
        Assert.Equal("cond('Expansion')", SkillCalcPointerExpander.Expand("cond('Expansion')", described, data));
    }

    [Theory]
    [InlineData("pst1%2+skill('Synergy'.blvl)")]
    [InlineData("pst1^2+skill('Synergy'.blvl)")]
    [InlineData("pst1=2")]
    [InlineData("pst1+0.5")]
    public async Task WebsiteUnsupportedSyntaxKeepsTheOriginalExpression(string expression)
    {
        var data = await NewDataAsync();
        var described = AddSkill(data, "Described", new Skills { PassiveCalc1 = "blvl" });
        AddSkill(data, "Synergy", new Skills());

        Assert.Equal(expression, SkillCalcPointerExpander.Expand(expression, described, data));
    }

    [Theory]
    [InlineData("hell", "1")]
    [InlineData("normal", "0")]
    public async Task DifficultyConditionIsResolvedForConfiguredDifficulty(string difficulty, string expected)
    {
        var data = await NewDataAsync(difficulty);
        var described = AddSkill(data, "Described", new Skills
        {
            PassiveCalc1 = "cond('Difficulty', hell)*blvl+skill('Synergy'.blvl)"
        });
        AddSkill(data, "Synergy", new Skills());

        var expanded = SkillCalcPointerExpander.Expand("pst1", described, data);
        Assert.Contains(expected, expanded, StringComparison.Ordinal);
        Assert.DoesNotContain("cond(", expanded, StringComparison.OrdinalIgnoreCase);

        foreach (var (level, hardPoints) in new[] { (1, 0), (3, 1), (5, 3) })
        {
            Assert.Equal(
                Evaluate(described, data, "pst1", level, hardPoints),
                Evaluate(described, data, expanded, level, hardPoints));
        }
    }
}