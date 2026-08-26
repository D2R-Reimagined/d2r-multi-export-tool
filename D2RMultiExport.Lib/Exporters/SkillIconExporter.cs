// SPDX-License-Identifier: GPL-3.0-or-later
using D2RMultiExport.Lib.Models;

namespace D2RMultiExport.Lib.Exporters;

/// <summary>
/// Extracts the skilldesc IconCel frames from each class's HD skill sheet and
/// writes one compact WebP per skill for the website planner and save viewer.
/// </summary>
public static class SkillIconExporter
{
    private static readonly IReadOnlyDictionary<string, (string Folder, string File)> Sheets =
        new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
        {
            ["ama"] = ("amazon", "amskillicon.sprite"),
            ["sor"] = ("sorceress", "soskillicon.sprite"),
            ["nec"] = ("necromancer", "neskillicon.sprite"),
            ["pal"] = ("paladin", "paskillicon.sprite"),
            ["bar"] = ("barbarian", "baskillicon.sprite"),
            ["dru"] = ("druid", "drskillicon.sprite"),
            ["ass"] = ("assassin", "asskillicon.sprite"),
            ["war"] = ("warlock", "waskillicon.sprite")
        };

    public static async Task ExportAsync(
        string exportDir,
        string excelPath,
        string? baseAssetsPath,
        GameData data)
    {
        var modDataRoot = Directory.GetParent(excelPath)?.Parent?.FullName
            ?? throw new DirectoryNotFoundException($"Could not resolve the mod data root from '{excelPath}'.");
        var assetRoots = new List<string>();
        if (!string.IsNullOrWhiteSpace(baseAssetsPath))
        {
            assetRoots.Add(ItemPresentationExporter.NormalizeDataRoot(baseAssetsPath));
        }
        assetRoots.Add(modDataRoot);

        var exported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var skill in data.Skills.Values.Where(static skill => !string.IsNullOrWhiteSpace(skill.CharClass)))
        {
            if (!Sheets.TryGetValue(skill.CharClass!, out var sheet)
                || !data.SkillDescs.TryGetValue(skill.SkillDesc ?? "", out var description)
                || description.IconCel is not >= 0)
            {
                continue;
            }

            var fileName = $"{skill.CharClass!.ToLowerInvariant()}-{description.IconCel.Value}.webp";
            if (!exported.Add(fileName)) continue;

            var relativeSource = Path.Combine("hd", "global", "ui", "spells", sheet.Folder, sheet.File);
            var source = assetRoots.Select(root => Path.Combine(root, relativeSource)).LastOrDefault(File.Exists);
            if (source is null) continue;

            await ItemPresentationExporter.ConvertSpriteAsync(
                source,
                Path.Combine(exportDir, "sprites", "skills", fileName),
                description.IconCel.Value);
        }
    }
}
