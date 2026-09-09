// SPDX-License-Identifier: GPL-3.0-or-later
namespace D2RMultiExport.Lib.Config;

/// <summary>Health engine rules not represented by Excel columns.</summary>
public sealed class MonsterHealthConfig
{
    public int AdditionalPlayerPercent { get; set; }
    public int MaximumPlayers { get; set; }
    public int MaximumLife { get; set; }
}
