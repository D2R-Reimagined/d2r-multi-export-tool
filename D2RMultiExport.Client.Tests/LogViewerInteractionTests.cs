// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using D2RMultiExport.Client.Models;
using D2RMultiExport.Client.ViewModels;
using D2RMultiExport.Client.Views;
using D2RMultiExport.Lib;

namespace D2RMultiExport.Client.Tests;

/// <summary>
/// Drives the real main window and log viewer on the headless platform with simulated mouse
/// and keyboard input — clicks, selection, Ctrl+C, filtering, saving, closing — including
/// while an export is in progress and after one fails.
/// </summary>
public sealed class LogViewerInteractionTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("d2r-client-tests-").FullName;

    public LogViewerInteractionTests()
    {
        // Never touch the user's real settings file.
        AppSettings.SettingsFile = Path.Combine(_root, "settings.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private (string Mod, string Output) CreateRoots()
    {
        var mod = Path.Combine(_root, "mod", "data");
        Directory.CreateDirectory(Path.Combine(mod, "global", "excel"));
        Directory.CreateDirectory(Path.Combine(mod, "local", "lng", "strings"));
        var output = Path.Combine(_root, "out");
        Directory.CreateDirectory(output);
        return (mod, output);
    }

    private (MainWindow Window, MainWindowViewModel Vm) ShowMain()
    {
        var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, vm);
    }

    private static void Settle(LogViewerWindow? viewer = null)
    {
        Dispatcher.UIThread.RunJobs();
        (viewer?.DataContext as LogViewerViewModel)?.Refresh();
        Dispatcher.UIThread.RunJobs();
    }

    private static void ClickLogPanel(MainWindow window)
    {
        var point = window.LogListBox.TranslatePoint(new Point(30, 30), window)
                    ?? throw new InvalidOperationException("log list not laid out");
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void ClickingTheLogOpensOneNonModalViewerWithTheCompleteLog()
    {
        var (window, vm) = ShowMain();
        for (var i = 0; i < 500; i++) vm.AppendLog($"line {i}");
        vm.AppendLog("[WARN] Export/x: something odd");
        vm.AppendLog("[ERROR] Export/y: something broke");

        ClickLogPanel(window);

        var viewer = Assert.IsType<LogViewerWindow>(window.LogViewer);
        Assert.True(viewer.IsVisible);
        Settle(viewer);
        var text = viewer.LogText.Text ?? "";
        Assert.Contains("line 0\n", text);
        Assert.Contains("line 499\n", text);
        Assert.Contains("[ERROR] Export/y", text);
        Assert.Contains("502 of 502", viewer.SummaryText.Text);
        Assert.Contains("1 error(s), 1 warning(s)", viewer.SummaryText.Text);

        // A second click brings the same window forward instead of stacking another.
        ClickLogPanel(window);
        Assert.Same(viewer, window.LogViewer);

        // The main window stays interactive (non-modal): its controls still take input.
        window.ModDataBox.Focus();
        window.KeyTextInput("abc");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("abc", vm.ModDataPath);
    }

    [AvaloniaFact]
    public async Task TextCanBeSelectedAndCopiedAndCopyAllIgnoresFilters()
    {
        var (window, vm) = ShowMain();
        vm.AppendLog("alpha");
        vm.AppendLog("[WARN] beta");
        vm.AppendLog("[ERROR] gamma");
        var viewer = window.OpenLogViewer()!;
        Settle(viewer);

        // Keyboard selection + Ctrl+C on the read-only text.
        viewer.LogText.Focus();
        viewer.KeyPress(Key.A, RawInputModifiers.Control, PhysicalKey.A, "a");
        viewer.KeyPress(Key.C, RawInputModifiers.Control, PhysicalKey.C, "c");
        Dispatcher.UIThread.RunJobs();
        var clipboard = viewer.Clipboard!;
        Assert.Equal("alpha\n[WARN] beta\n[ERROR] gamma\n", await clipboard.TryGetTextAsync());

        // A partial selection copies only the selected range.
        viewer.LogText.SelectionStart = 0;
        viewer.LogText.SelectionEnd = 5;
        viewer.KeyPress(Key.C, RawInputModifiers.Control, PhysicalKey.C, "c");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("alpha", await clipboard.TryGetTextAsync());

        // Filter to errors only; Copy All still copies everything.
        viewer.ShowInfoBox.IsChecked = false;
        viewer.ShowWarningsBox.IsChecked = false;
        Settle(viewer);
        Assert.Equal("[ERROR] gamma\n", viewer.LogText.Text);
        await viewer.CopyAllAsync();
        var all = await clipboard.TryGetTextAsync();
        Assert.Contains("alpha", all);
        Assert.Contains("[WARN] beta", all);
        Assert.Contains("[ERROR] gamma", all);
    }

    [AvaloniaFact]
    public async Task SearchFiltersAndSaveWritesTheCompleteLog()
    {
        var (window, vm) = ShowMain();
        foreach (var line in new[] { "Loading game data files...", "[WARN] Export/item-presentation.json: 1 asset(s) unresolved", "Pipeline finished successfully." })
            vm.AppendLog(line);
        var viewer = window.OpenLogViewer()!;
        Settle(viewer);

        // Ctrl+F jumps to the search box; typing there filters the shown lines.
        viewer.Activate();
        viewer.LogText.Focus();
        viewer.KeyPress(Key.F, RawInputModifiers.Control, PhysicalKey.F, "f");
        Dispatcher.UIThread.RunJobs();
        Assert.True(viewer.SearchBox.IsFocused);
        viewer.KeyTextInput("ASSET");
        Settle(viewer);
        Assert.Equal("[WARN] Export/item-presentation.json: 1 asset(s) unresolved\n", viewer.LogText.Text);

        var path = Path.Combine(_root, "saved-log.txt");
        await viewer.SaveToAsync(path);
        var saved = await File.ReadAllTextAsync(path);
        Assert.Contains("Loading game data files...", saved);
        Assert.Contains("Pipeline finished successfully.", saved);
        Assert.Contains("1 asset(s) unresolved", saved);
    }

    [AvaloniaFact]
    public void EscapeAndCloseButtonCloseTheViewerAndItCanBeReopened()
    {
        var (window, _) = ShowMain();
        var viewer = window.OpenLogViewer()!;
        viewer.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Dispatcher.UIThread.RunJobs();
        Assert.Null(window.LogViewer);

        var again = window.OpenLogViewer()!;
        Assert.NotSame(viewer, again);
        var closePoint = again.CloseButton.TranslatePoint(new Point(5, 5), again)
                         ?? throw new InvalidOperationException("close button not laid out");
        again.MouseDown(closePoint, MouseButton.Left);
        again.MouseUp(closePoint, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Null(window.LogViewer);
    }

    [AvaloniaFact]
    public async Task ViewerFollowsAnActiveExportAndKeepsTheLogAfterItEnds()
    {
        var (mod, output) = CreateRoots();
        var (window, vm) = ShowMain();
        vm.ModDataPath = mod;
        vm.OutputPath = output;
        Assert.True(vm.RunPipelineCommand.CanExecute(null), vm.ModDataStatus);

        var release = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        vm.RunOverride = async pipeline =>
        {
            pipeline.Progress!.Report("phase one");
            started.SetResult();
            await release.Task;
            pipeline.Result.AddWarning("Export", "item-presentation.json", "1 asset(s) unresolved");
            pipeline.Progress!.Report("phase two");
        };

        var run = vm.RunPipelineCommand.ExecuteAsync(null);
        await started.Task;
        Settle();
        Assert.True(vm.IsBusy);

        // Opening the viewer mid-export neither blocks nor cancels it.
        ClickLogPanel(window);
        var viewer = window.LogViewer!;
        Settle(viewer);
        Assert.Contains("phase one", viewer.LogText.Text);
        Assert.True(vm.IsBusy);
        Assert.False(vm.RunPipelineCommand.CanExecute(null));

        release.SetResult();
        await run;
        Settle(viewer);
        Assert.False(vm.IsBusy);
        var text = viewer.LogText.Text ?? "";
        Assert.Contains("phase two", text);
        // The full import report is appended, so nothing in import-report.txt is missing here.
        Assert.Contains("=== Import Report: 0 error(s), 1 warning(s) ===", text);
        Assert.Contains("[WARN] [Export] item-presentation.json: 1 asset(s) unresolved", text);
        Assert.Contains("Export successful!", text);
        Assert.True(viewer.IsVisible);
    }

    [AvaloniaFact]
    public async Task AFailingExportIsReportedWithoutClosingOrFreezingTheApp()
    {
        var (mod, output) = CreateRoots();
        var (window, vm) = ShowMain();
        vm.ModDataPath = mod;
        vm.OutputPath = output;
        vm.RunOverride = _ => throw new InvalidOperationException("simulated exporter crash");

        await vm.RunPipelineCommand.ExecuteAsync(null);
        Settle();

        Assert.False(vm.IsBusy);
        Assert.StartsWith("Aborted: simulated exporter crash", vm.StatusText);
        Assert.True(window.IsVisible);
        Assert.Contains(vm.LogEntries, e => e.Level == LogLevel.Error && e.Text.Contains("simulated exporter crash"));
        var viewer = window.OpenLogViewer()!;
        viewer.ShowInfoBox.IsChecked = false;
        viewer.ShowWarningsBox.IsChecked = false;
        Settle(viewer);
        Assert.StartsWith("[FATAL] System.InvalidOperationException: simulated exporter crash", viewer.LogText.Text);
        Assert.True(vm.RunPipelineCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void InvalidSourceSelectionsAreExplainedAndBlockTheRun()
    {
        var (mod, output) = CreateRoots();
        var (window, vm) = ShowMain();
        vm.OutputPath = output;

        vm.ModDataPath = Path.Combine(mod, "global");
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.ModDataValid);
        Assert.Contains("is not a supported layout", window.ModDataStatusText.Text);
        Assert.Contains("'global/excel'", window.ModDataStatusText.Text);
        Assert.False(vm.RunPipelineCommand.CanExecute(null));

        var missingStrings = Path.Combine(_root, "missing-strings");
        Directory.CreateDirectory(Path.Combine(missingStrings, "global", "excel"));
        vm.ModDataPath = missingStrings;
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.ModDataValid);
        Assert.Contains("'local/lng/strings'", window.ModDataStatusText.Text);
        Assert.False(vm.RunPipelineCommand.CanExecute(null));

        vm.CascDataPath = Path.Combine(_root, "does-not-exist");
        Assert.False(vm.CascDataValid);
        Assert.Contains("does not exist", vm.CascDataStatus);

        vm.ModDataPath = Path.GetDirectoryName(mod)!; // the folder that contains data/ is accepted
        vm.CascDataPath = "";
        Assert.True(vm.ModDataValid, vm.ModDataStatus);
        Assert.Contains("'data/' under the selected directory", vm.ModDataStatus);
        Assert.True(vm.RunPipelineCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void LegacySettingsMigrateToTheTwoRoots()
    {
        File.WriteAllText(AppSettings.SettingsFile,
            """{ "ExcelPath": "C:\\mods\\reimagined\\data\\global\\excel", "BaseAssetsPath": "C:\\casc\\data", "TranslationsPath": "x", "OutputPath": "C:\\out" }""");
        var settings = AppSettings.Load();
        Assert.Equal(@"C:\mods\reimagined\data", settings.ModDataPath);
        Assert.Equal(@"C:\casc\data", settings.CascDataPath);
        Assert.Equal(@"C:\out", settings.OutputPath);
        settings.Save();
        Assert.DoesNotContain("ExcelPath", File.ReadAllText(AppSettings.SettingsFile));
    }

    [AvaloniaTheory]
    [InlineData(@"C:\casc\data\local\lng\strings", null, null, @"C:\casc\data")]
    [InlineData(@"C:\casc\data\data\LOCAL\LNG\STRINGS\", " ", "", @"C:\casc\data\data")]
    [InlineData("/casc/data/local/lng/strings/", null, null, "/casc/data")]
    [InlineData(@"C:\casc\data\local\lng\strings", @"C:\assets\data", null, @"C:\assets\data")]
    [InlineData(@"C:\casc\data\local\lng\strings", @"C:\assets\data", @"C:\modern\data", @"C:\modern\data")]
    [InlineData(@"C:\casc\data\local\lng\strings", null, @"C:\modern\data", @"C:\modern\data")]
    [InlineData(@"C:\arbitrary-strings", null, null, "")]
    public void LegacyBaseStringsMigrationPreservesRootPrecedenceAndSurvivesSave(
        string strings, string? assets, string? modernRoot, string expectedRoot)
    {
        File.WriteAllText(AppSettings.SettingsFile, JsonSerializer.Serialize(new
        {
            BaseStringsPath = strings,
            BaseAssetsPath = assets,
            CascDataPath = modernRoot ?? "",
            OutputPath = @"C:\out"
        }));

        var settings = AppSettings.Load();
        Assert.Equal(expectedRoot, settings.CascDataPath);
        Assert.Equal(@"C:\out", settings.OutputPath);
        settings.Save();

        var saved = File.ReadAllText(AppSettings.SettingsFile);
        Assert.DoesNotContain("BaseStringsPath", saved);
        Assert.DoesNotContain("BaseAssetsPath", saved);
        Assert.Equal(expectedRoot, AppSettings.Load().CascDataPath);
    }
}
