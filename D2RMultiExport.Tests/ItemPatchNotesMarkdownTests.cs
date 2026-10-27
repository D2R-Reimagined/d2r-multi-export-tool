// SPDX-License-Identifier: GPL-3.0-or-later

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using D2RMultiExport.Lib.Comparison;

namespace D2RMultiExport.Tests;

/// <summary>Focused regressions for player-facing item patch notes.</summary>
public sealed class ItemPatchNotesMarkdownTests : IDisposable
{
    private static readonly string[] CategoryFiles =
    [
        "keyed/cube-recipes.json", "keyed/uniques.json", "keyed/sets.json", "keyed/runewords.json",
        "keyed/armors.json", "keyed/weapons.json", "keyed/magicprefix.json", "keyed/magicsuffix.json"
    ];

    private readonly string _root = Directory.CreateTempSubdirectory("d2r-item-patch-notes-").FullName;

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task ExistingMarkdownReportsKeepTheirBaselineOutput()
    {
        const string translations = "{\"uniqueName\":\"Baseline Unique\",\"damage\":\"+%d Damage\"}";
        var oldSnapshot = await CreateSnapshotAsync("old",
            "[{\"Id\":\"recipe\",\"Index\":1,\"Description\":\"Before\"}]",
            "[{\"Index\":\"Unique\",\"NameKey\":\"uniqueName\",\"Lines\":[{\"key\":\"damage\",\"args\":[1]}]}]",
            translations);
        var newSnapshot = await CreateSnapshotAsync("new",
            "[{\"Id\":\"recipe\",\"Index\":1,\"Description\":\"After\"}]",
            "[{\"Index\":\"Unique\",\"NameKey\":\"uniqueName\",\"Lines\":[{\"key\":\"damage\",\"args\":[2]}]}]",
            translations);
        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);

        var itemChanges = NormalizePaths(await SnapshotComparison.RenderItemChangesAsync(report), oldSnapshot,
            newSnapshot);
        var recipeChanges = NormalizePaths(await SnapshotComparison.RenderRecipeChangesAsync(report), oldSnapshot,
            newSnapshot);
        var itemPatchNotes = NormalizePaths(await SnapshotComparison.RenderItemPatchNotesAsync(report), oldSnapshot,
            newSnapshot);
        var outputDirectory = Directory.CreateDirectory(Path.Combine(_root, "comparison")).FullName;
        await SnapshotComparison.WriteAsync(report, outputDirectory);
        var changesJson = NormalizeJsonPaths(
            await File.ReadAllTextAsync(Path.Combine(outputDirectory, "changes.json")), oldSnapshot, newSnapshot);

