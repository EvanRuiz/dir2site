// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia;
using Avalonia.Headless;
using dir2site;
using dir2site.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

// One Application for the whole assembly, rather than tearing it down and rebuilding it around
// every test. The default costs correctness, not just time: work still queued on the dispatcher
// when a test ends runs against services that test has already disposed, and the failure surfaces
// somewhere else entirely — a later test asking the font manager for a typeface and finding the
// system font collection gone. https://github.com/AvaloniaUI/Avalonia/discussions/18867
//
// The trade is that state now outlives a test, so anything a test leaves behind is the next test's
// problem. That is the cost of not rebuilding the world 355 times.
[assembly: AvaloniaTestIsolation(AvaloniaTestIsolationLevel.PerAssembly)]

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
