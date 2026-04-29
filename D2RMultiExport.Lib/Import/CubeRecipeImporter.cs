// SPDX-License-Identifier: GPL-3.0-or-later
using D2RMultiExport.Lib.ErrorHandling;
using D2RMultiExport.Lib.Models;
using D2RMultiExport.Lib.Translation;
using D2RReimaginedTools.TextFileParsers;
using System.Reflection;
using K = D2RMultiExport.Lib.Translation.SyntheticStringRegistry.Keys;

namespace D2RMultiExport.Lib.Import;

public sealed class CubeRecipeImporter
{
    private readonly GameData _data;
    private readonly string _excelPath;
    private readonly Config.ExportConfig _config;

    // Cube ingredient/output qualifier labels are loaded from
    // export-config.json (cubeIngredientLabels). They ship raw English to
    // the wire by design (AGENTS.md exception #1) but storage is JSON so a
    // non-coder can fix typos without a code change.
    private Dictionary<string, string> InputDisplay  => _config.CubeIngredientLabels.Input;
    private Dictionary<string, string> OutputDisplay => _config.CubeIngredientLabels.Output;

    public bool EarlyStopSentinelEnabled { get; set; }
    public bool CubeRecipeUseDescription { get; set; }

    public CubeRecipeImporter(string excelPath, GameData data)
    {
        _excelPath = excelPath;
        _data = data;
        _config = data.ExportConfig;
    }

    public async Task<ImportResult<CubeRecipeExport>> ImportAsync()
    {
        var result = new ImportResult<CubeRecipeExport>();

        IList<D2RReimaginedTools.Models.CubeMain> rawEntries;
        try
        {
            rawEntries = await CubeMainParser.GetEntries(Path.Combine(_excelPath, "CubeMain.txt"));
        }
        catch (Exception ex)
        {
            result.AddError("CubeRecipe", "CubeMain.txt", $"Failed to load file: {ex.Message}", ex);
            return result;
        }

        int index = 0;
        // Once we cross the early-stop sentinel, switch to a "skip-but-continue"
        // mode where every subsequent row is dropped UNLESS it matches the
        // socket-punch pattern (Helm/Torso/Shield/Weapon + jewel × {mag|rar|uni}
        // → useitem with Mod1 = sock). This keeps the fast smoke-run truncation
        // while still recovering the tail-of-file socket-punch recipes the OG
        // tool used to produce.
        bool postSentinel = false;
        foreach (var entry in rawEntries)
        {
            index++;
            try
            {
                if (entry.Enabled == false) continue;

                if (EarlyStopSentinelEnabled && !postSentinel && IsEarlyStopSentinel(entry.Input1, entry.Output))
                {
                    postSentinel = true;
                    continue;
                }

                if (postSentinel && !IsPostSentinelSocketPunchRecipe(entry))
                    continue;

                // Apply skip rules from config
                if (IsSkipped(entry)) continue;

                // Check for blocked input tokens
                if (HasBlockedInputs(entry)) continue;

                var export = new CubeRecipeExport
                {
                    Index = index,
                    Description = CubeRecipeUseDescription ? (entry.Description ?? "") : "",
                    Enabled = true,
                    NumInputs = entry.NumInputs ?? 0,
                    Op = int.TryParse(entry.Operation, out var op) ? op : null,
                    Param = int.TryParse(entry.Param, out var param) ? param : null,
                    Value = int.TryParse(entry.Value, out var value) ? value : null,
                    Class = entry.Class ?? ""
                };

                // Resolve the 3-letter class token (e.g. "pal") to its localized
                // class-restriction translation key so the keyed JSON bundle never
                // carries the raw CharStats code on the wire.
                // Use the same parenthesized "(Class Only)" CASC keys that uniques
                // and sets render for their class-restriction footer (e.g. "pal" →
                // "PalOnly" → "(Paladin Only)"), so the website's translation table
                // has a single entry per class instead of separate name vs. only forms.
                var classKey = Translation.PropertyKeyResolver.TryGetClassOnlyKey(export.Class);
                if (classKey != null)
                {
                    export.ClassLine = KeyedLine.Of(classKey);
                }

                // Also surface the plain English class-name string in the same
                // shape uniques/sets/runewords use (`ExportEquipment.RequiredClass`,
                // e.g. "Amazon", "Sorceress"). The website's recipe renderer keys
                // off this field for class-restriction styling, so it must be
                // present alongside the localized `Class` keyed line.
                var className = Translation.PropertyKeyResolver.TryGetClassNameKey(export.Class);
                if (className != null)
                {
                    export.RequiredClass = className;
                }

                // 1. Process Inputs (1-7)
                for (int i = 1; i <= 7; i++)
                {
                    var inputStr = ImportReflection.GetString(entry, $"Input{i}");
                    if (!string.IsNullOrEmpty(inputStr))
                    {
                        export.Inputs.Add(ParseIngredient(inputStr));
                    }
                }
                export.ResolvedInputsCount = export.Inputs.Count;

                // 2. Process Outputs (A, B, C)
                export.Outputs.A = ParseOutput(entry.Output, null, "", entry);
                export.Outputs.B = ParseOutput(entry.OutputB, null, "B", entry);
                export.Outputs.C = ParseOutput(entry.OutputC, null, "C", entry);

                // 3. Process Notes/Heuristics
                ProcessNotes(export, entry);

                if (export.Inputs.Count > 0 && (export.Outputs.A != null || export.Outputs.B != null || export.Outputs.C != null))
                {
                    result.AddItem(export);
                }
            }
            catch (Exception ex)
            {
                result.AddError("CubeRecipe", entry.Description ?? $"Index {index}", $"Failed to process: {ex.Message}", ex);
            }
        }

        return result;
    }

