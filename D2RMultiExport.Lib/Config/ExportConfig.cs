// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using System.Text.Json.Serialization;

namespace D2RMultiExport.Lib.Config;

/// <summary>
/// Strongly-typed representation of export-config.json.
/// All formerly hardcoded bypass data lives here and is user-editable.
/// </summary>
public sealed class ExportConfig
{
    [JsonPropertyName("ignoredUniqueItems")]
    public List<string> IgnoredUniqueItems { get; set; } = [];

    [JsonPropertyName("itemTypeNameNormalizations")]
    public Dictionary<string, string> ItemTypeNameNormalizations { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("vanillaUniqueMaxRow")]
    public int VanillaUniqueMaxRow { get; set; } = 441;

    [JsonPropertyName("vanillaSetMaxRow")]
    public int VanillaSetMaxRow { get; set; } = 142;

    [JsonPropertyName("vanillaUniqueOverrides")]
    public List<string> VanillaUniqueOverrides { get; set; } = [];

    [JsonPropertyName("elementalDamageCodes")]
    public List<string> ElementalDamageCodes { get; set; } = [];

    [JsonPropertyName("elementalMinMaxPairs")]
    public Dictionary<string, string> ElementalMinMaxPairs { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("cubeRecipeCoreItemCodes")]
    public List<string> CubeRecipeCoreItemCodes { get; set; } = [];

    [JsonPropertyName("cubeRecipeAllowedCodes")]
    public List<string> CubeRecipeAllowedCodes { get; set; } = [];

    [JsonPropertyName("charmCodeToName")]
    public Dictionary<string, string> CharmCodeToName { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("blockedCubeInputCodes")]
    public List<string> BlockedCubeInputCodes { get; set; } = [];

    [JsonPropertyName("skippedCubeRecipes")]
    public List<SkippedRecipe> SkippedCubeRecipes { get; set; } = [];

    [JsonPropertyName("cubeRecipeNotes")]
    public List<RecipeNoteConfig> CubeRecipeNotes { get; set; } = [];

    /// <summary>
    /// Property codes that should be silently dropped from the export. Mirrors the
    /// formerly hardcoded <c>PropertyMapper.IgnoredProperties</c> set.
    /// </summary>
    [JsonPropertyName("ignoredPropertyCodes")]
    public List<string> IgnoredPropertyCodes { get; set; } = [];

    /// <summary>
    /// Maps a base item NameStr to a magic-quality name key for items that use a
    /// different display name when magical (e.g. <c>aqv</c> "Arrows" → <c>z01</c>
    /// "Magic Arrows"). Mirrors the formerly hardcoded
    /// <c>EquipmentHelper.MagicNameOverrides</c> table.
    /// </summary>
    [JsonPropertyName("magicNameOverrides")]
    public Dictionary<string, string> MagicNameOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// English class names that are considered valid <c>RequiredClass</c> values
    /// when resolved from a base item's <c>Equiv2</c>. Mirrors the formerly
    /// hardcoded <c>EquipmentHelper.ValidClassNames</c> set. Used only as a
    /// gate — the resolved value is the literal string written to
    /// <c>KeyedEquipment.RequiredClass</c>.
    /// </summary>
    [JsonPropertyName("validRequiredClassNames")]
    public List<string> ValidRequiredClassNames { get; set; } = [];

    /// <summary>
    /// AutoMagic group ids skipped wholesale during the keyed export. Mirrors the
    /// formerly hardcoded <c>KeyedJsonExporter.SkippedAutoMagicGroups</c> set.
    /// </summary>
    [JsonPropertyName("skippedAutoMagicGroups")]
    public List<int> SkippedAutoMagicGroups { get; set; } = [];

    // ── Property pipeline lookups (formerly hardcoded in PropertyMapper / PropertyCleanup) ──

    [JsonPropertyName("propertyPostCalcFilteredCodes")]
    public List<string> PropertyPostCalcFilteredCodes { get; set; } = [];

