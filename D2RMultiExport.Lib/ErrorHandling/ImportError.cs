// SPDX-License-Identifier: GPL-3.0-or-later
namespace D2RMultiExport.Lib.ErrorHandling;

/// <summary>
/// Represents a single error that occurred while processing an individual item.
/// Errors are collected per-item instead of halting the entire pipeline.
/// </summary>
public sealed class ImportError
{
    public required string Category { get; init; }
    public required string ItemIdentifier { get; init; }
    public required string Message { get; init; }
    public Exception? Exception { get; init; }
    public ImportErrorSeverity Severity { get; init; } = ImportErrorSeverity.Error;

    public override string ToString()
    {
        var severity = Severity == ImportErrorSeverity.Warning ? "WARN" : "ERROR";
        return $"[{severity}] [{Category}] {ItemIdentifier}: {Message}";
    }
}

public enum ImportErrorSeverity
{
    Warning,
    Error
}
