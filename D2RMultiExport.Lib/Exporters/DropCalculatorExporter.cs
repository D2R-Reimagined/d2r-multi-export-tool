// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using System.Text.Json.Serialization;
using D2RMultiExport.Lib.Import;
using D2RMultiExport.Lib.Models;
using D2RReimaginedTools.Models;
using D2RReimaginedTools.TextFileParsers;

namespace D2RMultiExport.Lib.Exporters;

/// <summary>Exports the drop graph and its inputs, preserving localization keys and source weights.</summary>
public static class DropCalculatorExporter
{
    public const string OutputPath = "keyed/drop-calculator.json";

    private static readonly string[] InputFiles =
    [
        "treasureclassex.txt", "itemratio.txt", "uniqueitems.txt", "setitems.txt", "itemtypes.txt",
        "armor.txt", "weapons.txt", "misc.txt", "monstats.txt", "superuniques.txt", "levels.txt"
    ];

    /// <summary>The three difficulties in <c>levels.txt</c> / <c>monstats.txt</c> column order.</summary>
    private const int Difficulties = 3;

    /// <summary>
    /// Monster spawn kinds in export order, with the <c>monstats.txt</c> treasure-class
    /// column each reads. The level bonus per kind is content and lives in
    /// <c>export-config.json</c> → <c>dropMonsterLevelBonuses</c>.
    /// </summary>
    private static readonly string[] MonsterKinds = ["normal", "champion", "unique", "quest"];

    private const string SuperUniqueKind = "superunique";

