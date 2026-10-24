// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using D2RMultiExport.Client.Models;
using D2RMultiExport.Lib;
using D2RMultiExport.Lib.Exporters;

namespace D2RMultiExport.Client.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly AppSettings _settings;

    /// <summary>The mod's <c>data</c> directory; every mod input is derived from it.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunPipelineCommand))]
    private string _modDataPath;

    /// <summary>The extracted vanilla CASC <c>data</c> directory (optional, strongly recommended).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunPipelineCommand))]
    private string _cascDataPath;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunPipelineCommand))]
    private string _outputPath;

    /// <summary>
    /// Replace all existing contents of <see cref="OutputPath"/> with only the new export
    /// (<see cref="D2RMultiExportPipeline.DestructiveExport"/>). Off by default; remembered.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunPipelineCommand))]
    private bool _destructiveExport;

    /// <summary>What the destructive setting will do, or why it is refused for the current output directory.</summary>
    [ObservableProperty]
    private string _destructiveStatus = "";

    [ObservableProperty]
    private bool _destructiveValid = true;

    /// <summary>
    /// Where the Dry Run button keeps its error log; empty selects
    /// <see cref="DefaultDryRunLogPath"/>. Remembered.
    /// </summary>
    [ObservableProperty]
    private string _dryRunLogPath;

    /// <summary>The dry-run log directory used when <see cref="DryRunLogPath"/> is empty.</summary>
    public string DefaultDryRunLogPath { get; } = DryRunExport.DefaultLogDirectory();

    /// <summary>Live validation result for <see cref="ModDataPath"/> (shown under the field).</summary>
    [ObservableProperty]
    private string _modDataStatus = "";

    [ObservableProperty]
    private bool _modDataValid;

    [ObservableProperty]
    private string _cascDataStatus = "";

    [ObservableProperty]
    private bool _cascDataValid;

    [ObservableProperty]
    private bool _prettyPrintJson;

    [ObservableProperty]
    private bool _continueOnException;

    [ObservableProperty]
    private bool _earlyStopSentinelEnabled;

    [ObservableProperty]
    private bool _cubeRecipeUseDescription;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunPipelineCommand))]
    [NotifyCanExecuteChangedFor(nameof(DryRunPipelineCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = "Idle.";

    /// <summary>
    /// Real percentage [0..100] driven by the pipeline's structured progress
    /// channel (<see cref="PipelineProgress"/>). Bound to a non-indeterminate
    /// <c>ProgressBar</c> in <c>MainWindow.axaml</c>.
    /// </summary>
    [ObservableProperty]
    private double _progressValue;

    /// <summary>
    /// Format string surfaced through the determinate <c>ProgressBar</c>'s
    /// <c>ShowProgressText</c> overlay. <c>{0}</c> is the percentage value.
    /// While a run is active, an animated <c>.</c> / <c>..</c> / <c>...</c>
    /// suffix is appended (and cycled by <see cref="_dotsTimer"/>) so the
    /// "NN%" label visually signals work-in-progress even when the percentage
    /// holds between milestone phases.
    /// </summary>
    [ObservableProperty]
    private string _progressTextFormat = "{0:0}%";

    /// <summary>Counts shown next to the log header so failures are visible without scrolling.</summary>
    [ObservableProperty]
    private string _logSummary = "";

    /// <summary>
    /// DispatcherTimer that drives the animated trailing-dots suffix on the
    /// progress bar's "NN%" overlay. Started when <see cref="IsBusy"/> turns
    /// true and stopped when it turns false (see <see cref="OnIsBusyChanged"/>).
    /// </summary>
    private readonly DispatcherTimer _dotsTimer;

    private int _dotsPhase;
    private int _warningCount;
    private int _errorCount;

    /// <summary>
    /// The complete log of the current (or last) run: every progress line, followed by the
    /// full import report once the run ends, so nothing in <c>extras/import-report.txt</c>
    /// is missing here. The main window shows it live; the expanded viewer reads the same
    /// collection.
    /// </summary>
    public ObservableCollection<LogEntry> LogEntries { get; } = new();

    /// <summary>
    /// Optional override for tests: replaces <see cref="D2RMultiExportPipeline.RunAsync"/> so
    /// an interaction test can drive a controllable "export" through the real view-model.
    /// </summary>
    internal Func<D2RMultiExportPipeline, Task>? RunOverride { get; set; }

    public MainWindowViewModel()
    {
        _settings = AppSettings.Load();
        _modDataPath = _settings.ModDataPath;
        _cascDataPath = _settings.CascDataPath;
        _outputPath = _settings.OutputPath;
        _destructiveExport = _settings.DestructiveExport;
        _dryRunLogPath = _settings.DryRunLogPath;
        _prettyPrintJson = _settings.PrettyPrintJson;
        _continueOnException = _settings.ContinueOnException;
        _earlyStopSentinelEnabled = _settings.EarlyStopSentinelEnabled;
        _cubeRecipeUseDescription = _settings.CubeRecipeUseDescription;
        ValidateModData(_modDataPath);
        ValidateCascData(_cascDataPath);
        ValidateDestructive();

        // ~500 ms cadence keeps the dot animation lively without being
        // distracting; the timer is only running while IsBusy is true.
        _dotsTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background, OnDotsTick);
        _dotsTimer.Stop();
    }

    partial void OnModDataPathChanged(string value)
    {
        ValidateModData(value);
        ValidateDestructive();
    }

    partial void OnCascDataPathChanged(string value)
    {
        ValidateCascData(value);
        ValidateDestructive();
    }

    partial void OnOutputPathChanged(string value) => ValidateDestructive();

    partial void OnDestructiveExportChanged(bool value)
    {
        ValidateDestructive();
        // Remembered immediately, so turning it off sticks even without another export.
        SaveSettings();
    }

    private void ValidateDestructive()
    {
        if (!DestructiveExport)
        {
            DestructiveValid = true;
            DestructiveStatus = "Off: files with matching paths are overwritten, new files are added, and everything else in the Output Files Directory is left untouched.";
            return;
        }
        var danger = string.IsNullOrWhiteSpace(OutputPath)
            ? null
            : DestructiveReplacement.FindDanger(OutputPath, [("the Mod Data Directory", ModDataPath), ("the CASC Data Directory", CascDataPath)]);
        DestructiveValid = danger is null;
        DestructiveStatus = danger is null
            ? "On: after a run without errors, everything else in the Output Files Directory is deleted. A run with errors leaves it unchanged."
            : $"Refused: {danger}";
    }

    private void ValidateModData(string value)
    {
        var result = SourceRoots.ValidateModRoot(value);
        ModDataValid = result.IsValid;
        ModDataStatus = string.IsNullOrWhiteSpace(value)
            ? "Select the mod's data folder (it contains global\\excel and local\\lng\\strings)."
            : result.Message;
    }

    private void ValidateCascData(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            CascDataValid = true;
            CascDataStatus = "Not set: vanilla sprites, base strings and tables the mod does not ship will be missing.";
            return;
        }
        var result = SourceRoots.ValidateCascRoot(value);
        CascDataValid = result.IsValid;
        CascDataStatus = result.Notes.Count == 0 ? result.Message : result.Message + " " + string.Join(" ", result.Notes);
    }

    private void OnDotsTick(object? sender, EventArgs e)
    {
        _dotsPhase = (_dotsPhase + 1) % 3;
        var dots = _dotsPhase switch
        {
            0 => ".",
            1 => "..",
            _ => "..."
        };
        // Pad to 3 chars so the overlay width does not jitter as the dots cycle.
        ProgressTextFormat = "{0:0}% " + dots.PadRight(3, '\u00A0');
    }

    partial void OnIsBusyChanged(bool value)
    {
        if (value)
        {
            _dotsPhase = 0;
            ProgressTextFormat = "{0:0}% .\u00A0\u00A0";
            _dotsTimer.Start();
        }
        else
        {
            _dotsTimer.Stop();
            ProgressTextFormat = "{0:0}%";
        }
    }

    public void SaveSettings()
    {
        _settings.ModDataPath = ModDataPath;
        _settings.CascDataPath = CascDataPath;
        _settings.OutputPath = OutputPath;
        _settings.DestructiveExport = DestructiveExport;
        _settings.DryRunLogPath = DryRunLogPath;
        _settings.PrettyPrintJson = PrettyPrintJson;
        _settings.ContinueOnException = ContinueOnException;
        _settings.EarlyStopSentinelEnabled = EarlyStopSentinelEnabled;
        _settings.CubeRecipeUseDescription = CubeRecipeUseDescription;
        _settings.Save();
    }

    internal void AppendLog(string line)
    {
        // Always marshal to UI thread — IProgress callbacks may fire from the
        // pipeline's worker thread. Avoiding a hard cap keeps the full diagnostic
        // history available; the listbox in the view is virtualised.
        if (Dispatcher.UIThread.CheckAccess())
        {
            AddEntry(line);
        }
        else
        {
            Dispatcher.UIThread.Post(() => AddEntry(line));
        }
    }

    private void AddEntry(string line)
    {
        var entry = LogEntry.From(line);
        if (entry.Level == LogLevel.Warning) _warningCount++;
        if (entry.Level == LogLevel.Error) _errorCount++;
        LogEntries.Add(entry);
        LogSummary = $"{LogEntries.Count} line(s), {_errorCount} error(s), {_warningCount} warning(s) — click the log to expand";
    }

    private void ClearLog()
    {
        LogEntries.Clear();
        _warningCount = 0;
        _errorCount = 0;
        LogSummary = "";
    }

    [RelayCommand(CanExecute = nameof(CanRunPipeline))]
    private Task RunPipeline() => RunAsync(dryRun: false);

    /// <summary>
    /// Runs the full export into a discarded scratch directory; only a log of the recorded errors
    /// and warnings is kept in <see cref="DryRunLogPath"/>, when there is one. The Output Files
    /// Directory is never touched, so it need not be set.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanDryRunPipeline))]
    private Task DryRunPipeline() => RunAsync(dryRun: true);

    private async Task RunAsync(bool dryRun)
    {
        IsBusy = true;
        ClearLog();
        StatusText = "Saving settings...";
        SaveSettings();

        ProgressValue = 0;
        D2RMultiExportPipeline pipeline;
        try
        {
            // A new pipeline (and so a new SourceRoots) per run: the roots are re-validated
            // from the current fields every time and no source mapping survives a run.
            pipeline = new D2RMultiExportPipeline(ModDataPath, string.IsNullOrWhiteSpace(CascDataPath) ? null : CascDataPath,
                dryRun ? null : OutputPath)
            {
                PrettyPrintJson = PrettyPrintJson,
                EarlyStopSentinelEnabled = EarlyStopSentinelEnabled,
                CubeRecipeUseDescription = CubeRecipeUseDescription,
                ContinueOnException = ContinueOnException,
                DestructiveExport = !dryRun && DestructiveExport,
                DryRun = dryRun,
                DryRunLogDirectory = string.IsNullOrWhiteSpace(DryRunLogPath) ? null : DryRunLogPath,
                Progress = new Progress<string>(line =>
                {
                    StatusText = line;
                    AppendLog(line);
                }),
                // Constructed on the UI thread so the captured SynchronizationContext
                // marshals callbacks back to the dispatcher even though the pipeline
                // itself runs on a thread-pool thread (see Task.Run below).
                StructuredProgress = new Progress<PipelineProgress>(p =>
                {
                    ProgressValue = p.Percent;
                })
            };
        }
        catch (SourceLayoutException ex)
        {
            StatusText = ex.Message;
            AppendLog($"[ERROR] {ex.Message}");
            IsBusy = false;
            return;
        }

        try
        {
            StatusText = dryRun ? "Running dry run (the export will be discarded)..." : "Running generation pipeline...";
            // Offload to a thread-pool thread. RunAsync mixes async I/O with
            // CPU-bound import/translation/export work that can run for many
            // seconds without yielding; awaiting it directly on the UI thread
            // (even though it is async) starves Avalonia's dispatcher and
            // produces visible UI freezes. Progress<string> was constructed on
            // the UI thread and therefore continues to marshal callbacks back
            // to the dispatcher via the captured SynchronizationContext.
            var run = RunOverride;
            await Task.Run(() => run is null ? pipeline.RunAsync() : run(pipeline));

            if (dryRun)
            {
                var findings = $"{pipeline.Result.WarningCount} warning(s), {pipeline.ReferencedMissingKeyCount} missing translation key(s)";
                var log = DescribeDryRunLog(pipeline);
                StatusText = pipeline.Result.HasErrors
                    ? $"Dry run finished with {pipeline.Result.ErrorCount} error(s), {findings}. The export was discarded.{log}"
                    : $"Dry run finished without errors ({findings}). The export was discarded.{log}";
            }
            else if (pipeline.Result.HasErrors)
            {
                StatusText = pipeline.DestructiveExport
                    ? $"Export finished with {pipeline.Result.AllErrors.Count} issue(s) — see the log. The Output Files Directory was not replaced."
                    : $"Export finished with {pipeline.Result.AllErrors.Count} issue(s) — see extras/import-report.txt";
            }
            else
            {
                StatusText = pipeline.DestructiveExport
                    ? "Export successful! The Output Files Directory now holds only this export."
                    : "Export successful!";
            }
            ProgressValue = 100;
        }
        catch (Exception ex)
        {
            StatusText = !dryRun
                ? $"Aborted: {ex.Message}"
                : $"Dry run aborted: {ex.Message} — the export was discarded.{DescribeDryRunLog(pipeline)}";
            AppendLog($"[FATAL] {ex}");
        }
        finally
        {
            // Progress<T> posts asynchronously; let queued lines land before the report.
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            AppendLog("");
            // One log entry per diagnostic, so a warning's detail lines filter with it.
            foreach (var line in pipeline.Result.GenerateReportEntries())
                AppendLog(line);
            AppendLog(StatusText);
            IsBusy = false;
        }
    }

    /// <summary>Where the last dry run wrote its error and warning log, or that it wrote none.</summary>
    private static string DescribeDryRunLog(D2RMultiExportPipeline pipeline) =>
        pipeline.DryRunLogFilePath is { } log && File.Exists(log)
            ? $" Log: {log}."
            : " No log was written.";

    private bool CanRunPipeline()
    {
        return !IsBusy
               && ModDataValid
               && CascDataValid
               && DestructiveValid
               && !string.IsNullOrWhiteSpace(OutputPath) && Directory.Exists(OutputPath);
    }

    /// <summary>A dry run needs valid sources only: it never writes to the Output Files Directory.</summary>
    private bool CanDryRunPipeline() => !IsBusy && ModDataValid && CascDataValid;

    partial void OnModDataValidChanged(bool value)
    {
        RunPipelineCommand.NotifyCanExecuteChanged();
        DryRunPipelineCommand.NotifyCanExecuteChanged();
    }

    partial void OnCascDataValidChanged(bool value)
    {
        RunPipelineCommand.NotifyCanExecuteChanged();
        DryRunPipelineCommand.NotifyCanExecuteChanged();
    }

    partial void OnDestructiveValidChanged(bool value) => RunPipelineCommand.NotifyCanExecuteChanged();
}
