// SPDX-License-Identifier: GPL-3.0-or-later
using D2RMultiExport.Lib;
using D2RMultiExport.Lib.Config;
using D2RMultiExport.Lib.ErrorHandling;
using D2RMultiExport.Lib.Import;
using D2RMultiExport.Lib.Models;
using D2RMultiExport.Lib.Translation;
using D2RReimaginedTools.TextFileParsers;

namespace D2RMultiExport.Tests;

/// <summary>
/// Raw item property slots are checked against the properties.txt function that reads their
/// param/min/max cells, before any mapping. Malformed slots are import errors naming the file,
/// item, slot, code and values; the item is still exported and nothing is corrected.
/// </summary>
public sealed class PropertySourceValidatorTests
{
    private static GameData NewData()
    {
        var config = new ExportConfig();
        config.BuildLookups();
        var data = new GameData { ExportConfig = config, StatOverrideConfig = new StatOverrideConfig(), Translations = new TranslationService() };
        // Definitions as written in the mod's properties.txt (func#/stat#).
        Define(data, "extra-cold", (1, "passive_cold_mastery"));
        Define(data, "res-pois", (1, "poisonresist"));
        Define(data, "dmg-ac", (1, "item_damagetargetac"));
        Define(data, "ease", (1, "item_req_percent"));
        Define(data, "nofreeze", (1, "item_cannotbefrozen"));
        Define(data, "half-freeze", (1, "item_halffreezeduration"));
        Define(data, "cold-len", (1, "coldlength"));
        Define(data, "res-all", (1, "fireresist"), (3, "lightresist"), (3, "coldresist"), (3, "poisonresist"));
        Define(data, "ac%", (2, "item_armor_percent"));
        Define(data, "dmg-fire", (15, "firemindam"), (16, "firemaxdam"));
        Define(data, "gethit-skill", (11, "item_skillongethit"));
        Define(data, "hit-skill", (11, "item_skillonhit"));
        Define(data, "splash", (11, "item_splashonhit"));
        Define(data, "charged", (19, "item_charged_skill"));
        Define(data, "skill-rand", (12, "item_singleskill"));
        Define(data, "sock", (14, "item_numsockets"));
        Define(data, "ac/lvl", (17, "item_armor_perlevel"));
        Define(data, "indestruct", (20, ""));
        // itemstatcost.txt Save Bits as written in the mod: 1-bit flags vs numeric stats.
        Stat(data, "item_cannotbefrozen", 1);
        Stat(data, "item_halffreezeduration", 1);
        Stat(data, "coldlength", 8);
        Stat(data, "passive_cold_mastery", 9);
        return data;
    }

    private static void Stat(GameData data, string stat, int saveBits) =>
        data.Stats[stat] = new StatEntry { Stat = stat, SaveBits = saveBits };

    private static void Define(GameData data, string code, params (int Function, string Stat)[] slots) =>
        data.Properties[code] = new PropertyEntry
        {
            Code = code,
            Stat1 = slots[0].Stat,
            Function1 = slots[0].Function,
            Slots = [.. slots.Select(static s => new PropertySlot { Function = s.Function, Stat = s.Stat })]
        };

    private static List<string> Validate(GameData data, string code, string? param, int? min, int? max) =>
        PropertySourceValidator.Validate("uniqueitems.txt", 7,
            new SourcePropertySlot("prop3", "par3", "min3", "max3", code, param, min, max), data);

    [Fact]
    public void GhostlyChainmailReversedRangeIsReportedWithFileSlotCodeAndValues()
    {
        var data = NewData();

        var message = Assert.Single(PropertySourceValidator.Validate("uniqueitems.txt", 901,
            new SourcePropertySlot("prop6", "par6", "min6", "max6", "extra-cold", null, 20, 10), data));

        Assert.Equal(
            "Invalid source property prop6 'extra-cold' in uniqueitems.txt (parsed row 901):"
            + "\n    Values:  par6=<blank>, min6='20', max6='10'"
            + "\n    Problem: min6 20 is greater than max6 10 (reversed range); properties.txt func 1 reads min6/max6 as the value range."
            + "\n    Action:  the property is exported unchanged; no value was corrected, filled in or dropped.",
            message);
        // The corrected 20-30 range is valid.
        Assert.Empty(PropertySourceValidator.Validate("uniqueitems.txt", 901,
            new SourcePropertySlot("prop6", "par6", "min6", "max6", "extra-cold", null, 20, 30), data));
    }

