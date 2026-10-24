// SPDX-License-Identifier: GPL-3.0-or-later
using D2RMultiExport.Lib;
using D2RMultiExport.Lib.ErrorHandling;
using D2RMultiExport.Lib.Exporters;

namespace D2RMultiExport.Tests;

/// <summary>
/// The dry-run mode: the full export runs into a scratch directory that is always discarded,
/// including its extras reports, the export directory is never touched, and only a log of the
/// recorded errors and warnings is kept — only when there is one — replacing the previous
/// dry run's files.
/// </summary>
public sealed class DryRunExportTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("d2r-dryrun-tests-").FullName;
    private readonly string _logs;
    private readonly string _out;

    public DryRunExportTests()
    {
        _logs = Path.Combine(_root, "test-output", "dryrun");
        _out = Path.Combine(_root, "out");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string Log => Path.Combine(_logs, DryRunExport.LogFileName);

    private string LegacyErrorLog => Path.Combine(_logs, DryRunExport.LegacyErrorLogFileName);

    private string ImportReport => Path.Combine(_logs, "extras", "import-report.txt");

    private static readonly string[] Reports = ["extras/import-report.txt", "extras/missing-translations.txt"];

    private string[] LogEntries() => [.. Directory.EnumerateFileSystemEntries(_logs).Select(static entry => Path.GetFileName(entry)).Order()];

    private void AssertNoScratchDirectories() =>
        Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(_logs), entry => Path.GetFileName(entry).StartsWith(DryRunExport.ScratchPrefix, StringComparison.Ordinal));

    private static async Task WriteExportAsync(string root)
    {
        var outputs = new ExportOutputs(root) { Producer = "keyed-core" };
        await outputs.WriteTextAsync("keyed/uniques.json", "[]");
        await outputs.WriteTextAsync("extras/import-report.txt", "report");
    }

    [Fact]
    public async Task CleanRunDiscardsTheExportAndItsReportsAndRemovesThePreviousLogs()
    {
        Directory.CreateDirectory(Path.Combine(_logs, "extras"));
        File.WriteAllText(Log, "log of an earlier dry run");
        File.WriteAllText(LegacyErrorLog, "errors of an earlier version's dry run");
        File.WriteAllText(Path.Combine(_logs, "extras", "missing-translations.txt"), "report of an earlier version's dry run");
        File.WriteAllText(Path.Combine(_logs, "extras", "notes.txt"), "unrelated");
        File.WriteAllText(Path.Combine(_logs, "notes.txt"), "unrelated");
        string? scratch = null;

        var result = await DryRunExport.RunAsync(_logs, Reports, async working =>
        {
            scratch = working;
            Assert.StartsWith(_logs, working, StringComparison.Ordinal);
            await WriteExportAsync(working);
            Assert.True(File.Exists(Path.Combine(working, "keyed", "uniques.json")));
        }, _ => null);

        Assert.False(result.LogWritten);
        Assert.Null(result.Warning);
        Assert.False(Directory.Exists(scratch));
        Assert.False(File.Exists(Log));
        Assert.False(File.Exists(LegacyErrorLog));
        // The run's import report is discarded with the export; the earlier copy is gone too.
        Assert.False(File.Exists(ImportReport));
        Assert.False(File.Exists(Path.Combine(_logs, "extras", "missing-translations.txt")));
        // Unrelated files stay, so the folder holding one is kept.
        Assert.Equal("unrelated", File.ReadAllText(Path.Combine(_logs, "notes.txt")));
        Assert.Equal("unrelated", File.ReadAllText(Path.Combine(_logs, "extras", "notes.txt")));
        Assert.Equal(["extras", "notes.txt"], LogEntries());
        Assert.Contains("no log was written", result.Message);
    }

    [Fact]
    public async Task RunWithFindingsKeepsOnlyTheLog()
    {
        var result = await DryRunExport.RunAsync(_logs, Reports, WriteExportAsync, failure =>
        {
            Assert.Null(failure);
            return "=== Dry Run Log: 0 error(s), 1 warning(s) ===\n";
        });

        Assert.True(result.LogWritten);
        Assert.Equal("=== Dry Run Log: 0 error(s), 1 warning(s) ===\n", File.ReadAllText(Log));
        Assert.Equal([DryRunExport.LogFileName], LogEntries());
        Assert.Contains(Log, result.Message);
    }

    [Fact]
    public async Task ReportCopiesOfEarlierVersionsAreRemovedWithTheirEmptyFolder()
    {
        Directory.CreateDirectory(Path.Combine(_logs, "extras"));
        File.WriteAllText(ImportReport, "report of an earlier version's dry run");
        File.WriteAllText(Path.Combine(_logs, "extras", "missing-translations.txt"), "report of an earlier version's dry run");

        await DryRunExport.RunAsync(_logs, Reports, WriteExportAsync, _ => "errors\n");

        Assert.Equal([DryRunExport.LogFileName], LogEntries());
    }

    [Fact]
    public async Task ThrowingRunWritesTheLogDiscardsTheExportAndRethrows()
    {
        DryRunResult? finished = null;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => DryRunExport.RunAsync(_logs, Reports, async working =>
        {
            await WriteExportAsync(working);
            throw new InvalidOperationException("exporter blew up");
        }, failure => $"fatal: {failure?.Message}", outcome => finished = outcome));

        Assert.Equal("exporter blew up", error.Message);
        Assert.Equal("fatal: exporter blew up", File.ReadAllText(Log));
        // Reports the run produced before it threw are discarded with the export.
        Assert.Equal([DryRunExport.LogFileName], LogEntries());
        Assert.NotNull(finished);
        Assert.True(finished.LogWritten);
        Assert.Contains("aborted", finished.Message);
    }

    [Fact]
    public async Task OnlyAbandonedScratchDirectoriesOfTheExactShapeAreRemoved()
    {
        var abandoned = Path.Combine(_logs, DryRunExport.ScratchPrefix + "0123456789abcdef");
        var lookalike = Path.Combine(_logs, DryRunExport.ScratchPrefix + "keep-me");
        Directory.CreateDirectory(Path.Combine(abandoned, "keyed"));
        File.WriteAllText(Path.Combine(abandoned, "keyed", "uniques.json"), "[]");
        Directory.CreateDirectory(lookalike);

        await DryRunExport.RunAsync(_logs, Reports, _ => Task.CompletedTask, _ => null);

        Assert.False(Directory.Exists(abandoned));
        Assert.True(Directory.Exists(lookalike));
    }

    [Fact]
    public void DefaultLogDirectoryIsTestOutputDryRunOfTheRepository()
    {
        var repository = Path.Combine(_root, "repo");
        var bin = Path.Combine(repository, "D2RMultiExport.Console", "bin", "Debug", "net10.0");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(repository, "D2RMultiExport.sln"), "");

        Assert.Equal(Path.Combine(repository, "test-output", "dryrun"), DryRunExport.DefaultLogDirectory(bin));

        // Outside a checkout (a release folder) it falls back to the exporter's own folder.
        var release = Path.Combine(_root, "release");
        Directory.CreateDirectory(release);
        Assert.Equal(Path.Combine(release, "test-output", "dryrun"), DryRunExport.DefaultLogDirectory(release));
    }

    private D2RMultiExportPipeline CreatePipeline(string? output, bool continueOnException)
    {
        // An empty mod: the run records errors (or throws) before it could produce a bundle.
        var mod = Path.Combine(_root, "mod", "data");
        Directory.CreateDirectory(Path.Combine(mod, "global", "excel"));
        Directory.CreateDirectory(Path.Combine(mod, "local", "lng", "strings"));
        return new D2RMultiExportPipeline(mod, null, output)
        {
            DryRun = true,
            DryRunLogDirectory = _logs,
            ContinueOnException = continueOnException
        };
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DryPipelineRunWithErrorsLeavesOnlyTheLogOfErrorsAndWarnings(bool continueOnException)
    {
        var pipeline = CreatePipeline(null, continueOnException);

        try
        {
            await pipeline.RunAsync();
        }
        catch (Exception) when (!continueOnException)
        {
            // Without ContinueOnException the first failed phase aborts the run.
        }

        Assert.True(pipeline.Result.HasErrors);
        Assert.Equal(Log, pipeline.DryRunLogFilePath);
        var log = File.ReadAllText(Log);
        Assert.StartsWith($"=== Dry Run Log: {pipeline.Result.ErrorCount} error(s), {pipeline.Result.WarningCount} warning(s) ===",
            log, StringComparison.Ordinal);
        Assert.Contains("=== Errors ===", log);
        Assert.All(pipeline.Result.AllErrors, e => Assert.Contains(e.ToString(), log));
        if (pipeline.Result.WarningCount > 0)
            Assert.True(log.IndexOf("=== Warnings ===", StringComparison.Ordinal) > log.IndexOf("=== Errors ===", StringComparison.Ordinal));
        // Whether or not the run reached the extras phase, no report is kept.
        Assert.Equal([DryRunExport.LogFileName], LogEntries());
    }

    [Fact]
    public async Task DryPipelineRunListsWarningsInTheLog()
    {
        // Without CASC data the empty mod records "no CASC" warnings next to its errors.
        var pipeline = CreatePipeline(null, continueOnException: true);

        await pipeline.RunAsync();

        Assert.True(pipeline.Result.WarningCount > 0);
        var log = File.ReadAllText(Log);
        Assert.Contains("=== Warnings ===", log);
        Assert.All(pipeline.Result.AllErrors.Where(static e => e.Severity == ImportErrorSeverity.Warning),
            warning => Assert.Contains($"  {warning}", log));
    }

    [Fact]
    public async Task DryPipelineRunNeverTouchesTheExportDirectory()
    {
        Directory.CreateDirectory(_out);
        File.WriteAllText(Path.Combine(_out, "manifest.json"), "previous manifest");
        var pipeline = CreatePipeline(_out, continueOnException: true);
        pipeline.DestructiveExport = true;

        await pipeline.RunAsync();

        Assert.Equal(["manifest.json"], Directory.EnumerateFileSystemEntries(_out).Select(Path.GetFileName));
        Assert.Equal("previous manifest", File.ReadAllText(Path.Combine(_out, "manifest.json")));
        AssertNoScratchDirectories();
    }

    [Fact]
    public async Task RegularRunWithoutAnExportDirectoryIsRefused()
    {
        var pipeline = CreatePipeline(null, continueOnException: true);
        pipeline.DryRun = false;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(pipeline.RunAsync);

        Assert.Contains("export directory is required", error.Message);
        Assert.False(Directory.Exists(_logs));
    }
}
