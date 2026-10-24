// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using D2RMultiExport.Lib.Models;
using D2RReimaginedTools.Models;
using D2RReimaginedTools.TextFileParsers;

namespace D2RMultiExport.Lib.Exporters;

/// <summary>Exports raw health contributors for the website's balancing workbench.</summary>
public static class MonsterHealthExporter
{
    public const string OutputPath = "keyed/health-calculator.json";

    private static readonly string[] InputFiles = ["monstats.txt", "monlvl.txt", "levels.txt", "monumod.txt"];

    /// <summary>
    /// Every table hashed into <c>Sources</c>. <c>monprop.txt</c> is not parsed here — the
    /// website reads <c>MonProp</c> against its own copy — but its hash tells it which copy.
    /// </summary>
    private static readonly string[] HashedFiles = ["monstats.txt", "monlvl.txt", "levels.txt", "monumod.txt", "monprop.txt"];

    public static async Task ExportAsync(ExportOutputs outputs, SourceRoots sources, GameData data, bool prettyPrint, Action<string> warn)
    {
        if (!ExcelInputs.RequireAll(sources, OutputPath, warn, InputFiles)) return;

        var config = data.ExportConfig;
        var rules = config.MonsterHealth;
        if (rules.MaximumPlayers < 1 || rules.MaximumLife < 1 || rules.AdditionalPlayerPercent < 0)
        {
            throw new InvalidDataException(
                "export-config.json monsterHealth is missing or invalid: maximumPlayers and maximumLife must be positive "
                + "and additionalPlayerPercent must not be negative.");
        }

        var monsters = await MonStatsParser.GetEntries(sources.Excel("monstats.txt"));
        var levels = await MonLvlParser.GetEntries(sources.Excel("monlvl.txt"));
        var areas = await DropLevelParser.GetEntries(sources.Excel("levels.txt"));
        var modifiers = ExcelInputs.IndexFirst(await MonUModParser.GetEntries(sources.Excel("monumod.txt")),
            static m => m.Id?.ToString(System.Globalization.CultureInfo.InvariantCulture), "monumod.txt", "id", warn);

        // Three consecutive monumod.txt rows (normal, nightmare, hell) per bonus kind.
        int[] Bonuses(string kind, int firstId) => Enumerable.Range(firstId, 3).Select(id =>
            modifiers.TryGetValue(id.ToString(System.Globalization.CultureInfo.InvariantCulture), out var row) && row.Constants is { } constant
                ? constant
                : throw new InvalidDataException(
                    $"monumod.txt has no constants for row id {id} (the {kind} HP bonus configured by export-config.json monsterHealth)."))
            .ToArray();

        var rows = monsters.Where(m => !string.IsNullOrWhiteSpace(m.Id) && !string.IsNullOrWhiteSpace(m.NameStr)).Select(m => new
        {
            m.Id,
            // Only claim a translation key when one actually resolves. A handful of
            // monstats.txt rows (Bunny, Injured Barbarian 1..3, Worldstone Effect) carry
            // a NameStr the game never translated; shipping it as "NameKey" made the
            // website print the identifier and the audit flag it as translator work.
            NameKey = data.Translations.ContainsKey(m.NameStr) ? m.NameStr : null,
            FallbackName = m.NameStr,
            Enabled = m.Enabled == true, Killable = m.Killable == true,
            Boss = m.Boss == true, NoRatio = m.NoRatio == true, Align = m.Align ?? 0,
            m.DamageRegen, MonProp = m.MonProp ?? "",
            Stats = Enumerable.Range(0, 3).Select(difficulty => Stats(m, difficulty)),
            Areas = Enumerable.Range(0, 3).Select(difficulty => areas.Where(a =>
                    (config.DropMonsterAreas.TryGetValue(m.Id!, out var fixedAreas) && fixedAreas.Contains(a.Id))
                    || DropCalculatorExporter.SpawnsIn(a, difficulty, m.Id))
                .Select(a => a.Id).ToArray())
        }).ToArray();

        var hashes = new List<object>();
        foreach (var file in HashedFiles)
        {
            var resolved = sources.ExcelSource(file);
            if (resolved.Path is not { } path)
            {
                warn($"{OutputPath}: '{file}' was not found (searched {resolved.DescribeSearch()}), so its hash is omitted from Sources.");
                continue;
            }
            await using var source = File.OpenRead(path);
            hashes.Add(new { Code = file, Hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(source)) });
        }

        var bundle = new
        {
            // Projected explicitly: MonsterHealthConfig also carries the monumod row ids,
            // which are exporter inputs, not part of the wire contract.
            Rules = new { rules.AdditionalPlayerPercent, rules.MaximumPlayers, rules.MaximumLife },
            Monsters = rows,
            Levels = levels.Where(l => l.Level.HasValue).Select(l => new
            {
                l.Level, HP = new[] { l.HP, l.HPN, l.HPH }, LHP = new[] { l.LHP, l.LHPN, l.LHPH }
            }),
            Areas = areas.Where(a => !string.IsNullOrEmpty(a.LevelName)).Select(a => new
            {
                a.Id, NameKey = a.LevelName, Levels = new[] { a.MonLvlEx, a.MonLvlExN, a.MonLvlExH }
            }),
            Bonuses = new
            {
                Minion = Bonuses("minion", rules.MinionBonusModifierId),
                Champion = Bonuses("champion", rules.ChampionBonusModifierId),
                Unique = Bonuses("unique", rules.UniqueBonusModifierId)
            },
            Sources = hashes
        };
        await outputs.WriteJsonAsync(OutputPath, bundle, new JsonSerializerOptions
        {
            WriteIndented = prettyPrint,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });
    }

    private sealed record DifficultyStats(int Level, int MinHP, int MaxHP, int PhysicalResist, int MagicResist,
        int FireResist, int LightningResist, int ColdResist, int PoisonResist);

    /// <summary>The per-difficulty monstats.txt columns (<c>Level</c>, <c>LevelN</c>, <c>LevelH</c>, …).</summary>
    private static DifficultyStats Stats(MonStat m, int difficulty) => difficulty switch
    {
        0 => new(m.Level ?? 0, m.MinHP ?? 0, m.MaxHP ?? 0, m.ResDm ?? 0, m.ResMa ?? 0,
            m.ResFi ?? 0, m.ResLi ?? 0, m.ResCo ?? 0, m.ResPo ?? 0),
        1 => new(m.LevelN ?? 0, m.MinHPN ?? 0, m.MaxHPN ?? 0, m.ResDmN ?? 0, m.ResMaN ?? 0,
            m.ResFiN ?? 0, m.ResLiN ?? 0, m.ResCoN ?? 0, m.ResPoN ?? 0),
        2 => new(m.LevelH ?? 0, m.MinHPH ?? 0, m.MaxHPH ?? 0, m.ResDmH ?? 0, m.ResMaH ?? 0,
            m.ResFiH ?? 0, m.ResLiH ?? 0, m.ResCoH ?? 0, m.ResPoH ?? 0),
        _ => throw new ArgumentOutOfRangeException(nameof(difficulty))
    };
}
