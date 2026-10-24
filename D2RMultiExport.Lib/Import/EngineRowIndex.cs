// SPDX-License-Identifier: GPL-3.0-or-later
namespace D2RMultiExport.Lib.Import;

/// <summary>
/// The game's record numbering of an excel <c>.txt</c> table. The table compiler (D2R
/// <c>Excel.cpp</c>, ported as <c>BuildCtx</c> in D2RBinTool) keeps every line after the
/// header as one record, in file order, except a line whose first cell is exactly
/// <c>Expansion</c> (case-sensitive). Section headings such as <c>Rings</c> or
/// <c>Armor</c>, rows with a blank key and even blank lines therefore still consume an
/// index, while the <c>Expansion</c> marker does not. The row count of every shipped
/// CASC <c>.bin</c> follows this rule, and the records of <c>monstats.bin</c> carry their
/// <c>Id</c> text in this order; no shipped table contains a blank line, so that case
/// rests on the compiler port alone.
/// <para>
/// Informational <c>*</c> columns (<c>*Id</c>, <c>*ID</c>, <c>*hcIdx</c>, …) are never read by
/// the game and must not be used as identifiers. Whether a table's numeric identifier is its
/// record index is decided per table by its loader: <c>skills.txt</c>, <c>monstats.txt</c>,
/// <c>uniqueitems.txt</c>, <c>setitems.txt</c> and <c>propertygroups.txt</c> use it, whereas
/// <c>levels.txt</c> <c>Id</c> and <c>superuniques.txt</c> <c>hcIdx</c> are parsed columns.
/// </para>
/// </summary>
public static class EngineRowIndex
{
    /// <summary>The section-header first cell the table compiler drops.</summary>
    public const string ExpansionMarker = "Expansion";

    /// <summary>True for a line the table compiler drops, so it consumes no record index.</summary>
    public static bool IsDroppedByEngine(string? firstCell) =>
        string.Equals(firstCell, ExpansionMarker, StringComparison.Ordinal);

    /// <summary>
    /// Returns the zero-based record index of each parsed row, aligned with
    /// <paramref name="firstCells"/> (the typed key column of each row the parser returned),
    /// or <c>null</c> for a row the game drops. <paramref name="skippedLinesBefore"/> gives,
    /// per parsed row, the number of blank lines the parser skipped immediately before it;
    /// the game counts each of them as a record.
    /// </summary>
    public static int?[] Derive(IReadOnlyList<string?> firstCells, IReadOnlyList<int>? skippedLinesBefore = null)
    {
        if (skippedLinesBefore is not null && skippedLinesBefore.Count != firstCells.Count)
            throw new ArgumentException("One skipped-line count is required per parsed row.", nameof(skippedLinesBefore));

        var indices = new int?[firstCells.Count];
        var next = 0;
        for (var row = 0; row < firstCells.Count; row++)
        {
            next += skippedLinesBefore?[row] ?? 0;
            if (IsDroppedByEngine(firstCells[row])) continue;
            indices[row] = next++;
        }
        return indices;
    }

    /// <summary>
    /// <see cref="Derive"/> for the file the rows were parsed from. The header-mapped parser
    /// skips blank and whitespace-only lines, which the game still numbers; only the line
    /// structure is read here to restore them, never cell values.
    /// </summary>
    public static async Task<int?[]> DeriveAsync(string path, IReadOnlyList<string?> firstCells)
    {
        var lines = await File.ReadAllLinesAsync(path);
        var skipped = new List<int>(firstCells.Count);
        var pending = 0;
        foreach (var line in lines.Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                pending++;
                continue;
            }
            skipped.Add(pending);
            pending = 0;
        }

        if (skipped.Count != firstCells.Count)
            throw new InvalidDataException(
                $"{Path.GetFileName(path)}: {skipped.Count} non-blank data lines but {firstCells.Count} parsed rows; "
                + "engine row indices cannot be aligned with the parsed rows.");
        return Derive(firstCells, skipped);
    }

    /// <summary>
    /// The first record whose name number differs from its record index, or <c>null</c>
    /// while the two agree. A key column the table compiler interns (<c>monstats.txt</c>
    /// <c>Id</c>) gives each distinct name, compared case-insensitively, the next number and
    /// returns the earlier number for a repeat; a blank key, including a blank line, is the
    /// name <c>""</c>. Name references compile to that number and are read as a record
    /// index, so after a repeated name they no longer point at the row carrying the name.
    /// <paramref name="indices"/> is the <see cref="Derive"/> result for
    /// <paramref name="firstCells"/>; a gap in it is a blank line the parser skipped.
    /// </summary>
    public static (int Index, string Name, int NameNumber)? FindNameNumberDivergence(
        IReadOnlyList<string?> firstCells, IReadOnlyList<int?> indices)
    {
        var numbers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var next = 0;
        for (var row = 0; row < firstCells.Count; row++)
        {
            if (indices[row] is not { } index) continue;
            for (; next <= index; next++)
            {
                var name = next == index ? firstCells[row] ?? "" : "";
                if (!numbers.TryGetValue(name, out var number))
                    numbers[name] = number = numbers.Count;
                if (number != next) return (next, name, number);
            }
        }
        return null;
    }
}
