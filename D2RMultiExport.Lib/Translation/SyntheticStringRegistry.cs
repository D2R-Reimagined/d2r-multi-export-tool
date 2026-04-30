// SPDX-License-Identifier: GPL-3.0-or-later
using System.Reflection;
using D2RReimaginedTools.JsonFileParsers;

namespace D2RMultiExport.Lib.Translation;

/// <summary>
/// Centralized list of every translation key the tool itself introduces (i.e. NOT a
/// key that already exists in the game's `local\lng\strings\*.json` files).
///
/// Every synthetic key is seeded in a single combined file
/// <c>Config/synthetic-strings.json</c> that uses the same flat row layout D2R
/// uses for its native <c>local\lng\strings\*.json</c> tables — each entry is an
/// object with an <c>id</c>, a <c>Key</c>, and one column per language
/// (<c>enUS, zhTW, deDE, esES, frFR, itIT, koKR, plPL, esMX, jaJP, ptBR, ruRU, zhCN</c>).
/// Translators only ever edit this one file. Keys from this registry are merged
/// into every per-language strings bundle written by <c>LanguageBundleExporter</c>.
///
/// The keys here are referenced from <c>PropertyKeyResolver</c>, equipment helpers,
/// and importers. Whenever a new synthetic key is added in code, it MUST also be
/// added to <c>synthetic-strings.json</c> with at least an <c>enUS</c> value
/// (other languages can stay as empty strings until a translator fills them in).
/// </summary>
public static class SyntheticStringRegistry
{
    /// <summary>Strongly-typed key identifiers — use these instead of string literals.</summary>
    public static class Keys
    {
        // ── Composite property shapes (single visible line, multi-component) ────────
        public const string SkillTabBonusClassOnly   = "strSkillTabBonusClassOnly";     // "+%d to %s (%s Only)"
        public const string SkillTabRandomClassOnly  = "strSkillTabRandomClassOnly";    // "+%d Random Skill Tab" (the "(Class Only)" suffix is appended via KeyedLine.ClassOnly)
        public const string DamageMergedRange        = "strDamageMergedRange";          // "Adds %d-%d to %d-%d Damage" (level-scaled, no game equivalent)
        public const string SkillCharges             = "strSkillCharges";               // "Level %d %s (%d/%d Charges)"
        public const string ChanceToCastOnAttack     = "strChanceToCastOnAttack";       // "%d%% Chance to cast level %d %s on attack"
        public const string ChanceToCastOnStriking   = "strChanceToCastOnStriking";     // "%d%% Chance to cast level %d %s on striking"
        public const string ChanceToCastOnHit        = "strChanceToCastOnHit";          // "%d%% Chance to cast level %d %s when struck"
        public const string ChanceToCastOnKill       = "strChanceToCastOnKill";         // "%d%% Chance to cast level %d %s when you Kill an Enemy"
        public const string ChanceToCastOnDeath      = "strChanceToCastOnDeath";        // "%d%% Chance to cast level %d %s on death"
        public const string ChanceToCastOnLevelUp    = "strChanceToCastOnLevelUp";      // "%d%% Chance to cast level %d %s on level up"
        public const string ChanceToCastWhenStruck   = "strChanceToCastWhenStruck";     // synonym → ChanceToCastOnHit (kept for clarity)
        public const string ReanimateAs              = "strReanimateAs";                // "%d%% Reanimate as: %s"
        public const string SkillRandom              = "strSkillRandom";                // "+%d Random Skill"
        public const string SkillRandomClass         = "strSkillRandomClass";           // "+%d Random %s Skill"
        public const string SkillRandomFromSkill     = "strSkillRandomFromSkill";       // "+%d to %s"  (no class qualifier)
        public const string SkillRandomFromSkillClass = "strSkillRandomFromSkillClass"; // "+%d to %s (%s Only)"
        public const string AurasWhenEquipped        = "strAuraWhenEquipped";           // "Level %d %s Aura When Equipped"

