// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace dir2site.Tests;

/// <summary>
/// An authored <c>{width=420}</c> used to render at 166px in an 820px column, because two separate
/// <c>max-width: 45%</c> rules clamped it in turn: one on the floated figure, and one written for
/// the <c>:::figure-left</c> container that also matched an image carrying the class — which is
/// where the <c>^^^</c> form puts it — and outranked <c>figure img { max-width: 100% }</c>. The
/// caption then sat at the figure's full width, so the picture looked narrower than its own text.
///
/// Neither clamp is visible from reading a rule on its own, and nothing else here lays out a page,
/// so these pin the two shapes that let the width through. A layout check needs a browser; what
/// can be checked cheaply is that the clamps have not crept back.
/// </summary>
public class FigureWidthCssTests
{
    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "dir2site.sln")))
                return dir.FullName;

        throw new InvalidOperationException("Could not locate the repository root.");
    }

    private static string SiteCss() => Strip(File.ReadAllText(
        Path.Combine(RepoRoot(), "Assets", "templates", "site-css.html")));

    private static string ExtensionCss() => Strip(File.ReadAllText(
        Path.Combine(RepoRoot(), "editors", "vscode-dir2site-figures", "media", "dir2site-figures.css")));

    /// Comments explain these very rules, so they have to go before anything is matched.
    private static string Strip(string css) =>
        Regex.Replace(css, @"/\*.*?\*/", " ", RegexOptions.Singleline);

    [Theory]
    [InlineData("site")]
    [InlineData("extension")]
    public void AFigureWithAnAuthoredWidth_IsNotCappedAtAFractionOfTheColumn(string which)
    {
        var css = which == "site" ? SiteCss() : ExtensionCss();

        var match = Regex.Match(css, @"figure:has\(img\[width\]\)[^{}]*\{([^{}]*)\}");
        Assert.True(match.Success,
            $"The {which} CSS has no figure:has(img[width]) rule, so a 45% guard clamps an authored width.");
        Assert.Matches(@"max-width:\s*100%", match.Groups[1].Value);
    }

    /// <summary>
    /// Order decides between these: the guard and the override have equal specificity.
    /// </summary>
    [Theory]
    [InlineData("site")]
    [InlineData("extension")]
    public void TheAuthoredWidthOverride_ComesAfterTheGuardItOverrides(string which)
    {
        var css = which == "site" ? SiteCss() : ExtensionCss();

        var guard = css.LastIndexOf("figure:has(img.figure-left)", StringComparison.Ordinal);
        var over = css.IndexOf("figure:has(img[width])", StringComparison.Ordinal);

        Assert.True(guard >= 0 && over > guard,
            $"In the {which} CSS the authored-width rule must follow figure:has(img.figure-left), "
            + "or the 45% guard wins on source order and the width is clamped again.");
    }

    [Theory]
    [InlineData("site")]
    [InlineData("extension")]
    public void TheContainerRules_DoNotAlsoMatchAnImageCarryingTheClass(string which)
    {
        var css = which == "site" ? SiteCss() : ExtensionCss();

        foreach (Match rule in Regex.Matches(css, @"(?<selector>[^{}]+)\{(?<body>[^{}]*)\}"))
        {
            var selector = Regex.Replace(rule.Groups["selector"].Value, @"\s+", " ").Trim();

            // Only the bare container selectors are at issue: `figure:has(…)` and descendant
            // selectors like `.figure-left img` cannot match the image itself.
            if (!Regex.IsMatch(selector, @"(^|[\s,])\.figure-(left|right|center)\b")) continue;

            Assert.False(Regex.IsMatch(rule.Groups["body"].Value, @"max-width:\s*45%"),
                $"In the {which} CSS, \"{selector}\" matches an <img> carrying the class as well as "
                + "a container, and clamps it to 45% of the figure. Scope it with :not(img).");
        }
    }
}
