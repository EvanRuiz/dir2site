// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.IO;
using System.Text.RegularExpressions;
using Markdig;

namespace dir2site.Services;

/// <summary>
/// Converts Markdown to HTML for the generated static site using Markdig.
/// YAML front matter (if any) is parsed and dropped — artifact metadata lives in the
/// sidecar YAML file, not in the body. Raw HTML in the body is passed through.
/// </summary>
public static partial class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseYamlFrontMatter()
        .Build();

    /// <summary>
    /// Reads a <c>.md</c> file and renders it to HTML. Relative media/link URLs gain one
    /// <c>../</c> segment because the artifact page is emitted one directory deeper than the
    /// source file (at <c>{folder}/{stem}/index.html</c>), so a reference such as
    /// <c>_media/figure.webp</c> still resolves once <c>_media</c> is copied verbatim into the site.
    /// </summary>
    public static string ToHtml(string mdFilePath)
    {
        string text;
        try { text = File.ReadAllText(mdFilePath); }
        catch { return string.Empty; }
        return ToHtml(text, rewriteRelativeUrls: true);
    }

    /// <summary>
    /// Renders Markdown text to HTML. When <paramref name="rewriteRelativeUrls"/> is true,
    /// relative <c>src</c>/<c>href</c> URLs (in both Markdown links/images and raw HTML) are
    /// prefixed with <c>../</c> (see <see cref="ToHtml(string)"/>).
    /// </summary>
    public static string ToHtml(string markdown, bool rewriteRelativeUrls)
    {
        var html = Markdig.Markdown.ToHtml(markdown, Pipeline);
        return rewriteRelativeUrls ? RewriteRelativeUrls(html) : html;
    }

    // Rewrites relative src=""/href="" attributes — covering both Markdig-emitted links/images and
    // raw HTML tags the author embedded (e.g. a floated <img>) — so they resolve from the nested
    // artifact page. Absolute, rooted, and anchor URLs are left untouched.
    private static string RewriteRelativeUrls(string html) =>
        AttrUrlRegex().Replace(html, m =>
        {
            var url = m.Groups["url"].Value;
            if (!IsRelativeUrl(url)) return m.Value;
            var q = m.Groups["q"].Value;
            return $"{m.Groups["attr"].Value}{q}../{url}{q}";
        });

    [GeneratedRegex("""(?<attr>\b(?:src|href)\s*=\s*)(?<q>["'])(?<url>[^"']*)\k<q>""", RegexOptions.IgnoreCase)]
    private static partial Regex AttrUrlRegex();

    // A URL is relative (and thus needs the ../ prefix) when it is not rooted, not an anchor,
    // and carries no URI scheme (http:, https:, data:, mailto:, etc.).
    private static bool IsRelativeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (url[0] is '/' or '#') return false;
        if (url.StartsWith("//", StringComparison.Ordinal)) return false;

        var colon = url.IndexOf(':');
        if (colon > 0)
        {
            var scheme = url.AsSpan(0, colon);
            var looksLikeScheme = true;
            foreach (var c in scheme)
            {
                if (!(char.IsLetterOrDigit(c) || c is '+' or '-' or '.')) { looksLikeScheme = false; break; }
            }
            if (looksLikeScheme) return false; // a leading scheme means the URL is absolute
        }

        return true;
    }
}
