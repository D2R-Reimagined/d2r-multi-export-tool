// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using D2RMultiExport.Lib;
using D2RMultiExport.Lib.Config;
using D2RMultiExport.Lib.Exporters;
using D2RMultiExport.Lib.Models;
using D2RMultiExport.Lib.Translation;

namespace D2RMultiExport.Tests;

/// <summary>
/// The drop, IAS and health calculator bundles honour the shared pretty-print option without
/// changing their data; the IAS and health bundles serialize source hashes as lowercase
/// hexadecimal like the manifest.
/// </summary>
public sealed partial class CalculatorExportFormattingTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("d2r-calculator-format-").FullName;

    private string Mod => Path.Combine(_root, "mod");
    private string Excel => Path.Combine(Mod, "global", "excel");

    public CalculatorExportFormattingTests()
    {
        Directory.CreateDirectory(Excel);
        Directory.CreateDirectory(Path.Combine(Mod, "local", "lng", "strings"));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex LowercaseSha256();

    private void WriteTable(string file, params string[] lines) =>
        File.WriteAllText(Path.Combine(Excel, file), string.Join("\r\n", lines) + "\r\n");

    private static GameData NewData(ExportConfig config) => new()
    {
        ExportConfig = config,
        StatOverrideConfig = new StatOverrideConfig(),
        Translations = new TranslationService()
    };

    private void WriteAttackSpeedTables()
    {
        WriteTable("weapons.txt", "name\ttype\tcode\tnamestr\tspeed\twclass\t2handedwclass", "Axe\taxe\thax\thax\t10\t1hs\t1hs");
        WriteTable("skills.txt", "skill\t*Id\tcharclass\tanim", "Attack\t0\t\tA1");
        WriteTable("itemtypes.txt", "ItemType\tCode\tEquiv1\tEquiv2", "Axe\taxe\tmele\t", "Melee Weapon\tmele\t\t");
    }

    private void WriteMonsterHealthTables()
    {
        WriteTable("monstats.txt", "Id\tNameStr\tMonProp\tenabled\tLevel\tAlign\tboss\tkillable\tDamageRegen\tnoRatio\tminHP\tmaxHP",
            "skeleton1\tSkeleton\t\t1\t2\t0\t0\t1\t0\t0\t5\t7");
        WriteTable("monlvl.txt", "Level\tHP\tHP(N)\tHP(H)\tL-HP\tL-HP(N)\tL-HP(H)", "1\t1\t2\t3\t4\t5\t6");
        WriteTable("levels.txt", "Name\tId\tLevelName\tMonLvlEx\tmon1", "Act 1 - Town\t1\tRogue Encampment\t1\tskeleton1");
        WriteTable("monumod.txt", "uniquemod\tid\tconstants",
            "minion\t1\t300", "minion\t2\t350", "minion\t3\t400",
            "champion\t4\t100", "champion\t5\t150", "champion\t6\t200",
            "unique\t7\t200", "unique\t8\t250", "unique\t9\t300");
    }

    private void WriteDropCalculatorTables()
    {
        WriteTable("treasureclassex.txt", "Treasure Class\tgroup\tlevel\tPicks\tUnique\tSet\tRare\tMagic\tNoDrop\tItem1\tProb1\tItem2\tProb2",
            "Act 1 Junk\t\t1\t1\t0\t0\t0\t0\t100\thax\t5\tr01\t3");
        WriteTable("itemratio.txt", "Version\tUnique\tRare\tSet\tMagic\tHiQuality\tNormal", "1\t400\t100\t160\t34\t12\t2");
        WriteTable("uniqueitems.txt", "index\tversion\tdisabled\tspawnable\trarity\tlvl\tcode", "The Gnasher\t0\t0\t1\t1\t7\thax");
        WriteTable("setitems.txt", "index\tset\tdisabled\tspawnable\titem\trarity\tlvl", "Berserker's Hatchet\tBerserker's Arsenal\t0\t1\thax\t1\t5");
        WriteTable("itemtypes.txt", "ItemType\tCode\tEquiv1\tEquiv2\tTreasureClass\tRarity\tClass",
            "Axe\taxe\tmele\t\t1\t3\t", "Melee Weapon\tmele\t\t\t0\t1\t", "Rune\trune\t\t\t0\t1\t");
        WriteTable("armor.txt", "name\tversion\tlevel\tspawnable\tcode\tnamestr\tnormcode\ttype\ttype2\tquest", "Cap\t0\t1\t1\tcap\tcap\tcap\thelm\t\t0");
        WriteTable("weapons.txt", "name\ttype\ttype2\tcode\tnamestr\tspawnable\tlevel\tnormcode\tquest", "Hand Axe\taxe\t\thax\thax\t1\t3\thax\t0");
        WriteTable("misc.txt", "name\tlevel\tspawnable\tcode\tnamestr\ttype\ttype2\tquest",
            "El Rune\t11\t1\tr01\tr01\trune\t\t0", "Key\t1\t1\tkey\tkey\tkey\t\t0");
        WriteTable("monstats.txt", "Id\tNameStr\tenabled\tkillable\tboss\tnoRatio\tLevel\tTreasureClass\tTreasureClassChamp\tTreasureClassUnique\tTreasureClassQuest",
            "skeleton1\tSkeleton\t1\t1\t0\t0\t2\tAct 1 Junk\tAct 1 Junk\t\t");
        WriteTable("superuniques.txt", "Superunique\tName\tClass\tTC\tTC(N)\tTC(H)", "Bishibosh\tBishibosh\tskeleton1\tAct 1 Junk\t\t");
        WriteTable("levels.txt", "Name\tId\tLevelName\tMonLvlEx\tmon1", "Act 1 - Wilderness 1\t2\tBlood Moor\t1\tskeleton1");
    }

    private async Task<string> ExportAsync(string relativeOutput, Func<ExportOutputs, SourceRoots, List<string>, Task> export, string path)
    {
        var outputs = new ExportOutputs(Path.Combine(_root, relativeOutput));
        var warnings = new List<string>();
        await export(outputs, SourceRoots.Create(Mod, null), warnings);
        Assert.True(outputs.WasWritten(path), string.Join(Environment.NewLine, warnings));
        return await File.ReadAllTextAsync(outputs.GetFullPath(path));
    }

    /// <summary>Asserts the pretty and compact documents differ only in whitespace.</summary>
    private static void AssertSameDocument(string pretty, string compact)
    {
        Assert.Contains('\n', pretty);
        Assert.DoesNotContain('\n', compact);
        using var document = JsonDocument.Parse(pretty);
        Assert.Equal(compact, JsonSerializer.Serialize(document.RootElement));
    }

    private void AssertLowercaseSourceHashes(string json, params string[] expectedFiles)
    {
        using var document = JsonDocument.Parse(json);
        var sources = document.RootElement.GetProperty("Sources").EnumerateArray()
            .ToDictionary(s => s.GetProperty("Code").GetString()!, s => s.GetProperty("Hash").GetString()!);
        Assert.Equal(expectedFiles.Order(StringComparer.Ordinal), sources.Keys.Order(StringComparer.Ordinal));
        foreach (var (file, hash) in sources)
        {
            var bytes = SHA256.HashData(File.ReadAllBytes(Path.Combine(Excel, file)));
            Assert.Matches(LowercaseSha256(), hash);
            Assert.Equal(Convert.ToHexStringLower(bytes), hash);
            // Same digest of the same bytes as before; only the letter case of the serialization changed.
            Assert.Equal(Convert.ToHexString(bytes), hash, ignoreCase: true);
        }
    }

    [Fact]
    public async Task DropCalculatorBundleHonoursPrettyPrint()
    {
        WriteDropCalculatorTables();
        var config = new ExportConfig
        {
            DropRuneTypes = ["rune"],
            DropMiscCodes = ["key"],
            DropGeneratedTreasureClassLevelStep = 3,
            DropItemRatioVersion = 1,
            DropMonsterLevelBonuses = new(StringComparer.OrdinalIgnoreCase) { ["champion"] = 2, ["superunique"] = 3 },
            DropSuperUniqueAreas = new(StringComparer.OrdinalIgnoreCase) { ["Bishibosh"] = [2] }
        };
        config.BuildLookups();
        var data = NewData(config);

        var pretty = await ExportAsync("pretty", (outputs, sources, warnings) =>
            DropCalculatorExporter.ExportAsync(outputs, sources, data, true, warnings.Add), DropCalculatorExporter.OutputPath);
        var compact = await ExportAsync("compact", (outputs, sources, warnings) =>
            DropCalculatorExporter.ExportAsync(outputs, sources, data, false, warnings.Add), DropCalculatorExporter.OutputPath);

        AssertSameDocument(pretty, compact);
        using var document = JsonDocument.Parse(compact);
        Assert.Equal(["Items", "Bases", "Ratios", "TreasureClasses", "Sources"],
            document.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal(["unique:The Gnasher", "set:Berserker's Hatchet", "rune:r01", "misc:key"],
            document.RootElement.GetProperty("Items").EnumerateArray().Select(i => i.GetProperty("Id").GetString()));
        Assert.Contains("{\"Code\":\"Act 1 Junk\",\"Group\":\"\",\"Level\":1,\"Picks\":1,\"Unique\":0,\"Set\":0,\"NoDrop\":100,"
            + "\"Entries\":[{\"Code\":\"hax\",\"Weight\":5},{\"Code\":\"r01\",\"Weight\":3}]", compact);
        // The generated itemtype class keeps its source weight (itemtypes.txt Rarity).
        Assert.Contains("{\"Code\":\"axe3\",\"Group\":\"\",\"Level\":3,\"Picks\":1,\"Unique\":0,\"Set\":0,\"NoDrop\":0,"
            + "\"Entries\":[{\"Code\":\"hax\",\"Weight\":3}]", compact);
        Assert.Contains("{\"Id\":\"skeleton1:2:0:champion\",\"NameKey\":\"Skeleton\",\"AreaKey\":\"Blood Moor\",\"Difficulty\":0,"
            + "\"Kind\":\"champion\",\"Level\":4,\"TreasureClass\":\"Act 1 Junk\"}", compact);
        Assert.Contains("\"Id\":\"super:Bishibosh:2:0\"", compact);
        // Unresolved conditions stay omitted (per-member WhenWritingNull) in both layouts.
        Assert.DoesNotContain("ConditionDifficulties", pretty);
        Assert.DoesNotContain("ConditionRuntime", pretty);
    }

    [Fact]
    public async Task AttackSpeedBundleHonoursPrettyPrintAndHashesInLowercase()
    {
        WriteAttackSpeedTables();
        var data = NewData(new ExportConfig { AttackSpeed = new AttackSpeedConfig { CurveLevels = 1 } });

        var pretty = await ExportAsync("pretty", (outputs, sources, warnings) =>
            AttackSpeedExporter.ExportAsync(outputs, sources, data, true, warnings.Add), AttackSpeedExporter.OutputPath);
        var compact = await ExportAsync("compact", (outputs, sources, warnings) =>
            AttackSpeedExporter.ExportAsync(outputs, sources, data, false, warnings.Add), AttackSpeedExporter.OutputPath);

        AssertSameDocument(pretty, compact);
        Assert.StartsWith("{\"SchemaVersion\":1,\"Sources\":[", compact);
        Assert.Contains("\"Weapons\":[{\"Code\":\"hax\",\"NameKey\":\"hax\",\"Speed\":10,\"WeaponClass\":\"1HS\"", compact);
        AssertLowercaseSourceHashes(compact, "weapons.txt", "skills.txt", "itemtypes.txt");
    }

    [Fact]
    public async Task MonsterHealthBundleHonoursPrettyPrintAndHashesInLowercase()
    {
        WriteMonsterHealthTables();
        var data = NewData(new ExportConfig
        {
            MonsterHealth = new MonsterHealthConfig
            {
                AdditionalPlayerPercent = 50, MaximumPlayers = 8, MaximumLife = 8388607,
                MinionBonusModifierId = 1, ChampionBonusModifierId = 4, UniqueBonusModifierId = 7
            }
        });

        var pretty = await ExportAsync("pretty", (outputs, sources, warnings) =>
            MonsterHealthExporter.ExportAsync(outputs, sources, data, true, warnings.Add), MonsterHealthExporter.OutputPath);
        var compact = await ExportAsync("compact", (outputs, sources, warnings) =>
            MonsterHealthExporter.ExportAsync(outputs, sources, data, false, warnings.Add), MonsterHealthExporter.OutputPath);

        AssertSameDocument(pretty, compact);
        Assert.StartsWith("{\"Rules\":{\"AdditionalPlayerPercent\":50,\"MaximumPlayers\":8,\"MaximumLife\":8388607},\"Monsters\":[", compact);
        // Null members stay omitted in both layouts (the untranslated NameKey).
        Assert.DoesNotContain("NameKey\":null", compact);
        Assert.Contains("\"Bonuses\":{\"Minion\":[300,350,400],\"Champion\":[100,150,200],\"Unique\":[200,250,300]}", compact);
        // monprop.txt is absent, so only the four parsed tables are hashed.
        AssertLowercaseSourceHashes(compact, "monstats.txt", "monlvl.txt", "levels.txt", "monumod.txt");
    }
}
