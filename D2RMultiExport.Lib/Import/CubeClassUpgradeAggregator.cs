// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using D2RMultiExport.Lib.Config;
using D2RMultiExport.Lib.ErrorHandling;
using D2RMultiExport.Lib.Models;
using D2RMultiExport.Lib.Translation;
using K = D2RMultiExport.Lib.Translation.SyntheticStringRegistry.Keys;

namespace D2RMultiExport.Lib.Import;

internal static class CubeClassUpgradeAggregator
{
    public static void Aggregate(ImportResult<CubeRecipeExport> result, ExportConfig config)
    {
        foreach (var family in config.CubeRecipeClassUpgradeFamilies)
        {
            var recipesByTier = new Dictionary<string, List<CubeRecipeExport>>(StringComparer.OrdinalIgnoreCase);
            foreach (var recipe in result.Items)
            {
                var inputCodes = recipe.Inputs.Select(static input => MainToken(input.RawToken)).ToArray();
                // The family's primary input (the upgraded item) identifies a member; recipes
                // that only share a secondary ingredient (e.g. Orb of Shadows + a tier rune
                // on weapon recipes) are unrelated. Ingredient mismatches are validated per tier.
                if (family.InputCodes.Count == 0
                    || !inputCodes.Contains(family.InputCodes[0], StringComparer.OrdinalIgnoreCase))
                    continue;

                var tierCodes = inputCodes.Where(family.TierInputCodesSet.Contains)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (tierCodes.Length == 0)
                {
                    if (family.InputCodesSet.All(code => inputCodes.Contains(code, StringComparer.OrdinalIgnoreCase)))
                    {
                        result.AddWarning("CubeRecipe", string.Join(" + ", inputCodes),
                            "Relic upgrade inputs were found without a configured tier rune; the original recipe was preserved.");
                    }
                    continue;
                }

                if (tierCodes.Length > 1)
                {
                    result.AddWarning("CubeRecipe", string.Join(" + ", inputCodes),
                        $"Relic upgrade inputs contain multiple tier runes ({string.Join(", ", tierCodes)}); the original recipe was preserved.");
                }

                foreach (var tierCode in tierCodes)
                {
                    if (!recipesByTier.TryGetValue(tierCode, out var tierRecipes))
                    {
                        tierRecipes = [];
                        recipesByTier.Add(tierCode, tierRecipes);
                    }
                    tierRecipes.Add(recipe);
                }
            }

            var suppressed = new HashSet<CubeRecipeExport>();
            foreach (var (tierCode, recipes) in recipesByTier)
            {
                var identity = $"{family.InputCodes.FirstOrDefault() ?? "cube recipe"} + {tierCode}";
                if (!TryAggregateTier(recipes, family, tierCode, out var representative, out var reason))
                {
                    result.AddWarning("CubeRecipe", identity,
                        $"Relic upgrade family was not aggregated: {reason} Original recipes were preserved.");
                    continue;
                }

                AddSummaryContent(representative!, recipes, family);
                foreach (var recipe in recipes)
                {
                    if (!ReferenceEquals(recipe, representative))
                        suppressed.Add(recipe);
                }
            }

            if (suppressed.Count > 0)
                result.Items.RemoveAll(suppressed.Contains);
        }
    }

