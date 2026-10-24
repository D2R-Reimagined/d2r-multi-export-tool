// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace D2RMultiExport.Lib.Exporters;

/// <summary>Outcome of a dry run (<see cref="DryRunExport.RunAsync"/>).</summary>
/// <param name="ErrorLogWritten">True when the run recorded errors and <see cref="DryRunExport.ErrorLogFileName"/> was written.</param>
/// <param name="Message">Human-readable outcome.</param>
/// <param name="Warning">A non-fatal problem, for example a scratch directory that could not be deleted.</param>
public sealed record DryRunResult(bool ErrorLogWritten, string Message, string? Warning);

/// <summary>
/// The dry-run mode of the pipeline: the complete export runs into a hidden scratch directory
/// (<c>.d2r-dryrun-*</c>) inside the dry-run log directory and is discarded afterwards, so the
/// export directory is never touched. The only file kept is <see cref="ErrorLogFileName"/>, and
/// only when the run recorded errors; the previous error log is removed at the start of every dry
/// run, so the folder always reflects the latest run. Nothing else in the log directory is
/// touched except scratch directories of the exact <c>.d2r-dryrun-&lt;16 lowercase hex&gt;</c>
/// shape that a killed earlier dry run left behind. Links are removed, never followed.
/// </summary>
public static class DryRunExport
{
    /// <summary>The only file a dry run keeps: every error the run recorded.</summary>
    public const string ErrorLogFileName = "dryrun-errors.txt";

    internal const string ScratchPrefix = ".d2r-dryrun-";

    /// <summary>Marks the repository root when the exporter runs from a source checkout.</summary>
    private const string SolutionFileName = "D2RMultiExport.sln";

    private static readonly Regex ScratchShape = new($"^{Regex.Escape(ScratchPrefix)}[0-9a-f]{{16}}$", RegexOptions.CultureInvariant);

    /// <summary>
    /// The default dry-run log directory: <c>test-output/dryrun</c> of the repository containing
    /// the running exporter (the nearest ancestor of <paramref name="startDirectory"/>, default
    /// <see cref="AppContext.BaseDirectory"/>, that holds <c>D2RMultiExport.sln</c>), else
    /// <c>test-output/dryrun</c> below <paramref name="startDirectory"/> itself.
    /// </summary>
    public static string DefaultLogDirectory(string? startDirectory = null)
    {
        var start = Path.TrimEndingDirectorySeparator(Path.GetFullPath(startDirectory ?? AppContext.BaseDirectory));
        for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, SolutionFileName)))
                return Path.Combine(directory.FullName, "test-output", "dryrun");
        }
        return Path.Combine(start, "test-output", "dryrun");
    }

    /// <summary>
    /// Runs <paramref name="generate"/> against a new scratch directory inside
    /// <paramref name="logRoot"/> and deletes it afterwards, whether the run succeeded, recorded
    /// errors or threw. <paramref name="renderErrorLog"/> receives the exception the run threw
    /// (or <c>null</c>) and returns the error log, or <c>null</c> when there is nothing to log.
    /// An exception thrown by <paramref name="generate"/> is rethrown after the log was written.
    /// </summary>
    internal static async Task<DryRunResult> RunAsync(string logRoot, Func<string, Task> generate, Func<Exception?, string?> renderErrorLog)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(logRoot));
        Directory.CreateDirectory(root);
        var logs = new ExportOutputs(root) { Producer = "dry-run" };
        // Replace own files: a clean run must not leave an earlier run's error log behind.
        AtomicFile.DeleteIfExists(logs.GetFullPath(ErrorLogFileName));
        var warnings = new List<string>();
        RemoveAbandonedScratch(root, warnings);

        var scratch = Path.Combine(root, ScratchPrefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)));
        Directory.CreateDirectory(scratch);
        Exception? failure = null;
        try
        {
            await generate(scratch);
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        if (!TryDelete(scratch))
            warnings.Add($"The discarded dry-run output in '{scratch}' could not be deleted; delete it by hand.");

        var log = renderErrorLog(failure);
        try
        {
            if (log is not null)
                await logs.WriteTextAsync(ErrorLogFileName, log);
        }
        catch (Exception ex) when (failure is not null && ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // The run's own exception is the one worth surfacing; its errors are also in the
            // console or GUI log.
        }
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();

        var message = log is null
            ? $"No errors; the export was discarded and no error log was written to '{root}'."
            : $"The run recorded errors; the export was discarded and the errors were written to '{logs.GetFullPath(ErrorLogFileName)}'.";
        return new DryRunResult(log is not null, message, warnings.Count > 0 ? string.Join(" ", warnings) : null);
    }

    /// <summary>Scratch directories a killed earlier dry run left behind; only the exact generated shape.</summary>
    private static void RemoveAbandonedScratch(string root, List<string> warnings)
    {
        foreach (var directory in new DirectoryInfo(root).EnumerateDirectories(ScratchPrefix + "*").ToList())
        {
            if (!ScratchShape.IsMatch(directory.Name) || directory.LinkTarget is not null) continue;
            if (!TryDelete(directory.FullName))
                warnings.Add($"The abandoned dry-run output in '{directory.FullName}' could not be deleted.");
        }
    }

    /// <summary>Deletes a scratch directory a dry run created. Links inside are removed, never followed.</summary>
    private static bool TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
