// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Specialized;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using D2RMultiExport.Client.ViewModels;

namespace D2RMultiExport.Client.Views;

public partial class MainWindow : Window
{
    private MainWindowViewModel? _hookedVm;

    /// <summary>The open expanded log viewer, if any (one per main window).</summary>
    internal LogViewerWindow? LogViewer { get; private set; }

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        HookLogAutoScroll(DataContext as MainWindowViewModel);
        // handledEventsToo: the ListBox marks pointer presses as handled for selection,
        // and a click anywhere in the log must still open the viewer.
        LogPanel.AddHandler(PointerReleasedEvent, LogPanel_PointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void LogPanel_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left) return;
        if (e.Source is Visual source && OpenLogButton.IsVisualAncestorOf(source)) return;
        OpenLogViewer();
    }

    private void OpenLog_Click(object? sender, RoutedEventArgs e) => OpenLogViewer();

    /// <summary>
    /// Opens (or brings forward) the expanded log viewer. Non-modal, so an active export
    /// keeps running and keeps logging into the same collection the viewer displays.
    /// </summary>
    internal LogViewerWindow? OpenLogViewer()
    {
        if (DataContext is not MainWindowViewModel vm) return null;
        if (LogViewer is { } open)
        {
            open.Activate();
            return open;
        }
        var viewer = new LogViewerWindow(new LogViewerViewModel(vm.LogEntries));
        viewer.Closed += (_, _) => LogViewer = null;
        LogViewer = viewer;
        viewer.Show(this);
        return viewer;
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        HookLogAutoScroll(DataContext as MainWindowViewModel);
    }

    private void HookLogAutoScroll(MainWindowViewModel? vm)
    {
        if (ReferenceEquals(_hookedVm, vm)) return;

        if (_hookedVm is not null)
        {
            _hookedVm.LogEntries.CollectionChanged -= LogLines_CollectionChanged;
        }

        _hookedVm = vm;

        if (_hookedVm is not null)
        {
            _hookedVm.LogEntries.CollectionChanged += LogLines_CollectionChanged;
        }
    }

    private void LogLines_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add) return;

        // Defer to allow the ListBox to materialise the new container before scrolling.
        Dispatcher.UIThread.Post(() =>
        {
            var list = LogListBox;
            if (list?.ItemCount > 0)
            {
                list.ScrollIntoView(list.ItemCount - 1);
            }
        }, DispatcherPriority.Background);
    }

    private async Task<IStorageFolder?> ResolveStartFolderAsync(string? currentPath)
    {
        if (string.IsNullOrWhiteSpace(currentPath)) return null;

        try
        {
            var path = currentPath.Trim();
            // Walk up to the nearest existing directory so the picker can open *somewhere* useful
            // even when the text box points at a not-yet-created folder.
            while (!string.IsNullOrEmpty(path) && !Directory.Exists(path))
            {
                var parent = Path.GetDirectoryName(path);
                if (string.IsNullOrEmpty(parent) || parent == path) return null;
                path = parent;
            }

            if (string.IsNullOrEmpty(path)) return null;

            return await StorageProvider.TryGetFolderFromPathAsync(new Uri(path));
        }
        catch
        {
            return null;
        }
    }

    private async void BrowseModData_Click(object? sender, RoutedEventArgs e)
    {
        var vm = DataContext as MainWindowViewModel;
        var result = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select the Mod Data Directory (the mod's data folder)",
            AllowMultiple = false,
            SuggestedStartLocation = await ResolveStartFolderAsync(vm?.ModDataPath)
        });

        if (result.Count > 0 && vm is not null)
        {
            vm.ModDataPath = result[0].Path.LocalPath;
        }
    }

    private async void BrowseCascData_Click(object? sender, RoutedEventArgs e)
    {
        var vm = DataContext as MainWindowViewModel;
        var result = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select the CASC Data Directory (extracted vanilla data folder)",
            AllowMultiple = false,
            SuggestedStartLocation = await ResolveStartFolderAsync(vm?.CascDataPath)
        });

        if (result.Count > 0 && vm is not null)
        {
            vm.CascDataPath = result[0].Path.LocalPath;
        }
    }

    private async void BrowseOutput_Click(object? sender, RoutedEventArgs e)
    {
        var vm = DataContext as MainWindowViewModel;
        var result = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select Output Files Directory",
            AllowMultiple = false,
            SuggestedStartLocation = await ResolveStartFolderAsync(vm?.OutputPath)
        });

        if (result.Count > 0 && vm is not null)
        {
            vm.OutputPath = result[0].Path.LocalPath;
        }
    }

}
