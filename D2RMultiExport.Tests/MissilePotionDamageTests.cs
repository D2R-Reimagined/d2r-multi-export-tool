// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using D2RMultiExport.Lib;
using D2RMultiExport.Lib.Config;
using D2RMultiExport.Lib.ErrorHandling;
using D2RMultiExport.Lib.Exporters;
using D2RMultiExport.Lib.Import;
using D2RMultiExport.Lib.Models;
using D2RMultiExport.Lib.Translation;
using D2RMultiExport.TestSupport;

namespace D2RMultiExport.Tests;

/// <summary>
/// Throwing potions take their damage from the <c>missiles.txt</c> row their numeric
/// <c>missiletype</c> names by engine record index. The fixture rows carry the shipped
/// (vanilla-identical) potion and cloud values, swapped names and misleading <c>*ID</c>s, so
/// a lookup by name or <c>*ID</c> exports different numbers.
/// </summary>
public sealed class MissilePotionDamageTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("d2r-missile-potions-").FullName;

    private string Excel => Path.Combine(_root, "global", "excel");

    public MissilePotionDamageTests()
    {
        Directory.CreateDirectory(Excel);
        Directory.CreateDirectory(Path.Combine(_root, "local", "lng", "strings"));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>
    /// Expected <c>DamageTypes</c> per potion: Type, AverageDamage, then each line's key and args.
    /// Gas poison is the projectile's <c>HitSubMissile1</c> cloud only, <c>floor(EMin × 50 / 256)</c>
    /// over <c>50 / 25</c> seconds (123/246/369 → 24/48/72); the projectile's own 48/144/192 must
    /// not contribute.
    /// </summary>
    public static TheoryData<string, int, string> Potions => new()
    {
        // code, engine missile index, expected DamageTypes
        { "opl", 44, "2|4.5|strWeaponDamageThrow:2,7;4|5.5|StrSkill5:3,8" },
        { "opm", 45, "2|10|strWeaponDamageThrow:8,12;4|13|StrSkill5:8,18" },
        { "ops", 46, "2|21.5|strWeaponDamageThrow:13,30;4|23.5|StrSkill5:13,34" },
        { "gpl", 47, "4|24|StrSkill8:24,24/StrSkill63:2" },
        { "gpm", 48, "4|48|StrSkill8:48,48/StrSkill63:2" },
        { "gps", 49, "4|72|StrSkill8:72,72/StrSkill63:2" },
    };

    [Theory]
    [MemberData(nameof(Potions))]
    public async Task MissilePotionsExportTheirEngineIndexedMissileDamage(string code, int missileIndex, string expected)
    {
        WriteFixture();
        var (data, weapons) = await ExportAsync();

        Assert.Equal(missileIndex, data.Weapons[code].MissileType);
        var potion = weapons[code];
        Assert.Equal(expected, Describe(potion.GetProperty("DamageTypes")));
        Assert.DoesNotContain(Keys(potion.GetProperty("Lines")), static key => key == SyntheticStringRegistry.Keys.Indestructible);
    }

    [Fact]
    public async Task MissileLookupUsesTheEngineRowNotTheNameOrInformationalId()
    {
        WriteFixture();
        var (data, _) = await ExportAsync();

        // The parser-skipped blank line counts as a record and the Expansion marker does not.
        Assert.Equal("oilpotion", data.MissilesByIndex[44].MissileName);
        Assert.Equal("stranglinggaspotion", data.MissilesByIndex[49].MissileName);
        Assert.Equal(1044, data.MissilesByIndex[44].MissileId);
        Assert.False(data.MissilesByIndex.ContainsKey(1044));
        Assert.Equal(data.MissilesByIndex.Count, data.Missiles.Count - 1);
        Assert.Equal(48, data.MissilesByIndex[47].ElementalMin);
        Assert.Equal(123, data.Missiles["rancidgascloud"].ElementalMin);
    }

    /// <summary>Each gas potion's projectile names its cloud, the row whose poison is applied.</summary>
    [Theory]
    [InlineData("gpl", "rancidgascloud", 123)]
    [InlineData("gpm", "chokinggascloud", 246)]
    [InlineData("gps", "stranglinggascloud", 369)]
    public async Task GasPotionsFollowTheirProjectilesHitSubMissile(string code, string cloud, int rate)
    {
        WriteFixture();
        var (data, _) = await ExportAsync();

        var projectile = data.MissilesByIndex[data.Weapons[code].MissileType!.Value];
        Assert.Equal(cloud, projectile.HitSubMissile1);
        Assert.Equal(rate, data.Missiles[cloud].ElementalMin);
        Assert.Equal(rate, data.Missiles[cloud].ElementalMax);
        Assert.Equal(50, data.Missiles[cloud].ElementalLength);
    }

    [Fact]
    public async Task GasPotionWithAnUnresolvableCloudExportsNoPoisonInsteadOfTheProjectiles()
    {
        WriteFixture(includeClouds: false);
        var (_, weapons) = await ExportAsync();

        foreach (var code in new[] { "gpl", "gpm", "gps" })
        {
            Assert.Equal("", Describe(weapons[code].GetProperty("DamageTypes")));
        }
        Assert.Equal("2|4.5|strWeaponDamageThrow:2,7;4|5.5|StrSkill5:3,8", Describe(weapons["opl"].GetProperty("DamageTypes")));
    }

    [Fact]
    public async Task OtherWeaponsKeepTheirDamageAndDurabilityLines()
    {
        WriteFixture();
        var (_, weapons) = await ExportAsync();

        var knife = weapons["tkf"];
        Assert.Equal("3|2.5|strWeaponDamageGeneric:2,3;2|6.5|strWeaponDamageThrow:4,9", Describe(knife.GetProperty("DamageTypes")));
        Assert.Contains(Keys(knife.GetProperty("Lines")), static key => key == SyntheticStringRegistry.Keys.Durability);

        // nodurability outside the configured potion types still reads Indestructible.
        var bow = weapons["sbw"];
        Assert.Equal("1|3.5|strWeaponDamageTwoHand:1,6", Describe(bow.GetProperty("DamageTypes")));
        Assert.Contains(Keys(bow.GetProperty("Lines")), static key => key == SyntheticStringRegistry.Keys.Indestructible);
    }

    [Fact]
    public void GasPoisonTruncatesTheAccumulatedFixedPointDamage()
    {
        // Cloud rates over 50 frames: 6150/12300/18450 256ths of a hit point.
        Assert.Equal([24, 48, 72], new[] { 123, 246, 369 }.Select(static rate => MissilePotionDamage.AppliedPoisonDamage(rate, 50)));
        Assert.Equal(2d, PropertyKeyResolver.PoisonDurationSeconds(50));
        // The item-stat tooltip rule rounds up; it stays unchanged and is not used here.
        Assert.Equal([25, 49, 73], new[] { 123, 246, 369 }.Select(static rate => PropertyKeyResolver.PoisonTotalDamage(rate, 50)));
        Assert.Equal(1, MissilePotionDamage.AppliedPoisonDamage(256, 1));
        Assert.Equal(0, MissilePotionDamage.AppliedPoisonDamage(255, 1));
    }

    /// <summary>The six potions on the real mod tables, against the shipped missile rows.</summary>
    [RealDataFact]
    [Trait("Category", "RealData")]
    public async Task RealDataMissilePotionsMatchTheirMissileRows()
    {
        var sources = RealDataInputs.RequireSources();
        var data = await NewDataAsync();
        await new DataLoader(sources, data, new PipelineResult()).LoadAllAsync();
        var outputs = new ExportOutputs(Path.Combine(_root, "out"));
        await KeyedJsonExporter.ExportAsync(outputs, data, false);
        var weapons = await ReadWeaponsAsync(outputs.GetFullPath("keyed/weapons.json"));

        foreach (var (code, index, expected) in Potions.Select(static row => ((string)row[0], (int)row[1], (string)row[2])))
        {
            Assert.Equal(index, data.Weapons[code].MissileType);
            Assert.Equal(expected, Describe(weapons[code].GetProperty("DamageTypes")));
        }
    }

    private static async Task<GameData> NewDataAsync() => new()
    {
        ExportConfig = await ExportConfig.LoadAsync(Path.Combine(AppContext.BaseDirectory, "Config", "export-config.json")),
        StatOverrideConfig = new StatOverrideConfig(),
        Translations = new TranslationService()
    };

    private void WriteFixture(bool includeClouds = true)
    {
        File.WriteAllText(Path.Combine(Excel, "weapons.txt"), string.Join("\r\n",
            "name\tcode\tnamestr\ttype\tmindam\tmaxdam\t2handmindam\t2handmaxdam\tminmisdam\tmaxmisdam\tmissiletype\tdurability\tnodurability\tlevelreq",
            "Rancid Gas Potion\tgps\tgps\ttpot\t0\t1\t\t\t\t\t49\t2\t1\t24",
            "Oil Potion\tops\tops\ttpot\t0\t1\t\t\t\t\t46\t2\t1\t20",
            "Choking Gas Potion\tgpm\tgpm\ttpot\t0\t1\t\t\t\t\t48\t2\t1\t16",
            "Exploding Potion\topm\topm\ttpot\t0\t1\t\t\t\t\t45\t2\t1\t12",
            "Strangling Gas Potion\tgpl\tgpl\ttpot\t0\t1\t\t\t\t\t47\t2\t1\t6",
            "Fulminating Potion\topl\topl\ttpot\t0\t1\t\t\t\t\t44\t2\t1\t2",
            "Throwing Knife\ttkf\ttkf\ttkni\t2\t3\t\t\t4\t9\t36\t250\t\t0",
            "Short Bow\tsbw\tsbw\tbow\t\t\t1\t6\t\t\t5\t250\t1\t0") + "\r\n");

        const string header = "Missile\t*ID\tpSrvHitFunc\tsHitPar1\tHitSubMissile1\tMinDamage\tMaxDamage\tEType\tEMin\tEMax\tELen\tHitShift";
        List<string> lines = [header];
        for (var index = 0; index < 44; index++)
        {
            if (index == 20)
            {
                lines.Add("Expansion");
            }
            // A blank line consumes a record index even though the parser skips it.
            lines.Add(index == 10 ? "" : $"filler{index}\t{1000 + index}\t\t\t\t\t\t\t\t\t\t8");
        }
        lines.AddRange(
        [
            "oilpotion\t1044\t3\t2\t\t2\t7\tfire\t3\t8\t\t8",
            "explosivepotion\t1045\t3\t3\t\t8\t12\tfire\t8\t18\t\t8",
            "fulminatingpotion\t1046\t3\t6\t\t13\t30\tfire\t13\t34\t\t8",
            "rancidgasepotion\t1047\t2\t0\trancidgascloud\t\t\tpois\t48\t48\t50\t8",
            "chokinggaspoition\t1048\t2\t2\tchokinggascloud\t\t\tpois\t144\t144\t50\t8",
            "stranglinggaspotion\t1049\t2\t1\tstranglinggascloud\t\t\tpois\t192\t192\t50\t8"
        ]);
        if (includeClouds)
        {
            // Blank HitShift, as shipped.
            lines.AddRange(
            [
                "rancidgascloud\t1050\t\t\t\t\t\tpois\t123\t123\t50\t",
                "chokinggascloud\t1051\t\t\t\t\t\tpois\t246\t246\t50\t",
                "stranglinggascloud\t1052\t\t\t\t\t\tpois\t369\t369\t50\t"
            ]);
        }
        File.WriteAllText(Path.Combine(Excel, "missiles.txt"), string.Join("\r\n", lines) + "\r\n");
    }

    private async Task<(GameData Data, Dictionary<string, JsonElement> Weapons)> ExportAsync()
    {
        var data = await NewDataAsync();
        await new DataLoader(SourceRoots.Create(_root, null), data, new PipelineResult()).LoadAllAsync();
        var outputs = new ExportOutputs(Path.Combine(_root, "out"));
        await KeyedJsonExporter.ExportAsync(outputs, data, false);
        return (data, await ReadWeaponsAsync(outputs.GetFullPath("keyed/weapons.json")));
    }

    private static async Task<Dictionary<string, JsonElement>> ReadWeaponsAsync(string path)
    {
        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(path));
        return document.RootElement.EnumerateArray()
            .Select(static row => row.Clone())
            .ToDictionary(static row => row.GetProperty("NameKey").GetString()!, StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string?> Keys(JsonElement lines) =>
        lines.EnumerateArray().Select(static line => line.GetProperty("key").GetString());

    /// <summary>Compact wire description: <c>Type|AverageDamage|key:args/key:args;…</c>.</summary>
    private static string Describe(JsonElement damageTypes) => string.Join(";",
        damageTypes.EnumerateArray().Select(static damage =>
            $"{damage.GetProperty("Type").GetInt32()}|{damage.GetProperty("AverageDamage").GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture)}|"
            + string.Join("/", damage.GetProperty("Lines").EnumerateArray().Select(static line =>
                $"{line.GetProperty("key").GetString()}:{string.Join(",", line.GetProperty("args").EnumerateArray().Select(static arg => arg.GetRawText()))}"))));
}
