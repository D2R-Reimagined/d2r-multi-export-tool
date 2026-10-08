// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Headless;
using D2RMultiExport.Client.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace D2RMultiExport.Client.Tests;

/// <summary>Boots the real <see cref="App"/> (styles, theme, XAML) on the headless platform.</summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
