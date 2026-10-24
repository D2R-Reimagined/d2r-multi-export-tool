// SPDX-License-Identifier: GPL-3.0-or-later
using D2RMultiExport.Lib.Config;
using D2RMultiExport.Lib.Import;
using D2RMultiExport.Lib.Models;
using D2RMultiExport.Lib.Translation;

namespace D2RMultiExport.Tests;

/// <summary>Duplicate rune contributions retain their scope and row metadata.</summary>
public sealed class PropertyMergeRegressionTests
{
    [Fact]
    public void PhoenixVexContributionsKeepWeaponAndShieldQualifiersAfterSumming()
    {
        var config = new ExportConfig();
        config.BuildLookups();
        var translations = new TranslationService();
        translations.MergeSeed(new Dictionary<string, string> { ["manaKey"] = "%d%% Mana stolen per hit", ["resKey"] = "%d%% Maximum Fire Resist" });
        var data = new GameData { ExportConfig = config, StatOverrideConfig = new StatOverrideConfig(), Translations = translations };
        data.Properties["manasteal"] = new PropertyEntry { Code = "manasteal", Stat1 = "item_manasteal" };
        data.Properties["res-fire-max"] = new PropertyEntry { Code = "res-fire-max", Stat1 = "maxfireresist" };
        data.Stats["item_manasteal"] = new StatEntry { Stat = "item_manasteal", DescriptionFunction = 1, DescriptionValue = 1, DescStrPosKey = "manaKey", DescriptionPriority = 10 };
        data.Stats["maxfireresist"] = new StatEntry { Stat = "maxfireresist", DescriptionFunction = 1, DescriptionValue = 1, DescStrPosKey = "resKey", DescriptionPriority = 9 };
        var properties = new List<CubePropertyExport>();
        foreach (var (code, value, scope) in new[] { ("manasteal", 7, "weapon"), ("res-fire-max", 5, "shield") })
        {
            for (var i = 0; i < 2; i++)
            {
                var row = Assert.Single(PropertyMapper.MapRows(code, null, value, value, data, 65, properties.Count));
                row.Suffix = scope == "weapon" ? "(Weapon)" : "(Shield)";
                row.ModChance = 100;
                row.NumberOfItemsRequired = 2;
                foreach (var line in row.Lines) line.Qualifier = scope;
                properties.Add(row);
            }
        }
        var cleaned = PropertyCleanup.CleanupDuplicates(properties, data, 65, PropertyCleanup.RunewordSuffixOrder);
        Assert.Equal(2, cleaned.Count);
        Assert.Equal("manasteal", cleaned[0].PropertyCode);
        Assert.Equal("res-fire-max", cleaned[1].PropertyCode);
        Assert.Equal(14, cleaned[0].Min);
        Assert.Equal(14, cleaned[0].Max);
        Assert.Equal(10, cleaned[1].Min);
        Assert.Equal(10, cleaned[1].Max);
        Assert.Equal("weapon", Assert.Single(cleaned[0].Lines).Qualifier);
        Assert.Equal("shield", Assert.Single(cleaned[1].Lines).Qualifier);
        Assert.All(cleaned, row => { Assert.Equal(100, row.ModChance); Assert.Equal(2, row.NumberOfItemsRequired); });
    }
}
