// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Text.Json;
using D2RMultiExport.Lib;
using D2RMultiExport.Lib.Config;
using D2RMultiExport.Lib.Exporters;
using D2RMultiExport.Lib.Models;
using D2RMultiExport.Lib.Translation;

namespace D2RMultiExport.Tests;

/// <summary>Final-JSON regressions for named artwork, contained paths, layering and fallbacks.</summary>
public sealed class ItemPresentationExporterTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("d2r-item-art-").FullName;
    private readonly List<string> _info = [];

    private string Mod => Path.Combine(_root, "mod");
    private string Casc => Path.Combine(_root, "casc");
    private string Output => Path.Combine(_root, "output");

    public ItemPresentationExporterTests()
    {
        string[] roots = [Mod, Casc];
        foreach (var root in roots)
        {
            Directory.CreateDirectory(Path.Combine(root, "global", "excel"));
            Directory.CreateDirectory(Path.Combine(root, "local", "lng", "strings"));
        }
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData("Griswold's Edge", "griswolds_edge")]
    [InlineData("Griswold’s Edge", "griswolds_edge")]
    [InlineData("Moser's Blessed Circle1", "mosers_blessed_circle_1")]
    [InlineData("The Minotaur1", "the_minotaur_1")]
    [InlineData("Ars Al'Diablolos", "ars_aldiablolos")]
    [InlineData("Ars Tor'Baalos", "ars_torbaalos")]
    [InlineData("Ars Dul'Mephistos", "ars_dulmephistos")]
    [InlineData("Que-Hegan's Wisdom1", "que_hegans_wisdom_1")]
    public async Task CompatibilityNamesSelectTheirNamedArtwork(string name, string key)
    {
        await WriteManifestsAsync(Mod, [Base("x", "sword/base")], [Variant(key, "sword/custom")]);
        await WriteSpriteAsync(Mod, "weapon", "sword/base");
        await WriteSpriteAsync(Mod, "weapon", "sword/custom", 200);

        var (json, _, diagnostics) = await ExportAsync(WeaponData(name));

        Assert.Equal("sprites/items/weapon-sword-custom.webp", UniqueSprite(json));
        Assert.Empty(diagnostics.Entries);
    }

    [Fact]
    public async Task ExistingNormalizedMatchPrecedesCompatibilityAliases()
    {
        await WriteManifestsAsync(Mod, [Base("x", "sword/base")],
            [Variant("griswold_s_edge", "sword/exact"), Variant("griswolds_edge", "sword/alias")]);
        await WriteSpriteAsync(Mod, "weapon", "sword/base");
        await WriteSpriteAsync(Mod, "weapon", "sword/exact");

        var (json, _, diagnostics) = await ExportAsync(WeaponData("Griswold's Edge"));

        Assert.Equal("sprites/items/weapon-sword-exact.webp", UniqueSprite(json));
        Assert.Empty(diagnostics.Entries);
    }

    [Fact]
    public async Task AmbiguousAliasesAreRejectedAndExactMatchesStillWin()
    {
        await WriteManifestsAsync(Mod, [Base("x", "sword/base")],
            [Variant("makers_mark1", "sword/first"), Variant("makers_mark_1", "sword/second")]);
        await WriteSpriteAsync(Mod, "weapon", "sword/base");
        await WriteSpriteAsync(Mod, "weapon", "sword/second");
        var data = WeaponData("Maker's Mark1");
        data.Uniques.Add(new UniqueExport { Index = "Makers Mark 1", Code = "x", FileIndex = 2 });

        var (json, _, diagnostics) = await ExportAsync(data);

        Assert.Equal("sprites/items/weapon-sword-base.webp", UniqueSprite(json));
        Assert.Equal("sprites/items/weapon-sword-second.webp", UniqueSprite(json, 1));
        var entry = Assert.Single(diagnostics.Entries);
        Assert.Equal(AssetIssueKind.Fallback, entry.Kind);
        Assert.Equal("ambiguous named artwork alias", entry.Reason);
        Assert.Contains("makers_mark1, makers_mark_1", entry.Mapping);
        Assert.Contains("Maker's Mark1", entry.Identifier);
    }

    [Theory]
    [InlineData("Foo-Bar", "foobar")]
    [InlineData("Blade2", "blade_1")]
    [InlineData("Blade1 of Fire", "blade_1_of_fire")]
    public async Task OtherPunctuationAndNumbersAreNotCollapsed(string name, string key)
    {
        await WriteManifestsAsync(Mod, [Base("x", "sword/base")], [Variant(key, "sword/other")]);
        await WriteSpriteAsync(Mod, "weapon", "sword/base");

        var (json, _, diagnostics) = await ExportAsync(WeaponData(name));

        Assert.Equal("sprites/items/weapon-sword-base.webp", UniqueSprite(json));
        Assert.Equal("no named artwork mapping (mod or CASC)", Assert.Single(diagnostics.Entries).Reason);
    }

    [Theory]
    [InlineData("Head Hunter's Glory", "head_hunters_glory", "headhunters_glory")]
    [InlineData("Blood Raven's Charge", "blood_ravens_charge", "bloodravens_charge")]
    public async Task WordSeparatorsDistinguishTheDemonstratedManifestSpellings(string name, string key, string compactKey)
    {
        await WriteManifestsAsync(Mod, [Base("x", "sword/base")],
            [Variant(key, "sword/correct"), Variant(compactKey, "sword/compact")]);
        await WriteSpriteAsync(Mod, "weapon", "sword/base");
        await WriteSpriteAsync(Mod, "weapon", "sword/correct");

        var (json, _, diagnostics) = await ExportAsync(WeaponData(name));

        Assert.Equal("sprites/items/weapon-sword-correct.webp", UniqueSprite(json));
        Assert.Empty(diagnostics.Entries);
    }

    [Fact]
    public async Task ModOverridesAndDuplicatesWinWhileOtherCascMappingsRemainAvailable()
    {
        await WriteManifestsAsync(Casc, [Base("x", "sword/base")],
            [Variant("griswolds_edge", "sword/casc"), Variant("unchanged", "sword/vanilla")]);
        await WriteManifestsAsync(Mod, [],
            [Variant("griswolds_edge", "sword/earlier"), Variant("griswolds_edge", "sword/mod")]);
        await WriteSpriteAsync(Casc, "weapon", "sword/base");
        await WriteSpriteAsync(Casc, "weapon", "sword/vanilla");
        await WriteSpriteAsync(Casc, "weapon", "sword/mod", 1);
        await WriteSpriteAsync(Mod, "weapon", "sword/mod", 200);
        var data = WeaponData("Griswold's Edge");
        data.Uniques.Add(new UniqueExport { Index = "Unchanged", Code = "x", FileIndex = 7 });

        var (json, outputs, diagnostics) = await ExportAsync(data);

        Assert.Equal("sprites/items/weapon-sword-mod.webp", UniqueSprite(json));
        Assert.Equal("sprites/items/weapon-sword-vanilla.webp", UniqueSprite(json, 1));
        var expected = new ExportOutputs(Path.Combine(_root, "expected"));
        await ItemPresentationExporter.ConvertSpriteAsync(expected,
            Path.Combine(Mod, "hd/global/ui/items/weapon/sword/mod.sprite"), "mod.webp");
        Assert.Equal(await File.ReadAllBytesAsync(expected.GetFullPath("mod.webp")),
            await File.ReadAllBytesAsync(outputs.GetFullPath(UniqueSprite(json))));
        Assert.Empty(diagnostics.Entries);
    }

    [Fact]
    public async Task SetVariantsUseTheSameCompatibilityLookupAndKeepTheirIdentity()
    {
        await WriteManifestsAsync(Mod, [Base("x", "sword/base")], [],
            [Variant("isenharts_case", "sword/set")]);
        await WriteSpriteAsync(Mod, "weapon", "sword/base");
        await WriteSpriteAsync(Mod, "weapon", "sword/set");
        var data = WeaponData();
        data.Sets.Add(new SetExport
        {
            Index = "Test Set",
            SetItems = [new SetItemExport { Index = "Isenhart's Case", Code = "x", FileIndex = 15 }]
        });

        var (json, _, diagnostics) = await ExportAsync(data);
        var variant = json[0].GetProperty("SetSprites")[0];

        Assert.Equal(15, variant.GetProperty("FileIndex").GetInt32());
        Assert.Equal("Isenhart's Case", variant.GetProperty("NameKey").GetString());
        Assert.Equal("sprites/items/weapon-sword-set.webp", variant.GetProperty("Sprite").GetString());
        Assert.Empty(diagnostics.Entries);
    }

    [Theory]
    [InlineData("normal", "sword/normal")]
    [InlineData("uber", "sword/uber")]
    [InlineData("ultra", "sword/uber")]
    public async Task ExistingTierSelectionAndMissingTierFallbackArePreserved(string tier, string expected)
    {
        await WriteManifestsAsync(Mod, [Base("x", "sword/base")],
            [Variant("exact", "sword/normal", "sword/uber")]);
        await WriteSpriteAsync(Mod, "weapon", "sword/base");
        await WriteSpriteAsync(Mod, "weapon", "sword/normal");
        await WriteSpriteAsync(Mod, "weapon", "sword/uber");
        var data = WeaponData("Exact");
        data.Weapons["x"] = new EquipmentEntry
        {
            Code = "x", NameStr = "x", EquipmentType = EquipmentType.Weapon,
            NormCode = "x", UberCode = tier == "uber" ? "x" : "y", UltraCode = tier == "ultra" ? "x" : "z"
        };

        var (json, _, diagnostics) = await ExportAsync(data);

        Assert.Equal($"sprites/items/weapon-{expected.Replace('/', '-')}.webp", UniqueSprite(json));
        Assert.Empty(diagnostics.Entries);
    }

    [Theory]
    [InlineData("sword/item", "weapon/sword/item")]
    [InlineData("../misc/body_part/fragment_cold", "misc/body_part/fragment_cold")]
    [InlineData("..\\misc\\body_part\\fragment_cold", "misc/body_part/fragment_cold")]
    [InlineData("./sword/../axe/ITEM", "weapon/axe/item")]
    public void ContainedPathsAreCanonicalized(string asset, string expected)
    {
        Assert.Equal(expected, ItemPresentationExporter.CanonicalizeAssetPath("weapon", asset));
    }

    [Fact]
    public void GeneralSourceRootTraversalRestrictionsRemainUnchanged()
    {
        Assert.Throws<ArgumentException>(() => SourceRoots.SplitRelative("hd/global/ui/items/weapon/../misc/item.sprite"));
        Assert.Throws<ArgumentException>(() => ItemPresentationExporter.CanonicalizeAssetPath("weapon/subdirectory", "item"));
    }

    [Theory]
    [InlineData("../../misc/item")]
    [InlineData("sword/../../../misc/item")]
    [InlineData("/misc/item")]
    [InlineData("\\misc\\item")]
    [InlineData("C:/misc/item")]
    [InlineData("C:relative")]
    [InlineData("\\\\server\\share\\item")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("sword//item")]
    [InlineData("sword/item/")]
    [InlineData("sword/item.")]
    [InlineData("sword/item ")]
    [InlineData("sword/it?em")]
    [InlineData("sword/it\0em")]
    [InlineData("sword/..")]
    public void EscapingAbsoluteAndMalformedPathsAreRejected(string asset)
    {
        Assert.Throws<ArgumentException>(() => ItemPresentationExporter.CanonicalizeAssetPath("weapon", asset));
    }

    [Fact]
    public async Task RelativeVariantsReuseCanonicalMiscSpritesWithoutDuplicateConversions()
    {
        await WriteManifestsAsync(Mod,
            [Base("x", "sword/base"), Base("um1", "body_part/fragment_cold")],
            [Variant("protectors_frost", "../misc/body_part/fragment_cold")]);
        await WriteSpriteAsync(Mod, "weapon", "sword/base");
        await WriteSpriteAsync(Mod, "misc", "body_part/fragment_cold");
        var data = WeaponData("Protector's Frost");
        data.MiscItems.Add("um1", new MiscEntry { Code = "um1", NameStr = "um1" });

        var (json, outputs, diagnostics) = await ExportAsync(data);
        const string expected = "sprites/items/misc-body_part-fragment_cold.webp";

        Assert.Equal(expected, UniqueSprite(json));
        Assert.Equal(expected, json[1].GetProperty("Sprite").GetString());
        Assert.Equal(2, outputs.Files.Count(static file => file.Path.StartsWith("sprites/items/", StringComparison.Ordinal)));
        Assert.DoesNotContain(outputs.Files, static file => file.Path.Contains("..", StringComparison.Ordinal));
        Assert.Contains(_info, static line => line.Contains("2 sprite file(s) converted", StringComparison.Ordinal));
        Assert.Empty(diagnostics.Entries);
    }

    [Theory]
    [InlineData("missing", "no named artwork mapping")]
    [InlineData("empty", "has no 'normal' sprite")]
    [InlineData("unavailable", "exists in neither")]
    [InlineData("invalid", "could not be decoded")]
    [InlineData("escaping", "invalid asset mapping")]
    public async Task NamedArtworkFailuresReportTheItemMappingReasonAndBaseFallback(string failure, string reason)
    {
        var asset = failure switch
        {
            "empty" => null,
            "escaping" => "../../outside",
            _ => "sword/custom"
        };
        await WriteManifestsAsync(Mod, [Base("x", "sword/base")],
            failure == "missing" ? [] : [Variant("expected", asset)]);
        await WriteSpriteAsync(Mod, "weapon", "sword/base");
        if (failure == "invalid")
        {
            await WriteAsync(Mod, "hd/global/ui/items/weapon/sword/custom.sprite", "not a sprite");
        }

        var (json, _, diagnostics) = await ExportAsync(WeaponData("Expected"));
        var entry = Assert.Single(diagnostics.Entries);

        Assert.Equal("sprites/items/weapon-sword-base.webp", UniqueSprite(json));
        Assert.Equal(AssetIssueKind.Fallback, entry.Kind);
        Assert.Contains("unique 'Expected' (x)", entry.Identifier);
        Assert.Contains("hd/items/uniques.json#expected", entry.Mapping);
        Assert.Contains(reason, entry.Reason);
        Assert.Equal("base item sprite sprites/items/weapon-sword-base.webp", entry.Fallback);
        Assert.Contains(entry.Identifier, diagnostics.Render());
        if (failure is "unavailable" or "invalid")
        {
            Assert.Contains(Mod, entry.Searched);
            Assert.NotNull(entry.Source);
        }
    }

    [Fact]
    public async Task ExplicitlySharedCanonicalArtworkIsNotReportedAsAnError()
    {
        await WriteManifestsAsync(Mod, [Base("x", "../misc/body_part/fragment_cold")],
            [Variant("shared", "../misc/./body_part/fragment_cold")]);
        await WriteSpriteAsync(Mod, "misc", "body_part/fragment_cold");

        var (json, outputs, diagnostics) = await ExportAsync(WeaponData("Shared"));

        Assert.Equal(json[0].GetProperty("Sprite").GetString(), UniqueSprite(json));
        Assert.Single(outputs.Files, static file => file.Path.StartsWith("sprites/items/", StringComparison.Ordinal));
        Assert.Contains(_info, static line => line.Contains("1 sprite file(s) converted", StringComparison.Ordinal));
        Assert.Empty(diagnostics.Entries);
    }

    [Fact]
    public async Task MissingNamedAndBaseArtworkRemainsAnUnresolvedDiagnostic()
    {
        await WriteManifestsAsync(Mod, [], []);

        var (json, _, diagnostics) = await ExportAsync(WeaponData("Expected"));

        Assert.False(json[0].GetProperty("UniqueSprites")[0].TryGetProperty("Sprite", out _));
        var entry = Assert.Single(diagnostics.Entries, static entry => entry.Identifier.StartsWith("unique", StringComparison.Ordinal));
        Assert.Equal(AssetIssueKind.Missing, entry.Kind);
        Assert.Equal("none", entry.Fallback);
        var warnings = new List<string>();
        diagnostics.Summarize(ItemPresentationExporter.Producer, warnings.Add);
        Assert.Contains(warnings, static warning => warning.Contains("no named artwork mapping", StringComparison.Ordinal));
    }

    private static GameData WeaponData(string? unique = null)
    {
        var data = new GameData
        {
            ExportConfig = new ExportConfig(),
            StatOverrideConfig = new StatOverrideConfig(),
            Translations = new TranslationService()
        };
        data.Weapons.Add("x", new EquipmentEntry
        {
            Code = "x", NameStr = "x", EquipmentType = EquipmentType.Weapon, NormCode = "x"
        });
        if (unique is not null)
        {
            data.Uniques.Add(new UniqueExport { Index = unique, Code = "x", FileIndex = 999 });
        }
        return data;
    }

    private async Task<(JsonElement Json, ExportOutputs Outputs, AssetDiagnostics Diagnostics)> ExportAsync(GameData data)
    {
        var outputs = new ExportOutputs(Output);
        var diagnostics = new AssetDiagnostics();
        await ItemPresentationExporter.ExportAsync(outputs, SourceRoots.Create(Mod, Casc), data, true, diagnostics, _ => { }, _info.Add);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(outputs.GetFullPath(ItemPresentationExporter.OutputPath)));
        return (document.RootElement.Clone(), outputs, diagnostics);
    }

    private static string UniqueSprite(JsonElement json, int index = 0) =>
        json[0].GetProperty("UniqueSprites")[index].GetProperty("Sprite").GetString()!;

    private static object Base(string code, string asset) => new Dictionary<string, object> { [code] = new { asset } };

    private static object Variant(string key, string? asset, string? uber = null) =>
        new Dictionary<string, object> { [key] = new { normal = asset, uber } };

    private static async Task WriteManifestsAsync(string root, object[] items, object[] uniques, object[]? sets = null)
    {
        await WriteAsync(root, "hd/items/items.json", JsonSerializer.Serialize(items));
        await WriteAsync(root, "hd/items/uniques.json", JsonSerializer.Serialize(uniques));
        await WriteAsync(root, "hd/items/sets.json", JsonSerializer.Serialize(sets ?? []));
    }

    private static async Task WriteAsync(string root, string relative, string content)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content);
    }

    private static async Task WriteSpriteAsync(string root, string category, string asset, byte red = 50)
    {
        var bytes = new byte[44];
        "SpA1"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), 31);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), 1);
        bytes[40] = red;
        bytes[43] = 255;
        var path = Path.Combine(root, $"hd/global/ui/items/{category}/{asset}.sprite");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, bytes);
    }
}