    private static bool TryAggregateTier(
        IReadOnlyList<CubeRecipeExport> recipes,
        ExportConfig.ClassUpgradeFamilyConfig family,
        string tierCode,
        out CubeRecipeExport? representative,
        out string reason)
    {
        representative = null;
        reason = "";

        var expectedRecipeCount = family.ClassCodesSet.Count + 2;
        if (recipes.Count != expectedRecipeCount)
            return Fail(out reason, $"expected {expectedRecipeCount} variants ({family.ClassCodesSet.Count} class successes and two refunds), found {recipes.Count}.");

        var expectedInputCount = family.InputCodesSet.Count + 1;
        var firstRawInputs = recipes[0].Inputs.Select(static input => input.RawToken).ToArray();
        foreach (var recipe in recipes)
        {
            if (recipe.Inputs.Count != expectedInputCount || recipe.NumInputs != expectedInputCount)
                return Fail(out reason, $"a variant has {recipe.Inputs.Count} parsed inputs and NumInputs={recipe.NumInputs}; expected {expectedInputCount}.");

            if (!recipe.Inputs.Select(static input => input.RawToken).SequenceEqual(firstRawInputs, StringComparer.Ordinal))
                return Fail(out reason, "variants do not have identical raw input tokens and order.");

            var actualInputCodes = recipe.Inputs.Select(static input => MainToken(input.RawToken)).ToArray();
            var expectedInputCodes = family.InputCodes.Append(tierCode).ToArray();
            if (!SameTokenMultiset(actualInputCodes, expectedInputCodes))
                return Fail(out reason, $"a variant's ingredients do not match the configured family inputs and tier rune {tierCode}.");
            if (!string.Equals(MainToken(recipe.Inputs[0].RawToken), family.InputCodes[0], StringComparison.OrdinalIgnoreCase))
                return Fail(out reason, "the Relic is not input 1, so the useitem output would not return the Relic.");
        }

        var successByClass = new Dictionary<string, CubeRecipeExport>(StringComparer.OrdinalIgnoreCase);
        var alreadyUpgraded = new List<CubeRecipeExport>();
        var insufficientKills = new List<CubeRecipeExport>();
        foreach (var recipe in recipes)
        {
            if (recipe.Op == family.AlreadyUpgradedOp)
            {
                alreadyUpgraded.Add(recipe);
                continue;
            }

            if (recipe.Op == family.InsufficientKillsOp)
            {
                if (recipe.Param != family.KillStatParam)
                    return Fail(out reason, $"a refund row uses op {family.InsufficientKillsOp} with Param={recipe.Param}, expected Param={family.KillStatParam}.");
                insufficientKills.Add(recipe);
                continue;
            }

            if (recipe.Op is not null)
                return Fail(out reason, $"unexpected recipe guard Op={recipe.Op}, Param={recipe.Param}, Value={recipe.Value}.");

            if (string.IsNullOrWhiteSpace(recipe.Class) || !family.ClassCodesSet.Contains(recipe.Class))
                return Fail(out reason, $"a success row has an unknown or missing class code '{recipe.Class}'.");
            if (!successByClass.TryAdd(recipe.Class, recipe))
                return Fail(out reason, $"class {recipe.Class} has duplicate success rows.");
        }

        if (successByClass.Count != family.ClassCodesSet.Count
            || family.ClassCodesSet.Any(classCode => !successByClass.ContainsKey(classCode)))
            return Fail(out reason, "the success rows do not contain exactly one variant for every configured class.");
        if (alreadyUpgraded.Count != 1)
            return Fail(out reason, $"expected one already-upgraded refund (Op={family.AlreadyUpgradedOp}), found {alreadyUpgraded.Count}.");
        if (insufficientKills.Count != 1)
            return Fail(out reason, $"expected one insufficient-kills refund (Op={family.InsufficientKillsOp}, Param={family.KillStatParam}), found {insufficientKills.Count}.");

        foreach (var refund in new[] { alreadyUpgraded[0], insufficientKills[0] })
        {
            if (!string.IsNullOrWhiteSpace(refund.Class))
                return Fail(out reason, "a refund row has a class restriction.");
            if (!IsUseItem(refund.Outputs.A) || refund.Outputs.A!.Properties.Count != 0)
                return Fail(out reason, "a refund row does not return the original Relic through a property-free useitem output.");
            if (refund.Outputs.A.Quantity != refund.Inputs[0].Quantity
                || refund.Outputs.A.Qualifiers.Count != 0
                || refund.Outputs.A.KeyedQualifiers.Count != 0)
            {
                return Fail(out reason, "a refund row modifies the returned Relic's quantity or qualifiers.");
            }
            if (refund.Outputs.B?.Properties.Count > 0 || refund.Outputs.C?.Properties.Count > 0)
                return Fail(out reason, "a refund row applies properties to a returned ingredient.");

            var expectedRefundIngredients = refund.Inputs.Skip(1).ToArray();
            var returnedIngredients = new[] { refund.Outputs.B, refund.Outputs.C }
                .Where(static output => output is not null)
                .Select(static output => output!)
                .ToArray();
            if (!RefundIngredientsMatch(expectedRefundIngredients, returnedIngredients))
                return Fail(out reason, "a refund row does not return each non-Relic ingredient with its original quantity.");
        }

        var killGuard = insufficientKills[0];
        if (!killGuard.Value.HasValue || killGuard.Value.Value < 0 || killGuard.Value.Value == int.MaxValue)
            return Fail(out reason, "the insufficient-kills guard has no usable numeric Value for deriving the kill requirement.");
        string? sharedSignature = null;
        string? classModifierSignature = null;
        string? outputSignature = null;
        foreach (var (classCode, recipe) in successByClass)
        {
            if (!IsUseItem(recipe.Outputs.A) || recipe.Outputs.B is not null || recipe.Outputs.C is not null)
                return Fail(out reason, $"class {classCode} success does not have a single useitem output.");
            if (!string.Equals(recipe.Class, classCode, StringComparison.OrdinalIgnoreCase))
                return Fail(out reason, $"class {classCode} success has an inconsistent Class restriction.");
            if (recipe.Outputs.A!.Quantity != recipe.Inputs[0].Quantity
                || recipe.Outputs.A.Qualifiers.Count != 0
                || recipe.Outputs.A.KeyedQualifiers.Count != 0)
            {
                return Fail(out reason, $"class {classCode} success modifies the Relic output's quantity or qualifiers.");
            }

            var currentOutputSignature = JsonSerializer.Serialize(new
            {
                recipe.Outputs.A!.Quantity,
                recipe.Outputs.A.OutputChance,
                recipe.Outputs.A.Qualifiers,
                recipe.Outputs.A.KeyedQualifiers
            });
            if (outputSignature is null)
                outputSignature = currentOutputSignature;
            else if (!string.Equals(outputSignature, currentOutputSignature, StringComparison.Ordinal))
                return Fail(out reason, "success rows have conflicting output quantity or qualifiers.");

            var properties = recipe.Outputs.A!.Properties;
            var classProperties = properties.Where(property => family.ClassCodesSet.Contains(property.PropertyCode ?? "")).ToList();
            if (classProperties.Count != 1 || !string.Equals(classProperties[0].PropertyCode, classCode, StringComparison.OrdinalIgnoreCase))
                return Fail(out reason, $"class {classCode} success must contain exactly one matching class-skill modifier.");

            var classProperty = classProperties[0];
            if (classProperty.Min != 1 || classProperty.Max != 1)
                return Fail(out reason, $"class {classCode} skill modifier is not a fixed +1 bonus.");
            var currentClassModifierSignature = PropertySignature(classProperty, includePropertyCode: false, includeLines: false);
            if (classModifierSignature is null)
                classModifierSignature = currentClassModifierSignature;
            else if (!string.Equals(classModifierSignature, currentClassModifierSignature, StringComparison.Ordinal))
                return Fail(out reason, "class-skill modifiers do not have equivalent source metadata across all classes.");

            var currentSharedProperties = properties.Where(property => !ReferenceEquals(property, classProperty)).ToList();
            if (currentSharedProperties.Count == 0)
                return Fail(out reason, $"class {classCode} success is missing the shared tier stat bonus.");
            var currentSharedSignature = string.Join("\u001e", currentSharedProperties.Select(property => PropertySignature(property, includePropertyCode: true, includeLines: true)));
            if (sharedSignature is null)
            {
                sharedSignature = currentSharedSignature;
            }
            else if (!string.Equals(sharedSignature, currentSharedSignature, StringComparison.Ordinal))
            {
                return Fail(out reason, "success rows have conflicting shared stat modifiers.");
            }
        }

        foreach (var refund in new[] { alreadyUpgraded[0], insufficientKills[0] })
        {
            if (refund.Outputs.A!.Properties.Count != 0)
                return Fail(out reason, "a refund row carries unexpected output properties.");
        }

        representative = successByClass[family.ClassCodes[0]];
        if (!representative.Outputs.A!.Properties.Any(property => family.ClassCodesSet.Contains(property.PropertyCode ?? "")))
            return Fail(out reason, "representative success is missing its class-skill modifier.");

        return true;
    }

