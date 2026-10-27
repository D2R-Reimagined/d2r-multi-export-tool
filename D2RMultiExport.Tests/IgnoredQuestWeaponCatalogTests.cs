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
/// The bundled <c>ignoredUniqueItems</c> list drops the unwanted quest weapons from the base
/// <c>keyed/weapons.json</c> catalog by their resolved English names, while the weapon
/// lookups keep every row and quest-flagged weapons that are not listed stay eligible.
/// </summary>
public sealed class IgnoredQuestWeaponCatalogTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("d2r-quest-weapons-").FullName;

    private string Excel => Path.Combine(_root, "global", "excel");

    public IgnoredQuestWeaponCatalogTests()
    {
        Directory.CreateDirectory(Excel);
        Directory.CreateDirectory(Path.Combine(_root, "local", "lng", "strings"));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>weapons.txt code → enUS item-names.json text, as verified against the mod sources.</summary>
    private static readonly Dictionary<string, string> ExcludedQuestWeapons = new(StringComparer.OrdinalIgnoreCase)
    {
        ["d33"] = "Decoy Gidbinn",
        ["g33"] = "The Gidbinn",
        ["leg"] = "Wirt's Leg",
        ["hdm"] = "Horadric Malus",
        ["qf1"] = "Khalim's Flail",
        ["qf2"] = "Khalim's Will"
    };

    [Fact]
    public async Task ConfiguredQuestWeaponsAreOmittedFromTheCatalogButUnlistedQuestWeaponsRemain()
    {
        File.WriteAllText(Path.Combine(Excel, "weapons.txt"), string.Join("\r\n",
        [
            "name\tcode\tnamestr\tspawnable\tquest\tlevel",
            .. ExcludedQuestWeapons.Select(static w => $"{w.Value}\t{w.Key}\t{w.Key}\t0\t1\t1"),
            "Test Quest Blade\ttqb\ttqb\t0\t5\t1",
            "Axe\taxe\taxe\t1\t\t1"
        ]) + "\r\n");
        File.WriteAllText(Path.Combine(Excel, "uniqueitems.txt"), string.Join("\r\n",
            "index\t*ID\tspawnable\tlvl\tlvl req\tcode",
            "KhalimFlail\t0\t1\t1\t1\tqf1",
            "SuperKhalimFlail\t1\t1\t1\t1\tqf2",
            "Test Quest Unique\t2\t1\t1\t1\ttqb") + "\r\n");

        var config = await ExportConfig.LoadAsync(Path.Combine(AppContext.BaseDirectory, "Config", "export-config.json"));
        var data = new GameData { ExportConfig = config, StatOverrideConfig = new StatOverrideConfig(), Translations = new TranslationService() };
        data.Translations.MergeSeed(new Dictionary<string, string>(ExcludedQuestWeapons)
        {
            ["tqb"] = "Test Quest Blade",
            ["axe"] = "Axe"
        });
        await new DataLoader(SourceRoots.Create(_root, null), data, new PipelineResult()).LoadAllAsync();

        // The underlying lookups keep every weapon; only the catalog output is filtered.
        foreach (var (code, name) in ExcludedQuestWeapons)
            Assert.Equal(name, data.Weapons[code].Name);
        Assert.True(data.Weapons.ContainsKey("tqb"));

        var uniques = await new UniqueImporter(SourceRoots.Create(_root, null), data).ImportAsync();
        Assert.Equal(["Test Quest Unique"], uniques.Items.Select(static u => u.Index));

        await KeyedJsonExporter.ExportAsync(new ExportOutputs(_root), data, false);
        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(_root, "keyed", "weapons.json")));
        var exported = document.RootElement.EnumerateArray()
            .Select(static row => row.GetProperty("NameKey").GetString())
            .ToList();

        Assert.Equal(["axe", "tqb"], exported.Order(StringComparer.Ordinal));
        Assert.DoesNotContain(exported, static code => code is not null && ExcludedQuestWeapons.ContainsKey(code));
    }
}
