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
    public async Task MissingAndEmptyRecipeDetailsDoNotProduceFalseModifications()
    {
        const string oldRecipes = "[{\"Id\":\"same\",\"Index\":1,\"Notes\":[]}]";
        const string newRecipes = "[{\"Id\":\"same\",\"Index\":2,\"Notes\":[],\"Details\":[]}]";
        var oldSnapshot = await CreateSnapshotAsync("details-old", oldRecipes, Translations);
        var newSnapshot = await CreateSnapshotAsync("details-new", newRecipes, Translations);
        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);
        var cube = Assert.Single(report.Categories, static category => category.Category == "cube-recipes");

        Assert.Equal(0, cube.Counts.Modified);
        Assert.Equal(1, cube.Counts.Unchanged);
        Assert.Equal(1, cube.Counts.Reindexed);
        Assert.Contains("*No recipe changes.*", await SnapshotComparison.RenderRecipePatchNotesAsync(report));
    }

    [Fact]
    public async Task PopulatedRecipeDetailsStillCountAsChanges()
    {
        const string oldRecipes = "[{\"Id\":\"same\",\"Index\":1,\"Details\":[]}]";
        const string newRecipes = "[{\"Id\":\"same\",\"Index\":1," +
                                  "\"Details\":[{\"key\":\"meaningful\",\"args\":[2]}]}]";
        var oldSnapshot = await CreateSnapshotAsync("detail-content-old", oldRecipes, Translations);
        var newSnapshot = await CreateSnapshotAsync("detail-content-new", newRecipes, Translations);
        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);
        var cube = Assert.Single(report.Categories, static category => category.Category == "cube-recipes");

        Assert.Equal(1, cube.Counts.Modified);
        Assert.Contains("Details", Assert.Single(cube.ModifiedRecords).ChangedFields);
    }

    [Fact]
    public async Task UniqueJewelMigrationIgnoresOldSchemaMissingDetails()
    {
        var oldSnapshot = await CreateSnapshotAsync("jewel-details-old",
            JewelRecipe("old-only", "\\\"jew,uni\\\"", "jew", 1, "socketPunch"), JewelTranslations);
        var newSnapshot = await CreateSnapshotAsync("jewel-details-new",
            JewelRecipe("new-only", "\\\"jewl,uni\\\"", "jewlitype", 1, "socketPunch")
                .Replace("\\"Enabled\\":true", "\\"Details\\":[],\\"Enabled\\":true", StringComparison.Ordinal),
            JewelTranslations);
        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);
        var markdown = await SnapshotComparison.RenderRecipePatchNotesAsync(report);

        Assert.Equal(1, Assert.Single(report.Categories, static category => category.Category == "cube-recipes")
            .Counts.Added);
        Assert.Contains("*No recipe changes.*", markdown);
    }

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
        Assert.Contains("<summary>Modified recipes (1)</summary>", markdown);
        Assert.DoesNotContain("<summary>Added recipes", markdown);
        Assert.DoesNotContain("<summary>Removed recipes", markdown);
        Assert.Contains("#### ", markdown);
        Assert.Equal(2, markdown.Split("<details>", StringSplitOptions.None).Length - 1);
        Assert.Contains("<summary>Modified recipes (1)</summary>",
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
        Assert.DoesNotContain("Recipe condition changed", markdown);
        Assert.DoesNotContain("see detailed comparison", markdown);
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

        Assert.DoesNotContain("## Recipe identity and unmatched definitions", markdown);
        Assert.DoesNotContain("not automatically paired", markdown);
        Assert.Contains("| Item Crafting Recipe | 0 | 1 | 1 |", markdown);
        Assert.Contains("<summary>Added recipes (1)</summary>", markdown);
        Assert.Contains("<summary>Removed recipes (1)</summary>", markdown);
        Assert.DoesNotContain("<summary>Modified recipes", markdown);
        var addedSection = GetDetailsSection(markdown, "Added recipes (1)");
        var removedSection = GetDetailsSection(markdown, "Removed recipes (1)");
        Assert.Equal(1, CountLines(addedSection, "#### New recipe"));
        Assert.DoesNotContain("#### Old recipe", addedSection);
        Assert.Equal(1, CountLines(removedSection, "#### Old recipe"));
        Assert.DoesNotContain("#### New recipe", removedSection);
        Assert.Contains("- Ingredients:", markdown);
        Assert.Contains("2 × Perfect Ruby", markdown);
        Assert.Contains("El Rune", markdown);
        Assert.DoesNotContain("1 × El Rune", markdown);
        Assert.Contains("- Results:", markdown);
        Assert.Contains("Rare Jewel", markdown);
        Assert.DoesNotContain("<!-- changes.json", markdown);
        Assert.DoesNotContain("added-id", markdown);
        Assert.DoesNotContain("removed-id", markdown);
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
        var enchantmentSection = GetDetailsSection(first, "Item Enchantment Recipe");
        var otherSection = GetDetailsSection(first, "Other recipes");
        Assert.Contains("<summary>Modified recipes (1)</summary>", craftingSection);
        Assert.Contains("<summary>Added recipes (1)</summary>", craftingSection);
        Assert.Contains("- Additional notes: Secondary label", first);
        Assert.Contains("- Crafting category or notes: Item Crafting Recipe / Secondary label " +
                        "(was Item Enchantment Recipe).", craftingSection);
        Assert.DoesNotContain("Translation unavailable", first);
        Assert.DoesNotContain("see detailed comparison", first);
        Assert.DoesNotContain("missingCategory", first);
        Assert.DoesNotContain("- Assigned notes:", first);
        Assert.DoesNotContain("Item Crafting Recipe / Secondary label</summary>", first);
        Assert.DoesNotContain("<!-- changes.json", first);
        Assert.Equal(1, CountLines(first, "#### Modified recipe"));
        Assert.Equal(1, CountLines(craftingSection, "#### Modified recipe"));
        Assert.Equal(1, CountLines(first, "#### Added recipe"));
        Assert.Equal(1, CountLines(craftingSection, "#### Added recipe"));
        Assert.Equal(1, CountLines(first, "#### Removed recipe"));
        Assert.Equal(1, CountLines(enchantmentSection, "#### Removed recipe"));
        Assert.Equal(1, CountLines(first, "#### Unnoted recipe"));
        Assert.Equal(1, CountLines(otherSection, "#### Unnoted recipe"));
        Assert.Equal(1, CountLines(first, "#### Untranslated note recipe"));
        Assert.Equal(1, CountLines(otherSection, "#### Untranslated note recipe"));
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

        Assert.Contains("| Other recipes | 1 | 0 | 0 |", markdown);
        Assert.Equal(1, CountLines(markdown, "#### Unknown item → Unknown item"));
        Assert.DoesNotContain("Untranslated item", markdown);
        Assert.DoesNotContain("Recipe condition changed", markdown);
        Assert.DoesNotContain("see detailed comparison", markdown);
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
        Assert.DoesNotContain("cannot be translated safely", markdown);
        Assert.DoesNotContain("see detailed comparison", markdown);
        Assert.DoesNotContain("missingStat", markdown);
        Assert.DoesNotContain("selection", markdown);
        Assert.DoesNotContain("stat: item_singleskill", markdown);
        Assert.DoesNotContain("- A:", markdown);
        Assert.DoesNotContain("- B:", markdown);
        Assert.DoesNotContain("1 ×", markdown);
        Assert.Equal(1, CountLines(markdown, "#### Any Weapon → Original Weapon"));
        Assert.Equal(1, CountLines(markdown, "#### Any Weapon → New Weapon (same base type)"));
        Assert.Equal(1, CountLines(markdown, "#### Rare Amulet → Rare Jewel / 2 × Socketed Armor"));
        Assert.Equal(3, markdown.Split('\n').Count(static line => line.StartsWith("#### ", StringComparison.Ordinal)));
        Assert.DoesNotContain("<!-- changes.json", markdown);
        AssertBalancedDetails(markdown);
    }

    [Fact]
    public async Task IdenticalHeadingsWithTechnicalConditionsStayUnpairedWithoutConditionDiagnostics()
    {
        var recipes = CombineRecipes(
            RecipeWithCondition("condition-one", 1),
            RecipeWithCondition("condition-two", 2));
        var oldSnapshot = await CreateSnapshotAsync("old", "[]", Translations);
        var newSnapshot = await CreateSnapshotAsync("new", recipes, Translations);
        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);

        var markdown = await SnapshotComparison.RenderRecipePatchNotesAsync(report);

        Assert.Contains("| Item Crafting Recipe | 0 | 2 | 0 |", markdown);
        Assert.Contains("<summary>Added recipes (2)</summary>", markdown);
        Assert.DoesNotContain("<summary>Modified recipes", markdown);
        Assert.Equal(2, CountLines(markdown, "#### Amulet → Rare Jewel"));
        Assert.DoesNotContain("Recipe condition distinguishes this definition", markdown);
        Assert.DoesNotContain("see detailed comparison", markdown);
        Assert.DoesNotContain("condition-one", markdown);
        Assert.DoesNotContain("condition-two", markdown);
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
    public async Task VerifiedFixedFlagPropertiesRenderWhileUnverifiedValuesAreOmitted()
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

        Assert.Equal(1, CountLines(fallbackMarkdown, "#### Amulet → Rare Jewel"));
        Assert.DoesNotContain("Hit Blinds Target", fallbackMarkdown);
        Assert.DoesNotContain("Indestructible", fallbackMarkdown);
        Assert.DoesNotContain("(value unverified)", fallbackMarkdown);
        Assert.DoesNotContain("ModStr6d", fallbackMarkdown);
        Assert.DoesNotContain("ModStr1y", fallbackMarkdown);
        Assert.DoesNotContain("see detailed comparison", fallbackMarkdown);
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
        Assert.DoesNotContain("365", markdown);
        Assert.DoesNotContain("101", markdown);
        Assert.DoesNotContain("guaranteed Coral prefix", markdown);
        Assert.DoesNotContain("guaranteed of the Leech suffix", markdown);
    }

    [Fact]
    public async Task ParenthesizedClassTranslationIsNotWrappedTwiceInRecipeNotes()
    {
        const string translations = "{\"damage\":\"+%d Damage\",\"amazon\":\"(Amazon Only)\"}";
        const string properties = "[{\"key\":\"damage\",\"args\":[5],\"classOnly\":\"amazon\"}]";
        var oldSnapshot = await CreateSnapshotAsync("old", "[]", Translations);
        var newSnapshot = await CreateSnapshotAsync("new", RecipeWithProperties("class-only", properties),
            translations);
        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);

        var markdown = await SnapshotComparison.RenderRecipePatchNotesAsync(report);

        Assert.Contains("+5 Damage (Amazon Only)", markdown);
        Assert.DoesNotContain("+5 Damage ((Amazon Only))", markdown);
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
        var oldSnapshot = await CreateSnapshotAsync("old", RecipeWithForcePrefix(387), Translations);
        var newSnapshot = await CreateSnapshotAsync("new", RecipeWithForcePrefix(381), Translations);
        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);

        var markdown = await SnapshotComparison.RenderRecipePatchNotesAsync(report);

        Assert.Contains("Forced prefix", markdown);
        Assert.Contains("Result forced magic prefix changed.", markdown);
        Assert.DoesNotContain("387", markdown);
        Assert.DoesNotContain("381", markdown);
        Assert.DoesNotContain("Outputs.A.Qualifiers", markdown);
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
        Assert.Contains("*Recipe changes are not available.*", first);
        Assert.DoesNotContain("## Summary", first);
        Assert.DoesNotContain("| *No affected categories* | 0 | 0 | 0 |", first);
        Assert.DoesNotContain("*No recipe changes.*", first);
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
                .Where(static line => !line.StartsWith("<summary>Modified recipes", StringComparison.Ordinal) &&
                                      !line.StartsWith("<summary>Added recipes", StringComparison.Ordinal) &&
                                      !line.StartsWith("<summary>Removed recipes", StringComparison.Ordinal))
                .Skip(1));
        Assert.Contains("<summary>Modified recipes (1)</summary>", enchantmentSection);
        Assert.Contains("<summary>Added recipes (1)</summary>", enchantmentSection);
        Assert.Contains("<summary>Removed recipes (1)</summary>", enchantmentSection);
        var anyWeaponSection = GetDetailsSection(enchantmentSection, "Any Weapon (2)");
        Assert.Contains("<summary>Modified recipes (1)</summary>", anyWeaponSection);
        Assert.Contains("<summary>Added recipes (1)</summary>", anyWeaponSection);
        Assert.Equal(1, CountLines(anyWeaponSection, "##### Any Weapon → 2 × Rare Jewel"));
        Assert.Equal(1, CountLines(anyWeaponSection, "##### Any Weapon → Rare Jewel"));
        Assert.Equal(3, anyWeaponSection.Split("<details>", StringSplitOptions.None).Length - 1);
        var missileSection = GetDetailsSection(enchantmentSection, "Missile (2)");
        Assert.Contains("<summary>Added recipes (1)</summary>", missileSection);
        Assert.Contains("<summary>Removed recipes (1)</summary>", missileSection);
        Assert.Equal(2, CountLines(missileSection, "##### Missile → Rare Jewel"));
        Assert.Equal(3, missileSection.Split("<details>", StringSplitOptions.None).Length - 1);
        Assert.Equal(8,
            enchantmentSection.Split('\n').Count(static line => line.StartsWith("##### ", StringComparison.Ordinal)));
        Assert.DoesNotContain("<!-- changes.json", first);
        Assert.Equal(15, enchantmentSection.Split("<details>", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("### 1H Weapons (1)", enchantmentSection);
        Assert.DoesNotContain("### Added recipes (1)", first);
        Assert.Contains("#### Ordinary recipe", first);
        Assert.Equal(17, first.Split("<details>", StringSplitOptions.None).Length - 1);
        AssertBalancedDetails(first);
    }

    [Fact]
    public async Task UniqueJewelIngredientMigrationPairsAreOmittedWhileOtherRecipeChangesRemain()
    {
        var oldRecipes = CombineRecipes(
            JewelRecipe("old-punch-1", "\"jew,uni\"", "jew", 1, "socketPunch"),
            JewelRecipe("old-punch-4", "\"jew,uni,qty=4\"", "jew", 4, "socketPunch"),
            JewelRecipe("old-craft", "\"jew,uni\"", "jew", 1, "category", "r01"),
            JewelRecipe("old-output-change", "\"jew,uni\"", "jew", 1, "category", "r02"),
            JewelRecipe("old-quantity-change", "\"jew,uni,qty=2\"", "jew", 2, "category"),
            JewelRecipe("old-magic", "\"jew,mag\"", "jew", 1, "category", qualifier: "magic"),
            JewelRecipe("old-other-input", "\"jew,uni\"", "jew", 1, "category", "gem"),
            RecipeWithNotes("removed-other", "Removed other", "r01", 1, "category"),
            RecipeWithNotes("modified-id", "Modified recipe", "amu", 1, "category"));
        var newRecipes = CombineRecipes(
            JewelRecipe("new-punch-1", "\"jewl,uni\"", "jewlitype", 1, "socketPunch"),
            JewelRecipe("new-punch-4", "\"jewl,uni,qty=4\"", "jewlitype", 4, "socketPunch"),
            JewelRecipe("new-craft", "\"jewl,uni\"", "jewlitype", 1, "category", "r01"),
            JewelRecipe("new-output-change", "\"jewl,uni\"", "jewlitype", 1, "category", "r02",
                outputQuantity: 2),
            JewelRecipe("new-quantity-change", "\"jewl,uni,qty=3\"", "jewlitype", 3, "category"),
            JewelRecipe("new-magic", "\"jewl,mag\"", "jewlitype", 1, "category", qualifier: "magic"),
            JewelRecipe("new-other-input", "\"jewl,uni\"", "jewlitype", 1, "category", "amu"),
            RecipeWithNotes("added-other", "Added other", "gem", 1, "category"),
            RecipeWithNotes("modified-id", "Modified recipe", "amu", 2, "category"));
        var oldSnapshot = await CreateSnapshotAsync("old", oldRecipes, JewelTranslations);
        var newSnapshot = await CreateSnapshotAsync("new", newRecipes, JewelTranslations);
        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);
        var reportJson = JsonSerializer.Serialize(report);
        var recipeChanges = await SnapshotComparison.RenderRecipeChangesAsync(report);

        var markdown = await SnapshotComparison.RenderRecipePatchNotesAsync(report);

        foreach (var id in new[] { "old-punch-1", "new-punch-1", "old-punch-4", "new-punch-4", "old-craft", "new-craft" })
        {
            Assert.DoesNotContain(id, markdown);
            Assert.Contains(id, recipeChanges);
        }

        Assert.DoesNotContain("Crafted Helm + Unique Jewel + El Rune", markdown);
        Assert.DoesNotContain("4 × Unique Jewel", markdown);

        var modifiedSection = GetDetailsSection(markdown, "Modified recipes (1)");
        Assert.Equal(1, CountLines(modifiedSection, "#### Modified recipe"));
        var addedSection = GetDetailsSection(markdown, "Added recipes (5)");
        foreach (var heading in new[]
                 {
                     "#### Added other", "#### Crafted Helm + 3 × Unique Jewel → Original Crafted Helm",
                     "#### Crafted Helm + Magic Jewel → Original Crafted Helm",
                     "#### Crafted Helm + Unique Jewel + Amulet → Original Crafted Helm",
                     "#### Crafted Helm + Unique Jewel + Eld Rune → 2 × Original Crafted Helm"
                 })
            Assert.Equal(1, CountLines(addedSection, heading));
        var removedSection = GetDetailsSection(markdown, "Removed recipes (5)");
        foreach (var heading in new[]
                 {
                     "#### Crafted Helm + 2 × Unique Jewel → Original Crafted Helm",
                     "#### Crafted Helm + Magic Jewel → Original Crafted Helm",
                     "#### Crafted Helm + Unique Jewel + Eld Rune → Original Crafted Helm",
                     "#### Crafted Helm + Unique Jewel + Perfect Ruby → Original Crafted Helm", "#### Removed other"
                 })
            Assert.Equal(1, CountLines(removedSection, heading));
        Assert.Equal(11, markdown.Split('\n').Count(static line => line.StartsWith("#### ", StringComparison.Ordinal)));

        Assert.DoesNotContain("Socket Punch", markdown);
        Assert.Contains("| Item Crafting Recipe | 1 | 5 | 5 |", markdown);
        Assert.Contains("<summary>Item Crafting Recipe — 1 modified, 5 added, 5 removed</summary>", markdown);
        Assert.DoesNotContain("<!--", markdown);
        AssertBalancedDetails(markdown);

        var cube = Assert.Single(report.Categories, static category => category.Category == "cube-recipes");
        Assert.Equal(8, cube.Counts.Added);
        Assert.Equal(8, cube.Counts.Removed);
        Assert.Equal(1, cube.Counts.Modified);
        Assert.Equal(reportJson, JsonSerializer.Serialize(report));
        Assert.Equal(recipeChanges, await SnapshotComparison.RenderRecipeChangesAsync(report));
    }

    [Fact]
    public async Task UniqueJewelIngredientMigrationIsNotPairedWhenAmbiguousAndCanLeaveNoVisibleChanges()
    {
        var ambiguousOld = await CreateSnapshotAsync("ambiguous-old", CombineRecipes(
            JewelRecipe("old-a", "\"jew,uni\"", "jew", 1, "socketPunch"),
            JewelRecipe("old-b", "\"jew,uni\"", "jew", 1, "socketPunch")), JewelTranslations);
        var ambiguousNew = await CreateSnapshotAsync("ambiguous-new",
            JewelRecipe("new-a", "\"jewl,uni\"", "jewlitype", 1, "socketPunch"), JewelTranslations);
        var ambiguous = await SnapshotComparison.RenderRecipePatchNotesAsync(
            await SnapshotComparison.CompareAsync(ambiguousOld, ambiguousNew));

        Assert.Contains("| Socket Punch | 0 | 1 | 2 |", ambiguous);
        Assert.Equal(1, CountLines(GetDetailsSection(ambiguous, "Added recipes (1)"),
            "#### Crafted Helm + Unique Jewel → Original Crafted Helm"));
        Assert.Equal(2, CountLines(GetDetailsSection(ambiguous, "Removed recipes (2)"),
            "#### Crafted Helm + Unique Jewel → Original Crafted Helm"));
        Assert.DoesNotContain("<!--", ambiguous);

        var onlyOld = await CreateSnapshotAsync("only-old",
            JewelRecipe("old-only", "\"jew,uni,qty=6\"", "jew", 6, "socketPunch"), JewelTranslations);
        var onlyNew = await CreateSnapshotAsync("only-new",
            JewelRecipe("new-only", "\"jewl,uni,qty=6\"", "jewlitype", 6, "socketPunch"), JewelTranslations);
        var onlyReport = await SnapshotComparison.CompareAsync(onlyOld, onlyNew);
        var only = await SnapshotComparison.RenderRecipePatchNotesAsync(onlyReport);

        Assert.Contains("| *No affected categories* | 0 | 0 | 0 |", only);
        Assert.Contains("*No recipe changes.*", only);
        Assert.DoesNotContain("<!--", only);
        Assert.DoesNotContain("old-only", only);
        Assert.DoesNotContain("new-only", only);
        Assert.DoesNotContain("Socket Punch", only);
        Assert.DoesNotContain("<details>", only);
        var cube = Assert.Single(onlyReport.Categories, static category => category.Category == "cube-recipes");
        Assert.Equal("new-only", Assert.Single(cube.AddedRecords).Key);
        Assert.Equal("old-only", Assert.Single(cube.RemovedRecords).Key);
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

    private static string JewelRecipe(string id, string rawToken, string nameKey, int quantity, string noteKey,
        string? secondIngredient = null, string qualifier = "strCubeQualifierUniqueItem", int outputQuantity = 1)
    {
        var quantityQualifier = quantity > 1
            ? ",{\"key\":\"strCubeQualifierQuantityN\",\"args\":[" + quantity + "]}"
            : "";
        var second = secondIngredient is null
            ? ""
            : ",{\"Name\":{\"key\":\"" + secondIngredient + "\",\"args\":[]},\"Quantity\":1," +
              "\"Qualifiers\":[],\"RawToken\":\"" + secondIngredient + "\"}";
        return "[{\"Id\":\"" + id + "\",\"Index\":" + (id.StartsWith("old", StringComparison.Ordinal) ? 10 : 20) +
               ",\"Description\":\"\",\"RequiredClass\":\"\",\"NumInputs\":" + (quantity + 1) +
               ",\"ResolvedInputsCount\":2,\"Inputs\":[{\"Name\":{\"key\":\"helmitype\",\"args\":[]}," +
               "\"Quantity\":1,\"Qualifiers\":[{\"key\":\"strCubeQualifierCraftedItem\",\"args\":[]}]," +
               "\"RawToken\":\"\\\"helm,crf\\\"\"},{\"Name\":{\"key\":\"" + nameKey + "\",\"args\":[]}," +
               "\"Quantity\":" + quantity + ",\"Qualifiers\":[{\"key\":\"" + qualifier + "\",\"args\":[]}" +
               quantityQualifier + "],\"RawToken\":" + JsonSerializer.Serialize(rawToken) + "}" + second + "]," +
               "\"Outputs\":{\"A\":{\"Name\":{\"key\":\"strCubeOutputUseItemFromInput1\",\"args\":[]}," +
               "\"Quantity\":" + outputQuantity + ",\"Qualifiers\":[],\"Lines\":[{\"key\":\"strSocketedCount\"," +
               "\"args\":[" + quantity + "]}]}},\"Notes\":[{\"key\":\"" + noteKey + "\",\"args\":[]}]," +
               "\"Enabled\":true}]";
    }

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

    private static int CountLines(string markdown, string expectedLine) =>
        markdown.Split('\n').Count(line => line.TrimEnd('\r') == expectedLine);

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

    private static readonly string JewelTranslations =
        Translations[..^1] + ",\"socketPunch\":\"Socket Punch\",\"jew\":\"Jewel\",\"jewlitype\":\"Jewel\"," +
        "\"helmitype\":\"Any Helm\",\"strCubeQualifierUniqueItem\":\"Unique Item\"," +
        "\"strCubeQualifierCraftedItem\":\"Crafted Item\",\"strSocketedCount\":\"Socketed (%d)\"}";

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