        // ── Stat / status synthetics not present as game keys with these names ──────
        public const string CharmWeight              = "strCharmWeight";                // "Charm Weight: %d"
        public const string Indestructible           = "strIndestructible";             // "Indestructible"
        public const string CannotBeFrozen           = "strCannotBeFrozen";             // "Cannot Be Frozen"
        public const string SlainMonstersRestInPeace = "strSlainMonstersRestInPeace";   // "Slain Monsters Rest in Peace"
        public const string Ethereal                 = "strEthereal";                   // "Ethereal (Cannot be Repaired)"
        public const string SocketedCount            = "strSocketedCount";              // "Socketed (%d)"
        public const string DropConditionLevel       = "strDropConditionLevel";         // "Required Level: %d"
        public const string ReplenishesQuantity      = "strReplenishesQuantity";        // "Replenishes Quantity"
        public const string IncreasedStackSize       = "strIncreasedStackSize";         // "Increased Stack Size"
        public const string FiresExplosiveArrows     = "strFiresExplosiveArrows";       // "Fires Explosive Arrows or Bolts"
        public const string FiresMagicArrows         = "strFiresMagicArrows";           // "Fires Magic Arrows"
        public const string PiercingAttack           = "strPiercingAttack";             // "+%d%% Piercing Attack"
        public const string IgnoreTargetDefense      = "strIgnoreTargetDefense";        // "Ignore Target's Defense"
        public const string ItemExtraBlood           = "strItemExtraBlood";             // "Extra Blood"  (item_extrablood synthetic)
        public const string RequirementsReducedBy    = "strRequirementsReducedBy";      // "Requirements -%d%%"  (ease, when ItemStatCost has no descstr)
        public const string RequirementsIncreasedBy  = "strRequirementsIncreasedBy";    // "Requirements +%d%%"  (ease, when ItemStatCost has no descstr)
        public const string PropertyGroupsProperty   = "strPropertyGroupsProperty";     // "Random Grouped Affix" (display label for propertygroups.txt parent lines)
        // NOTE: strModMinDamageRange / strModPoisonDamageRange are NOT registered here:
        // both are game-native CASC keys (extracted from item-modifiers.json via CascView),
        // so PropertyKeyResolver / PropertyCleanup reference them as bare string literals
        // (mod-overridable) per the convention: bare literal = CASC key, registry constant = tool-introduced.

        // ── Range/per-level synthetic shapes ────────────────────────────────────────
        // Legacy wrapper synthetic — kept for back-compat lookup, but the resolver no
        // longer emits it. Per-level rows now ride on the inner stat template and set
        // KeyedLine.PerLevel = true; the website appends PerCharacterLevelSuffix.
        public const string PerCharacterLevel        = "strPerCharacterLevel";          // "%s (Based on Character Level)"
        public const string PerCharacterLevelSuffix  = "strPerCharacterLevelSuffix";    // " (Based on Character Level)"
        public const string PerLevelGrowthRange      = "strPerLevelGrowthRange";        // "%s %d-%d (%s Per Level)"

        // ── Equipment synthetics ────────────────────────────────────────────────────
        public const string Defense                  = "strDefense";                    // "Defense: %d"
        public const string DefenseRange             = "strDefenseRange";               // "Defense: %d-%d"
        public const string ModEnhancedDefense       = "strModEnhancedDefense";         // "+%d-%d%% Enhanced Defense" (ac% range)
        public const string ChanceToBlock            = "strChanceToBlock";              // "Chance to Block: %d%%"
        public const string Durability               = "strDurability";                 // "Durability: %d of %d"
        public const string DurabilityMax            = "strDurabilityMax";              // "Durability: %d"
        public const string AttackSpeed              = "strAttackSpeed";                // synonym; rare
        public const string WeaponDamageOneHand      = "strWeaponDamageOneHand";        // "One-Hand Damage: %d to %d"
        public const string WeaponDamageTwoHand      = "strWeaponDamageTwoHand";        // "Two-Hand Damage: %d to %d"
        public const string WeaponDamageThrow        = "strWeaponDamageThrow";          // "Throw Damage: %d to %d"
        public const string WeaponDamageGeneric      = "strWeaponDamageGeneric";        // "Damage: %d to %d"
        // Level-scaled "(low-high) to (low-high)" variants emitted when min and/or max scale by required level.
        public const string WeaponDamageOneHandRange = "strWeaponDamageOneHandRange";   // "One-Hand Damage: %d-%d to %d-%d"
        public const string WeaponDamageTwoHandRange = "strWeaponDamageTwoHandRange";   // "Two-Hand Damage: %d-%d to %d-%d"
        public const string WeaponDamageThrowRange   = "strWeaponDamageThrowRange";     // "Throw Damage: %d-%d to %d-%d"
        public const string WeaponDamageGenericRange = "strWeaponDamageGenericRange";   // "Damage: %d-%d to %d-%d"
        public const string DefenseRangeRange        = "strDefenseRangeRange";          // "Defense: %d-%d to %d-%d"
        public const string ElementalDamage          = "strElementalDamage";            // "Elemental Damage: %d to %d"
        public const string GemSocketsTier           = "strGemSocketsTier";             // "(%d-%d): %d"  (level-range : sockets)
        public const string GemSocketsOpen           = "strGemSocketsOpen";             // "(%d+): %d"
        public const string SmiteDamage              = "strSmiteDamage";                // "Smite Damage: %d to %d"
        public const string KickDamage               = "strKickDamage";                 // "Kick Damage: %d to %d"
        public const string RequiredStrength         = "strRequiredStrength";           // "Required Strength: %d"
        public const string RequiredDexterity        = "strRequiredDexterity";          // "Required Dexterity: %d"
        public const string RequiredLevel            = "strRequiredLevel";              // "Required Level: %d"
        public const string RequiredClass            = "strRequiredClass";              // "(%s Only)"
        public const string ItemTypeName             = "strItemTypeName";               // pass-through for ItemType labels we don't have keys for

