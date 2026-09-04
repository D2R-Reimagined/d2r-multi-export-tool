// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using D2RMultiExport.Lib.Config;
using D2RReimaginedTools.Models;
using D2RReimaginedTools.TextFileParsers;

namespace D2RMultiExport.Lib.Exporters;

/// <summary>Exports the drop graph and its inputs, preserving localization keys and source weights.</summary>
public static class DropCalculatorExporter
{
    public static async Task ExportAsync(string exportDir, string excelPath, ExportConfig config)
    {
        var classes = await DropTreasureClassParser.GetEntries(Path.Combine(excelPath, "treasureclassex.txt"));
        var ratios = await DropItemRatioParser.GetEntries(Path.Combine(excelPath, "itemratio.txt"));
        var uniques = await DropNamedItemParser.GetEntries(Path.Combine(excelPath, "uniqueitems.txt"));
        var sets = await DropNamedItemParser.GetEntries(Path.Combine(excelPath, "setitems.txt"));
        var types = (await ItemTypeParser.GetEntries(Path.Combine(excelPath, "itemtypes.txt")))
            .Where(t => !string.IsNullOrEmpty(t.Code)).ToDictionary(t => t.Code!, StringComparer.OrdinalIgnoreCase);
        var equipment = (await ArmorParser.GetEntries(Path.Combine(excelPath, "armor.txt"))).Cast<Equipment>()
            .Concat(await WeaponParser.GetEntries(Path.Combine(excelPath, "weapons.txt"))).ToList();
        var misc = await MiscParser.GetEntries(Path.Combine(excelPath, "misc.txt"));
        var bases = equipment.Concat(misc).Where(b => !string.IsNullOrEmpty(b.Code)).ToList();
        var monsters = await MonStatsParser.GetEntries(Path.Combine(excelPath, "monstats.txt"));
        var supers = await SuperUniquesParser.GetEntries(Path.Combine(excelPath, "superuniques.txt"));
        var levels = await DropLevelParser.GetEntries(Path.Combine(excelPath, "levels.txt"));

        bool HasType(string? code, string target, HashSet<string>? visited = null)
        {
            if (string.IsNullOrEmpty(code)) return false;
            if (code.Equals(target, StringComparison.OrdinalIgnoreCase)) return true;
            visited ??= new(StringComparer.OrdinalIgnoreCase);
            return visited.Add(code) && types.TryGetValue(code, out var type)
                && (HasType(type.Equiv1, target, visited) || HasType(type.Equiv2, target, visited));
        }

        var treasureClasses = classes.Where(t => !string.IsNullOrWhiteSpace(t.TreasureClass)).Select(t => new DropClass(
            t.TreasureClass!, t.Group ?? "", t.Level, t.Picks, t.Unique, t.Set, t.NoDrop,
            Enumerable.Range(1, 10).Select(i => new DropEntry(
                (string?)typeof(DropTreasureClass).GetProperty($"Item{i}")!.GetValue(t) ?? "",
                (int)typeof(DropTreasureClass).GetProperty($"Prob{i}")!.GetValue(t)!))
                .Where(e => e.Code.Length > 0 && e.Weight > 0).ToList(),
            t.ConditionCalc ?? "", t.QuestFlag ?? "", t.QuestFlagEx ?? "")).ToList();

        // The game generates these classes from itemtypes and equipment quality levels.
        foreach (var type in types.Values.Where(t => t.TreasureClass == "1"))
        {
            var members = equipment.Where(b => b.Spawnable && (b.Quest ?? 0) == 0
                && (HasType(b.Type, type.Code!) || HasType(b.Type2, type.Code!))).ToList();
            var maximum = members.Select(b => b.Level ?? 0).DefaultIfEmpty().Max();
            for (var level = 3; level <= Math.Ceiling(maximum / 3.0) * 3; level += 3)
            {
                var entries = members.Where(b => b.Level > level - 3 && b.Level <= level)
                    .Select(b => new DropEntry(b.Code!, types.TryGetValue(b.Type ?? "", out var itemType) ? itemType.Rarity : 1)).ToList();
                treasureClasses.Add(new DropClass(type.Code + level, "", level, 1, 0, 0, 0, entries, "", "", ""));
            }
        }

        var items = new List<DropItem>();
        var directNames = treasureClasses.SelectMany(t => t.Entries).Select(e => e.Code.Split(',')[0])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        void AddNamed(IEnumerable<DropNamedItem> rows, string quality)
        {
            foreach (var row in rows.Where(r => !r.Disabled && !string.IsNullOrWhiteSpace(r.Index)
                         && (r.Spawnable || directNames.Contains(r.Index))))
            {
                var code = quality == "unique" ? row.Code : row.Item;
                if (string.IsNullOrWhiteSpace(code)) continue;
                items.Add(new DropItem(quality + ":" + row.Index, row.Index!, code, quality, row.Lvl, row.Rarity, row.Spawnable, row.DropConditionCalc ?? ""));
            }
        }
        AddNamed(uniques, "unique");
        AddNamed(sets, "set");
        items.AddRange(misc.Where(b => config.DropRuneTypes.Any(t => HasType(b.Type, t)))
            .Select(b => new DropItem("rune:" + b.Code, b.NameStr ?? b.Code!, b.Code!, "rune", b.Level ?? 0, 1, true, "")));

        var sources = new List<DropSource>();
        foreach (var monster in monsters.Where(m => m.Enabled == true && m.Killable == true && !string.IsNullOrEmpty(m.NameStr)))
        {
            for (var difficulty = 0; difficulty < 3; difficulty++)
            {
                var suffix = new[] { "", "N", "H" }[difficulty];
                var areas = levels.Where(l => Enumerable.Range(1, 25).Any(i =>
                    string.Equals((string?)typeof(DropLevel).GetProperty($"{(difficulty == 0 ? "Mon" : "Nmon")}{i}")!.GetValue(l), monster.Id, StringComparison.OrdinalIgnoreCase)
                    || string.Equals((string?)typeof(DropLevel).GetProperty($"Umon{i}")!.GetValue(l), monster.Id, StringComparison.OrdinalIgnoreCase))).ToList();
                var fixedSuperOnly = areas.Count == 0 && supers.Any(s =>
                    string.Equals(s.Class, monster.Id, StringComparison.OrdinalIgnoreCase)
                    && config.DropSuperUniqueAreas.ContainsKey(s.Superunique ?? ""));
                if (config.DropMonsterAreas.TryGetValue(monster.Id ?? "", out var fixedAreaIds))
                    areas = levels.Where(l => fixedAreaIds.Contains(l.Id)).ToList();
                if (areas.Count == 0) areas.Add(new DropLevel()); // Fixed/script-spawned monsters have no level-table membership.
                foreach (var area in areas)
                {
                    if (fixedSuperOnly) continue;
                    var baseLevel = (int?)typeof(MonStat).GetProperty("Level" + suffix)!.GetValue(monster) ?? 1;
                    if (difficulty > 0 && monster.Boss != true && monster.NoRatio != true)
                        baseLevel = (int)typeof(DropLevel).GetProperty("MonLvlEx" + suffix)!.GetValue(area)! is > 0 and var areaLevel ? areaLevel : baseLevel;
                    foreach (var (kind, field, bonus) in new[] { ("normal", "", 0), ("champion", "Champ", 2), ("unique", "Unique", 3), ("quest", "Quest", 0) })
                    {
                        if (monster.Boss == true && kind is "champion" or "unique") continue;
                        var tc = (string?)typeof(MonStat).GetProperty("TreasureClass" + field + suffix)!.GetValue(monster);
                        if (string.IsNullOrEmpty(tc)) continue;
                        sources.Add(new DropSource(monster.Id + ":" + area.Id + ":" + difficulty + ":" + kind,
                            monster.NameStr!, area.LevelName ?? "", difficulty, monster.Boss == true && kind == "normal" ? "boss" : kind, baseLevel + bonus, tc));
                    }
                }
                foreach (var super in supers.Where(s => string.Equals(s.Class, monster.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    var tc = (string?)typeof(SuperUnique).GetProperty("TC" + suffix)!.GetValue(super);
                    if (string.IsNullOrEmpty(tc) || string.IsNullOrEmpty(super.Name)) continue;
                    var superAreas = config.DropSuperUniqueAreas.TryGetValue(super.Superunique ?? "", out var areaIds)
                        ? levels.Where(l => areaIds.Contains(l.Id)).ToList()
                        : string.Equals(super.Name, monster.NameStr, StringComparison.OrdinalIgnoreCase) ? areas : [];
                    // Never assign a fixed superunique every area occupied by its generic base class.
                    foreach (var area in superAreas)
                    {
                        var level = (int?)typeof(MonStat).GetProperty("Level" + suffix)!.GetValue(monster) ?? 1;
                        var areaLevel = (int)typeof(DropLevel).GetProperty("MonLvlEx" + suffix)!.GetValue(area)!;
                        if (difficulty > 0 && monster.Boss != true && monster.NoRatio != true && areaLevel > 0) level = areaLevel;
                        sources.Add(new DropSource("super:" + super.Superunique + ":" + area.Id + ":" + difficulty,
                            super.Name, area.LevelName ?? "", difficulty, "superunique", level + (monster.Boss == true ? 0 : 3), tc));
                    }
                }
            }
        }

        var bundle = new
        {
            Items = items,
            Bases = bases.Select(b => new { b.Code, Level = b.Level ?? 0, Uber = !string.IsNullOrEmpty(b.NormCode) && b.Code != b.NormCode,
                ClassSpecific = types.TryGetValue(b.Type ?? "", out var type) && !string.IsNullOrEmpty(type.Class), Quest = (b.Quest ?? 0) != 0 }),
            Ratios = ratios.Where(r => r.Version == 1),
            TreasureClasses = treasureClasses,
            Sources = sources.DistinctBy(s => new { s.NameKey, s.AreaKey, s.Difficulty, s.Kind, s.Level, s.TreasureClass })
        };
        var directory = Path.Combine(exportDir, "keyed");
        Directory.CreateDirectory(directory);
        await using var stream = File.Create(Path.Combine(directory, "drop-calculator.json"));
        await JsonSerializer.SerializeAsync(stream, bundle);
    }

    private sealed record DropEntry(string Code, int Weight);
    private sealed record DropClass(string Code, string Group, int Level, int Picks, int Unique, int Set, int NoDrop, List<DropEntry> Entries, string Condition, string QuestFlag, string QuestFlagEx);
    private sealed record DropItem(string Id, string NameKey, string Code, string Quality, int Level, int Rarity, bool Random, string Condition);
    private sealed record DropSource(string Id, string NameKey, string AreaKey, int Difficulty, string Kind, int Level, string TreasureClass);
}
