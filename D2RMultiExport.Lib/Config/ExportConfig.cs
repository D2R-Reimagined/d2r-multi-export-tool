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
    [JsonPropertyName("monsterHealth")]
    public MonsterHealthConfig MonsterHealth { get; set; } = new();

    [JsonPropertyName("attackSpeed")]
    public AttackSpeedConfig AttackSpeed { get; set; } = new();

    [JsonPropertyName("dropRuneTypes")]
    public List<string> DropRuneTypes { get; set; } = [];

    [JsonPropertyName("dropMiscCodes")]
    public List<string> DropMiscCodes { get; set; } = [];

    [JsonPropertyName("dropSuperUniqueAreas")]
    public Dictionary<string, int[]> DropSuperUniqueAreas { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("dropMonsterAreas")]
    public Dictionary<string, int[]> DropMonsterAreas { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Engine level bonus per spawn kind (<c>champion</c>, <c>unique</c>,
    /// <c>superunique</c>) added to a monster's base level when choosing its drop
    /// treasure-class level. Kinds that are absent get no bonus.
    /// </summary>
    [JsonPropertyName("dropMonsterLevelBonuses")]
    public Dictionary<string, int> DropMonsterLevelBonuses { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Width of the quality-level bands of the game-generated equipment treasure classes
    /// (<c>armo3</c>, <c>armo6</c>, …). Must be positive; the drop exporter refuses to run
    /// otherwise rather than loop forever or emit nothing.
    /// </summary>
    [JsonPropertyName("dropGeneratedTreasureClassLevelStep")]
    public int DropGeneratedTreasureClassLevelStep { get; set; }

    /// <summary><c>itemratio.txt</c> <c>Version</c> rows exported to the drop calculator.</summary>
    [JsonPropertyName("dropItemRatioVersion")]
    public int DropItemRatioVersion { get; set; }

    /// <summary>
    /// <c>itemtypes.txt</c> codes whose exported <c>Index</c> is redirected onto another
    /// item type's key instead of the default <c>${code}itype</c>.
    /// </summary>
    [JsonPropertyName("itemTypeIndexAliases")]
    public Dictionary<string, string> ItemTypeIndexAliases { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>skills.txt <c>charclass</c> code → the HD skill-icon sprite sheet for that class.</summary>
    [JsonPropertyName("skillIconSheets")]
    public Dictionary<string, SkillIconSheet> SkillIconSheets { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Composite damage line → translation key of its combined range template, written
    /// verbatim to <c>keyed/item-stat-presentation.json</c> as <c>CompositeKeys</c>.
    /// </summary>
    [JsonPropertyName("itemStatCompositeKeys")]
    public Dictionary<string, string> ItemStatCompositeKeys { get; set; } = new(StringComparer.Ordinal);

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
    /// Maps each skills.txt three-letter class code to its three localized
    /// skill-tree category keys in skilldesc.txt page order.
    /// </summary>
    [JsonPropertyName("skillTreeCategoryKeys")]
    public Dictionary<string, List<string>> SkillTreeCategoryKeys { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The highest <em>effective</em> skill level the exported per-level description
    /// tables are solved to. A character can only spend <c>maxlvl</c> hard points, but
    /// <c>+skills</c> gear pushes the effective level far past that, so the tables have to
    /// cover the whole displayable range. The bonus-level axis of a
    /// <c>values2d</c> grid runs <c>0..(this - maxlvl)</c>.
    /// </summary>
    [JsonPropertyName("skillDescriptionMaxLevel")]
    public int SkillDescriptionMaxLevel { get; set; } = 100;

    /// <summary>
    /// The highest character level (<c>ulvl</c>) the exported <c>valuesUlvl</c> tables are
    /// solved to. Only the handful of lines whose formula actually reads <c>ulvl</c> pay
    /// for that axis.
    /// </summary>
    [JsonPropertyName("skillDescriptionCharacterLevels")]
    public int SkillDescriptionCharacterLevels { get; set; } = 100;

    /// <summary>
    /// Which branch of <c>cond('Difficulty', normal|nightmare|hell)</c> the exported
    /// description tables are solved for. The website shows end-game numbers, so the
    /// default is the hell branch; the other two are what the same line would read on a
    /// lower difficulty.
    /// </summary>
    [JsonPropertyName("skillDescriptionDifficulty")]
    public string SkillDescriptionDifficulty { get; set; } = "hell";

    /// <summary>
    /// Replacement formulas for <c>skillcalc.txt</c> / <c>misscalc.txt</c> codes whose
    /// printed description states the <em>result</em> rather than the arithmetic the game
    /// actually performs. Only codes the reference table already defines as formulas are
    /// replaced, so this cannot invent vocabulary, and a mod shipping its own copy of the
    /// table still gets the correction.
    /// </summary>
    [JsonPropertyName("calcFormulaOverrides")]
    public Dictionary<string, string> CalcFormulaOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

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

    /// <summary>
    /// Mod-added property codes that act as a class-restricted random skill tab
    /// (e.g. <c>skilltab-war</c> on Wraithstep) but have no row in
    /// <c>properties.txt</c>. Maps the property code to a 3-letter CharStats
    /// class code (<c>war</c>, <c>nec</c>, …); <see cref="Translation.PropertyKeyResolver"/>
    /// routes it through the same shape as <c>tab-rand</c> with the class
    /// fixed. Mirrors the doc-generator's hardcoded Wraithstep workaround.
    /// </summary>
    [JsonPropertyName("customSkillTabAliases")]
    public Dictionary<string, string> CustomSkillTabAliases { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("propertyElementalExpansions")]
    public Dictionary<string, ElementalExpansion> PropertyElementalExpansions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Property code → the property codes it expands into, each mapped with the original param/min/max.</summary>
    [JsonPropertyName("propertyCodeExpansions")]
    public Dictionary<string, List<string>> PropertyCodeExpansions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Property codes that set the exported <c>DamageArmorEnhanced</c> flag.</summary>
    [JsonPropertyName("damageArmorEnhancedCodes")]
    public List<string> DamageArmorEnhancedCodes { get; set; } = [];

    /// <summary>Property codes that scale Str/Dex requirements by a percentage (<c>item_req_percent</c>).</summary>
    [JsonPropertyName("requirementPercentCodes")]
    public List<string> RequirementPercentCodes { get; set; } = [];

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

    /// <summary>
    /// Throwing potions whose damage lives on the <c>missiles.txt</c> row their
    /// <c>missiletype</c> names rather than on the weapon damage columns.
    /// </summary>
    [JsonPropertyName("missilePotionDamage")]
    public MissilePotionDamageConfig MissilePotionDamage { get; set; } = new();

    public sealed class SkillIconSheet
    {
        [JsonPropertyName("folder")] public string Folder { get; set; } = "";
        [JsonPropertyName("file")]   public string File { get; set; } = "";
    }

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

    public sealed class MissilePotionDamageConfig
    {
        /// <summary>weapons.txt <c>type</c> codes whose damage is read from their missile row.</summary>
        [JsonPropertyName("itemTypes")] public List<string> ItemTypes { get; set; } = [];

        /// <summary>missiles.txt <c>EType</c> → template key for that missile's elemental damage.</summary>
        [JsonPropertyName("elementalDamageKeys")] public Dictionary<string, string> ElementalDamageKeys { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Template for a poison duration in seconds, and its exactly-one-second form.</summary>
        [JsonPropertyName("durationKey")]         public string DurationKey { get; set; } = "";
        [JsonPropertyName("durationSingularKey")] public string DurationSingularKey { get; set; } = "";

        [JsonIgnore] public HashSet<string> ItemTypesSet { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

        internal void BuildLookups()
        {
            ItemTypesSet = new HashSet<string>(ItemTypes, StringComparer.OrdinalIgnoreCase);
            if (ElementalDamageKeys.Count > 0) ElementalDamageKeys = new Dictionary<string, string>(ElementalDamageKeys, StringComparer.OrdinalIgnoreCase);
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
    public HashSet<string> DamageArmorEnhancedCodesSet { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonIgnore]
    public HashSet<string> RequirementPercentCodesSet { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

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

    [JsonIgnore]
    public HashSet<string> DropRuneTypesSet { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonIgnore]
    public HashSet<string> DropMiscCodesSet { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    public void BuildLookups()
    {
        AttackSpeed.BuildLookups();
        IgnoredUniqueItemsSet = new HashSet<string>(IgnoredUniqueItems, StringComparer.OrdinalIgnoreCase);
        VanillaUniqueOverridesSet = new HashSet<string>(VanillaUniqueOverrides, StringComparer.OrdinalIgnoreCase);
        ElementalDamageCodesSet = new HashSet<string>(ElementalDamageCodes, StringComparer.OrdinalIgnoreCase);
        DamageArmorEnhancedCodesSet = new HashSet<string>(DamageArmorEnhancedCodes, StringComparer.OrdinalIgnoreCase);
        RequirementPercentCodesSet = new HashSet<string>(RequirementPercentCodes, StringComparer.OrdinalIgnoreCase);
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
        DropRuneTypesSet                 = new HashSet<string>(DropRuneTypes,                 StringComparer.OrdinalIgnoreCase);
        DropMiscCodesSet                 = new HashSet<string>(DropMiscCodes,                 StringComparer.OrdinalIgnoreCase);

        // Rebuild dictionaries case-insensitively where they came in via JSON.
        if (ItemTypeNameNormalizations.Count > 0)
            ItemTypeNameNormalizations = new Dictionary<string, string>(ItemTypeNameNormalizations, StringComparer.OrdinalIgnoreCase);
        if (MagicNameOverrides.Count > 0)
            MagicNameOverrides = new Dictionary<string, string>(MagicNameOverrides, StringComparer.OrdinalIgnoreCase);
        if (PropertyStatOverrides.Count > 0)
            PropertyStatOverrides = new Dictionary<string, string>(PropertyStatOverrides, StringComparer.OrdinalIgnoreCase);
        if (PropertyPriorityOverrides.Count > 0)
            PropertyPriorityOverrides = new Dictionary<string, int>(PropertyPriorityOverrides, StringComparer.OrdinalIgnoreCase);
        if (CustomSkillTabAliases.Count > 0)
            CustomSkillTabAliases = new Dictionary<string, string>(CustomSkillTabAliases, StringComparer.OrdinalIgnoreCase);
        if (PropertyElementalExpansions.Count > 0)
            PropertyElementalExpansions = new Dictionary<string, ElementalExpansion>(PropertyElementalExpansions, StringComparer.OrdinalIgnoreCase);
        PropertyCodeExpansions = new Dictionary<string, List<string>>(PropertyCodeExpansions, StringComparer.OrdinalIgnoreCase);
        if (ElementalDamagePairs.Count > 0)
            ElementalDamagePairs = new Dictionary<string, ElementalDamagePair>(ElementalDamagePairs, StringComparer.OrdinalIgnoreCase);
        if (CalcFormulaOverrides.Count > 0)
            CalcFormulaOverrides = new Dictionary<string, string>(CalcFormulaOverrides, StringComparer.OrdinalIgnoreCase);
        // System.Text.Json replaces these dictionaries with default-comparer instances, so
        // the case-insensitive initializers above do not survive deserialization on their own.
        DropSuperUniqueAreas = new Dictionary<string, int[]>(DropSuperUniqueAreas, StringComparer.OrdinalIgnoreCase);
        DropMonsterAreas = new Dictionary<string, int[]>(DropMonsterAreas, StringComparer.OrdinalIgnoreCase);
        DropMonsterLevelBonuses = new Dictionary<string, int>(DropMonsterLevelBonuses, StringComparer.OrdinalIgnoreCase);
        ItemTypeIndexAliases = new Dictionary<string, string>(ItemTypeIndexAliases, StringComparer.OrdinalIgnoreCase);
        SkillIconSheets = new Dictionary<string, SkillIconSheet>(SkillIconSheets, StringComparer.OrdinalIgnoreCase);

        CubeSocketPunch.BuildLookups();
        CubeIngredientLabels.BuildLookups();
        MissilePotionDamage.BuildLookups();
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
