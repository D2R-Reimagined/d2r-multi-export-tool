// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using D2RMultiExport.Lib;
using D2RMultiExport.Lib.Config;
using D2RMultiExport.Lib.ErrorHandling;
using D2RMultiExport.Lib.Exporters;
using D2RMultiExport.Lib.Import;
using D2RMultiExport.Lib.Models;
using D2RMultiExport.Lib.Translation;

namespace D2RMultiExport.Tests;

/// <summary>
/// Item source levels (<c>level</c> / <c>lvl</c>) remain distinct from their required character
/// levels (<c>levelreq</c> / <c>lvl req</c>) through parser-backed import and keyed JSON output.
/// </summary>
public sealed class ItemLevelExportTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("d2r-item-levels-").FullName;
    private string Excel => Path.Combine(_root, "global", "excel");

    public ItemLevelExportTests()
    {
        Directory.CreateDirectory(Excel);
        Directory.CreateDirectory(Path.Combine(_root, "local", "lng", "strings"));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task ItemLevelsAndRequiredLevelsSurviveParserImportAndKeyedJsonExport()
    {
        WriteFixture();
        var data = new GameData
        {
            ExportConfig = NewExportConfig(),
            StatOverrideConfig = new StatOverrideConfig(),
            Translations = new TranslationService()
        };
        var sources = SourceRoots.Create(_root, null);
        await new DataLoader(sources, data, new PipelineResult()).LoadAllAsync();

        Assert.Equal(17, data.Armors["cap"].Level);
        Assert.Equal(11, data.Armors["cap"].LevelReq);
        Assert.Equal(23, data.Weapons["9st"].Level);
        Assert.Equal(7, data.Weapons["9st"].LevelReq);

        var uniques = await new UniqueImporter(sources, data).ImportAsync();
        Assert.Empty(uniques.Errors);
        data.Uniques = uniques.Items;
        var sets = await new SetImporter(sources, data).ImportAsync();
        Assert.Empty(sets.Errors);
        data.Sets = sets.Items;

        var outputs = new ExportOutputs(Path.Combine(_root, "out"));
        await KeyedJsonExporter.ExportAsync(outputs, data, false);

        using var armorJson =
            JsonDocument.Parse(await File.ReadAllBytesAsync(outputs.GetFullPath("keyed/armors.json")));
        using var weaponJson =
            JsonDocument.Parse(await File.ReadAllBytesAsync(outputs.GetFullPath("keyed/weapons.json")));
        using var uniqueJson =
            JsonDocument.Parse(await File.ReadAllBytesAsync(outputs.GetFullPath("keyed/uniques.json")));
        using var setJson = JsonDocument.Parse(await File.ReadAllBytesAsync(outputs.GetFullPath("keyed/sets.json")));

        var armor = FindByNameKey(armorJson.RootElement, "cap");
        Assert.Equal(17, armor.GetProperty("ItemLevel").GetInt32());
        Assert.Equal(11, armor.GetProperty("RequiredLevel").GetInt32());

        var weapon = FindByNameKey(weaponJson.RootElement, "9st");
        Assert.Equal(23, weapon.GetProperty("ItemLevel").GetInt32());
        Assert.Equal(7, weapon.GetProperty("RequiredLevel").GetInt32());

        var uniqueRows = uniqueJson.RootElement.EnumerateArray()
            .ToDictionary(static item => item.GetProperty("Index").GetString()!, StringComparer.Ordinal);
        Assert.Equal(1, uniqueRows["The Gnasher"].GetProperty("ItemLevel").GetInt32());
        Assert.Equal(5, uniqueRows["The Gnasher"].GetProperty("RequiredLevel").GetInt32());
        Assert.Equal(12, uniqueRows["Skull Splitter"].GetProperty("ItemLevel").GetInt32());
        Assert.Equal(24, uniqueRows["Skull Splitter"].GetProperty("RequiredLevel").GetInt32());
        AssertNoNestedItemLevel(uniqueRows["The Gnasher"]);
        AssertNoNestedItemLevel(uniqueRows["Skull Splitter"]);

        var set = Assert.Single(setJson.RootElement.EnumerateArray());
        Assert.Equal(1, set.GetProperty("ItemLevel").GetInt32());
        var setItems = set.GetProperty("SetItems").EnumerateArray()
            .ToDictionary(static item => item.GetProperty("Index").GetString()!, StringComparer.Ordinal);
        Assert.Equal(1, setItems["Set Level One"].GetProperty("ItemLevel").GetInt32());
        Assert.Equal(4, setItems["Set Level One"].GetProperty("RequiredLevel").GetInt32());
        Assert.Equal(13, setItems["Set Level Thirteen"].GetProperty("ItemLevel").GetInt32());
        Assert.Equal(8, setItems["Set Level Thirteen"].GetProperty("RequiredLevel").GetInt32());
        AssertNoNestedItemLevel(setItems["Set Level One"]);
        AssertNoNestedItemLevel(setItems["Set Level Thirteen"]);
    }

    private void WriteFixture()
    {
        WriteTable("armor.txt",
            "name\tcode\tnamestr\ttype\tlevel\tlevelreq",
            "Test Cap\tcap\tcap\thelm\t17\t11");
        WriteTable("weapons.txt",
            "name\tcode\tnamestr\ttype\tlevel\tlevelreq",
            "Test Short Staff\t9st\t9st\tstaf\t23\t7");
        WriteTable("uniqueitems.txt",
            "index\t*ID\tspawnable\tlvl\tlvl req\tcode",
            "The Gnasher\t0\t1\t1\t5\t9st",
            "Skull Splitter\t1\t1\t12\t24\t9st");
        WriteTable("setitems.txt",
            "index\t*ID\tset\titem\tspawnable\tlvl\tlvl req",
            "Set Level One\t0\tTest Set\t9st\t1\t1\t4",
            "Set Level Thirteen\t1\tTest Set\t9st\t1\t13\t8");
        WriteTable("sets.txt", "index\tname", "Test Set\tTest Set");
    }

    private void WriteTable(string file, params string[] lines) =>
        File.WriteAllText(Path.Combine(Excel, file), string.Join("\r\n", lines) + "\r\n");

    private static ExportConfig NewExportConfig()
    {
        var config = new D2RMultiExport.Lib.Config.ExportConfig();
        config.BuildLookups();
        return config;
    }

    private static JsonElement FindByNameKey(JsonElement rows, string nameKey) => rows.EnumerateArray()
        .Single(row => row.GetProperty("NameKey").GetString() == nameKey);

    private static void AssertNoNestedItemLevel(JsonElement item) =>
        Assert.False(item.GetProperty("Equipment").TryGetProperty("ItemLevel", out _));
}