        Assert.Equal("33223a5850e90335f4a6becfc7fad91730cd1044fd9dc55cd436977bdcc2f4a2",
            Sha256(Encoding.UTF8.GetBytes(itemChanges)));
        Assert.Equal("b3ed1b58d7dfac491c5ff805f15403ac23114849dfa205919fce1eb5c1de1d56",
            Sha256(Encoding.UTF8.GetBytes(recipeChanges)));
        Assert.Equal("b2163ac8a25a95668e93f5f30934e184614107baf7c83bde878689ab9ccfe77e",
            Sha256(Encoding.UTF8.GetBytes(itemPatchNotes)));
        Assert.Equal("d312736ad9f5763a424e1ca066b661b006febcfcc21726ad03dd431d7386dfa5",
            Sha256(Encoding.UTF8.GetBytes(changesJson)));
    }

    [Fact]
    public async Task NumericChangesAndPropertyAdditionsAndRemovalsAreConcise()
    {
        const string translations = "{\"uniqueName\":\"Baseline Unique\",\"damage\":\"%+d-%d Damage\","
                                    + "\"added\":\"+%d Attack Speed\",\"removed\":\"+%d Deadly Strike\"}";
        const string oldUnique =
            "[{\"Index\":\"Unique\",\"NameKey\":\"uniqueName\",\"Rarity\":3,\"ReqLevel\":40,\"Lines\":["
            + "{\"key\":\"damage\",\"args\":[10,20]},{\"key\":\"removed\",\"args\":[15]}]}]";
        const string newUnique =
            "[{\"Index\":\"Unique\",\"NameKey\":\"uniqueName\",\"Rarity\":1,\"ReqLevel\":35,\"Lines\":["
            + "{\"key\":\"damage\",\"args\":[10,25]},{\"key\":\"added\",\"args\":[10]}]}]";
        var oldSnapshot = await CreateSnapshotAsync("old", "[]", "[]", translations,
            new Dictionary<string, string> { ["keyed/uniques.json"] = oldUnique });
        var newSnapshot = await CreateSnapshotAsync("new", "[]", "[]", translations,
            new Dictionary<string, string> { ["keyed/uniques.json"] = newUnique });

        var markdown = await SnapshotComparison.RenderItemPatchNotesAsync(
            await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot));

        Assert.Contains("Changed: +10-25 Damage (was +10-20)", markdown);
        Assert.Contains("Added: +10 Attack Speed", markdown);
        Assert.Contains("Removed: +15 Deadly Strike", markdown);
        Assert.Contains("Required Level: 35 (was 40)", markdown);
        Assert.Contains("Rarity: 1 (was 3)", markdown);
        Assert.DoesNotContain("Index:", markdown);
        Assert.DoesNotContain("Stable comparison identity", markdown);
    }

    [Fact]
    public async Task NumericRangeTemplatesHaveSingleParentheses()
    {
        const string unique = "[{\"Index\":\"Socketed Item\",\"Lines\":[{\"key\":\"sockets\",\"args\":[2,3]}]}]";
        var oldSnapshot = await CreateSnapshotAsync("old", "[]", "[]", "{\"sockets\":\"Socketed (%d)\"}");
        var newSnapshot = await CreateSnapshotAsync("new", "[]", unique, "{\"sockets\":\"Socketed (%d)\"}");

        var markdown = await SnapshotComparison.RenderItemPatchNotesAsync(
            await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot));

        Assert.Contains("Socketed (2-3)", markdown);
        Assert.DoesNotContain("Socketed ((2-3))", markdown);
    }

    [Fact]
    public async Task AddedAndRemovedItemsIncludeMetadataAndEveryReadableProperty()
    {
        const string translations = "{\"damage\":\"+%d Damage\",\"defense\":\"%d Defense\"}";
        const string oldUnique = "[{\"Index\":\"Removed Relic\",\"ItemLevel\":5,\"ReqLevel\":20,\"Lines\":["
                                 + "{\"key\":\"damage\",\"args\":[1]}]}]";
        const string newUnique = "[{\"Index\":\"Added Relic\",\"ItemLevel\":25,\"Rarity\":1,\"Lines\":["
                                 + "{\"key\":\"damage\",\"args\":[2]},{\"key\":\"defense\",\"args\":[3]}]}]";
        var oldSnapshot = await CreateSnapshotAsync("old", "[]", oldUnique, translations);
        var newSnapshot = await CreateSnapshotAsync("new", "[]", newUnique, translations);

        var markdown = await SnapshotComparison.RenderItemPatchNotesAsync(
            await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot));

        Assert.Contains("Added Relic", markdown);
        Assert.Contains("Removed Relic", markdown);
        Assert.Contains("Item Level: 25", markdown);
        Assert.Contains("Required Level: 20", markdown);
        Assert.Contains("Rarity: 1", markdown);
        Assert.Contains("+2 Damage", markdown);
        Assert.Contains("3 Defense", markdown);
        Assert.Contains("+1 Damage", markdown);
    }

    [Fact]
    public async Task KeyedLineQualifiersAndPerLevelContextRemainReadable()
    {
        const string translations = "{\"damage\":\"+%d Damage\",\"amazon\":\"Amazon Only\","
                                    + "\"strPerCharacterLevelSuffix\":\"per character level\"}";
        const string unique = "[{\"Index\":\"Qualified Item\",\"Lines\":[{\"key\":\"damage\",\"args\":[5],"
                              + "\"classOnly\":\"amazon\",\"itemsRequired\":2,\"qualifier\":\"Weapon\",\"perLevel\":true}]}]";
        var oldSnapshot = await CreateSnapshotAsync("old", "[]", "[]", translations);
        var newSnapshot = await CreateSnapshotAsync("new", "[]", unique, translations);

        var markdown = await SnapshotComparison.RenderItemPatchNotesAsync(
            await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot));

        Assert.Contains("+5 Damage", markdown);
        Assert.Contains("Amazon Only", markdown);
        Assert.Contains("2 Items", markdown);
        Assert.Contains("Weapon only", markdown);
        Assert.Contains("per character level", markdown);
        Assert.Contains("Weapon only) per character level", markdown);
    }

    [Fact]
    public async Task OldAndNewPropertiesUseTheirOwnManifestVerifiedTranslations()
    {
        const string oldUnique = "[{\"Index\":\"Translated Item\",\"Lines\":[{\"key\":\"damage\",\"args\":[1]}]}]";
        const string newUnique = "[{\"Index\":\"Translated Item\",\"Lines\":[{\"key\":\"damage\",\"args\":[2]}]}]";
        var oldSnapshot = await CreateSnapshotAsync("old", "[]", oldUnique, "{\"damage\":\"Old %d Fire\"}");
        var newSnapshot = await CreateSnapshotAsync("new", "[]", newUnique, "{\"damage\":\"New %d Fire\"}");

        var markdown = await SnapshotComparison.RenderItemPatchNotesAsync(
            await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot));

        Assert.Contains("Changed: New 2 Fire (was Old 1)", markdown);
    }

    [Fact]
    public async Task DuplicateNumericPropertyIdentitiesRemainAdditionsAndRemovals()
    {
        const string translations = "{\"damage\":\"+%d%% Damage\"}";
        const string oldUnique = "[{\"Index\":\"Duplicate Test\",\"Lines\":["
                                 + "{\"key\":\"damage\",\"args\":[1]},{\"key\":\"damage\",\"args\":[2]}]}]";
        const string newUnique = "[{\"Index\":\"Duplicate Test\",\"Lines\":[{\"key\":\"damage\",\"args\":[3]}]}]";
        var oldSnapshot = await CreateSnapshotAsync("old", "[]", oldUnique, translations);
        var newSnapshot = await CreateSnapshotAsync("new", "[]", newUnique, translations);

        var markdown = await SnapshotComparison.RenderItemPatchNotesAsync(
            await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot));

        Assert.Contains("Added: +3% Damage", markdown);
        Assert.Contains("Removed: +1% Damage", markdown);
        Assert.Contains("Removed: +2% Damage", markdown);
        Assert.DoesNotContain("Changed: +3% Damage", markdown);
    }

    [Fact]
    public async Task NumericChangesPairBySkillAndPreserveTriggerAndSkillChanges()
    {
        const string translations = "{\"proc\":\"%d %s\",\"holyShock\":\"Holy Shock\",\"frozenOrb\":\"Frozen Orb\"}";
        const string oldUnique = "[{\"Index\":\"Proc Item\",\"Lines\":["
                                 + "{\"key\":\"proc\",\"args\":[10,\"holyShock\"],\"trigger\":\"onHit\"},"
                                 + "{\"key\":\"proc\",\"args\":[20,\"frozenOrb\"],\"trigger\":\"onHit\"}]}]";
        const string newUnique = "[{\"Index\":\"Proc Item\",\"Lines\":["
                                 + "{\"key\":\"proc\",\"args\":[12,\"holyShock\"],\"trigger\":\"onHit\"},"
                                 + "{\"key\":\"proc\",\"args\":[25,\"frozenOrb\"],\"trigger\":\"onHit\"}]}]";
        var oldSnapshot = await CreateSnapshotAsync("old", "[]", oldUnique, translations);
        var newSnapshot = await CreateSnapshotAsync("new", "[]", newUnique, translations);

        var markdown = await SnapshotComparison.RenderItemPatchNotesAsync(
            await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot));

        Assert.Contains("Changed: 12 Holy Shock (trigger=onHit) (was 10)", markdown);
        Assert.Contains("Changed: 25 Frozen Orb (trigger=onHit) (was 20)", markdown);
        Assert.DoesNotContain("Added: 12 Holy Shock", markdown);
        Assert.DoesNotContain("Removed: 10 Holy Shock", markdown);

        var changedIdentityOld = "[{\"Index\":\"Proc Item\",\"Lines\":["
                                 + "{\"key\":\"proc\",\"args\":[10,\"holyShock\"],\"trigger\":\"onHit\"}]}]";
        var changedIdentityNew = "[{\"Index\":\"Proc Item\",\"Lines\":["
                                 + "{\"key\":\"proc\",\"args\":[12,\"frozenOrb\"],\"trigger\":\"onGetHit\"}]}]";
        oldSnapshot = await CreateSnapshotAsync("identity-old", "[]", changedIdentityOld, translations);
        newSnapshot = await CreateSnapshotAsync("identity-new", "[]", changedIdentityNew, translations);
        markdown = await SnapshotComparison.RenderItemPatchNotesAsync(
            await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot));

        Assert.Contains("Added: 12 Frozen Orb", markdown);
        Assert.Contains("Removed: 10 Holy Shock", markdown);
        Assert.DoesNotContain("Changed: 12 Frozen Orb", markdown);
    }

    [Fact]
    public async Task EmptySetBonusTierDoesNotRenderBlankRemoval()
    {
        const string oldSet = "[{\"NameKey\":\"demonBlade\",\"SetBonuses\":[[]]}]";
        const string newSet = "[{\"NameKey\":\"demonBlade\",\"SetBonuses\":[[{\"key\":\"bonus\",\"args\":[1]}]]}]";
        const string translations = "{\"bonus\":\"+%d Damage\"}";
        var oldSnapshot = await CreateSnapshotAsync("old", "[]", "[]", translations,
            new Dictionary<string, string> { ["keyed/weapons.json"] = oldSet });
        var newSnapshot = await CreateSnapshotAsync("new", "[]", "[]", translations,
            new Dictionary<string, string> { ["keyed/weapons.json"] = newSet });

        var markdown = await SnapshotComparison.RenderItemPatchNotesAsync(
            await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot));

        Assert.Contains("Added: +1 Damage", markdown);
        Assert.DoesNotContain("- Removed: \n", markdown.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DamageTypePropertiesAreNotRepeatedAsMetadata()
    {
        const string oldWeapon = "[{\"NameKey\":\"throwingPotion\",\"Equipment\":{\"DamageTypes\":[]}}]";
        const string newWeapon = "[{\"NameKey\":\"throwingPotion\",\"Equipment\":{\"DamageTypes\":["
                                 + "{\"Type\":2,\"AverageDamage\":5,\"Lines\":[{\"key\":\"fireDamage\",\"args\":[5]}]}]}}]";
        const string translations = "{\"fireDamage\":\"+%d Fire Damage\"}";
        var oldSnapshot = await CreateSnapshotAsync("old", "[]", "[]", translations,
            new Dictionary<string, string> { ["keyed/weapons.json"] = oldWeapon });
        var newSnapshot = await CreateSnapshotAsync("new", "[]", "[]", translations,
            new Dictionary<string, string> { ["keyed/weapons.json"] = newWeapon });

        var markdown = await SnapshotComparison.RenderItemPatchNotesAsync(
            await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot));

        Assert.Equal(1, markdown.Split("+5 Fire Damage", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("Damage Types:", markdown);
    }

    [Fact]
    public async Task AuraRangeTemplateFormatsAndUnsupportedStaticTemplateKeepsFallback()
    {
        const string oldUnique = "[{\"Index\":\"Aura Item\",\"Lines\":["
                                 + "{\"key\":\"aura\",\"args\":[10,12,\"holyShock\"]}]}]";
        const string newUnique = "[{\"Index\":\"Aura Item\",\"Lines\":["
                                 + "{\"key\":\"aura\",\"args\":[15,20,\"holyShock\"]},"
                                 + "{\"key\":\"static\",\"args\":[1]},"
                                 + "{\"key\":\"plusRange\",\"args\":[2,5,\"holyShock\"]},"
                                 + "{\"key\":\"equalRange\",\"args\":[4,4,\"holyShock\"]}]}]";
        const string translations = "{\"aura\":\"Level %d %s Aura\",\"holyShock\":\"Holy Shock\","
                                    + "\"static\":\"Static description\",\"plusRange\":\"%+d %s\","
                                    + "\"equalRange\":\"%i %s\"}";
        var oldSnapshot = await CreateSnapshotAsync("old", "[]", oldUnique, translations);
        var newSnapshot = await CreateSnapshotAsync("new", "[]", newUnique, translations);

        var markdown = await SnapshotComparison.RenderItemPatchNotesAsync(
            await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot));

        Assert.Contains("Changed: Level (15-20) Holy Shock Aura (was Level (10-12))", markdown);
        Assert.Contains("Added: static \\[1\\] (translation template unsupported; review)", markdown);
        Assert.Contains("Added: +(2-5) Holy Shock", markdown);
        Assert.Contains("Added: 4 Holy Shock", markdown);
    }

    [Fact]
    public async Task SharedSetBonusesAndOnlyChangedSetPiecesAreShown()
    {
        const string translations = "{\"setName\":\"Dawn Set\",\"pieceA\":\"Dawn Helm\",\"pieceB\":\"Dawn Shield\","
                                    + "\"bonus\":\"%d shared bonus\",\"full\":\"%d full bonus\",\"damage\":\"Damage %d\"}";
        const string oldSet =
            "[{\"Index\":\"setName\",\"NameKey\":\"setName\",\"PartialBonuses\":[{\"key\":\"bonus\",\"args\":[3]}],"
            + "\"FullBonuses\":[{\"key\":\"full\",\"args\":[10]}],"
            + "\"SetItems\":[{\"Index\":\"pieceA\",\"NameKey\":\"pieceA\",\"Lines\":[{\"key\":\"damage\",\"args\":[1]}]},"
            + "{\"Index\":\"pieceB\",\"NameKey\":\"pieceB\",\"Lines\":[{\"key\":\"damage\",\"args\":[2]}]}]}]";
        const string newSet =
            "[{\"Index\":\"setName\",\"NameKey\":\"setName\",\"PartialBonuses\":[{\"key\":\"bonus\",\"args\":[5]}],"
            + "\"FullBonuses\":[{\"key\":\"full\",\"args\":[10]}],"
            + "\"SetItems\":[{\"Index\":\"pieceA\",\"NameKey\":\"pieceA\",\"Lines\":[{\"key\":\"damage\",\"args\":[4]}]},"
            + "{\"Index\":\"pieceB\",\"NameKey\":\"pieceB\",\"Lines\":[{\"key\":\"damage\",\"args\":[2]}]}]}]";
        var oldSnapshot = await CreateSnapshotAsync("old", "[]", "[]", translations,
            new Dictionary<string, string> { ["keyed/sets.json"] = oldSet });
        var newSnapshot = await CreateSnapshotAsync("new", "[]", "[]", translations,
            new Dictionary<string, string> { ["keyed/sets.json"] = newSet });

        var markdown = await SnapshotComparison.RenderItemPatchNotesAsync(
            await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot));

        Assert.Contains("Changed: 5 shared bonus (was 3)", markdown);
        Assert.Contains("Dawn Helm", markdown);
        Assert.Contains("Changed: Damage 4 (was 1)", markdown);
        Assert.DoesNotContain("10 full bonus", markdown);
        Assert.DoesNotContain("fullSet=true", markdown);
        Assert.DoesNotContain("Dawn Shield", markdown);
    }

    [Fact]
    public async Task RunewordVariantsAndRuneSequenceRemainDistinct()
    {
        const string translations = "{\"El\":\"El\",\"Tir\":\"Tir\"}";
        const string oldRuneword =
            "[{\"Index\":\"Leaf\",\"RequiredLevel\":10,\"Runes\":[{\"NameKey\":\"El\"},{\"NameKey\":\"Tir\"}],"
            + "\"Types\":[{\"Index\":\"sword\"}]},{\"Index\":\"Leaf\",\"RequiredLevel\":10,"
            + "\"Runes\":[{\"NameKey\":\"El\"},{\"NameKey\":\"Tir\"}],\"Types\":[{\"Index\":\"bow\"}]}]";
        const string newRuneword =
            "[{\"Index\":\"Leaf\",\"RequiredLevel\":12,\"Runes\":[{\"NameKey\":\"Tir\"},{\"NameKey\":\"El\"}],"
            + "\"Types\":[{\"Index\":\"sword\"}]},{\"Index\":\"Leaf\",\"RequiredLevel\":10,"
            + "\"Runes\":[{\"NameKey\":\"El\"},{\"NameKey\":\"Tir\"}],\"Types\":[{\"Index\":\"bow\"}]}]";
        var oldSnapshot = await CreateSnapshotAsync("old", "[]", "[]", translations,
            new Dictionary<string, string> { ["keyed/runewords.json"] = oldRuneword });
        var newSnapshot = await CreateSnapshotAsync("new", "[]", "[]", translations,
            new Dictionary<string, string> { ["keyed/runewords.json"] = newRuneword });

        var markdown = await SnapshotComparison.RenderItemPatchNotesAsync(
            await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot));

        Assert.Contains("Equipment variants", markdown);
        Assert.Contains("sword", markdown);
        Assert.Contains("Rune sequence: Tir → El (was El → Tir)", markdown);
        Assert.DoesNotContain("Rune sequence: El → Tir", markdown);
    }

    [Fact]
    public async Task BaseEquipmentStatsSocketsAndAutomagicAreDescribedAccurately()
    {
        const string translations = "{\"armor\":\"Armor Base\",\"damage\":\"Damage %d\",\"group\":\"Possible group\"}";
        const string oldArmor = "[{\"NameKey\":\"armor\",\"RequiredLevel\":5,\"GemSockets\":2,\"Durability\":20,"
                                + "\"Equipment\":{\"DamageTypes\":[{\"Type\":1,\"AverageDamage\":10,\"Lines\":[{\"key\":\"damage\",\"args\":[10]}]}]},"
                                + "\"AutoMagicGroups\":[{\"Code\":\"group\",\"NameKey\":\"group\",\"Lines\":[{\"key\":\"damage\",\"args\":[1]}]}]}]";
        const string newArmor = "[{\"NameKey\":\"armor\",\"RequiredLevel\":6,\"GemSockets\":3,\"Durability\":25,"
                                + "\"Equipment\":{\"DamageTypes\":[{\"Type\":1,\"AverageDamage\":12,\"Lines\":[{\"key\":\"damage\",\"args\":[12]}]}]},"
                                + "\"AutoMagicGroups\":[{\"Code\":\"group\",\"NameKey\":\"group\",\"Lines\":[{\"key\":\"damage\",\"args\":[3]}]}]}]";
        var oldSnapshot = await CreateSnapshotAsync("old", "[]", "[]", translations,
            new Dictionary<string, string> { ["keyed/armors.json"] = oldArmor });
        var newSnapshot = await CreateSnapshotAsync("new", "[]", "[]", translations,
            new Dictionary<string, string> { ["keyed/armors.json"] = newArmor });

        var markdown = await SnapshotComparison.RenderItemPatchNotesAsync(
            await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot));

        Assert.Contains("Sockets: 3 (was 2)", markdown);
        Assert.Contains("Durability: 25 (was 20)", markdown);
        Assert.Contains("Average Damage: 12 (was 10)", markdown);
        Assert.Contains("Possible automagic group changes", markdown);
        Assert.Contains("do not imply a guaranteed affix roll", markdown);
        Assert.Contains("Changed: Damage 3 (was 1)", markdown);
    }

    [Fact]
    public async Task AffixVariantsKeepAmbiguityDiagnosticsAndTranslationFallback()
    {
        const string oldAffixes =
            "[{\"NameKey\":\"Frosted\",\"Level\":8,\"Group\":1,\"Types\":[\"armo\"],\"Lines\":[{\"key\":\"fire\",\"args\":[1]}]},"
            + "{\"NameKey\":\"Frosted\",\"Level\":8,\"Group\":1,\"Types\":[\"armo\"],\"Lines\":[{\"key\":\"cold\",\"args\":[2]}]},"
            + "{\"NameKey\":\"Sapphire\",\"Level\":9,\"Group\":2,\"Types\":[\"armo\"],\"Lines\":[{\"key\":\"missing\",\"args\":[7]}]},"
            + "{\"NameKey\":\"Sapphire\",\"Level\":9,\"Group\":2,\"Types\":[\"armo\"],\"Lines\":[{\"key\":\"missing\",\"args\":[8]}]}]";
        const string newAffixes =
            "[{\"NameKey\":\"Frosted\",\"Level\":8,\"Group\":1,\"Types\":[\"armo\"],\"Lines\":[{\"key\":\"fire\",\"args\":[1]}]},"
            + "{\"NameKey\":\"Frosted\",\"Level\":8,\"Group\":1,\"Types\":[\"armo\"],\"Lines\":[{\"key\":\"lightning\",\"args\":[2]}]},"
            + "{\"NameKey\":\"Sapphire\",\"Level\":9,\"Group\":2,\"Types\":[\"armo\"],\"Lines\":[{\"key\":\"missing\",\"args\":[7]}]},"
            + "{\"NameKey\":\"Sapphire\",\"Level\":9,\"Group\":2,\"Types\":[\"armo\"],\"Lines\":[{\"key\":\"missing\",\"args\":[8]}]}]";
        const string translations = "{\"Frosted\":\"Frosted\",\"Sapphire\":\"Sapphire\",\"fire\":\"Fire %d\","
                                    + "\"cold\":\"Cold %d\",\"lightning\":\"Lightning %d\"}";
        var oldSnapshot = await CreateSnapshotAsync("old", "[]", "[]", translations,
            new Dictionary<string, string> { ["keyed/magicprefix.json"] = oldAffixes });
        var newSnapshot = await CreateSnapshotAsync("new", "[]", "[]", translations,
            new Dictionary<string, string> { ["keyed/magicprefix.json"] = newAffixes });

        var markdown = await SnapshotComparison.RenderItemPatchNotesAsync(
            await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot));

        Assert.Contains("ambiguous-affix-identity", markdown);
        Assert.Contains("ambiguous candidate", markdown);
        Assert.Contains("missing \\[7\\]", markdown);
        Assert.Contains("translation unavailable", markdown);
    }

    [Fact]
    public async Task ModifiedAffixVariantsRetainReadableIdentityMetadata()
    {
        const string translations = "{\"name\":\"Frosted\",\"damage\":\"+%d Damage\"}";
        const string oldAffix = "[{\"NameKey\":\"name\",\"Level\":8,\"Group\":1,\"Types\":[\"armo\"],"
                                + "\"Lines\":[{\"key\":\"damage\",\"args\":[1]}]}]";
        const string newAffix = "[{\"NameKey\":\"name\",\"Level\":8,\"Group\":1,\"Types\":[\"armo\"],"
                                + "\"Lines\":[{\"key\":\"damage\",\"args\":[2]}]}]";
        var oldSnapshot = await CreateSnapshotAsync("old", "[]", "[]", translations,
            new Dictionary<string, string> { ["keyed/magicprefix.json"] = oldAffix });
        var newSnapshot = await CreateSnapshotAsync("new", "[]", "[]", translations,
            new Dictionary<string, string> { ["keyed/magicprefix.json"] = newAffix });

        var markdown = await SnapshotComparison.RenderItemPatchNotesAsync(
            await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot));

        Assert.Contains("Affix variant identity: Level: 8; Group: 1; Eligible variants: `armo`", markdown);
        Assert.DoesNotContain("PropertySignature", markdown);
    }

    [Fact]
    public async Task RenderingIsDeterministicAndUnavailableCategoriesAreNotZeroChanges()
    {
        var oldSnapshot = await CreateSnapshotAsync("old", "[]", "[]", "{}",
            new Dictionary<string, string> { ["keyed/magicprefix.json"] = "{}" });
        var newSnapshot = await CreateSnapshotAsync("new", "[]", "[]", "{}");
        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);

        var first = await SnapshotComparison.RenderItemPatchNotesAsync(report);
        var second = await SnapshotComparison.RenderItemPatchNotesAsync(report);

        Assert.Equal(first, second);
        Assert.Contains("Magic prefixes | unavailable", first);
        Assert.Contains("Comparison unavailable: NotCompared", first);
        Assert.DoesNotContain("Magic prefixes | 0 | 0 | 0", first);
    }

    private async Task<string> CreateSnapshotAsync(
        string name,
        string cubeRecipes,
        string uniques,
        string translations,
        IReadOnlyDictionary<string, string>? categoryContents = null)
    {
        var snapshot = Directory.CreateDirectory(Path.Combine(_root, name)).FullName;
        var files = new List<object>();
        foreach (var relativePath in CategoryFiles)
        {
            var content = categoryContents is not null &&
                          categoryContents.TryGetValue(relativePath, out var overrideContent)
                ? overrideContent
                : relativePath switch
                {
                    "keyed/cube-recipes.json" => cubeRecipes,
                    "keyed/uniques.json" => uniques,
                    _ => "[]"
                };
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

        const string stringsPath = "strings/enUS.json";
        var stringsFilePath = Path.Combine(snapshot, "strings", "enUS.json");
        Directory.CreateDirectory(Path.GetDirectoryName(stringsFilePath)!);
        var stringsBytes = Encoding.UTF8.GetBytes(translations);
        await File.WriteAllBytesAsync(stringsFilePath, stringsBytes);
        files.Add(new
        {
            Path = stringsPath,
            Bytes = stringsBytes.LongLength,
            Sha256 = Sha256(stringsBytes),
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
                    Outputs = new[] { stringsPath },
                    NotWritten = Array.Empty<string>()
                }
            },
            Files = files
        };
        await File.WriteAllTextAsync(Path.Combine(snapshot, "manifest.json"), JsonSerializer.Serialize(manifest));
        return snapshot;
    }

    private static string NormalizePaths(string markdown, string oldSnapshot, string newSnapshot) =>
        markdown.Replace(oldSnapshot, "<old-snapshot>", StringComparison.Ordinal)
            .Replace(newSnapshot, "<new-snapshot>", StringComparison.Ordinal);

    private static string NormalizeJsonPaths(string json, string oldSnapshot, string newSnapshot) =>
        json.Replace(JsonSerializer.Serialize(oldSnapshot)[1..^1],
                JsonSerializer.Serialize("<old-snapshot>")[1..^1], StringComparison.Ordinal)
            .Replace(JsonSerializer.Serialize(newSnapshot)[1..^1],
                JsonSerializer.Serialize("<new-snapshot>")[1..^1], StringComparison.Ordinal);

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
