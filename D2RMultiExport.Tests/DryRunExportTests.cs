// SPDX-License-Identifier: GPL-3.0-or-later
using D2RMultiExport.Lib;
using D2RMultiExport.Lib.Exporters;

namespace D2RMultiExport.Tests;

/// <summary>
/// The dry-run mode: the full export runs into a scratch directory that is always discarded,
/// the export directory is never touched, and only an error log is kept — only when the run
/// recorded errors, and replacing the previous dry run's log.
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

    private string ErrorLog => Path.Combine(_logs, DryRunExport.ErrorLogFileName);

    private void AssertNoScratchDirectories() =>
        Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(_logs), entry => Path.GetFileName(entry).StartsWith(DryRunExport.ScratchPrefix, StringComparison.Ordinal));

    private static async Task WriteExportAsync(string root)
    {
        var outputs = new ExportOutputs(root) { Producer = "keyed-core" };
        await outputs.WriteTextAsync("keyed/uniques.json", "[]");
        await outputs.WriteTextAsync("extras/import-report.txt", "report");
    }

    [Fact]
    public async Task CleanRunDiscardsTheExportAndRemovesThePreviousErrorLog()
    {
        Directory.CreateDirectory(_logs);
        File.WriteAllText(ErrorLog, "errors of an earlier dry run");
        File.WriteAllText(Path.Combine(_logs, "notes.txt"), "unrelated");
        string? scratch = null;

        var result = await DryRunExport.RunAsync(_logs, async working =>
        {
            scratch = working;
            Assert.StartsWith(_logs, working, StringComparison.Ordinal);
            await WriteExportAsync(working);
            Assert.True(File.Exists(Path.Combine(working, "keyed", "uniques.json")));
        }, _ => null);

        Assert.False(result.ErrorLogWritten);
        Assert.Null(result.Warning);
        Assert.False(Directory.Exists(scratch));
        Assert.False(File.Exists(ErrorLog));
        Assert.Equal("unrelated", File.ReadAllText(Path.Combine(_logs, "notes.txt")));
        Assert.Equal(["notes.txt"], Directory.EnumerateFileSystemEntries(_logs).Select(Path.GetFileName));
    }

    [Fact]
    public async Task RunWithErrorsKeepsOnlyTheErrorLog()
    {
        var result = await DryRunExport.RunAsync(_logs, WriteExportAsync, failure =>
        {
            Assert.Null(failure);
            return "=== Dry Run Errors: 1 error(s) ===\n";
        });

        Assert.True(result.ErrorLogWritten);
        Assert.Equal("=== Dry Run Errors: 1 error(s) ===\n", File.ReadAllText(ErrorLog));
        Assert.Equal([DryRunExport.ErrorLogFileName], Directory.EnumerateFileSystemEntries(_logs).Select(Path.GetFileName));
    }

    [Fact]
    public async Task ThrowingRunWritesTheErrorLogDiscardsTheExportAndRethrows()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => DryRunExport.RunAsync(_logs, async working =>
        {
            await WriteExportAsync(working);
            throw new InvalidOperationException("exporter blew up");
        }, failure => $"fatal: {failure?.Message}"));

        Assert.Equal("exporter blew up", error.Message);
        Assert.Equal("fatal: exporter blew up", File.ReadAllText(ErrorLog));
        AssertNoScratchDirectories();
    }

    [Fact]
    public async Task OnlyAbandonedScratchDirectoriesOfTheExactShapeAreRemoved()
    {
        var abandoned = Path.Combine(_logs, DryRunExport.ScratchPrefix + "0123456789abcdef");
        var lookalike = Path.Combine(_logs, DryRunExport.ScratchPrefix + "keep-me");
        Directory.CreateDirectory(Path.Combine(abandoned, "keyed"));
        File.WriteAllText(Path.Combine(abandoned, "keyed", "uniques.json"), "[]");
        Directory.CreateDirectory(lookalike);

        await DryRunExport.RunAsync(_logs, _ => Task.CompletedTask, _ => null);

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
    public async Task DryPipelineRunWithErrorsLeavesOnlyTheErrorLog(bool continueOnException)
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
        Assert.Equal(ErrorLog, pipeline.DryRunErrorLogPath);
        var log = File.ReadAllText(ErrorLog);
        Assert.StartsWith("=== Dry Run Errors: ", log, StringComparison.Ordinal);
        Assert.Contains("[ERROR]", log);
        Assert.DoesNotContain("[WARN]", log);
        Assert.Equal([DryRunExport.ErrorLogFileName], Directory.EnumerateFileSystemEntries(_logs).Select(Path.GetFileName));
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
