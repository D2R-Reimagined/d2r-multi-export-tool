// SPDX-License-Identifier: GPL-3.0-or-later
namespace D2RMultiExport.Lib.Import;

/// <summary>
/// A keyed line <see cref="PropertyMapper"/> omitted because its template has a value
/// placeholder the mapped property cannot fill. Carries the mapped (post-normalization)
/// code and values so an importer can report it against the source slot that produced it.
/// </summary>
public sealed record OmittedPropertyLine(string PropertyCode, string? Parameter, int? Min, int? Max, string TemplateKey, string Template)
{
    /// <summary>Renders a source cell: <c>&lt;blank&gt;</c> for a missing value, so it never reads as a numeric zero.</summary>
    public static string Cell(string? value) => string.IsNullOrEmpty(value) ? "<blank>" : $"'{value}'";

    /// <inheritdoc cref="Cell(string?)"/>
    public static string Cell(int? value) => value is { } number ? $"'{number}'" : "<blank>";

    /// <summary>
    /// Why the placeholder had no value, naming <paramref name="minColumn"/>/<paramref name="maxColumn"/>
    /// when both mapped endpoints are missing.
    /// </summary>
    public string Reason(string minColumn = "min", string maxColumn = "max") =>
        Min is null && Max is null
            ? $"{minColumn} and {maxColumn} are blank or non-numeric"
            : "the property resolver produced no argument for it";

    /// <summary>The template's key and English text, as shown in diagnostics.</summary>
    public string DescribeTemplate() => $"'{TemplateKey}' (\"{Template}\")";

    /// <summary>The generic diagnostic, used when no importer attributes the line to a source slot.</summary>
    public override string ToString() =>
        $"Property line omitted for '{PropertyCode}':"
        + $"\n    Values:   param {Cell(Parameter)}, min {Cell(Min)}, max {Cell(Max)}"
        + $"\n    Template: {DescribeTemplate()} needs a value, but {Reason()}."
        + "\n    Action: the line was omitted; no value was invented.";
}
