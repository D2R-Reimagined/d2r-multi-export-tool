// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using System.Text.Json;
using D2RMultiExport.Lib.Config;
using D2RReimaginedTools.Models;
using D2RReimaginedTools.TextFileParsers;

namespace D2RMultiExport.Lib.Exporters;

/// <summary>Exports raw health contributors for the website's balancing workbench.</summary>
public static class MonsterHealthExporter
{
    public static async Task ExportAsync(string exportDir, string excelPath, ExportConfig config)
    {
        var monsters = await MonStatsParser.GetEntries(Path.Combine(excelPath, "monstats.txt"));
        var levels = await MonLvlParser.GetEntries(Path.Combine(excelPath, "monlvl.txt"));
        var areas = await DropLevelParser.GetEntries(Path.Combine(excelPath, "levels.txt"));
        var modifiers = await MonUModParser.GetEntries(Path.Combine(excelPath, "monumod.txt"));
        var rules = config.MonsterHealth;
        if (rules.MaximumPlayers < 1 || rules.MaximumLife < 1 || rules.AdditionalPlayerPercent < 0)
        {
            throw new InvalidDataException("monsterHealth engine rules are missing or invalid.");
        }

        static int Number(object row, string field) => Convert.ToInt32(row.GetType().GetProperty(field)!.GetValue(row));
        // These are engine table offsets, not configurable content IDs (UMod2_HealthBonus).
        int[] Bonuses(int offset) => Enumerable.Range(offset, 3).Select(id =>
            modifiers.Single(m => m.Id == id).Constants ?? throw new InvalidDataException($"Missing monumod constant {id}.")).ToArray();
        var suffixes = new[] { "", "N", "H" };
        var rows = monsters.Where(m => !string.IsNullOrWhiteSpace(m.Id) && !string.IsNullOrWhiteSpace(m.NameStr)).Select(m => new
        {
            m.Id, NameKey = m.NameStr, Enabled = m.Enabled == true, Killable = m.Killable == true,
            Boss = m.Boss == true, NoRatio = m.NoRatio == true, Align = Number(m, "Align"),
            m.DamageRegen, MonProp = m.MonProp ?? "",
            Stats = suffixes.Select(s => new
            {
                Level = Number(m, "Level" + s), MinHP = Number(m, "MinHP" + s), MaxHP = Number(m, "MaxHP" + s),
                PhysicalResist = Number(m, "ResDm" + s), MagicResist = Number(m, "ResMa" + s),
                FireResist = Number(m, "ResFi" + s), LightningResist = Number(m, "ResLi" + s),
                ColdResist = Number(m, "ResCo" + s), PoisonResist = Number(m, "ResPo" + s)
            }),
            Areas = suffixes.Select((s, difficulty) => areas.Where(a =>
                (config.DropMonsterAreas.TryGetValue(m.Id!, out var fixedAreas) && fixedAreas.Contains(a.Id)) ||
                Enumerable.Range(1, 25).Any(i =>
                    string.Equals((string?)typeof(DropLevel).GetProperty($"{(difficulty == 0 ? "Mon" : "Nmon")}{i}")!.GetValue(a), m.Id, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals((string?)typeof(DropLevel).GetProperty($"Umon{i}")!.GetValue(a), m.Id, StringComparison.OrdinalIgnoreCase)))
                .Select(a => a.Id).ToArray())
        }).ToArray();
        var hashes = new List<object>();
        foreach (var file in new[] { "monstats.txt", "monlvl.txt", "levels.txt", "monumod.txt", "monprop.txt" })
        {
            await using var source = File.OpenRead(Path.Combine(excelPath, file));
            hashes.Add(new { Code = file, Hash = Convert.ToHexString(await SHA256.HashDataAsync(source)) });
        }
        var bundle = new
        {
            Rules = rules, Monsters = rows,
            Levels = levels.Where(l => l.Level.HasValue).Select(l => new
            {
                l.Level, HP = new[] { l.HP, l.HPN, l.HPH }, LHP = new[] { l.LHP, l.LHPN, l.LHPH }
            }),
            Areas = areas.Where(a => !string.IsNullOrEmpty(a.LevelName)).Select(a => new
            {
                a.Id, NameKey = a.LevelName, Levels = new[] { a.MonLvlEx, a.MonLvlExN, a.MonLvlExH }
            }),
            Bonuses = new { Minion = Bonuses(1), Champion = Bonuses(4), Unique = Bonuses(7) }, Sources = hashes
        };
        var directory = Path.Combine(exportDir, "keyed");
        Directory.CreateDirectory(directory);
        await using var output = File.Create(Path.Combine(directory, "health-calculator.json"));
        await JsonSerializer.SerializeAsync(output, bundle);
    }
}