    private CubeIngredientExport ParseIngredient(string token)
    {
        var parts = token.Split(',').Select(p => p.Trim().Trim('"')).Where(p => p.Length > 0).ToList();
        var ingredient = new CubeIngredientExport { RawToken = token, Quantity = 1 };

        string? mainCode = null;

        foreach (var p in parts)
        {
            if (p.StartsWith("qty="))
            {
                if (int.TryParse(p.Substring(4), out var q)) ingredient.Quantity = q;
                ingredient.Qualifiers.Add(InputDisplay.TryGetValue("qty=#", out var d) ? d.Replace("#", ingredient.Quantity.ToString()) : p);
                var keyed = CubeQualifierKeyMap.TryGetInputQualifier(p, ingredient.Quantity);
                if (keyed != null) ingredient.KeyedQualifiers.Add(keyed);
            }
            else if (p.StartsWith("sock="))
            {
                ingredient.Qualifiers.Add(InputDisplay.TryGetValue("sock=#", out var d) ? d.Replace("#", p.Substring(5)) : p);
                var keyed = CubeQualifierKeyMap.TryGetInputQualifier(p, ingredient.Quantity);
                if (keyed != null) ingredient.KeyedQualifiers.Add(keyed);
            }
            else if (InputDisplay.TryGetValue(p, out var friendly))
            {
                ingredient.Qualifiers.Add(friendly);
                var keyed = CubeQualifierKeyMap.TryGetInputQualifier(p, ingredient.Quantity);
                if (keyed != null) ingredient.KeyedQualifiers.Add(keyed);
            }
            else
            {
                // Likely a code or type
                mainCode = p;
            }
        }

        var (resolvedName, resolvedKey) = ResolveNameLine(mainCode, null);
        ingredient.Name = resolvedName ?? mainCode ?? "Unknown";
        ingredient.NameLine = resolvedKey ?? KeyedLine.Of(SyntheticStringRegistry.Keys.CubeNameUnknown);
        return ingredient;
    }

