// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using dir2site.Services;
using Xunit;

namespace dir2site.Tests;

/// <summary>
/// The ../ rewrite exists because an artifact page is emitted one directory deeper than its source
/// file, so a reference to _media/x.webp has to gain a segment to still resolve. It runs over
/// already-rendered HTML with a regex, which is what makes it worth pinning: every case below is a
/// place the pattern has to stop itself from matching, and two of them were live bugs.
/// </summary>
public class MarkdownRendererTests
{
    private static string Render(string markdown) =>
        MarkdownRenderer.ToHtml(markdown, rewriteRelativeUrls: true);

    [Fact]
    public void RelativeImageAndLinkUrls_GainOneSegment()
    {
        var html = Render("![fig](_media/figure.webp)\n\n[next](other-article/)");

        Assert.Contains("src=\"../_media/figure.webp\"", html);
        Assert.Contains("href=\"../other-article/\"", html);
    }

    [Theory]
    [InlineData("https://example.com/x.png")]
    [InlineData("http://example.com/x.png")]
    [InlineData("//cdn.example.com/x.png")]
    [InlineData("data:image/gif;base64,R0lGOD")]
    public void AbsoluteImageUrls_AreLeftAlone(string url)
    {
        var html = Render($"![fig]({url})");

        Assert.Contains($"src=\"{url}\"", html);
        Assert.DoesNotContain("../", html);
    }

    [Theory]
    [InlineData("/rooted/page/")]
    [InlineData("#section")]
    [InlineData("mailto:someone@example.com")]
    public void RootedAnchorAndSchemeLinks_AreLeftAlone(string url)
    {
        var html = Render($"[label]({url})");

        Assert.Contains($"href=\"{url}\"", html);
        Assert.DoesNotContain("../", html);
    }

    // docs/writing-articles.md teaches authors to write raw HTML figure snippets, so an article
    // documenting its own markup contains src="…" that must survive verbatim. Markdig escapes " to
    // &quot; inside code but leaves ' alone, which is how the single-quoted case slipped through.
    [Fact]
    public void UrlsInsideAFencedCodeBlock_AreNotRewritten()
    {
        var html = Render("""
            ```html
            <img src='pic.png'>
            <img src="pic.png">
            <a href='page/'>x</a>
            ```
            """);

        Assert.Contains("src='pic.png'", html);
        Assert.DoesNotContain("../", html);
    }

    [Fact]
    public void UrlsInsideAnInlineCodeSpan_AreNotRewritten()
    {
        var html = Render("Write `<img src='pic.png'>` to embed it.");

        Assert.Contains("src='pic.png'", html);
        Assert.DoesNotContain("../", html);
    }

    // \b matched after the hyphen, so data-src was treated as a bare src and lazy-loading markup
    // was rewritten. The attribute name has to be kept whole.
    [Fact]
    public void HyphenatedAttributesEndingInSrc_AreNotRewritten()
    {
        var html = Render("""<img data-src="_media/lazy.webp" class="lazy">""");

        Assert.Contains("data-src=\"_media/lazy.webp\"", html);
        Assert.DoesNotContain("../", html);
    }

    [Fact]
    public void Srcset_IsNotRewritten()
    {
        var html = Render("""<img srcset="_media/a.webp 1x, _media/b.webp 2x">""");

        Assert.Contains("srcset=\"_media/a.webp 1x, _media/b.webp 2x\"", html);
        Assert.DoesNotContain("../", html);
    }

    [Fact]
    public void RawHtmlFigures_AreRewrittenLikeMarkdownImages()
    {
        var html = Render("""<div class="figure-right"><img src="_media/side.webp" width="230"></div>""");

        Assert.Contains("src=\"../_media/side.webp\"", html);
    }

    [Fact]
    public void WithRewritingOff_RelativeUrlsAreUntouched()
    {
        var html = MarkdownRenderer.ToHtml("![fig](_media/figure.webp)", rewriteRelativeUrls: false);

        Assert.Contains("src=\"_media/figure.webp\"", html);
        Assert.DoesNotContain("../", html);
    }

    // Artifact metadata lives in the sidecar YAML, so front matter in the body is parsed and dropped
    // rather than rendered as content.
    [Fact]
    public void YamlFrontMatter_IsNotRendered()
    {
        var html = Render("---\ntitle: Secret\n---\n\nBody text.");

        Assert.DoesNotContain("title: Secret", html);
        Assert.Contains("Body text.", html);
    }
}
