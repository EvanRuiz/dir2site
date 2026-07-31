// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.IO;
using Avalonia;
using dir2site.Models;
using dir2site.Services;
using Xunit;

namespace dir2site.Tests;

/// <summary>
/// Covers the parts of window-geometry persistence that don't need a real window: deciding where a
/// remembered rectangle may be placed, and the JSON round-trip. Whether the platform actually
/// honours the resulting Position is a manual check on Windows and macOS.
/// </summary>
public class WindowGeometryPolicyTests
{
    // A typical primary monitor with a taskbar along the bottom.
    private static readonly PixelRect Primary = new(0, 0, 1920, 1040);
    private static readonly PixelSize MinSize = new(900, 600);

    [Fact]
    public void AWindowAlreadyOnScreenIsLeftAlone()
    {
        var saved = new PixelRect(200, 150, 1280, 820);

        var fitted = WindowGeometryPolicy.Fit(saved, [Primary], MinSize);

        Assert.Equal(saved, fitted);
    }

    [Fact]
    public void AWindowHangingOffTheRightEdgeIsPulledBackIn()
    {
        var fitted = WindowGeometryPolicy.Fit(new PixelRect(1500, 900, 1280, 820), [Primary], MinSize);

        Assert.NotNull(fitted);
        Assert.Equal(1920 - 1280, fitted!.Value.X);
        Assert.Equal(1040 - 820, fitted.Value.Y);
        Assert.Equal(1280, fitted.Value.Width);
    }

    [Fact]
    public void AWindowSavedOnAMonitorThatIsGoneIsRecentredOnTheRemainingOne()
    {
        // Saved on a second monitor to the right that is no longer connected.
        var fitted = WindowGeometryPolicy.Fit(new PixelRect(2400, 300, 1280, 820), [Primary], MinSize);

        Assert.NotNull(fitted);
        Assert.Equal((1920 - 1280) / 2, fitted!.Value.X);
        Assert.Equal((1040 - 820) / 2, fitted.Value.Y);
    }

    [Fact]
    public void AMonitorLeftOfPrimaryKeepsItsNegativeCoordinates()
    {
        var left = new PixelRect(-1920, 0, 1920, 1040);
        var saved = new PixelRect(-1700, 100, 1280, 820);

        var fitted = WindowGeometryPolicy.Fit(saved, [Primary, left], MinSize);

        Assert.Equal(saved, fitted);
    }

    [Fact]
    public void AWindowSpanningTwoMonitorsLandsOnTheOneHoldingMostOfIt()
    {
        var right = new PixelRect(1920, 0, 1920, 1040);
        // 1080 wide on the right monitor, 200 on the primary.
        var saved = new PixelRect(1720, 100, 1280, 820);

        var fitted = WindowGeometryPolicy.Fit(saved, [Primary, right], MinSize);

        Assert.NotNull(fitted);
        Assert.Equal(1920, fitted!.Value.X);
    }

    [Fact]
    public void AWindowBiggerThanItsScreenIsShrunkToFit()
    {
        var small = new PixelRect(0, 0, 1366, 720);

        var fitted = WindowGeometryPolicy.Fit(new PixelRect(0, 0, 2560, 1400), [small], MinSize);

        Assert.Equal(small, fitted);
    }

    [Fact]
    public void ATinySavedSizeIsGrownToTheMinimum()
    {
        var fitted = WindowGeometryPolicy.Fit(new PixelRect(100, 100, 320, 200), [Primary], MinSize);

        Assert.NotNull(fitted);
        Assert.Equal(900, fitted!.Value.Width);
        Assert.Equal(600, fitted.Value.Height);
    }

    [Fact]
    public void AScreenSmallerThanTheMinimumStillWins()
    {
        // The minimum is a preference; running off the edge of the display is not.
        var tiny = new PixelRect(0, 0, 800, 500);

        var fitted = WindowGeometryPolicy.Fit(new PixelRect(0, 0, 1280, 820), [tiny], MinSize);

        Assert.Equal(tiny, fitted);
    }

    [Fact]
    public void AWindowLeftBarelyPeekingOnScreenIsTreatedAsStranded()
    {
        // Only 30px of it overlaps — not enough title bar to grab.
        var fitted = WindowGeometryPolicy.Fit(new PixelRect(1890, 400, 1280, 820), [Primary], MinSize);

        Assert.NotNull(fitted);
        Assert.Equal((1920 - 1280) / 2, fitted!.Value.X);
    }

    [Fact]
    public void WithNoScreensToValidateAgainstNothingIsRestored()
    {
        Assert.Null(WindowGeometryPolicy.Fit(new PixelRect(0, 0, 1280, 820), [], MinSize));
    }

    [Theory]
    [InlineData(0, 820)]
    [InlineData(1280, 0)]
    [InlineData(-100, -100)]
    public void ANonsenseSizeIsRejected(int width, int height)
    {
        Assert.Null(WindowGeometryPolicy.Fit(new PixelRect(0, 0, width, height), [Primary], MinSize));
    }
}

/// <summary>
/// The JSON side. Each test gets its own directory, so nothing here touches the real
/// %AppData%/dir2site/ui or collides with the other test classes xUnit runs in parallel.
/// </summary>
public class WindowGeometryStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "d2s-ui-" + Guid.NewGuid().ToString("N"));

    private WindowGeometryStore Store => new(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static WindowGeometry Sample() => new()
    {
        X = 220, Y = 140, Width = 1280, Height = 820, Scaling = 2.0,
    };

    [Fact]
    public void Load_WhenNothingWasEverSaved_ReturnsNull()
    {
        Assert.Null(Store.Load());
    }

    [Fact]
    public void WhatIsSavedComesBack()
    {
        Store.Save(Sample());

        var loaded = Store.Load();

        Assert.NotNull(loaded);
        Assert.Equal(220, loaded!.X);
        Assert.Equal(140, loaded.Y);
        Assert.Equal(1280, loaded.Width);
        Assert.Equal(820, loaded.Height);
        Assert.Equal(2.0, loaded.Scaling);
    }

    [Fact]
    public void SavingCreatesTheDirectory()
    {
        Assert.False(Directory.Exists(_dir));

        Store.Save(Sample());

        Assert.True(File.Exists(Path.Combine(_dir, "window.json")));
    }

    [Fact]
    public void AHalfWrittenFileIsIgnoredRatherThanThrowing()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "window.json"), "{ \"X\": 220, \"Y\":");

        Assert.Null(Store.Load());
    }

    [Fact]
    public void ARecordFromAnUnknownShapeVersionIsIgnored()
    {
        var future = Sample();
        future.Version = WindowGeometry.CurrentVersion + 1;
        Store.Save(future);

        Assert.Null(Store.Load());
    }

    [Theory]
    [InlineData(0, 820, 1.0)]
    [InlineData(1280, 0, 1.0)]
    [InlineData(1280, 820, 0)]
    public void AnUnusableRecordIsIgnored(double width, double height, double scaling)
    {
        Store.Save(new WindowGeometry { Width = width, Height = height, Scaling = scaling });

        Assert.Null(Store.Load());
    }

    [Fact]
    public void SavingSomewhereUnwritableIsSwallowed()
    {
        // A file where the directory should be — Save must not take the app down with it.
        var blocked = Path.Combine(_dir, "blocked");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(blocked, "not a directory");

        new WindowGeometryStore(blocked).Save(Sample());

        Assert.Null(new WindowGeometryStore(blocked).Load());
    }
}