    private CubeOutputExport? ParseOutput(string? token, int? chance, string suffix, D2RReimaginedTools.Models.CubeMain entry)
    {
        if (string.IsNullOrEmpty(token)) return null;

        var parts = token.Split(',').Select(p => p.Trim().Trim('"')).Where(p => p.Length > 0).ToList();
        var output = new CubeOutputExport { MainToken = token, Quantity = 1, OutputChance = chance };

        string? mainCode = null;

        foreach (var p in parts)
        {
            string? friendly = null;
            if (p.StartsWith("qty="))
            {
                if (int.TryParse(p.Substring(4), out var q)) output.Quantity = q;
                friendly = OutputDisplay.TryGetValue("qty=#", out var d) ? d.Replace("#", output.Quantity.ToString()) : p;
            }
            else if (p.StartsWith("pre="))
            {
                friendly = OutputDisplay.TryGetValue("pre=#", out var d) ? d.Replace("#", p.Substring(4)) : p;
            }
            else if (p.StartsWith("suf="))
            {
                friendly = OutputDisplay.TryGetValue("suf=#", out var d) ? d.Replace("#", p.Substring(4)) : p;
            }
            else if (p.StartsWith("sock="))
            {
                friendly = OutputDisplay.TryGetValue("sock=#", out var d) ? d.Replace("#", p.Substring(5)) : p;
            }
            else if (p.StartsWith("lvl="))
            {
                friendly = OutputDisplay.TryGetValue("lvl=#", out var d) ? d.Replace("#", p.Substring(4)) : p;
            }
            else if (OutputDisplay.TryGetValue(p, out var fixedFriendly))
            {
                friendly = fixedFriendly;
            }
            else
            {
                mainCode = p;
            }

            if (friendly != null)
            {
                output.Qualifiers.Add(friendly);
                var keyed = CubeQualifierKeyMap.TryGetOutputQualifier(p, output.Quantity);
                if (keyed != null) output.KeyedQualifiers.Add(keyed);
            }
        }

        var (resolvedName, resolvedKey) = ResolveNameLine(mainCode, entry);
        output.Name = resolvedName ?? mainCode ?? "Unknown";
        output.NameLine = resolvedKey ?? KeyedLine.Of(SyntheticStringRegistry.Keys.CubeNameUnknown);

        // Process properties
        for (int i = 1; i <= 5; i++) // CubeMain only has Mod1 to Mod5
        {
            var propCode = ImportReflection.GetString(entry, $"Mod{i}{suffix}");
            if (string.IsNullOrEmpty(propCode)) continue;
            if (PropertyMapper.IsIgnored(propCode, _data.ExportConfig)) continue;

            var param = ImportReflection.GetString(entry, $"Mod{i}Param{suffix}");
            var min = ImportReflection.GetInt(entry, $"Mod{i}Min{suffix}");
            var max = ImportReflection.GetInt(entry, $"Mod{i}Max{suffix}");
            var modChance = ImportReflection.GetInt(entry, $"Mod{i}Chance{suffix}");

            var resolved = PropertyMapper.Map(propCode, param, min, max, _data, 99);
            output.Properties.Add(new CubePropertyExport
            {
                Index = i,
                ModChance = modChance,
                PropertyCode = propCode,
                Priority = resolved.Priority,
                Min = min,
                Max = max,
                Parameter = param,
                Lines = resolved.Lines
            });
        }

        return output;
    }

    private string? ResolveName(string? code, D2RReimaginedTools.Models.CubeMain? entry = null)
        => ResolveNameLine(code, entry).Name;

