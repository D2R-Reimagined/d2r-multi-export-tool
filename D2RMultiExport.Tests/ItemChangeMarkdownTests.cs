// SPDX-License-Identifier: GPL-3.0-or-later

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using D2RMultiExport.Lib.Comparison;

namespace D2RMultiExport.Tests;

/// <summary>Focused regressions for human-readable item comparison Markdown.</summary>
public sealed class ItemChangeMarkdownTests : IDisposable
{
    private static readonly string[] CategoryFiles =
    [
        "keyed/cube-recipes.json", "keyed/uniques.json", "keyed/sets.json", "keyed/runewords.json",
        "keyed/armors.json", "keyed/weapons.json", "keyed/magicprefix.json", "keyed/magicsuffix.json"
    ];

    private readonly string _root = Directory.CreateTempSubdirectory("d2r-item-markdown-").FullName;

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task UniqueAddedRemovedAndModifiedRecordsShowReadableBeforeAfterValues()
    {
        const string oldUniques =
            "[{\"Index\":\"Removed Unique\",\"Code\":\"old\"},"
            + "{\"Index\":\"Changed Unique\",\"Code\":\"cap\",\"RequiredLevel\":20,\"Lines\":[{\"key\":\"damage\",\"args\":[10,20]}]},"
            + "{\"Index\":\"Unchanged Unique\",\"Lines\":[]}]";
        const string newUniques =
            "[{\"Index\":\"Added Unique\",\"Code\":\"new\"},"
            + "{\"Index\":\"Changed Unique\",\"Code\":\"cap\",\"RequiredLevel\":25,\"Lines\":[{\"key\":\"damage\",\"args\":[15,30]}]},"
            + "{\"Index\":\"Unchanged Unique\",\"Lines\":[]}]";
        var oldSnapshot = await CreateSnapshotAsync("old", new Dictionary<string, string>
        {
            ["keyed/uniques.json"] = oldUniques
        }, "{\"damage\":\"%+d old damage\"}");
        var newSnapshot = await CreateSnapshotAsync("new", new Dictionary<string, string>
        {
            ["keyed/uniques.json"] = newUniques
        }, "{\"damage\":\"%+d new damage\"}");
        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);

        var first = await SnapshotComparison.RenderItemChangesAsync(report);
        var second = await SnapshotComparison.RenderItemChangesAsync(report);

        Assert.Contains("#### Added Unique", first);
        Assert.Contains("#### Removed Unique", first);
        Assert.Contains("#### Changed Unique", first);
        Assert.Contains("Before: \\+(10-20) old damage", first);
        Assert.Contains("After: \\+(15-30) new damage", first);
        Assert.Contains("Before: 20", first);
        Assert.Contains("After: 25", first);
        Assert.DoesNotContain("#### Unchanged Unique", first);
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task SetSharedBonusesAndOnlyChangedPieceArePresentedSeparately()
    {
        const string oldSets =
            "[{\"Index\":\"Set Alpha\",\"PartialBonuses\":[{\"key\":\"bonus\",\"args\":[10]}],\"SetItems\":["
            + "{\"Index\":\"Piece A\",\"Lines\":[{\"key\":\"damage\",\"args\":[1]}]},"
            + "{\"Index\":\"Piece B\",\"Lines\":[{\"key\":\"damage\",\"args\":[2]}]}]}]";
        const string newSets =
            "[{\"Index\":\"Set Alpha\",\"PartialBonuses\":[{\"key\":\"bonus\",\"args\":[15]}],\"SetItems\":["
            + "{\"Index\":\"Piece A\",\"Lines\":[{\"key\":\"damage\",\"args\":[3]}]},"
            + "{\"Index\":\"Piece B\",\"Lines\":[{\"key\":\"damage\",\"args\":[2]}]}]}]";
        var oldSnapshot = await CreateSnapshotAsync("old", new Dictionary<string, string>
        {
            ["keyed/sets.json"] = oldSets
        }, "{\"damage\":\"Damage %d\",\"bonus\":\"%d shared bonus\"}");
        var newSnapshot = await CreateSnapshotAsync("new", new Dictionary<string, string>
        {
            ["keyed/sets.json"] = newSets
        }, "{\"damage\":\"Damage %d\",\"bonus\":\"%d shared bonus\"}");

        var markdown = await SnapshotComparison.RenderItemChangesAsync(
            await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot));

