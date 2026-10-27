// SPDX-License-Identifier: GPL-3.0-or-later
using D2RMultiExport.Lib;
using D2RMultiExport.Lib.Config;
using D2RMultiExport.Lib.ErrorHandling;
using D2RMultiExport.Lib.Import;
using D2RMultiExport.Lib.Models;
using D2RMultiExport.Lib.Translation;

namespace D2RMultiExport.Tests;

/// <summary>
/// Duplicate-key, unresolved-property and cube-id-collision warnings name the conflicting
/// source rows and positions, while the imported data and recipe ids stay unchanged.
/// </summary>
public sealed class DiagnosticMessageTests
{
    private static GameData NewData()
    {
        var config = new ExportConfig();
        config.BuildLookups();
        return new GameData { ExportConfig = config, StatOverrideConfig = new StatOverrideConfig(), Translations = new TranslationService() };
    }

    private static async Task<T> WithExcelAsync<T>(Func<string, string, Task<T>> run)
    {
        var root = Directory.CreateTempSubdirectory("d2r-diagnostics-").FullName;
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

    private static async Task<(GameData Data, List<ImportError> Warnings)> LoadAsync(string fileName, params string[] lines)
    {
        return await WithExcelAsync(async (root, excel) =>
        {
            await File.WriteAllLinesAsync(Path.Combine(excel, fileName), lines);
            var data = NewData();
            data.Translations.MergeSeed(new Dictionary<string, string> { ["SerpentLord"] = "Serpent Lord" });
            var pipeline = new PipelineResult();
            await new DataLoader(SourceRoots.Create(root, null), data, pipeline).LoadAllAsync();
            return (data, pipeline.AllErrors
                .Where(static e => e.Severity == ImportErrorSeverity.Warning && e.Category == "DataLoader")
                .ToList());
        });
    }

    [Fact]
    public async Task DuplicateSkillNamesNameBothRowsWhileEachKeepsItsEngineId()
    {
        var (data, warnings) = await LoadAsync("skills.txt",
            "skill\t*Id\tcharclass", "Fire Bolt\t0\tsor", "Fire Bolt\t1\tsor");

        var warning = Assert.Single(warnings);
        Assert.Equal("skills.txt", warning.ItemIdentifier);
        Assert.Equal(
            "Duplicate skill 'Fire Bolt':"
            + "\n    Original:    skill='Fire Bolt', parsed row 1"
            + "\n    Replacement: skill='Fire Bolt', parsed row 2"
            + "\n    Action: the replacement overwrote the original entry in Skills.",
            warning.Message);
        // The name lookup keeps the later row; both rows keep their own engine id.
        Assert.Same(data.SkillsById[1], data.Skills["Fire Bolt"]);
        Assert.Equal([0, 1], data.SkillsById.Keys.Order());
    }

    [Fact]
    public async Task InformationalSkillIdDriftIsSummarizedOnceWithoutReportingBlankCells()
    {
        // The blank line is skipped by the parser but still numbered by the game.
        var (data, warnings) = await LoadAsync("skills.txt",
            "skill\t*Id\tcharclass", "Attack\t0\t", "", "azm_globe\t\t", "azm_bad\tabc\t",
            "Fire Bolt\t36\tsor", "Fire Bolt Copy\t36\tsor");

        var warning = Assert.Single(warnings);
        Assert.Equal("skills.txt", warning.ItemIdentifier);
        Assert.Equal(
            "2 row(s) carry an informational *Id that differs from the engine row index "
            + "(skill 'Fire Bolt': *Id='36', engine index 4; skill 'Fire Bolt Copy': *Id='36', engine index 5). "
            + "*Id is ignored; identifiers come from the row position.",
            warning.Message);
        Assert.Equal(
            [(0, "Attack"), (2, "azm_globe"), (3, "azm_bad"), (4, "Fire Bolt"), (5, "Fire Bolt Copy")],
            data.SkillsById.OrderBy(static pair => pair.Key).Select(static pair => (pair.Key, pair.Value.Skill)));
    }

    [Fact]
    public async Task InformationalMonsterIndexDriftListsAtMostThreeRows()
    {
        var (data, warnings) = await LoadAsync("monstats.txt",
            "Id\t*hcIdx\tNameStr",
            "skeleton1\t0\tSkeleton",
            "Expansion\t\t",
            "serpentlord\t5\tSerpentLord",
            "rmap_event_wave\t5\tRMapEventWave",
            "unnumbered\t\tUnnumbered",
            "late_a\t9\tLateA",
            "late_b\t9\tLateB");

        var warning = Assert.Single(warnings);
        Assert.Equal("monstats.txt", warning.ItemIdentifier);
        Assert.Equal(
            "4 row(s) carry an informational *hcIdx that differs from the engine row index "
            + "(Id 'serpentlord': *hcIdx='5', engine index 1; Id 'rmap_event_wave': *hcIdx='5', engine index 2; "
            + "Id 'late_a': *hcIdx='9', engine index 4; …). *hcIdx is ignored; identifiers come from the row position.",
            warning.Message);
        Assert.Equal(
            [(0, "skeleton1"), (1, "serpentlord"), (2, "rmap_event_wave"), (3, "unnumbered"), (4, "late_a"), (5, "late_b")],
            data.MonStatsByIndex.OrderBy(static pair => pair.Key).Select(static pair => (pair.Key, pair.Value.Id)));
    }

    [Fact]
    public async Task AnUnrenderableUniquePropertyNamesItsSlotRowAndRawCells()
    {
        var header = string.Join('\t', new[] { "index", "spawnable", "lvl", "lvl req", "code" }
            .Concat(Enumerable.Range(1, 7).SelectMany(static i => new[] { $"prop{i}", $"par{i}", $"min{i}", $"max{i}" })));
        string Row(string name, int slot, string par, string min, string max)
        {
            var cells = new string[5 + 7 * 4];
            Array.Fill(cells, "");
            (cells[0], cells[1], cells[2], cells[3], cells[4]) = (name, "1", "10", "8", "9st");
            var at = 5 + (slot - 1) * 4;
            (cells[at], cells[at + 1], cells[at + 2], cells[at + 3]) = ("value", par, min, max);
            return string.Join('\t', cells);
        }

        var (uniques, data) = await WithExcelAsync(async (root, excel) =>
        {
            await File.WriteAllLinesAsync(Path.Combine(excel, "uniqueitems.txt"),
            [
                header,
                Row("Blank Rolls", 7, "", "", ""),
                Row("Zero Rolls", 1, "", "0", "0"),
                Row("Param Only", 2, "25", "", "")
            ]);
            var data = NewData();
            data.TemplateLookup = static key => key == "valueKey" ? "%+d to Value" : null;
            data.Properties["value"] = new PropertyEntry { Code = "value", Stat1 = "valueStat", Function1 = 1 };
            data.Stats["valueStat"] = new StatEntry { Stat = "valueStat", DescriptionFunction = 19, DescStrPosKey = "valueKey", OpParam = 3 };
            data.Weapons["9st"] = new EquipmentEntry { Code = "9st", NameStr = "9st", EquipmentType = EquipmentType.Weapon, Durability = 250 };
            return (await new UniqueImporter(SourceRoots.Create(root, null), data).ImportAsync(), data);
        });

        var omissions = uniques.Errors.Where(static e => e.Message.Contains("Property line omitted")).ToList();
        Assert.Equal(2, omissions.Count);
        Assert.All(omissions, static e => Assert.Equal(ImportErrorSeverity.Warning, e.Severity));
        Assert.Equal("Blank Rolls", omissions[0].ItemIdentifier);
        Assert.Equal(
            "Property line omitted for prop7 'value':"
            + "\n    Source:   uniqueitems.txt parsed row 1; par7=<blank>, min7=<blank>, max7=<blank>"
            + "\n    Template: 'valueKey' (\"%+d to Value\") needs a value, but min7 and max7 are blank or non-numeric."
            + "\n    Action: the line was omitted; no value was invented.",
            omissions[0].Message);
        Assert.Equal("Param Only", omissions[1].ItemIdentifier);
        Assert.Contains("Source:   uniqueitems.txt parsed row 3; par2='25', min2=<blank>, max2=<blank>", omissions[1].Message);
        Assert.Contains("but min2 and max2 are blank or non-numeric", omissions[1].Message);
        Assert.DoesNotContain(uniques.Errors, static e => e.ItemIdentifier == "Zero Rolls");
        Assert.Empty(data.OmittedPropertyLines);

        // A legitimate zero still renders; the omitted rows keep their item.
        Assert.Equal(3, uniques.Items.Count);
        var zero = Assert.Single(uniques.Items, static u => u.Index == "Zero Rolls");
        var line = Assert.Single(zero.Properties.SelectMany(static p => p.Lines), static l => l.Key == "valueKey");
        Assert.Equal(new object[] { 0 }, line.Args);
        Assert.DoesNotContain(uniques.Items.Single(static u => u.Index == "Blank Rolls").Properties.SelectMany(static p => p.Lines),
            static l => l.Key == "valueKey");
    }

    /// <summary>The published recipe id algorithm, restated independently of the importer.</summary>
    private static string ExpectedRecipeId(params string[] cells)
    {
        var hash = 0xcbf29ce484222325UL;
        unchecked
        {
            foreach (var b in System.Text.Encoding.UTF8.GetBytes(string.Join('\u001F', cells)))
            {
                hash ^= b;
                hash *= 0x100000001b3UL;
            }
        }
        return "cr-" + hash.ToString("x16");
    }

    [Fact]
    public async Task CubeIdCollisionsListEveryRecipeWithItsIdAndKeepIdsStable()
    {
        var header = await File.ReadAllLinesAsync(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "PropertyRanges", "data", "global", "excel", "cubemain.txt"));
        var columns = header[0].Split('\t');
        string Row(string description, string output, params string[] inputs)
        {
            var cells = new string[columns.Length];
            Array.Fill(cells, "");
            cells[Array.IndexOf(columns, "description")] = description;
            cells[Array.IndexOf(columns, "enabled")] = "1";
            cells[Array.IndexOf(columns, "numinputs")] = inputs.Length.ToString();
            for (var i = 0; i < inputs.Length; i++) cells[Array.IndexOf(columns, $"input {i + 1}")] = inputs[i];
            cells[Array.IndexOf(columns, "output")] = output;
            return string.Join('\t', cells);
        }

        var cube = await WithExcelAsync(async (root, excel) =>
        {
            await File.WriteAllLinesAsync(Path.Combine(excel, "cubemain.txt"),
            [
                header[0],
                Row("Pair A", "r03", "r01", "r02"),
                Row("Pair B", "r04", "r01", "r02"),
                Row("Triple 1", "r12", "r10", "r11"),
                Row("Triple 2", "r13", "r10", "r11"),
                Row("Unique", "r21", "r20"),
                Row("", "r14", "r10", "r11")
            ]);
            return await new CubeRecipeImporter(SourceRoots.Create(root, null), NewData()) { EarlyStopSentinelEnabled = false }.ImportAsync();
        });

        var pair = ExpectedRecipeId("", "", "", "", "r01", "r02", "", "", "", "", "");
        var triple = ExpectedRecipeId("", "", "", "", "r10", "r11", "", "", "", "", "");
        Assert.Equal(
            [(1, pair), (2, pair + "-2"), (3, triple), (4, triple + "-2"), (5, ExpectedRecipeId("", "", "", "", "r20", "", "", "", "", "", "")), (6, triple + "-3")],
            cube.Items.Select(static r => (r.Index, r.Id)));

        var collisions = cube.Errors.Where(static e => e.Message.Contains("share this base ID")).ToList();
        Assert.Equal(2, collisions.Count);
        Assert.Equal([pair, triple], collisions.Select(static e => e.ItemIdentifier));
        Assert.Equal(
            "2 accepted recipes share this base ID; their identity cells (class, op, param, value, input 1-7) are identical."
            + $"\n    Index 1 -> {pair} (unsuffixed): description \"Pair A\"; inputs r01 + r02; output r03"
            + $"\n    Index 2 -> {pair}-2: description \"Pair B\"; inputs r01 + r02; output r04"
            + "\n    Action: all 2 recipes were exported; the first accepted row keeps the base ID and the second received -2.",
            collisions[0].Message);
        Assert.DoesNotContain("through -2", collisions[0].Message);
        Assert.Contains("3 accepted recipes share this base ID", collisions[1].Message);
        Assert.Contains($"Index 3 -> {triple} (unsuffixed): description \"Triple 1\"", collisions[1].Message);
        Assert.Contains($"Index 4 -> {triple}-2: description \"Triple 2\"", collisions[1].Message);
        Assert.Contains($"Index 6 -> {triple}-3: description <blank>; inputs r10 + r11; output r14", collisions[1].Message);
        Assert.Contains("later rows received -2 through -3 in that order", collisions[1].Message);
    }

    [Fact]
    public void ReportEntriesKeepAMultiLineWarningTogether()
    {
        var result = new PipelineResult();
        result.AddWarning("DataLoader", "skills.txt", "Duplicate skill 'a':\n    Original:    a\n    Replacement: b");
        result.AddWarning("DataLoader", "monstats.txt", "single line");

        var entries = result.GenerateReportEntries();

        Assert.Equal(
        [
            "=== Import Report: 0 error(s), 2 warning(s) ===",
            "",
            "--- DataLoader ---",
            "  [WARN] [DataLoader] skills.txt: Duplicate skill 'a':\n    Original:    a\n    Replacement: b",
            "  [WARN] [DataLoader] monstats.txt: single line"
        ], entries);
        Assert.Equal(string.Join("\n", entries), result.GenerateReport());
        Assert.Equal(["No errors."], new PipelineResult().GenerateReportEntries());
    }
}
