// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using D2RMultiExport.Lib;
using D2RMultiExport.Lib.Config;
using D2RMultiExport.Lib.Exporters;
using D2RMultiExport.Lib.Models;
using D2RMultiExport.Lib.Translation;

namespace D2RMultiExport.Tests;

/// <summary>Unprobed display keys are audited; structural automagic names and fallback names are not.</summary>
public sealed class TranslationAuditRegressionTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("d2r-translation-audit-").FullName;
    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static readonly string[] AutoMagicNames =
    [
        "of Cinders", "of Darkness", "of Despair", "of Ember", "of Flames", "of Inferno", "of Nightmares", "of Shadows",
        "A Jav 1", "A Jav 2", "A Jav 3", "Bow 1", "Bow 2", "Bow 3", "HP 1", "HP 2", "HP 3", "HP 4", "HP 5", "HP 6",
        "Mana 1", "Mana 2", "Mana 3", "Mana 4", "Mana 5", "Mana 6", "Mana 7", "Replinish Quant", "Spear 1", "Spear 2", "Spear 3"
    ];

    [Fact]
    public async Task AllUnprobedAutoMagicDisplayKeysAreReportedWithCurrentRunProvenance()
    {
        var outputs = new ExportOutputs(_root);
        await outputs.WriteJsonAsync("keyed/armors.json", AutoMagicNames.Select(static name => new { NameKey = name }), new JsonSerializerOptions());
        await File.WriteAllTextAsync(Path.Combine(_root, "keyed", "stale.json"), "{\"NameKey\":\"staleMissing\"}");
        var references = await D2RMultiExportPipeline.CollectKeyReferencesAsync(outputs, displayOnly: true);
        var missing = D2RMultiExportPipeline.FindMissingDisplayKeys(references, new MultiLanguageTranslationService());
        Assert.Equal(AutoMagicNames.Order(StringComparer.Ordinal), missing);
        Assert.All(references.Values, sources => Assert.Equal("armors.json", Assert.Single(sources)));
    }

    [Fact]
    public async Task DisplayAuditIncludesNestedKeysAndArgumentsButExcludesIdentifiersAndFallbackNames()
    {
        var outputs = new ExportOutputs(_root);
        await outputs.WriteTextAsync("keyed/test.json", """
            { "NameKey": "known", "FallbackName": "Monster internal name", "Code": "internal",
              "Calculation": { "ElementalDamage": "lvl*2" }, "Icon": "sprites/skills/test.webp",
              "CompositeKeys": { "physical": "missingComposite" },
              "Lines": [{ "key": "known", "args": [3, "missingArg", { "key": "missingNested" }],
                          "qualifier": "weapon", "classOnly": "missingClass" }] }
            """);
        var translations = new MultiLanguageTranslationService();
        translations.MergeSynthetic(new Dictionary<string, string> { ["known"] = "Known" }, new Dictionary<string, IReadOnlyDictionary<string, string>>());
        var references = await D2RMultiExportPipeline.CollectKeyReferencesAsync(outputs, displayOnly: true);
        Assert.Equal(["missingArg", "missingClass", "missingComposite", "missingNested"],
            D2RMultiExportPipeline.FindMissingDisplayKeys(references, translations));
        Assert.DoesNotContain("Monster internal name", references.Keys);
        Assert.DoesNotContain("weapon", references.Keys);
    }

    [Fact]
    public async Task AutoMagicInternalNamesExportAsCodesAndTranslatedNamesKeepTheirDisplayKey()
    {
        var config = new ExportConfig();
        config.BuildLookups();
        var data = new GameData { ExportConfig = config, StatOverrideConfig = new StatOverrideConfig(), Translations = new TranslationService() };
        data.Properties["testprop"] = new PropertyEntry { Code = "testprop", Stat1 = "teststat" };
        data.Stats["teststat"] = new StatEntry { Stat = "teststat", DescriptionFunction = 1, DescriptionValue = 1, DescStrPosKey = "known" };
        data.Translations.MergeSeed(new Dictionary<string, string> { ["known"] = "%d" });
        data.Armors["test"] = new EquipmentEntry { Code = "test", NameStr = "known", Name = "known", EquipmentType = EquipmentType.Armor, AutoPrefix = 1 };
        foreach (var name in AutoMagicNames.Append("translatedAffix"))
        {
            data.AutoMagics.Add(new MagicAffixEntry { Name = name, Group = 1, Properties = [new ItemPropertyValue { Code = "testprop", Min = 1, Max = 1 }] });
        }
        var outputs = new ExportOutputs(_root);
        // Simulate a CASC-only translated name that the enUS importer has never probed.
        await KeyedJsonExporter.ExportAsync(outputs, data, false, static key => key == "translatedAffix");
        using var doc = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(_root, "keyed", "armors.json")));
        var groups = doc.RootElement[0].GetProperty("AutoMagicGroups").EnumerateArray().ToList();
        Assert.Equal(32, groups.Count);
        foreach (var group in groups)
        {
            var code = group.GetProperty("Code").GetString();
            if (code == "translatedAffix") Assert.Equal(code, group.GetProperty("NameKey").GetString());
            else Assert.False(group.TryGetProperty("NameKey", out _));
        }
        var references = await D2RMultiExportPipeline.CollectKeyReferencesAsync(outputs, displayOnly: true);
        Assert.DoesNotContain(references.Keys, AutoMagicNames.Contains);
        Assert.Contains("translatedAffix", references.Keys);
    }
}
