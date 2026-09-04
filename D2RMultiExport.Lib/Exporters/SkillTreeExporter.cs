// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using System.Globalization;
using D2RMultiExport.Lib.Import;
using D2RMultiExport.Lib.Models;

namespace D2RMultiExport.Lib.Exporters;

/// <summary>
/// Projects the class-owned rows from skills.txt and skilldesc.txt into the
/// localized, layout-aware skill-tree bundle consumed by the website planner.
/// </summary>
internal static class SkillTreeExporter
{
    public static async Task ExportAsync(
        string keyedDir,
        GameData data,
        JsonSerializerOptions options)
    {
        var classes = data.Skills.Values
            .Where(static skill => !string.IsNullOrWhiteSpace(skill.CharClass))
            .GroupBy(static skill => skill.CharClass!, StringComparer.OrdinalIgnoreCase)
            .OrderBy(static group => group.Min(static skill => skill.Id))
            .Select(group => MapClass(group.Key, group, data))
            .Where(static skillClass => skillClass.Tabs.Any(static tab => tab.Skills.Count > 0))
            .ToList();

        if (classes.Count == 0) return;

        var path = Path.Combine(keyedDir, "skills.json");
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, classes, options);
    }

    private static KeyedSkillClass MapClass(
        string classCode,
        IEnumerable<SkillEntry> skills,
        GameData data)
    {
        var className = data.ResolveClassName(classCode);
        var classSkills = skills.OrderBy(static skill => skill.Id).ToList();

        return new KeyedSkillClass
        {
            Class = className,
            ClassCode = classCode,
            NameKey = className,
            Tabs = Enumerable.Range(1, 3)
                .Select(page => new KeyedSkillTab
                {
                    Page = page,
                    NameKey = ResolveCategoryKey(classCode, className, page, data),
                    Skills = classSkills
                        .Where(skill => data.SkillDescs.TryGetValue(skill.SkillDesc ?? "", out var desc)
                            && desc.Page == page
                            && desc.Row > 0
                            && desc.Column > 0)
                        .Select(skill => MapSkill(skill, data))
                        .OrderBy(static skill => skill.Row)
                        .ThenBy(static skill => skill.Column)
                        .ToList()
                })
                .ToList()
        };
    }

    private static KeyedSkill MapSkill(SkillEntry skill, GameData data)
    {
        data.SkillDescs.TryGetValue(skill.SkillDesc ?? "", out var desc);
        data.SkillDescriptions.TryGetValue(skill.Id, out var descriptions);

        return new KeyedSkill
        {
            Calculation = MapCalculation(skill),
            Descriptions = descriptions,
            Id = skill.Id,
            Code = skill.Skill,
            NameKey = skill.NameKey,
            ElementType = NullIfWhiteSpace(skill.SourceRow?.EType),
            ShortDescriptionKey = desc?.ShortString,
            DescriptionKey = desc?.LongString,
            Icon = desc?.IconCel is >= 0 && !string.IsNullOrWhiteSpace(skill.CharClass)
                ? $"sprites/skills/{skill.CharClass!.ToLowerInvariant()}-{desc.IconCel.Value}.webp"
                : null,
            Row = desc?.Row ?? 0,
            Column = desc?.Column ?? 0,
            RequiredLevel = skill.RequiredLevel,
            MaxLevel = skill.MaxLevel,
            PrerequisiteIds = skill.Prerequisites
                .Select(data.ResolveSkill)
                .Where(static prerequisite => prerequisite is not null)
                .Select(static prerequisite => prerequisite!.Id)
                .Distinct()
                .ToList()
        };
    }

    private static KeyedSkillCalculation? MapCalculation(SkillEntry skill)
    {
        var row = skill.SourceRow;
        if (row is null) return null;
        var calculation = new KeyedSkillCalculation
        {
            Params = Enumerable.Range(1, 20)
                .Select(index => (Index: index, Raw: SkillCalcSource.Param(row, index)))
                .Where(static entry => long.TryParse(entry.Raw, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var value) && value != 0)
                .ToDictionary(static entry => entry.Index, static entry => long.Parse(entry.Raw!, CultureInfo.InvariantCulture)),
            Calcs = Enumerable.Range(1, 10)
                .Select(index => (Index: index, Value: SkillCalcSource.Calc(row, index)))
                .Where(static entry => !string.IsNullOrWhiteSpace(entry.Value))
                .ToDictionary(static entry => entry.Index, static entry => entry.Value!),
            PhysicalDamage = NullIfWhiteSpace(row.DmgSymPerCalc),
            ElementalDamage = NullIfWhiteSpace(row.EDmgSymPerCalc),
            ElementalLength = NullIfWhiteSpace(row.ELenSymPerCalc)
        };
        return calculation.Params.Count == 0 && calculation.Calcs.Count == 0
               && calculation.PhysicalDamage is null && calculation.ElementalDamage is null
               && calculation.ElementalLength is null
            ? null
            : calculation;
    }

    private static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string ResolveCategoryKey(string classCode, string className, int page, GameData data)
    {
        if (data.ExportConfig.SkillTreeCategoryKeys.TryGetValue(classCode, out var keys)
            && keys.Count >= page
            && !string.IsNullOrWhiteSpace(keys[page - 1]))
        {
            return keys[page - 1];
        }

        var prefix = className.Length >= 2 ? className[..2] : className;
        return $"SkillCategory{prefix}{page}";
    }

    private sealed class KeyedSkillClass
    {
        public string Class { get; set; } = "";
        public string ClassCode { get; set; } = "";
        public string NameKey { get; set; } = "";
        public List<KeyedSkillTab> Tabs { get; set; } = [];
    }

    private sealed class KeyedSkillTab
    {
        public int Page { get; set; }
        public string NameKey { get; set; } = "";
        public List<KeyedSkill> Skills { get; set; } = [];
    }

    private sealed class KeyedSkill
    {
        public int Id { get; set; }
        public string Code { get; set; } = "";
        public string NameKey { get; set; } = "";
        public string? ElementType { get; set; }
        public string? ShortDescriptionKey { get; set; }
        public string? DescriptionKey { get; set; }

        public string? Icon { get; set; }
        public int Row { get; set; }
        public int Column { get; set; }
        public int RequiredLevel { get; set; }
        public int MaxLevel { get; set; }
        public List<int> PrerequisiteIds { get; set; } = [];
        public KeyedSkillCalculation? Calculation { get; set; }


        /// <summary>
        /// What the skill actually does, solved for every level the planner can display.
        /// Null for skills whose <c>skilldesc.txt</c> row carries no description lines.
        /// </summary>
        public SkillDescriptionSet? Descriptions { get; set; }
    }

    private sealed class KeyedSkillCalculation
    {
        public Dictionary<int, long> Params { get; set; } = [];
        public Dictionary<int, string> Calcs { get; set; } = [];
        public string? PhysicalDamage { get; set; }
        public string? ElementalDamage { get; set; }
        public string? ElementalLength { get; set; }
    }
}
