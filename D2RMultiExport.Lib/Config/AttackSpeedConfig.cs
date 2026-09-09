// SPDX-License-Identifier: GPL-3.0-or-later
namespace D2RMultiExport.Lib.Config;

/// <summary>Engine rules which cannot be obtained from the mod's Excel tables.</summary>
public sealed class AttackSpeedConfig
{
    public Dictionary<string, string> ClassTokens { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> FormTokens { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> AnimationModes { get; set; } = [];
    public List<string> ExcludedSkills { get; set; } = [];
    public List<string> CommonSkills { get; set; } = [];
    public List<string> BuffSkills { get; set; } = [];
    public Dictionary<string, string> BuffCalcOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string AttackRateStat { get; set; } = "";
    public Dictionary<string, AttackSpeedRule> SkillRules { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> StartingFrames { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    internal void BuildLookups()
    {
        ClassTokens = new(ClassTokens, StringComparer.OrdinalIgnoreCase);
        FormTokens = new(FormTokens, StringComparer.OrdinalIgnoreCase);
        BuffCalcOverrides = new(BuffCalcOverrides, StringComparer.OrdinalIgnoreCase);
        SkillRules = new(SkillRules, StringComparer.OrdinalIgnoreCase);
        StartingFrames = new(StartingFrames, StringComparer.OrdinalIgnoreCase);
        foreach (var rule in SkillRules.Values)
        {
            rule.SequenceFrames = new(rule.SequenceFrames, StringComparer.OrdinalIgnoreCase);
        }
    }
}

/// <summary>Special timing behavior keyed by the source skill identifier.</summary>
public sealed class AttackSpeedRule
{
    public string Kind { get; set; } = "normal";
    public Dictionary<string, int> SequenceFrames { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public int DualFrames { get; set; }
    public bool DualRequired { get; set; }
    public bool DualAllowed { get; set; }
    public int SpeedAdjustment { get; set; }
    public int Hits { get; set; } = 1;
    public string? RollbackCalc { get; set; }
    public string? HitsCalc { get; set; }
    public string? SelfSpeedCalc { get; set; }
}