        // ── Sets / cube ─────────────────────────────────────────────────────────────
        public const string PartialSetBonus          = "strPartialSetBonus";            // "(%d items): %s"
        public const string FullSetBonus             = "strFullSetBonus";               // "Full Set Bonus"
        public const string CubeRecipe               = "strCubeRecipe";                 // "Item Crafting Recipe"
        public const string CubeRecipeNoteRequiresLadder = "strCubeRecipeNoteRequiresLadder"; // "Requires Ladder Character"
        public const string CubeRecipeNoteHardcoreOnly = "strCubeRecipeNoteHardcoreOnly"; // "Hardcore Only"
        // ── Cube recipe note labels (one per distinct heuristic / op-based literal) ─────
        public const string CubeNoteOrbOfCorruption       = "strCubeNoteOrbOfCorruption";       // "Orb of Corruption Recipe"
        public const string CubeNoteOrbOfConversion       = "strCubeNoteOrbOfConversion";       // "Orb of Conversion Recipe"
        public const string CubeNoteOrbOfAssemblage       = "strCubeNoteOrbOfAssemblage";       // "Orb of Assemblage Recipe"
        public const string CubeNoteOrbOfInfusion         = "strCubeNoteOrbOfInfusion";         // "Orb of Infusion Recipe"
        public const string CubeNoteOrbOfSocketing        = "strCubeNoteOrbOfSocketing";        // "Orb of Socketing Recipe"
        public const string CubeNoteOrbOfShadows          = "strCubeNoteOrbOfShadows";          // "Orb of Shadows Recipe"
        public const string CubeNoteSocketPunch           = "strCubeNoteSocketPunch";           // "Socket Punch Recipe"
        public const string CubeNotePliers                = "strCubeNotePliers";                // "Pliers Recipe"
        public const string CubeNoteItemCrafting          = "strCubeNoteItemCrafting";          // "Item Crafting Recipe" (alias of strCubeRecipe; kept for note-mapping)
        public const string CubeNoteSunderItemCrafting    = "strCubeNoteSunderItemCrafting";    // "Sunder Item Crafting Recipe"
        public const string CubeNoteTristramUberSouls     = "strCubeNoteTristramUberSouls";     // "Tristram Uber Souls Recipe"
        public const string CubeNoteKeyConversion         = "strCubeNoteKeyConversion";         // "Key Conversion Recipe"
        public const string CubeNoteRejuvenationPotion    = "strCubeNoteRejuvenationPotion";    // "Rejuvenation Potion Recipe"
        public const string CubeNoteForceEthereal         = "strCubeNoteForceEthereal";         // "Force Ethereal Recipe"
        public const string CubeNoteRepairNonEthereal     = "strCubeNoteRepairNonEthereal";     // "Repair Non-Ethereal Item"
        public const string CubeNoteRepairEthereal        = "strCubeNoteRepairEthereal";        // "Repair Ethereal Recipe"
        public const string CubeNoteForceWhite            = "strCubeNoteForceWhite";            // "Force White Recipe"
        public const string CubeNoteRerollItem            = "strCubeNoteRerollItem";            // "Reroll Item Recipe"
        public const string CubeNoteJewelUpgrade          = "strCubeNoteJewelUpgrade";          // "Jewel Upgrade Recipe"
        public const string CubeNoteRecycle               = "strCubeNoteRecycle";               // "Recycle Recipe"
        public const string CubeNoteSplashCharmUpgrade    = "strCubeNoteSplashCharmUpgrade";    // "Splash Charm Upgrade Recipe"
        public const string CubeNoteUberCharmUpgrade      = "strCubeNoteUberCharmUpgrade";      // "Uber Charm Upgrade Recipe"
        public const string CubeNoteBaseTierUpgrade       = "strCubeNoteBaseTierUpgrade";       // "Base Tier Upgrade Recipe"
        public const string CubeNoteReplenishQuiver       = "strCubeNoteReplenishQuiver";       // "Replenish Quiver/Bolt Case Recipe"
        public const string CubeNoteCowPortalRecipe       = "strCubeNoteCowPortalRecipe";       // "Cow Portal Recipe"
        public const string CubeNoteRandomMiniUberPortal  = "strCubeNoteRandomMiniUberPortal";  // "Random Mini-Uber Portal Recipe"
        public const string CubeNoteTristramUberPortal    = "strCubeNoteTristramUberPortal";    // "Tristram Uber Portal Recipe"
        public const string CubeNoteItemEnchantment       = "strCubeNoteItemEnchantment";       // "Item Enchantment Recipe"
        public const string CubeNoteRerollUberAncient     = "strCubeNoteRerollUberAncient";     // "Reroll Uber Ancient Material"
        public const string CubeNoteCowPortal             = "strCubeNoteCowPortal";             // "Cow Portal"
        public const string CubeNotePandemoniumPortal     = "strCubeNotePandemoniumPortal";     // "Pandemonium Portal"
        public const string CubeNotePandemoniumFinalePortal = "strCubeNotePandemoniumFinalePortal"; // "Pandemonium Finale Portal"
        public const string CubeNoteRedPortal             = "strCubeNoteRedPortal";             // "Red Portal"
        public const string CubeNoteTransmuteItem         = "strCubeNoteTransmuteItem";         // "Transmute Item"
        public const string CubeNoteSocketItem            = "strCubeNoteSocketItem";            // "Socket Item"
        public const string CubeNoteEarlyGamePotion       = "strCubeNoteEarlyGamePotion";       // "This recipe is used for early game potion crafting."

