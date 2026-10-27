// SPDX-License-Identifier: GPL-3.0-or-later

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using D2RMultiExport.Lib.Comparison;

namespace D2RMultiExport.Tests;

/// <summary>Focused regressions for player-facing recipe patch notes.</summary>
public sealed class RecipePatchNotesMarkdownTests : IDisposable
{
    private static readonly string[] CategoryFiles =
    [
        "keyed/cube-recipes.json", "keyed/uniques.json", "keyed/sets.json", "keyed/runewords.json",
        "keyed/armors.json", "keyed/weapons.json", "keyed/magicprefix.json", "keyed/magicsuffix.json"
    ];

    private readonly string _root = Directory.CreateTempSubdirectory("d2r-recipe-patch-notes-").FullName;

    [Fact]
    public async Task MatchedChangesDescribeIngredientsRestrictionsAndOutputs()
    {
        var oldSnapshot = await CreateSnapshotAsync("old", MatchedRecipe("Amazon", 5, "magic", "out", 1, 100, 10, 1),
            Translations);
        var newSnapshot = await CreateSnapshotAsync("new", MatchedRecipe("Sorceress", 3, "rare", "out2", 2, 50, 15, 2),
            Translations);
        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);

        var markdown = await SnapshotComparison.RenderRecipePatchNotesAsync(report);