    public static async Task ExportAsync(ExportOutputs outputs, SourceRoots inputs, GameData data, bool prettyPrint, Action<string> warn)
    {
        if (!ExcelInputs.RequireAll(inputs, OutputPath, warn, InputFiles)) return;

        var config = data.ExportConfig;
        var levelStep = config.DropGeneratedTreasureClassLevelStep;
        if (levelStep < 1)
            throw new InvalidDataException(
                $"export-config.json dropGeneratedTreasureClassLevelStep must be a positive integer (found {levelStep}).");

        var classes = await DropTreasureClassParser.GetEntries(inputs.Excel("treasureclassex.txt"));
        var ratios = await DropItemRatioParser.GetEntries(inputs.Excel("itemratio.txt"));
        var uniques = await DropNamedItemParser.GetEntries(inputs.Excel("uniqueitems.txt"));
        var sets = await DropNamedItemParser.GetEntries(inputs.Excel("setitems.txt"));
        var types = ExcelInputs.IndexFirst(await ItemTypeParser.GetEntries(inputs.Excel("itemtypes.txt")),
            static t => t.Code, "itemtypes.txt", "Code", warn);
        var equipment = (await ArmorParser.GetEntries(inputs.Excel("armor.txt"))).Cast<Equipment>()
            .Concat(await WeaponParser.GetEntries(inputs.Excel("weapons.txt"))).ToList();
        var equipmentSet = new HashSet<Equipment>(equipment, ReferenceEqualityComparer.Instance);
        var misc = await MiscParser.GetEntries(inputs.Excel("misc.txt"));
        var bases = equipment.Concat(misc).Where(b => !string.IsNullOrEmpty(b.Code)).ToList();
        var monsters = await MonStatsParser.GetEntries(inputs.Excel("monstats.txt"));
        var supers = await SuperUniquesParser.GetEntries(inputs.Excel("superuniques.txt"));
        var levels = await DropLevelParser.GetEntries(inputs.Excel("levels.txt"));

        bool HasType(string? code, string target, HashSet<string>? visited = null)
        {
            if (string.IsNullOrEmpty(code)) return false;
            if (code.Equals(target, StringComparison.OrdinalIgnoreCase)) return true;
            visited ??= new(StringComparer.OrdinalIgnoreCase);
            return visited.Add(code) && types.TryGetValue(code, out var type)
                && (HasType(type.Equiv1, target, visited) || HasType(type.Equiv2, target, visited));
        }

        // Resolved alongside the raw text (which stays the contract): the difficulty indexes a
        // difficulty-only condition holds on, or ConditionRuntime = true when it reads live
        // game state. Both null/absent for an empty condition.
        (int[]? Difficulties, bool? Runtime) ResolveCondition(string? condition)
        {
            if (string.IsNullOrWhiteSpace(condition)) return (null, null);
            return DropConditionValidator.TryResolveDifficulties(condition, data, out var difficulties)
                ? (difficulties, false)
                : (null, true);
        }

        void CheckCondition(string file, string row, string? condition)
        {
            if (!DropConditionValidator.TryValidate(condition, data, out var reason))
                warn($"{file} row '{row}': condition '{condition}' is not a valid BBE expression — {reason}. "
                    + "It is exported unchanged, but the website cannot evaluate it.");
        }

        var seenClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var treasureClasses = new List<DropClass>();
        foreach (var t in classes.Where(t => !string.IsNullOrWhiteSpace(t.TreasureClass)))
        {
            if (!seenClasses.Add(t.TreasureClass!))
                warn($"treasureclassex.txt: duplicate Treasure Class '{t.TreasureClass}'; every row is exported in file order.");
            CheckCondition("treasureclassex.txt", t.TreasureClass!, t.ConditionCalc);
            var entries = Enumerable.Range(1, TreasureClassSlots)
                .Select(i => new DropEntry(TreasureClassItem(t, i) ?? "", TreasureClassProbability(t, i)))
                .Where(e => e.Code.Length > 0 && e.Weight > 0)
                .ToList();
            var (tcDifficulties, tcRuntime) = ResolveCondition(t.ConditionCalc);
            treasureClasses.Add(new DropClass(t.TreasureClass!, t.Group ?? "", t.Level, t.Picks, t.Unique, t.Set, t.NoDrop,
                entries, t.ConditionCalc ?? "", t.QuestFlag ?? "", t.QuestFlagEx ?? "", tcDifficulties, tcRuntime));
        }

        // The game generates these classes from itemtypes and equipment quality levels.
        foreach (var type in types.Values.Where(t => t.TreasureClass == "1"))
        {
            var members = equipment.Where(b => b.Spawnable && (b.Quest ?? 0) == 0
                && (HasType(b.Type, type.Code!) || HasType(b.Type2, type.Code!))).ToList();
            var maximum = members.Select(b => b.Level ?? 0).DefaultIfEmpty().Max();
            for (var level = levelStep; level <= Math.Ceiling(maximum / (double)levelStep) * levelStep; level += levelStep)
            {
                var entries = members.Where(b => b.Level > level - levelStep && b.Level <= level)
                    .Select(b => new DropEntry(b.Code!, types.TryGetValue(b.Type ?? "", out var itemType) ? itemType.Rarity : 1)).ToList();
                treasureClasses.Add(new DropClass(type.Code + level, "", level, 1, 0, 0, 0, entries, "", "", "", null, null));
            }
        }

        var items = new List<DropItem>();
        var directNames = treasureClasses.SelectMany(t => t.Entries).Select(e => e.Code.Split(',')[0])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        void AddNamed(IEnumerable<DropNamedItem> rows, string quality, string file)
        {
            foreach (var row in rows.Where(r => !r.Disabled && !string.IsNullOrWhiteSpace(r.Index)
                         && (r.Spawnable || directNames.Contains(r.Index))))
            {
                var code = quality == "unique" ? row.Code : row.Item;
                if (string.IsNullOrWhiteSpace(code))
                {
                    warn($"{file} row '{row.Index}': no base item code; it cannot drop and is omitted.");
                    continue;
                }
                CheckCondition(file, row.Index!, row.DropConditionCalc);
                var (itemDifficulties, itemRuntime) = ResolveCondition(row.DropConditionCalc);
                items.Add(new DropItem(quality + ":" + row.Index, row.Index!, code, quality, row.Lvl, row.Rarity, row.Spawnable,
                    row.DropConditionCalc ?? "", itemDifficulties, itemRuntime));
            }
        }
        AddNamed(uniques, "unique", "uniqueitems.txt");
        AddNamed(sets, "set", "setitems.txt");
        items.AddRange(misc.Where(b => config.DropRuneTypes.Any(t => HasType(b.Type, t)))
            .Select(b => new DropItem("rune:" + b.Code, b.NameStr ?? b.Code!, b.Code!, "rune", b.Level ?? 0, 1, true, "", null, null)));
        items.AddRange(misc.Where(b => !string.IsNullOrEmpty(b.Code) && config.DropMiscCodesSet.Contains(b.Code))
            .Select(b => new DropItem("misc:" + b.Code, b.NameStr ?? b.Code!, b.Code!, "misc", b.Level ?? 0, 1, true, "", null, null)));

        int Bonus(string kind) => config.DropMonsterLevelBonuses.GetValueOrDefault(kind);

        var sources = new List<DropSource>();
        foreach (var monster in monsters.Where(m => m.Enabled == true && m.Killable == true && !string.IsNullOrEmpty(m.NameStr)))
        {
            for (var difficulty = 0; difficulty < Difficulties; difficulty++)
            {
                var areas = levels.Where(l => SpawnsIn(l, difficulty, monster.Id)).ToList();
                var fixedSuperOnly = areas.Count == 0 && supers.Any(s =>
                    string.Equals(s.Class, monster.Id, StringComparison.OrdinalIgnoreCase)
                    && config.DropSuperUniqueAreas.ContainsKey(s.Superunique ?? ""));
                if (config.DropMonsterAreas.TryGetValue(monster.Id ?? "", out var fixedAreaIds))
                    areas = levels.Where(l => fixedAreaIds.Contains(l.Id)).ToList();
                if (areas.Count == 0) areas.Add(new DropLevel()); // Fixed/script-spawned monsters have no level-table membership.
                foreach (var area in areas)
                {
                    if (fixedSuperOnly) continue;
                    var baseLevel = MonsterLevel(monster, difficulty) ?? 1;
                    if (difficulty > 0 && monster.Boss != true && monster.NoRatio != true)
                        baseLevel = AreaLevel(area, difficulty) is > 0 and var areaLevel ? areaLevel : baseLevel;
                    foreach (var kind in MonsterKinds)
                    {
                        if (monster.Boss == true && kind is "champion" or "unique") continue;
                        var tc = MonsterTreasureClass(monster, kind, difficulty);
                        if (string.IsNullOrEmpty(tc)) continue;
                        sources.Add(new DropSource(monster.Id + ":" + area.Id + ":" + difficulty + ":" + kind,
                            monster.NameStr!, area.LevelName ?? "", difficulty, monster.Boss == true && kind == "normal" ? "boss" : kind,
                            baseLevel + Bonus(kind), tc));
                    }
                }
                foreach (var super in supers.Where(s => string.Equals(s.Class, monster.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    var tc = SuperUniqueTreasureClass(super, difficulty);
                    if (string.IsNullOrEmpty(tc) || string.IsNullOrEmpty(super.Name)) continue;
                    var superAreas = config.DropSuperUniqueAreas.TryGetValue(super.Superunique ?? "", out var areaIds)
                        ? levels.Where(l => areaIds.Contains(l.Id)).ToList()
                        : string.Equals(super.Name, monster.NameStr, StringComparison.OrdinalIgnoreCase) ? areas : [];
                    // Never assign a fixed superunique every area occupied by its generic base class.
                    foreach (var area in superAreas)
                    {
                        var level = MonsterLevel(monster, difficulty) ?? 1;
                        var areaLevel = AreaLevel(area, difficulty);
                        if (difficulty > 0 && monster.Boss != true && monster.NoRatio != true && areaLevel > 0) level = areaLevel;
                        sources.Add(new DropSource("super:" + super.Superunique + ":" + area.Id + ":" + difficulty,
                            super.Name, area.LevelName ?? "", difficulty, SuperUniqueKind,
                            level + (monster.Boss == true ? 0 : Bonus(SuperUniqueKind)), tc));
                    }
                }
            }
        }

        var bundle = new
        {
            Items = items,
            Bases = bases.Select(b => new { b.Code, NameKey = b.NameStr ?? b.Code, Equipment = equipmentSet.Contains(b), Level = b.Level ?? 0, Uber = !string.IsNullOrEmpty(b.NormCode) && b.Code != b.NormCode,
                ClassSpecific = types.TryGetValue(b.Type ?? "", out var type) && !string.IsNullOrEmpty(type.Class), Quest = (b.Quest ?? 0) != 0 }),
            Ratios = ratios.Where(r => r.Version == config.DropItemRatioVersion),
            TreasureClasses = treasureClasses,
            Sources = sources.DistinctBy(s => new { s.NameKey, s.AreaKey, s.Difficulty, s.Kind, s.Level, s.TreasureClass })
        };
        await outputs.WriteJsonAsync(OutputPath, bundle, new JsonSerializerOptions { WriteIndented = prettyPrint });
    }

    // ── Typed accessors for the numbered .txt columns (structure, not content) ──

    /// <summary>Number of <c>Item</c>/<c>Prob</c> column pairs on a treasureclassex.txt row.</summary>
    private const int TreasureClassSlots = 10;

    private static string? TreasureClassItem(DropTreasureClass t, int index) => index switch
    {
        1 => t.Item1, 2 => t.Item2, 3 => t.Item3, 4 => t.Item4, 5 => t.Item5,
        6 => t.Item6, 7 => t.Item7, 8 => t.Item8, 9 => t.Item9, 10 => t.Item10,
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };

    private static int TreasureClassProbability(DropTreasureClass t, int index) => index switch
    {
        1 => t.Prob1, 2 => t.Prob2, 3 => t.Prob3, 4 => t.Prob4, 5 => t.Prob5,
        6 => t.Prob6, 7 => t.Prob7, 8 => t.Prob8, 9 => t.Prob9, 10 => t.Prob10,
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };

    /// <summary>levels.txt <c>mon1..25</c> (normal), <c>nmon1..25</c> (nightmare/hell) and <c>umon1..25</c>.</summary>
    private static readonly Func<DropLevel, string?>[] NormalSpawns =
    [
        l => l.Mon1, l => l.Mon2, l => l.Mon3, l => l.Mon4, l => l.Mon5, l => l.Mon6, l => l.Mon7, l => l.Mon8,
        l => l.Mon9, l => l.Mon10, l => l.Mon11, l => l.Mon12, l => l.Mon13, l => l.Mon14, l => l.Mon15, l => l.Mon16,
        l => l.Mon17, l => l.Mon18, l => l.Mon19, l => l.Mon20, l => l.Mon21, l => l.Mon22, l => l.Mon23, l => l.Mon24,
        l => l.Mon25
    ];

    private static readonly Func<DropLevel, string?>[] NightmareSpawns =
    [
        l => l.Nmon1, l => l.Nmon2, l => l.Nmon3, l => l.Nmon4, l => l.Nmon5, l => l.Nmon6, l => l.Nmon7, l => l.Nmon8,
        l => l.Nmon9, l => l.Nmon10, l => l.Nmon11, l => l.Nmon12, l => l.Nmon13, l => l.Nmon14, l => l.Nmon15, l => l.Nmon16,
        l => l.Nmon17, l => l.Nmon18, l => l.Nmon19, l => l.Nmon20, l => l.Nmon21, l => l.Nmon22, l => l.Nmon23, l => l.Nmon24,
        l => l.Nmon25
    ];

    private static readonly Func<DropLevel, string?>[] UniqueSpawns =
    [
        l => l.Umon1, l => l.Umon2, l => l.Umon3, l => l.Umon4, l => l.Umon5, l => l.Umon6, l => l.Umon7, l => l.Umon8,
        l => l.Umon9, l => l.Umon10, l => l.Umon11, l => l.Umon12, l => l.Umon13, l => l.Umon14, l => l.Umon15, l => l.Umon16,
        l => l.Umon17, l => l.Umon18, l => l.Umon19, l => l.Umon20, l => l.Umon21, l => l.Umon22, l => l.Umon23, l => l.Umon24,
        l => l.Umon25
    ];

    /// <summary>
    /// True when <paramref name="monsterId"/> is in the area's spawn pool for the difficulty:
    /// <c>mon*</c> in normal, <c>nmon*</c> in nightmare and hell, and <c>umon*</c> in all three.
    /// </summary>
    internal static bool SpawnsIn(DropLevel level, int difficulty, string? monsterId)
    {
        var pool = difficulty == 0 ? NormalSpawns : NightmareSpawns;
        for (var i = 0; i < pool.Length; i++)
        {
            if (string.Equals(pool[i](level), monsterId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(UniqueSpawns[i](level), monsterId, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    internal static int AreaLevel(DropLevel level, int difficulty) => difficulty switch
    {
        0 => level.MonLvlEx, 1 => level.MonLvlExN, 2 => level.MonLvlExH,
        _ => throw new ArgumentOutOfRangeException(nameof(difficulty))
    };

    private static int? MonsterLevel(MonStat monster, int difficulty) => difficulty switch
    {
        0 => monster.Level, 1 => monster.LevelN, 2 => monster.LevelH,
        _ => throw new ArgumentOutOfRangeException(nameof(difficulty))
    };

    private static string? MonsterTreasureClass(MonStat monster, string kind, int difficulty) => (kind, difficulty) switch
    {
        ("normal", 0) => monster.TreasureClass,
        ("normal", 1) => monster.TreasureClassN,
        ("normal", 2) => monster.TreasureClassH,
        ("champion", 0) => monster.TreasureClassChamp,
        ("champion", 1) => monster.TreasureClassChampN,
        ("champion", 2) => monster.TreasureClassChampH,
        ("unique", 0) => monster.TreasureClassUnique,
        ("unique", 1) => monster.TreasureClassUniqueN,
        ("unique", 2) => monster.TreasureClassUniqueH,
        ("quest", 0) => monster.TreasureClassQuest,
        ("quest", 1) => monster.TreasureClassQuestN,
        ("quest", 2) => monster.TreasureClassQuestH,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), $"{kind}/{difficulty}")
    };

    private static string? SuperUniqueTreasureClass(SuperUnique super, int difficulty) => difficulty switch
    {
        0 => super.TC, 1 => super.TCN, 2 => super.TCH,
        _ => throw new ArgumentOutOfRangeException(nameof(difficulty))
    };

    private sealed record DropEntry(string Code, int Weight);
    private sealed record DropClass(string Code, string Group, int Level, int Picks, int Unique, int Set, int NoDrop, List<DropEntry> Entries, string Condition, string QuestFlag, string QuestFlagEx,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int[]? ConditionDifficulties,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? ConditionRuntime);
    private sealed record DropItem(string Id, string NameKey, string Code, string Quality, int Level, int Rarity, bool Random, string Condition,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int[]? ConditionDifficulties,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? ConditionRuntime);
    private sealed record DropSource(string Id, string NameKey, string AreaKey, int Difficulty, string Kind, int Level, string TreasureClass);
}
