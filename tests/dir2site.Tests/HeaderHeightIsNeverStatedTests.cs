// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Avalonia.Headless.XUnit;
using dir2site.Models;
using dir2site.Services;
using Xunit;

namespace dir2site.Tests;

/// <summary>
/// How tall the header is depends on a logo this generator has never seen, so no part of the site
/// may claim to know it.
///
/// It did once. A fixed header is out of flow, so the stylesheet pushed every page down by a stated
/// 106px, and a viewer page subtracted a stated 94px to find the room it had — both written against
/// a 56px navbar over a 38px breadcrumb bar. Add a logo and the real header is 104px; add a wide one
/// and on a 375px phone it is 144px, half again as tall as the number. What the reader saw was the
/// top of the picture sitting underneath the breadcrumb bar.
///
/// The cure was to stop measuring: the header is in the flow, so it takes the room it needs, and a
/// grid hands the picture whatever is left. These pin that nobody puts the number back — which is
/// easy to do by accident, because a single stated height makes every layout sum simpler right up
/// until someone changes their logo.
/// </summary>
public class HeaderHeightIsNeverStatedTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "d2s-header-" + Guid.NewGuid().ToString("N"));

    public HeaderHeightIsNeverStatedTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>
    /// Logos of three shapes, plus none at all. The height attribute in the markup normalises them
    /// to 40px tall, so what actually moves the header is width: a wide mark crowds the nav and
    /// wraps it onto another line. All four are here because the point is that the stylesheet is
    /// the same whichever a project picks.
    /// </summary>
    public static TheoryData<string, int, int> Logos() => new()
    {
        { "", 0, 0 },
        { "logo.png", 300, 28 },     // wider than tall
        { "logo.png", 900, 120 },
        { "logo.png", 4200, 200 },   // wide enough to push the nav to a second line
    };

    private string GenerateWithLogo(string logo, int logoWidth, int logoHeight)
    {
        var album = Path.Combine(_root, "Album");
        Directory.CreateDirectory(album);
        foreach (var stem in new[] { "Apple", "Cherry" })
        {
            File.WriteAllText(Path.Combine(album, $"{stem}.jpg"), "not really a jpeg");
            File.WriteAllText(Path.Combine(album, $"{stem}.jpg.yaml"),
                $"type: photo\ncaption: {stem}\npreview: .dir2site/{stem}/{stem}-preview.jpg\n");
        }

        // A file of the right shape, so the copy job has something to take and the markup is what
        // a real project's would be. Its bytes are never read by the generator.
        if (logo.Length > 0)
            File.WriteAllText(Path.Combine(_root, logo), $"not really a png {logoWidth}x{logoHeight}");

        var tree = DirectoryTraverser.BuildTree(_root, new List<string>(), new List<string>());
        var result = SiteGenerator.Generate(_root, tree, new Dir2SiteModel
        {
            Title = "My Site",
            Footer = "© 2026",
            SiteUrl = "https://example.test",
            Logo = logo,
        });
        Assert.Empty(result.Errors);

        return File.ReadAllText(Path.Combine(_root, "_site", "css", "site.css"));
    }

    /// <summary>
    /// The one that would have caught the original bug, and catches its return in any spelling:
    /// nothing may take a viewport height and subtract a number from it. `100dvh - var(--band)` is
    /// fine — that is a length this stylesheet owns. `100dvh - 94px` is a claim about a logo.
    /// </summary>
    [AvaloniaTheory]
    [MemberData(nameof(Logos))]
    public void NothingSubtractsAFixedHeightFromTheViewport(string logo, int w, int h)
    {
        var css = GenerateWithLogo(logo, w, h);

        var subtraction = Regex.Match(css, @"100d?vh\s*-\s*[\d.]+\s*(px|rem|em)");
        Assert.False(
            subtraction.Success,
            $"`{subtraction.Value}` takes a constant off the viewport. Whatever that constant "
            + "stands for, it is a guess about how tall someone else's header is.");
    }

    /// <summary>
    /// In flow is what makes the header measurable by the layout rather than by a person. Fixed or
    /// absolute takes it out again, and then something below has to be told how far to move.
    /// </summary>
    [AvaloniaTheory]
    [MemberData(nameof(Logos))]
    public void TheHeaderStaysInTheFlow(string logo, int w, int h)
    {
        var css = GenerateWithLogo(logo, w, h);

        // Asked of the header's own rule. A ban on the word across the stylesheet would read as
        // the same invariant, and would fail on the first modal or toast anyone adds — in a test
        // named after the header, pointing nowhere near what they changed.
        var header = HeaderRule(css);
        Assert.Contains("position: sticky;", header);
        Assert.DoesNotContain("position: fixed", header);
        Assert.DoesNotContain("position: absolute", header);
    }

    /// <summary>
    /// The compensation is the tell. A page pushed down by a stated amount is a page that has been
    /// told the header's height, however that height was arrived at.
    /// </summary>
    [AvaloniaTheory]
    [MemberData(nameof(Logos))]
    public void NoPageIsPushedDownToClearTheHeader(string logo, int w, int h)
    {
        var css = GenerateWithLogo(logo, w, h);

        var body = Between(css, "body {", "}");
        Assert.DoesNotContain("padding-top", body);
        Assert.DoesNotContain("margin-top", body);

        // The collection page carried an inline override of the same kind for pages with no
        // breadcrumb bar, which is the same mistake one level down.
        var home = File.ReadAllText(Path.Combine(_root, "_site", "index.html"));
        Assert.DoesNotContain("padding-top", home);
    }

    /// <summary>
    /// The header's row is sized by its content — that is the whole mechanism. Anything else there,
    /// a length or an fr, is the number coming back under another name.
    /// </summary>
    [AvaloniaTheory]
    [MemberData(nameof(Logos))]
    public void TheScreenLetsTheHeaderRowSizeItself(string logo, int w, int h)
    {
        var css = GenerateWithLogo(logo, w, h);

        // Two header rows now — the navbar and the trail are separate, so only the trail stays
        // pinned — and both are `auto`, sized by their content. A length or an fr in either is the
        // stated number coming back under another name.
        Assert.Contains(
            "grid-template-rows: auto auto minmax(0, 1fr) auto auto;",
            Between(css, ".artifact-screen-fit {", "}"));

        // And the spacer is what makes those rows add up to a viewport, without saying how tall any
        // of them is.
        var spacer = Between(css, ".artifact-screen-fit > .artifact-screen-spacer {", "}");
        Assert.Contains("grid-row: 1 / 4;", spacer);
        Assert.Contains("height: calc(100dvh - var(--band-height));", spacer);
        // Its own column, or the grid feeds part of its height to the header rows instead and a
        // long caption comes out with a shorter picture than its neighbours.
        Assert.Contains("grid-column: 1;", spacer);
    }

    /// <summary>
    /// And the header has to be inside that screen, or the grid has nothing to measure and the row
    /// it sizes is somebody else's.
    /// </summary>
    [AvaloniaTheory]
    [MemberData(nameof(Logos))]
    public void TheHeaderIsInsideTheScreenThatMeasuresIt(string logo, int w, int h)
    {
        GenerateWithLogo(logo, w, h);

        var page = File.ReadAllText(
            Path.Combine(_root, "_site", "Album", "Apple", "index.html"));

        var screen = page.IndexOf("artifact-screen", StringComparison.Ordinal);
        var header = page.IndexOf("site-header", StringComparison.Ordinal);
        var viewer = page.IndexOf("artifact-viewer", StringComparison.Ordinal);

        Assert.True(screen >= 0 && header > screen && viewer > header,
            "the screen has to open before the header, and the header before the picture");
    }

    /// <summary>
    /// Whichever logo a project picks, it gets the same stylesheet. A rule that varied with the
    /// logo would mean the generator had formed an opinion about how tall the result would be —
    /// and it cannot have one, because it never renders the page.
    /// </summary>
    [AvaloniaFact]
    public void TheStylesheetDoesNotVaryWithTheLogo()
    {
        var withNone = GenerateWithLogo("", 0, 0);
        var withWide = GenerateWithLogo("logo.png", 4200, 200);

        Assert.Equal(withNone, withWide);
    }

    /// <summary>
    /// Existing sites keep the clear space they have always had under the header. It was an
    /// accident of the old arithmetic — 106px cleared for a 94px header left 12px above a page's
    /// content, and 66px cleared for a 56px navbar left 10px on the home page — but it is what
    /// every site built before this rewrite looks like, and nothing about fixing the header's
    /// height is a reason for all of their pages to shift up.
    ///
    /// It is now a margin on the header rather than slack in a guess, so it is the same 12px
    /// whatever the logo does, instead of being whatever the guess happened to have left over.
    /// </summary>
    [AvaloniaTheory]
    [MemberData(nameof(Logos))]
    public void ThePageKeepsTheClearSpaceItHadUnderTheHeader(string logo, int w, int h)
    {
        var css = GenerateWithLogo(logo, w, h);

        Assert.Contains("margin-bottom: 12px;", HeaderRule(css));
        Assert.Contains(
            "margin-bottom: 10px;", Between(css, ".site-header:has(.no-breadcrumb) {", "}"));

        // Not on a viewer page, where the picture starts where the header ends. Nothing overrides
        // the margin there: the wrapper becomes `display: contents`, so there is no box to carry
        // one — which also frees its two bars to be rows of the page's own grid.
        Assert.Contains(
            "display: contents;", Between(css, ".artifact-screen-fit > .site-header {", "}"));
    }

    private static string HeaderRule(string css) => Between(css, ".site-header {", "}");

    private static string Between(string css, string opening, string closing)
    {
        var start = css.IndexOf(opening, StringComparison.Ordinal);
        Assert.True(start >= 0, $"no `{opening}` in the stylesheet");
        var end = css.IndexOf(closing, start, StringComparison.Ordinal);
        Assert.True(end > start, $"unterminated `{opening}`");
        return css[start..end];
    }
}
