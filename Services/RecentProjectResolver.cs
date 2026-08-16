// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.IO;
using System.Linq;
using Avalonia.Media;
using dir2site.Models;

namespace dir2site.Services;

/// <summary>What a remembered folder should say on its welcome-screen tile.</summary>
/// <param name="Path">Absolute path to the project root; also the tile's tooltip.</param>
/// <param name="Title">Never empty — the site's title, or the folder name, or the path.</param>
/// <param name="LogoPath">Absolute path to a displayable logo, or null to show the title instead.</param>
/// <param name="HeaderBackground">The site header's background color, as CSS writes it.</param>
/// <param name="HeaderForeground">The color the site header draws its brand in.</param>
public sealed record RecentProjectInfo(
    string Path, string Title, string? LogoPath, string HeaderBackground, string HeaderForeground);

/// <summary>
/// Turns a remembered folder into the title and logo its tile should show, reading the project's
/// own <c>dir2site.yaml</c> each time so a renamed site or a swapped logo is reflected at once.
///
/// This is deliberately read-only. <c>MainWindowViewModel.LoadOrCreateDir2SiteConfig</c> writes a
/// <c>dir2site.yaml</c> into any folder it opens; merely listing the welcome screen must never do
/// that, or a folder the user has since repurposed would silently be made a project again.
/// </summary>
public static class RecentProjectResolver
{
    /// <summary>The project marker, and the same filename the view model opens.</summary>
    private const string ConfigFileName = "dir2site.yaml";

    /// <summary>Everything the logo picker offers, which the tiles can all now draw.</summary>
    private static readonly string[] DisplayableLogoExtensions =
        [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".svg"];

    /// <summary>The header background when a project asks for a light navbar.</summary>
    private const string LightNavbar = "#ffffff";

    /// <summary>The brand color on a dark navbar.</summary>
    private const string DarkNavbarText = "#ffffff";

    /// <summary>
    /// The tile for <paramref name="projectPath"/>, or null if it should not be shown — the folder
    /// is gone (deleted, renamed, or on an unmounted volume) or is no longer a dir2site project.
    /// </summary>
    public static RecentProjectInfo? Resolve(string projectPath)
    {
        var root = RecentProjectsStore.Normalize(projectPath);
        if (root == null) return null;

        try
        {
            if (!Directory.Exists(root)) return null;

            var configPath = Path.Combine(root, ConfigFileName);
            if (!File.Exists(configPath)) return null;

            var config = TryReadConfig(configPath);

            // The same rule the generated stylesheet uses, so a tile reads as that site's header:
            // a dark navbar is painted in the primary color with white on it, a light one is white
            // with the primary color on it.
            var primary = PrimaryColorFor(config);
            var dark = config?.NavbarDark ?? new Dir2SiteModel().NavbarDark;

            return new RecentProjectInfo(
                root,
                TitleFor(root, config),
                LogoFor(root, config),
                HeaderBackground: dark ? primary : LightNavbar,
                HeaderForeground: dark ? DarkNavbarText : primary);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The project's config, or null when it can't be read. A project whose yaml is corrupt or
    /// locked still gets a tile — the folder is plainly a project, we just can't read its details.
    /// </summary>
    private static Dir2SiteModel? TryReadConfig(string configPath)
    {
        try
        {
            return YamlParser.DeserializeAs<Dir2SiteModel>(File.ReadAllText(configPath));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The same fallback chain <c>LoadOrCreateDir2SiteConfig</c> uses, with the path as a last
    /// resort so a drive root never produces a blank tile.
    /// </summary>
    private static string TitleFor(string root, Dir2SiteModel? config)
    {
        if (config != null && !string.IsNullOrWhiteSpace(config.Title)) return config.Title;
        return Path.GetFileName(root) is { Length: > 0 } name ? name : root;
    }

    /// <summary>
    /// The project's primary color, falling back to the model default when it is unset or isn't a
    /// color. The generator warns about a bad value; a tile just draws the color the site would.
    /// </summary>
    private static string PrimaryColorFor(Dir2SiteModel? config)
    {
        var fallback = new Dir2SiteModel().PrimaryColor;
        var color = (config?.PrimaryColor ?? string.Empty).Trim();
        return color.Length > 0 && Color.TryParse(color, out _) ? color : fallback;
    }

    private static string? LogoFor(string root, Dir2SiteModel? config)
    {
        if (config == null || string.IsNullOrWhiteSpace(config.Logo)) return null;

        try
        {
            var logoPath = Path.GetFullPath(Path.Combine(root, config.Logo));

            // The logo is recorded relative to the project root (see ChooseLogo). A hand-written
            // "../../.ssh/id_rsa" shouldn't get us to open an arbitrary file.
            if (!IsInside(root, logoPath)) return null;

            if (!File.Exists(logoPath)) return null;

            // And what the path leads to, not only how it is spelled: a logo.png sitting in the
            // project can be a symlink to somewhere else entirely, which reads as contained.
            if (!IsInside(root, RealPathOf(logoPath))) return null;

            var extension = Path.GetExtension(logoPath);
            if (!DisplayableLogoExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
                return null;

            return logoPath;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsInside(string root, string path) =>
        path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    /// <summary>
    /// Where a path actually lands once links are followed, or the path itself when it is not a
    /// link (or the link cannot be resolved, which is treated as "not somewhere we trust").
    /// </summary>
    private static string RealPathOf(string path) =>
        new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? path;
}