    private static bool Fail(out string reason, string failure)
    {
        reason = failure;
        return false;
    }

    private static void AddSummaryContent(
        CubeRecipeExport representative,
        IReadOnlyList<CubeRecipeExport> recipes,
        ExportConfig.ClassUpgradeFamilyConfig family)
    {
        var killGuard = recipes.Single(recipe => recipe.Op == family.InsufficientKillsOp && recipe.Param == family.KillStatParam);
        var requiredKills = killGuard.Value!.Value + 1;
        var output = representative.Outputs.A!;
        var relic = representative.Inputs.First(input => string.Equals(MainToken(input.RawToken), family.InputCodes[0], StringComparison.OrdinalIgnoreCase));
        var classProperty = output.Properties.Single(property => family.ClassCodesSet.Contains(property.PropertyCode ?? ""));
        classProperty.Lines = [KeyedLine.Of(K.CubeOutputClassSkillLevels, classProperty.Min!.Value)];

        output.Name = relic.Name;
        output.NameLine = relic.NameLine;
        representative.Class = "";
        representative.ClassLine = null;
        representative.RequiredClass = "";
        representative.Description = K.CubeRelicUpgradeTitle;

        var notes = recipes.SelectMany(static recipe => recipe.Notes)
            .GroupBy(static note => JsonSerializer.Serialize(note), StringComparer.Ordinal)
            .Select(static group => group.First())
            .ToList();
        AddNote(notes, K.CubeNoteUberCharmUpgrade);
        representative.Notes = notes;
        representative.Details =
        [
            KeyedLine.Of(K.CubeNoteRelicKillRequirement, requiredKills),
            KeyedLine.Of(K.CubeNoteRelicUpgradeRefund),
        ];
    }

