// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using dir2site.Services;
using Xunit;

namespace dir2site.Tests;

/// <summary>
/// The published article's column width is stated twice — as <c>.markdown-body</c>'s max-width in
/// the site CSS, and as <c>MarkdownPreviewRenderer.SiteColumnWidth</c>, which reads an authored
/// figure width as a fraction of it. Nothing else makes them agree, and disagreement shows up only
/// as a card thumbnail whose figure is subtly the wrong size, which nobody would think to check.
/// </summary>
public class SiteColumnWidthTests
{
    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "dir2site.sln")))
                return dir.FullName;

        throw new InvalidOperationException("Could not locate the repository root.");
    }

    [Fact]
    public void ThePreviewRenderersColumnMatchesTheSiteCss()
    {
        var css = File.ReadAllText(
            Path.Combine(RepoRoot(), "Assets", "templates", "site-css.html"));

        var match = Regex.Match(css, @"\.article-column\s*\{[^}]*?max-width:\s*(\d+(?:\.\d+)?)px");
        Assert.True(match.Success, "Could not find .article-column's max-width in site-css.html.");

        var fromCss = float.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);

        Assert.Equal(MarkdownPreviewRenderer.SiteColumnWidth, fromCss);
    }
}
