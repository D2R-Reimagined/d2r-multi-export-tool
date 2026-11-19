// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Encodings.Web;
using System.Text.Json;
using D2RReimaginedTools.Models;
using D2RReimaginedTools.TextFileParsers;

namespace D2RMultiExport.Lib.Exporters;

/// <summary>Exports class resource growth and difficulty resistance rules used by player calculators.</summary>
public static class PlayerCalculationExporter
{
    public const string Producer = "player-calculation.json";
    public const string OutputPath = "keyed/player-calculation.json";

    public static async Task ExportAsync(
        ExportOutputs outputs,
        SourceRoots sources,
        bool prettyPrint,
        Action<string> warn)
    {
        if (!ExcelInputs.RequireAll(sources, OutputPath, warn, "charstats.txt", "difficultylevels.txt")) return;
        var classes = await CharStatsParser.GetEntries(sources.Excel("charstats.txt"));
        var difficulties = await DifficultyLevelParser.GetEntries(sources.Excel("difficultylevels.txt"));
        await ExportAsync(outputs, classes, difficulties, prettyPrint);
    }

    internal static async Task ExportAsync(
        ExportOutputs outputs,
        IEnumerable<CharStats> classes,
        IEnumerable<DifficultyLevel> difficulties,
        bool prettyPrint)
    {
        var document = new
        {
            Classes = classes
                .Where(static row => !string.IsNullOrWhiteSpace(row.Class)
                    && !row.Class.Equals("Expansion", StringComparison.OrdinalIgnoreCase))
                .Select(static row => new
                {
                    row.Class,
                    StartingStrength = row.Strength,
                    StartingDexterity = row.Dexterity,
                    StartingEnergy = row.Intelligence,
                    StartingVitality = row.Vitality,
                    StartingStamina = row.Stamina,
                    HitPointsAdd = row.HpAdd,
                    LifePerLevel = row.LifePerLevel / 4d,
                    StaminaPerLevel = row.StaminaPerLevel / 4d,
                    ManaPerLevel = row.ManaPerLevel / 4d,
                    LifePerVitality = row.LifePerVitality / 4d,
                    StaminaPerVitality = row.StaminaPerVitality / 4d,
                    ManaPerEnergy = row.ManaPerMagic / 4d,
                    StatPointsPerLevel = row.StatPerLevel,
                    SkillPointsPerLevel = row.SkillsPerLevel,
                    row.ToHitFactor,
                    row.BlockFactor
                })
                .ToArray(),
            Difficulties = difficulties
                .Where(static row => !string.IsNullOrWhiteSpace(row.Name))
                .Select(static row => new
                {
                    Id = row.Name,
                    ResistancePenalty = row.ResistPenalty,
                    NonExpansionResistancePenalty = row.ResistPenaltyNonExpansion,
                    ResistanceFloor = row.ResistFloor
                })
                .ToArray()
        };
        var options = new JsonSerializerOptions
        {
            WriteIndented = prettyPrint,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        await outputs.WriteJsonAsync(OutputPath, document, options);
    }
}