    [Fact]
    public void GloomformBlankTriggerChanceIsReportedButAnExplicitZeroIsTheEngineDefault()
    {
        var data = NewData();

        var message = Assert.Single(Validate(data, "gethit-skill", "Dim Vision", null, 2));
        Assert.Contains("Values:  par3='Dim Vision', min3=<blank>, max3='2'", message);
        Assert.Contains("min3 (trigger chance %) is blank", message);
        Assert.Contains("only an explicit 0 selects the engine's 5% default", message);

        // Fixed row (Min=10) and an explicit 0 (documented 5% default) are both valid.
        Assert.Empty(Validate(data, "gethit-skill", "Dim Vision", 10, 2));
        Assert.Empty(Validate(data, "gethit-skill", "Dim Vision", 0, 2));
    }

    [Theory]
    [InlineData("hit-skill", "Life Tap", 10, 18)]      // chance to cast, level
    [InlineData("hit-skill", "38", 10, 0)]             // numeric skill id; level 0 derives from item level
    [InlineData("gethit-skill", "Nova", 5, -3)]        // negative level derives from item level
    [InlineData("splash", "proc_SplashDamage", 100, 1)] // custom splash plumbing (chance 100, level 1)
    [InlineData("charged", "Teleport", 5, 10)]         // charges, level
    [InlineData("charged", "Teleport", 0, 10)]         // 0 charges selects the engine's 5
    [InlineData("charged", "395", -60, -10)]           // vanilla magicsuffix "of Lethargy": ilvl-scaled charges/level
    [InlineData("res-pois", null, -30, -10)]
    [InlineData("res-pois", null, -10, -30)]           // negative ranges are written by magnitude
    [InlineData("dmg-ac", null, -25, -40)]             // vanilla magicsuffix "of Bashing"
    [InlineData("ease", null, -5, -20)]
    [InlineData("res-all", null, -20, -30)]
    [InlineData("extra-cold", null, 0, 0)]             // explicit zeros are values, not blanks
    [InlineData("extra-cold", null, 15, 15)]
    [InlineData("ac%", null, null, 50)]                // func 2 always applies max
    [InlineData("dmg-fire", null, 10, 20)]
    [InlineData("skill-rand", "3", 6, 35)]             // min/max bound the random skill id
    [InlineData("sock", "3", null, null)]              // socket count from the parameter
    [InlineData("sock", null, 1, 6)]
    [InlineData("ac/lvl", "8", null, null)]            // func 17 reads the parameter
    [InlineData("indestruct", null, null, null)]       // func 20 reads no value
    [InlineData("nofreeze", null, 1, 1)]               // a flag may still carry an explicit value
    [InlineData("cold-len", null, 300, 300)]           // standalone cold duration needs no cold-min/cold-max
    [InlineData("cold-len", null, 250, 300)]
    public void LegitimateSpecialAndNegativePropertiesAreNotReported(string code, string? param, int? min, int? max)
    {
        Assert.Empty(Validate(NewData(), code, param, min, max));
    }

    [Theory]
    [InlineData("setitems.txt", "aprop2a", "apar2a", "amin2a", "amax2a", "nofreeze")]  // The River Stix
    [InlineData("setitems.txt", "aprop3a", "apar3a", "amin3a", "amax3a", "nofreeze")]  // Skeleton Warrior's Corpse Plate
    [InlineData("sets.txt", "FCode6", "FParam6", "FMin6", "FMax6", "half-freeze")]      // Insight of Brother Laz
    public void ValuelessFlagPropertiesAreNotReported(string file, string codeColumn, string paramColumn, string minColumn, string maxColumn, string code)
    {
        // properties.txt func 1 sets a 1-bit itemstatcost flag, so the property is defined by its presence.
        Assert.Empty(PropertySourceValidator.Validate(file, null,
            new SourcePropertySlot(codeColumn, paramColumn, minColumn, maxColumn, code, null, null, null), NewData()));
    }

    [Fact]
    public void BlankValuesAreStillReportedForNumericStatsAndPartlyFilledFlags()
    {
        var data = NewData();

        Assert.Contains("min3 and max3 are blank, so the property has no value", Assert.Single(Validate(data, "extra-cold", null, null, null)));
        Assert.Contains("max3 is blank while min3 is 1", Assert.Single(Validate(data, "nofreeze", null, 1, null)));
        // Without an itemstatcost.txt definition the stat is not known to be a flag.
        data.Stats.Remove("item_cannotbefrozen");
        Assert.Contains("min3 and max3 are blank", Assert.Single(Validate(data, "nofreeze", null, null, null)));
    }

