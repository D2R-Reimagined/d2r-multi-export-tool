// SPDX-License-Identifier: GPL-3.0-or-later
using D2RReimaginedTools.Models;

namespace D2RMultiExport.Lib.Import;

/// <summary>Shared accessors for the source calc columns used by export and evaluation.</summary>
internal static class SkillCalcSource
{
    public static string? Calc(Skills? row, int index) => row is null ? null : index switch
    {
        1 => row.Calc1, 2 => row.Calc2, 3 => row.Calc3, 4 => row.Calc4, 5 => row.Calc5,
        6 => row.Calc6, 7 => row.Calc7, 8 => row.Calc8, 9 => row.Calc9, 10 => row.Calc10,
        _ => null
    };

    public static string? Param(Skills? row, int index) => row is null ? null : index switch
    {
        1 => row.Param1, 2 => row.Param2, 3 => row.Param3, 4 => row.Param4, 5 => row.Param5,
        6 => row.Param6, 7 => row.Param7, 8 => row.Param8, 9 => row.Param9, 10 => row.Param10,
        11 => row.Param11, 12 => row.Param12, 13 => row.Param13, 14 => row.Param14, 15 => row.Param15,
        16 => row.Param16, 17 => row.Param17, 18 => row.Param18, 19 => row.Param19, 20 => row.Param20,
        _ => null
    };

    /// <summary>Number of <c>AuraStat</c>/<c>AuraStatCalc</c> column pairs on a skills.txt row.</summary>
    public const int AuraStatSlots = 6;

    /// <summary>Number of <c>PassiveStat</c>/<c>PassiveCalc</c> column pairs on a skills.txt row.</summary>
    public const int PassiveStatSlots = 14;

    public static string? AuraStat(Skills row, int index) => index switch
    {
        1 => row.AuraStat1, 2 => row.AuraStat2, 3 => row.AuraStat3,
        4 => row.AuraStat4, 5 => row.AuraStat5, 6 => row.AuraStat6,
        _ => null
    };

    public static string? AuraStatCalc(Skills row, int index) => index switch
    {
        1 => row.AuraStatCalc1, 2 => row.AuraStatCalc2, 3 => row.AuraStatCalc3,
        4 => row.AuraStatCalc4, 5 => row.AuraStatCalc5, 6 => row.AuraStatCalc6,
        _ => null
    };

    public static string? PassiveStat(Skills row, int index) => index switch
    {
        1 => row.PassiveStat1, 2 => row.PassiveStat2, 3 => row.PassiveStat3, 4 => row.PassiveStat4,
        5 => row.PassiveStat5, 6 => row.PassiveStat6, 7 => row.PassiveStat7, 8 => row.PassiveStat8,
        9 => row.PassiveStat9, 10 => row.PassiveStat10, 11 => row.PassiveStat11, 12 => row.PassiveStat12,
        13 => row.PassiveStat13, 14 => row.PassiveStat14,
        _ => null
    };

    public static string? PassiveCalc(Skills row, int index) => index switch
    {
        1 => row.PassiveCalc1, 2 => row.PassiveCalc2, 3 => row.PassiveCalc3, 4 => row.PassiveCalc4,
        5 => row.PassiveCalc5, 6 => row.PassiveCalc6, 7 => row.PassiveCalc7, 8 => row.PassiveCalc8,
        9 => row.PassiveCalc9, 10 => row.PassiveCalc10, 11 => row.PassiveCalc11, 12 => row.PassiveCalc12,
        13 => row.PassiveCalc13, 14 => row.PassiveCalc14,
        _ => null
    };
}
