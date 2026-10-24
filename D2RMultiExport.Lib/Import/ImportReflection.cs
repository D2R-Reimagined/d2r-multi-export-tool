// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace D2RMultiExport.Lib.Import;

/// <summary>
/// Thrown when an importer asks <see cref="ImportReflection"/> for a column the row DTO
/// does not have, or one whose type it cannot read. That is a programming error (a
/// misspelled slot name, or a column renamed/retyped upstream in
/// <c>D2RReimaginedTools.FileExtensions</c>), never a data problem, so it is not
/// silently turned into an empty cell: it surfaces through the importer's normal
/// <c>AddError</c> path with the DTO type, property and calling method.
/// </summary>
public sealed class ImportColumnException(string message) : InvalidOperationException(message);

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
///
/// Absent data and invalid references are kept apart: an empty cell (a <c>null</c> or
/// blank value) is returned as <c>null</c> without comment, because most numbered slots
/// are legitimately unused; a property that does not exist or has an unreadable type
/// throws <see cref="ImportColumnException"/>; a non-empty integer cell that does not
/// parse is reported through the caller's <c>warn</c> sink and read as <c>null</c>.
/// </summary>
internal static class ImportReflection
{
    private static readonly ConcurrentDictionary<(Type Type, string Name), PropertyInfo?> Properties = new();

    /// <summary>
    /// Returns the named string column of <paramref name="obj"/>, or <c>null</c> when the
    /// cell is empty. Throws <see cref="ImportColumnException"/> when the property does
    /// not exist or is not a string.
    /// </summary>
    public static string? GetString(object obj, string propertyName, [CallerMemberName] string caller = "")
    {
        var property = Resolve(obj, propertyName, caller);
        // Checked against the declared type, not the value, so a mistyped reference fails on
        // every row rather than only on the rows where the cell happens to be filled.
        if (property.PropertyType != typeof(string))
            throw new ImportColumnException(
                $"{caller}: column '{propertyName}' on {obj.GetType().Name} is {property.PropertyType.Name}, not a string.");
        return (string?)property.GetValue(obj);
    }

    /// <summary>
    /// Returns the named column of <paramref name="obj"/> as an <c>int?</c>, accepting
    /// either a directly-typed <c>int</c> column or a string column whose contents parse as
    /// an integer (the parser DTOs are inconsistent on which numbered columns are typed and
    /// which arrive as raw strings). Returns <c>null</c> for an empty cell. A non-empty
    /// string that does not parse is reported through <paramref name="warn"/> (when given)
    /// and read as <c>null</c>. Throws <see cref="ImportColumnException"/> when the property
    /// does not exist or has a type other than <c>int</c>/<c>string</c>.
    /// </summary>
    public static int? GetInt(object obj, string propertyName, Action<string>? warn = null, [CallerMemberName] string caller = "")
    {
        var property = Resolve(obj, propertyName, caller);
        if (property.PropertyType != typeof(int) && property.PropertyType != typeof(int?) && property.PropertyType != typeof(string))
            throw new ImportColumnException(
                $"{caller}: column '{propertyName}' on {obj.GetType().Name} is {property.PropertyType.Name}, not an int or string.");
        switch (property.GetValue(obj))
        {
            case null:
                return null;
            case int i:
                return i;
            case string s when string.IsNullOrWhiteSpace(s):
                return null;
            case string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed):
                return parsed;
            default:
                warn?.Invoke($"column '{propertyName}' value '{property.GetValue(obj)}' is not an integer; it was ignored.");
                return null;
        }
    }

    private static PropertyInfo Resolve(object obj, string propertyName, string caller)
    {
        var type = obj.GetType();
        return Properties.GetOrAdd((type, propertyName), static key => key.Type.GetProperty(key.Name))
               ?? throw new ImportColumnException(
                   $"{caller}: {type.Name} has no column '{propertyName}'. The slot name is misspelled or the "
                   + "D2RReimaginedTools.FileExtensions DTO changed; fix the reference (or the parser upstream).");
    }
}
