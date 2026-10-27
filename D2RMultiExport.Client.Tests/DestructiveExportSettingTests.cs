// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using D2RMultiExport.Client.Models;
using D2RMultiExport.Client.ViewModels;
using D2RMultiExport.Client.Views;

namespace D2RMultiExport.Client.Tests;

/// <summary>
/// The "Destructive export" checkbox: off by default, remembered across sessions, passed to
/// the pipeline the Import and Export button runs, and blocking the run for a dangerous
/// Output Files Directory.
/// </summary>
public sealed class DestructiveExportSettingTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("d2r-client-destructive-").FullName;
    private readonly string _mod;
    private readonly string _output;

    public DestructiveExportSettingTests()
    {
        // Never touch the user's real settings file.
        AppSettings.SettingsFile = Path.Combine(_root, "settings.json");
        _mod = Path.Combine(_root, "mod", "data");
        Directory.CreateDirectory(Path.Combine(_mod, "global", "excel"));
        Directory.CreateDirectory(Path.Combine(_mod, "local", "lng", "strings"));
        _output = Path.Combine(_root, "website", "static", "data");
        Directory.CreateDirectory(_output);
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
    public async Task CheckboxIsOffByDefaultRememberedAndPassedToThePipeline()
    {
        var (window, vm) = ShowMain();
        Assert.False(vm.DestructiveExport);
        Assert.False(window.DestructiveExportBox.IsChecked);
        Assert.Equal("Destructive export — replace all existing destination contents", window.DestructiveExportBox.Content);

        vm.ModDataPath = _mod;
        vm.OutputPath = _output;
        window.DestructiveExportBox.IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.DestructiveExport);
        Assert.True(vm.DestructiveValid, vm.DestructiveStatus);
        window.Close();

        // A new session restores the checked state.
        var (second, restored) = ShowMain();
        Assert.True(restored.DestructiveExport);
        Assert.True(second.DestructiveExportBox.IsChecked);
        bool? passed = null;
        restored.RunOverride = pipeline =>
        {
            passed = pipeline.DestructiveExport;
            return Task.CompletedTask;
        };
        Assert.True(restored.RunPipelineCommand.CanExecute(null), restored.ModDataStatus + " / " + restored.DestructiveStatus);
        await restored.RunPipelineCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(passed);
        Assert.Contains("now holds only this export", restored.StatusText);

        // Unchecking is remembered as well, without running another export.
        second.DestructiveExportBox.IsChecked = false;
        Dispatcher.UIThread.RunJobs();
        Assert.False(AppSettings.Load().DestructiveExport);
        restored.RunOverride = pipeline =>
        {
            passed = pipeline.DestructiveExport;
            return Task.CompletedTask;
        };
        await restored.RunPipelineCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(passed);
        Assert.Equal("Export successful!", restored.StatusText);
    }

    [AvaloniaFact]
    public void DangerousOutputDirectoryBlocksOnlyTheDestructiveRun()
    {
        var (window, vm) = ShowMain();
        var repository = Path.Combine(_root, "website");
        Directory.CreateDirectory(Path.Combine(repository, ".git"));
        vm.ModDataPath = _mod;
        vm.OutputPath = repository;
        Assert.True(vm.RunPipelineCommand.CanExecute(null));

        window.DestructiveExportBox.IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.DestructiveValid);
        Assert.Contains("Git repository root", vm.DestructiveStatus);
        Assert.Contains("Git repository root", window.DestructiveStatusText.Text);
        Assert.False(vm.RunPipelineCommand.CanExecute(null));

        // The website's static/data inside the same repository is an allowed destination.
        vm.OutputPath = _output;
        Assert.True(vm.DestructiveValid, vm.DestructiveStatus);
        Assert.True(vm.RunPipelineCommand.CanExecute(null));
    }
}
