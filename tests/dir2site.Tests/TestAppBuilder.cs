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
    /// <remarks>
    /// Skia rather than headless drawing, because headless drawing stubs out text and cannot create
    /// a typeface — so anything using the vendored icon font failed here while working in the app,
    /// which is the wrong way round for a test to be wrong. With real drawing the font is genuinely
    /// loaded, and a font this project ships but cannot read fails a test.
    /// </remarks>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