        Assert.Contains("<summary>Item Crafting Recipe — 1 modified, 0 added, 0 removed</summary>", markdown);
        Assert.Contains("<summary>Confirmed matched modifications (1)</summary>", markdown);
        Assert.DoesNotContain("<summary>Added recipe definitions", markdown);
        Assert.DoesNotContain("<summary>Removed recipe definitions", markdown);
        Assert.Contains("#### ", markdown);
        Assert.Equal(2, markdown.Split("<details>", StringSplitOptions.None).Length - 1);
        Assert.Contains("<summary>Confirmed matched modifications (1)</summary>",
            GetDetailsSection(markdown, "Item Crafting Recipe"));
        Assert.Contains("#### Sorceress-only recipe: 3 × Rare Amulet + El Rune → 2 × Socketed Armor", markdown);
        Assert.Contains("Ingredient quantity: 3 × Amulet (was 5 × Amulet)", markdown);
        Assert.Contains("Amulet now requires: Rare Item", markdown);
        Assert.Contains("Amulet no longer requires: Magic Item", markdown);
        Assert.Contains("Required class restriction changed: Sorceress (was Amazon)", markdown);
        Assert.Contains("Result: Socketed Armor (was Rare Jewel)", markdown);
        Assert.Contains("Result quantity: 2 × Socketed Armor (was Rare Jewel)", markdown);
        Assert.Contains("Result chance: 50 (was 100)", markdown);
        Assert.Contains("Changed: +15% Enhanced Damage (was +10%)", markdown);
        Assert.Contains("Recipe condition changed; see detailed comparison.", markdown);
        Assert.DoesNotContain("Output property (A)", markdown);
        Assert.DoesNotContain("Source:", markdown);
        Assert.DoesNotContain("row 1", markdown);
        Assert.DoesNotContain("Op=", markdown);
        Assert.DoesNotContain("Param=", markdown);
        Assert.DoesNotContain("Value=", markdown);
        Assert.Contains("Sorceress-only recipe", markdown);
    }

    [Fact]
    public async Task UnmatchedRecipesShowReadableDefinitionsAndDoNotGuessPairs()
    {
        var oldSnapshot = await CreateSnapshotAsync("old", RecipeWithId("removed-id", "Old recipe", "r01", 1),
            Translations);
        var newSnapshot = await CreateSnapshotAsync("new", RecipeWithId("added-id", "New recipe", "gem", 2),
            Translations);
        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);

        var markdown = await SnapshotComparison.RenderRecipePatchNotesAsync(report);

        Assert.Contains("## Recipe identity and unmatched definitions", markdown);
        Assert.Contains("not automatically paired", markdown);
        Assert.Contains("<summary>Added recipe definitions (1)</summary>", markdown);
        Assert.Contains("<summary>Removed recipe definitions (1)</summary>", markdown);
        Assert.DoesNotContain("<summary>Confirmed matched modifications", markdown);
        Assert.Contains("#### New recipe", markdown);
        Assert.Contains("#### Old recipe", markdown);
        Assert.Contains("- Ingredients:", markdown);
        Assert.Contains("2 × Perfect Ruby", markdown);
        Assert.Contains("El Rune", markdown);
        Assert.DoesNotContain("1 × El Rune", markdown);
        Assert.Contains("- Results:", markdown);
        Assert.Contains("Rare Jewel", markdown);
        Assert.Contains("<!-- changes.json: cube-recipes / added-id -->", markdown);
        Assert.DoesNotContain("added-id", markdown.Replace("<!-- changes.json: cube-recipes / added-id -->", "",
            StringComparison.Ordinal));
        Assert.DoesNotContain("row 1", markdown);
        Assert.DoesNotContain("#### Recipe `added-id`", markdown);
        Assert.DoesNotContain("```json", markdown);
        Assert.DoesNotContain("Complete original record JSON", markdown);
        AssertBalancedDetails(markdown);

        var outputDirectory = Directory.CreateDirectory(Path.Combine(_root, "comparison")).FullName;
        await SnapshotComparison.WriteRecipePatchNotesAsync(markdown, outputDirectory);
        Assert.Equal(markdown, await File.ReadAllTextAsync(Path.Combine(outputDirectory, "recipe-patch-notes.md")));
    }

    [Fact]
    public async Task NoteCategoriesUseOnePrimaryNoteAndPreserveAllAffectedRecipes()
    {
        var oldRecipes = CombineRecipes(
            RecipeWithNotes("removed-id", "Removed recipe", "r01", 1, "enchantment", "secondary"),
            RecipeWithNotes("modified-id", "Modified recipe", "amu", 1, "enchantment"));
        var newRecipes = CombineRecipes(
            RecipeWithNotes("modified-id", "Modified recipe", "amu", 1, "crafting", "secondary"),
            RecipeWithNotes("added-id", "Added recipe", "gem", 1, "crafting", "secondary"),
            RecipeWithNotes("unnoted-id", "Unnoted recipe", "r02", 1),
            RecipeWithNotes("untranslated-id", "Untranslated note recipe", "r02", 1, "missingCategory"));
        var oldSnapshot = await CreateSnapshotAsync("old", oldRecipes, Translations);
        var newSnapshot = await CreateSnapshotAsync("new", newRecipes, Translations);
        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);

        var first = await SnapshotComparison.RenderRecipePatchNotesAsync(report);
        var second = await SnapshotComparison.RenderRecipePatchNotesAsync(report);

        Assert.Equal(first, second);
        Assert.Contains("| Item Crafting Recipe | 1 | 1 | 0 |", first);
        Assert.Contains("| Item Enchantment Recipe | 0 | 0 | 1 |", first);
        Assert.Contains("| Other recipes | 0 | 2 | 0 |", first);
        Assert.Contains("<summary>Item Crafting Recipe — 1 modified, 1 added, 0 removed</summary>", first);
        Assert.Contains("<summary>Item Enchantment Recipe — 0 modified, 0 added, 1 removed</summary>", first);
        Assert.Contains("<summary>Other recipes — 0 modified, 2 added, 0 removed</summary>", first);
        var craftingSection = GetDetailsSection(first, "Item Crafting Recipe");
        Assert.Contains("<summary>Confirmed matched modifications (1)</summary>", craftingSection);
        Assert.Contains("<summary>Added recipe definitions (1)</summary>", craftingSection);
        Assert.Contains("- Additional notes: Secondary label", first);
        Assert.Contains("- Assigned notes: Translation unavailable; see detailed comparison.", first);
        Assert.DoesNotContain("Item Crafting Recipe / Secondary label</summary>", first);
        Assert.Equal(1,
            first.Split("<!-- changes.json: cube-recipes / modified-id -->", StringSplitOptions.None).Length - 1);
        Assert.Equal(1,
            first.Split("<!-- changes.json: cube-recipes / added-id -->", StringSplitOptions.None).Length - 1);
        Assert.Equal(1,
            first.Split("<!-- changes.json: cube-recipes / removed-id -->", StringSplitOptions.None).Length - 1);
        Assert.Equal(1,
            first.Split("<!-- changes.json: cube-recipes / unnoted-id -->", StringSplitOptions.None).Length - 1);
        Assert.Equal(1,
            first.Split("<!-- changes.json: cube-recipes / untranslated-id -->", StringSplitOptions.None).Length - 1);
        AssertBalancedDetails(first);
    }

    [Fact]
    public async Task MissingTranslationsAndOperationChangesUseConciseNeutralFallbacks()
    {
        const string oldRecipe = "[{\"Id\":\"matched\",\"Index\":1,\"NumInputs\":1,\"ResolvedInputsCount\":1," +
                                 "\"Inputs\":[{\"Name\":{\"key\":\"missingName\",\"args\":[5]},\"Quantity\":1," +
                                 "\"Qualifiers\":[]}],\"Outputs\":{\"A\":{\"Name\":{\"key\":\"missingOutput\",\"args\":[]}," +
                                 "\"Quantity\":1,\"Qualifiers\":[],\"Lines\":[]}},\"Op\":1,\"Param\":7,\"Value\":2}]";
        const string newRecipe = "[{\"Id\":\"matched\",\"Index\":1,\"NumInputs\":1,\"ResolvedInputsCount\":1," +
                                 "\"Inputs\":[{\"Name\":{\"key\":\"missingName\",\"args\":[5]},\"Quantity\":1," +
                                 "\"Qualifiers\":[]}],\"Outputs\":{\"A\":{\"Name\":{\"key\":\"missingOutput\",\"args\":[]}," +
                                 "\"Quantity\":1,\"Qualifiers\":[],\"Lines\":[]}},\"Op\":2,\"Param\":8,\"Value\":2}]";
        var oldSnapshot = await CreateSnapshotAsync("old", oldRecipe, "{}");
        var newSnapshot = await CreateSnapshotAsync("new", newRecipe, "{}");
        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);

        var markdown = await SnapshotComparison.RenderRecipePatchNotesAsync(report);

        Assert.Contains("Untranslated item", markdown);
        Assert.Contains("Recipe condition changed; see detailed comparison.", markdown);
        Assert.Equal(1, markdown.Split("Recipe condition changed; see detailed comparison.",
            StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("missingName", markdown);
        Assert.DoesNotContain("missingOutput", markdown);
        Assert.DoesNotContain("Op=", markdown);
        Assert.DoesNotContain("Param=", markdown);
        Assert.DoesNotContain("Value=", markdown);
        Assert.DoesNotContain("Review items:", markdown);
    }

    [Fact]
    public async Task GroupedIngredientQuantitiesAreNotRepeatedAsRequirementsOrCountErrors()
    {
        const string cubeRecipes = "[{\"Id\":\"socket-punch\",\"Index\":15882,\"NumInputs\":5," +
                                   "\"ResolvedInputsCount\":2,\"Inputs\":[{\"Name\":{\"key\":\"weapon\",\"args\":[]}," +
                                   "\"Quantity\":1,\"Qualifiers\":[]},{\"Name\":{\"key\":\"jewel\",\"args\":[]}," +
                                   "\"Quantity\":4,\"Qualifiers\":[{\"key\":\"strCubeQualifierQuantityN\",\"args\":[4]}]}]," +
                                   "\"Outputs\":{\"A\":{\"Name\":{\"key\":\"strCubeOutputUseItemFromInput1\",\"args\":[]}," +
                                   "\"Quantity\":1,\"Qualifiers\":[{\"key\":\"strCubeQualifierItemWithSocketsN\"," +
                                   "\"args\":[4]}],\"Lines\":[]}},\"Notes\":[{\"key\":\"category\",\"args\":[]}]}]";
        var oldSnapshot = await CreateSnapshotAsync("old", "[]", Translations);
        var newSnapshot = await CreateSnapshotAsync("new", cubeRecipes, Translations);
        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);

        var markdown = await SnapshotComparison.RenderRecipePatchNotesAsync(report);

        Assert.True(markdown.Contains("Any Weapon + 4 × Jewel → Original Weapon (4 sockets)", StringComparison.Ordinal),
            markdown);
        Assert.DoesNotContain("Quantity (4)", markdown);
        Assert.DoesNotContain("1 ×", markdown);
    }

    [Fact]
    public async Task OutputModesAndMultipleResultsRemainDistinctWithoutTechnicalLabels()
    {
        var recipes = CombineRecipes(
            RecipeWithOutputMode("exact-return", "strCubeOutputUseItemFromInput1"),
            RecipeWithOutputMode("same-type", "strCubeOutputUseTypeOfInput1"),
            "[{\"Id\":\"multiple-results\",\"Index\":3,\"NumInputs\":1," +
            "\"ResolvedInputsCount\":1,\"Inputs\":[{\"Name\":{\"key\":\"amu\",\"args\":[]}," +
            "\"Quantity\":1,\"Qualifiers\":[{\"key\":\"rare\",\"args\":[]}]}]," +
            "\"Outputs\":{\"A\":{\"Name\":{\"key\":\"out\",\"args\":[]},\"Quantity\":1," +
            "\"Qualifiers\":[],\"Lines\":[{\"key\":\"enhanced\",\"args\":[15]," +
            "\"selection\":\"stat: item_singleskill; min: 6; max: 35\"}]}," +
            "\"B\":{\"Name\":{\"key\":\"out2\",\"args\":[]},\"Quantity\":2," +
            "\"Qualifiers\":[],\"Lines\":[{\"key\":\"missingStat\",\"args\":[6]," +
            "\"selection\":\"stat: item_singleskill; min: 6; max: 35\"}]}}," +
            "\"Notes\":[{\"key\":\"category\",\"args\":[]}]}]");
        var oldSnapshot = await CreateSnapshotAsync("old", "[]", Translations);
        var newSnapshot = await CreateSnapshotAsync("new", recipes, Translations);
        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);

        var markdown = await SnapshotComparison.RenderRecipePatchNotesAsync(report);

        Assert.Contains("Any Weapon → Original Weapon", markdown);
        Assert.Contains("Any Weapon → New Weapon (same base type)", markdown);
        Assert.Contains("#### Rare Amulet → Rare Jewel / 2 × Socketed Armor", markdown);
        Assert.Contains("Rare Jewel", markdown);
        Assert.Contains("2 × Socketed Armor", markdown);
        Assert.Contains("+15% Enhanced Damage", markdown);
        Assert.Contains("Some result properties cannot be translated safely; see detailed comparison.", markdown);
        Assert.DoesNotContain("missingStat", markdown);
        Assert.DoesNotContain("selection", markdown);
        Assert.DoesNotContain("stat: item_singleskill", markdown);
        Assert.DoesNotContain("- A:", markdown);
        Assert.DoesNotContain("- B:", markdown);
        Assert.DoesNotContain("1 ×", markdown);
        Assert.Equal(3, markdown.Split("<!-- changes.json: cube-recipes /", StringSplitOptions.None).Length - 1);
        AssertBalancedDetails(markdown);
    }

    [Fact]
    public async Task IdenticalHeadingsWithTechnicalConditionsStayUnpairedAndCaveated()
    {
        var recipes = CombineRecipes(
            RecipeWithCondition("condition-one", 1),
            RecipeWithCondition("condition-two", 2));
        var oldSnapshot = await CreateSnapshotAsync("old", "[]", Translations);
        var newSnapshot = await CreateSnapshotAsync("new", recipes, Translations);
        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);

        var markdown = await SnapshotComparison.RenderRecipePatchNotesAsync(report);

        Assert.Equal(2, markdown.Split("#### Amulet → Rare Jewel", StringSplitOptions.None).Length - 1);
        Assert.Equal(2, markdown.Split("Recipe condition distinguishes this definition; see detailed comparison.",
            StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("Op=", markdown);
        Assert.DoesNotContain("Param=", markdown);
        Assert.DoesNotContain("Value=", markdown);
    }

    [Fact]
    public async Task RejuvenationPotionUsesVerifiedMiscNameWhenEnglishStringIsMalformedTemplate()
    {
        var oldSnapshot = await CreateSnapshotAsync("old", "[]", Translations);
        var newSnapshot = await CreateSnapshotAsync("new", RecipeWithId("rejuvenation", "", "rvs", 1), Translations);
        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);

        var markdown = await SnapshotComparison.RenderRecipePatchNotesAsync(report);

        Assert.Contains("Rejuvenation Potion", markdown);
        Assert.DoesNotContain("Untranslated item", markdown);
    }

    [Fact]
    public async Task VerifiedFixedFlagPropertiesOmitWarningButOtherNumericValuesRetainIt()
    {
        const string verifiedProperties =
            "[{\"key\":\"ModStr1y\",\"args\":[1]},{\"key\":\"ModStr3y\",\"args\":[1]}," +
            "{\"key\":\"ModStr4b\",\"args\":[1]},{\"key\":\"ModStr5z\",\"args\":[1]}," +
            "{\"key\":\"ModStre9s\",\"args\":[1]},{\"key\":\"strethereal\",\"args\":[1]}," +
            "{\"key\":\"ModStr6d\",\"args\":[10]}]";
        const string presentation =
            "{\"Stats\":{\"indestructible\":{\"Function\":19,\"PositiveKey\":\"ModStr1y\",\"NegativeKey\":\"ModStr1y\"}," +
            "\"cannotbefrozen\":{\"Function\":19,\"PositiveKey\":\"ModStr3y\",\"NegativeKey\":\"ModStr3y\"}," +
            "\"knockback\":{\"Function\":19,\"PositiveKey\":\"ModStr4b\",\"NegativeKey\":\"ModStr4b\"}," +
            "\"ignoretargetac\":{\"Function\":19,\"PositiveKey\":\"ModStr5z\",\"NegativeKey\":\"ModStr5z\"}," +
            "\"half_freeze\":{\"Function\":19,\"PositiveKey\":\"ModStre9s\",\"NegativeKey\":\"ModStre9s\"}," +
            "\"item_stupidity\":{\"Function\":12,\"ValueMode\":2,\"ValueShift\":0,\"PositiveKey\":\"ModStr6d\"," +
            "\"NegativeKey\":\"ModStr6d\"}}}";
        var oldSnapshot = await CreateSnapshotAsync("old", "[]", Translations,
            itemStatPresentation: presentation);
        var newSnapshot = await CreateSnapshotAsync("new", RecipeWithProperties("flags", verifiedProperties),
            Translations,
            itemStatPresentation: presentation);
        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);

        var markdown = await SnapshotComparison.RenderRecipePatchNotesAsync(report);

        Assert.Contains("Indestructible", markdown);
        Assert.Contains("Cannot Be Frozen", markdown);
        Assert.Contains("Knockback", markdown);
        Assert.Contains("Ignore Target's Defense", markdown);
        Assert.Contains("Half Freeze Duration", markdown);
        Assert.Contains("Ethereal (Cannot be Repaired)", markdown);
        Assert.Contains("Hit Blinds Target +10", markdown);
        Assert.DoesNotContain("(value unverified)", markdown);

        var unverifiedPresentation = presentation.Replace(",\"ValueMode\":2,\"ValueShift\":0", "",
            StringComparison.Ordinal);
        var fallbackOldSnapshot = await CreateSnapshotAsync("fallback-old", "[]", Translations,
            itemStatPresentation: unverifiedPresentation);
        var fallbackNewSnapshot = await CreateSnapshotAsync("fallback-new", RecipeWithProperties("fallback-flags",
                "[{\"key\":\"ModStr6d\",\"args\":[10]},{\"key\":\"ModStr1y\",\"args\":[2]}]"), Translations,
            itemStatPresentation: unverifiedPresentation);
        var fallbackReport = await SnapshotComparison.CompareAsync(fallbackOldSnapshot, fallbackNewSnapshot);
        var fallbackMarkdown = await SnapshotComparison.RenderRecipePatchNotesAsync(fallbackReport);

        Assert.Contains("Hit Blinds Target (value unverified)", fallbackMarkdown);
        Assert.Contains("Indestructible (value unverified)", fallbackMarkdown);
    }

    [Fact]
    public async Task ForcedAffixSelectorStaysGenericWhenSnapshotDoesNotPreserveSourceRowIds()
    {
        var oldSnapshot = await CreateSnapshotAsync("old", "[]", Translations);
        var recipes = CombineRecipes(RecipeWithForcePrefix(365), RecipeWithForceSuffix(101));
        var newSnapshot = await CreateSnapshotAsync("new", recipes, Translations);
        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);

        var markdown = await SnapshotComparison.RenderRecipePatchNotesAsync(report);

        Assert.Contains("Magic Ring (Forced prefix)", markdown);
        Assert.Contains("Magic Ring (Forced suffix)", markdown);
        Assert.Contains(
            "Some guaranteed affixes cannot currently be identified by name.",
            markdown);
        Assert.DoesNotContain("guaranteed Coral prefix", markdown);
        Assert.DoesNotContain("guaranteed of the Leech suffix", markdown);
    }

    [Fact]
    public async Task RuneIndexSuffixIsRemovedWithoutChangingQuantityOrSocketCount()
    {
        var oldSnapshot = await CreateSnapshotAsync("old", "[]", Translations);
        var newSnapshot = await CreateSnapshotAsync("new", RecipeWithRuneAndSocket(), Translations);
        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);

        var markdown = await SnapshotComparison.RenderRecipePatchNotesAsync(report);

        Assert.Contains("2 × Ohm Rune", markdown);
        Assert.Contains("Dem Rune (#38)", markdown);
        Assert.Contains("3 sockets", markdown);
        Assert.DoesNotContain("Ohm Rune (#27)", markdown);
    }

    [Fact]
    public async Task HiddenQualifierArgumentChangesUseNeutralConditionWording()
    {
        var oldSnapshot = await CreateSnapshotAsync("old", RecipeWithForcePrefix(373), Translations);
        var newSnapshot = await CreateSnapshotAsync("new", RecipeWithForcePrefix(366), Translations);
        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);

        var markdown = await SnapshotComparison.RenderRecipePatchNotesAsync(report);

        Assert.Contains("Forced prefix", markdown);
        Assert.Contains("Recipe condition changed; see detailed comparison.", markdown);
        Assert.DoesNotContain("373", markdown);
        Assert.DoesNotContain("366", markdown);
        Assert.DoesNotContain("now requires: Forced prefix", markdown);
        Assert.DoesNotContain("no longer requires: Forced prefix", markdown);
    }

    [Fact]
    public async Task UnavailableCategoriesAreNotReportedAsZeroAndOutputIsDeterministic()
    {
        var oldSnapshot = await CreateSnapshotAsync("old", "[]", Translations, includeCube: false);
        var newSnapshot = await CreateSnapshotAsync("new", "[]", Translations);
        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);

        var first = await SnapshotComparison.RenderRecipePatchNotesAsync(report);
        var second = await SnapshotComparison.RenderRecipePatchNotesAsync(report);

        Assert.Equal(first, second);
        Assert.Contains("Recipe changes unavailable", first);
        Assert.Contains("Recipe change counts are unavailable", first);
        Assert.DoesNotContain("Total matched modifications: 0", first);
        Assert.DoesNotContain("| Other recipes | 0 | 0 | 0 |", first);
        AssertBalancedDetails(first);
    }

    [Fact]
    public async Task EnchantmentRecipesGroupByTranslatedFirstIngredientType()
    {
        var oldSnapshot = await CreateSnapshotAsync("old", CombineRecipes(
            EnchantmentRecipe("modified-weapon", "weapon", 1),
            EnchantmentRecipe("removed-missile", "missile")), Translations);
        var newSnapshot = await CreateSnapshotAsync("new", CombineRecipes(
            EnchantmentRecipe("modified-weapon", "weapon", 2),
            EnchantmentRecipe("added-weapon", "weapon"),
            EnchantmentRecipe("added-onehand", "onehand"),
            EnchantmentRecipe("added-slam", "slam"),
            EnchantmentRecipe("added-missileweapons", "missileweapons"),
            EnchantmentRecipe("added-spearpolearm", "spearpolearm"),
            EnchantmentRecipe("added-missile", "missile"),
            RecipeWithId("ordinary-recipe", "Ordinary recipe", "r01", 1)), Translations);
        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);

        var first = await SnapshotComparison.RenderRecipePatchNotesAsync(report);
        var second = await SnapshotComparison.RenderRecipePatchNotesAsync(report);
        var enchantmentSection = GetDetailsSection(first, "Item Enchantment Recipe");

        Assert.Equal(first, second);
        Assert.Equal(
            [
                "<summary>1H Weapons (1)</summary>",
                "<summary>Any Weapon (2)</summary>",
                "<summary>Missile (2)</summary>",
                "<summary>Missile Weapons (1)</summary>",
                "<summary>Slam Weapons (1)</summary>",
                "<summary>Spears and Polearms (1)</summary>"
            ],
            enchantmentSection.Split('\n').Select(static line => line.TrimEnd('\r'))
                .Where(static line => line.StartsWith("<summary>", StringComparison.Ordinal))
                .Where(static line => !line.Contains("matched modifications", StringComparison.Ordinal) &&
                                      !line.Contains("recipe definitions", StringComparison.Ordinal))
                .Skip(1));
        Assert.Contains("<summary>Confirmed matched modifications (1)</summary>", enchantmentSection);
        Assert.Contains("<summary>Added recipe definitions (1)</summary>", enchantmentSection);
        Assert.Contains("<summary>Removed recipe definitions (1)</summary>", enchantmentSection);
        var anyWeaponSection = GetDetailsSection(enchantmentSection, "Any Weapon (2)");
        Assert.Contains("<summary>Confirmed matched modifications (1)</summary>", anyWeaponSection);
        Assert.Contains("<summary>Added recipe definitions (1)</summary>", anyWeaponSection);
        Assert.Equal(3, anyWeaponSection.Split("<details>", StringSplitOptions.None).Length - 1);
        var missileSection = GetDetailsSection(enchantmentSection, "Missile (2)");
        Assert.Contains("<summary>Added recipe definitions (1)</summary>", missileSection);
        Assert.Contains("<summary>Removed recipe definitions (1)</summary>", missileSection);
        Assert.Equal(3, missileSection.Split("<details>", StringSplitOptions.None).Length - 1);
        Assert.Equal(8,
            enchantmentSection.Split("<!-- changes.json: cube-recipes / ", StringSplitOptions.None).Length - 1);
        Assert.Equal(15, enchantmentSection.Split("<details>", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("### 1H Weapons (1)", enchantmentSection);
        Assert.DoesNotContain("### Added recipe definitions (1)", first);
        Assert.Contains("#### Ordinary recipe", first);
        Assert.Equal(17, first.Split("<details>", StringSplitOptions.None).Length - 1);
        AssertBalancedDetails(first);
    }

    private async Task<string> CreateSnapshotAsync(
        string name,
        string cubeRecipes,
        string translations,
        bool includeCube = true,
        string? itemStatPresentation = null)
    {
        var snapshot = Directory.CreateDirectory(Path.Combine(_root, name)).FullName;
        var includedCategoryFiles = CategoryFiles.Where(path => includeCube || path != "keyed/cube-recipes.json")
            .ToArray();
        var files = new List<object>();
        foreach (var relativePath in includedCategoryFiles)
        {
            var content = relativePath == "keyed/cube-recipes.json" ? cubeRecipes : "[]";
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

        if (itemStatPresentation is not null)
        {
            const string presentationPath = "keyed/item-stat-presentation.json";
            var presentationFilePath =
                Path.Combine(snapshot, presentationPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(presentationFilePath)!);
            var presentationBytes = Encoding.UTF8.GetBytes(itemStatPresentation);
            await File.WriteAllBytesAsync(presentationFilePath, presentationBytes);
            files.Add(new
            {
                Path = presentationPath,
                Bytes = presentationBytes.LongLength,
                Sha256 = Sha256(presentationBytes),
                Producer = "item-stat-presentation.json"
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
            Steps = itemStatPresentation is null
                ? new object[]
                {
                    new
                    {
                        Producer = "keyed-core",
                        Status = "Written",
                        Outputs = includedCategoryFiles,
                        NotWritten = Array.Empty<string>()
                    },
                    new
                    {
                        Producer = "strings",
                        Status = "Written",
                        Outputs = new[] { stringsPath },
                        NotWritten = Array.Empty<string>()
                    }
                }
                : new object[]
                {
                    new
                    {
                        Producer = "keyed-core",
                        Status = "Written",
                        Outputs = includedCategoryFiles,
                        NotWritten = Array.Empty<string>()
                    },
                    new
                    {
                        Producer = "item-stat-presentation.json",
                        Status = "Written",
                        Outputs = new[] { "keyed/item-stat-presentation.json" },
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

    private static string MatchedRecipe(
        string requiredClass,
        int ingredientQuantity,
        string ingredientQualifier,
        string output,
        int outputQuantity,
        int outputChance,
        int enhancedDamage,
        int operation) =>
        "[{\"Id\":\"matched\",\"Index\":1,\"RequiredClass\":\"" + requiredClass + "\",\"NumInputs\":" +
        (ingredientQuantity + 1) + "," +
        "\"ResolvedInputsCount\":2,\"Inputs\":[{\"Name\":{\"key\":\"amu\",\"args\":[]},\"Quantity\":" +
        ingredientQuantity + ",\"Qualifiers\":[{\"key\":\"" + ingredientQualifier +
        "\",\"args\":[]}],\"RawToken\":\"amu\"}," +
        "{\"Name\":{\"key\":\"r01\",\"args\":[]},\"Quantity\":1,\"Qualifiers\":[],\"RawToken\":\"r01\"}]," +
        "\"Outputs\":{\"A\":{\"Name\":{\"key\":\"" + output + "\",\"args\":[]},\"Quantity\":" +
        outputQuantity + ",\"Qualifiers\":[],\"OutputChance\":" + outputChance + ",\"Lines\":[{\"key\":\"enhanced\"," +
        "\"args\":[" + enhancedDamage + "]}]}},\"Notes\":[{\"key\":\"category\",\"args\":[]}],\"Op\":" +
        operation + ",\"Param\":7,\"Value\":1}]";

    private static string RecipeWithId(string id, string description, string ingredient, int quantity) =>
        "[{\"Id\":\"" + id + "\",\"Index\":1,\"Description\":\"" + description + "\",\"NumInputs\":" +
        quantity + "," +
        "\"ResolvedInputsCount\":1,\"Inputs\":[{\"Name\":{\"key\":\"" + ingredient + "\",\"args\":[]}," +
        "\"Quantity\":" + quantity +
        ",\"Qualifiers\":[]}],\"Outputs\":{\"A\":{\"Name\":{\"key\":\"out\",\"args\":[]}," +
        "\"Quantity\":1,\"Qualifiers\":[],\"OutputChance\":100,\"Lines\":[]}},\"Notes\":[{\"key\":\"category\",\"args\":[]}]}]";

    private static string RecipeWithNotes(string id, string description, string ingredient, int quantity,
        params string[] noteKeys)
    {
        var notes = string.Join(",", noteKeys.Select(key => "{\"key\":\"" + key + "\",\"args\":[]}"));
        return "[{\"Id\":\"" + id + "\",\"Index\":1,\"Description\":\"" + description +
               "\",\"NumInputs\":" + quantity +
               ",\"ResolvedInputsCount\":1,\"Inputs\":[{\"Name\":{\"key\":\"" + ingredient +
               "\",\"args\":[]},\"Quantity\":" + quantity + ",\"Qualifiers\":[]}]," +
               "\"Outputs\":{\"A\":{\"Name\":{\"key\":\"out\",\"args\":[]},\"Quantity\":1," +
               "\"Qualifiers\":[],\"OutputChance\":100,\"Lines\":[]}},\"Notes\":[" + notes + "]}]";
    }

    private static string EnchantmentRecipe(string id, string itemType, int outputQuantity = 1) =>
        "[{\"Id\":\"" + id + "\",\"Index\":1,\"NumInputs\":1,\"ResolvedInputsCount\":1," +
        "\"Inputs\":[{\"Name\":{\"key\":\"" + itemType + "\",\"args\":[]},\"Quantity\":1," +
        "\"Qualifiers\":[]}],\"Outputs\":{\"A\":{\"Name\":{\"key\":\"out\",\"args\":[]}," +
        "\"Quantity\":" + outputQuantity + ",\"Qualifiers\":[],\"OutputChance\":100,\"Lines\":[]}}," +
        "\"Notes\":[{\"key\":\"strCubeNoteItemEnchantment\",\"args\":[]}]}]";

    private static string RecipeWithOutputMode(string id, string outputKey) =>
        "[{\"Id\":\"" + id + "\",\"Index\":1,\"NumInputs\":1,\"ResolvedInputsCount\":1," +
        "\"Inputs\":[{\"Name\":{\"key\":\"weapon\",\"args\":[]},\"Quantity\":1,\"Qualifiers\":[]}]," +
        "\"Outputs\":{\"A\":{\"Name\":{\"key\":\"" + outputKey + "\",\"args\":[]}," +
        "\"Quantity\":1,\"Qualifiers\":[],\"Lines\":[]}},\"Notes\":[{\"key\":\"category\",\"args\":[]}]}]";

    private static string RecipeWithCondition(string id, int operation) =>
        "[{\"Id\":\"" + id + "\",\"Index\":1,\"NumInputs\":1,\"ResolvedInputsCount\":1," +
        "\"Inputs\":[{\"Name\":{\"key\":\"amu\",\"args\":[]},\"Quantity\":1,\"Qualifiers\":[]}]," +
        "\"Outputs\":{\"A\":{\"Name\":{\"key\":\"out\",\"args\":[]},\"Quantity\":1," +
        "\"Qualifiers\":[],\"Lines\":[]}},\"Notes\":[{\"key\":\"category\",\"args\":[]}]," +
        "\"Op\":" + operation + ",\"Param\":7,\"Value\":1}]";

    private static string RecipeWithForcePrefix(int group) =>
        "[{\"Id\":\"force-prefix\",\"Index\":1,\"NumInputs\":1,\"ResolvedInputsCount\":1," +
        "\"Inputs\":[{\"Name\":{\"key\":\"ring\",\"args\":[]},\"Quantity\":1,\"Qualifiers\":[]}]," +
        "\"Outputs\":{\"A\":{\"Name\":{\"key\":\"ring\",\"args\":[]},\"Quantity\":1," +
        "\"Qualifiers\":[{\"key\":\"strCubeQualifierForcePrefixN\",\"args\":[" + group + "]}]," +
        "\"Lines\":[]}},\"Notes\":[{\"key\":\"category\",\"args\":[]}]}]";

    private static string RecipeWithForceSuffix(int group) =>
        "[{\"Id\":\"force-suffix\",\"Index\":2,\"NumInputs\":1,\"ResolvedInputsCount\":1," +
        "\"Inputs\":[{\"Name\":{\"key\":\"ring\",\"args\":[]},\"Quantity\":1,\"Qualifiers\":[]}]," +
        "\"Outputs\":{\"A\":{\"Name\":{\"key\":\"ring\",\"args\":[]},\"Quantity\":1," +
        "\"Qualifiers\":[{\"key\":\"strCubeQualifierForceSuffixN\",\"args\":[" + group + "]}]," +
        "\"Lines\":[]}},\"Notes\":[{\"key\":\"category\",\"args\":[]}]}]";

    private static string RecipeWithProperties(string id, string properties) =>
        "[{\"Id\":\"" + id + "\",\"Index\":1,\"NumInputs\":1,\"ResolvedInputsCount\":1," +
        "\"Inputs\":[{\"Name\":{\"key\":\"amu\",\"args\":[]},\"Quantity\":1,\"Qualifiers\":[]}]," +
        "\"Outputs\":{\"A\":{\"Name\":{\"key\":\"out\",\"args\":[]},\"Quantity\":1," +
        "\"Qualifiers\":[],\"Lines\":" + properties + "}},\"Notes\":[{\"key\":\"category\",\"args\":[]}]}]";

    private static string RecipeWithRuneAndSocket() =>
        "[{\"Id\":\"rune-socket\",\"Index\":1,\"NumInputs\":2,\"ResolvedInputsCount\":2," +
        "\"Inputs\":[{\"Name\":{\"key\":\"r27\",\"args\":[]},\"Quantity\":2,\"Qualifiers\":[]}," +
        "{\"Name\":{\"key\":\"demRune\",\"args\":[]},\"Quantity\":1,\"Qualifiers\":[]}]," +
        "\"Outputs\":{\"A\":{\"Name\":{\"key\":\"out\",\"args\":[]},\"Quantity\":1," +
        "\"Qualifiers\":[{\"key\":\"strCubeQualifierItemWithSocketsN\",\"args\":[3]}],\"Lines\":[]}}," +
        "\"Notes\":[{\"key\":\"category\",\"args\":[]}]}]";

    private static string CombineRecipes(params string[] recipes) =>
        "[" + string.Join(",", recipes.Select(static recipe => recipe[1..^1])) + "]";

    private static string GetDetailsSection(string markdown, string summaryPrefix)
    {
        var summaryStart = markdown.IndexOf("<summary>" + summaryPrefix, StringComparison.Ordinal);
        Assert.True(summaryStart >= 0);
        var detailsStart = markdown.LastIndexOf("<details>", summaryStart, StringComparison.Ordinal);
        Assert.True(detailsStart >= 0);

        var depth = 0;
        var sectionEnd = detailsStart;
        foreach (var line in markdown[detailsStart..].Split('\n'))
        {
            var trimmedLine = line.TrimEnd('\r');
            if (trimmedLine == "<details>")
                depth++;
            else if (trimmedLine == "</details>" && --depth == 0)
            {
                sectionEnd += line.Length;
                return markdown[detailsStart..sectionEnd];
            }

            sectionEnd += line.Length + 1;
        }

        Assert.Fail("Details section was not closed.");
        return "";
    }

    private static void AssertBalancedDetails(string markdown)
    {
        var details = new Stack<bool>();
        var lines = markdown.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].TrimEnd('\r');
            if (line == "<details>")
            {
                details.Push(false);
                continue;
            }

            if (line.StartsWith("<summary>", StringComparison.Ordinal) &&
                line.EndsWith("</summary>", StringComparison.Ordinal))
            {
                Assert.NotEmpty(details);
                Assert.False(details.Pop());
                details.Push(true);
                Assert.Equal("", lines[index + 1].TrimEnd('\r'));
                continue;
            }

            if (line != "</details>") continue;
            Assert.NotEmpty(details);
            Assert.True(details.Pop());
            Assert.Equal("", lines[index + 1].TrimEnd('\r'));
        }

        Assert.Empty(details);
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private const string Translations =
        "{\"category\":\"Item Crafting Recipe\",\"enchantment\":\"Item Enchantment Recipe\"," +
        "\"crafting\":\"Item Crafting Recipe\",\"secondary\":\"Secondary label\",\"amu\":\"Amulet\"," +
        "\"r01\":\"El Rune\",\"r02\":\"Eld Rune\",\"gem\":\"Perfect Ruby\"," +
        "\"out\":\"Rare Jewel\",\"out2\":\"Socketed Armor\",\"magic\":\"Magic Item\"," +
        "\"rare\":\"Rare Item\",\"enhanced\":\"%+d%% Enhanced Damage\"," +
        "\"weapon\":\"Any Weapon\",\"onehand\":\"1H Weapons\",\"slam\":\"Slam Weapons\"," +
        "\"missileweapons\":\"Missile Weapons\",\"spearpolearm\":\"Spears and Polearms\"," +
        "\"missile\":\"Missile\",\"jewel\":\"Jewel\",\"useitem\":\"Return Exact Input Item\"," +
        "\"strCubeNoteItemEnchantment\":\"Item Enchantment Recipe\"," +
        "\"rvs\":\"35%\",\"r27\":\"Ohm Rune (#27)\",\"demRune\":\"Dem Rune (#38)\",\"ring\":\"Magic Ring\"," +
        "\"ModStr1y\":\"Indestructible\",\"ModStr3y\":\"Cannot Be Frozen\"," +
        "\"ModStr4b\":\"Knockback\",\"ModStr5z\":\"Ignore Target's Defense\"," +
        "\"ModStre9s\":\"Half Freeze Duration\",\"ModStr6d\":\"Hit Blinds Target\"," +
        "\"strethereal\":\"Ethereal (Cannot be Repaired)\"," +
        "\"strCubeQualifierItemWithSocketsN\":\"Socketed (%d)\"," +
        "\"strCubeQualifierQuantityN\":\"Quantity (%d)\"}";

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
