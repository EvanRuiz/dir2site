// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Headless.XUnit;
using dir2site.Models;
using dir2site.Services;
using Xunit;

namespace dir2site.Tests;

/// <summary>
/// Prev/next across the artifacts of one folder, so reading a folder of photos doesn't mean going
/// back up to the collection between every one.
///
/// The chain is not simply "the pages this folder writes". A type carries the arrows or it doesn't
/// — <c>SiteGenerator.PagePolicies</c> decides, and today only photos do — and a type that doesn't
/// carry them is not somewhere they can lead either, or the reader arrives at a page with no way
/// onward. That is what most of these pin: what gets stepped over.
/// </summary>
public class ArtifactFolderNavTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "d2s-nav-" + Guid.NewGuid().ToString("N"));

    public ArtifactFolderNavTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string MakeFolder(params string[] parts)
    {
        var path = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(path);
        return path;
    }

    private void MakePhoto(string folder, string fileName, string caption)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        File.WriteAllText(Path.Combine(folder, fileName), "not really a jpeg");
        File.WriteAllText(Path.Combine(folder, fileName + ".yaml"),
            $"""
             type: photo
             caption: {caption}
             preview: .dir2site/{stem}/{stem}-preview.jpg
             previewLarge: .dir2site/{stem}/{stem}-preview-large.jpg
             """);
    }

    private void MakeArticle(string folder, string fileName, string caption)
    {
        File.WriteAllText(Path.Combine(folder, fileName), "Hello.");
        File.WriteAllText(Path.Combine(folder, fileName + ".yaml"),
            $"type: markdown\ncaption: {caption}\n");
    }

    private void MakePdf(string folder, string fileName, string caption)
    {
        File.WriteAllText(Path.Combine(folder, fileName), "not really a pdf");
        File.WriteAllText(Path.Combine(folder, fileName + ".yaml"),
            $"type: pdf\ncaption: {caption}\n");
    }

    private void MakeVideo(string folder, string fileName, string caption)
    {
        File.WriteAllText(Path.Combine(folder, fileName),
            "[InternetShortcut]\r\nURL=https://www.youtube.com/watch?v=AbCdEfGhIjK\r\n");
        File.WriteAllText(Path.Combine(folder, fileName + ".yaml"),
            $"type: video\ncaption: {caption}\n");
    }

    private void Generate()
    {
        var tree = DirectoryTraverser.BuildTree(_root, new List<string>(), new List<string>());
        var result = SiteGenerator.Generate(_root, tree, new Dir2SiteModel
        {
            Title = "My Site",
            Footer = "© 2026",
            SiteUrl = "https://example.test",
        });
        Assert.Empty(result.Errors);
    }

    private string ReadPage(params string[] parts) =>
        File.ReadAllText(Path.Combine([_root, "_site", .. parts, "index.html"]));

    /// <summary>
    /// The declarations of one rule, so a test about that rule can ask about that rule rather than
    /// about the whole stylesheet. A bare <c>Assert.Contains</c> on a file this size passes on any
    /// other rule that happens to share the declaration — <c>text-overflow: ellipsis</c> is in the
    /// card's trail as well as the breadcrumb bar's, and asking the file proved nothing about
    /// either.
    /// </summary>
    private static string Rule(string css, string selector)
    {
        // Every block for the selector, not the first. A selector can legitimately open more than
        // once — a shorthand shared with a sibling, a media query override — and returning whichever
        // came first answers a question about ordering rather than about the rule. Silently: the
        // assertion passes or fails against a block the test never meant to read.
        var blocks = new List<string>();
        for (var at = 0; ; )
        {
            var start = css.IndexOf(selector + " {", at, StringComparison.Ordinal);
            if (start < 0) break;
            var end = css.IndexOf('}', start);
            Assert.True(end > start, $"unterminated rule for `{selector}`");
            blocks.Add(css[start..end]);
            at = end;
        }

        Assert.True(blocks.Count > 0, $"no rule for `{selector}` in the stylesheet");
        return string.Join("\n", blocks);
    }

    /// <summary>
    /// The body of an at-rule — everything between its opening brace and the brace that closes it,
    /// nested rules included. <see cref="Rule"/> stops at the first '}', which inside a media query
    /// is the end of its first rule rather than the end of the query.
    /// </summary>
    private static string MediaBlock(string css, string query)
    {
        var start = css.IndexOf(query, StringComparison.Ordinal);
        Assert.True(start >= 0, $"no `{query}` block in the stylesheet");
        var open = css.IndexOf('{', start);
        Assert.True(open > start, $"unterminated `{query}`");

        var depth = 0;
        for (var i = open; i < css.Length; i++)
        {
            if (css[i] == '{') depth++;
            else if (css[i] == '}' && --depth == 0) return css[open..i];
        }

        Assert.Fail($"unterminated `{query}`");
        return "";
    }

    /// <summary>The three photos a folder needs before the middle one has both neighbours.</summary>
    private string MakeAlbum()
    {
        var album = MakeFolder("Album");
        MakePhoto(album, "Apple.jpg", "An Apple");
        MakePhoto(album, "Banana.jpg", "A Banana");
        MakePhoto(album, "Cherry.jpg", "A Cherry");
        return album;
    }

    [AvaloniaFact]
    public void TheMiddlePhotoLinksBothWays()
    {
        MakeAlbum();
        Generate();

        var page = ReadPage("Album", "Banana");

        Assert.Contains("href=\"../Apple/\" rel=\"prev\"", page);
        Assert.Contains("href=\"../Cherry/\" rel=\"next\"", page);
    }

    /// <summary>
    /// An arrow carries an address and nothing else — no neighbour's caption, as a tooltip or
    /// otherwise. As a tooltip it would fire on every photo, since reading a folder means clicking
    /// Next in the same spot and the pointer never leaves the link. Anywhere else it couples the
    /// pages, which is what this pins: retitling one photo rewrites one page, and
    /// <c>GenerateProgressTests</c> can go on reporting one artifact updated for one edit.
    /// </summary>
    [AvaloniaFact]
    public void RetitlingAPhotoLeavesItsNeighboursPagesAlone()
    {
        var album = MakeAlbum();
        Generate();
        var before = File.ReadAllText(
            Path.Combine(_root, "_site", "Album", "Apple", "index.html"));

        File.WriteAllText(Path.Combine(album, "Banana.jpg.yaml"),
            """
            type: photo
            caption: A Banana, retitled
            preview: .dir2site/Banana/Banana-preview.jpg
            previewLarge: .dir2site/Banana/Banana-preview-large.jpg
            """);
        Generate();

        Assert.Equal(before, ReadPage("Album", "Apple"));
        Assert.Contains("A Banana, retitled", ReadPage("Album", "Banana"));
    }

    /// The arrows say which way they go before you follow them, in words as well as in glyphs.
    [AvaloniaFact]
    public void EachArrowCarriesAnIconAndItsWord()
    {
        MakeAlbum();
        Generate();

        var page = ReadPage("Album", "Banana");

        Assert.Contains("<i class=\"bi bi-arrow-left\" aria-hidden=\"true\"></i> Prev", page);
        Assert.Contains("Next <i class=\"bi bi-arrow-right\" aria-hidden=\"true\"></i>", page);
    }

    /// Prev on one side of the caption and Next on the other — the shape the row's CSS assumes.
    [AvaloniaFact]
    public void TheCaptionSitsBetweenTheTwoArrows()
    {
        MakeAlbum();
        Generate();

        var page = ReadPage("Album", "Banana");

        var prev = page.IndexOf("artifact-nav-prev", StringComparison.Ordinal);
        var body = page.IndexOf("artifact-meta-body", StringComparison.Ordinal);
        var next = page.IndexOf("artifact-nav-next", StringComparison.Ordinal);

        Assert.InRange(body, prev, next);
    }

    /// <summary>
    /// At the ends the link is absent rather than dimmed, and nothing stands in for it — the
    /// caption beside it takes the space back.
    /// </summary>
    [AvaloniaFact]
    public void TheFirstAndLastPhotosAreMissingOneArrowEach()
    {
        MakeAlbum();
        Generate();

        var first = ReadPage("Album", "Apple");
        Assert.DoesNotContain("rel=\"prev\"", first);
        Assert.DoesNotContain("artifact-nav-prev", first);
        Assert.Contains("href=\"../Banana/\" rel=\"next\"", first);

        var last = ReadPage("Album", "Cherry");
        Assert.Contains("href=\"../Banana/\" rel=\"prev\"", last);
        Assert.DoesNotContain("rel=\"next\"", last);
        Assert.DoesNotContain("artifact-nav-next", last);
    }

    /// It is a chain, not a ring: the last photo doesn't send you back to the first.
    [AvaloniaFact]
    public void TheChainDoesNotWrapAround()
    {
        MakeAlbum();
        Generate();

        Assert.DoesNotContain("href=\"../Cherry/\"", ReadPage("Album", "Apple"));
        Assert.DoesNotContain("href=\"../Apple/\"", ReadPage("Album", "Cherry"));
    }

    /// A video plays on the folder's page and has none of its own, so there is nothing to link to.
    [AvaloniaFact]
    public void AVideoBetweenTwoPhotosIsSteppedOver()
    {
        var album = MakeFolder("Album");
        MakePhoto(album, "Apple.jpg", "An Apple");
        MakeVideo(album, "Banana.url", "A Talk");
        MakePhoto(album, "Cherry.jpg", "A Cherry");
        Generate();

        Assert.Contains("href=\"../Cherry/\" rel=\"next\"", ReadPage("Album", "Apple"));
        Assert.DoesNotContain("../Banana/", ReadPage("Album", "Apple"));
    }

    /// <summary>
    /// The one the <c>HasPrevNextNav</c> filter exists for. Both of these do get pages, so a chain
    /// built from "every artifact page in the folder" would thread them in — and land the reader on
    /// a page with no arrows to leave by.
    /// </summary>
    [AvaloniaFact]
    public void APdfAndAnArticleBetweenTwoPhotosAreSteppedOverToo()
    {
        var album = MakeFolder("Album");
        MakePhoto(album, "Apple.jpg", "An Apple");
        MakePdf(album, "Banana.pdf", "A Report");
        MakeArticle(album, "Berry.md", "An Essay");
        MakePhoto(album, "Cherry.jpg", "A Cherry");
        Generate();

        var first = ReadPage("Album", "Apple");
        Assert.Contains("href=\"../Cherry/\" rel=\"next\"", first);
        Assert.DoesNotContain("../Banana/", first);
        Assert.DoesNotContain("../Berry/", first);
    }

    /// And they carry no arrows of their own while their policy rows say they are off the chain.
    [AvaloniaFact]
    public void ThePdfAndArticlePagesCarryNoArrows()
    {
        var album = MakeFolder("Album");
        MakePhoto(album, "Apple.jpg", "An Apple");
        MakePdf(album, "Banana.pdf", "A Report");
        MakeArticle(album, "Berry.md", "An Essay");
        MakePhoto(album, "Cherry.jpg", "A Cherry");
        Generate();

        Assert.DoesNotContain("artifact-nav-link", ReadPage("Album", "Banana"));
        Assert.DoesNotContain("artifact-nav-link", ReadPage("Album", "Berry"));
    }

    /// Subfolders are the breadcrumb's business, not the arrows'.
    [AvaloniaFact]
    public void ASubfolderIsNotOnTheChain()
    {
        var album = MakeFolder("Album");
        MakePhoto(album, "Apple.jpg", "An Apple");
        MakePhoto(MakeFolder("Album", "Basket"), "Fig.jpg", "A Fig");
        MakePhoto(album, "Cherry.jpg", "A Cherry");
        Generate();

        var first = ReadPage("Album", "Apple");
        Assert.Contains("href=\"../Cherry/\" rel=\"next\"", first);
        Assert.DoesNotContain("../Basket/", first);
    }

    /// <summary>
    /// A folder holding one photo publishes it as the folder's own index, a level up from where
    /// "../" is measured. It has no siblings to point at, and must not point anywhere.
    /// </summary>
    [AvaloniaFact]
    public void APhotoPublishedAtItsFoldersIndexGetsNoArrows()
    {
        MakePhoto(MakeFolder("Album"), "Apple.jpg", "An Apple");
        Generate();

        Assert.DoesNotContain("artifact-nav-link", ReadPage("Album"));
    }

    /// Alone on the chain is the same as not being on it — the other pages are not destinations.
    [AvaloniaFact]
    public void TheOnlyPhotoAmongArticlesGetsNoArrows()
    {
        var album = MakeFolder("Album");
        MakeArticle(album, "Apple.md", "An Essay");
        MakePhoto(album, "Banana.jpg", "A Banana");
        MakeArticle(album, "Cherry.md", "Another Essay");
        Generate();

        Assert.DoesNotContain("artifact-nav-link", ReadPage("Album", "Banana"));
    }

    /// <summary>
    /// The other half of the policy: a viewer is sized to the window so the caption and the arrows
    /// under it are on screen, and an article — read by scrolling — is not.
    /// </summary>
    [AvaloniaFact]
    public void OnlyViewerPagesAreSizedToTheWindow()
    {
        var album = MakeFolder("Album");
        MakePhoto(album, "Apple.jpg", "An Apple");
        MakePdf(album, "Banana.pdf", "A Report");
        MakeArticle(album, "Cherry.md", "An Essay");
        MakePhoto(album, "Damson.jpg", "A Damson");
        Generate();

        Assert.Contains("artifact-screen-fit", ReadPage("Album", "Apple"));
        Assert.Contains("artifact-screen-fit", ReadPage("Album", "Banana"));
        Assert.DoesNotContain("artifact-screen-fit", ReadPage("Album", "Cherry"));
    }

    /// <summary>
    /// The caption band is a reserved height rather than a measured one, so the viewer above it is
    /// the same on every page of a folder and the picture doesn't resize as you arrow through. How
    /// much it reserves is the folder's answer: two rows where anything in it has something under
    /// its title, one where nothing does.
    /// </summary>
    [AvaloniaFact]
    public void AFolderWhereSomethingCarriesACreditReservesTwoRowsOnEveryPage()
    {
        var album = MakeFolder("Album");
        MakePhoto(album, "Apple.jpg", "An Apple");
        File.WriteAllText(Path.Combine(album, "Banana.jpg"), "not really a jpeg");
        File.WriteAllText(Path.Combine(album, "Banana.jpg.yaml"),
            "type: photo\ncaption: A Banana\ncredit: A. Nother\n");
        Generate();

        // Apple has no credit of its own and still reserves two: it shares the album with one that
        // does, and the point is that the two pages agree.
        Assert.Contains("artifact-meta-rows-2", ReadPage("Album", "Apple"));
        Assert.Contains("artifact-meta-rows-2", ReadPage("Album", "Banana"));
    }

    /// And an album of bare titles gives up no picture to hold a line that is never there.
    [AvaloniaFact]
    public void AFolderOfBareTitlesReservesOneRow()
    {
        MakeAlbum();
        Generate();

        Assert.Contains("artifact-meta-rows-1", ReadPage("Album", "Apple"));
        Assert.Contains("artifact-meta-rows-1", ReadPage("Album", "Cherry"));
    }

    /// <summary>
    /// Credit, source link and date share the reserved row rather than taking one each. Stacked,
    /// each was a short phrase with an empty line beside it and the date fell outside the band.
    /// </summary>
    [AvaloniaFact]
    public void CreditLinkAndDateShareOneLine()
    {
        var album = MakeFolder("Album");
        MakePhoto(album, "Apple.jpg", "An Apple");
        // A second photo, or the folder publishes Apple as its own index and there is no
        // Album/Apple/ page to read.
        MakePhoto(album, "Banana.jpg", "A Banana");
        File.WriteAllText(Path.Combine(album, "Apple.jpg.yaml"),
            """
            type: photo
            caption: An Apple
            credit: A. Nother
            date: March 1890
            url: https://example.org/apple
            url-text: See the original
            """);
        Generate();

        var page = ReadPage("Album", "Apple");

        // One paragraph holding all three, not three paragraphs.
        Assert.Contains(
            "<p class=\"text-muted small mb-1\">A. Nother<span class=\"artifact-meta-sep\">·</span>",
            page);
        Assert.Contains("<span class=\"artifact-meta-sep\">·</span>March 1890</p>", page);
    }

    /// <summary>
    /// The type badge belongs on a card, which is one of a row of mixed things, not on the page
    /// that is already showing you the photo.
    /// </summary>
    [AvaloniaFact]
    public void TheTypeBadgeIsOnTheCardAndNotOnThePage()
    {
        MakeAlbum();
        Generate();

        Assert.DoesNotContain("badge-type", ReadPage("Album", "Apple"));
        Assert.Contains("badge-type", ReadPage("Album"));
    }

    /// <summary>
    /// The badge used to be the one thing an article's closing strip always had. Without it, an
    /// article naming no credit, source or date would have ruled a line under nothing.
    /// </summary>
    [AvaloniaFact]
    public void AnArticleWithNothingToSayHasNoClosingStrip()
    {
        var album = MakeFolder("Album");
        MakeArticle(album, "Bare.md", "An Essay");
        MakeArticle(album, "Full.md", "Another Essay");
        File.WriteAllText(Path.Combine(album, "Full.md.yaml"),
            "type: markdown\ncaption: Another Essay\ncredit: A. Nother\n");
        Generate();

        Assert.DoesNotContain("artifact-meta", ReadPage("Album", "Bare"));
        Assert.Contains("artifact-meta", ReadPage("Album", "Full"));
    }

    /// The stylesheet has to hold up its end, or the class on the page means nothing.
    [AvaloniaFact]
    public void TheStylesheetGivesTheViewerTheRoomTheCaptionLeaves()
    {
        MakeAlbum();
        Generate();

        var css = File.ReadAllText(Path.Combine(_root, "_site", "css", "site.css"));

        // Nothing states how tall the header is, because nothing can: it depends on a logo this
        // generator has never seen. A stated 94px was wrong by 50px on a phone with a wide logo,
        // and the picture started underneath the breadcrumb bar.
        //
        // Asked as "never declared, never read" rather than "the name is absent from the file" —
        // the comment above these rules explains why the variable went, and names it to do so.
        Assert.DoesNotContain("--site-header-height:", css);
        Assert.DoesNotContain("--site-header-flush:", css);
        Assert.DoesNotContain("var(--site-header", css);

        // The page is the grid, and an empty spacer spanning the header rows and the picture's is
        // what makes them add up to a viewport less the band. The browser does the arithmetic.
        Assert.Contains("display: grid;", Rule(css, ".artifact-screen-fit"));
        Assert.Contains(
            "height: calc(100dvh - var(--band-height));",
            Rule(css, ".artifact-screen-fit > .artifact-screen-spacer"));

        // The trail keeps its place while the navbar leaves, which only works because its parent is
        // the page: a sticky box cannot escape whatever wraps it.
        Assert.Contains("position: sticky;", Rule(css, ".artifact-screen-fit .breadcrumb-bar"));

        Assert.Contains(".artifact-meta-rows-1 { --caption-height:", css);
        Assert.Contains(".artifact-meta-rows-2 { --caption-height:", css);

        // The caption lands in the room the spacer left and grows downward out of it, so what the
        // caption does can never reach the picture — and anything past that room lengthens the
        // document instead, which is how a long caption is read.
        Assert.Contains(
            "min-height: var(--band-height);", Rule(css, ".artifact-meta-reserved"));

        // And no scroll box of its own — an over-long caption is read by scrolling the page.
        // Asked of this rule rather than of the stylesheet: a site-wide ban on the word would fail
        // for whoever next gives a table wrapper or a code block a scrollbar, in a test named after
        // the caption band and pointing nowhere near what they changed.
        Assert.DoesNotContain("overflow", Rule(css, ".artifact-meta-reserved .artifact-meta-body"));

        // Centred in the room set aside for the caption rather than in the row, which is what keeps
        // them in the same place on the page whose caption outgrows its band and stretches the row.
        Assert.Contains("align-self: flex-start;", css);
        Assert.Contains(
            "margin-top: calc((var(--caption-height) - var(--nav-button-height)) / 2);", css);
        // The offset is half of what the band has left over after the button, so the button's own
        // height has to be a stated number rather than whatever the font and padding come to.
        Assert.Contains("--nav-button-height:", css);
        Assert.Contains("height: var(--nav-button-height);", css);

        Assert.Contains("justify-content: center;", css);

        // Bootstrap's mb-1 is !important, so without matching it the trailing margin stays inside
        // the centred content and lifts the text off the buttons it should be level with.
        Assert.Contains(
            ".artifact-meta-reserved .artifact-meta-body > :last-child { margin-bottom: 0 !important; }",
            css);
    }

    /// <summary>
    /// A viewer page starts where the header ends, whatever the header turns out to be. It ends
    /// where it ends because it is in the flow rather than out of it: a fixed header has to be
    /// compensated for by a number, and the number was a guess about a logo this generator has
    /// never seen — 94px, against a real 144px on a phone whose site has a wide one.
    /// </summary>
    [AvaloniaFact]
    public void AViewerPageSitsFlushUnderTheHeaderWithNoShadowOverIt()
    {
        MakeAlbum();
        Generate();

        var css = File.ReadAllText(Path.Combine(_root, "_site", "css", "site.css"));

        // In flow, so it takes the room it needs and the grid hands the rest to the picture. It
        // still stays put as the page scrolls, which is the only thing `fixed` was buying.
        // Both asked of the header's own rules, so a modal added later doesn't fail a test about
        // the header.
        Assert.Contains("position: sticky;", Rule(css, ".site-header"));
        Assert.DoesNotContain("position: fixed", Rule(css, ".site-header"));

        // Nothing below it is pushed down by a stated amount, because nothing needs to be.
        Assert.DoesNotContain("padding-top", Rule(css, "body"));

        Assert.Contains(".artifact-screen-fit .breadcrumb-bar {", css);
    }

    /// <summary>
    /// What the page leaves clear for the header is a stated number, so the header has to keep to
    /// it. The trail's last crumb is the artifact's caption, as long as whoever wrote the yaml felt
    /// like: a 232-character one wrapped the bar to six lines and made the header 262px of a
    /// 375px-wide phone, with the picture starting somewhere underneath it.
    /// </summary>
    [AvaloniaFact]
    public void TheBreadcrumbTrailCannotWrapHoweverLongACaptionIs()
    {
        MakeAlbum();
        Generate();

        var css = File.ReadAllText(Path.Combine(_root, "_site", "css", "site.css"));

        // Asked of the bar's own rules. `text-overflow: ellipsis` is also on the card's trail, so
        // asking the stylesheet for it would have passed on main, before any of this existed.
        var list = Rule(css, ".breadcrumb-bar .breadcrumb");
        Assert.Contains("flex-wrap: nowrap;", list);

        // What doesn't fit is scrolled to rather than shrunk into a stub: four ordinary folder
        // names want more room than a phone has, and no way of distributing that deficit invents
        // any. The scrollbar is hidden because it would be drawn inside the bar, whose height is
        // what the picture below is measured against.
        Assert.Contains("overflow-x: auto;", list);
        Assert.Contains("scrollbar-width: none;", list);
        Assert.Contains("display: none;", Rule(css, ".breadcrumb-bar .breadcrumb::-webkit-scrollbar"));

        // The names stay whole — no crumb gives up characters to its neighbours.
        Assert.Contains("flex-shrink: 0;", Rule(css, ".breadcrumb-bar .breadcrumb-item"));

        // Except the caption, which is the one crumb with no upper bound on what it may say.
        var active = Rule(css, ".breadcrumb-bar .breadcrumb-item.active");
        Assert.Contains("max-width: 36ch;", active);
        Assert.Contains("text-overflow: ellipsis;", active);
    }

    /// <summary>
    /// A phone held sideways has no height to divide up: setting the caption's band aside out of
    /// 375px left the picture a 197px letterbox. There the viewer takes what the header leaves and
    /// the caption is reached by scrolling — the one place the band is given up.
    /// </summary>
    [AvaloniaFact]
    public void AViewportTooShortToDivideGivesTheViewerTheScreen()
    {
        MakeAlbum();
        Generate();

        var css = File.ReadAllText(Path.Combine(_root, "_site", "css", "site.css"));
        var block = MediaBlock(css, "@media (max-height: 480px)");

        // What the rule does, not merely that the query is there. This one went missing once
        // already, in a rework, and an assertion on the query alone would have passed on the empty
        // block it left behind.
        //
        // Giving the band up is now a matter of not reserving it: the spacer runs the full height,
        // so the picture takes everything the header leaves and the caption follows it down.
        Assert.Contains("height: 100dvh;", block);
        Assert.Contains("artifact-screen-spacer", block);
    }

    /// <summary>
    /// The reservation binds the chain, not the folder. A PDF is not on the chain, so its author
    /// line is not a reason for every photo in the folder to give up a row of picture for something
    /// none of them shows — and the PDF still answers the question for itself.
    /// </summary>
    [AvaloniaFact]
    public void APdfsSubtitleCostsThePhotosAroundItNothing()
    {
        var album = MakeFolder("Album");
        MakePhoto(album, "Apple.jpg", "An Apple");
        MakePhoto(album, "Cherry.jpg", "A Cherry");
        File.WriteAllText(Path.Combine(album, "Report.pdf"), "not really a pdf");
        File.WriteAllText(Path.Combine(album, "Report.pdf.yaml"),
            "type: pdf\ncaption: A Report\nauthor: A. Cartographer\n");
        Generate();

        Assert.Contains("artifact-meta-rows-1", ReadPage("Album", "Apple"));
        Assert.Contains("artifact-meta-rows-1", ReadPage("Album", "Cherry"));
        Assert.Contains("artifact-meta-rows-2", ReadPage("Album", "Report"));
    }

    /// <summary>
    /// The class a viewer's height hangs off sits on a box no library owns. BookReader writes its
    /// own list over #BookReader's class attribute as it initialises, so a page that put
    /// <c>artifact-viewer</c> there lost it before the stylesheet could use it and the reader laid
    /// out with no height at all.
    ///
    /// The photo page is held to the same shape though nothing is wrong with it today:
    /// OpenSeadragon appends children and leaves the attribute alone, but that is a property of the
    /// version we ship rather than anything the page arranges — and it is the bet BookReader lost.
    /// The failure is a viewer with no height, which shows as a black page rather than a red build.
    /// </summary>
    [AvaloniaFact]
    public void NeitherViewerHangsItsHeightOnAnElementALibraryOwns()
    {
        var album = MakeFolder("Album");
        MakePhoto(album, "Apple.jpg", "An Apple");
        MakePhoto(album, "Cherry.jpg", "A Cherry");
        File.WriteAllText(Path.Combine(album, "Report.pdf"), "not really a pdf");
        File.WriteAllText(Path.Combine(album, "Report.pdf.yaml"), "type: pdf\ncaption: A Report\n");
        Generate();

        // The wrapper carries the class; the element handed to the library carries only its id.
        foreach (var (page, viewerId) in new[]
                 {
                     (ReadPage("Album", "Report"), "BookReader"),
                     (ReadPage("Album", "Apple"), "osd-viewer"),
                 })
        {
            Assert.Contains("<div class=\"artifact-viewer\">", page);
            Assert.Contains($"<div id=\"{viewerId}\"></div>", page);
            Assert.DoesNotContain($"id=\"{viewerId}\" class=", page);
        }

        // And the stylesheet reaches them by id, the one attribute a viewer library leaves alone.
        var css = File.ReadAllText(Path.Combine(_root, "_site", "css", "site.css"));
        Assert.Contains("height: 100%;", Rule(css, "#BookReader"));
        Assert.Contains("height: 100%;", Rule(css, "#osd-viewer"));
    }

}
