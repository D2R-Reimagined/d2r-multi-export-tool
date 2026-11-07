// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using D2RMultiExport.Lib.Config;
using D2RMultiExport.Lib.Exporters;
using D2RMultiExport.Lib.Models;
using D2RMultiExport.Lib.Translation;

namespace D2RMultiExport.Tests;

/// <summary>Final-JSON regressions for the additive socketable catalog.</summary>
public sealed class SocketableExporterTests : IDisposable
{
    private readonly string _output = Directory.CreateTempSubdirectory("d2r-socketables-").FullName;

    public void Dispose() => Directory.Delete(_output, recursive: true);

    [Fact]
    public async Task ExportsSocketableTypesWithContextLinesAndKeepsJewelsWithoutFixedEffects()
    {
        var config = new ExportConfig
        {
            SocketableItemKinds = new Dictionary<string, string>
            {
                ["rune"] = "rune",
                ["gem"] = "gem",
                ["jewl"] = "jewel"
            }
        };
        config.BuildLookups();
        var translations = new TranslationService();
        translations.MergeSeed(new Dictionary<string, string>
        {
            ["strengthKey"] = "+%d Strength",
            ["r01"] = "El Rune"
        });
        var data = new GameData
        {
            ExportConfig = config,
            StatOverrideConfig = new StatOverrideConfig(),
            Translations = translations
        };
        data.ItemTypes["sock"] = Type("sock", "sockitype");
        data.ItemTypes["sock-child"] = Type("sock-child", "sockchilditype", "sock");
        data.ItemTypes["rune"] = Type("rune", "runeitype", "sock-child");
        data.ItemTypes["gem"] = Type("gem", "gemitype", "sock");
        data.ItemTypes["jewl"] = Type("jewl", "jewlitype", "sock");
        data.ItemTypes["poti"] = Type("poti", "potiitype");
        data.MiscItems["r01"] = Misc("r01", "r01L", "rune", 1, 11);
        data.MiscItems["gpr"] = Misc("gpr", "gpr", "poti", 18, 18, "gem");
        data.MiscItems["jew"] = Misc("jew", "jew", "jewl", 1, 1);
        data.MiscItems["hp1"] = Misc("hp1", "hp1", "poti", 1, 1);
        data.Properties["str"] = new PropertyEntry { Code = "str", Stat1 = "strength", Function1 = 1 };
        data.Stats["strength"] = new StatEntry
        {
            Stat = "strength",
            DescriptionPriority = 10,
            DescriptionFunction = 1,
            DescriptionValue = 1,
            DescStrPosKey = "strengthKey"
        };
        data.Gems["r01"] = Gem("r01", 3, 4, 5);
        data.Gems["gpr"] = Gem("gpr", 6, 7, 8);

        var outputs = new ExportOutputs(_output) { Producer = SocketableExporter.Producer };
        await SocketableExporter.ExportAsync(outputs, data, prettyPrint: false, static _ => { });

        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_output, "keyed", "socketables.json")));
        var rows = json.RootElement.EnumerateArray().ToList();
        Assert.Equal(["jew", "r01", "gpr"], rows.Select(row => row.GetProperty("Code").GetString()));
        Assert.DoesNotContain(rows, row => row.GetProperty("Code").GetString() == "hp1");

        var rune = rows.Single(row => row.GetProperty("Code").GetString() == "r01");
        Assert.Equal("r01", rune.GetProperty("NameKey").GetString());
        Assert.Equal("runeitype", rune.GetProperty("Type").GetString());
        Assert.Equal("rune", rune.GetProperty("Kind").GetString());
        Assert.Equal(11, rune.GetProperty("RequiredLevel").GetInt32());
        Assert.Equal(3, LineValue(rune, "WeaponLines"));
        Assert.Equal(4, LineValue(rune, "ArmorLines"));
        Assert.Equal(5, LineValue(rune, "ShieldLines"));

        var jewel = rows.Single(row => row.GetProperty("Code").GetString() == "jew");
        Assert.Equal("jewlitype", jewel.GetProperty("Type").GetString());
        Assert.Equal("jewel", jewel.GetProperty("Kind").GetString());
        Assert.Empty(jewel.GetProperty("WeaponLines").EnumerateArray());
        Assert.Empty(jewel.GetProperty("ArmorLines").EnumerateArray());
        Assert.Empty(jewel.GetProperty("ShieldLines").EnumerateArray());
    }

    private static int LineValue(JsonElement item, string property) =>
        item.GetProperty(property)[0].GetProperty("args")[0].GetInt32();

    private static ItemTypeEntry Type(string code, string index, string? parent = null) => new()
    {
        Code = code,
        Index = index,
        Equiv1 = parent
    };

    private static MiscEntry Misc(
        string code,
        string nameKey,
        string type,
        int level,
        int requiredLevel,
        string? type2 = null) => new()
    {
        Code = code,
        NameStr = nameKey,
        Type = type,
        Type2 = type2,
        Level = level,
        LevelReq = requiredLevel
    };

    private static GemEntry Gem(string code, int weapon, int armor, int shield) => new()
    {
        Name = code,
        Code = code,
        WeaponProperties = [Property(weapon)],
        HelmProperties = [Property(armor)],
        ShieldProperties = [Property(shield)]
    };

    private static ItemPropertyValue Property(int value) => new()
    {
        Code = "str",
        Min = value,
        Max = value
    };
}
