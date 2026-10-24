// SPDX-License-Identifier: GPL-3.0-or-later
using D2RMultiExport.Lib.Config;
using D2RMultiExport.Lib.Import;
using D2RMultiExport.Lib.Models;
using D2RMultiExport.Lib.Translation;

namespace D2RMultiExport.Tests;

/// <summary>
/// Multi-stat Properties.txt definitions: only stats the engine guarantees the same value
/// and that form a complete ItemStatCost display group share one description.
/// </summary>
public sealed class MultiStatPropertyTests
{
    private static GameData CreateData()
    {
        var config = new ExportConfig();
        config.BuildLookups();
        return new GameData { ExportConfig = config, StatOverrideConfig = new StatOverrideConfig(), Translations = new TranslationService() };
    }

    private static void AddDefinition(GameData data, string code, params (int Function, string Stat)[] slots)
    {
        data.Properties[code] = new PropertyEntry
        {
            Code = code,
            Stat1 = slots[0].Stat,
            Function1 = slots[0].Function,
            Slots = [.. slots.Select(s => new PropertySlot { Function = s.Function, Stat = s.Stat })]
        };
    }

    private static void AddStat(GameData data, string stat, int priority, string? key, int? group = null, string? groupKey = null)
    {
        data.Stats[stat] = new StatEntry
        {
            Stat = stat,
            DescriptionPriority = priority,
            DescriptionFunction = 19,
            DescriptionValue = 3,
            DescStrPosKey = key,
            DescStrNegKey = key,
            GroupDescription = group,
            GroupDescriptionFunction = group.HasValue ? 19 : null,
            GroupDescriptionValue = group.HasValue ? 3 : null,
            GroupDescStrPosKey = groupKey,
            GroupDescStrNegKey = groupKey
        };
    }

    private static void AddAttributePercentStats(GameData data)
    {
        AddStat(data, "item_strength_percent", 67, "ModAttStrPercent", 8, "ModAllStatsPercent");
        AddStat(data, "item_dexterity_percent", 67, "ModAttDexPercent", 8, "ModAllStatsPercent");
        AddStat(data, "item_vitality_percent", 67, "ModAttVitPercent", 8, "ModAllStatsPercent");
        AddStat(data, "item_energy_percent", 67, "ModAttEnrPercent", 8, "ModAllStatsPercent");
    }

    private static (int Function, string Stat)[] AttributePercentSlots(int laterFunction) =>
    [
        (1, "item_strength_percent"), (laterFunction, "item_dexterity_percent"),
        (laterFunction, "item_vitality_percent"), (laterFunction, "item_energy_percent")
    ];

    [Fact]
    public void MirroredStatsWithCompleteDisplayGroupRenderTheGroupDescription()
    {
        var data = CreateData();
        AddAttributePercentStats(data);
        AddDefinition(data, "stats%", AttributePercentSlots(3));

        var line = Assert.Single(PropertyMapper.Map("stats%", null, 5, 5, data, 0).Lines);

        Assert.Equal("ModAllStatsPercent", line.Key);
        Assert.Equal([5], line.Args);
    }

    [Fact]
    public void MirroredStatsWithoutDisplayGroupKeepOneLinePerStatInPriorityOrder()
    {
        var data = CreateData();
        AddStat(data, "item_absorbfire_percent", 23, "ModStr5g");
        AddStat(data, "item_absorblight_percent", 24, "ModStr5i");
        AddStat(data, "item_absorbcold_percent", 25, "ModStr5m");
        AddStat(data, "item_absorbmagic_percent", 26, "ModStr5k");
        AddDefinition(data, "abs-all%", (1, "item_absorbfire_percent"), (3, "item_absorblight_percent"),
            (3, "item_absorbcold_percent"), (3, "item_absorbmagic_percent"));

        var lines = PropertyMapper.Map("abs-all%", null, 5, 10, data, 0).Lines;

        Assert.Equal(["ModStr5k", "ModStr5m", "ModStr5i", "ModStr5g"], lines.Select(l => l.Key));
        Assert.All(lines, l => Assert.Equal([5, 10], l.Args));
    }

    [Fact]
    public void IndependentRollsStaySeparateDespiteEqualRangesAndCompleteDisplayGroup()
    {
        var data = CreateData();
        AddAttributePercentStats(data);
        AddDefinition(data, "stats%-rand", AttributePercentSlots(1));

        var lines = PropertyMapper.Map("stats%-rand", null, 5, 5, data, 0).Lines;

        Assert.Equal(["ModAttStrPercent", "ModAttDexPercent", "ModAttVitPercent", "ModAttEnrPercent"], lines.Select(l => l.Key));
        Assert.All(lines, l => Assert.Equal([5], l.Args));
    }

    [Fact]
    public void IncompleteOrMixedDisplayGroupsRenderIndividually()
    {
        var data = CreateData();
        AddAttributePercentStats(data);
        // Three of the four dgrp 8 members: the game cannot use the group description.
        AddDefinition(data, "three%", (1, "item_strength_percent"), (3, "item_dexterity_percent"), (3, "item_vitality_percent"));
        Assert.Equal(["ModAttStrPercent", "ModAttDexPercent", "ModAttVitPercent"],
            PropertyMapper.Map("three%", null, 5, 5, data, 0).Lines.Select(l => l.Key));

        // A mirrored stat from another group completes neither dgrp 8 nor its own dgrp 2.
        AddStat(data, "fireresist", 36, "ModStr1j", 2, "strModAllResistances");
        AddStat(data, "lightresist", 38, "ModStr1l", 2, "strModAllResistances");
        AddDefinition(data, "mixed", (1, "item_strength_percent"), (3, "item_dexterity_percent"),
            (3, "item_vitality_percent"), (3, "fireresist"));
        Assert.Equal(["ModAttStrPercent", "ModAttDexPercent", "ModAttVitPercent", "ModStr1j"],
            PropertyMapper.Map("mixed", null, 5, 5, data, 0).Lines.Select(l => l.Key));
    }

