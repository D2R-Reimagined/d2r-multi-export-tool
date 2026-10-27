// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using D2RMultiExport.Client.ViewModels;

namespace D2RMultiExport.Client.Views;

/// <summary>
/// Expanded, non-modal log viewer. Opening it never blocks the export: it is shown with
/// <c>Show</c> (not <c>ShowDialog</c>) and only reads the shared log collection.
/// </summary>
public partial class LogViewerWindow : Window
{
    public LogViewerWindow()
    {
        InitializeComponent();
        KeyDown += OnKeyDown;
        DataContextChanged += (_, _) => Hook();
        Closed += (_, _) => (DataContext as IDisposable)?.Dispose();
    }

    public LogViewerWindow(LogViewerViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }

    private LogViewerViewModel? _hooked;

    private void Hook()
    {
        if (_hooked is not null) _hooked.Refreshed -= OnRefreshed;
        _hooked = DataContext as LogViewerViewModel;
        if (_hooked is not null) _hooked.Refreshed += OnRefreshed;
    }

    private void OnRefreshed(object? sender, EventArgs e)
    {
        if (_hooked?.FollowTail != true) return;
        Dispatcher.UIThread.Post(() =>
        {
            var box = LogText;
            if (box is null) return;
            box.CaretIndex = box.Text?.Length ?? 0;
        }, DispatcherPriority.Background);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
        else if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            SearchBox.Focus();
            e.Handled = true;
        }
    }

    /// <summary>Copies the complete log, independent of filters.</summary>
    public async Task CopyAllAsync()
    {
        if (DataContext is not LogViewerViewModel vm || Clipboard is not { } clipboard) return;
        await clipboard.SetTextAsync(vm.AllText());
        ActionStatus.Text = $"Copied {vm.TotalCount} line(s).";
    }

    /// <summary>Writes the complete log to <paramref name="path"/>.</summary>
    public async Task SaveToAsync(string path)
    {
        if (DataContext is not LogViewerViewModel vm) return;
        await File.WriteAllTextAsync(path, vm.AllText());
        ActionStatus.Text = $"Saved {vm.TotalCount} line(s) to {path}.";
    }

    private async void CopyAll_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            await CopyAllAsync();
        }
        catch (Exception ex)
        {
            ActionStatus.Text = $"Copy failed: {ex.Message}";
        }
    }

    private async void Save_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save pipeline log",
                SuggestedFileName = $"d2r-export-log-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
                DefaultExtension = "txt",
                FileTypeChoices = [new FilePickerFileType("Text file") { Patterns = ["*.txt"] }]
            });
            if (file?.TryGetLocalPath() is { } path)
                await SaveToAsync(path);
        }
        catch (Exception ex)
        {
            ActionStatus.Text = $"Save failed: {ex.Message}";
        }
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