    private static void AddNote(List<KeyedLine> notes, string key)
    {
        if (notes.All(note => !string.Equals(note.Key, key, StringComparison.Ordinal)))
            notes.Add(KeyedLine.Of(key));
    }

    private static bool IsUseItem(CubeOutputExport? output) =>
        output is not null && string.Equals(MainToken(output.MainToken), "useitem", StringComparison.OrdinalIgnoreCase);

    private static string MainToken(string? rawToken) =>
        string.IsNullOrWhiteSpace(rawToken)
            ? ""
            : rawToken.Split(',')[0].Trim().Trim('"');

    private static bool SameTokenMultiset(IReadOnlyCollection<string> first, IReadOnlyCollection<string> second)
    {
        if (first.Count != second.Count)
            return false;

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in first)
            counts[token] = counts.GetValueOrDefault(token) + 1;
        foreach (var token in second)
        {
            if (!counts.TryGetValue(token, out var count))
                return false;
            if (count == 1)
                counts.Remove(token);
            else
                counts[token] = count - 1;
        }
        return counts.Count == 0;
    }

    private static bool RefundIngredientsMatch(
        IReadOnlyList<CubeIngredientExport> ingredients,
        IReadOnlyList<CubeOutputExport> outputs)
    {
        if (ingredients.Count != outputs.Count)
            return false;

        foreach (var ingredient in ingredients)
        {
            var matchingOutputs = outputs.Where(output => string.Equals(
                MainToken(output.MainToken), MainToken(ingredient.RawToken), StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matchingOutputs.Length != 1)
                return false;

            var output = matchingOutputs[0];
            if (output.Quantity != ingredient.Quantity
                || !output.Qualifiers.SequenceEqual(ingredient.Qualifiers, StringComparer.Ordinal)
                || !KeyedQualifiersMatch(output.KeyedQualifiers, ingredient.KeyedQualifiers))
            {
                return false;
            }
        }
        return true;
    }

    private static bool KeyedQualifiersMatch(IReadOnlyList<KeyedLine> first, IReadOnlyList<KeyedLine> second) =>
        first.Select(static line => JsonSerializer.Serialize(line))
            .SequenceEqual(second.Select(static line => JsonSerializer.Serialize(line)), StringComparer.Ordinal);

    private static string PropertySignature(CubePropertyExport property, bool includePropertyCode, bool includeLines)
    {
        var groups = property.GroupProperties?.OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new
            {
                pair.Key,
                Properties = pair.Value.Select(child => PropertySignature(child, includePropertyCode: true, includeLines: true)).ToArray()
            })
            .ToArray();
        return JsonSerializer.Serialize(new
        {
            property.ModChance,
            property.Index,
            property.NumberOfItemsRequired,
            property.PickMode,
            property.Chance,
            Groups = groups,
            property.IsPickModeEntry,
            PropertyCode = includePropertyCode ? property.PropertyCode : null,
            property.Priority,
            property.Min,
            property.Max,
            property.DamageRolls,
            property.Parameter,
            property.IsEase,
            property.Suffix,
            Lines = includeLines ? property.Lines.Select(static line => JsonSerializer.Serialize(line)).ToArray() : null
        });
    }
}