    [Fact]
    public void ZeroCrossingFirstRollIsNotPassedToReuseSlots()
    {
        var data = CreateData();
        AddAttributePercentStats(data);
        AddDefinition(data, "stats%", AttributePercentSlots(3));

        var lines = PropertyMapper.Map("stats%", null, -5, 5, data, 0).Lines;

        Assert.Equal(4, lines.Count);
        Assert.DoesNotContain(lines, l => l.Key == "ModAllStatsPercent");
    }

    [Fact]
    public void MinimumAndMaximumFunctionsApplyTheirOwnEndpointEvenWhenEqual()
    {
        var data = CreateData();
        AddStat(data, "pl_mindamage_percent", 130, "ModStrPlayerMinDmgPercent", 6, "ModStrPlayerDmgPercent");
        AddStat(data, "pl_maxdamage_percent", 129, "ModStrPlayerMaxDmgPercent", 6, "ModStrPlayerDmgPercent");
        AddDefinition(data, "dmg2%", (15, "pl_mindamage_percent"), (16, "pl_maxdamage_percent"));

        var ranged = PropertyMapper.Map("dmg2%", null, -35, -40, data, 0).Lines;
        Assert.Equal(["ModStrPlayerMinDmgPercent", "ModStrPlayerMaxDmgPercent"], ranged.Select(l => l.Key));
        Assert.Equal([-35], ranged[0].Args);
        Assert.Equal([-40], ranged[1].Args);

        var equal = PropertyMapper.Map("dmg2%", null, -40, -40, data, 0).Lines;
        Assert.Equal(["ModStrPlayerMinDmgPercent", "ModStrPlayerMaxDmgPercent"], equal.Select(l => l.Key));
    }

    [Fact]
    public void ParameterFunctionMirrorsOnlyWhenTheParameterIsSupplied()
    {
        var data = CreateData();
        AddStat(data, "item_strength_perlevel", 67, "ModStr1a", 9, "allStatsPerLevel");
        AddStat(data, "item_dexterity_perlevel", 65, "ModStr1b", 9, "allStatsPerLevel");
        AddDefinition(data, "both/lvl", (17, "item_strength_perlevel"), (17, "item_dexterity_perlevel"));

        var mirrored = Assert.Single(PropertyMapper.Map("both/lvl", "8", null, null, data, 0).Lines);
        Assert.Equal("allStatsPerLevel", mirrored.Key);
        Assert.True(mirrored.PerLevel);

        var rolled = PropertyMapper.Map("both/lvl", null, 1, 3, data, 0).Lines;
        Assert.Equal(["ModStr1a", "ModStr1b"], rolled.Select(l => l.Key));
    }

    [Fact]
    public void SyntheticCompositeAndHiddenHelperStatsKeepTheirExistingRendering()
    {
        var data = CreateData();
        AddStat(data, "fireresist", 36, "ModStr1j", 2, "strModAllResistances");
        AddStat(data, "lightresist", 38, "ModStr1l", 2, "strModAllResistances");
        data.Stats["res-all"] = new StatEntry
        {
            Stat = "res-all", IsSynthetic = true, DescriptionPriority = 34, DescriptionFunction = 19,
            DescriptionValue = 3, DescStrPosKey = "strModAllResistances", DescStrNegKey = "strModAllResistances"
        };
        AddDefinition(data, "res-all", (1, "fireresist"), (3, "lightresist"));
        var resAll = Assert.Single(PropertyMapper.Map("res-all", null, -5, 5, data, 0).Lines);
        Assert.Equal("strModAllResistances", resAll.Key);
        Assert.Equal([-5, 5], resAll.Args);

        // oskill: the second stat is the engine's hidden counterpart of the displayed one.
        AddStat(data, "item_nonclassskill_display", 150, "ItemModifierNonClassSkill");
        AddStat(data, "item_nonclassskill", 0, null);
        AddDefinition(data, "hidden-pair", (1, "item_nonclassskill_display"), (3, "item_nonclassskill"));
        Assert.Equal(["ItemModifierNonClassSkill"], PropertyMapper.Map("hidden-pair", null, 2, 2, data, 0).Lines.Select(l => l.Key));

        // A definition loaded without slot data stays a single-stat definition.
        data.Properties["legacy"] = new PropertyEntry { Code = "legacy", Stat1 = "fireresist", Function1 = 1 };
        Assert.Equal(["ModStr1j"], PropertyMapper.Map("legacy", null, 5, 5, data, 0).Lines.Select(l => l.Key));
    }

    [Fact]
    public void AdditiveDuplicatesAreSummedBeforeTheGroupIsRendered()
    {
        var data = CreateData();
        AddAttributePercentStats(data);
        AddDefinition(data, "stats%", AttributePercentSlots(3));
        List<CubePropertyExport> rows =
        [
            .. PropertyMapper.MapRows("stats%", null, 5, 5, data, 0, 0),
            .. PropertyMapper.MapRows("stats%", null, 3, 3, data, 0, 1)
        ];

        var merged = Assert.Single(PropertyCleanup.CleanupDuplicates(rows, data, 0));

        var line = Assert.Single(merged.Lines);
        Assert.Equal("ModAllStatsPercent", line.Key);
        Assert.Equal([8], line.Args);
    }
}
