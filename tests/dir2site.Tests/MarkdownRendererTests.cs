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
        MarkdownRenderer.ToHtml(markdown, pageIsNested: true);

    [Fact]
    public void ASingleNewline_BreaksTheLine()
    {
        // Standard Markdown reflows these into one line, which is not what someone writing prose
        // in a wrapping editor means by pressing return.
        var html = Render("First line\nSecond line");

        Assert.Contains("<br", html);
        Assert.Contains("First line", html);
        Assert.Contains("Second line", html);
    }

    [Fact]
    public void ABlankLine_StillStartsANewParagraph()
    {
        var html = Render("First para\n\nSecond para");

        Assert.Contains("<p>First para</p>", html);
        Assert.Contains("<p>Second para</p>", html);
    }

    [Fact]
    public void ACaptionUnderAnImage_StaysOnItsOwnLine()
    {
        // Written without a blank line, which used to run the caption on beside the image.
        var html = Render("![fig](_media/figure.webp)\nA caption");

        var img = html.IndexOf("<img", System.StringComparison.Ordinal);
        var br = html.IndexOf("<br", System.StringComparison.Ordinal);
        var caption = html.IndexOf("A caption", System.StringComparison.Ordinal);
        Assert.True(img < br && br < caption, "the break belongs between the image and its caption");
    }

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

    // Authors write cross-article links the way their editor resolves them — at the sibling source
    // file. The .md is never copied into the site (the article publishes as a folder), so the href
    // has to be pointed at the folder or it 404s.
    [Theory]
    [InlineData("brownian-motion.md", "../brownian-motion/")]
    [InlineData("papers/brownian-motion.md", "../papers/brownian-motion/")]
    [InlineData("../Notes/entry.MD", "../../Notes/entry/")]
    [InlineData("brownian-motion.md#abstract", "../brownian-motion/#abstract")]
    [InlineData("brownian-motion.md?v=2", "../brownian-motion/?v=2")]
    public void ALinkToASiblingMarkdownFile_PointsAtItsPublishedFolder(string url, string expected)
    {
        var html = Render($"See the [paper]({url}).");

        Assert.Contains($"href=\"{expected}\"", html);
    }

    // Nothing here names an article: ".md" alone is a dotfile, and a src is not a page link — while
    // _media (the one place a .md could be served as a file) is copied verbatim.
    [Theory]
    [InlineData("""[label](.md)""", "href=\"../.md\"")]
    [InlineData("""[label](notes/.md)""", "href=\"../notes/.md\"")]
    [InlineData("""<img src="_media/diagram.md">""", "src=\"../_media/diagram.md\"")]
    public void MarkdownSuffixesThatAreNotPageLinks_KeepTheirExtension(string markdown, string expected)
    {
        var html = Render(markdown);

        Assert.Contains(expected, html);
    }

    [Fact]
    public void AMarkdownLinkInsideACodeBlock_KeepsItsExtension()
    {
        // Single-quoted, since Markdig escapes " to &quot; inside code but leaves ' alone — that is
        // the form that reaches the rewriter unescaped.
        var html = Render("""
            ```html
            <a href='brownian-motion.md'>paper</a>
            ```
            """);

        Assert.Contains("href='brownian-motion.md'", html);
        Assert.DoesNotContain("brownian-motion/", html);
    }

    [Fact]
    public void RawHtmlFigures_AreRewrittenLikeMarkdownImages()
    {
        var html = Render("""<div class="figure-right"><img src="_media/side.webp" width="230"></div>""");

        Assert.Contains("src=\"../_media/side.webp\"", html);
    }

    [Fact]
    public void OnAPageThatIsNotNested_RelativeUrlsKeepTheirDepth()
    {
        var html = MarkdownRenderer.ToHtml("![fig](_media/figure.webp)", pageIsNested: false);

        Assert.Contains("src=\"_media/figure.webp\"", html);
        Assert.DoesNotContain("../", html);
    }

    [Fact]
    public void OnAPageThatIsNotNested_AMarkdownLinkStillPointsAtItsPublishedFolder()
    {
        // The ../ is about the page's depth; a .md target is wrong at any depth, because the site
        // never publishes the file. A sole-artifact article is the layout where they differ.
        var html = MarkdownRenderer.ToHtml("See the [paper](../Notes/brownian-motion.md).", pageIsNested: false);

        Assert.Contains("href=\"../Notes/brownian-motion/\"", html);
        Assert.DoesNotContain(".md\"", html);
    }

    [Fact]
    public void ALinkIntoAnUnderscoreFolder_KeepsItsExtension()
    {
        // An "_"-folder is copied verbatim, so _media/notes.md really is served as that file.
        var html = Render("See the [notes](_media/notes.md).");

        Assert.Contains("href=\"../_media/notes.md\"", html);
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
