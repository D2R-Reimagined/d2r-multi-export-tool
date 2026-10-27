// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using D2RMultiExport.Lib.Models;
using D2RMultiExport.Lib.Translation;
using D2RReimaginedTools.TextFileParsers;

namespace D2RMultiExport.Lib.Exporters;

/// <summary>
/// Exports the ItemStatCost display metadata needed to turn decoded save stats
/// back into the same localized keyed lines used by the catalog pages.
/// </summary>
public static class ItemStatPresentationExporter
{
    public const string OutputPath = "keyed/item-stat-presentation.json";

    public static async Task ExportAsync(
        ExportOutputs outputs,
        SourceRoots sources,
        GameData data,
        bool prettyPrint,
        Action<string> warn)
    {
        if (!ExcelInputs.RequireAll(sources, OutputPath, warn, "itemstatcost.txt")) return;

        var stats = await ReadStatsAsync(sources.Excel("itemstatcost.txt"), warn);
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
            CompositeKeys = data.ExportConfig.ItemStatCompositeKeys
        };

        var options = new JsonSerializerOptions
        {
            WriteIndented = prettyPrint,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        await outputs.WriteJsonAsync(OutputPath, bundle, options);
    }

    /// <summary>
    /// Reads the display columns through the upstream <see cref="ItemStatCostParser"/> (the
    /// project's header-mapped parser) rather than splitting the file by hand. A repeated
    /// <c>Stat</c> keeps the last row, as the game's own table load does, and is reported.
    /// </summary>
    private static async Task<Dictionary<string, ItemStatPresentation>> ReadStatsAsync(string path, Action<string> warn)
    {
        var result = new Dictionary<string, ItemStatPresentation>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in await ItemStatCostParser.GetEntries(path))
        {
            var stat = Clean(row.Stat);
            if (stat is null) continue;
            if (result.ContainsKey(stat))
                warn($"itemstatcost.txt: duplicate Stat '{stat}'; the last row wins.");

            result[stat] = new ItemStatPresentation
            {
                Priority = ParseInt(row.DescPriority),
                Function = ParseInt(row.DescFunc),
                ValueMode = ParseInt(row.DescVal),
                PositiveKey = Clean(row.DescStrPos),
                NegativeKey = Clean(row.DescStrNeg),
                SecondaryKey = Clean(row.DescStr2),
                ValueShift = ParseInt(row.ValShift) ?? 0
            };
        }

        return result;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static int? ParseInt(string? value) => int.TryParse(Clean(value), out var parsed) ? parsed : null;
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
    /// <summary>
    /// Translation key for the skill's name, or <c>null</c> for the monster-only rows
    /// of <c>skills.txt</c> that carry no <c>skilldesc</c> row and therefore no
    /// <c>str name</c>. Consumers must render <see cref="FallbackName"/> when absent.
    /// </summary>
    public string? NameKey { get; init; }

    /// <summary>Raw English name, always present. Never a translation key.</summary>
    public required string FallbackName { get; init; }
    public required string LineKey { get; init; }
    public string? ClassOnlyKey { get; init; }
}
