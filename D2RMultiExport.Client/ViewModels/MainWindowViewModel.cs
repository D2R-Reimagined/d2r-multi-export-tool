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

namespace D2RMultiExport.Client.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly AppSettings _settings;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunPipelineCommand))]
    private string _excelPath;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunPipelineCommand))]
    private string _translationsPath;

    [ObservableProperty]
    private string _baseStringsPath;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunPipelineCommand))]
    private string _outputPath;

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

    /// <summary>
    /// DispatcherTimer that drives the animated trailing-dots suffix on the
    /// progress bar's "NN%" overlay. Started when <see cref="IsBusy"/> turns
    /// true and stopped when it turns false (see <see cref="OnIsBusyChanged"/>).
    /// </summary>
    private readonly DispatcherTimer _dotsTimer;

    private int _dotsPhase;

    /// <summary>
    /// Live log surfaced by the Avalonia view. Bound by <c>MainWindow.axaml</c>
    /// to a scrollable list so the user can watch the pipeline progress and see
    /// every per-phase error or warning as it happens.
    /// </summary>
    public ObservableCollection<string> LogLines { get; } = new();

    public MainWindowViewModel()
    {
        _settings = AppSettings.Load();
        _excelPath = _settings.ExcelPath;
        _translationsPath = _settings.TranslationsPath;
        _baseStringsPath = _settings.BaseStringsPath;
        _outputPath = _settings.OutputPath;
        _prettyPrintJson = _settings.PrettyPrintJson;
        _continueOnException = _settings.ContinueOnException;
        _earlyStopSentinelEnabled = _settings.EarlyStopSentinelEnabled;
        _cubeRecipeUseDescription = _settings.CubeRecipeUseDescription;

        // ~500 ms cadence keeps the dot animation lively without being
        // distracting; the timer is only running while IsBusy is true.
        _dotsTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background, OnDotsTick);
        _dotsTimer.Stop();
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
        _settings.ExcelPath = ExcelPath;
        _settings.TranslationsPath = TranslationsPath;
        _settings.BaseStringsPath = BaseStringsPath;
        _settings.OutputPath = OutputPath;
        _settings.PrettyPrintJson = PrettyPrintJson;
        _settings.ContinueOnException = ContinueOnException;
        _settings.EarlyStopSentinelEnabled = EarlyStopSentinelEnabled;
        _settings.CubeRecipeUseDescription = CubeRecipeUseDescription;
        _settings.Save();
    }

    private void AppendLog(string line)
    {
        // Always marshal to UI thread — IProgress callbacks may fire from the
        // pipeline's worker thread. Avoiding a hard cap keeps the full diagnostic
        // history available; the listbox in the view is virtualised.
        if (Dispatcher.UIThread.CheckAccess())
        {
            LogLines.Add(line);
        }
        else
        {
            Dispatcher.UIThread.Post(() => LogLines.Add(line));
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunPipeline))]
    private async Task RunPipeline()
    {
        IsBusy = true;
        LogLines.Clear();
        StatusText = "Saving settings...";
        SaveSettings();

        ProgressValue = 0;
        var pipeline = new D2RMultiExportPipeline(ExcelPath, TranslationsPath, OutputPath)
        {
            PrettyPrintJson = PrettyPrintJson,
            EarlyStopSentinelEnabled = EarlyStopSentinelEnabled,
            CubeRecipeUseDescription = CubeRecipeUseDescription,
            ContinueOnException = ContinueOnException,
            BaseStringsPath = string.IsNullOrWhiteSpace(BaseStringsPath) ? null : BaseStringsPath,
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

        try
        {
            StatusText = "Running generation pipeline...";
            // Offload to a thread-pool thread. RunAsync mixes async I/O with
            // CPU-bound import/translation/export work that can run for many
            // seconds without yielding; awaiting it directly on the UI thread
            // (even though it is async) starves Avalonia's dispatcher and
            // produces visible UI freezes. Progress<string> was constructed on
            // the UI thread and therefore continues to marshal callbacks back
            // to the dispatcher via the captured SynchronizationContext.
            await Task.Run(() => pipeline.RunAsync());

            if (pipeline.Result.HasErrors)
            {
                StatusText = $"Export finished with {pipeline.Result.AllErrors.Count} issue(s) — see extras/import-report.txt";
                AppendLog(StatusText);
            }
            else
            {
                StatusText = "Export successful!";
                AppendLog(StatusText);
            }
            ProgressValue = 100;
        }
        catch (Exception ex)
        {
            StatusText = $"Aborted: {ex.Message}";
            AppendLog($"[FATAL] {ex}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanRunPipeline()
    {
        return !IsBusy &&
               !string.IsNullOrWhiteSpace(ExcelPath) && Directory.Exists(ExcelPath) &&
               !string.IsNullOrWhiteSpace(TranslationsPath) && Directory.Exists(TranslationsPath) &&
               !string.IsNullOrWhiteSpace(OutputPath) && Directory.Exists(OutputPath);
    }
}
