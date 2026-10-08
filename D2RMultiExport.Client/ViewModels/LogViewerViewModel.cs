// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace D2RMultiExport.Client.ViewModels;

/// <summary>
/// Backs the expanded log window. It reads the main window's live <see cref="LogEntry"/>
/// collection (never a copy), so opening it mid-export shows everything logged so far and
/// keeps following new lines; after the run it still holds the complete log. Filtering and
/// search only change what is shown — Copy All and Save always use the complete log.
/// </summary>
public partial class LogViewerViewModel : ViewModelBase, IDisposable
{
    private readonly ObservableCollection<LogEntry> _source;
    private readonly DispatcherTimer _refreshTimer;

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private bool _showInfo = true;

    [ObservableProperty]
    private bool _showWarnings = true;

    [ObservableProperty]
    private bool _showErrors = true;

    [ObservableProperty]
    private bool _followTail = true;

    [ObservableProperty]
    private string _visibleText = "";

    [ObservableProperty]
    private string _summary = "";

    public LogViewerViewModel(ObservableCollection<LogEntry> source)
    {
        _source = source;
        _source.CollectionChanged += OnSourceChanged;
        // Coalesce bursts of log lines into one refresh so a busy export never floods the UI.
        _refreshTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) =>
        {
            _refreshTimer!.Stop();
            Refresh();
        });
        Refresh();
    }

    public int TotalCount => _source.Count;

    public int VisibleCount { get; private set; }

    public int WarningCount => _source.Count(static e => e.Level == LogLevel.Warning);

    public int ErrorCount => _source.Count(static e => e.Level == LogLevel.Error);

    /// <summary>Raised after <see cref="VisibleText"/> changes, so the view can follow the tail.</summary>
    public event EventHandler? Refreshed;

    partial void OnSearchTextChanged(string value) => Refresh();
    partial void OnShowInfoChanged(bool value) => Refresh();
    partial void OnShowWarningsChanged(bool value) => Refresh();
    partial void OnShowErrorsChanged(bool value) => Refresh();

    private void OnSourceChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!_refreshTimer.IsEnabled) _refreshTimer.Start();
    }

    public bool IsVisible(LogEntry entry)
    {
        var levelShown = entry.Level switch
        {
            LogLevel.Error => ShowErrors,
            LogLevel.Warning => ShowWarnings,
            _ => ShowInfo
        };
        return levelShown
               && (string.IsNullOrEmpty(SearchText)
                   || entry.Text.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Rebuilds the shown text from the current filters (also called by tests).</summary>
    public void Refresh()
    {
        var builder = new StringBuilder();
        var visible = 0;
        foreach (var entry in _source.ToArray())
        {
            if (!IsVisible(entry)) continue;
            builder.Append(entry.Text).Append('\n');
            visible++;
        }
        VisibleCount = visible;
        VisibleText = builder.ToString();
        Summary = $"{visible} of {TotalCount} line(s) shown — {ErrorCount} error(s), {WarningCount} warning(s)";
        Refreshed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The complete accumulated log, regardless of filters.</summary>
    public string AllText() => string.Join(Environment.NewLine, _source.ToArray().Select(static e => e.Text)) + Environment.NewLine;

    public void Dispose()
    {
        _refreshTimer.Stop();
        _source.CollectionChanged -= OnSourceChanged;
        GC.SuppressFinalize(this);
    }
}
