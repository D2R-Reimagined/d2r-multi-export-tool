// SPDX-License-Identifier: GPL-3.0-or-later
namespace D2RMultiExport.Lib.ErrorHandling;

/// <summary>
/// Contains both the successfully processed items and any errors that occurred.
/// Every import/processing step returns this instead of throwing.
/// </summary>
public sealed class ImportResult<T>
{
    public List<T> Items { get; set; } = [];
    public List<ImportError> Errors { get; } = [];

    public bool HasErrors => Errors.Any(e => e.Severity == ImportErrorSeverity.Error);
    public bool HasWarnings => Errors.Any(e => e.Severity == ImportErrorSeverity.Warning);

    public void AddItem(T item) => Items.Add(item);

    public void AddError(string category, string itemId, string message, Exception? ex = null)
    {
        Errors.Add(new ImportError
        {
            Category = category,
            ItemIdentifier = itemId,
            Message = message,
            Exception = ex,
            Severity = ImportErrorSeverity.Error
        });
    }

    public void AddWarning(string category, string itemId, string message)
    {
        Errors.Add(new ImportError
        {
            Category = category,
            ItemIdentifier = itemId,
            Message = message,
            Severity = ImportErrorSeverity.Warning
        });
    }
}

/// <summary>
/// Aggregates errors and reports across all import phases for final reporting.
/// </summary>
public sealed class PipelineResult
{
    public List<ImportError> AllErrors { get; } = [];

    public void Merge<T>(ImportResult<T> result)
    {
        AllErrors.AddRange(result.Errors);
    }

    public void AddError(string category, string itemId, string message, Exception? ex = null)
    {
        AllErrors.Add(new ImportError
        {
            Category = category,
            ItemIdentifier = itemId,
            Message = message,
            Exception = ex,
            Severity = ImportErrorSeverity.Error
        });
    }

    public bool HasErrors => AllErrors.Any(e => e.Severity == ImportErrorSeverity.Error);

    public string GenerateReport()
    {
        if (AllErrors.Count == 0)
            return "No errors.";

        var errors = AllErrors.Where(e => e.Severity == ImportErrorSeverity.Error).ToList();
        var warnings = AllErrors.Where(e => e.Severity == ImportErrorSeverity.Warning).ToList();
        var lines = new List<string>
        {
            $"=== Import Report: {errors.Count} error(s), {warnings.Count} warning(s) ==="
        };

        foreach (var group in AllErrors.GroupBy(e => e.Category))
        {
            lines.Add($"\n--- {group.Key} ---");
            foreach (var error in group)
            {
                lines.Add($"  {error}");
            }
        }

        return string.Join("\n", lines);
    }

    public async Task WriteReportAsync(string exportPath)
    {
        var report = GenerateReport();
        await File.WriteAllTextAsync(exportPath, report);
    }

}