        // ── Cube output / ingredient names (CubeRecipeImporter.ResolveName fallbacks) ──
        public const string CubeOutputUseTypeOfInput1     = "strCubeOutputUseTypeOfInput1";     // "Use Type of Input 1"
        public const string CubeOutputUseItemFromInput1   = "strCubeOutputUseItemFromInput1";   // "Use Item from Input 1"
        public const string CubeOutputReturnGemBag        = "strCubeOutputReturnGemBag";        // "Return Gem Bag, Update Gem Credits"
        public const string CubeNameUnknown               = "strCubeNameUnknown";               // "Unknown"

        // ── Cube ingredient / output qualifier labels (CubeRecipeImporter.InputDisplay/OutputDisplay) ──
        // Fixed-text qualifiers.
        public const string CubeQualifierQuantity                = "strCubeQualifierQuantity";                // "Quantity"
        public const string CubeQualifierLowQuality              = "strCubeQualifierLowQuality";              // "Low Quality"           (input)
        public const string CubeQualifierNormalQuality           = "strCubeQualifierNormalQuality";           // "Normal Quality"        (input)
        public const string CubeQualifierHighQualitySuperior     = "strCubeQualifierHighQualitySuperior";     // "High Quality (Superior)" (input)
        public const string CubeQualifierLowQualityItem          = "strCubeQualifierLowQualityItem";          // "Low Quality Item"      (output)
        public const string CubeQualifierNormalItem              = "strCubeQualifierNormalItem";              // "Normal Item"           (output)
        public const string CubeQualifierHighQualityItemSuperior = "strCubeQualifierHighQualityItemSuperior"; // "High Quality Item (Superior)" (output)
        public const string CubeQualifierMagicItem               = "strCubeQualifierMagicItem";               // "Magic Item"
        public const string CubeQualifierSetItem                 = "strCubeQualifierSetItem";                 // "Set Item"
        public const string CubeQualifierRareItem                = "strCubeQualifierRareItem";                // "Rare Item"
        public const string CubeQualifierUniqueItem              = "strCubeQualifierUniqueItem";              // "Unique Item"
        public const string CubeQualifierCraftedItem             = "strCubeQualifierCraftedItem";             // "Crafted Item"
        public const string CubeQualifierTemperedItem            = "strCubeQualifierTemperedItem";            // "Tempered Item"
        public const string CubeQualifierNoSockets               = "strCubeQualifierNoSockets";               // "No Sockets"
        public const string CubeQualifierItemWithSockets         = "strCubeQualifierItemWithSockets";         // "Item with Sockets"
        public const string CubeQualifierNotEthereal             = "strCubeQualifierNotEthereal";             // "Not Ethereal"
        public const string CubeQualifierEthereal                = "strCubeQualifierEthereal";                // "Ethereal"               (input)
        public const string CubeQualifierEtherealItem            = "strCubeQualifierEtherealItem";            // "Ethereal Item"          (output)
        public const string CubeQualifierUpgradeable             = "strCubeQualifierUpgradeable";             // "Upgradeable"
        public const string CubeQualifierBasicItem               = "strCubeQualifierBasicItem";               // "Basic Item"
        public const string CubeQualifierExceptionalItem         = "strCubeQualifierExceptionalItem";         // "Exceptional Item"
        public const string CubeQualifierEliteItem               = "strCubeQualifierEliteItem";               // "Elite Item"
        public const string CubeQualifierNotARuneword            = "strCubeQualifierNotARuneword";            // "Not a Runeword"
        public const string CubeQualifierKeepModifiers           = "strCubeQualifierKeepModifiers";           // "Keep Modifiers"
        public const string CubeQualifierUnsocketDestroy         = "strCubeQualifierUnsocketDestroy";         // "Unsocket (Destroy Socketed)"
        public const string CubeQualifierRemoveSocketedReturn    = "strCubeQualifierRemoveSocketedReturn";    // "Remove Socketed (Return)"
        public const string CubeQualifierRegenerateUnique        = "strCubeQualifierRegenerateUnique";        // "Regenerate Unique (Reroll if Base Upgraded)"
        public const string CubeQualifierRepairItem              = "strCubeQualifierRepairItem";              // "Repair Item"
        public const string CubeQualifierRechargeCharges         = "strCubeQualifierRechargeCharges";         // "Recharge Charges"
        // Parametrised qualifiers (single numeric arg).
        public const string CubeQualifierQuantityN               = "strCubeQualifierQuantityN";               // "Quantity (%d)"
        public const string CubeQualifierItemWithSocketsN        = "strCubeQualifierItemWithSocketsN";        // "Item with Sockets (%d)"
        public const string CubeQualifierForcePrefixN            = "strCubeQualifierForcePrefixN";            // "Force Prefix (%d)"
        public const string CubeQualifierForceSuffixN            = "strCubeQualifierForceSuffixN";            // "Force Suffix (%d)"
        public const string CubeQualifierSetLevelN               = "strCubeQualifierSetLevelN";               // "Set Level (%d)"

