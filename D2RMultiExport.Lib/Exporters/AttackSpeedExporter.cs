// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using D2RMultiExport.Lib.Config;
using D2RMultiExport.Lib.Import;
using D2RMultiExport.Lib.Models;
using D2RReimaginedTools.TextFileParsers;

namespace D2RMultiExport.Lib.Exporters;

/// <summary>Exports source weapon speeds, animations and evaluated mod skill speed curves.</summary>
public static class AttackSpeedExporter
{
    public static async Task ExportAsync(string exportDir, string excelPath, GameData data)
    {
        var config = data.ExportConfig.AttackSpeed;
        var animationPath = Path.Combine(Directory.GetParent(excelPath)!.FullName, "animdata.d2");
        // Missing animations are an explicit unavailable dataset; never substitute vanilla timings.
        var animations = File.Exists(animationPath) ? ReadAnimations(animationPath, config) : [];
        var types = (await ItemTypeParser.GetEntries(Path.Combine(excelPath, "itemtypes.txt")))
            .Where(t => !string.IsNullOrEmpty(t.Code)).ToDictionary(t => t.Code!, StringComparer.OrdinalIgnoreCase);
        HashSet<string> ExpandTypes(string? first, string? second)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Add(string? code)
            {
                if (string.IsNullOrEmpty(code) || !result.Add(code)) return;
                if (types.TryGetValue(code, out var type))
                {
                    Add(type.Equiv1);
                    Add(type.Equiv2);
                }
            }
            Add(first); Add(second);
            return result;
        }
        var weapons = (await WeaponParser.GetEntries(Path.Combine(excelPath, "weapons.txt")))
            .Where(w => !string.IsNullOrEmpty(w.Code) && !string.IsNullOrEmpty(w.WClass))
            .Select(w =>
            {
                var family = ExpandTypes(w.Type, w.Type2);
                return new
                {
                    Code = w.Code!, NameKey = w.NameStr ?? w.Code!, Speed = w.Speed ?? 0,
                    WeaponClass = w.WClass!.ToUpperInvariant(), TwoHandClass = w.TwoHandedWClass?.ToUpperInvariant(),
                    TwoHanded = w.TwoHanded == true, OneOrTwoHanded = w.OneOrTwoHanded == 1,
                    Types = family.Order().ToArray(),
                    ClassCode = family.Select(t => types.GetValueOrDefault(t)?.Class).FirstOrDefault(c => !string.IsNullOrEmpty(c)) ?? ""
                };
            }).ToList();
        var skills = new List<object>();
        var buffs = new List<object>();
        foreach (var skill in data.Skills.Values.OrderBy(s => s.Id))
        {
            var row = skill.SourceRow;
            if (row is null) continue;
            long[]? Evaluate(string? expression)
            {
                if (string.IsNullOrWhiteSpace(expression)) return null;
                var values = new long[100];
                for (var level = 1; level <= values.Length; level++)
                {
                    if (!SkillCalculator.TryEvaluate(expression, new SkillCalcContext { Skill = skill, Data = data, Level = level }, out values[level - 1]))
                        return null;
                }
                return values;
            }
            if (config.BuffSkills.Contains(skill.Skill, StringComparer.OrdinalIgnoreCase))
            {
                for (var index = 1; index <= 12; index++)
                {
                    foreach (var (statPrefix, calcPrefix) in new[] { ("AuraStat", "AuraStatCalc"), ("PassiveStat", "PassiveCalc") })
                    {
                        var stat = row.GetType().GetProperty(statPrefix + index)?.GetValue(row) as string;
                        if (!string.Equals(stat, config.AttackRateStat, StringComparison.OrdinalIgnoreCase)) continue;
                        var expression = row.GetType().GetProperty(calcPrefix + index)?.GetValue(row) as string;
                        if (config.BuffCalcOverrides.TryGetValue(skill.Skill, out var replacement)) expression = replacement;
                        buffs.Add(new { Code = skill.Skill, NameKey = skill.NameKey, Values = Evaluate(expression) });
                    }
                }
            }
            var common = config.CommonSkills.Contains(skill.Skill, StringComparer.OrdinalIgnoreCase);
            if ((!common && string.IsNullOrEmpty(skill.CharClass)) || config.ExcludedSkills.Contains(skill.Skill, StringComparer.OrdinalIgnoreCase) || row.Passive == "1") continue;
            if (!common && (!data.SkillDescs.TryGetValue(skill.SkillDesc ?? "", out var desc) || desc.Row <= 0 || desc.Column <= 0)) continue;
            if (!config.AnimationModes.Contains(row.Anim ?? "", StringComparer.OrdinalIgnoreCase)) continue;
            var rule = config.SkillRules.GetValueOrDefault(skill.Skill) ?? new AttackSpeedRule();
            skills.Add(new
            {
                Code = skill.Skill, NameKey = skill.NameKey, ClassCode = skill.CharClass ?? "",
                Mode = row.Anim!.ToUpperInvariant(), Sequence = row.SeqNum, Rule = rule,
                Restrict = row.Restrict, States = new[] { row.State1, row.State2, row.State3 }.Where(s => !string.IsNullOrEmpty(s)).ToArray(),
                IncludeTypes = new[] { row.ITypeA1, row.ITypeA2, row.ITypeA3 }.Where(t => !string.IsNullOrEmpty(t)).ToArray(),
                ExcludeTypes = new[] { row.ETypeA1, row.ETypeA2 }.Where(t => !string.IsNullOrEmpty(t)).ToArray(),
                Rollback = Evaluate(rule.RollbackCalc), Hits = Evaluate(rule.HitsCalc), SelfSpeed = Evaluate(rule.SelfSpeedCalc)
            });
        }
        var sources = new[] { Path.Combine(excelPath, "weapons.txt"), Path.Combine(excelPath, "skills.txt"), Path.Combine(excelPath, "itemtypes.txt"), animationPath };
        var output = new
        {
            SchemaVersion = 1,
            Sources = sources.Where(File.Exists).Select(path => new { Code = Path.GetFileName(path), Hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) }),
            Classes = config.ClassTokens.Select(pair => new { ClassCode = pair.Key, Token = pair.Value, NameKey = data.ResolveClassName(pair.Key) }),
            Weapons = weapons, Skills = skills, Buffs = buffs, Animations = animations,
            StartingFrames = config.StartingFrames, FormTokens = config.FormTokens
        };
        Directory.CreateDirectory(Path.Combine(exportDir, "keyed"));
        await using var stream = File.Create(Path.Combine(exportDir, "keyed", "ias-calculator.json"));
        await JsonSerializer.SerializeAsync(stream, output);
    }

    private sealed record Animation(string Code, int Frames, int AnimationSpeed, int[] ActionFrames);

    /// <summary>AnimData's 256 hash buckets contain fixed 160-byte little-endian records.</summary>
    private static List<Animation> ReadAnimations(string path, AttackSpeedConfig config)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        var result = new List<Animation>();
        for (var bucket = 0; bucket < 256; bucket++)
        {
            var count = reader.ReadInt32();
            if (count < 0 || count > (reader.BaseStream.Length - reader.BaseStream.Position) / 160)
                throw new InvalidDataException($"Invalid animation bucket in {path}.");
            for (var index = 0; index < count; index++)
            {
                var code = Encoding.ASCII.GetString(reader.ReadBytes(8)).TrimEnd('\0').ToUpperInvariant();
                var frames = reader.ReadInt32();
                var speed = reader.ReadInt32();
                var flags = reader.ReadBytes(144);
                if (flags.Length != 144) throw new EndOfStreamException(path);
                if (code.Length == 7 && config.ClassTokens.Values.Concat(config.FormTokens.Values).Contains(code[..2], StringComparer.OrdinalIgnoreCase))
                    result.Add(new(code, frames, speed, flags.Select((flag, frame) => (flag, frame)).Where(p => p.flag is 1 or 2).Select(p => p.frame).ToArray()));
            }
        }
        if (reader.BaseStream.Position != reader.BaseStream.Length) throw new InvalidDataException($"Unexpected trailing animation data in {path}.");
        return result;
    }
}
