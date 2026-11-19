// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using D2RMultiExport.Lib.Exporters;
using D2RReimaginedTools.Models;

namespace D2RMultiExport.Tests;

/// <summary>Final-JSON regressions for player calculation inputs.</summary>
public sealed class PlayerCalculationExporterTests : IDisposable
{
    private readonly string _output = Directory.CreateTempSubdirectory("d2r-player-calculation-").FullName;

    public void Dispose() => Directory.Delete(_output, recursive: true);

    [Fact]
    public async Task ExportsFractionalClassGrowthAndDifficultyResistanceRules()
    {
        var classes = new[]
        {
            new CharStats
            {
                Class = "Assassin", Strength = 20, Dexterity = 20, Intelligence = 25, Vitality = 20,
                Stamina = 95, HpAdd = 30, LifePerLevel = 8, StaminaPerLevel = 5, ManaPerLevel = 6,
                LifePerVitality = 14, StaminaPerVitality = 5, ManaPerMagic = 9,
                StatPerLevel = 5, SkillsPerLevel = 1, ToHitFactor = 15, BlockFactor = 25
            },
            new CharStats { Class = "Expansion" }
        };
        var difficulties = new[]
        {
            new DifficultyLevel { Name = "Normal", ResistPenalty = 0, ResistPenaltyNonExpansion = 0, ResistFloor = -100 },
            new DifficultyLevel { Name = "Hell", ResistPenalty = -120, ResistPenaltyNonExpansion = -50, ResistFloor = -100 }
        };
        var outputs = new ExportOutputs(_output) { Producer = PlayerCalculationExporter.Producer };

        await PlayerCalculationExporter.ExportAsync(outputs, classes, difficulties, prettyPrint: false);

        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_output, "keyed", "player-calculation.json")));
        var characterClass = Assert.Single(json.RootElement.GetProperty("Classes").EnumerateArray());
        Assert.Equal("Assassin", characterClass.GetProperty("Class").GetString());
        Assert.Equal(3.5, characterClass.GetProperty("LifePerVitality").GetDouble());
        Assert.Equal(2.25, characterClass.GetProperty("ManaPerEnergy").GetDouble());
        var hell = json.RootElement.GetProperty("Difficulties").EnumerateArray()
            .Single(row => row.GetProperty("Id").GetString() == "Hell");
        Assert.Equal(-120, hell.GetProperty("ResistancePenalty").GetInt32());
        Assert.Equal(-50, hell.GetProperty("NonExpansionResistancePenalty").GetInt32());
        Assert.Equal(-100, hell.GetProperty("ResistanceFloor").GetInt32());
    }
}
