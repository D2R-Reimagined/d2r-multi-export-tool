// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using D2RMultiExport.Lib.Models;
using D2RMultiExport.Lib.Translation;

namespace D2RMultiExport.Lib.Exporters;

/// <summary>
/// Exports the ItemStatCost display metadata needed to turn decoded save stats
/// back into the same localized keyed lines used by the catalog pages.
/// </summary>
public static class ItemStatPresentationExporter
{
    private static readonly IReadOnlyDictionary<string, string> CompositeKeys =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PhysicalDamage"] = "strModMinDamageRange",
            ["FireDamage"] = "strModFireDamageRange",
            ["ColdDamage"] = "strModColdDamageRange",
            ["LightningDamage"] = "strModLightningDamageRange",
            ["MagicDamage"] = "strModMagicDamageRange",
            ["PoisonDamage"] = "strModPoisonDamageRange"
        };

    public static async Task ExportAsync(
        string exportDir,
        string excelPath,
        GameData data,
        bool prettyPrint = true)
    {
        var stats = ReadStats(Path.Combine(excelPath, "itemstatcost.txt"));
        var skills = data.SkillsById.Values
            .OrderBy(static skill => skill.Id)
            .ToDictionary(
                static skill => skill.Id,
                static skill => new ItemStatSkillPresentation
                {
                    NameKey = skill.NameKey,
                    FallbackName = string.IsNullOrWhiteSpace(skill.Name) ? skill.Skill : skill.Name,
                    ClassOnlyKey = PropertyKeyResolver.TryGetClassOnlyKey(skill.CharClass),
                    LineKey = string.IsNullOrWhiteSpace(skill.CharClass)
                        ? SyntheticStringRegistry.Keys.SkillRandomFromSkill
                        : SyntheticStringRegistry.Keys.SkillRandomFromSkillClass
                });

        var bundle = new ItemStatPresentationBundle
        {
            Stats = stats,
            Skills = skills,
            CompositeKeys = CompositeKeys
        };

        var keyedDir = Path.Combine(exportDir, "keyed");
        Directory.CreateDirectory(keyedDir);
        var options = new JsonSerializerOptions
        {
            WriteIndented = prettyPrint,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        await using var stream = File.Create(Path.Combine(keyedDir, "item-stat-presentation.json"));
        await JsonSerializer.SerializeAsync(stream, bundle, options);
    }

    private static Dictionary<string, ItemStatPresentation> ReadStats(string path)
    {
        using var reader = new StreamReader(path);
        var header = reader.ReadLine()?.Split('\t')
            ?? throw new InvalidDataException($"'{path}' does not contain an ItemStatCost header.");
        var columns = header
            .Select((name, index) => (name, index))
            .ToDictionary(static column => column.name, static column => column.index, StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, ItemStatPresentation>(StringComparer.OrdinalIgnoreCase);

        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var values = line.Split('\t');
            var stat = Read(values, columns, "Stat");
            if (string.IsNullOrWhiteSpace(stat)) continue;

            result[stat] = new ItemStatPresentation
            {
                Priority = ReadInt(values, columns, "descpriority"),
                Function = ReadInt(values, columns, "descfunc"),
                ValueMode = ReadInt(values, columns, "descval"),
                PositiveKey = Read(values, columns, "descstrpos"),
                NegativeKey = Read(values, columns, "descstrneg"),
                SecondaryKey = Read(values, columns, "descstr2"),
                ValueShift = ReadInt(values, columns, "ValShift") ?? 0
            };
        }

        return result;
    }

    private static string? Read(string[] values, IReadOnlyDictionary<string, int> columns, string name)
    {
        if (!columns.TryGetValue(name, out var index) || index >= values.Length) return null;
        return string.IsNullOrWhiteSpace(values[index]) ? null : values[index].Trim();
    }

    private static int? ReadInt(string[] values, IReadOnlyDictionary<string, int> columns, string name) =>
        int.TryParse(Read(values, columns, name), out var value) ? value : null;
}

public sealed class ItemStatPresentationBundle
{
    public required IReadOnlyDictionary<string, ItemStatPresentation> Stats { get; init; }
    public required IReadOnlyDictionary<int, ItemStatSkillPresentation> Skills { get; init; }
    public required IReadOnlyDictionary<string, string> CompositeKeys { get; init; }
}

public sealed class ItemStatPresentation
{
    public int? Priority { get; init; }
    public int? Function { get; init; }
    public int? ValueMode { get; init; }
    public string? PositiveKey { get; init; }
    public string? NegativeKey { get; init; }
    public string? SecondaryKey { get; init; }
    public int ValueShift { get; init; }
}

public sealed class ItemStatSkillPresentation
{
    public required string NameKey { get; init; }
    public required string FallbackName { get; init; }
    public required string LineKey { get; init; }
    public string? ClassOnlyKey { get; init; }
}
