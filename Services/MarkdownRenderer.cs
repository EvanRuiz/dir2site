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
        // A single newline becomes a <br> rather than a space. Standard Markdown reflows a
        // hand-wrapped paragraph into one line, which surprises anyone writing prose in an editor
        // that wraps for them, and offers only trailing whitespace as the way to say otherwise.
        .UseSoftlineBreakAsHardlineBreak()
        .Build();

    /// <summary>
    /// Reads a <c>.md</c> file and renders it to HTML. Relative media/link URLs gain one
    /// <c>../</c> segment because the artifact page is emitted one directory deeper than the
    /// source file (at <c>{folder}/{stem}/index.html</c>), so a reference such as
    /// <c>_media/figure.webp</c> still resolves once <c>_media</c> is copied verbatim into the site.
    /// </summary>
    public static string ToHtml(string mdFilePath) =>
        FileToHtml(mdFilePath, pageIsNested: true);

    /// <summary>
    /// Renders a <c>.md</c> file, choosing whether relative URLs gain a <c>../</c> segment.
    ///
    /// They need one when the page sits a directory deeper than the source, which is where an
    /// artifact page normally goes. A folder holding nothing but this one article publishes it as
    /// the folder's own index instead, level with the source, and then the segment would be one
    /// too many — <c>_media/figure.webp</c> is already correct from there.
    /// </summary>
    public static string FileToHtml(string mdFilePath, bool pageIsNested)
    {
        string text;
        try { text = File.ReadAllText(mdFilePath); }
        catch { return string.Empty; }
        return ToHtml(text, pageIsNested);
    }

    /// <summary>
    /// Renders Markdown text to HTML and points relative <c>src</c>/<c>href</c> URLs — in both
    /// Markdig-emitted links/images and raw HTML — at where the site actually publishes them. An
    /// <c>href</c> naming a sibling <c>.md</c> file is pointed at that article's published
    /// <c>stem/</c> folder always; the extra <c>../</c> segment is added only when
    /// <paramref name="pageIsNested"/> (see <see cref="ToHtml(string)"/>). URLs inside
    /// <c>&lt;pre&gt;</c> and <c>&lt;code&gt;</c> spans are left verbatim so documented markup
    /// renders as written.
    /// </summary>
    /// <remarks>
    /// The two rewrites are independent and were once gated together: <c>../</c> compensates for
    /// the page's depth, while a <c>.md</c> target is wrong at any depth because the site never
    /// publishes the file. Gating both meant a sole-artifact article — published at its folder's
    /// index, so not nested — emitted its cross-article links verbatim and 404'd.
    /// </remarks>
    public static string ToHtml(string markdown, bool pageIsNested)
    {
        var html = Markdig.Markdown.ToHtml(markdown, Pipeline);
        return RewriteRelativeUrls(html, pageIsNested);
    }

    // Rewrites relative src=""/href="" attributes — covering both Markdig-emitted links/images and
    // raw HTML tags the author embedded (e.g. a floated <img>) — so they resolve from the nested
    // artifact page. Absolute, rooted, and anchor URLs are left untouched.
    //
    // <pre>/<code> spans are matched by the same pass purely so they can be skipped: an article
    // that documents HTML markup must render its samples verbatim. Markdig escapes " to &quot;
    // inside code but leaves ' alone, so without this a single-quoted src='…' in a fenced block
    // would be silently rewritten.
    private static string RewriteRelativeUrls(string html, bool pageIsNested) =>
        AttrUrlRegex().Replace(html, m =>
        {
            if (m.Groups["skip"].Success) return m.Value;
            var url = m.Groups["url"].Value;
            if (!IsRelativeUrl(url)) return m.Value;
            var attr = m.Groups["attr"].Value;
            if (attr.StartsWith("href", StringComparison.OrdinalIgnoreCase))
                url = MapMarkdownTargetToPublishedFolder(url);
            var q = m.Groups["q"].Value;
            return $"{attr}{q}{(pageIsNested ? "../" : "")}{url}{q}";
        });

    // An author writing in an editor that resolves links locally points a cross-article link at the
    // sibling source file (brownian-motion.md). The site never publishes the .md itself — the
    // article becomes the folder brownian-motion/ — so the href is pointed there instead. Any
    // ?query or #fragment rides along unchanged.
    //
    // href only: a src="" naming a .md is not a page link.
    private static string MapMarkdownTargetToPublishedFolder(string url)
    {
        var cut = url.IndexOfAny(['?', '#']);
        var path = cut < 0 ? url : url[..cut];
        if (!path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) return url;

        // An "_"-folder is copied into the site verbatim, so it is the one place a .md really is
        // published as a file — linking to _media/notes.md means the file, not an article page.
        foreach (var segment in path.Split('/'))
            if (segment.StartsWith('_')) return url;

        var stem = path[..^".md".Length];
        // ".md" on its own, or a trailing "dir/.md", names no article — leave it be.
        if (stem.Length == 0 || stem.EndsWith('/')) return url;

        return cut < 0 ? $"{stem}/" : $"{stem}/{url[cut..]}";
    }

    // The (?<![\w-]) guard keeps the attribute name whole, so data-src="…" and the like are left
    // alone rather than being treated as a bare src.
    [GeneratedRegex(
        """(?<skip><pre\b[^>]*>.*?</pre>|<code\b[^>]*>.*?</code>)|(?<attr>(?<![\w-])(?:src|href)\s*=\s*)(?<q>["'])(?<url>[^"']*)\k<q>""",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
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
