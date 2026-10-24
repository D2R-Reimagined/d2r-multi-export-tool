// SPDX-License-Identifier: GPL-3.0-or-later
namespace D2RMultiExport.Lib.Config;

/// <summary>Health engine rules not represented by Excel columns.</summary>
public sealed class MonsterHealthConfig
{
    public int AdditionalPlayerPercent { get; set; }
    public int MaximumPlayers { get; set; }
    public int MaximumLife { get; set; }

    /// <summary>
    /// <c>monumod.txt</c> row id of the normal-difficulty minion HP bonus; the nightmare and
    /// hell rows follow it consecutively. Same shape for the champion and unique ids.
    /// </summary>
    public int MinionBonusModifierId { get; set; }

    public int ChampionBonusModifierId { get; set; }

    public int UniqueBonusModifierId { get; set; }
}
