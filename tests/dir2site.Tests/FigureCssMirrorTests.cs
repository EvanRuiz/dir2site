// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace dir2site.Tests;

/// <summary>
/// The VS Code extension's stylesheet mirrors the site's figure rules by hand, so that the preview
/// an author writes against matches the page a reader gets. Nothing made the two agree — the
/// extension's own header just asks whoever edits one to remember the other — and a drift shows up
/// as a figure that looks right while writing and wrong once published.
///
/// Only the <c>figure</c> rules are compared: the container (<c>:::figure-*</c>) and caption
/// typography rules differ on purpose, the extension taking its colours from VS Code theme
/// variables so captions stay readable in a dark theme.
///
/// When one of these fails, copy the changed rule across — <c>Assets/templates/site-css.html</c>
/// and <c>editors/vscode-dir2site-figures/media/dir2site-figures.css</c> — then repackage with
/// <c>scripts/package-vscode-extension.sh</c>.
/// </summary>
public class FigureCssMirrorTests
{
    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "dir2site.sln")))
                return dir.FullName;

        throw new InvalidOperationException("Could not locate the repository root.");
    }

    /// <summary>
    /// Every rule whose selector list starts with <c>figure</c>, as "selector { declarations }"
    /// with runs of whitespace collapsed and the site's <c>.markdown-body</c> scope removed, so the
    /// two files are comparable. Comments are stripped first — they are prose, and differ.
    /// </summary>
    private static List<string> FigureRules(string css)
    {
        css = Regex.Replace(css, @"/\*.*?\*/", " ", RegexOptions.Singleline);

        var rules = new List<string>();
        foreach (Match rule in Regex.Matches(css, @"([^{}]+)\{([^{}]*)\}"))
        {
            var selector = Normalise(rule.Groups[1].Value).Replace(".markdown-body ", "");
            if (!selector.StartsWith("figure", StringComparison.Ordinal)) continue;

            rules.Add($"{selector} {{ {Normalise(rule.Groups[2].Value)} }}");
        }
        return rules;

        static string Normalise(string s) => Regex.Replace(s, @"\s+", " ").Trim();
    }

    [Fact]
    public void TheExtensionsFigureRulesMatchTheSites()
    {
        var site = FigureRules(File.ReadAllText(
            Path.Combine(RepoRoot(), "Assets", "templates", "site-css.html")));
        var extension = FigureRules(File.ReadAllText(
            Path.Combine(RepoRoot(), "editors", "vscode-dir2site-figures", "media", "dir2site-figures.css")));

        // Guards the guard: a selector rename on both sides at once would otherwise pass by
        // comparing nothing at all.
        Assert.NotEmpty(site);
        Assert.Equal(string.Join("\n", site), string.Join("\n", extension));
    }
}
