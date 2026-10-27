// SPDX-License-Identifier: GPL-3.0-or-later

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using D2RMultiExport.Lib.Comparison;

namespace D2RMultiExport.Tests;

/// <summary>Manifest validation and keyed snapshot comparison regressions.</summary>
public sealed class SnapshotComparisonTests : IDisposable
{
    private const string CubeRecipePath = "keyed/cube-recipes.json";
    private const string UniquePath = "keyed/uniques.json";
    private const string SetPath = "keyed/sets.json";
    private const string RunewordPath = "keyed/runewords.json";
    private const string ArmorPath = "keyed/armors.json";
    private const string WeaponPath = "keyed/weapons.json";
    private const string MagicPrefixPath = "keyed/magicprefix.json";
    private const string MagicSuffixPath = "keyed/magicsuffix.json";

    private static readonly string[] CategoryFiles =
        [CubeRecipePath, UniquePath, SetPath, RunewordPath, ArmorPath, WeaponPath, MagicPrefixPath, MagicSuffixPath];

    private readonly string _root = Directory.CreateTempSubdirectory("d2r-snapshot-comparison-").FullName;

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task CubeRecipesStillMatchByStableIdAndIgnoreRowIndexShifts()
    {
        var oldSnapshot =
            await CreateSnapshotAsync("old", cubeRecipes: "[{\"Id\":\"same\",\"Index\":1,\"Enabled\":true}]");
        var newSnapshot =
            await CreateSnapshotAsync("new", cubeRecipes: "[{\"Id\":\"same\",\"Index\":7,\"Enabled\":true}]");

        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);
        var cube = GetCategory(report, "cube-recipes");

