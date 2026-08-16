// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.IO;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Svg.Skia;
using dir2site.Services;

namespace dir2site.ViewModels;

/// <summary>
/// One tile on the welcome screen: a project the user has opened before, drawn as a miniature of
/// that site's own header — its logo on its navbar color, or, when there is no logo, its title in
/// the color the navbar writes its brand in, exactly as the generated stylesheet would.
///
/// Immutable, and split across two threads on purpose. <see cref="Prepare"/> does the disk work —
/// reading the config, decoding the logo — and returns plain data. <see cref="Create"/> turns that
/// into the tile and must run on the UI thread, because <c>SvgImage</c> derives from
/// <c>AvaloniaObject</c>, whose constructor rejects any other thread. Getting that wrong is
/// silent: the tile throws and the welcome screen simply looks like a first run.
///
/// The brushes cut the other way, and deliberately: <c>ImmutableSolidColorBrush</c> is a plain
/// object rather than an <c>AvaloniaObject</c>, so it can be built on any thread. A plain
/// <c>SolidColorBrush</c> would compile, pass every test here, and throw the moment a tile is
/// built off the UI thread — which is where <see cref="Prepare"/> runs.
/// </summary>
public sealed class RecentProjectItem : IDisposable
{
    /// <summary>Roughly twice the drawn logo height, so it still looks right on a HiDPI screen.</summary>
    private const int LogoDecodeWidth = 512;

    /// <summary>A tile's worth of disk work, done before any UI object exists.</summary>
    /// <param name="Info">The project's title, logo path and header colors.</param>
    /// <param name="Raster">A decoded bitmap logo, if the logo is a raster image.</param>
    /// <param name="Vector">A parsed SVG logo, if it is a vector one.</param>
    public sealed record Prepared(RecentProjectInfo Info, Bitmap? Raster, SvgSource? Vector);

    private readonly IDisposable? _ownedLogo;

    private RecentProjectItem(RecentProjectInfo info, IImage? logo, IDisposable? ownedLogo)
    {
        Path = info.Path;
        Title = info.Title;
        Logo = logo;
        _ownedLogo = ownedLogo;
        Background = BrushFor(info.HeaderBackground, Colors.White);
        Foreground = BrushFor(info.HeaderForeground, Colors.Black);
    }

    /// <summary>Absolute path to the project root; shown as the tile's tooltip.</summary>
    public string Path { get; }

    public string Title { get; }

    /// <summary>The logo, or null when the tile should show <see cref="Title"/>.</summary>
    public IImage? Logo { get; }

    public bool HasLogo => Logo != null;

    /// <summary>The site's navbar color.</summary>
    public IBrush Background { get; }

    /// <summary>The color that navbar draws its brand in.</summary>
    public IBrush Foreground { get; }

    /// <summary>
    /// Reads what a remembered folder needs to draw its tile, or returns null if it shouldn't be
    /// shown. Touches the disk — call it from a background thread.
    /// </summary>
    public static Prepared? Prepare(string projectPath)
    {
        var info = RecentProjectResolver.Resolve(projectPath);
        if (info == null) return null;

        var (raster, vector) = TryLoadLogo(info.LogoPath);
        return new Prepared(info, raster, vector);
    }

    /// <summary>Builds the tile. UI thread only — see the note on the class.</summary>
    public static RecentProjectItem Create(Prepared prepared)
    {
        if (prepared.Vector != null)
        {
            var svg = new SvgImage { Source = prepared.Vector };
            return new RecentProjectItem(prepared.Info, svg, prepared.Vector);
        }

        return new RecentProjectItem(prepared.Info, prepared.Raster, prepared.Raster);
    }

    private static IBrush BrushFor(string color, Color fallback) =>
        new ImmutableSolidColorBrush(Color.TryParse(color, out var parsed) ? parsed : fallback);

    private static (Bitmap? Raster, SvgSource? Vector) TryLoadLogo(string? logoPath)
    {
        if (logoPath == null) return (null, null);
        try
        {
            // Vector logos are common — the header is exactly where a site puts one — and Avalonia
            // has no built-in SVG support, hence Avalonia.Svg.Skia. Parsing is safe off the UI
            // thread; only wrapping the result in an SvgImage is not.
            if (System.IO.Path.GetExtension(logoPath).Equals(".svg", StringComparison.OrdinalIgnoreCase))
                return (null, SvgSource.Load(logoPath, null));

            using var stream = File.OpenRead(logoPath);
            // DecodeToWidth rather than the whole image: a logo saved at print resolution should
            // cost a thumbnail, not hundreds of megabytes across a dozen tiles.
            return (Bitmap.DecodeToWidth(stream, LogoDecodeWidth), null);
        }
        catch
        {
            // Truncated, locked, malformed, or not really the image its extension claims. The
            // title reads better than a blank header.
            return (null, null);
        }
    }

    public void Dispose() => _ownedLogo?.Dispose();
}
