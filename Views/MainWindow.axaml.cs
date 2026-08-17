// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using dir2site.Models;
using dir2site.Services;
using dir2site.ViewModels;

namespace dir2site.Views;

public partial class MainWindow : Window
{
    private readonly WindowGeometryStore _geometryStore = WindowGeometryStore.Default;

    /// <summary>
    /// The last bounds seen while the window was in its <i>normal</i> state, and the scaling that
    /// was in force at the time. Tracked continuously rather than read at shutdown because by then
    /// a maximized window reports its maximized frame (on Win32, an origin of -8,-8) and a
    /// minimized one reports the -32000,-32000 sentinel — neither is what the user wants back.
    /// </summary>
    private PixelRect? _normalBounds;
    private double _normalScaling = 1.0;

    /// <summary>Where <see cref="RestoreGeometry"/> decided to put us, or null on a first run.</summary>
    private PixelPoint? _restoredPosition;

    public MainWindow()
    {
        InitializeComponent();

        DataContextChanged += (sender, args) =>
        {
            if(DataContext is MainWindowViewModel viewModel)
            {
                viewModel.TopLevel = GetTopLevel(this);
                viewModel.StartWatching();

                // The real prompt, supplied by the window that can parent it. The view model's own
                // default declines, which is what a headless host should do rather than pretending
                // someone answered.
                viewModel.AskAboutOrphans = async orphans =>
                    await new OrphanFilesView(orphans).ShowDialog<IReadOnlyList<string>?>(this);
            }
        };

        RestoreGeometry();
        Opened += OnFirstOpened;
    }

    /// <summary>
    /// Writes the current geometry out. Safe to call more than once — both <c>Closing</c> here and
    /// <c>ShutdownRequested</c> in <see cref="App"/> do, because neither hook covers every quit
    /// path on its own.
    /// </summary>
    public void SaveGeometry()
    {
        if (_normalBounds is not { } bounds) return;

        _geometryStore.Save(new WindowGeometry
        {
            X = bounds.X,
            Y = bounds.Y,
            Width = bounds.Width / _normalScaling,
            Height = bounds.Height / _normalScaling,
            Scaling = _normalScaling,
        });
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        SaveGeometry();

        // Watching was started from here, so it is stopped from here — a filesystem watcher left
        // running would go on posting to a dispatcher that is on its way out.
        if (DataContext is MainWindowViewModel viewModel) viewModel.StopWatching();

        base.OnClosing(e);
    }

    /// <summary>
    /// Applies the saved geometry before the window is ever shown, so there is no visible jump from
    /// the default placement to the remembered one.
    /// </summary>
    private void RestoreGeometry()
    {
        var saved = _geometryStore.Load();
        if (saved == null)
        {
            // First run: keep the XAML default size, but put it somewhere sensible.
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return;
        }

        var scaling = saved.Scaling > 0 ? saved.Scaling : 1.0;
        var savedRect = new PixelRect(
            saved.X,
            saved.Y,
            (int)Math.Round(saved.Width * scaling),
            (int)Math.Round(saved.Height * scaling));

        var fitted = WindowGeometryPolicy.Fit(savedRect, WorkAreas(), MinimumPixelSize(scaling));
        if (fitted == null)
        {
            // No screens to validate against — a centred default is always reachable, an
            // unvalidated position is not.
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return;
        }

        Apply(fitted.Value, scaling);
    }

    private void OnFirstOpened(object? sender, EventArgs e)
    {
        Opened -= OnFirstOpened;

        if (_restoredPosition is { } position)
        {
            // Re-assert the position: on Win32 the DWM can override a pre-show move at first paint.
            Position = position;

            // Now that Screens and DesktopScaling are trustworthy, do the real clamp — this is what
            // catches landing on a monitor with a different scale factor than the one we saved on.
            var scaling = CurrentScaling;
            var fitted = WindowGeometryPolicy.Fit(CurrentBounds(), WorkAreas(), MinimumPixelSize(scaling));
            if (fitted != null) Apply(fitted.Value, scaling);
        }

        _normalScaling = CurrentScaling;
        _normalBounds = CurrentBounds();

        PositionChanged += OnWindowPositionChanged;
        Resized += OnWindowResized;
    }

    private void OnWindowPositionChanged(object? sender, PixelPointEventArgs e) => TrackNormalBounds();

    private void OnWindowResized(object? sender, WindowResizedEventArgs e)
    {
        // A DPI change resizes the window without the user asking for it; recording that would
        // persist a size they never chose.
        if (e.Reason == WindowResizeReason.DpiChange) return;
        TrackNormalBounds();
    }

    private void TrackNormalBounds()
    {
        if (WindowState != WindowState.Normal) return;
        _normalScaling = CurrentScaling;
        _normalBounds = CurrentBounds();
    }

    private void Apply(PixelRect rect, double scaling)
    {
        // Anything other than Manual makes Avalonia reposition at show time and silently discard
        // whatever we set here.
        WindowStartupLocation = WindowStartupLocation.Manual;
        Width = rect.Width / scaling;
        Height = rect.Height / scaling;
        _restoredPosition = new PixelPoint(rect.X, rect.Y);
        Position = _restoredPosition.Value;
    }

    private double CurrentScaling => DesktopScaling > 0 ? DesktopScaling : 1.0;

    private PixelRect CurrentBounds()
    {
        var scaling = CurrentScaling;
        return new PixelRect(
            Position.X,
            Position.Y,
            (int)Math.Round(ClientSize.Width * scaling),
            (int)Math.Round(ClientSize.Height * scaling));
    }

    private PixelSize MinimumPixelSize(double scaling) => new(
        (int)Math.Round(MinWidth * scaling),
        (int)Math.Round(MinHeight * scaling));

    /// <summary>Screen work areas with the primary first, or empty when the platform won't say.</summary>
    private IReadOnlyList<PixelRect> WorkAreas()
    {
        try
        {
            var screens = Screens;
            if (screens?.All is not { Count: > 0 } all) return Array.Empty<PixelRect>();

            var areas = new List<PixelRect>(all.Count);
            foreach (var screen in all)
            {
                if (screen.IsPrimary)
                    areas.Insert(0, screen.WorkingArea);
                else
                    areas.Add(screen.WorkingArea);
            }

            return areas;
        }
        catch
        {
            // Headless, or a backend without screen details.
            return Array.Empty<PixelRect>();
        }
    }
}