        Assert.True(report.Succeeded);
        Assert.Equal(1, cube.Counts.Unchanged);
        Assert.Equal(1, cube.Counts.Reindexed);
        Assert.Equal(0, cube.Counts.Modified);
    }

    [Fact]
    public async Task CubeRecipeAddRemoveAndStructuralModificationArePreserved()
    {
        var oldSnapshot = await CreateSnapshotAsync("old",
            cubeRecipes:
            "[{\"Id\":\"removed\",\"Index\":1},{\"Id\":\"changed\",\"Outputs\":{\"A\":{\"Quantity\":1}}}]");
        var newSnapshot = await CreateSnapshotAsync("new",
            cubeRecipes: "[{\"Id\":\"added\",\"Index\":4},{\"Id\":\"changed\",\"Outputs\":{\"A\":{\"Quantity\":2}}}]");

        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);
        var cube = GetCategory(report, "cube-recipes");

        Assert.Equal((1, 1, 1), (cube.Counts.Added, cube.Counts.Removed, cube.Counts.Modified));
        Assert.Equal("added", Assert.Single(cube.AddedRecords).Key);
        Assert.Equal("removed", Assert.Single(cube.RemovedRecords).Key);
        Assert.Equal("Outputs.A.Quantity", Assert.Single(Assert.Single(cube.ModifiedRecords).Changes).Path);
    }

    [Fact]
    public async Task CubeFormattingAndObjectPropertyOrderDoNotCreateChanges()
    {
        const string oldRecipes =
            "[{\"Id\":\"same\",\"Index\":1,\"Enabled\":true,\"Outputs\":{\"A\":{\"Quantity\":2,\"Name\":\"item\"}}}]";
        const string newRecipes =
            "[ { \"Outputs\": { \"A\": { \"Name\": \"item\", \"Quantity\": 2.0 } }, \"Enabled\": true, \"Index\": 1, \"Id\": \"same\" } ]";
        var oldSnapshot = await CreateSnapshotAsync("old", cubeRecipes: oldRecipes);
        var newSnapshot = await CreateSnapshotAsync("new", cubeRecipes: newRecipes);

        var cube = GetCategory(await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot), "cube-recipes");

        Assert.Equal(0, cube.Counts.Modified);
        Assert.Equal(1, cube.Counts.Unchanged);
    }

    [Fact]
    public async Task UniqueAddRemoveAndUnchangedUseStringIndexIdentity()
    {
        var oldSnapshot = await CreateSnapshotAsync("old", uniques:
            "[{\"Index\":\"Old Unique\",\"Code\":\"old\"},{\"Index\":\"Kept Unique\",\"Enabled\":true}]");
        var newSnapshot = await CreateSnapshotAsync("new", uniques:
            "[{\"Index\":\"New Unique\",\"Code\":\"new\"},{\"Index\":\"Kept Unique\",\"Enabled\":true}]");

        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);
        var uniques = GetCategory(report, "uniques");

        Assert.Equal((1, 1, 0, 1),
            (uniques.Counts.Added, uniques.Counts.Removed, uniques.Counts.Modified, uniques.Counts.Unchanged));
        Assert.Equal("New Unique", Assert.Single(uniques.AddedRecords).Key);
        Assert.Equal("Old Unique", Assert.Single(uniques.RemovedRecords).Key);
        Assert.Equal("old", Assert.Single(uniques.RemovedRecords).Record.GetProperty("Code").GetString());
    }

    [Fact]
    public async Task UniquePropertyAndNumericRangeChangesIncludeFieldDiffAndBeforeAfterRecords()
    {
        const string oldUniques =
            "[{\"Index\":\"Unique\",\"Lines\":[{\"key\":\"strDamage\",\"args\":[10,20]}],\"Equipment\":{\"NormCode\":\"abc\"}}]";
        const string newUniques =
            "[{\"Index\":\"Unique\",\"Lines\":[{\"key\":\"strEnhancedDamage\",\"args\":[10,25]}],\"Equipment\":{\"NormCode\":\"def\"}}]";
        var oldSnapshot = await CreateSnapshotAsync("old", uniques: oldUniques);
        var newSnapshot = await CreateSnapshotAsync("new", uniques: newUniques);

        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);
        var uniques = GetCategory(report, "uniques");
        var modified = Assert.Single(uniques.ModifiedRecords);

        Assert.Equal(1, uniques.Counts.Modified);
        Assert.Contains(modified.Changes, static change => change.Path == "Lines[0].key");
        var rangeChange = Assert.Single(modified.Changes, static change => change.Path == "Lines[0].args[1]");
        Assert.Equal(20, rangeChange.OldValue!.Value.GetInt32());
        Assert.Equal(25, rangeChange.NewValue!.Value.GetInt32());
        Assert.Equal("abc", modified.Old.GetProperty("Equipment").GetProperty("NormCode").GetString());
        Assert.Equal("def", modified.New.GetProperty("Equipment").GetProperty("NormCode").GetString());
    }

    [Fact]
    public async Task UniquePropertyLineReorderingIsReportedAsAChange()
    {
        var oldSnapshot = await CreateSnapshotAsync("old", uniques:
            "[{\"Index\":\"Unique\",\"Lines\":[{\"key\":\"strFirst\"},{\"key\":\"strSecond\"}]}]");
        var newSnapshot = await CreateSnapshotAsync("new", uniques:
            "[{\"Index\":\"Unique\",\"Lines\":[{\"key\":\"strSecond\"},{\"key\":\"strFirst\"}]}]");

        var uniques = GetCategory(await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot), "uniques");

        Assert.Equal(1, uniques.Counts.Modified);
        Assert.Contains(Assert.Single(uniques.ModifiedRecords).ChangedFields, static path => path == "Lines[0].key");
    }

    [Fact]
    public async Task DuplicateUniqueIdentityInvalidatesOnlyUniqueCategory()
    {
        var oldSnapshot =
            await CreateSnapshotAsync("old", uniques: "[{\"Index\":\"Duplicate\"},{\"Index\":\"Duplicate\"}]");
        var newSnapshot = await CreateSnapshotAsync("new");

        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);

        Assert.False(report.Succeeded);
        Assert.Equal("NotCompared", GetCategory(report, "uniques").Status);
        Assert.Contains(GetCategory(report, "uniques").Errors,
            static issue => issue.Code == "duplicate-record-identity");
        Assert.Equal("Compared", GetCategory(report, "cube-recipes").Status);
        Assert.Equal("Compared", GetCategory(report, "sets").Status);
    }

    [Fact]
    public async Task MissingUniqueIdentityInvalidatesCategory()
    {
        var oldSnapshot = await CreateSnapshotAsync("old", uniques: "[{\"Code\":\"no-index\"}]");
        var newSnapshot = await CreateSnapshotAsync("new");

        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);

        Assert.Equal("NotCompared", GetCategory(report, "uniques").Status);
        Assert.Contains(GetCategory(report, "uniques").Errors, static issue => issue.Code == "missing-record-identity");
    }

    [Fact]
    public async Task UniqueRunewordModificationUsesIndexAndPreservesStructuralChanges()
    {
        const string oldRunewords =
            "[{\"Index\":\"Solo\",\"Vanilla\":\"N\",\"Enabled\":true,\"Rarity\":1,\"ItemLevel\":8,\"RequiredLevel\":12,\"Code\":\"\",\"Runes\":[{\"NameKey\":\"Tal\"}],\"Types\":[{\"Index\":\"sword\"}],\"Qualifiers\":[\"weapon\"],\"Lines\":[{\"key\":\"strDamage\",\"args\":[10,20]}]}]";
        const string newRunewords =
            "[{\"Index\":\"Solo\",\"Vanilla\":\"Y\",\"Enabled\":false,\"Rarity\":2,\"ItemLevel\":9,\"RequiredLevel\":15,\"Code\":\"\",\"Runes\":[{\"NameKey\":\"Ral\"}],\"Types\":[{\"Index\":\"sword\"},{\"Index\":\"polearm\"}],\"Qualifiers\":[\"armor\"],\"Lines\":[{\"key\":\"strDamage\",\"args\":[10,25]}]}]";
        var oldSnapshot = await CreateSnapshotAsync("old", runewords: oldRunewords);
        var newSnapshot = await CreateSnapshotAsync("new", runewords: newRunewords);

        var runewords = GetCategory(await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot), "runewords");
        var modified = Assert.Single(runewords.ModifiedRecords);

        Assert.Equal("Compared", runewords.Status);
        Assert.Equal("Index", runewords.MatchKey);
        Assert.Equal("Types", runewords.VariantField);
        Assert.Equal("Index", runewords.VariantMatchKey);
        Assert.Equal(1, runewords.Counts.Modified);
        Assert.Contains(modified.ChangedFields, static path => path == "Enabled");
        Assert.Contains(modified.ChangedFields, static path => path == "RequiredLevel");
        Assert.Contains(modified.ChangedFields, static path => path == "Runes[0].NameKey");
        Assert.Contains(modified.ChangedFields, static path => path == "Qualifiers[0]");
        Assert.Contains(modified.ChangedFields, static path => path == "Lines[0].args[1]");
        Assert.Contains(modified.Changes,
            static change => change.Path == "Types[Index=\"polearm\"]" && change.Kind == "Added");
        Assert.Equal("Tal", modified.Old.GetProperty("Runes")[0].GetProperty("NameKey").GetString());
        Assert.Equal("Ral", modified.New.GetProperty("Runes")[0].GetProperty("NameKey").GetString());
    }

    [Fact]
    public async Task FourKnownDuplicateRunewordNamesMatchByTypeIdentifiers()
    {
        var oldRows = new object[]
        {
            CreateRuneword("Leaf", ["Tir", "Ral"], ["sword", "polearm"]),
            CreateRuneword("Leaf", ["Tir", "Ral"], ["bow"]),
            CreateRuneword("Spark", ["Nef", "Tir"], ["sword", "axe"]),
            CreateRuneword("Spark", ["Nef", "Tir"], ["staff"]),
            CreateRuneword("Snowdrop", ["Tal", "Eth"], ["armor"]),
            CreateRuneword("Snowdrop", ["Tal", "Eth"], ["shield"]),
            CreateRuneword("Doom1", ["Hel", "Ohm"], ["sword"]),
            CreateRuneword("Doom1", ["Hel", "Ohm"], ["polearm"])
        };
        var newRows = new object[]
        {
            CreateRuneword("Leaf", ["Tir", "Ral"], ["polearm", "sword"]),
            CreateRuneword("Leaf", ["Tir", "Ral"], ["bow"]),
            CreateRuneword("Spark", ["Nef", "Tir"], ["sword", "axe"]),
            CreateRuneword("Spark", ["Nef", "Tir"], ["staff"]),
            CreateRuneword("Snowdrop", ["Tal", "Eth"], ["armor"]),
            CreateRuneword("Snowdrop", ["Tal", "Eth"], ["shield"]),
            CreateRuneword("Doom1", ["Hel", "Ohm"], ["sword"]),
            CreateRuneword("Doom1", ["Hel", "Ohm"], ["polearm"])
        };
        var oldSnapshot = await CreateSnapshotAsync("old", runewords: JsonSerializer.Serialize(oldRows));
        var newSnapshot = await CreateSnapshotAsync("new", runewords: JsonSerializer.Serialize(newRows));

        var runewords = GetCategory(await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot), "runewords");

        Assert.Equal((0, 0, 0, 8),
            (runewords.Counts.Added, runewords.Counts.Removed, runewords.Counts.Modified, runewords.Counts.Unchanged));
        Assert.Empty(runewords.Warnings);
    }

    [Fact]
    public async Task DuplicateRunewordEligibilityChangesAreAdditionsAndRemovals()
    {
        var oldRows = new object[]
        {
            CreateRuneword("Shared Runes", ["Tal", "Eth"], ["sword"]),
            CreateRuneword("Shared Runes", ["Tal", "Eth"], ["staff"])
        };
        var newRows = new object[]
        {
            CreateRuneword("Shared Runes", ["Tal", "Eth"], ["sword"]),
            CreateRuneword("Shared Runes", ["Tal", "Eth"], ["shield"])
        };
        var oldSnapshot = await CreateSnapshotAsync("old", runewords: JsonSerializer.Serialize(oldRows));
        var newSnapshot = await CreateSnapshotAsync("new", runewords: JsonSerializer.Serialize(newRows));

        var runewords = GetCategory(await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot), "runewords");

        Assert.Equal((1, 1, 0, 1),
            (runewords.Counts.Added, runewords.Counts.Removed, runewords.Counts.Modified, runewords.Counts.Unchanged));
        Assert.Equal(new[] { "shield" }, Assert.Single(runewords.AddedRecords).VariantTypes);
        Assert.Equal(new[] { "staff" }, Assert.Single(runewords.RemovedRecords).VariantTypes);
    }

    [Fact]
    public async Task RunewordRuneSequenceOrderingIsMeaningful()
    {
        var oldSnapshot = await CreateSnapshotAsync("old", runewords:
            "[{\"Index\":\"Ordered\",\"Runes\":[{\"NameKey\":\"Tal\"},{\"NameKey\":\"Eth\"}],\"Types\":[{\"Index\":\"sword\"}]}]");
        var newSnapshot = await CreateSnapshotAsync("new", runewords:
            "[{\"Index\":\"Ordered\",\"Runes\":[{\"NameKey\":\"Eth\"},{\"NameKey\":\"Tal\"}],\"Types\":[{\"Index\":\"sword\"}]}]");

        var runewords = GetCategory(await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot), "runewords");
        var modified = Assert.Single(runewords.ModifiedRecords);

        Assert.Equal(1, runewords.Counts.Modified);
        Assert.Contains(modified.ChangedFields, static path => path == "Runes[0].NameKey");
        Assert.Contains(modified.ChangedFields, static path => path == "Runes[1].NameKey");
    }

    [Fact]
    public async Task AmbiguousRunewordVariantsAreNotPaired()
    {
        var oldRows = new object[]
        {
            CreateRuneword("Ambiguous", ["Tal"], ["sword"]),
            CreateRuneword("Ambiguous", ["Eth"], ["sword"])
        };
        var newRows = new object[] { CreateRuneword("Ambiguous", ["Ral"], ["sword"]) };
        var oldSnapshot = await CreateSnapshotAsync("old", runewords: JsonSerializer.Serialize(oldRows));
        var newSnapshot = await CreateSnapshotAsync("new", runewords: JsonSerializer.Serialize(newRows));

        var runewords = GetCategory(await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot), "runewords");

        Assert.Equal((1, 2, 0),
            (runewords.Counts.Added, runewords.Counts.Removed, runewords.Counts.Modified));
        Assert.Single(runewords.Warnings, static issue => issue.Code == "ambiguous-variant-identity");
        Assert.Equal(3, runewords.AddedRecords.Select(static record => record.Key)
            .Concat(runewords.RemovedRecords.Select(static record => record.Key)).Distinct(StringComparer.Ordinal)
            .Count());
        Assert.All(runewords.AddedRecords.Concat(runewords.RemovedRecords),
            static record => Assert.Equal(new[] { "sword" }, record.VariantTypes));
    }

    [Fact]
    public async Task MissingRunewordFileFromManifestDoesNotSuppressOtherCategories()
    {
        var oldSnapshot = await CreateSnapshotAsync("old", omitFromManifest: [RunewordPath]);
        var newSnapshot = await CreateSnapshotAsync("new");

        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);

        Assert.Equal("NotCompared", GetCategory(report, "runewords").Status);
        Assert.Contains(GetCategory(report, "runewords").Errors, static issue => issue.Code == "file-not-in-manifest");
        Assert.Equal("Compared", GetCategory(report, "cube-recipes").Status);
        Assert.Equal("Compared", GetCategory(report, "uniques").Status);
        Assert.Equal("Compared", GetCategory(report, "sets").Status);
        Assert.Equal("Compared", GetCategory(report, "armors").Status);
        Assert.Equal("Compared", GetCategory(report, "weapons").Status);
    }

    [Theory]
    [InlineData("[{\"Index\":\"Missing Types\"}]", "invalid-variant-identity")]
    [InlineData("[{\"Index\":\"Missing Type Index\",\"Types\":[{}]}]", "invalid-variant-identity")]
    [InlineData("[{\"Index\":\"Duplicate Type\",\"Types\":[{\"Index\":\"sword\"},{\"Index\":\"sword\"}]}]",
        "invalid-variant-identity")]
    [InlineData("[{\"Types\":[]}]", "missing-record-identity")]
    public async Task InvalidRunewordIdentityInvalidatesOnlyRunewordCategory(string runewordsPayload, string errorCode)
    {
        var oldSnapshot = await CreateSnapshotAsync("old", runewords: runewordsPayload);
        var newSnapshot = await CreateSnapshotAsync("new");

        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);

        Assert.Equal("NotCompared", GetCategory(report, "runewords").Status);
        Assert.Contains(GetCategory(report, "runewords").Errors, issue => issue.Code == errorCode);
        Assert.Equal("Compared", GetCategory(report, "cube-recipes").Status);
        Assert.Equal("Compared", GetCategory(report, "uniques").Status);
        Assert.Equal("Compared", GetCategory(report, "sets").Status);
    }

    [Fact]
    public async Task DuplicateSetIdentityInvalidatesSetsCategory()
    {
        var oldSnapshot = await CreateSnapshotAsync("old", sets:
            "[{\"Index\":\"Duplicate\",\"SetItems\":[]},{\"Index\":\"Duplicate\",\"SetItems\":[]}]");
        var newSnapshot = await CreateSnapshotAsync("new");

        var sets = GetCategory(await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot), "sets");

        Assert.Equal("NotCompared", sets.Status);
        Assert.Contains(sets.Errors, static issue => issue.Code == "duplicate-record-identity");
    }

    [Fact]
    public async Task MissingSetIdentityInvalidatesSetsCategory()
    {
        var oldSnapshot = await CreateSnapshotAsync("old", sets: "[{\"SetItems\":[]}]");
        var newSnapshot = await CreateSnapshotAsync("new");

        var sets = GetCategory(await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot), "sets");

        Assert.Equal("NotCompared", sets.Status);
        Assert.Contains(sets.Errors, static issue => issue.Code == "missing-record-identity");
    }

    [Fact]
    public async Task AddedAndRemovedSetsUseStringIndexIdentity()
    {
        var oldSnapshot = await CreateSnapshotAsync("old", sets:
            "[{\"Index\":\"Removed Set\",\"SetItems\":[]},{\"Index\":\"Kept Set\",\"SetItems\":[]}]");
        var newSnapshot = await CreateSnapshotAsync("new", sets:
            "[{\"Index\":\"Added Set\",\"SetItems\":[]},{\"Index\":\"Kept Set\",\"SetItems\":[]}]");

        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);
        var sets = GetCategory(report, "sets");

        Assert.Equal((1, 1, 0, 1),
            (sets.Counts.Added, sets.Counts.Removed, sets.Counts.Modified, sets.Counts.Unchanged));
        Assert.Equal("Added Set", Assert.Single(sets.AddedRecords).Key);
        Assert.Equal("Removed Set", Assert.Single(sets.RemovedRecords).Key);
        Assert.Equal("Index", sets.NestedMatchKey);
        Assert.Equal("SetItems", sets.NestedField);
    }

    [Fact]
    public async Task SetPieceChangesAndSharedBonusesAreReportedByPieceIdentity()
    {
        const string oldSets =
            "[{\"Index\":\"Set\",\"PartialBonuses\":[{\"key\":\"strPartial\",\"args\":[2]}],\"FullBonuses\":[{\"key\":\"strFull\",\"args\":[5]}],\"SetItems\":[{\"Index\":\"Hat\",\"Lines\":[{\"key\":\"strPiece\",\"args\":[1,10]}],\"SetBonuses\":[[{\"key\":\"strPieceBonus\",\"args\":[10]}]]},{\"Index\":\"Removed Piece\",\"Lines\":[]}]}]";
        const string newSets =
            "[{\"Index\":\"Set\",\"PartialBonuses\":[{\"key\":\"strPartial\",\"args\":[3]}],\"FullBonuses\":[{\"key\":\"strFull\",\"args\":[6]}],\"SetItems\":[{\"Index\":\"Hat\",\"Lines\":[{\"key\":\"strPiece\",\"args\":[1,15]}],\"SetBonuses\":[[{\"key\":\"strPieceBonus\",\"args\":[15]}]]},{\"Index\":\"Added Piece\",\"Lines\":[]}]}]";
        var oldSnapshot = await CreateSnapshotAsync("old", sets: oldSets);
        var newSnapshot = await CreateSnapshotAsync("new", sets: newSets);

        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);
        var sets = GetCategory(report, "sets");
        var modified = Assert.Single(sets.ModifiedRecords);
        var nestedChanges = Assert.IsType<NestedRecordChanges>(modified.NestedChanges);

        Assert.Equal(1, sets.Counts.Modified);
        Assert.Equal((1, 1, 1), (sets.NestedCounts!.Added, sets.NestedCounts.Removed, sets.NestedCounts.Modified));
        Assert.Equal("Added Piece", Assert.Single(nestedChanges.Added).Key);
        Assert.Equal("Removed Piece", Assert.Single(nestedChanges.Removed).Key);
        Assert.Contains(Assert.Single(nestedChanges.Modified).ChangedFields, static path => path == "Lines[0].args[1]");
        Assert.Contains(modified.ChangedFields, static path => path == "PartialBonuses[0].args[0]");
        Assert.Contains(modified.ChangedFields, static path => path == "FullBonuses[0].args[0]");
        Assert.Contains(Assert.Single(nestedChanges.Modified).ChangedFields,
            static path => path == "SetBonuses[0][0].args[0]");
        Assert.Contains(modified.Changes,
            static change => change.Path == "SetItems[Index=\"Added Piece\"]" && change.Kind == "Added");
    }

    [Fact]
    public async Task ReorderingSetPiecesDoesNotCreateGameplayChanges()
    {
        const string oldSets =
            "[{\"Index\":\"Set\",\"SetItems\":[{\"Index\":\"Hat\",\"Lines\":[]},{\"Index\":\"Boots\",\"Lines\":[]}]}]";
        const string newSets =
            "[{\"Index\":\"Set\",\"SetItems\":[{\"Index\":\"Boots\",\"Lines\":[]},{\"Index\":\"Hat\",\"Lines\":[]}]}]";
        var oldSnapshot = await CreateSnapshotAsync("old", sets: oldSets);
        var newSnapshot = await CreateSnapshotAsync("new", sets: newSets);

        var sets = GetCategory(await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot), "sets");

        Assert.Equal(0, sets.Counts.Modified);
        Assert.Equal(1, sets.Counts.Unchanged);
        Assert.Equal(2, sets.NestedCounts!.Unchanged);
    }

    [Fact]
    public async Task EquipmentCategoriesMatchByNameKeyAndPreserveAddedAndRemovedRecords()
    {
        var oldSnapshot = await CreateSnapshotAsync("old",
            armors:
            "[{\"NameKey\":\"Kept Armor\",\"NormCode\":\"kept\"},{\"NameKey\":\"Removed Armor\",\"NormCode\":\"shared\"}]",
            weapons: "[{\"NameKey\":\"Removed Weapon\",\"NormCode\":\"same-tier-code\"}]");
        var newSnapshot = await CreateSnapshotAsync("new",
            armors:
            "[{\"NameKey\":\"Kept Armor\",\"NormCode\":\"kept\"},{\"NameKey\":\"Added Armor\",\"NormCode\":\"shared\"}]",
            weapons: "[{\"NameKey\":\"Added Weapon\",\"NormCode\":\"same-tier-code\"}]");

        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);
        var armors = GetCategory(report, "armors");
        var weapons = GetCategory(report, "weapons");

        Assert.True(report.Succeeded);
        Assert.Equal("NameKey", armors.MatchKey);
        Assert.Equal("NameKey", weapons.MatchKey);
        Assert.Equal((1, 1, 0), (armors.Counts.Added, armors.Counts.Removed, armors.Counts.Modified));
        Assert.Equal((1, 1, 0), (weapons.Counts.Added, weapons.Counts.Removed, weapons.Counts.Modified));
        Assert.Equal("Added Armor", Assert.Single(armors.AddedRecords).Record.GetProperty("NameKey").GetString());
        Assert.Equal("shared", Assert.Single(armors.AddedRecords).Record.GetProperty("NormCode").GetString());
        Assert.Equal("Removed Armor", Assert.Single(armors.RemovedRecords).Key);
        Assert.Equal("Added Weapon", Assert.Single(weapons.AddedRecords).Key);
        Assert.Equal("Removed Weapon", Assert.Single(weapons.RemovedRecords).Key);
    }

    [Fact]
    public async Task EquipmentMetadataChangesIncludeStructuralDiffsAndCompleteRecords()
    {
        const string oldArmor =
            "[{\"EquipmentType\":1,\"NameKey\":\"Armor\",\"NormCode\":\"n1\",\"UberCode\":\"u1\",\"UltraCode\":\"x1\",\"AutoPrefix\":10,\"RequiredClass\":\"amazon\",\"Type\":{\"Index\":\"armor\"},\"DamageTypes\":[{\"Type\":1,\"AverageDamage\":2,\"Lines\":[{\"key\":\"strDamage\",\"args\":[1,2]}]}],\"RequiredLevel\":5,\"MaxSockets\":2,\"GemSockets\":\"(1-25): 1\",\"Lines\":[{\"key\":\"strDefense\",\"args\":[10]},{\"key\":\"strDurability\",\"args\":[20]},{\"key\":\"strRequiredLevel\",\"args\":[5]}]}]";
        const string newArmor =
            "[{\"EquipmentType\":2,\"NameKey\":\"Armor\",\"NormCode\":\"n2\",\"UberCode\":\"u2\",\"UltraCode\":\"x2\",\"AutoPrefix\":11,\"RequiredClass\":\"paladin\",\"Type\":{\"Index\":\"shield\"},\"DamageTypes\":[{\"Type\":2,\"AverageDamage\":4,\"Lines\":[{\"key\":\"strDamage\",\"args\":[2,4]}]}],\"RequiredLevel\":6,\"MaxSockets\":4,\"GemSockets\":\"(1-25): 2\",\"Lines\":[{\"key\":\"strDefense\",\"args\":[15]},{\"key\":\"strDurability\",\"args\":[25]},{\"key\":\"strRequiredLevel\",\"args\":[6]}]}]";
        var oldSnapshot = await CreateSnapshotAsync("old", armors: oldArmor);
        var newSnapshot = await CreateSnapshotAsync("new", armors: newArmor);

        var armors = GetCategory(await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot), "armors");
        var modified = Assert.Single(armors.ModifiedRecords);

        Assert.Equal(1, armors.Counts.Modified);
        Assert.Contains(modified.ChangedFields, static path => path == "EquipmentType");
        Assert.Contains(modified.ChangedFields, static path => path == "RequiredClass");
        Assert.Contains(modified.ChangedFields, static path => path == "Type.Index");
        Assert.Contains(modified.ChangedFields, static path => path == "NormCode");
        Assert.Contains(modified.ChangedFields, static path => path == "UberCode");
        Assert.Contains(modified.ChangedFields, static path => path == "UltraCode");
        Assert.Contains(modified.ChangedFields, static path => path == "AutoPrefix");
        Assert.Contains(modified.ChangedFields, static path => path == "RequiredLevel");
        Assert.Contains(modified.ChangedFields, static path => path == "MaxSockets");
        Assert.Contains(modified.ChangedFields, static path => path == "GemSockets");
        Assert.Contains(modified.ChangedFields, static path => path == "DamageTypes[0].AverageDamage");
        Assert.Contains(modified.ChangedFields, static path => path == "DamageTypes[0].Lines[0].args[1]");
        Assert.Contains(modified.ChangedFields, static path => path == "Lines[0].args[0]");
        Assert.Contains(modified.ChangedFields, static path => path == "Lines[1].args[0]");
        Assert.Contains(modified.ChangedFields, static path => path == "Lines[2].args[0]");
        Assert.Equal("n1", modified.Old.GetProperty("NormCode").GetString());
        Assert.Equal(4, modified.New.GetProperty("MaxSockets").GetInt32());
    }

    [Fact]
    public async Task AutoMagicGroupsMatchByCodeAndReportAddedRemovedAndModifiedGroups()
    {
        const string oldArmor =
            "[{\"NameKey\":\"Armor\",\"AutoMagicGroups\":[{\"Code\":\"Changed\",\"NameKey\":\"strChanged\",\"Level\":10,\"RequiredLevel\":5,\"Lines\":[{\"key\":\"strDefense\",\"args\":[2]},{\"key\":\"strPropertyGroupsProperty\",\"args\":[],\"pickMode\":\"2\",\"children\":[{\"key\":\"strFireDamage\",\"args\":[1,2],\"chance\":40,\"selection\":{\"stat\":\"skill\",\"min\":1,\"max\":2}}]}]},{\"Code\":\"Removed\",\"Level\":1,\"RequiredLevel\":1,\"Lines\":[]}]}]";
        const string newArmor =
            "[{\"NameKey\":\"Armor\",\"AutoMagicGroups\":[{\"Code\":\"Added\",\"Level\":3,\"RequiredLevel\":2,\"Lines\":[{\"key\":\"strDamage\",\"args\":[4]}]},{\"Code\":\"Changed\",\"NameKey\":\"strChanged\",\"Level\":11,\"RequiredLevel\":6,\"Lines\":[{\"key\":\"strDefense\",\"args\":[5]},{\"key\":\"strPropertyGroupsProperty\",\"args\":[],\"pickMode\":\"1\",\"children\":[{\"key\":\"strFireDamage\",\"args\":[1,2],\"chance\":60,\"selection\":{\"stat\":\"skill\",\"min\":1,\"max\":3}}]}]}]}]";
        var oldSnapshot = await CreateSnapshotAsync("old", armors: oldArmor,
            weapons: "[{\"NameKey\":\"Weapon\",\"AutoMagicGroups\":[]}]");
        var newSnapshot = await CreateSnapshotAsync("new", armors: newArmor,
            weapons:
            "[{\"NameKey\":\"Weapon\",\"AutoMagicGroups\":[{\"Code\":\"Bow Group\",\"Level\":4,\"RequiredLevel\":3,\"Lines\":[]}]}]");

        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);
        var armors = GetCategory(report, "armors");
        var modified = Assert.Single(armors.ModifiedRecords);
        var nested = Assert.IsType<NestedRecordChanges>(modified.NestedChanges);
        var modifiedGroup = Assert.Single(nested.Modified);
        var weapons = GetCategory(report, "weapons");
        var addedWeaponGroup = Assert.Single(Assert.Single(weapons.ModifiedRecords).NestedChanges!.Added);

        Assert.Equal("AutoMagicGroups", armors.NestedField);
        Assert.Equal("Code", armors.NestedMatchKey);
        Assert.Equal((1, 1, 1),
            (armors.NestedCounts!.Added, armors.NestedCounts.Removed, armors.NestedCounts.Modified));
        Assert.Equal("Added", Assert.Single(nested.Added).Key);
        Assert.Equal("strDamage",
            Assert.Single(nested.Added).Record.GetProperty("Lines")[0].GetProperty("key").GetString());
        Assert.Equal("Removed", Assert.Single(nested.Removed).Key);
        Assert.Equal(10, modifiedGroup.Old.GetProperty("Level").GetInt32());
        Assert.Equal(11, modifiedGroup.New.GetProperty("Level").GetInt32());
        Assert.Equal(6, modifiedGroup.New.GetProperty("RequiredLevel").GetInt32());
        Assert.Contains(modifiedGroup.ChangedFields, static path => path == "Lines[0].args[0]");
        Assert.Contains(modifiedGroup.ChangedFields,
            static path => path == "Lines[1].children[0].chance");
        Assert.Contains(modifiedGroup.ChangedFields,
            static path => path == "Lines[1].children[0].selection.max");
        Assert.Equal("Bow Group", addedWeaponGroup.Record.GetProperty("Code").GetString());
        Assert.Contains(modified.Changes,
            static change => change.Path == "AutoMagicGroups[Code=\"Added\"]" && change.Kind == "Added");
    }

    [Fact]
    public async Task AutoMagicGroupReorderingIsNonSemanticAndNullEqualsMissing()
    {
        const string oldArmor =
            "[{\"NameKey\":\"Armor\",\"AutoMagicGroups\":[{\"Code\":\"A\",\"Level\":1},{\"Code\":\"B\",\"Level\":2}]}]";
        const string newArmor =
            "[{\"NameKey\":\"Armor\",\"AutoMagicGroups\":[{\"Code\":\"B\",\"Level\":2},{\"Code\":\"A\",\"Level\":1}]}]";
        var oldSnapshot = await CreateSnapshotAsync("old", armors: oldArmor,
            weapons: "[{\"NameKey\":\"Weapon\",\"AutoMagicGroups\":null}]");
        var newSnapshot = await CreateSnapshotAsync("new", armors: newArmor, weapons: "[{\"NameKey\":\"Weapon\"}]");

        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);
        var armors = GetCategory(report, "armors");
        var weapons = GetCategory(report, "weapons");

        Assert.True(report.Succeeded);
        Assert.Equal(1, armors.Counts.Unchanged);
        Assert.Equal(2, armors.NestedCounts!.Unchanged);
        Assert.Equal(1, weapons.Counts.Unchanged);
        Assert.Equal(0, weapons.NestedCounts!.Added + weapons.NestedCounts.Removed + weapons.NestedCounts.Modified);
        Assert.Empty(armors.ModifiedRecords);
        Assert.Empty(weapons.ModifiedRecords);
    }

    [Theory]
    [InlineData("armors", "[{\"NameKey\":\"Duplicate\"},{\"NameKey\":\"Duplicate\"}]", "duplicate-record-identity")]
    [InlineData("armors", "[{\"NormCode\":\"missing-key\"}]", "missing-record-identity")]
    [InlineData("weapons", "[{\"NameKey\":\"Duplicate\"},{\"NameKey\":\"Duplicate\"}]", "duplicate-record-identity")]
    [InlineData("weapons", "[{\"NormCode\":\"missing-key\"}]", "missing-record-identity")]
    public async Task InvalidEquipmentIdentityInvalidatesOnlyItsCategory(
        string category,
        string payload,
        string errorCode)
    {
        var oldSnapshot = category == "armors"
            ? await CreateSnapshotAsync("old", armors: payload)
            : await CreateSnapshotAsync("old", weapons: payload);
        var newSnapshot = await CreateSnapshotAsync("new");

        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);

        Assert.Equal("NotCompared", GetCategory(report, category).Status);
        Assert.Contains(GetCategory(report, category).Errors, issue => issue.Code == errorCode);
        Assert.Equal("Compared", GetCategory(report, category == "armors" ? "weapons" : "armors").Status);
        Assert.Equal("Compared", GetCategory(report, "uniques").Status);
    }

    [Theory]
    [InlineData("[{\"Code\":\"Duplicate\"},{\"Code\":\"Duplicate\"}]", "duplicate-nested-record-identity")]
    [InlineData("[{\"NameKey\":\"No code\"}]", "missing-nested-record-identity")]
    public async Task InvalidAutoMagicGroupIdentityInvalidatesOnlyItsEquipmentCategory(
        string groups,
        string errorCode)
    {
        var oldSnapshot = await CreateSnapshotAsync("old",
            armors: $"[{{\"NameKey\":\"Armor\",\"AutoMagicGroups\":{groups}}}]");
        var newSnapshot = await CreateSnapshotAsync("new");

        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);

        Assert.Equal("NotCompared", GetCategory(report, "armors").Status);
        Assert.Contains(GetCategory(report, "armors").Errors, issue => issue.Code == errorCode);
        Assert.Equal("Compared", GetCategory(report, "weapons").Status);
        Assert.Equal("Compared", GetCategory(report, "uniques").Status);
    }

    [Fact]
    public async Task InvalidArmorNestedDataDoesNotSuppressWeaponsOrOtherCategories()
    {
        var oldSnapshot = await CreateSnapshotAsync("old",
            armors: "[{\"NameKey\":\"Bad armor\",\"AutoMagicGroups\":{}}]",
            weapons: "[{\"NameKey\":\"Good weapon\"}]");
        var newSnapshot = await CreateSnapshotAsync("new",
            armors: "[{\"NameKey\":\"Bad armor\",\"AutoMagicGroups\":[]}]",
            weapons: "[{\"NameKey\":\"Good weapon\"}]");

        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);

        Assert.False(report.Succeeded);
        Assert.Equal("NotCompared", GetCategory(report, "armors").Status);
        Assert.Contains(GetCategory(report, "armors").Errors, static issue => issue.Code == "invalid-nested-records");
        Assert.Equal("Compared", GetCategory(report, "weapons").Status);
        Assert.Equal(1, GetCategory(report, "weapons").Counts.Unchanged);
        Assert.Equal("Compared", GetCategory(report, "uniques").Status);
    }

    [Fact]
    public async Task MissingWeaponFileFromManifestDoesNotSuppressArmorComparison()
    {
        var oldSnapshot = await CreateSnapshotAsync("old", omitFromManifest: [WeaponPath]);
        var newSnapshot = await CreateSnapshotAsync("new");

        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);

        Assert.Equal("NotCompared", GetCategory(report, "weapons").Status);
        Assert.Contains(GetCategory(report, "weapons").Errors, static issue => issue.Code == "file-not-in-manifest");
        Assert.Equal("Compared", GetCategory(report, "armors").Status);
        Assert.Equal("Compared", GetCategory(report, "sets").Status);
    }

    [Fact]
    public async Task DuplicateSetPieceIdentityInvalidatesSetsCategory()
    {
        var oldSnapshot = await CreateSnapshotAsync("old", sets:
            "[{\"Index\":\"Set\",\"SetItems\":[{\"Index\":\"Piece\"},{\"Index\":\"Piece\"}]}]");
        var newSnapshot = await CreateSnapshotAsync("new");

        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);
        var sets = GetCategory(report, "sets");

        Assert.Equal("NotCompared", sets.Status);
        Assert.Contains(sets.Errors, static issue => issue.Code == "duplicate-nested-record-identity");
    }

    [Fact]
    public async Task MissingSetPieceIdentityInvalidatesSetsCategory()
    {
        var oldSnapshot =
            await CreateSnapshotAsync("old", sets: "[{\"Index\":\"Set\",\"SetItems\":[{\"Code\":\"no-index\"}]}]");
        var newSnapshot = await CreateSnapshotAsync("new");

        var sets = GetCategory(await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot), "sets");

        Assert.Equal("NotCompared", sets.Status);
        Assert.Contains(sets.Errors, static issue => issue.Code == "missing-nested-record-identity");
    }

    [Fact]
    public async Task MissingCategoryFileFromManifestIsIsolatedFromOtherCategories()
    {
        var oldSnapshot = await CreateSnapshotAsync("old", omitFromManifest: [UniquePath]);
        var newSnapshot = await CreateSnapshotAsync("new");

        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);

        Assert.False(report.Succeeded);
        Assert.Equal("NotCompared", GetCategory(report, "uniques").Status);
        Assert.Contains(GetCategory(report, "uniques").Errors, static issue => issue.Code == "file-not-in-manifest");
        Assert.Equal("Compared", GetCategory(report, "cube-recipes").Status);
        Assert.Equal("Compared", GetCategory(report, "sets").Status);
        Assert.Equal("Compared", GetCategory(report, "armors").Status);
        Assert.Equal("Compared", GetCategory(report, "weapons").Status);
        Assert.Empty(report.OldSnapshot.Errors);
    }

    [Fact]
    public async Task InvalidCategoryJsonDoesNotSuppressOtherCategories()
    {
        var oldSnapshot = await CreateSnapshotAsync("old", uniques: "{");
        var newSnapshot = await CreateSnapshotAsync("new");

        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);

        Assert.Equal("NotCompared", GetCategory(report, "uniques").Status);
        Assert.Contains(GetCategory(report, "uniques").Errors, static issue => issue.Code == "invalid-category-json");
        Assert.Equal("old", Assert.Single(GetCategory(report, "uniques").Errors).Snapshot);
        Assert.Equal("Compared", GetCategory(report, "cube-recipes").Status);
        Assert.Equal("Compared", GetCategory(report, "sets").Status);
    }

    [Fact]
    public async Task HashMismatchIsCategorySpecific()
    {
        var oldSnapshot = await CreateSnapshotAsync("old");
        var newSnapshot = await CreateSnapshotAsync("new", hashOverrides: new Dictionary<string, string>
        {
            [SetPath] = new string('0', 64)
        });

        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);

        Assert.Equal("NotCompared", GetCategory(report, "sets").Status);
        Assert.Contains(GetCategory(report, "sets").Errors,
            static issue => issue.Code == "snapshot-file-hash-mismatch");
        Assert.Equal("Compared", GetCategory(report, "cube-recipes").Status);
        Assert.Equal("Compared", GetCategory(report, "uniques").Status);
    }

    [Fact]
    public async Task MissingManifestPreventsAllCategoriesFromBeingCompared()
    {
        var oldSnapshot = Directory.CreateDirectory(Path.Combine(_root, "old")).FullName;
        var newSnapshot = await CreateSnapshotAsync("new");

        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);

        Assert.False(report.Succeeded);
        Assert.All(report.Categories, category => Assert.Equal("NotCompared", category.Status));
        Assert.All(report.Categories,
            category => Assert.Contains(category.Errors, static issue => issue.Code == "manifest-unreadable"));
    }

    [Fact]
    public async Task FailedSnapshotWarnsButVerifiedCategoryFilesCanBeCompared()
    {
        var oldSnapshot = await CreateSnapshotAsync("old", succeeded: false, complete: false);
        var newSnapshot = await CreateSnapshotAsync("new");

        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);

        Assert.True(report.Succeeded);
        Assert.False(report.OldSnapshot.Succeeded);
        Assert.False(report.OldSnapshot.Complete);
        Assert.Empty(report.OldSnapshot.Errors);
        Assert.Contains(report.OldSnapshot.Warnings, static issue => issue.Code == "snapshot-failed");
        Assert.All(report.Categories, category => Assert.Equal("Compared", category.Status));
        Assert.All(report.Categories,
            category => Assert.Contains(category.Warnings, static issue => issue.Code == "snapshot-failed"));
    }

    [Fact]
    public async Task FailedSnapshotStillRejectsNotWrittenCategoryFiles()
    {
        var oldSnapshot = await CreateSnapshotAsync("old", stepStatus: "Failed", succeeded: false, complete: false);
        var newSnapshot = await CreateSnapshotAsync("new");

        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);

        Assert.False(report.Succeeded);
        Assert.All(report.Categories, category => Assert.Equal("NotCompared", category.Status));
        Assert.All(report.Categories,
            category => Assert.Contains(category.Errors, static issue => issue.Code == "export-step-not-written"));
    }

    [Fact]
    public async Task IncompleteSnapshotWarnsButVerifiedCategoryFilesCanBeCompared()
    {
        var oldSnapshot = await CreateSnapshotAsync("old", complete: false);
        var newSnapshot = await CreateSnapshotAsync("new");

        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);

        Assert.True(report.Succeeded);
        Assert.All(report.Categories, category => Assert.Equal("Compared", category.Status));
        Assert.All(report.Categories,
            category => Assert.Contains(category.Warnings, static issue => issue.Code == "snapshot-incomplete"));
    }

    [Fact]
    public async Task SkippedCubeOutputIsNotTreatedAsDeletedRecipes()
    {
        var oldSnapshot = await CreateSnapshotAsync("old", cubeRecipes: "[]", stepStatus: "Skipped", complete: false);
        var newSnapshot = await CreateSnapshotAsync("new", cubeRecipes: "[{\"Id\":\"current\"}]");

        var cube = GetCategory(await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot), "cube-recipes");

        Assert.Equal("NotCompared", cube.Status);
        Assert.Contains(cube.Errors, static issue => issue.Code == "export-step-not-written");
        Assert.Empty(cube.RemovedRecords);
    }

    [Fact]
    public async Task DuplicateCubeIdsRemainInvalid()
    {
        var duplicate = "[{\"Id\":\"same\"},{\"Id\":\"same\"}]";
        var oldSnapshot = await CreateSnapshotAsync("old", cubeRecipes: duplicate);
        var newSnapshot = await CreateSnapshotAsync("new", cubeRecipes: duplicate);

        var cube = GetCategory(await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot), "cube-recipes");

        Assert.Equal("NotCompared", cube.Status);
        Assert.Contains(cube.Errors, static issue => issue.Code == "duplicate-record-identity");
    }

    [Fact]
    public async Task IdenticalSnapshotsProduceNoChangesAndDeterministicReports()
    {
        var oldSnapshot = await CreateSnapshotAsync("old",
            cubeRecipes: "[{\"Id\":\"cr\",\"Index\":0,\"Enabled\":true}]",
            uniques: "[{\"Index\":\"Unique\",\"Lines\":[]}]",
            sets: "[{\"Index\":\"Set\",\"SetItems\":[{\"Index\":\"Piece\",\"Lines\":[]}]}]");
        var newSnapshot = await CreateSnapshotAsync("new",
            cubeRecipes: "[{\"Id\":\"cr\",\"Index\":0,\"Enabled\":true}]",
            uniques: "[{\"Index\":\"Unique\",\"Lines\":[]}]",
            sets: "[{\"Index\":\"Set\",\"SetItems\":[{\"Index\":\"Piece\",\"Lines\":[]}]}]");
        var first = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);
        var second = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);
        var firstOutput = Path.Combine(_root, "comparison-one");
        var secondOutput = Path.Combine(_root, "comparison-two");

        await SnapshotComparison.WriteAsync(first, firstOutput);
        await SnapshotComparison.WriteAsync(second, secondOutput);

        Assert.True(first.Succeeded);
        Assert.Equal(2, first.SchemaVersion);
        Assert.Equal(0, first.Counts.Added + first.Counts.Removed + first.Counts.Modified);
        Assert.Equal(3, first.Counts.Unchanged);
        Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(firstOutput, "changes.json")),
            await File.ReadAllBytesAsync(Path.Combine(secondOutput, "changes.json")));
    }

    [Fact]
    public async Task AggregateCountsSumComparedCategories()
    {
        var oldSnapshot = await CreateSnapshotAsync("old", uniques: "[{\"Index\":\"Unique\",\"Enabled\":false}]",
            sets: "[{\"Index\":\"Set\",\"SetItems\":[]}]",
            runewords: "[{\"Index\":\"Rune\",\"Enabled\":false,\"Types\":[{\"Index\":\"sword\"}]}]");
        var newSnapshot = await CreateSnapshotAsync("new", cubeRecipes: "[{\"Id\":\"Recipe\"}]",
            uniques: "[{\"Index\":\"Unique\",\"Enabled\":true}]", sets: "[{\"Index\":\"Set\",\"SetItems\":[]}]",
            runewords: "[{\"Index\":\"Rune\",\"Enabled\":true,\"Types\":[{\"Index\":\"sword\"}]}]");

        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);

        Assert.Equal((1, 0, 2, 1),
            (report.Counts.Added, report.Counts.Removed, report.Counts.Modified, report.Counts.Unchanged));
    }

    [Fact]
    public async Task WritingComparisonDoesNotModifyInputSnapshots()
    {
        var oldSnapshot = await CreateSnapshotAsync("old", uniques: "[{\"Index\":\"Old\"}]");
        var newSnapshot = await CreateSnapshotAsync("new", uniques: "[{\"Index\":\"New\"}]");
        var before = new[] { oldSnapshot, newSnapshot }
            .SelectMany(snapshot => CategoryFiles.Prepend("manifest.json")
                .Select(path => (Path: Path.Combine(snapshot, path),
                    Bytes: File.ReadAllBytes(Path.Combine(snapshot, path)))))
            .ToList();
        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);
        var outputDirectory = Path.Combine(_root, "comparison");

        await SnapshotComparison.WriteAsync(report, outputDirectory);

        Assert.True(File.Exists(Path.Combine(outputDirectory, "changes.json")));
        foreach (var file in before) Assert.Equal(file.Bytes, await File.ReadAllBytesAsync(file.Path));
    }

    [Fact]
    public async Task UniqueNameAffixChangesIncludeStructuralFieldsAndCompleteRecords()
    {
        const string oldAffix =
            "[{\"NameKey\":\"of Power\",\"Level\":12,\"MaxLevel\":20,\"RequiredLevel\":5,\"Group\":3,\"ClassSpecific\":\"\",\"Class\":\"\",\"ClassLevelReq\":0,\"Types\":[\"weap\"],\"ETypes\":[\"swor\"],\"PType\":\"Suffix\",\"Lines\":[{\"key\":\"strDamage\",\"args\":[5,10]}]}]";
        const string newAffix =
            "[{\"NameKey\":\"of Power\",\"Level\":14,\"MaxLevel\":22,\"RequiredLevel\":7,\"Group\":4,\"ClassSpecific\":\"Amazon\",\"Class\":\"AmaOnly\",\"ClassLevelReq\":2,\"Types\":[\"weap\"],\"ETypes\":[\"swor\",\"axe\"],\"PType\":\"Suffix\",\"Lines\":[{\"key\":\"strDamage\",\"args\":[6,12]}]}]";
        var oldSnapshot = await CreateSnapshotAsync("old", magicSuffixes: oldAffix);
        var newSnapshot = await CreateSnapshotAsync("new", magicSuffixes: newAffix);

        var suffixes = GetCategory(await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot), "magicsuffix");
        var modified = Assert.Single(suffixes.ModifiedRecords);

        Assert.Equal((0, 0, 1), (suffixes.Counts.Added, suffixes.Counts.Removed, suffixes.Counts.Modified));
        Assert.Contains("Level", modified.ChangedFields);
        Assert.Contains("MaxLevel", modified.ChangedFields);
        Assert.Contains("RequiredLevel", modified.ChangedFields);
        Assert.Contains("Group", modified.ChangedFields);
        Assert.Contains("ClassSpecific", modified.ChangedFields);
        Assert.Contains("Class", modified.ChangedFields);
        Assert.Contains("ClassLevelReq", modified.ChangedFields);
        Assert.Contains("ETypes[Value=\"axe\"]", modified.ChangedFields);
        Assert.Equal("of Power", modified.Old.GetProperty("NameKey").GetString());
        Assert.Equal(7, modified.New.GetProperty("RequiredLevel").GetInt32());
    }

    [Fact]
    public async Task AffixPropertyLineOrderAndPropertyGroupSelectionChangesAreDetected()
    {
        const string oldAffix =
            "[{\"NameKey\":\"Grouped\",\"Level\":4,\"Group\":1,\"Types\":[],\"Lines\":[{\"key\":\"strPropertyGroupsProperty\",\"args\":[{\"pickMode\":\"1\",\"properties\":[{\"code\":\"cold\",\"chance\":10}]}]},{\"key\":\"strOtherProperty\",\"args\":[]}]}]";
        const string newAffix =
            "[{\"NameKey\":\"Grouped\",\"Level\":4,\"Group\":1,\"Types\":[],\"Lines\":[{\"key\":\"strPropertyGroupsProperty\",\"args\":[{\"pickMode\":\"2\",\"properties\":[{\"code\":\"cold\",\"chance\":20}]}]},{\"key\":\"strOtherProperty\",\"args\":[]}]}]";
        var oldSnapshot = await CreateSnapshotAsync("old", magicPrefixes: oldAffix);
        var newSnapshot = await CreateSnapshotAsync("new", magicPrefixes: newAffix);

        var prefixes = GetCategory(await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot), "magicprefix");
        var modified = Assert.Single(prefixes.ModifiedRecords);

        Assert.Contains("Lines[0].args[0].pickMode", modified.ChangedFields);
        Assert.Contains("Lines[0].args[0].properties[0].chance", modified.ChangedFields);
    }

    [Fact]
    public async Task AffixPropertyLineReorderingIsReportedAsAChange()
    {
        const string oldAffix =
            "[{\"NameKey\":\"Ordered\",\"Level\":4,\"Group\":1,\"Types\":[],\"Lines\":[{\"key\":\"strFirst\",\"args\":[]},{\"key\":\"strSecond\",\"args\":[]}]}]";
        const string newAffix =
            "[{\"NameKey\":\"Ordered\",\"Level\":4,\"Group\":1,\"Types\":[],\"Lines\":[{\"key\":\"strSecond\",\"args\":[]},{\"key\":\"strFirst\",\"args\":[]}]}]";
        var oldSnapshot = await CreateSnapshotAsync("old", magicPrefixes: oldAffix);
        var newSnapshot = await CreateSnapshotAsync("new", magicPrefixes: newAffix);

        var prefixes = GetCategory(await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot), "magicprefix");

        Assert.Contains("Lines[0].key", Assert.Single(prefixes.ModifiedRecords).ChangedFields);
    }

    [Fact]
    public async Task DuplicateNameAffixesMatchByPropertySignatureAndIgnoreEquipmentTypeOrdering()
    {
        const string oldAffixes =
            "[{\"NameKey\":\"Sharp\",\"Level\":10,\"Group\":2,\"Types\":[\"weap\",\"swor\"],\"ETypes\":[\"axe\",\"swor\"],\"Lines\":[{\"key\":\"strColdDamage\",\"args\":[1,2]}]},{\"NameKey\":\"Sharp\",\"Level\":10,\"Group\":2,\"Types\":[\"weap\",\"swor\"],\"ETypes\":[\"swor\",\"axe\"],\"Lines\":[{\"key\":\"strFireDamage\",\"args\":[3,4]}]}]";
        const string newAffixes =
            "[{\"NameKey\":\"Sharp\",\"Level\":10,\"Group\":2,\"Types\":[\"swor\",\"weap\"],\"ETypes\":[\"swor\",\"axe\"],\"Lines\":[{\"key\":\"strFireDamage\",\"args\":[3,4]}]},{\"NameKey\":\"Sharp\",\"Level\":10,\"Group\":2,\"Types\":[\"weap\",\"swor\"],\"ETypes\":[\"axe\",\"swor\"],\"Lines\":[{\"key\":\"strColdDamage\",\"args\":[1,2]}]}]";
        var oldSnapshot = await CreateSnapshotAsync("old", magicPrefixes: oldAffixes);
        var newSnapshot = await CreateSnapshotAsync("new", magicPrefixes: newAffixes);

        var prefixes = GetCategory(await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot), "magicprefix");

        Assert.Equal((0, 0, 0, 2),
            (prefixes.Counts.Added, prefixes.Counts.Removed, prefixes.Counts.Modified, prefixes.Counts.Unchanged));
        Assert.Empty(prefixes.Warnings);
    }

    [Fact]
    public async Task DuplicateNameNumericAffixRollChangesAreModificationsNotIdentityChurn()
    {
        const string oldAffixes =
            "[{\"NameKey\":\"Glowing\",\"Level\":5,\"Group\":1,\"Types\":[\"armo\"],\"Lines\":[{\"key\":\"strFireDamage\",\"args\":[5]}]},{\"NameKey\":\"Glowing\",\"Level\":5,\"Group\":1,\"Types\":[\"armo\"],\"Lines\":[{\"key\":\"strColdDamage\",\"args\":[8]}]}]";
        const string newAffixes =
            "[{\"NameKey\":\"Glowing\",\"Level\":5,\"Group\":1,\"Types\":[\"armo\"],\"Lines\":[{\"key\":\"strFireDamage\",\"args\":[6,10]}]},{\"NameKey\":\"Glowing\",\"Level\":5,\"Group\":1,\"Types\":[\"armo\"],\"Lines\":[{\"key\":\"strColdDamage\",\"args\":[8]}]}]";
        var oldSnapshot = await CreateSnapshotAsync("old", magicPrefixes: oldAffixes);
        var newSnapshot = await CreateSnapshotAsync("new", magicPrefixes: newAffixes);

        var prefixes = GetCategory(await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot), "magicprefix");

        Assert.Equal((0, 0, 1, 1),
            (prefixes.Counts.Added, prefixes.Counts.Removed, prefixes.Counts.Modified, prefixes.Counts.Unchanged));
        Assert.Equal("strFireDamage", Assert.Single(prefixes.ModifiedRecords).Old.GetProperty("Lines")[0]
            .GetProperty("key").GetString());
    }

    [Fact]
    public async Task DuplicateNameAffixLevelAndEligibleTypeChangesRemainModifications()
    {
        const string oldAffixes =
            "[{\"NameKey\":\"Mystic\",\"Level\":10,\"Group\":2,\"Types\":[\"armo\"],\"ETypes\":[\"helm\"],\"Lines\":[{\"key\":\"strFireDamage\",\"args\":[1]}]},{\"NameKey\":\"Mystic\",\"Level\":11,\"Group\":2,\"Types\":[\"armo\"],\"ETypes\":[\"helm\"],\"Lines\":[{\"key\":\"strColdDamage\",\"args\":[2]}]}]";
        const string newAffixes =
            "[{\"NameKey\":\"Mystic\",\"Level\":10,\"Group\":2,\"Types\":[\"armo\",\"shld\"],\"ETypes\":[\"helm\"],\"Lines\":[{\"key\":\"strFireDamage\",\"args\":[1]}]},{\"NameKey\":\"Mystic\",\"Level\":12,\"Group\":2,\"Types\":[\"armo\"],\"ETypes\":[\"helm\"],\"Lines\":[{\"key\":\"strColdDamage\",\"args\":[2]}]}]";
        var oldSnapshot = await CreateSnapshotAsync("old", magicPrefixes: oldAffixes);
        var newSnapshot = await CreateSnapshotAsync("new", magicPrefixes: newAffixes);

        var prefixes = GetCategory(await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot), "magicprefix");

        Assert.Equal((0, 0, 2), (prefixes.Counts.Added, prefixes.Counts.Removed, prefixes.Counts.Modified));
        Assert.Contains(prefixes.ModifiedRecords, static record => record.ChangedFields.Contains("Level"));
        Assert.Contains(prefixes.ModifiedRecords,
            static record =>
                record.ChangedFields.Any(path => path.StartsWith("Types[Value=", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task DuplicateNameAffixVariantsCanBeAddedAndRemovedIndependently()
    {
        const string oldAffixes =
            "[{\"NameKey\":\"Beryl\",\"Level\":8,\"Group\":1,\"Types\":[\"armo\"],\"Lines\":[{\"key\":\"strFireDamage\",\"args\":[1]}]},{\"NameKey\":\"Beryl\",\"Level\":8,\"Group\":1,\"Types\":[\"armo\"],\"Lines\":[{\"key\":\"strColdDamage\",\"args\":[2]}]}]";
        const string newAffixes =
            "[{\"NameKey\":\"Beryl\",\"Level\":8,\"Group\":1,\"Types\":[\"armo\"],\"Lines\":[{\"key\":\"strFireDamage\",\"args\":[1]}]},{\"NameKey\":\"Beryl\",\"Level\":8,\"Group\":2,\"Types\":[\"armo\"],\"Lines\":[{\"key\":\"strLightningDamage\",\"args\":[3]}]}]";
        var oldSnapshot = await CreateSnapshotAsync("old", magicPrefixes: oldAffixes);
        var newSnapshot = await CreateSnapshotAsync("new", magicPrefixes: newAffixes);

        var prefixes = GetCategory(await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot), "magicprefix");

        Assert.Equal((1, 1, 0), (prefixes.Counts.Added, prefixes.Counts.Removed, prefixes.Counts.Modified));
        Assert.Contains("Properties=", Assert.Single(prefixes.RemovedRecords).Key);
        Assert.Contains("Group=2", Assert.Single(prefixes.AddedRecords).Key);
    }

    [Fact]
    public async Task UnresolvedDuplicateNameAffixesAreNotPairedAndWarnAboutAmbiguity()
    {
        const string identicalAffixes =
            "[{\"NameKey\":\"Sapphire\",\"Level\":9,\"Group\":2,\"Types\":[\"armo\"],\"Lines\":[{\"key\":\"strColdDamage\",\"args\":[2]}]},{\"NameKey\":\"Sapphire\",\"Level\":9,\"Group\":2,\"Types\":[\"armo\"],\"Lines\":[{\"key\":\"strColdDamage\",\"args\":[2]}]}]";
        var oldSnapshot = await CreateSnapshotAsync("old", magicPrefixes: identicalAffixes);
        var newSnapshot = await CreateSnapshotAsync("new", magicPrefixes: identicalAffixes);

        var prefixes = GetCategory(await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot), "magicprefix");

        Assert.Equal((2, 2, 0), (prefixes.Counts.Added, prefixes.Counts.Removed, prefixes.Counts.Modified));
        var warning = Assert.Single(prefixes.Warnings, static issue => issue.Code == "ambiguous-affix-identity");
        Assert.Contains("Sapphire", warning.Message);
        var unresolvedRecords = prefixes.AddedRecords.Concat(prefixes.RemovedRecords).ToList();
        Assert.Equal(4, unresolvedRecords.Select(static record => record.Key).Distinct(StringComparer.Ordinal).Count());
        Assert.All(unresolvedRecords, record => Assert.Contains(record.Key, warning.Message, StringComparison.Ordinal));
    }

    [Fact]
    public async Task DuplicateNamePropertyKeyChangePairsByMetadataAndReportsMatchingRule()
    {
        const string oldAffixes =
            "[{\"NameKey\":\"Frosted\",\"Level\":8,\"Group\":1,\"Types\":[\"armo\"],\"Lines\":[{\"key\":\"strFireDamage\",\"args\":[1]}]},{\"NameKey\":\"Frosted\",\"Level\":8,\"Group\":1,\"Types\":[\"armo\"],\"Lines\":[{\"key\":\"strColdDamage\",\"args\":[2]}]}]";
        const string newAffixes =
            "[{\"NameKey\":\"Frosted\",\"Level\":8,\"Group\":1,\"Types\":[\"armo\"],\"Lines\":[{\"key\":\"strFireDamage\",\"args\":[1]}]},{\"NameKey\":\"Frosted\",\"Level\":8,\"Group\":1,\"Types\":[\"armo\"],\"Lines\":[{\"key\":\"strLightningDamage\",\"args\":[2]}]}]";
        var oldSnapshot = await CreateSnapshotAsync("old", magicPrefixes: oldAffixes);
        var newSnapshot = await CreateSnapshotAsync("new", magicPrefixes: newAffixes);

        var prefixes = GetCategory(await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot), "magicprefix");
        var modified = Assert.Single(prefixes.ModifiedRecords);

        Assert.Equal("StructuralMetadata", modified.MatchedBy);
        Assert.Contains("Lines[0].key", modified.ChangedFields);
    }

    [Fact]
    public async Task PrefixAndSuffixAffixesAreIndependentAndUnorderedTypesDoNotModifyUniqueAffixes()
    {
        const string oldAffix =
            "[{\"NameKey\":\"Shared Name\",\"Level\":1,\"Group\":1,\"Types\":[\"armo\",\"weap\"],\"ETypes\":[\"helm\",\"swor\"],\"Lines\":[{\"key\":\"strDamage\",\"args\":[1,2]}]}]";
        const string reorderedAffix =
            "[{\"NameKey\":\"Shared Name\",\"Level\":1,\"Group\":1,\"Types\":[\"weap\",\"armo\"],\"ETypes\":[\"swor\",\"helm\"],\"Lines\":[{\"key\":\"strDamage\",\"args\":[1,2]}]}]";
        var oldSnapshot = await CreateSnapshotAsync("old", magicPrefixes: oldAffix, magicSuffixes: oldAffix);
        var newSnapshot =
            await CreateSnapshotAsync("new", magicPrefixes: reorderedAffix, magicSuffixes: reorderedAffix);

        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);

        Assert.Equal(1, GetCategory(report, "magicprefix").Counts.Unchanged);
        Assert.Equal(1, GetCategory(report, "magicsuffix").Counts.Unchanged);
        Assert.Equal(2, report.Counts.Unchanged);
    }

    [Fact]
    public async Task MissingAffixManifestEntryDoesNotBlockTheOtherCategory()
    {
        var oldSnapshot = await CreateSnapshotAsync("old",
            magicPrefixes: "[{\"NameKey\":\"Valid\",\"Level\":1,\"Group\":1,\"Types\":[],\"Lines\":[]}]",
            magicSuffixes: "[{\"NameKey\":\"Valid\",\"Level\":1,\"Group\":1,\"Types\":[],\"Lines\":[]}]",
            omitFromManifest: [MagicPrefixPath]);
        var newSnapshot = await CreateSnapshotAsync("new",
            magicPrefixes: "[{\"NameKey\":\"Valid\",\"Level\":1,\"Group\":1,\"Types\":[],\"Lines\":[]}]",
            magicSuffixes: "[{\"NameKey\":\"Valid\",\"Level\":1,\"Group\":1,\"Types\":[],\"Lines\":[]}] ");

        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);

        Assert.Equal("NotCompared", GetCategory(report, "magicprefix").Status);
        Assert.Equal("Compared", GetCategory(report, "magicsuffix").Status);
        Assert.NotEmpty(GetCategory(report, "magicprefix").Errors);
    }

    [Fact]
    public async Task InvalidAffixTypeArrayInvalidatesOnlyItsCategory()
    {
        const string validAffix = "[{\"NameKey\":\"Valid\",\"Level\":1,\"Group\":1,\"Types\":[],\"Lines\":[]}]";
        var oldSnapshot = await CreateSnapshotAsync("old", magicPrefixes: validAffix, magicSuffixes: validAffix);
        var newSnapshot = await CreateSnapshotAsync("new", magicPrefixes: validAffix,
            magicSuffixes: "[{\"NameKey\":\"Invalid\",\"Level\":1,\"Group\":1,\"Types\":[4],\"Lines\":[]}] ");

        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);

        Assert.Equal("Compared", GetCategory(report, "magicprefix").Status);
        Assert.Equal("NotCompared", GetCategory(report, "magicsuffix").Status);
        Assert.Contains(GetCategory(report, "magicsuffix").Errors,
            static issue => issue.Code == "invalid-affix-identity");
    }

    [Fact]
    public async Task MissingAffixNameKeyInvalidatesOnlyThatCategory()
    {
        var oldSnapshot = await CreateSnapshotAsync("old",
            magicPrefixes: "[{\"Level\":1,\"Group\":1,\"Types\":[],\"Lines\":[]}]",
            magicSuffixes: "[]");
        var newSnapshot = await CreateSnapshotAsync("new",
            magicPrefixes: "[{\"Level\":1,\"Group\":1,\"Types\":[],\"Lines\":[]}]",
            magicSuffixes: "[]");

        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);

        Assert.Equal("NotCompared", GetCategory(report, "magicprefix").Status);
        Assert.Contains(GetCategory(report, "magicprefix").Errors,
            static issue => issue.Code == "missing-record-identity");
        Assert.Equal("Compared", GetCategory(report, "magicsuffix").Status);
    }

    [Fact]
    public void OutputDirectoryInsideAnInputSnapshotIsRefused()
    {
        var oldSnapshot = Path.Combine(_root, "old");
        var newSnapshot = Path.Combine(_root, "new");

        Assert.NotNull(SnapshotComparison.FindOutputConflict(oldSnapshot, newSnapshot, oldSnapshot));
        Assert.NotNull(SnapshotComparison.FindOutputConflict(oldSnapshot, newSnapshot,
            Path.Combine(newSnapshot, "reports")));
        Assert.Null(SnapshotComparison.FindOutputConflict(oldSnapshot, newSnapshot, Path.Combine(_root, "comparison")));
    }

    private async Task<string> CreateSnapshotAsync(
        string name,
        string cubeRecipes = "[]",
        string uniques = "[]",
        string sets = "[]",
        string runewords = "[]",
        string armors = "[]",
        string weapons = "[]",
        string magicPrefixes = "[]",
        string magicSuffixes = "[]",
        string stepStatus = "Written",
        bool succeeded = true,
        bool complete = true,
        IReadOnlyCollection<string>? omitFromManifest = null,
        IReadOnlyDictionary<string, string>? hashOverrides = null)
    {
        var snapshot = Directory.CreateDirectory(Path.Combine(_root, name)).FullName;
        var payloads = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [CubeRecipePath] = cubeRecipes,
            [UniquePath] = uniques,
            [SetPath] = sets,
            [RunewordPath] = runewords,
            [ArmorPath] = armors,
            [WeaponPath] = weapons,
            [MagicPrefixPath] = magicPrefixes,
            [MagicSuffixPath] = magicSuffixes
        };
        var files = new List<object>();
        foreach (var (relativePath, content) in payloads)
        {
            var path = Path.Combine(snapshot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var bytes = Encoding.UTF8.GetBytes(content);
            await File.WriteAllBytesAsync(path, bytes);
            if (stepStatus != "Written" ||
                omitFromManifest?.Contains(relativePath, StringComparer.Ordinal) == true) continue;
            var hash = hashOverrides?.GetValueOrDefault(relativePath) ??
                       Convert.ToHexStringLower(SHA256.HashData(bytes));
            files.Add(new { Path = relativePath, Bytes = bytes.LongLength, Sha256 = hash, Producer = "keyed-core" });
        }

        var outputs = stepStatus == "Written" ? CategoryFiles : [];
        var notWritten = stepStatus == "Written" ? Array.Empty<string>() : CategoryFiles;
        var manifest = new
        {
            SchemaVersion = 2,
            Succeeded = succeeded,
            Complete = complete,
            Steps = new[]
            {
                new { Producer = "keyed-core", Status = stepStatus, Outputs = outputs, NotWritten = notWritten }
            },
            Files = files
        };
        await File.WriteAllTextAsync(Path.Combine(snapshot, "manifest.json"), JsonSerializer.Serialize(manifest));
        return snapshot;
    }

    private static CategoryComparison GetCategory(ComparisonReport report, string category) =>
        Assert.Single(report.Categories, result => result.Category == category);

    private static object CreateRuneword(string index, string[] runes, string[] types) => new
    {
        Index = index,
        Enabled = true,
        Runes = runes.Select(static nameKey => new { NameKey = nameKey }).ToArray(),
        Types = types.Select(static type => new { Index = type }).ToArray(),
        Lines = Array.Empty<object>()
    };
}
