// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia.Headless.XUnit;
using Avalonia.Platform;
using Xunit;

namespace dir2site.Tests;

/// <summary>
/// Every value a template drops into a page lands somewhere with a syntax of its own — an HTML
/// attribute, a javascript string, a CSS declaration — and each of those has a different way of
/// being ended early by a folder named with a quote or a color typed with a semicolon. Fixing them
/// as they were found took three passes; this is what makes the fourth unnecessary. It reads the
/// templates themselves and fails on any interpolation that isn't guarded for where it sits, so a
/// new one has to say what it is rather than being noticed later.
/// </summary>
public class TemplateInterpolationAuditTests
{
    /// <summary>
    /// Values written raw on purpose, and why. Being here is the decision; anything not here and
    /// not guarded is an oversight, which is the whole distinction the audit exists to draw.
    /// </summary>
    private static readonly Dictionary<string, string> DeliberatelyRaw = new()
    {
        ["artifact.html_content"] = "Markdown rendered to HTML — being HTML is the point of it.",
        ["intro_html"] = "A folder's index.md rendered to HTML, for the same reason as an article's.",
        ["site.footer"] = "The footer is documented as markup the author writes; FooterTests pins it.",
        ["item.badge_icon"] = "A Bootstrap Icons class this generator chose, never anything a project supplies.",
        ["$2"] = "badge.html's icon parameter, which is item.badge_icon under another name.",
        ["prefix"] = "\"../\" repeated by RelativePrefix. Nothing a project can influence reaches it.",
        ["artifact.bookreader_data"] = "JSON from System.Text.Json, whose default encoder escapes < > and &.",
        ["site.footer_columns.size"] = "A count.",
    };

    /// <summary>
    /// Values allowed into a CSS declaration. There is no escape that leaves a color meaning what
    /// it says, so each of these is checked against an allow-list in C# instead — see
    /// SiteGenerator.SanitizeSiteColors.
    /// </summary>
    private static readonly HashSet<string> CheckedInCsharp =
    [
        "site.primary_color", "site.secondary_color", "site.background_color", "site.footer_color",
    ];

    private static readonly string[] Templates =
    [
        "artifact-default", "artifact-link", "artifact-markdown", "artifact-nav", "artifact-pdf",
        "artifact-photo", "artifact-subtitle", "badge", "card", "collection", "footer", "header",
        "opengraph", "site-css",
    ];

    private static string Read(string name)
    {
        using var stream = AssetLoader.Open(
            new Uri($"avares://dir2site/Assets/templates/{name}.html"));
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Control flow and comments say nothing to the page, so they are not interpolations.</summary>
    private static bool IsCode(string expression) =>
        expression.StartsWith('#') ||
        Regex.IsMatch(expression, @"^(if|else|end|for|include|while|when|case|capture|ret)\b");

    /// <summary>Which of the three syntaxes the character at this offset sits inside.</summary>
    private static string ContextAt(string template, string name, int index)
    {
        if (name == "site-css") return "css";

        var open = template.LastIndexOf("<script", index, StringComparison.Ordinal);
        if (open < 0) return "html";
        var close = template.LastIndexOf("</script", index, StringComparison.Ordinal);
        return close > open ? "html" : "js";
    }

    [AvaloniaFact]
    public void EveryInterpolationIsGuardedForWhereItLands()
    {
        var unguarded = new List<string>();

        foreach (var name in Templates)
        {
            var template = Read(name);
            foreach (Match match in Regex.Matches(template, @"\{\{(.*?)\}\}", RegexOptions.Singleline))
            {
                var expression = match.Groups[1].Value.Trim().Trim('-', '~').Trim();
                if (expression.Length == 0 || IsCode(expression)) continue;

                // An expression can hold more than one value: "{{ a }}/{{ b }}" is two matches, but
                // "{{ if x }}y{{ end }}" arrives here only as its parts. What matters is the pipe.
                var guarded = expression.Contains("html.escape", StringComparison.Ordinal);
                var value = expression.Split('|')[0].Trim();

                var context = ContextAt(template, name, match.Index);
                var ok = context switch
                {
                    // A CSS declaration cannot be escaped into safety, so the value must be one the
                    // generator has already checked.
                    "css" => CheckedInCsharp.Contains(value),
                    // A javascript string needs javascript escaping; JsString's callers name their
                    // field for it, which is what makes the wrong escape visible here.
                    "js" => value.EndsWith("_js", StringComparison.Ordinal)
                            || DeliberatelyRaw.ContainsKey(value),
                    _ => guarded || DeliberatelyRaw.ContainsKey(value),
                };

                if (!ok) unguarded.Add($"{name}.html [{context}]: {{{{ {expression} }}}}");
            }
        }

        Assert.True(unguarded.Count == 0,
            "These reach a page unguarded. Escape for where each one lands — html.escape in markup, "
            + "a _js field for javascript, a checked value for CSS — or, if raw is deliberate, say so "
            + "in DeliberatelyRaw:\n  " + string.Join("\n  ", unguarded));
    }

    [AvaloniaFact]
    public void TheAuditWouldNoticeANewRawValue()
    {
        // The audit is only worth having if it fails on the thing it is looking for, so this is the
        // failing case its own rules are written against.
        const string sneaked = "<a href=\"{{ item.href }}\">{{ item.caption }}</a>";

        var offenders = Regex.Matches(sneaked, @"\{\{(.*?)\}\}")
            .Select(m => m.Groups[1].Value.Trim())
            .Where(e => !e.Contains("html.escape") && !DeliberatelyRaw.ContainsKey(e))
            .ToList();

        Assert.Equal(2, offenders.Count);
    }
}