    [Theory]
    [InlineData("uniqueitems.txt", "prop6", "par6", "min6", "max6")]       // Hailstrike
    [InlineData("setitems.txt", "aprop2a", "apar2a", "amin2a", "amax2a")]  // Death Knight's Demon Blade
    public void ColdDurationWrittenOnlyInTheParameterIsReported(string file, string codeColumn, string paramColumn, string minColumn, string maxColumn)
    {
        var message = Assert.Single(PropertySourceValidator.Validate(file, null,
            new SourcePropertySlot(codeColumn, paramColumn, minColumn, maxColumn, "cold-len", "300", null, null), NewData()));

        Assert.Contains($"Values:  {paramColumn}='300', {minColumn}=<blank>, {maxColumn}=<blank>", message);
        Assert.Contains($"{minColumn} and {maxColumn} are blank, so the property has no value; "
                        + $"properties.txt func 1 reads {minColumn}/{maxColumn} as the value range ({paramColumn} is not read as the value)", message);
    }

    [Theory]
    [InlineData("extra-cold", null, null, 10, "min3 is blank while max3 is 10")]
    [InlineData("extra-cold", null, 6, null, "max3 is blank while min3 is 6")]
    [InlineData("extra-cold", null, null, null, "min3 and max3 are blank, so the property has no value")]
    [InlineData("cold-len", null, 300, null, "max3 is blank while min3 is 300")]
    [InlineData("extra-cold", "300", null, null, "(par3 is not read as the value)")]
    [InlineData("res-pois", null, 5, -5, "min3 5 is greater than max3 -5 (reversed range)")]
    [InlineData("ac%", null, 50, null, "max3 is blank while min3 is 50")]
    [InlineData("dmg-fire", null, 20, 10, "min3 20 is greater than max3 10 (reversed range)")]
    [InlineData("hit-skill", null, 10, 5, "par3 is blank; properties.txt func 11 reads it as the skill to cast")]
    [InlineData("hit-skill", "Nova", -5, 5, "min3 (trigger chance %) is negative (-5)")]
    [InlineData("hit-skill", "Nova", 10, null, "max3 (skill level) is blank")]
    [InlineData("charged", "Teleport", null, 10, "min3 (maximum charges) is blank")]
    [InlineData("charged", "Teleport", 5, null, "max3 (skill level) is blank")]
    [InlineData("skill-rand", "3", 35, 6, "min3 35 is greater than max3 6 (reversed range)")]
    [InlineData("sock", null, 6, 1, "min3 6 is greater than max3 1 (reversed range)")]
    [InlineData("sock", null, null, null, "par3, min3 and max3 are blank")]
    [InlineData("ac/lvl", null, null, null, "min3 and max3 are blank")]
    public void MalformedSlotsAreReported(string code, string? param, int? min, int? max, string problem)
    {
        var message = Assert.Single(Validate(NewData(), code, param, min, max));
        Assert.Contains(problem, message);
    }

    [Fact]
    public void IgnoredGroupedAndUndefinedCodesAreLeftToTheirOwnPaths()
    {
        var data = NewData();
        data.PropertyGroups["Gelid-Affix1"] = new PropertyGroupEntry { Code = "Gelid-Affix1" };

        Assert.Empty(Validate(data, "state", "1", null, null));        // export-config ignoredPropertyCodes
        Assert.Empty(Validate(data, "Gelid-Affix1", null, null, null));
        Assert.Empty(Validate(data, "*enr", null, -5, -5));             // not a properties.txt code
        Assert.Empty(Validate(data, "", null, 20, 10));
    }

