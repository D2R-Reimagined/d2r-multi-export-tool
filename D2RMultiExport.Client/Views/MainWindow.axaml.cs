// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Specialized;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using D2RMultiExport.Client.ViewModels;

namespace D2RMultiExport.Client.Views;

public partial class MainWindow : Window
{
    private MainWindowViewModel? _hookedVm;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        HookLogAutoScroll(DataContext as MainWindowViewModel);
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
            _hookedVm.LogLines.CollectionChanged -= LogLines_CollectionChanged;
        }

        _hookedVm = vm;

        if (_hookedVm is not null)
        {
            _hookedVm.LogLines.CollectionChanged += LogLines_CollectionChanged;
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

    private async void BrowseExcel_Click(object? sender, RoutedEventArgs e)
    {
        var vm = DataContext as MainWindowViewModel;
        var result = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select Mod Excel Directory",
            AllowMultiple = false,
            SuggestedStartLocation = await ResolveStartFolderAsync(vm?.ExcelPath)
        });

        if (result.Count > 0 && vm is not null)
        {
            vm.ExcelPath = result[0].Path.LocalPath;
        }
    }

    private async void BrowseTranslations_Click(object? sender, RoutedEventArgs e)
    {
        var vm = DataContext as MainWindowViewModel;
        var result = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select Mod JSON String Directory",
            AllowMultiple = false,
            SuggestedStartLocation = await ResolveStartFolderAsync(vm?.TranslationsPath)
        });

        if (result.Count > 0 && vm is not null)
        {
            vm.TranslationsPath = result[0].Path.LocalPath;
        }
    }

    private async void BrowseBaseStrings_Click(object? sender, RoutedEventArgs e)
    {
        var vm = DataContext as MainWindowViewModel;
        var result = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select CASC Base Strings Directory (optional)",
            AllowMultiple = false,
            SuggestedStartLocation = await ResolveStartFolderAsync(vm?.BaseStringsPath)
        });

        if (result.Count > 0 && vm is not null)
        {
            vm.BaseStringsPath = result[0].Path.LocalPath;
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
