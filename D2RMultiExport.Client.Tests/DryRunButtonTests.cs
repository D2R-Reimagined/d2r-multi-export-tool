// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using D2RMultiExport.Client.Models;
using D2RMultiExport.Client.ViewModels;
using D2RMultiExport.Client.Views;
using D2RMultiExport.Lib.Exporters;

namespace D2RMultiExport.Client.Tests;

/// <summary>
/// The "Dry Run" button: runs the pipeline as a dry run without needing an Output Files
/// Directory, never as a destructive export, and with the remembered Dry-run Log Directory.
/// </summary>
public sealed class DryRunButtonTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("d2r-client-dryrun-").FullName;
    private readonly string _mod;

    public DryRunButtonTests()
    {
        // Never touch the user's real settings file.
        AppSettings.SettingsFile = Path.Combine(_root, "settings.json");
        _mod = Path.Combine(_root, "mod", "data");
        Directory.CreateDirectory(Path.Combine(_mod, "global", "excel"));
        Directory.CreateDirectory(Path.Combine(_mod, "local", "lng", "strings"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static (MainWindow Window, MainWindowViewModel Vm) ShowMain()
    {
        var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, vm);
    }

    [AvaloniaFact]
    public async Task DryRunNeedsNoOutputDirectoryAndPassesTheLogDirectory()
    {
        var (window, vm) = ShowMain();
        Assert.Equal("Dry Run", window.DryRunButton.Content);
        Assert.Equal(DryRunExport.DefaultLogDirectory(), window.DryRunLogBox.PlaceholderText);

        vm.ModDataPath = _mod;
        vm.OutputPath = "";
        vm.DestructiveExport = true;
        Assert.False(vm.RunPipelineCommand.CanExecute(null));
        Assert.True(vm.DryRunPipelineCommand.CanExecute(null), vm.ModDataStatus);

        var logs = Path.Combine(_root, "logs");
        window.DryRunLogBox.Text = logs;
        Dispatcher.UIThread.RunJobs();
        (bool DryRun, bool Destructive, string? Logs)? passed = null;
        vm.RunOverride = pipeline =>
        {
            passed = (pipeline.DryRun, pipeline.DestructiveExport, pipeline.DryRunLogDirectory);
            return Task.CompletedTask;
        };
        await vm.DryRunPipelineCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(passed);
        Assert.Equal((true, false, (string?)logs), passed.Value);
        Assert.Contains("Dry run finished without errors", vm.StatusText);
        Assert.Equal(logs, AppSettings.Load().DryRunLogPath);
        window.Close();
    }

    [AvaloniaFact]
    public async Task DryRunAbortedBeforeTheReportsShowsThatNoneWereKept()
    {
        var (window, vm) = ShowMain();
        var logs = Path.Combine(_root, "logs");
        var staleReport = Path.Combine(logs, "extras", "import-report.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(staleReport)!);
        File.WriteAllText(staleReport, "report of an earlier dry run");
        vm.ModDataPath = _mod;
        vm.DryRunLogPath = logs;
        // The empty mod fails its first phase, which aborts the run before the extras.
        vm.ContinueOnException = false;

        await vm.DryRunPipelineCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.StartsWith("Dry run aborted: ", vm.StatusText, StringComparison.Ordinal);
        Assert.EndsWith(" No reports were kept.", vm.StatusText, StringComparison.Ordinal);
        Assert.DoesNotContain("Reports:", vm.StatusText);
        Assert.False(File.Exists(staleReport));
        window.Close();
    }
}