    private static async Task<T> WithExcelAsync<T>(Func<string, string, Task<T>> run)
    {
        var root = Directory.CreateTempSubdirectory("d2r-source-properties-").FullName;
        try
        {
            var excel = Directory.CreateDirectory(Path.Combine(root, "global", "excel")).FullName;
            Directory.CreateDirectory(Path.Combine(root, "local", "lng", "strings"));
            return await run(root, excel);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<string[]> FixtureHeaderAsync(string file) =>
        (await File.ReadAllLinesAsync(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "PropertyRanges", "data", "global", "excel", file)))[0].Split('\t');

    private static string Row(string[] columns, params (string Column, string Value)[] cells)
    {
        var row = new string[columns.Length];
        Array.Fill(row, "");
        foreach (var (column, value) in cells)
        {
            var index = Array.IndexOf(columns, column);
            Assert.True(index >= 0, $"fixture header has no '{column}' column");
            row[index] = value;
        }
        return string.Join('\t', row);
    }

    private static List<ImportError> Findings<T>(ImportResult<T> result) =>
        [.. result.Errors.Where(static e => e.Category == PropertySourceValidator.Category)];

    [Fact]
    public async Task UniqueImporterReportsTheOriginalRowsAndStillExportsEveryItem()
    {
        var columns = await FixtureHeaderAsync("uniqueitems.txt");
        var uniques = await WithExcelAsync(async (root, excel) =>
        {
            await File.WriteAllLinesAsync(Path.Combine(excel, "uniqueitems.txt"),
            [
                string.Join('\t', columns),
                Row(columns, ("index", "Ghostly Chainmail"), ("spawnable", "1"), ("lvl", "40"), ("lvl req", "35"), ("code", "9st"),
                    ("prop6", "extra-cold"), ("min6", "20"), ("max6", "10")),
                Row(columns, ("index", "Gloomform"), ("spawnable", "1"), ("lvl", "40"), ("lvl req", "35"), ("code", "9st"),
                    ("prop3", "gethit-skill"), ("par3", "Dim Vision"), ("max3", "2")),
                Row(columns, ("index", "Valid Caster"), ("spawnable", "1"), ("lvl", "40"), ("lvl req", "35"), ("code", "9st"),
                    ("prop1", "hit-skill"), ("par1", "Nova"), ("min1", "0"), ("max1", "0"),
                    ("prop2", "charged"), ("par2", "Teleport"), ("min2", "-20"), ("max2", "-5"),
                    ("prop3", "res-pois"), ("min3", "-10"), ("max3", "-30"),
                    ("prop4", "cold-len"), ("min4", "300"), ("max4", "300"),
                    ("prop5", "nofreeze")),
                Row(columns, ("index", "Hailstrike"), ("spawnable", "1"), ("lvl", "40"), ("lvl req", "35"), ("code", "9st"),
                    ("prop6", "cold-len"), ("par6", "300"))
            ]);
            var data = NewData();
            data.Weapons["9st"] = new EquipmentEntry { Code = "9st", NameStr = "9st", EquipmentType = EquipmentType.Weapon, Durability = 250 };
            return await new UniqueImporter(SourceRoots.Create(root, null), data).ImportAsync();
        });

        Assert.Equal(["Ghostly Chainmail", "Gloomform", "Hailstrike", "Valid Caster"], uniques.Items.Select(static u => u.Index).Order());
        var findings = Findings(uniques);
        Assert.Equal(3, findings.Count);
        Assert.All(findings, static e => Assert.Equal(ImportErrorSeverity.Error, e.Severity));
        Assert.Equal(["Ghostly Chainmail", "Gloomform", "Hailstrike"], findings.Select(static e => e.ItemIdentifier));
        Assert.StartsWith("Invalid source property prop6 'extra-cold' in uniqueitems.txt (parsed row 1):", findings[0].Message);
        Assert.Contains("min6='20', max6='10'", findings[0].Message);
        Assert.StartsWith("Invalid source property prop3 'gethit-skill' in uniqueitems.txt (parsed row 2):", findings[1].Message);
        Assert.Contains("par3='Dim Vision', min3=<blank>, max3='2'", findings[1].Message);
        Assert.Equal(
            "[ERROR] [SourceProperties] Gloomform: Invalid source property prop3 'gethit-skill' in uniqueitems.txt (parsed row 2):",
            findings[1].ToString().Split('\n')[0]);
        Assert.StartsWith("Invalid source property prop6 'cold-len' in uniqueitems.txt (parsed row 4):", findings[2].Message);
        Assert.Contains("par6='300', min6=<blank>, max6=<blank>", findings[2].Message);

        // Nothing was corrected: the exported rows keep the source values.
        var ghostly = uniques.Items.Single(static u => u.Index == "Ghostly Chainmail").Properties.Single(static p => p.PropertyCode == "extra-cold");
        Assert.Equal((20, 10), (ghostly.Min, ghostly.Max));
        var gloom = uniques.Items.Single(static u => u.Index == "Gloomform").Properties.Single(static p => p.PropertyCode == "gethit-skill");
        Assert.Equal((null, 2), (gloom.Min, gloom.Max));
    }

    [Fact]
    public async Task SetItemSetBonusRunewordAndCubeSlotsAreReportedByTheirSourceColumns()
    {
        var setItemColumns = await FixtureHeaderAsync("setitems.txt");
        var setColumns = await FixtureHeaderAsync("sets.txt");
        var runeColumns = await FixtureHeaderAsync("runes.txt");
        var cubeColumns = await FixtureHeaderAsync("cubemain.txt");

        var (sets, runewords, cube) = await WithExcelAsync(async (root, excel) =>
        {
            await File.WriteAllLinesAsync(Path.Combine(excel, "setitems.txt"),
            [
                string.Join('\t', setItemColumns),
                Row(setItemColumns, ("index", "Test Helm"), ("set", "Test Set"), ("item", "9st"), ("lvl", "10"),
                    ("prop2", "extra-cold"), ("min2", "20"), ("max2", "10"),
                    ("aprop2a", "nofreeze"),
                    ("aprop3a", "cold-len"), ("apar3a", "300"))
            ]);
            await File.WriteAllLinesAsync(Path.Combine(excel, "sets.txt"),
            [
                string.Join('\t', setColumns),
                Row(setColumns, ("index", "Test Set"), ("name", "Test Set"),
                    ("PCode3b", "res-pois"), ("PMin3b", "-10"), ("PMax3b", "-30"),
                    ("FCode2", "hit-skill"), ("FParam2", "Nova"), ("FMax2", "4"),
                    ("FCode6", "half-freeze"))
            ]);
            await File.WriteAllLinesAsync(Path.Combine(excel, "runes.txt"),
            [
                string.Join('\t', runeColumns),
                Row(runeColumns, ("Name", "Runeword1"), ("complete", "1"), ("Rune1", "r01"),
                    ("T1Code1", "hit-skill"), ("T1Param1", "Nova"), ("T1Min1", "10"), ("T1Max1", "5"),
                    ("T1Code3", "charged"), ("T1Param3", "Teleport"), ("T1Max3", "5"))
            ]);
            await File.WriteAllLinesAsync(Path.Combine(excel, "cubemain.txt"),
            [
                string.Join('\t', cubeColumns),
                Row(cubeColumns, ("description", "Craft"), ("enabled", "1"), ("numinputs", "1"), ("input 1", "r01"),
                    ("output", "r02"), ("mod 1", "extra-cold"), ("mod 1 min", "5"), ("mod 1 max", "10"),
                    ("output b", "r03"), ("b mod 2", "sock"), ("b mod 2 min", "6"), ("b mod 2 max", "1"))
            ]);

            var data = NewData();
            data.Weapons["9st"] = new EquipmentEntry { Code = "9st", NameStr = "9st", EquipmentType = EquipmentType.Weapon, Durability = 250 };
            data.MiscItems["r01"] = new MiscEntry { Code = "r01", NameStr = "r01", Level = 11, LevelReq = 11 };
            var sources = SourceRoots.Create(root, null);
            foreach (var set in await SetsParser.GetEntries(sources.Excel("sets.txt")))
                data.SetEntries[set.Index!] = set;
            foreach (var item in await SetItemParser.GetEntries(sources.Excel("setitems.txt")))
                data.SetItemEntries[item.Index!] = item;
            return (await new SetImporter(sources, data).ImportAsync(),
                await new RunewordImporter(sources, data).ImportAsync(),
                await new CubeRecipeImporter(sources, data) { EarlyStopSentinelEnabled = false }.ImportAsync());
        });

        var setFindings = Findings(sets);
        Assert.Equal(3, setFindings.Count);
        Assert.Equal(["Test Helm", "Test Helm", "Test Set"], setFindings.Select(static e => e.ItemIdentifier));
        Assert.StartsWith("Invalid source property prop2 'extra-cold' in setitems.txt:", setFindings[0].Message);
        // The valueless nofreeze/half-freeze flags are not reported; the parameter-only cold-len is.
        Assert.StartsWith("Invalid source property aprop3a 'cold-len' in setitems.txt:", setFindings[1].Message);
        Assert.Contains("apar3a='300', amin3a=<blank>, amax3a=<blank>", setFindings[1].Message);
        Assert.StartsWith("Invalid source property FCode2 'hit-skill' in sets.txt:", setFindings[2].Message);
        Assert.Contains("FMin2 (trigger chance %) is blank", setFindings[2].Message);
        Assert.Single(Assert.Single(sets.Items).SetItems);

        var runeFinding = Assert.Single(Findings(runewords));
        Assert.Equal("Runeword1", runeFinding.ItemIdentifier);
        Assert.StartsWith("Invalid source property T1Code3 'charged' in runes.txt (parsed row 1):", runeFinding.Message);
        Assert.Contains("T1Min3 (maximum charges) is blank", runeFinding.Message);
        Assert.Single(runewords.Items);

        var cubeFinding = Assert.Single(Findings(cube));
        Assert.Equal("Craft", cubeFinding.ItemIdentifier);
        Assert.StartsWith("Invalid source property b mod 2 'sock' in cubemain.txt (parsed row 1):", cubeFinding.Message);
        Assert.Contains("b mod 2 param=<blank>, b mod 2 min='6', b mod 2 max='1'", cubeFinding.Message);
        Assert.Single(cube.Items);
    }

    [Fact]
    public async Task DataLoaderReportsGemAndAffixSlotsWhileKeepingTheRows()
    {
        var pipeline = new PipelineResult();
        var data = await WithExcelAsync(async (root, excel) =>
        {
            await File.WriteAllLinesAsync(Path.Combine(excel, "properties.txt"),
            [
                "code\tfunc1\tstat1\tfunc2\tstat2",
                "str\t1\tstrength\t\t",
                "charged\t19\titem_charged_skill\t\t",
                "dmg-fire\t15\tfiremindam\t16\tfiremaxdam"
            ]);
            const string affixHeader = "Name\tspawnable\tlevel\tmod1code\tmod1param\tmod1min\tmod1max\tmod2code\tmod2param\tmod2min\tmod2max";
            await File.WriteAllLinesAsync(Path.Combine(excel, "magicprefix.txt"), [affixHeader]);
            await File.WriteAllLinesAsync(Path.Combine(excel, "automagic.txt"), [affixHeader]);
            await File.WriteAllLinesAsync(Path.Combine(excel, "magicsuffix.txt"),
            [
                affixHeader,
                "of Lethargy\t1\t20\tcharged\t395\t-20\t-5\t\t\t\t",
                "of Strength\t1\t5\tstr\t\t4\t\tdmg-fire\t\t1\t2"
            ]);
            await File.WriteAllLinesAsync(Path.Combine(excel, "gems.txt"),
            [
                "name\tcode\tweaponMod1Code\tweaponMod1Param\tweaponMod1Min\tweaponMod1Max\thelmMod1Code\thelmMod1Param\thelmMod1Min\thelmMod1Max",
                "Ruby\tgcr\tdmg-fire\t\t20\t10\tstr\t\t3\t3"
            ]);
            var data = NewData();
            data.Properties.Clear();
            await new DataLoader(SourceRoots.Create(root, null), data, pipeline).LoadAllAsync();
            return data;
        });

        var findings = pipeline.AllErrors.Where(static e => e.Category == PropertySourceValidator.Category).ToList();
        Assert.Equal(["gcr", "of Strength"], findings.Select(static e => e.ItemIdentifier));
        Assert.All(findings, static e => Assert.Equal(ImportErrorSeverity.Error, e.Severity));
        Assert.StartsWith("Invalid source property weaponMod1Code 'dmg-fire' in gems.txt (parsed row 1):", findings[0].Message);
        Assert.Contains("weaponMod1Param=<blank>, weaponMod1Min='20', weaponMod1Max='10'", findings[0].Message);
        Assert.Equal("of Strength", findings[1].ItemIdentifier);
        Assert.StartsWith("Invalid source property mod1code 'str' in magicsuffix.txt (parsed row 2):", findings[1].Message);
        Assert.Contains("mod1max is blank while mod1min is 4", findings[1].Message);

        // Rows are kept with their raw values; the vanilla-style ilvl-scaled charges are valid.
        Assert.Equal(["of Lethargy", "of Strength"], data.MagicSuffixes.Select(static a => a.Name));
        Assert.Equal(4, data.MagicSuffixes[1].Properties[0].Min);
        Assert.Null(data.MagicSuffixes[1].Properties[0].Max);
        Assert.Equal((20, 10), (data.Gems["gcr"].WeaponProperties[0].Min, data.Gems["gcr"].WeaponProperties[0].Max));
    }
}
