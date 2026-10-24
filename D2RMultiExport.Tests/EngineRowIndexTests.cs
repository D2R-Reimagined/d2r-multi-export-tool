// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using D2RMultiExport.Lib;
using D2RMultiExport.Lib.Config;
using D2RMultiExport.Lib.ErrorHandling;
using D2RMultiExport.Lib.Exporters;
using D2RMultiExport.Lib.Import;
using D2RMultiExport.Lib.Models;
using D2RMultiExport.Lib.Translation;
using D2RMultiExport.TestSupport;

namespace D2RMultiExport.Tests;

/// <summary>
/// Numeric identifiers come from the game's record numbering — the row position after the
/// <c>Expansion</c> marker is dropped — and never from informational <c>*</c> columns. Every
/// expected id below is counted by hand from the row layout, not read from those columns.
/// </summary>
public sealed class EngineRowIndexTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("d2r-engine-rows-").FullName;

    private string Excel => Path.Combine(_root, "global", "excel");

    public EngineRowIndexTests()
    {
        Directory.CreateDirectory(Excel);
        Directory.CreateDirectory(Path.Combine(_root, "local", "lng", "strings"));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void WriteTable(string file, params string[] lines) =>
        File.WriteAllText(Path.Combine(Excel, file), string.Join("\r\n", lines) + "\r\n");

    private static GameData NewData()
    {
        var config = new ExportConfig();
        config.BuildLookups();
        return new GameData { ExportConfig = config, StatOverrideConfig = new StatOverrideConfig(), Translations = new TranslationService() };
    }

    private async Task<(GameData Data, PipelineResult Pipeline)> LoadAsync()
    {
        var data = NewData();
        var pipeline = new PipelineResult();
        await new DataLoader(SourceRoots.Create(_root, null), data, pipeline).LoadAllAsync();
        return (data, pipeline);
    }

    private static List<ImportError> DataLoaderWarnings(PipelineResult pipeline) =>
        [.. pipeline.AllErrors.Where(static e => e.Severity == ImportErrorSeverity.Warning && e.Category == "DataLoader")];

    [Fact]
    public void OnlyTheExactExpansionMarkerIsDroppedAndEverySectionRowConsumesAnIndex()
    {
        Assert.Equal(
            [0, null, 1, 2, 3, 4],
            EngineRowIndex.Derive(["Attack", "Expansion", "Rings", "", "expansion", "Kick"]));
    }

    [Fact]
    public void BlankLinesTheParserSkippedStillConsumeIndices()
    {
        Assert.Equal([0, 3, null, 4], EngineRowIndex.Derive(["a", "b", "Expansion", "c"], [0, 2, 0, 0]));
        Assert.Throws<ArgumentException>(() => EngineRowIndex.Derive(["a"], [0, 0]));
    }

    [Fact]
    public async Task AppendedSkillsWithBlankInformationalIdsKeepAttackAtZero()
    {
        WriteTable("skills.txt",
        [
            "skill\t*Id\tcharclass",
            "Attack\t0\t", "Kick\t1\t", "Throw\t2\t",
            .. Enumerable.Range(1, 10).Select(static i => $"azm_{i}\t\t")
        ]);

        var (data, pipeline) = await LoadAsync();

        Assert.Empty(DataLoaderWarnings(pipeline));
        Assert.Equal("Attack", data.SkillsById[0].Skill);
        Assert.Equal(0, data.Skills["Attack"].Id);
        Assert.Equal(13, data.SkillsById.Count);
        for (var i = 1; i <= 10; i++)
        {
            Assert.Equal(2 + i, data.Skills[$"azm_{i}"].Id);
            Assert.Same(data.Skills[$"azm_{i}"], data.SkillsById[2 + i]);
        }
        Assert.Same(data.Skills["Attack"], data.ResolveSkill("0"));
        Assert.Same(data.Skills["azm_10"], data.ResolveSkill("12"));
        Assert.Same(data.Skills["Kick"], data.ResolveSkill("Kick"));
    }

    [Fact]
    public async Task WrongOrDuplicatedInformationalSkillIdsDoNotMoveSkills()
    {
        WriteTable("skills.txt",
            "skill\t*Id\tcharclass",
            "Attack\t7\t",
            "Kick\t7\t",
            "\t\tsor",
            "Throw\t0\t");

        var (data, pipeline) = await LoadAsync();

        // The nameless row is not a skill, but it still occupies engine index 2.
        Assert.Equal(
            [(0, "Attack"), (1, "Kick"), (3, "Throw")],
            data.SkillsById.OrderBy(static pair => pair.Key).Select(static pair => (pair.Key, pair.Value.Skill)));
        var warning = Assert.Single(DataLoaderWarnings(pipeline));
        Assert.StartsWith("3 row(s) carry an informational *Id that differs from the engine row index", warning.Message);
    }

    [Fact]
    public async Task WrongOrDuplicatedInformationalMonsterIndicesDoNotMoveMonsters()
    {
        WriteTable("monstats.txt",
            "Id\t*hcIdx\tNameStr",
            "skeleton1\t0\tSkeleton",
            "zombie1\t7\tZombie",
            "Expansion\t\t",
            "ghoul1\t1\tGhoul",
            "wraith1\t1\tWraith");

        var (data, _) = await LoadAsync();

        Assert.Equal(
            [(0, "skeleton1"), (1, "zombie1"), (2, "ghoul1"), (3, "wraith1")],
            data.MonStatsByIndex.OrderBy(static pair => pair.Key).Select(static pair => (pair.Key, pair.Value.Id)));
        Assert.All(data.MonStatsByIndex, static pair => Assert.Equal(pair.Key, pair.Value.Index));
        // The dropped section header is neither indexed nor a monster.
        Assert.DoesNotContain("Expansion", data.MonStats.Keys);

        // A numeric reanimate parameter names the engine index, not the *hcIdx label.
        data.Properties["reanimate"] = new PropertyEntry { Code = "reanimate", Stat1 = "item_reanimate", Function1 = 1 };
        data.Stats["item_reanimate"] = new StatEntry { Stat = "item_reanimate", DescriptionFunction = 23, DescStrPosKey = "Moditemreanimas" };
        var line = Assert.Single(PropertyMapper.Map("reanimate", "1", 10, 10, data, 1).Lines);
        Assert.Equal(new object[] { 10, "Zombie" }, line.Args);
    }

    [Fact]
    public async Task MonsterIndicesCountBlankLinesAndSectionRowsButNotTheExpansionMarker()
    {
        // Physical lines: 1 header, 2 skeleton1, 3 zombie1, 4 blank, 5 a name-and-zero section
        // row, 6 Expansion, 7 wight, 8 lowercase expansion, 9 greenwidow. Above the Expansion
        // line the index is line - 2, below it line - 3 (wight: line 7 → 4, as line 800 → 797
        // in the mod).
        WriteTable("monstats.txt",
            "Id\t*hcIdx\tNameStr",
            "skeleton1\t0\tSkeleton",
            "zombie1\t1\tZombie",
            "",
            "Act 2\t\t0",
            "Expansion\t\t0",
            "wight\t2\tWight",
            "expansion\t\t",
            "greenwidow\t\tGreen Widow");

        var (data, pipeline) = await LoadAsync();

        Assert.Equal(
            [(0, "skeleton1"), (1, "zombie1"), (3, "Act 2"), (4, "wight"), (5, "expansion"), (6, "greenwidow")],
            data.MonStatsByIndex.OrderBy(static pair => pair.Key).Select(static pair => (pair.Key, pair.Value.Id)));
        Assert.All(data.MonStatsByIndex, static pair => Assert.Equal(pair.Key, pair.Value.Index));
        // Only the stale *hcIdx of wight is reported; one blank line keeps names and rows aligned.
        var warning = Assert.Single(DataLoaderWarnings(pipeline));
        Assert.StartsWith("1 row(s) carry an informational *hcIdx that differs from the engine row index (Id 'wight': *hcIdx='2', engine index 4)",
            warning.Message);

        data.Properties["reanimate"] = new PropertyEntry { Code = "reanimate", Stat1 = "item_reanimate", Function1 = 1 };
        data.Stats["item_reanimate"] = new StatEntry { Stat = "item_reanimate", DescriptionFunction = 23, DescStrPosKey = "Moditemreanimas" };
        Assert.Equal(new object[] { 10, "Wight" }, Assert.Single(PropertyMapper.Map("reanimate", "4", 10, 10, data, 1).Lines).Args);
        Assert.Equal(new object[] { 15, "Green Widow" }, Assert.Single(PropertyMapper.Map("reanimate", "6", 15, 15, data, 1).Lines).Args);
        Assert.Equal(new object[] { 15, "Green Widow" }, Assert.Single(PropertyMapper.Map("reanimate", "greenwidow", 15, 15, data, 1).Lines).Args);
    }

    [Fact]
    public void NameNumbersFollowDistinctNamesAndDivergeAfterARepeat()
    {
        // One blank line or blank key is just the first "" name; Expansion is not a record.
        Assert.Null(EngineRowIndex.FindNameNumberDivergence(["a", "b"], EngineRowIndex.Derive(["a", "b"], [0, 1])));
        Assert.Null(EngineRowIndex.FindNameNumberDivergence(["expansion", "Expansion", "b"], [0, null, 1]));
        // A second blank (line or key) or a case-insensitive repeat reuses the earlier number.
        Assert.Equal<(int, string, int)?>((2, "", 1), EngineRowIndex.FindNameNumberDivergence(["a", "b"], [0, 3]));
        Assert.Equal<(int, string, int)?>((2, "", 0), EngineRowIndex.FindNameNumberDivergence(["a", "", "b"], [1, 2, 3]));
        Assert.Equal<(int, string, int)?>((2, "zombie1", 1),
            EngineRowIndex.FindNameNumberDivergence(["a", "Zombie1", "zombie1", "c"], [0, 1, 2, 3]));
    }

    [Fact]
    public async Task RepeatedMonsterIdsKeepTheirRowIndicesAndAreReportedOnce()
    {
        WriteTable("monstats.txt",
            "Id\t*hcIdx\tNameStr",
            "skeleton1\t0\tSkeleton",
            "Zombie1\t1\tZombie",
            "zombie1\t2\tZombie",
            "wight\t3\tWight");

        var (data, pipeline) = await LoadAsync();

        Assert.Equal(
            [(0, "skeleton1"), (1, "Zombie1"), (2, "zombie1"), (3, "wight")],
            data.MonStatsByIndex.OrderBy(static pair => pair.Key).Select(static pair => (pair.Key, pair.Value.Id)));
        var warnings = DataLoaderWarnings(pipeline).Select(static warning => warning.Message).ToList();
        Assert.Equal(2, warnings.Count);
        Assert.Contains(warnings, static message => message.StartsWith("Duplicate Id 'zombie1':", StringComparison.Ordinal)
            && message.Contains("Id='Zombie1', engine index 1", StringComparison.Ordinal)
            && message.Contains("Id='zombie1', engine index 2", StringComparison.Ordinal));
        Assert.Contains(warnings, static message =>
            message.StartsWith("Id 'zombie1' at engine index 2 repeats the Id numbered 1 ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PropertyGroupIdsComeFromRowPosition()
    {
        WriteTable("propertygroups.txt",
            "code\t*Id\tPickMode\tProp1\tModMin1\tModMax1",
            "groupA\t5\t1\tstr\t1\t2",
            "\t\t\t\t\t",
            "groupB\t\t1\tdex\t1\t2");

        var (data, _) = await LoadAsync();

        Assert.Equal(0, data.PropertyGroups["groupA"].Id);
        Assert.Equal(2, data.PropertyGroups["groupB"].Id);
        Assert.Same(data.PropertyGroups["groupB"], data.PropertyGroupsById[2]);
    }

    [Fact]
    public async Task SkillDescriptionsPrerequisitesAndSkillReferencesUseTheEngineIds()
    {
        WriteTable("skills.txt",
            "skill\t*Id\tcharclass\tskilldesc\treqskill1\tmaxlvl",
            "Attack\t0\t\t\t\t",
            "azm_globe\t\t\t\t\t",
            "azm_bombard\t\t\t\t\t",
            "Fire Bolt\t\tsor\tfire bolt\t\t20",
            "Warmth\t3\tsor\twarmth\tFire Bolt\t20");
        WriteTable("skilldesc.txt",
            "skilldesc\tSkillPage\tSkillRow\tSkillColumn\tstr name\tdescline1\tdesctexta1\tdesccalca1",
            "fire bolt\t1\t1\t1\tskillname36\t75\tdamage\t5",
            "warmth\t1\t2\t1\tskillname37\t75\tdamage\t7");
        WriteTable("itemstatcost.txt", "Stat\tdescpriority", "strength\t1");

        var (data, _) = await LoadAsync();
        Assert.Equal(3, data.Skills["Fire Bolt"].Id);
        Assert.Equal(4, data.Skills["Warmth"].Id);

        var descriptions = await new SkillDescriptionImporter(data).ImportAsync();
        Assert.Empty(descriptions.Errors);
        Assert.Equal([3, 4], descriptions.Items.Select(static set => set.SkillId).Order());
        // The pipeline keys descriptions by id; colliding ids used to make this throw.
        data.SkillDescriptions = descriptions.Items.ToDictionary(static set => set.SkillId);

        var outputs = new ExportOutputs(Path.Combine(_root, "out"));
        await SkillTreeExporter.ExportAsync(outputs, data, new JsonSerializerOptions());
        using var tree = JsonDocument.Parse(await File.ReadAllTextAsync(outputs.GetFullPath(SkillTreeExporter.OutputPath)));
        var skills = tree.RootElement.EnumerateArray().Single()
            .GetProperty("Tabs")[0].GetProperty("Skills").EnumerateArray()
            .ToDictionary(static skill => skill.GetProperty("Code").GetString()!);
        Assert.Equal(3, skills["Fire Bolt"].GetProperty("Id").GetInt32());
        Assert.Equal(4, skills["Warmth"].GetProperty("Id").GetInt32());
        Assert.Equal([3], skills["Warmth"].GetProperty("PrerequisiteIds").EnumerateArray().Select(static id => id.GetInt32()));
        Assert.Equal(7, skills["Warmth"].GetProperty("Descriptions").GetProperty("Stats")[0]
            .GetProperty("values")[0][0].GetDouble());

        List<string> warnings = [];
        await ItemStatPresentationExporter.ExportAsync(outputs, SourceRoots.Create(_root, null), data, false, warnings.Add);
        using var presentation = JsonDocument.Parse(await File.ReadAllTextAsync(outputs.GetFullPath(ItemStatPresentationExporter.OutputPath)));
        var byId = presentation.RootElement.GetProperty("Skills").EnumerateObject()
            .ToDictionary(static pair => pair.Name, static pair => pair.Value.GetProperty("FallbackName").GetString());
        Assert.Equal(
            new Dictionary<string, string?> { ["0"] = "Attack", ["1"] = "azm_globe", ["2"] = "azm_bombard", ["3"] = "Fire Bolt", ["4"] = "Warmth" },
            byId);
    }

    private static string UniqueRow(string name, int level, string informationalId) =>
        $"{name}\t{informationalId}\t1\t{level}\t1\t9st";

    [Fact]
    public async Task UniqueSaveIdsCountSectionRowsAndBlankLinesButNotTheExpansionMarker()
    {
        WriteTable("uniqueitems.txt",
            "index\t*ID\tspawnable\tlvl\tlvl req\tcode",
            UniqueRow("The Gnasher", 7, "0"),
            UniqueRow("Deathspade", 9, "1"),
            "Expansion\t\t\t0\t\t",
            "Rings\t\t\t0\t\t",
            UniqueRow("Nagelring", 10, "2"),
            "",
            UniqueRow("Manald Heal", 20, "3"));
        var data = NewData();
        data.Weapons["9st"] = new EquipmentEntry { Code = "9st", NameStr = "9st", EquipmentType = EquipmentType.Weapon };

        var uniques = await new UniqueImporter(SourceRoots.Create(_root, null), data).ImportAsync();

        Assert.Equal(
            [("The Gnasher", 0), ("Deathspade", 1), ("Nagelring", 3), ("Manald Heal", 5)],
            uniques.Items.Select(static unique => (unique.Index, unique.FileIndex)));
    }

    [Fact]
    public async Task SetItemSaveIdsCountRowsAfterTheExpansionMarker()
    {
        WriteTable("setitems.txt",
            "index\t*ID\tset\titem\tspawnable\tlvl",
            "Civerb's Ward\t0\tCiverb's Vestments\t9st\t1\t13",
            "Expansion\t\t\t\t\t",
            "Hsarus' Iron Heel\t0\tHsarus' Defense\t9st\t1\t3",
            "Hsarus' Iron Fist\t\tHsarus' Defense\t9st\t1\t4");
        WriteTable("sets.txt", "index\tname", "Civerb's Vestments\tCiverb's Vestments", "Hsarus' Defense\tHsarus' Defense");
        var (data, _) = await LoadAsync();
        data.Weapons["9st"] = new EquipmentEntry { Code = "9st", NameStr = "9st", EquipmentType = EquipmentType.Weapon };

        var sets = await new SetImporter(SourceRoots.Create(_root, null), data).ImportAsync();

        Assert.Equal(
            [("Civerb's Ward", 0), ("Hsarus' Iron Fist", 2), ("Hsarus' Iron Heel", 1)],
            sets.Items.SelectMany(static set => set.SetItems).Select(static item => (item.Index, item.FileIndex)).OrderBy(static item => item.Index));
    }

    [Fact]
    public async Task RareNameSlotsKeepNamelessRowsAndDropOnlyTheExpansionMarker()
    {
        WriteTable("raresuffix.txt", "name", "bite", "", "Expansion", "scratch");
        WriteTable("rareprefix.txt", "name", "beast", "\t", "eagle");
        var outputs = new ExportOutputs(Path.Combine(_root, "out"));

        await RareNameExporter.ExportAsync(outputs, SourceRoots.Create(_root, null), false, static _ => { });

        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(outputs.GetFullPath(RareNameExporter.OutputPath)));
        Assert.Equal(
            [null, "bite", null, "scratch", "beast", null, "eagle"],
            json.RootElement.GetProperty("Names").EnumerateArray().Select(static name => name.GetString()));
    }

    /// <summary>
    /// Baseline rows whose engine ids are fixed by vanilla saves and the documented unique/set
    /// fixtures (docs/TESTING.md §3.3–3.4). Loads the real tables; no export is written.
    /// </summary>
    [RealDataFact]
    [Trait("Category", "RealData")]
    public async Task RealDataBaselineRowsKeepTheirEngineIndices()
    {
        var sources = RealDataInputs.RequireSources();
        var data = NewData();
        await new DataLoader(sources, data, new PipelineResult()).LoadAllAsync();

        foreach (var (skill, id) in new[] { ("Attack", 0), ("Kick", 1), ("Throw", 2), ("Fire Bolt", 36), ("Teleport", 54) })
            Assert.Equal(skill, data.SkillsById[id].Skill);
        Assert.All(data.SkillsById, static pair => Assert.Equal(pair.Key, pair.Value.Id));
        Assert.All(data.Skills.Values, skill => Assert.Same(skill, data.SkillsById[skill.Id]));
        foreach (var (monster, index) in new[] { ("skeleton1", 0), ("hephasto", 409), ("wakeofdestruction", 410) })
            Assert.Equal(monster, data.MonStatsByIndex[index].Id);
        // Mod rows below the Expansion line (line N is index N - 3): the uniques' numeric
        // reanimate parameters 767 (Tomb Reaver), 768 (Wisp Projector) and 769 (Arachnid's Bite).
        foreach (var (monster, index) in new[] { ("wight", 767), ("voltshade", 768), ("greenwidow", 769), ("labskelemagecold", 797) })
            Assert.Equal(monster, data.MonStatsByIndex[index].Id);
        Assert.Equal(data.MonStats["wight"].NameStr, Assert.Single(PropertyMapper.Map("reanimate", "767", 10, 10, data, 1).Lines).Args[^1]);

        var uniques = await new UniqueImporter(sources, data).ImportAsync();
        var uniqueIds = uniques.Items.ToDictionary(static unique => unique.Index, static unique => unique.FileIndex);
        foreach (var (unique, id) in new[] { ("Magefist", 105), ("The Spirit Shroud", 209), ("Skin of the Vipermagi", 210),
                     ("Sorcerer's Cache", 1015), ("Asheara's Slippers", 1102), ("Duskwreath", 1137) })
            Assert.Equal(id, uniqueIds[unique]);

        var sets = await new SetImporter(sources, data).ImportAsync();
        var setIds = sets.Items.SelectMany(static set => set.SetItems).ToDictionary(static item => item.Index, static item => item.FileIndex);
        foreach (var (item, id) in new[] { ("Angelic Halo", 52), ("Kingpin's Signet", 296), ("Draven Coil", 307), ("Holy Ring of Amaunator", 384) })
            Assert.Equal(id, setIds[item]);
    }

    /// <summary>
    /// Compiled game data as the authority: every shipped CASC <c>.bin</c> holds as many
    /// records as <see cref="EngineRowIndex"/> derives from its <c>.txt</c>, and
    /// <c>monstats.bin</c> stores each record's <c>Id</c> text (after a u16 name number)
    /// in the order the loader indexes <see cref="GameData.MonStatsByIndex"/>.
    /// </summary>
    [RealDataFact]
    [Trait("Category", "RealData")]
    public async Task ShippedCompiledTablesMatchTheEngineRowRule()
    {
        var sources = RealDataInputs.RequireSources();
        var monstatsBin = sources.ResolveIn(SourceOrigin.Casc, "global/excel/monstats.bin").Path
            ?? throw new InvalidOperationException("The CASC Data global/excel folder has no compiled monstats.bin to compare against.");
        var casc = Path.GetDirectoryName(monstatsBin)!;

        static int BinCount(string path)
        {
            using var stream = File.OpenRead(path);
            Span<byte> count = stackalloc byte[4];
            stream.ReadExactly(count);
            return BitConverter.ToInt32(count);
        }

        var compared = 0;
        foreach (var txt in Directory.GetFiles(casc, "*.txt"))
        {
            var bin = Path.ChangeExtension(txt, ".bin");
            if (!File.Exists(bin)) continue;
            List<string?> firstCells = [.. (await File.ReadAllLinesAsync(txt)).Skip(1)
                .Where(static line => !string.IsNullOrWhiteSpace(line)).Select(static line => line.Split('\t')[0])];
            var records = (await EngineRowIndex.DeriveAsync(txt, firstCells)).Count(static index => index is not null);
            Assert.True(records == BinCount(bin), $"{Path.GetFileName(txt)}: {records} derived records, {BinCount(bin)} in the .bin");
            compared++;
        }
        Assert.True(compared > 50, $"only {compared} CASC tables had a .bin");

        File.Copy(Path.Combine(casc, "monstats.txt"), Path.Combine(Excel, "monstats.txt"));
        var (data, _) = await LoadAsync();
        var bytes = await File.ReadAllBytesAsync(monstatsBin);
        var count = BitConverter.ToInt32(bytes, 0);
        var size = (bytes.Length - 4) / count;
        Assert.Equal(count, data.MonStatsByIndex.Count);
        for (var index = 0; index < count; index++)
        {
            var record = bytes.AsSpan(4 + index * size, size);
            Assert.Equal(index, BitConverter.ToUInt16(record));
            var name = record.Slice(2, 48);
            var length = name.IndexOf((byte)0);
            Assert.Equal(System.Text.Encoding.ASCII.GetString(name[..(length < 0 ? name.Length : length)]), data.MonStatsByIndex[index].Id);
        }
    }
}
