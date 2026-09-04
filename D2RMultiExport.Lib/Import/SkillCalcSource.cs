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
}
