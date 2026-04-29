// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using D2RMultiExport.Client.ViewModels;
using D2RMultiExport.Client.Views;

namespace D2RMultiExport.Client;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Avalonia 12 removed the DataAnnotations binding plugin from the default
            // pipeline (BindingPlugins / DataAnnotationsValidationPlugin are gone), so the
            // CommunityToolkit.Mvvm + INotifyDataErrorInfo path no longer double-reports
            // validation. The previous DisableAvaloniaDataAnnotationValidation() workaround
            // is intentionally not carried over.
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
