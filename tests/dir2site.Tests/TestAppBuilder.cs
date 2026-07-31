// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia;
using Avalonia.Headless;
using dir2site;
using dir2site.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace dir2site.Tests;

/// <summary>
/// Boots the real <see cref="App"/> on Avalonia's headless platform so tests get the actual
/// control tree, styling and data binding without a display — which means a binding that doesn't
/// resolve fails a test instead of quietly doing nothing at runtime.
///
/// Safe to start the real App here: it only builds MainWindow (and with it the update-checking
/// view model) under the classic desktop lifetime, which headless does not use.
/// </summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