    /// <summary>
    /// Resolves a cube ingredient/output token to both the legacy English display
    /// name (for heuristic matching in <see cref="ProcessNotes"/> and for the
    /// retired English JSON exporter) and a <see cref="KeyedLine"/> for the keyed
    /// wire-format bundle.
    /// </summary>
    private (string? Name, KeyedLine? Keyed) ResolveNameLine(string? code, D2RReimaginedTools.Models.CubeMain? entry = null)
    {
        if (string.IsNullOrEmpty(code)) return (null, null);

        if (string.Equals(code, "usetype", StringComparison.OrdinalIgnoreCase))
        {
            if (entry != null && InputsContainGemBag(entry))
                return ("Return Gem Bag, Update Gem Credits",
                    KeyedLine.Of(SyntheticStringRegistry.Keys.CubeOutputReturnGemBag));
            return ("Use Type of Input 1",
                KeyedLine.Of(SyntheticStringRegistry.Keys.CubeOutputUseTypeOfInput1));
        }
        if (string.Equals(code, "useitem", StringComparison.OrdinalIgnoreCase))
        {
            if (entry != null && InputsContainGemBag(entry))
                return ("Return Gem Bag, Update Gem Credits",
                    KeyedLine.Of(SyntheticStringRegistry.Keys.CubeOutputReturnGemBag));
            return ("Use Item from Input 1",
                KeyedLine.Of(SyntheticStringRegistry.Keys.CubeOutputUseItemFromInput1));
        }

        // Real items resolve to their NameStr (game translation key) when present;
        // when the source row has no `namestr` we fall back to the row's `code`
        // column, mirroring the website's lookup order. Without this fallback,
        // any base item missing a `namestr` would publish an empty translation
        // key and surface as a "??" name in the keyed bundle.
        if (_data.Armors.TryGetValue(code, out var armor))
        {
            var key = !string.IsNullOrEmpty(armor.NameStr) ? armor.NameStr : armor.Code;
            return (_data.Translations.GetValue(key), KeyedLine.Of(key));
        }
        if (_data.Weapons.TryGetValue(code, out var weapon))
        {
            var key = !string.IsNullOrEmpty(weapon.NameStr) ? weapon.NameStr : weapon.Code;
            return (_data.Translations.GetValue(key), KeyedLine.Of(key));
        }
        if (_data.MiscItems.TryGetValue(code, out var misc))
        {
            var key = !string.IsNullOrEmpty(misc.NameStr) ? misc.NameStr : misc.Code;
            return (_data.Translations.GetValue(key), KeyedLine.Of(key));
        }

        // Item-types fall through to their canonical Index (already used as a
        // language-agnostic identifier elsewhere via ResolveItemTypeIndex).
        if (_data.ItemTypes.TryGetValue(code, out var it))
            return (it.Name, KeyedLine.Of(it.Index));
        if (_data.ItemTypesByName.TryGetValue(code, out var itByName))
            return (itByName.Name, KeyedLine.Of(itByName.Index));

        return (null, null);
    }