        // ── Runeword / runes ────────────────────────────────────────────────────────
        public const string RunewordHeader           = "strRunewordHeader";             // "Runeword"
        public const string RunesIn                  = "strRunesIn";                    // "%s in %s"
        // Scope qualifier suffixes appended after a runeword line whose
        // KeyedLine.Qualifier is set, so multi-itype runewords (e.g. Steel:
        // Sword/Mace/Axe) display per-itype rune contributions clearly.
        public const string RuneScopeWeapon          = "strRuneScopeWeapon";            // " (Weapon)"
        public const string RuneScopeShield          = "strRuneScopeShield";            // " (Shield)"
        public const string RuneScopeArmor           = "strRuneScopeArmor";             // " (Armor)"
    }

    /// <summary>Loaded enUS seed values keyed by <see cref="Keys"/>.</summary>
    public static IReadOnlyDictionary<string, string> EnUSSeed { get; private set; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Loaded per-language seed values: <c>lang → (key → value)</c>. Populated for each
    /// non-enUS language present in the combined seed file. Empty-string values are
    /// skipped (treated as "translator hasn't supplied this yet, fall back to enUS").
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> PerLanguageSeeds { get; private set; } =
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);

    /// <summary>
    /// Loads the combined synthetic-strings seed from <c>Config/synthetic-strings.json</c>.
    /// The file uses the D2R native flat row layout — a JSON array of rows, each with
    /// an <c>id</c>, a <c>Key</c>, and one column per language. Both <see cref="EnUSSeed"/>
    /// and <see cref="PerLanguageSeeds"/> are populated by this single load.
    /// </summary>
    public static async Task LoadEnUSAsync(string configDir)
    {
        var path = Path.Combine(configDir, "synthetic-strings.json");
        if (!File.Exists(path))
        {
            EnUSSeed = new Dictionary<string, string>(StringComparer.Ordinal);
            PerLanguageSeeds = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
            return;
        }

        var parser = new TranslationFileParser(path);
        var rows = await parser.GetAllEntriesAsync();

        var enUS = new Dictionary<string, string>(StringComparer.Ordinal);
        var perLang = MultiLanguageTranslationService.AllLanguages
            .Where(l => l != "enUS")
            .ToDictionary(l => l,
                          _ => new Dictionary<string, string>(StringComparer.Ordinal),
                          StringComparer.Ordinal);

        foreach (var row in rows)
        {
            if (string.IsNullOrEmpty(row.Key)) continue;
            if (!string.IsNullOrEmpty(row.EnUS)) enUS[row.Key] = row.EnUS;

            // Empty/whitespace per-language values are treated as "not yet translated"
            // so MergeSynthetic falls back to enUS for that key/language.
            if (!string.IsNullOrWhiteSpace(row.ZhTW)) perLang["zhTW"][row.Key] = row.ZhTW!;
            if (!string.IsNullOrWhiteSpace(row.DeDE)) perLang["deDE"][row.Key] = row.DeDE!;
            if (!string.IsNullOrWhiteSpace(row.EsES)) perLang["esES"][row.Key] = row.EsES!;
            if (!string.IsNullOrWhiteSpace(row.FrFR)) perLang["frFR"][row.Key] = row.FrFR!;
            if (!string.IsNullOrWhiteSpace(row.ItIT)) perLang["itIT"][row.Key] = row.ItIT!;
            if (!string.IsNullOrWhiteSpace(row.KoKR)) perLang["koKR"][row.Key] = row.KoKR!;
            if (!string.IsNullOrWhiteSpace(row.PlPL)) perLang["plPL"][row.Key] = row.PlPL!;
            if (!string.IsNullOrWhiteSpace(row.EsMX)) perLang["esMX"][row.Key] = row.EsMX!;
            if (!string.IsNullOrWhiteSpace(row.JaJP)) perLang["jaJP"][row.Key] = row.JaJP!;
            if (!string.IsNullOrWhiteSpace(row.PtBR)) perLang["ptBR"][row.Key] = row.PtBR!;
            if (!string.IsNullOrWhiteSpace(row.RuRU)) perLang["ruRU"][row.Key] = row.RuRU!;
            if (!string.IsNullOrWhiteSpace(row.ZhCN)) perLang["zhCN"][row.Key] = row.ZhCN!;
        }

        EnUSSeed = enUS;
        PerLanguageSeeds = perLang.ToDictionary(
            kvp => kvp.Key,
            kvp => (IReadOnlyDictionary<string, string>)kvp.Value,
            StringComparer.Ordinal);
    }

    /// <summary>
    /// Enumerates every known synthetic key constant declared on <see cref="Keys"/>.
    /// Useful for the <c>synthetic-strings.txt</c> audit report.
    /// </summary>
    public static IEnumerable<string> AllKnownKeys()
    {
        foreach (var f in typeof(Keys).GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy))
        {
            if (f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(string))
            {
                yield return (string)f.GetRawConstantValue()!;
            }
        }
    }
}
