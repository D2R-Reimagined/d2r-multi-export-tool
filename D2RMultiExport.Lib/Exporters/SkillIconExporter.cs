// SPDX-License-Identifier: GPL-3.0-or-later
using D2RMultiExport.Lib.Models;

namespace D2RMultiExport.Lib.Exporters;

/// <summary>
/// Extracts the skilldesc IconCel frames from each class's HD skill sheet and
/// writes one compact WebP per skill for the website planner and save viewer.
/// The class → sheet mapping is content and lives in <c>export-config.json</c> →
/// <c>skillIconSheets</c>. Sheets resolve mod first, then CASC.
/// <para>
/// Runs before <c>keyed/skills.json</c> is written: the paths it actually produced are
/// recorded in <see cref="GameData.ExportedSkillIcons"/>, and <c>SkillTreeExporter</c> only
/// emits an <c>Icon</c> that is in that set, so a skill never references an icon this run
/// did not write (an older copy on disk does not count).
/// </para>
/// </summary>
public static class SkillIconExporter
{
    public const string Producer = "skill-icons";

    public static async Task ExportAsync(
        ExportOutputs outputs,
        SourceRoots sources,
        GameData data,
        AssetDiagnostics diagnostics,
        Action<string> warn,
        Action<string>? info = null)
    {
        var generated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        data.ExportedSkillIcons = generated;

        var sheets = data.ExportConfig.SkillIconSheets;
        var attempted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unmappedClasses = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var sheetSources = new Dictionary<string, ResolvedSource>(StringComparer.OrdinalIgnoreCase);
        int fromMod = 0, fromCasc = 0;
        foreach (var skill in data.Skills.Values.Where(static skill => !string.IsNullOrWhiteSpace(skill.CharClass))
                     .OrderBy(static skill => skill.Id))
        {
            if (!data.SkillDescs.TryGetValue(skill.SkillDesc ?? "", out var description)
                || description.IconCel is not >= 0)
            {
                continue;
            }
            if (!sheets.TryGetValue(skill.CharClass!, out var sheet))
            {
                unmappedClasses.Add(skill.CharClass!);
                diagnostics.Add(new AssetDiagnostics.Entry(Producer, skill.Skill, null, null, "",
                    $"class '{skill.CharClass}' has no export-config.json skillIconSheets entry", "none", AssetIssueKind.Missing));
                continue;
            }

            var relativeOutput = IconPath(skill.CharClass!, description.IconCel.Value);
            if (!attempted.Add(relativeOutput)) continue;

            var relativeSource = $"hd/global/ui/spells/{sheet.Folder}/{sheet.File}";
            if (!sheetSources.TryGetValue(relativeSource, out var source))
            {
                source = sources.Resolve(relativeSource);
                sheetSources[relativeSource] = source;
            }
            if (source.Path is null)
            {
                diagnostics.Add(new AssetDiagnostics.Entry(Producer, skill.Skill, relativeSource, source.Expected,
                    source.DescribeSearch(),
                    sources.CascDataRoot is null
                        ? "skill sheet not in the mod and no CASC data root is configured"
                        : "skill sheet exists in neither the mod nor the CASC data root",
                    "none", AssetIssueKind.Missing));
                continue;
            }

            try
            {
                await ItemPresentationExporter.ConvertSpriteAsync(outputs, source.Path, relativeOutput, description.IconCel.Value);
                generated.Add(relativeOutput);
                if (source.Origin == SourceOrigin.Mod) fromMod++;
                else fromCasc++;
            }
            catch (InvalidDataException ex)
            {
                // Decoding fails before anything is written, so nothing this run owns needs
                // removing; an earlier run's copy is left alone and is not in the manifest.
                diagnostics.Add(new AssetDiagnostics.Entry(Producer, skill.Skill, $"{relativeSource}#{description.IconCel.Value}",
                    source.Path, source.DescribeSearch(), $"sprite could not be decoded ({ex.Message})", "none", AssetIssueKind.Missing));
            }
        }

        info?.Invoke($"  {Producer}: {generated.Count} icon(s) written ({fromMod} from the mod, {fromCasc} from CASC).");
        if (unmappedClasses.Count > 0)
            warn($"Skill icons omitted for class(es) {string.Join(", ", unmappedClasses)}: no export-config.json skillIconSheets entry.");
        diagnostics.Summarize(Producer, warn);
    }

    /// <summary>The output path of a class's icon cell (the website reads it from <c>Skill.Icon</c>).</summary>
    public static string IconPath(string charClass, int iconCel) =>
        $"sprites/skills/{charClass.ToLowerInvariant()}-{iconCel}.webp";
}
