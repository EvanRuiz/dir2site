// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Headless.XUnit;
using dir2site.Models;
using dir2site.Services;
using Xunit;

namespace dir2site.Tests;

/// <summary>
/// The footer is the one part of a page assembled from a list the site owner writes by hand, so
/// these pin the two things that follow from that: a link has to resolve to wherever its artifact
/// actually publishes, and everything reaching an attribute has to be checked rather than trusted.
/// </summary>
public class FooterTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "d2s-footer-" + Guid.NewGuid().ToString("N"));

    public FooterTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string ReadPage(params string[] parts) =>
        File.ReadAllText(Path.Combine([_root, "_site", .. parts, "index.html"]));

    /// <summary>
    /// Just the footer. A collection page's card grid is Bootstrap columns too, so anything counting
    /// columns has to say which ones it means.
    /// </summary>
    private string ReadFooter(params string[] parts)
    {
        var page = ReadPage(parts);
        var start = page.IndexOf("<footer", StringComparison.Ordinal);
        var end = page.IndexOf("</footer>", StringComparison.Ordinal);
        Assert.InRange(start, 0, end);
        return page[start..end];
    }

    private string MakeFolder(params string[] parts)
    {
        var path = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(path);
        return path;
    }

    private void MakeArtifact(string folder, string fileName, string caption)
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

    private void MakeVideo(string folder, string fileName, string caption)
    {
        File.WriteAllText(Path.Combine(folder, fileName), "[InternetShortcut]\nURL=https://youtu.be/abc\n");
        File.WriteAllText(Path.Combine(folder, fileName + ".yaml"),
            $"""
             type: video
             caption: {caption}
             videoId: abc
             """);
    }

    private static Dir2SiteModel Config(params FooterItem[] items) => new()
    {
        Title = "My Site",
        Footer = "© 2026",
        FooterItems = [.. items],
    };

    private (string Summary,
        IReadOnlyList<string> Errors,
        IReadOnlyList<string> Warnings,
        IReadOnlyList<string> Orphans) Generate(Dir2SiteModel config)
    {
        var tree = DirectoryTraverser.BuildTree(_root, new List<string>(), new List<string>());
        return SiteGenerator.Generate(_root, tree, config);
    }

    // A second artifact keeps a folder a collection: one on its own is published as that artifact,
    // which is a different href and is covered by its own test below.
    private void MakeCollection(string name)
    {
        var folder = MakeFolder(name);
        MakeArtifact(folder, "One.jpg", "One");
        MakeArtifact(folder, "Two.jpg", "Two");
    }

    [AvaloniaFact]
    public void ItemsAreGroupedIntoTheColumnsTheyAskedFor()
    {
        MakeCollection("Photographs");

        Generate(Config(
            new FooterItem { Column = 1, Title = "First", Link = "https://example.test/one" },
            new FooterItem { Column = 2, Title = "Second", Link = "https://example.test/two" },
            new FooterItem { Column = 1, Title = "Third", Link = "https://example.test/three" }));

        var footer = ReadFooter();
        Assert.Equal(2, Occurrences(footer, "<div class=\"col\">"));
        Assert.Contains("row-cols-lg-2", footer);

        // Order within a column is the order they were written, not the order of the whole list.
        Assert.True(footer.IndexOf("First", StringComparison.Ordinal) < footer.IndexOf("Third", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public void EmptyColumnsCloseUpRatherThanLeavingAGap()
    {
        MakeCollection("Photographs");

        Generate(Config(
            new FooterItem { Column = 1, Title = "First", Link = "https://example.test/one" },
            new FooterItem { Column = 3, Title = "Second", Link = "https://example.test/two" }));

        var footer = ReadFooter();
        Assert.Equal(2, Occurrences(footer, "<div class=\"col\">"));
        Assert.Contains("row-cols-lg-2", footer);
    }

    [AvaloniaFact]
    public void AProjectPathResolvesToWhereThatArtifactPublishes()
    {
        var folder = MakeFolder("Photographs", "1890s");
        MakeArtifact(folder, "Portrait.jpg", "A Portrait");
        MakeArtifact(folder, "Landscape.jpg", "A Landscape");

        Generate(Config(new FooterItem { Title = "The Portrait", Link = "Photographs/1890s/Portrait.jpg" }));

        Assert.Contains("href=\"Photographs/1890s/Portrait/\"", ReadPage());
    }

    [AvaloniaFact]
    public void ANestedPageGetsTheSameTargetThroughItsOwnPrefix()
    {
        var folder = MakeFolder("Photographs", "1890s");
        MakeArtifact(folder, "Portrait.jpg", "A Portrait");
        MakeArtifact(folder, "Landscape.jpg", "A Landscape");

        Generate(Config(new FooterItem { Title = "The Portrait", Link = "Photographs/1890s/Portrait.jpg" }));

        Assert.Contains("href=\"../../Photographs/1890s/Portrait/\"", ReadPage("Photographs", "1890s"));
    }

    [AvaloniaFact]
    public void AFolderShownAsItsOnlyArtifactIsLinkedAtTheFoldersOwnAddress()
    {
        var folder = MakeFolder("-Info");
        MakeArtifact(folder, "About.jpg", "About Us");

        Generate(Config(new FooterItem { Title = "About", Link = "-Info/About.jpg" }));

        // Not "Info/About/": the folder holds one artifact, so it is published as that artifact.
        Assert.Contains("href=\"Info/\"", ReadPage());
    }

    [AvaloniaFact]
    public void AnExternalLinkIsLeftAloneAndOpensInANewTab()
    {
        MakeCollection("Photographs");

        Generate(Config(new FooterItem { Title = "Off Site", Link = "https://example.test/channel" }));

        var page = ReadPage("Photographs");
        Assert.Contains("href=\"https://example.test/channel\"", page);
        Assert.Contains("target=\"_blank\" rel=\"noopener noreferrer\"", page);
    }

    [AvaloniaFact]
    public void ASiteRelativeLinkKeepsThePrefixAndOneSlash()
    {
        MakeCollection("Photographs");

        Generate(Config(new FooterItem { Title = "Privacy", Link = "/privacy/" }));

        Assert.Contains("href=\"privacy/\"", ReadPage());
        Assert.Contains("href=\"../privacy/\"", ReadPage("Photographs"));
    }

    [AvaloniaFact]
    public void ALinkToNothingIsWarnedAboutAndShownWithoutALink()
    {
        MakeCollection("Photographs");

        var result = Generate(Config(new FooterItem { Title = "Ghost", Link = "Nowhere/Nothing.md" }));

        Assert.Contains(result.Warnings, w => w.Contains("Ghost") && w.Contains("isn't in the project"));
        Assert.Empty(result.Errors);

        // Dropping it made a typo look like the row had never been written. It stays, as text.
        var footer = ReadFooter();
        Assert.Contains("Ghost", footer);
        Assert.Contains("footer-link-unlinked", footer);
        Assert.DoesNotContain("href=\"Nowhere", footer);
    }

    [AvaloniaFact]
    public void ARowWithNoLinkAtAllIsStillShown()
    {
        MakeCollection("Photographs");

        var result = Generate(Config(new FooterItem { Title = "Just Words", Icon = "bi-lock" }));

        Assert.Contains(result.Warnings, w => w.Contains("Just Words") && w.Contains("no link"));

        var footer = ReadFooter();
        Assert.Contains("Just Words", footer);
        // The icon it was given comes along, so the row still reads as the one that was written.
        Assert.Contains("bi-lock", footer);
    }

    [AvaloniaFact]
    public void ALinkToAVideoIsWarnedAboutBecauseItHasNoPage()
    {
        var folder = MakeFolder("Films");
        MakeVideo(folder, "Clip.url", "A Clip");
        MakeArtifact(folder, "Still.jpg", "A Still");

        var result = Generate(Config(new FooterItem { Title = "The Clip", Link = "Films/Clip.url" }));

        Assert.Contains(result.Warnings, w => w.Contains("The Clip") && w.Contains("video"));
        Assert.Contains("The Clip", ReadFooter());
        Assert.Contains("footer-link-unlinked", ReadFooter());
    }

    [AvaloniaFact]
    public void AnIconIsAcceptedWithOrWithoutItsPrefix()
    {
        MakeCollection("Photographs");

        Generate(Config(
            new FooterItem { Title = "Bare", Icon = "envelope", Link = "https://example.test/a" },
            new FooterItem { Title = "Prefixed", Icon = "bi-lock", Link = "https://example.test/b" }));

        var page = ReadPage();
        Assert.Contains("class=\"bi bi-envelope fs-5\"", page);
        Assert.Contains("class=\"bi bi-lock fs-5\"", page);
    }

    [AvaloniaFact]
    public void AnIconThatIsNotAnIconNameIsDroppedRatherThanWritten()
    {
        MakeCollection("Photographs");

        var result = Generate(Config(
            new FooterItem { Title = "Sneaky", Icon = "x\" onload=alert(1) \"", Link = "https://example.test/a" }));

        var page = ReadPage();
        Assert.Contains(result.Warnings, w => w.Contains("Sneaky") && w.Contains("Bootstrap Icons name"));
        Assert.DoesNotContain("onload", page);
        // The row itself survives — only its icon was the problem.
        Assert.Contains("Sneaky", page);
    }

    [AvaloniaFact]
    public void IconColorsReachTheStyleAttributeOnlyWhenTheyAreHex()
    {
        MakeCollection("Photographs");

        var result = Generate(Config(
            new FooterItem { Title = "Brand", Icon = "bi-youtube", IconColor = "#ff0000", IconBackground = "#ffffff", Link = "https://example.test/a" },
            new FooterItem { Title = "Sneaky", Icon = "bi-lock", IconColor = "red;background:url(x)", Link = "https://example.test/b" }));

        var page = ReadPage();
        Assert.Contains("style=\"color:#ff0000\"", page);
        Assert.Contains("footer-icon-knockout", page);
        Assert.Contains("--knockout:#ffffff", page);

        Assert.Contains(result.Warnings, w => w.Contains("Sneaky") && w.Contains("iconColor"));
        Assert.DoesNotContain("background:url", page);
    }

    [AvaloniaFact]
    public void AQuoteInALinkCannotEscapeTheHrefAttribute()
    {
        MakeCollection("Photographs");

        // The realistic case is a pasted URL carrying a quote, not an attack — but either way it
        // must not close the attribute and turn the rest of the line into markup.
        Generate(Config(
            new FooterItem { Title = "Pasted", Link = "https://example.test/a\" onmouseover=\"alert(1)" },
            new FooterItem { Title = "Site Path", Link = "/page\" onmouseover=\"alert(1)" }));

        var footer = ReadFooter();
        Assert.DoesNotContain("onmouseover=\"", footer);
        Assert.Contains("&quot;", footer);
        // Both rows still render — escaping is not dropping them.
        Assert.Contains("Pasted", footer);
        Assert.Contains("Site Path", footer);
    }

    [AvaloniaFact]
    public void ABrandIconGetsItsOwnColorsWithoutBeingAsked()
    {
        MakeCollection("Photographs");

        // The whole point: bi-youtube on its own must not render as a mark with the footer showing
        // through the play triangle, which is the wrong-looking result and the easiest to get.
        Generate(Config(new FooterItem { Title = "Watch", Icon = "bi-youtube", Link = "https://example.test/a" }));

        var footer = ReadFooter();
        Assert.Contains("style=\"color:#ff0000\"", footer);
        Assert.Contains("--knockout:#ffffff", footer);
        Assert.Contains("footer-icon-knockout", footer);
    }

    [AvaloniaFact]
    public void AnOrdinaryIconGetsNoColorsOfItsOwn()
    {
        MakeCollection("Photographs");

        Generate(Config(new FooterItem { Title = "Contact", Icon = "bi-envelope", Link = "https://example.test/a" }));

        var footer = ReadFooter();
        Assert.DoesNotContain("style=\"color:", footer);
        Assert.DoesNotContain("footer-icon-knockout", footer);
    }

    [AvaloniaFact]
    public void SayingEitherColorTurnsTheBrandDefaultsOff()
    {
        MakeCollection("Photographs");

        // Naming a color is the author taking charge of the mark; filling in the other half of a
        // brand's palette underneath them would be a surprise.
        Generate(Config(new FooterItem
        {
            Title = "Muted",
            Icon = "bi-youtube",
            IconColor = "#999999",
            Link = "https://example.test/a",
        }));

        var footer = ReadFooter();
        Assert.Contains("style=\"color:#999999\"", footer);
        Assert.DoesNotContain("#ff0000", footer);
        Assert.DoesNotContain("footer-icon-knockout", footer);
    }

    [AvaloniaFact]
    public void AMailtoLinkWorksAndDoesNotOpenAnEmptyTab()
    {
        MakeCollection("Photographs");

        Generate(Config(new FooterItem { Title = "Mail Us", Link = "mailto:hello@example.test" }));

        var footer = ReadFooter();
        Assert.Contains("href=\"mailto:hello@example.test\"", footer);
        // Absolute, so it takes no prefix — but it hands off to a mail client rather than loading a
        // page, and a new tab for that just leaves an empty one behind.
        Assert.DoesNotContain("target=\"_blank\"", footer);
        Assert.DoesNotContain("../mailto", footer);
    }

    [AvaloniaFact]
    public void AnExternalLinkDisclaimsTheReferrerAsWellAsTheOpener()
    {
        MakeCollection("Photographs");

        Generate(Config(new FooterItem { Title = "Off Site", Link = "https://example.test/a" }));

        Assert.Contains("rel=\"noopener noreferrer\"", ReadFooter());
    }

    [AvaloniaFact]
    public void AColumnOutsideTheRangeIsMovedAndSaidSo()
    {
        MakeCollection("Photographs");

        var result = Generate(Config(
            new FooterItem { Column = 99, Title = "TooHigh", Link = "https://example.test/a" },
            new FooterItem { Column = 0, Title = "TooLow", Link = "https://example.test/b" },
            new FooterItem { Column = 2, Title = "Fine", Link = "https://example.test/c" }));

        Assert.Contains(result.Warnings, w => w.Contains("TooHigh") && w.Contains("column 99"));
        Assert.Contains(result.Warnings, w => w.Contains("TooLow") && w.Contains("column 0"));
        // A column that was already in range says nothing.
        Assert.DoesNotContain(result.Warnings, w => w.Contains("Fine"));

        // Still rendered, just moved — clamping is not dropping them.
        var footer = ReadFooter();
        Assert.Contains("TooHigh", footer);
        Assert.Contains("TooLow", footer);
    }

    [AvaloniaFact]
    public void AHexColorIsAcceptedOnlyAtTheLengthsCssHas()
    {
        MakeCollection("Photographs");

        var result = Generate(Config(
            new FooterItem { Title = "Shorthand", Icon = "bi-lock", IconColor = "#f00", Link = "https://example.test/a" },
            new FooterItem { Title = "Alpha", Icon = "bi-lock", IconColor = "#f00f", Link = "https://example.test/b" },
            new FooterItem { Title = "Nonsense", Icon = "bi-lock", IconColor = "#12345", Link = "https://example.test/c" }));

        var footer = ReadFooter();
        Assert.Contains("color:#f00\"", footer);
        Assert.Contains("color:#f00f\"", footer);
        // Five digits is not a CSS color, and IsDarkColor could not read it either.
        Assert.DoesNotContain("#12345", footer);
        Assert.Contains(result.Warnings, w => w.Contains("Nonsense") && w.Contains("iconColor"));
    }

    [AvaloniaFact]
    public void AFourDigitFooterColorIsReadForDarknessRatherThanAssumedDark()
    {
        MakeCollection("Photographs");

        var config = Config();
        config.FooterColor = "#fffe";   // very light, with alpha
        Generate(config);

        var css = File.ReadAllText(Path.Combine(_root, "_site", "css", "site.css"));
        Assert.Contains("#555555", css);
        Assert.DoesNotContain("rgba(255, 255, 255, 0.72)", css);
    }

    [AvaloniaFact]
    public void ARowWithNoIconBackgroundCarriesNoKnockoutMarkup()
    {
        MakeCollection("Photographs");

        Generate(Config(new FooterItem { Title = "Plain", Icon = "bi-lock", Link = "https://example.test/a" }));

        var page = ReadPage();
        Assert.DoesNotContain("footer-icon-knockout", page);
        Assert.DoesNotContain("--knockout", page);
    }

    [AvaloniaFact]
    public void TitlesAndNotesAreEscapedButTheFooterStringIsNot()
    {
        MakeCollection("Photographs");

        var config = Config(new FooterItem
        {
            Title = "Tom & Jerry <b>",
            Note = "5 > 3 & counting",
            Link = "https://example.test/a",
        });
        config.Footer = "&copy; 2026<br>All rights reserved.";

        Generate(config);

        var page = ReadPage();
        Assert.Contains("Tom &amp; Jerry &lt;b&gt;", page);
        Assert.Contains("5 &gt; 3 &amp; counting", page);
        // The one field that has always been raw HTML, because it holds the copyright line.
        Assert.Contains("&copy; 2026<br>All rights reserved.", page);
    }

    [AvaloniaFact]
    public void NoFooterItemsLeavesTheOldSingleBarFooter()
    {
        MakeCollection("Photographs");

        Generate(Config());

        var footer = ReadFooter();
        Assert.Contains("© 2026", footer);
        Assert.DoesNotContain("<div class=\"col\">", footer);
        // Nothing to divide, so no rule above the copyright line.
        Assert.DoesNotContain("border-top", footer);
        // And no band: one sentence marooned on a colored strip is worse than the plain line that
        // every project had before columns existed.
        Assert.DoesNotContain("has-columns", footer);
    }

    [AvaloniaFact]
    public void TheBandBelongsToAFooterThatHasColumns()
    {
        MakeCollection("Photographs");

        Generate(Config(new FooterItem { Title = "Row", Link = "https://example.test/a" }));

        Assert.Contains("has-columns", ReadFooter());

        var css = File.ReadAllText(Path.Combine(_root, "_site", "css", "site.css"));
        // The color is on the qualified selector, so a footer without columns cannot pick it up.
        Assert.Contains(".site-footer.has-columns { background-color:", css);
        // And the footer is pushed down, so a short page has no white left under it.
        Assert.Contains(".site-footer { margin-top: auto; }", css);
    }

    [AvaloniaFact]
    public void TheFooterColorFallsBackToThePrimaryColor()
    {
        MakeCollection("Photographs");

        var config = Config();
        config.PrimaryColor = "#223355";
        config.FooterColor = string.Empty;
        Generate(config);

        var css = File.ReadAllText(Path.Combine(_root, "_site", "css", "site.css"));
        Assert.Contains(".site-footer.has-columns { background-color: #223355; }", css);
        // Dark, so the band takes light text.
        Assert.Contains("rgba(255, 255, 255, 0.72)", css);
    }

    [AvaloniaFact]
    public void ALightFooterColorGetsDarkTextInstead()
    {
        MakeCollection("Photographs");

        var config = Config();
        config.FooterColor = "#f5f5f5";
        Generate(config);

        var css = File.ReadAllText(Path.Combine(_root, "_site", "css", "site.css"));
        Assert.Contains(".site-footer.has-columns { background-color: #f5f5f5; }", css);
        Assert.Contains("#555555", css);
        Assert.DoesNotContain("rgba(255, 255, 255, 0.72)", css);
    }

    private static int Occurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }
}