    [JsonPropertyName("propertyStatOverrides")]
    public Dictionary<string, string> PropertyStatOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("propertyPriorityOverrides")]
    public Dictionary<string, int> PropertyPriorityOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("propertyElementalExpansions")]
    public Dictionary<string, ElementalExpansion> PropertyElementalExpansions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("elementalDamagePairs")]
    public Dictionary<string, ElementalDamagePair> ElementalDamagePairs { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("splitElementalCodes")]
    public List<string> SplitElementalCodes { get; set; } = [];

    [JsonPropertyName("propertyMergeBlocklist")]
    public List<string> PropertyMergeBlocklist { get; set; } = [];

    // ── Cube recipe lookups (formerly hardcoded in CubeRecipeImporter) ──

    [JsonPropertyName("cubeGrabberSentinels")]
    public List<string> CubeGrabberSentinels { get; set; } = [];

    [JsonPropertyName("cubeSocketPunch")]
    public CubeSocketPunchConfig CubeSocketPunch { get; set; } = new();

    [JsonPropertyName("cubeIngredientLabels")]
    public CubeIngredientLabelsConfig CubeIngredientLabels { get; set; } = new();

    public sealed class ElementalExpansion
    {
        [JsonPropertyName("min")] public string Min { get; set; } = "";
        [JsonPropertyName("max")] public string Max { get; set; } = "";
        [JsonPropertyName("len")] public string? Len { get; set; }
    }

    public sealed class ElementalDamagePair
    {
        [JsonPropertyName("max")]     public string Max { get; set; } = "";
        [JsonPropertyName("element")] public string Element { get; set; } = "";
    }

    public sealed class CubeSocketPunchConfig
    {
        [JsonPropertyName("equipTokens")]    public List<string> EquipTokens { get; set; } = [];
        [JsonPropertyName("jewelQualities")] public List<string> JewelQualities { get; set; } = [];

        [JsonIgnore] public HashSet<string> EquipTokensSet { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
        [JsonIgnore] public HashSet<string> JewelQualitiesSet { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

        internal void BuildLookups()
        {
            EquipTokensSet    = new HashSet<string>(EquipTokens,    StringComparer.OrdinalIgnoreCase);
            JewelQualitiesSet = new HashSet<string>(JewelQualities, StringComparer.OrdinalIgnoreCase);
        }
    }

    public sealed class CubeIngredientLabelsConfig
    {
        [JsonPropertyName("input")]  public Dictionary<string, string> Input  { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        [JsonPropertyName("output")] public Dictionary<string, string> Output { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        internal void BuildLookups()
        {
            if (Input.Count  > 0) Input  = new Dictionary<string, string>(Input,  StringComparer.OrdinalIgnoreCase);
            if (Output.Count > 0) Output = new Dictionary<string, string>(Output, StringComparer.OrdinalIgnoreCase);
        }
    }

    public sealed class SkippedRecipe
    {
        public int? Op { get; set; }
        public int? Param { get; set; }
        public int? Value { get; set; }
        public int? ValueMin { get; set; }
        public int? ValueMax { get; set; }
    }

    public sealed class RecipeNoteConfig
    {
        public string? Description { get; set; }
        public int? Op { get; set; }
        public int? Param { get; set; }
        public int? Value { get; set; }
        public List<string> Notes { get; set; } = [];
    }

    // Derived lookup sets built after deserialization
    [JsonIgnore]
    public HashSet<string> IgnoredUniqueItemsSet { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonIgnore]
    public HashSet<string> VanillaUniqueOverridesSet { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonIgnore]
    public HashSet<string> ElementalDamageCodesSet { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonIgnore]
    public HashSet<string> CubeRecipeCoreItemCodesSet { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonIgnore]
    public HashSet<string> CubeRecipeAllowedCodesSet { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonIgnore]
    public HashSet<string> BlockedCubeInputCodesSet { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonIgnore]
    public HashSet<string> IgnoredPropertyCodesSet { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonIgnore]
    public HashSet<string> ValidRequiredClassNamesSet { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonIgnore]
    public HashSet<int> SkippedAutoMagicGroupsSet { get; private set; } = [];

    [JsonIgnore]
    public HashSet<string> PropertyPostCalcFilteredCodesSet { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonIgnore]
    public HashSet<string> SplitElementalCodesSet { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonIgnore]
    public HashSet<string> PropertyMergeBlocklistSet { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonIgnore]
    public HashSet<string> CubeGrabberSentinelsSet { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    public void BuildLookups()
    {
        IgnoredUniqueItemsSet = new HashSet<string>(IgnoredUniqueItems, StringComparer.OrdinalIgnoreCase);
        VanillaUniqueOverridesSet = new HashSet<string>(VanillaUniqueOverrides, StringComparer.OrdinalIgnoreCase);
        ElementalDamageCodesSet = new HashSet<string>(ElementalDamageCodes, StringComparer.OrdinalIgnoreCase);
        CubeRecipeCoreItemCodesSet = new HashSet<string>(CubeRecipeCoreItemCodes, StringComparer.OrdinalIgnoreCase);
        CubeRecipeAllowedCodesSet = new HashSet<string>(CubeRecipeAllowedCodes, StringComparer.OrdinalIgnoreCase);
        BlockedCubeInputCodesSet = new HashSet<string>(BlockedCubeInputCodes, StringComparer.OrdinalIgnoreCase);
        IgnoredPropertyCodesSet = new HashSet<string>(IgnoredPropertyCodes, StringComparer.OrdinalIgnoreCase);
        ValidRequiredClassNamesSet = new HashSet<string>(ValidRequiredClassNames, StringComparer.OrdinalIgnoreCase);
        SkippedAutoMagicGroupsSet = [..SkippedAutoMagicGroups];

        PropertyPostCalcFilteredCodesSet = new HashSet<string>(PropertyPostCalcFilteredCodes, StringComparer.OrdinalIgnoreCase);
        SplitElementalCodesSet           = new HashSet<string>(SplitElementalCodes,           StringComparer.OrdinalIgnoreCase);
        PropertyMergeBlocklistSet        = new HashSet<string>(PropertyMergeBlocklist,        StringComparer.OrdinalIgnoreCase);
        CubeGrabberSentinelsSet          = new HashSet<string>(CubeGrabberSentinels,          StringComparer.OrdinalIgnoreCase);

        // Rebuild dictionaries case-insensitively where they came in via JSON.
        if (ItemTypeNameNormalizations.Count > 0)
            ItemTypeNameNormalizations = new Dictionary<string, string>(ItemTypeNameNormalizations, StringComparer.OrdinalIgnoreCase);
        if (MagicNameOverrides.Count > 0)
            MagicNameOverrides = new Dictionary<string, string>(MagicNameOverrides, StringComparer.OrdinalIgnoreCase);
        if (PropertyStatOverrides.Count > 0)
            PropertyStatOverrides = new Dictionary<string, string>(PropertyStatOverrides, StringComparer.OrdinalIgnoreCase);
        if (PropertyPriorityOverrides.Count > 0)
            PropertyPriorityOverrides = new Dictionary<string, int>(PropertyPriorityOverrides, StringComparer.OrdinalIgnoreCase);
        if (PropertyElementalExpansions.Count > 0)
            PropertyElementalExpansions = new Dictionary<string, ElementalExpansion>(PropertyElementalExpansions, StringComparer.OrdinalIgnoreCase);
        if (ElementalDamagePairs.Count > 0)
            ElementalDamagePairs = new Dictionary<string, ElementalDamagePair>(ElementalDamagePairs, StringComparer.OrdinalIgnoreCase);

        CubeSocketPunch.BuildLookups();
        CubeIngredientLabels.BuildLookups();
    }

    public static async Task<ExportConfig> LoadAsync(string path)
    {
        var json = await File.ReadAllTextAsync(path);
        var config = JsonSerializer.Deserialize<ExportConfig>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        }) ?? new ExportConfig();
        config.BuildLookups();
        return config;
    }
}