    private bool InputsContainGemBag(D2RReimaginedTools.Models.CubeMain entry)
    {
        for (int i = 1; i <= 7; i++)
        {
            var input = ImportReflection.GetString(entry, $"Input{i}");
            if (string.IsNullOrEmpty(input)) continue;
            if (string.Equals(GetMainToken(input), "bag", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private void ProcessNotes(CubeRecipeExport export, D2RReimaginedTools.Models.CubeMain entry)
    {
        var added = new HashSet<string>(StringComparer.Ordinal);

        // Notes are emitted as synthetic translation keys (see SyntheticStringRegistry.Keys).
        // The website resolves them against strings/{lang}.json — no English literals leak
        // into the keyed bundle. New notes: add a key to SyntheticStringRegistry.Keys, seed it
        // in Config/synthetic-strings.json, and pass the key here.
        void AddNote(string key)
        {
            if (added.Add(key))
                export.Notes.Add(KeyedLine.Of(key));
        }

        // --- Heuristics from old project ---

        var outputs = new List<CubeOutputExport?>();
        if (export.Outputs.A != null) outputs.Add(export.Outputs.A);
        if (export.Outputs.B != null) outputs.Add(export.Outputs.B);
        if (export.Outputs.C != null) outputs.Add(export.Outputs.C);

        bool InputsContainAnyCodes(params string[] codes) =>
            export.Inputs.Any(i => codes.Any(c => string.Equals(GetMainToken(i.RawToken), c, StringComparison.OrdinalIgnoreCase)));

        bool InputsContainCodes(params string[] codes) =>
            codes.All(c => export.Inputs.Any(i => string.Equals(GetMainToken(i.RawToken), c, StringComparison.OrdinalIgnoreCase)));

        int InputQtyForMain(string code) =>
            export.Inputs.Where(i => string.Equals(GetMainToken(i.RawToken), code, StringComparison.OrdinalIgnoreCase)).Sum(i => i.Quantity);

        bool AnyOutputHasAnyQualifiers(params string[] friendlyAny) =>
            outputs.Any(o => o != null && o.Qualifiers.Any(q => friendlyAny.Any(fn => string.Equals(q, fn, StringComparison.OrdinalIgnoreCase))));

        bool AnyOutputHasQualifiers(params string[] friendlyNeeded) =>
            outputs.Any(o => o != null && friendlyNeeded.All(fn => o.Qualifiers.Any(q => string.Equals(q, fn, StringComparison.OrdinalIgnoreCase))));

        bool AnyOutputHasBoth(string friendlyA, params string[] friendlyBOr) =>
            outputs.Any(o => o != null && o.Qualifiers.Any(q => string.Equals(q, friendlyA, StringComparison.OrdinalIgnoreCase)) &&
                              o.Qualifiers.Any(q => friendlyBOr.Any(b => string.Equals(q, b, StringComparison.OrdinalIgnoreCase))));

        bool AnyOutputNameContains(string substr) =>
            outputs.Any(o => o != null && o.Name.Contains(substr, StringComparison.OrdinalIgnoreCase));

        bool AnyOutputPropertyHasCode(string code) =>
            outputs.Any(o => o != null && o.Properties.Any(p => string.Equals(p.PropertyCode, code, StringComparison.OrdinalIgnoreCase)));

        // Legacy fuzzy-match used to scan the now-removed English `PropertyString`
        // (e.g. for "upgrade" / "enchant"). The keyed wire only carries the
        // property `code`, so we substring-match that instead — no current code
        // contains "upgrade"/"enchant" so the affected note rules never fire,
        // matching the pre-refactor behaviour where those English sentences
        // were never produced for the wire-side property either.
        bool AnyOutputPropertyContains(string substr) =>
            outputs.Any(o => o != null && o.Properties.Any(p =>
                (p.PropertyCode ?? string.Empty).Contains(substr, StringComparison.OrdinalIgnoreCase)));

        bool OutputEqualsUseType() => export.Outputs.A?.Name == "Use Type of Input 1" || export.Outputs.A?.MainToken == "usetype";
        bool OutputEqualsUseItem() => export.Outputs.A?.Name == "Use Item from Input 1" || export.Outputs.A?.MainToken == "useitem";

        bool FirstOutputHasAnyPropertiesAndFirstNotCorrupted()
        {
            var first = export.Outputs.A ?? export.Outputs.B ?? export.Outputs.C;
            if (first == null || first.Properties.Count == 0) return false;
            return !string.Equals(first.Properties[0].PropertyCode, "corrupted", StringComparison.OrdinalIgnoreCase);
        }

        bool InputsQualifiersContain(string friendly) =>
            export.Inputs.Any(i => i.Qualifiers.Any(q => string.Equals(q, friendly, StringComparison.OrdinalIgnoreCase)));

        bool InputsNamesContainExact(params string[] names) =>
            export.Inputs.Any(i => names.Any(n => string.Equals(i.Name, n, StringComparison.OrdinalIgnoreCase)));

        bool InputsNamesContainSubstring(string substr) =>
            export.Inputs.Any(i => i.Name.Contains(substr, StringComparison.OrdinalIgnoreCase));

        bool InputsRawTokensContainSubstring(string substr) =>
            export.Inputs.Any(i => i.RawToken.Contains(substr, StringComparison.OrdinalIgnoreCase));

        // 1-5) Orbs
        if (export.NumInputs == 2 && InputsContainAnyCodes("ka3")) AddNote(K.CubeNoteOrbOfCorruption);
        if (export.NumInputs == 2 && InputsContainAnyCodes("ooc")) AddNote(K.CubeNoteOrbOfConversion);
        if (export.NumInputs == 2 && InputsContainAnyCodes("ooa")) AddNote(K.CubeNoteOrbOfAssemblage);
        if (export.NumInputs == 2 && InputsContainAnyCodes("ooi")) AddNote(K.CubeNoteOrbOfInfusion);
        if (export.NumInputs == 2 && InputsContainAnyCodes("oos")) AddNote(K.CubeNoteOrbOfSocketing);

        // 5.5) Socket Punch
        if (IsSocketPunchRecipe(entry)) AddNote(K.CubeNoteSocketPunch);

        // 6-8) More Orbs / Pliers
        if (export.NumInputs == 2 && InputsContainAnyCodes("ooe")) AddNote(K.CubeNoteOrbOfShadows);
        if (export.NumInputs == 2 && InputsContainAnyCodes("jwp", "rup")) AddNote(K.CubeNotePliers);

        // 9) Crafting
        if (OutputEqualsUseType() && AnyOutputHasAnyQualifiers("Magic Item", "Rare Item", "Crafted Item") && FirstOutputHasAnyPropertiesAndFirstNotCorrupted())
            AddNote(K.CubeNoteItemCrafting);

        if (AnyOutputNameContains("Quiver") || AnyOutputNameContains("Bolt Case") || AnyOutputNameContains("Barilzar's Mazed Band"))
            AddNote(K.CubeNoteItemCrafting);

        // 10) Sunder
        if (export.Param == 386 && InputsNamesContainExact("Cold Rupture", "Flame Rift", "Crack of the Heavens", "Rotting Fissure", "Bone Break", "Black Cleft"))
            AddNote(K.CubeNoteSunderItemCrafting);
        if (InputsNamesContainExact("Latent Cold Rupture", "Latent Flame Rift", "Latent Crack of the Heavens", "Latent Rotting Fissure", "Latent Bone Break", "Latent Black Cleft"))
            AddNote(K.CubeNoteSunderItemCrafting);

        // 11-12) Uber / Keys
        if (InputsContainAnyCodes("mpa", "blc", "dia")) AddNote(K.CubeNoteTristramUberSouls);
        if (InputsContainAnyCodes("pk1", "pk2", "pk3") && InputsContainCodes("ka3")) AddNote(K.CubeNoteKeyConversion);

        // 13) Rejuvenation
        if ((InputsContainCodes("hpot") && (AnyOutputNameContains("Rejuvenation Potion") || AnyOutputNameContains("Full Rejuvenation Potion"))) ||
            (InputQtyForMain("rvs") >= 3 && AnyOutputNameContains("Full Rejuvenation Potion")))
            AddNote(K.CubeNoteRejuvenationPotion);

        // 14) Ethereal / Repair
        if (InputsQualifiersContain("Not Ethereal") && OutputEqualsUseItem() && AnyOutputPropertyHasCode("ethereal"))
            AddNote(K.CubeNoteForceEthereal);
        if (InputsQualifiersContain("Not Ethereal") && AnyOutputHasQualifiers("Repair Item"))
            AddNote(K.CubeNoteRepairNonEthereal);
        if (InputsQualifiersContain("Ethereal") && AnyOutputHasQualifiers("Repair Item"))
            AddNote(K.CubeNoteRepairEthereal);

        // 15) Force White
        bool isOor = InputsContainCodes("oor");
        bool isR01R15 = InputsContainCodes("r01") && InputsContainCodes("r15");
        bool hasNormalOutput = AnyOutputHasQualifiers("Normal Item") || AnyOutputHasQualifiers("Normal Quality");
        if ((isR01R15 && OutputEqualsUseType() && hasNormalOutput) || isOor) AddNote(K.CubeNoteForceWhite);

        // 16) Reroll
        if (export.Param == 386)
        {
            var core = _config.CubeRecipeCoreItemCodesSet;
            var optionals = new HashSet<string>(new[] { "bag" }, StringComparer.OrdinalIgnoreCase);
            var mains = export.Inputs.Select(i => GetMainToken(i.RawToken) ?? "").ToArray();
            bool onlyAllowed = mains.Length > 0 && mains.All(m => core.Contains(m) || optionals.Contains(m));
            bool hasCore = mains.Any(m => core.Contains(m));
            if (onlyAllowed && hasCore) AddNote(K.CubeNoteRerollItem);
        }

        // 17) Reroll 3x
        if (export.NumInputs == 3)
        {
            var allowed = _config.CubeRecipeAllowedCodesSet;
            var counts = allowed.ToDictionary(a => a, a => InputQtyForMain(a), StringComparer.OrdinalIgnoreCase);
            var present = counts.Where(kv => kv.Value > 0).Select(kv => kv.Key).ToList();
            var mains = export.Inputs.Select(i => GetMainToken(i.RawToken) ?? "").ToArray();
            if (present.Count == 1 && counts[present[0]] == 3 && mains.All(m => allowed.Contains(m)))
                AddNote(K.CubeNoteRerollItem);
        }

        // 18) Jewel Upgrade
        if (export.Inputs.Count == 1)
        {
            var main = GetMainToken(export.Inputs[0].RawToken);
            if (string.Equals(main, "jew", StringComparison.OrdinalIgnoreCase) && export.Inputs[0].Quantity > 0)
                AddNote(K.CubeNoteJewelUpgrade);
        }

        // 19) Recycle / Splash
        if (AnyOutputNameContains("Gem Cluster") && AnyOutputNameContains("Tome of Identify") && InputsNamesContainExact("Tome of Identify"))
            AddNote(K.CubeNoteRecycle);

        if (export.Param == 386 && (InputsNamesContainExact("Splash Charm") || InputsNamesContainSubstring("Splash Charm") || InputsRawTokensContainSubstring("Splash Charm")))
            AddNote(K.CubeNoteSplashCharmUpgrade);

        // 20) Uber Charm
        if ((InputsNamesContainExact("Hellfire Torch") || InputsNamesContainExact("Annihilus")) && AnyOutputPropertyContains("upgrade"))
            AddNote(K.CubeNoteUberCharmUpgrade);
        if (AnyOutputNameContains("Black Soulstone") || AnyOutputNameContains("Obsidian Beacon"))
            AddNote(K.CubeNoteUberCharmUpgrade);

        // 22) Base Tier
        if (AnyOutputHasBoth("Keep Modifiers", "Exceptional Item", "Elite Item"))
            AddNote(K.CubeNoteBaseTierUpgrade);

        // 24-27) Portal / Replenish
        if (InputsContainCodes("misl") && InputsContainCodes("hpot")) AddNote(K.CubeNoteReplenishQuiver);
        if (InputsContainCodes("leg")) AddNote(K.CubeNoteCowPortalRecipe);
        if (InputsContainCodes("pk1", "pk2", "pk3")) AddNote(K.CubeNoteRandomMiniUberPortal);
        if (InputsContainCodes("dhn", "bey", "mbr")) AddNote(K.CubeNoteTristramUberPortal);

        // 28) Enchantment
        if (OutputEqualsUseItem() && (AnyOutputPropertyContains("upgrade") || AnyOutputPropertyContains("enchant")))
            AddNote(K.CubeNoteItemEnchantment);

        // 29) Recycle 2
        if (export.NumInputs >= 4 && InputsContainAnyCodes("ibk", "tpk") && (AnyOutputNameContains("Jewel") || AnyOutputNameContains("Grand Charm")))
            AddNote(K.CubeNoteRecycle);

        // 30) Uber Ancient
        var uberAncientCodes = new[] { "xa1", "xa2", "xa3", "xa4", "xa5", "ua1", "ua2", "ua3", "ua4", "ua5" };
        if (InputsContainAnyCodes(uberAncientCodes) && outputs.Any(o => o != null && uberAncientCodes.Any(c => string.Equals(GetMainToken(o.MainToken), c, StringComparison.OrdinalIgnoreCase))))
            AddNote(K.CubeNoteRerollUberAncient);

        // Custom Portals
        if (export.Outputs.A?.MainToken == "cx7") AddNote(K.CubeNoteCowPortal);
        if (export.Outputs.A?.MainToken == "pk1") AddNote(K.CubeNotePandemoniumPortal);
        if (export.Outputs.A?.MainToken == "pk3") AddNote(K.CubeNotePandemoniumFinalePortal);
        if (export.Outputs.A?.MainToken == "her") AddNote(K.CubeNoteRedPortal);

        // Operations
        if (export.Op == 1) AddNote(K.CubeNoteTransmuteItem);
        if (export.Op == 18) AddNote(K.CubeNoteSocketItem);

        // Configuration-based notes. Each entry in `cubeRecipeNotes[].notes` is a synthetic
        // translation key (see SyntheticStringRegistry.Keys / Config/synthetic-strings.json).
        foreach (var noteRule in _config.CubeRecipeNotes)
        {
            bool matches = true;

            if (!string.IsNullOrEmpty(noteRule.Description) &&
                !(entry.Description?.Contains(noteRule.Description, StringComparison.OrdinalIgnoreCase) ?? false))
            {
                matches = false;
            }

            if (noteRule.Op.HasValue && export.Op != noteRule.Op.Value) matches = false;
            if (noteRule.Param.HasValue && export.Param != noteRule.Param.Value) matches = false;
            if (noteRule.Value.HasValue && export.Value != noteRule.Value.Value) matches = false;

            if (matches)
            {
                foreach (var noteKey in noteRule.Notes)
                {
                    AddNote(noteKey);
                }
            }
        }
    }

    private bool IsSocketPunchRecipe(D2RReimaginedTools.Models.CubeMain entry)
    {
        if (GetMainToken(entry.Output) != "useitem") return false;

        for (int i = 1; i <= 5; i++)
        {
            if (ImportReflection.GetString(entry, $"Mod{i}") == "sock") return true;
        }
        return false;
    }

    private string? GetMainToken(string? rawToken)
    {
        if (string.IsNullOrEmpty(rawToken)) return null;
        var parts = rawToken.Split(',').Select(p => p.Trim().Trim('"')).Where(p => p.Length > 0).ToList();
        return parts.Count > 0 ? parts[0] : null;
    }

    // Cube grabber-sentinel + socket-punch token lookups are config-driven
    // (export-config.json -> cubeGrabberSentinels / cubeSocketPunch).

    private bool IsEarlyStopSentinel(string? input1, string? output)
    {
        if (string.IsNullOrWhiteSpace(input1) || string.IsNullOrWhiteSpace(output)) return false;
        var inToken = GetMainToken(input1);
        var outToken = GetMainToken(output);
        var sentinels = _config.CubeGrabberSentinelsSet;
        return inToken != null && outToken != null && sentinels.Contains(inToken) && sentinels.Contains(outToken);
    }

    /// <summary>
    /// Post-sentinel allow-list for "skip-but-continue" mode. Matches the
    /// socket-punch family of recipes that lives after the grabber sentinel
    /// in CubeMain.txt: Input1 ∈ {helm, tors, shld, weap}, Input2 starts
    /// with `jew` and carries a quality discriminator (mag|rar|uni),
    /// Output is `useitem`, and Mod1 is `sock`. The note tagging is handled
    /// downstream by <see cref="IsSocketPunchRecipe"/>.
    /// </summary>
    private bool IsPostSentinelSocketPunchRecipe(D2RReimaginedTools.Models.CubeMain entry)
    {
        var in1Token = GetMainToken(entry.Input1);
        if (in1Token == null || !_config.CubeSocketPunch.EquipTokensSet.Contains(in1Token)) return false;

        var in2Parts = (entry.Input2 ?? "")
            .Split(',')
            .Select(p => p.Trim().Trim('"'))
            .Where(p => p.Length > 0)
            .ToList();
        if (in2Parts.Count == 0 || !string.Equals(in2Parts[0], "jew", StringComparison.OrdinalIgnoreCase)) return false;
        if (!in2Parts.Any(p => _config.CubeSocketPunch.JewelQualitiesSet.Contains(p))) return false;

        if (!string.Equals(GetMainToken(entry.Output), "useitem", StringComparison.OrdinalIgnoreCase)) return false;

        var mod1 = ImportReflection.GetString(entry, "Mod1");
        if (!string.Equals(mod1, "sock", StringComparison.OrdinalIgnoreCase)) return false;

        return true;
    }

    private bool IsSkipped(D2RReimaginedTools.Models.CubeMain entry)
    {
        var op = int.TryParse(entry.Operation, out var opVal) ? opVal : (int?)null;
        var param = int.TryParse(entry.Param, out var paramVal) ? paramVal : (int?)null;
        var value = int.TryParse(entry.Value, out var valVal) ? valVal : (int?)null;

        foreach (var rule in _config.SkippedCubeRecipes)
        {
            if (rule.Op.HasValue && op != rule.Op.Value) continue;
            if (rule.Param.HasValue && param != rule.Param.Value) continue;
            if (rule.Value.HasValue && value != rule.Value.Value) continue;
            if (rule.ValueMin.HasValue && (!value.HasValue || value.Value < rule.ValueMin.Value)) continue;
            if (rule.ValueMax.HasValue && (!value.HasValue || value.Value > rule.ValueMax.Value)) continue;

            // All specified rule criteria matched
            return true;
        }
        return false;
    }

    private bool HasBlockedInputs(D2RReimaginedTools.Models.CubeMain entry)
    {
        for (int i = 1; i <= 7; i++)
        {
            var input = ImportReflection.GetString(entry, $"Input{i}");
            if (string.IsNullOrEmpty(input)) continue;

            var parts = input.Split(',').Select(p => p.Trim().Trim('"')).Where(p => p.Length > 0);
            foreach (var p in parts)
            {
                if (p.StartsWith("qty=")) continue;
                if (_config.BlockedCubeInputCodesSet.Contains(p)) return true;
            }
        }
        return false;
    }

}
