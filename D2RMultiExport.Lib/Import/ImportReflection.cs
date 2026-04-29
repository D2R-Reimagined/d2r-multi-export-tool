// SPDX-License-Identifier: GPL-3.0-or-later
namespace D2RMultiExport.Lib.Import;

/// <summary>
/// Tiny reflection helpers shared by the importers.
///
/// Several `D2RReimaginedTools` row DTOs (e.g. <c>RuneWord</c>, <c>CubeMain</c>,
/// <c>Unique</c>) expose dozens of numbered columns as separate properties
/// (<c>Rune1..Rune6</c>, <c>Mod1Code..Mod7Code</c>, <c>IType1..IType7</c>, …).
/// The importers iterate those numbered slots in a loop and need a tiny
/// reflection probe to fetch a column by name. This file is the single
/// source for those probes so the same one-liner does not get re-inlined into
/// every importer.
/// </summary>
internal static class ImportReflection
{
    /// <summary>
    /// Returns the value of the named string property on <paramref name="obj"/>,
    /// or <c>null</c> if the property does not exist or is not a string.
    /// </summary>
    public static string? GetString(object obj, string propertyName) =>
        obj.GetType().GetProperty(propertyName)?.GetValue(obj) as string;

    /// <summary>
    /// Returns the value of the named property on <paramref name="obj"/> as
    /// an <c>int?</c>, accepting either a directly-typed <c>int</c> column or
    /// a string column whose contents parse as an integer (the parser DTOs
    /// are inconsistent on which numbered columns are typed and which arrive
    /// as raw strings). Returns <c>null</c> when the property is missing,
    /// null, or unparseable.
    /// </summary>
    public static int? GetInt(object obj, string propertyName)
    {
        var val = obj.GetType().GetProperty(propertyName)?.GetValue(obj);
        return val switch
        {
            int i => i,
            string s when int.TryParse(s, out var parsed) => parsed,
            _ => null
        };
    }
}