        Assert.Contains("10 shared bonus", markdown);
        Assert.Contains("15 shared bonus", markdown);
        Assert.Contains("### Nested Modified", markdown);
        Assert.Contains("#### Piece A", markdown);
        Assert.DoesNotContain("#### Piece B", markdown);
        Assert.Contains("Before: Damage 1", markdown);
        Assert.Contains("After: Damage 3", markdown);
    }

    [Fact]
    public async Task RunewordVariantsAndRuneSequenceAreKeptDistinctAndOrdered()
    {
        const string oldRunewords =
            "[{\"Index\":\"Leaf\",\"RequiredLevel\":10,\"Runes\":[{\"NameKey\":\"El\"},{\"NameKey\":\"Tir\"}],\"Types\":[{\"Index\":\"sword\"}]},"
            + "{\"Index\":\"Leaf\",\"RequiredLevel\":10,\"Runes\":[{\"NameKey\":\"El\"},{\"NameKey\":\"Tir\"}],\"Types\":[{\"Index\":\"bow\"}]}]";
        const string newRunewords =
            "[{\"Index\":\"Leaf\",\"RequiredLevel\":12,\"Runes\":[{\"NameKey\":\"Tir\"},{\"NameKey\":\"El\"}],\"Types\":[{\"Index\":\"sword\"}]},"
            + "{\"Index\":\"Leaf\",\"RequiredLevel\":10,\"Runes\":[{\"NameKey\":\"El\"},{\"NameKey\":\"Tir\"}],\"Types\":[{\"Index\":\"bow\"}]}]";
        var oldSnapshot = await CreateSnapshotAsync("old", new Dictionary<string, string>
        {
            ["keyed/runewords.json"] = oldRunewords
        });
        var newSnapshot = await CreateSnapshotAsync("new", new Dictionary<string, string>
        {
            ["keyed/runewords.json"] = newRunewords
        });

        var markdown = await SnapshotComparison.RenderItemChangesAsync(
            await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot));

        Assert.Contains("VariantTypes (preserved by the comparator)", markdown);
        Assert.Contains("Rune 1 (sequence order)", markdown);
        Assert.Contains("NameKey", markdown);
        Assert.Contains("RequiredLevel", markdown);
        Assert.Contains("sword", markdown);
        Assert.Contains("Before: `El`", markdown);
        Assert.Contains("After: `Tir`", markdown);
    }

    [Fact]
    public async Task ArmorNestedAutomagicChangesIdentifyOnlyAffectedGroups()
    {
        const string oldArmors =
            "[{\"NameKey\":\"strArmor\",\"RequiredLevel\":5,\"Equipment\":{\"DamageTypes\":[{\"Type\":1,\"AverageDamage\":10,\"Lines\":[{\"key\":\"damage\",\"args\":[10,20]}]}]},\"AutoMagicGroups\":["
            + "{\"Code\":\"group-a\",\"Lines\":[{\"key\":\"damage\",\"args\":[1]}]},"
            + "{\"Code\":\"group-b\",\"Lines\":[{\"key\":\"damage\",\"args\":[2]}]}]}]";
        const string newArmors =
            "[{\"NameKey\":\"strArmor\",\"RequiredLevel\":6,\"Equipment\":{\"DamageTypes\":[{\"Type\":1,\"AverageDamage\":12,\"Lines\":[{\"key\":\"damage\",\"args\":[11,21]}]}]},\"AutoMagicGroups\":["
            + "{\"Code\":\"group-a\",\"Lines\":[{\"key\":\"damage\",\"args\":[3]}]},"
            + "{\"Code\":\"group-b\",\"Lines\":[{\"key\":\"damage\",\"args\":[2]}]},"
            + "{\"Code\":\"group-c\",\"Lines\":[]}]}]";
        var oldSnapshot = await CreateSnapshotAsync("old", new Dictionary<string, string>
        {
            ["keyed/armors.json"] = oldArmors
        }, "{\"strArmor\":\"Armor\",\"damage\":\"%d physical damage\"}");
        var newSnapshot = await CreateSnapshotAsync("new", new Dictionary<string, string>
        {
            ["keyed/armors.json"] = newArmors
        }, "{\"strArmor\":\"Armor\",\"damage\":\"%d physical damage\"}");

        var markdown = await SnapshotComparison.RenderItemChangesAsync(
            await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot));

        Assert.Contains("possible automagic groups", markdown);
        Assert.Contains("group-a", markdown);
        Assert.Contains("group-c", markdown);
        Assert.DoesNotContain("#### group-b", markdown);
        Assert.Contains("RequiredLevel", markdown);
        Assert.Contains("AverageDamage", markdown);
        Assert.Contains("Before: 10", markdown);
        Assert.Contains("After: 12", markdown);
        Assert.Contains("(10-20) physical damage", markdown);
        Assert.Contains("(11-21) physical damage", markdown);
    }

    [Fact]
    public async Task AffixMatchRuleAndAmbiguousCandidatesRemainExplicit()
    {
        const string oldAffixes =
            "[{\"NameKey\":\"Frosted\",\"Level\":8,\"Group\":1,\"Types\":[\"armo\"],\"Lines\":[{\"key\":\"fire\",\"args\":[1]}]},"
            + "{\"NameKey\":\"Frosted\",\"Level\":8,\"Group\":1,\"Types\":[\"armo\"],\"Lines\":[{\"key\":\"cold\",\"args\":[2]}]},"
            + "{\"NameKey\":\"Sapphire\",\"Level\":9,\"Group\":2,\"Types\":[\"armo\"],\"Lines\":[{\"key\":\"cold\",\"args\":[2]}]},"
            + "{\"NameKey\":\"Sapphire\",\"Level\":9,\"Group\":2,\"Types\":[\"armo\"],\"Lines\":[{\"key\":\"cold\",\"args\":[2]}]}]";
        const string newAffixes =
            "[{\"NameKey\":\"Frosted\",\"Level\":8,\"Group\":1,\"Types\":[\"armo\"],\"Lines\":[{\"key\":\"fire\",\"args\":[1]}]},"
            + "{\"NameKey\":\"Frosted\",\"Level\":8,\"Group\":1,\"Types\":[\"armo\"],\"Lines\":[{\"key\":\"lightning\",\"args\":[2]}]},"
            + "{\"NameKey\":\"Sapphire\",\"Level\":9,\"Group\":2,\"Types\":[\"armo\"],\"Lines\":[{\"key\":\"cold\",\"args\":[2]}]},"
            + "{\"NameKey\":\"Sapphire\",\"Level\":9,\"Group\":2,\"Types\":[\"armo\"],\"Lines\":[{\"key\":\"cold\",\"args\":[2]}]}]";
        var oldSnapshot = await CreateSnapshotAsync("old", new Dictionary<string, string>
        {
            ["keyed/magicprefix.json"] = oldAffixes
        }, "{\"Frosted\":\"Frosted\",\"fire\":\"Fire %d\",\"cold\":\"Cold %d\",\"lightning\":\"Lightning %d\"}");
        var newSnapshot = await CreateSnapshotAsync("new", new Dictionary<string, string>
        {
            ["keyed/magicprefix.json"] = newAffixes
        }, "{\"Frosted\":\"Frosted\",\"fire\":\"Fire %d\",\"cold\":\"Cold %d\",\"lightning\":\"Lightning %d\"}");

        var markdown = await SnapshotComparison.RenderItemChangesAsync(
            await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot));

        Assert.Contains("MatchedBy: `StructuralMetadata`", markdown);
        Assert.Contains("Warnings and ambiguous identity matches", markdown);
        Assert.Contains("ambiguous-affix-identity", markdown);
        Assert.Contains("unresolved ambiguous candidate", markdown);
        Assert.Contains("not a confirmed item removal/addition", markdown);
    }

    [Fact]
    public async Task MissingTranslationFallsBackToKeyAndUnavailableCategoryIsNotZeroChanges()
    {
        var oldSnapshot = await CreateSnapshotAsync("old", new Dictionary<string, string>
        {
            ["keyed/magicprefix.json"] = "{}"
        });
        var newSnapshot = await CreateSnapshotAsync("new", new Dictionary<string, string>
        {
            ["keyed/uniques.json"] = "[{\"Index\":\"Fallback Item\",\"Lines\":[{\"key\":\"missingKey\",\"args\":[7]}]}]"
        });

        var markdown = await SnapshotComparison.RenderItemChangesAsync(
            await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot));

        Assert.Contains("missingKey \\[7\\]", markdown);
        Assert.Contains("Magic prefixes (`magicprefix`) | NotCompared | unavailable", markdown);
        Assert.Contains("**Comparison unavailable: NotCompared.**", markdown);
        Assert.Contains("does not mean that there were no changes", markdown);
        Assert.DoesNotContain("| Magic prefixes (`magicprefix`) | NotCompared | 0 |", markdown);
        Assert.Contains("Unique items (`uniques`)", markdown);
        Assert.Contains("Sets and set pieces (`sets`)", markdown);
        Assert.Contains("Runewords (`runewords`)", markdown);
        Assert.Contains("Base armor (`armors`)", markdown);
        Assert.Contains("Base weapons (`weapons`)", markdown);
        Assert.Contains("Magic suffixes (`magicsuffix`)", markdown);
    }

    private async Task<string> CreateSnapshotAsync(string name, IReadOnlyDictionary<string, string> overrides,
        string translations = "{}")
    {
        var snapshot = Directory.CreateDirectory(Path.Combine(_root, name)).FullName;
        var files = new List<object>();
        foreach (var relativePath in CategoryFiles)
        {
            var content = overrides.GetValueOrDefault(relativePath, "[]");
            var path = Path.Combine(snapshot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var bytes = Encoding.UTF8.GetBytes(content);
            await File.WriteAllBytesAsync(path, bytes);
            files.Add(new
            {
                Path = relativePath,
                Bytes = bytes.LongLength,
                Sha256 = Sha256(bytes),
                Producer = "keyed-core"
            });
        }

        const string translationsPath = "strings/enUS.json";
        var translationsFile = Path.Combine(snapshot, "strings", "enUS.json");
        Directory.CreateDirectory(Path.GetDirectoryName(translationsFile)!);
        var translationBytes = Encoding.UTF8.GetBytes(translations);
        await File.WriteAllBytesAsync(translationsFile, translationBytes);
        files.Add(new
        {
            Path = translationsPath,
            Bytes = translationBytes.LongLength,
            Sha256 = Sha256(translationBytes),
            Producer = "strings"
        });

        var manifest = new
        {
            SchemaVersion = 2,
            Succeeded = true,
            Complete = true,
            Steps = new object[]
            {
                new
                {
                    Producer = "keyed-core",
                    Status = "Written",
                    Outputs = CategoryFiles,
                    NotWritten = Array.Empty<string>()
                },
                new
                {
                    Producer = "strings",
                    Status = "Written",
                    Outputs = new[] { translationsPath },
                    NotWritten = Array.Empty<string>()
                }
            },
            Files = files
        };
        await File.WriteAllTextAsync(Path.Combine(snapshot, "manifest.json"), JsonSerializer.Serialize(manifest));
        return snapshot;
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
