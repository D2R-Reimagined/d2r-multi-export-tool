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
    public const string OutputPath = "keyed/ias-calculator.json";

    private static readonly string[] InputFiles = ["weapons.txt", "skills.txt", "itemtypes.txt"];

    /// <summary>The mod's animation table, relative to the data root.</summary>
    internal const string AnimationTable = "global/animdata.d2";

    public static async Task ExportAsync(ExportOutputs outputs, SourceRoots sources, GameData data, bool prettyPrint, Action<string> warn)
    {
        if (!ExcelInputs.RequireAll(sources, OutputPath, warn, InputFiles)) return;

        var config = data.ExportConfig.AttackSpeed;
        if (config.CurveLevels < 1)
            throw new InvalidDataException(
                $"export-config.json attackSpeed.curveLevels must be a positive integer (found {config.CurveLevels}).");

        // Missing animations are an explicit unavailable dataset; never substitute vanilla
        // timings (docs/IAS-CALCULATOR.md). This is the one input deliberately resolved in the
        // mod root only: the mod's weapon speeds and skill curves are tuned against its own
        // animdata, so vanilla frames would export plausible-looking but wrong breakpoints.
        var animationSource = sources.ResolveIn(SourceOrigin.Mod, AnimationTable);
        var animationPath = animationSource.Path;
        List<Animation> animations = [];
        if (animationPath is not null)
            animations = ReadAnimations(animationPath, config);
        else
            warn($"{OutputPath}: '{animationSource.Expected}' is not present, so Animations is empty and animation-based speeds are unavailable (vanilla timings are never substituted).");
        var types = ExcelInputs.IndexFirst(await ItemTypeParser.GetEntries(sources.Excel("itemtypes.txt")),
            static t => t.Code, "itemtypes.txt", "Code", warn);
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
        var weapons = (await WeaponParser.GetEntries(sources.Excel("weapons.txt")))
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
            // Null for an absent expression (the curve does not apply). An expression that is
            // present but unsolvable is also exported as null, and reported with its cause.
            long[]? Evaluate(string? expression, string curve)
            {
                if (string.IsNullOrWhiteSpace(expression)) return null;
                var values = new long[config.CurveLevels];
                for (var level = 1; level <= values.Length; level++)
                {
                    // The IAS curves are indexed by a single skill level, so hard and bonus
                    // points are treated alike here — unlike the description tables, these
                    // expressions are speed rules rather than blvl-sensitive damage terms.
                    var levels = new CalcLevels(level, level);
                    var context = new SkillCalcContext { Skill = skill, Data = data, Levels = levels };
                    if (!SkillCalculator.TryEvaluate(expression, context, out values[level - 1]))
                    {
                        warn($"{OutputPath}: {skill.Skill} {curve} '{expression}' could not be evaluated at level {level} "
                            + $"({context.Trace.Reason ?? "unknown cause"}); exported as null.");
                        return null;
                    }
                }
                return values;
            }
            if (config.BuffSkillsSet.Contains(skill.Skill))
            {
                // Aura slot N is checked before passive slot N, preserving the historical
                // Buffs order; skills.txt has 6 aura and 14 passive stat slots.
                var slots = Math.Max(SkillCalcSource.AuraStatSlots, SkillCalcSource.PassiveStatSlots);
                for (var index = 1; index <= slots; index++)
                {
                    foreach (var (stat, calc) in new[]
                             {
                                 (SkillCalcSource.AuraStat(row, index), SkillCalcSource.AuraStatCalc(row, index)),
                                 (SkillCalcSource.PassiveStat(row, index), SkillCalcSource.PassiveCalc(row, index))
                             })
                    {
                        if (!string.Equals(stat, config.AttackRateStat, StringComparison.OrdinalIgnoreCase)) continue;
                        var expression = config.BuffCalcOverrides.TryGetValue(skill.Skill, out var replacement) ? replacement : calc;
                        buffs.Add(new { Code = skill.Skill, NameKey = skill.NameKey ?? skill.Skill, Values = Evaluate(expression, "buff curve") });
                    }
                }
            }
            var common = config.CommonSkillsSet.Contains(skill.Skill);
            if ((!common && string.IsNullOrEmpty(skill.CharClass)) || config.ExcludedSkillsSet.Contains(skill.Skill) || row.Passive == "1") continue;
            if (!common && (!data.SkillDescs.TryGetValue(skill.SkillDesc ?? "", out var desc) || desc.Row <= 0 || desc.Column <= 0)) continue;
            if (!config.AnimationModesSet.Contains(row.Anim ?? "")) continue;
            var rule = config.SkillRules.GetValueOrDefault(skill.Skill) ?? new AttackSpeedRule();
            skills.Add(new
            {
                // Player skills only, so skill.NameKey is always set; `?? Code` is defensive.
                Code = skill.Skill, NameKey = skill.NameKey ?? skill.Skill, ClassCode = skill.CharClass ?? "",
                Mode = row.Anim!.ToUpperInvariant(), Sequence = row.SeqNum, Rule = rule,
                Restrict = row.Restrict, States = new[] { row.State1, row.State2, row.State3 }.Where(s => !string.IsNullOrEmpty(s)).ToArray(),
                IncludeTypes = new[] { row.ITypeA1, row.ITypeA2, row.ITypeA3 }.Where(t => !string.IsNullOrEmpty(t)).ToArray(),
                ExcludeTypes = new[] { row.ETypeA1, row.ETypeA2 }.Where(t => !string.IsNullOrEmpty(t)).ToArray(),
                Rollback = Evaluate(rule.RollbackCalc, "rollbackCalc"), Hits = Evaluate(rule.HitsCalc, "hitsCalc"),
                SelfSpeed = Evaluate(rule.SelfSpeedCalc, "selfSpeedCalc")
            });
        }
        var inputPaths = new[] { sources.Excel("weapons.txt"), sources.Excel("skills.txt"), sources.Excel("itemtypes.txt"), animationPath ?? "" };
        var output = new
        {
            SchemaVersion = 1,
            Sources = inputPaths.Where(File.Exists).Select(path => new { Code = Path.GetFileName(path), Hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))) }),
            Classes = config.ClassTokens.Select(pair => new { ClassCode = pair.Key, Token = pair.Value, NameKey = data.ResolveClassName(pair.Key) }),
            Weapons = weapons, Skills = skills, Buffs = buffs, Animations = animations,
            StartingFrames = config.StartingFrames, FormTokens = config.FormTokens
        };
        await outputs.WriteJsonAsync(OutputPath, output, new JsonSerializerOptions { WriteIndented = prettyPrint });
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
