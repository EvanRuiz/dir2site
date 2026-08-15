// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.IO;
using dir2site.Models;
using dir2site.Services;
using Xunit;

namespace dir2site.Tests;

/// <summary>
/// Generate Site writes the project config on every run, so this is the path that used to eat a
/// hand-edited dir2site.yaml the first time the user clicked the button.
/// </summary>
public class SaveDir2SiteConfigTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "d2s-cfg-" + Guid.NewGuid().ToString("N"));

    public SaveDir2SiteConfigTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string Path_ => System.IO.Path.Combine(_dir, "dir2site.yaml");

    private static Dir2SiteModel Sample() => new()
    {
        Title = "My Site",
        Footer = "© 2026",
        PrimaryColor = "#333333",
        SecondaryColor = "#666666",
        BackgroundColor = "#ffffff",
        NavbarDark = true,
        CardBreadcrumbs = true,
        PdfResizeEnabled = true,
        PdfMaxWidth = 1600,
        PdfQuality = 80,
    };

    [Fact]
    public void CreatesTheFileWhenAbsent()
    {
        YamlParser.SaveDir2SiteConfig(Path_, Sample());

        var loaded = YamlParser.DeserializeAs<Dir2SiteModel>(File.ReadAllText(Path_));
        Assert.Equal("My Site", loaded.Title);
        Assert.Equal(1600, loaded.PdfMaxWidth);
    }

    [Fact]
    public void ChangingATitle_KeepsCommentsAndUnknownKeys()
    {
        File.WriteAllText(Path_,
            """
            # My site config — please don't eat my notes
            title: Old Name
            footer: © 2026

            # Colors picked to match the logo
            primaryColor: '#333333'
            secondaryColor: '#666666'
            backgroundColor: '#ffffff'
            navbarDark: true
            cardBreadcrumbs: true
            pdfResizeEnabled: true
            pdfMaxWidth: 1600
            pdfQuality: 80
            experimentalThing: yes please
            """);

        var config = Sample();
        config.Title = "New Name";
        YamlParser.SaveDir2SiteConfig(Path_, config);

        var result = File.ReadAllText(Path_);
        Assert.Contains("# My site config — please don't eat my notes", result);
        Assert.Contains("# Colors picked to match the logo", result);
        Assert.Contains("experimentalThing: yes please", result);
        Assert.Contains("title: New Name", result);
        Assert.DoesNotContain("Old Name", result);
    }

    [Fact]
    public void BooleansAndNumbers_StayBareNotQuoted()
    {
        YamlParser.SaveDir2SiteConfig(Path_, Sample());
        var config = Sample();
        config.NavbarDark = false;
        config.CardBreadcrumbs = false;
        config.PdfMaxWidth = 1200;

        YamlParser.SaveDir2SiteConfig(Path_, config);

        var result = File.ReadAllText(Path_);
        Assert.Contains("navbarDark: false", result);
        Assert.Contains("cardBreadcrumbs: false", result);
        Assert.Contains("pdfMaxWidth: 1200", result);
        Assert.DoesNotContain("\"false\"", result);
        Assert.DoesNotContain("'1200'", result);

        var loaded = YamlParser.DeserializeAs<Dir2SiteModel>(result);
        Assert.False(loaded.NavbarDark);
        Assert.False(loaded.CardBreadcrumbs);
        Assert.Equal(1200, loaded.PdfMaxWidth);
    }

    [Fact]
    public void NoChanges_LeaveTheFileByteIdentical()
    {
        YamlParser.SaveDir2SiteConfig(Path_, Sample());
        var before = File.ReadAllText(Path_);

        YamlParser.SaveDir2SiteConfig(Path_, Sample());

        Assert.Equal(before, File.ReadAllText(Path_));
    }

    // footerItems is the only setting that is a list of records rather than a single value, so it
    // takes the block-rewriting path rather than the scalar one.

    [Fact]
    public void FooterItems_RoundTripThroughTheFile()
    {
        var config = Sample();
        config.FooterItems =
        [
            new FooterItem
            {
                Column = 2,
                Icon = "bi-youtube",
                IconColor = "#ff0000",
                IconBackground = "#ffffff",
                Title = "Example External Link",
                Link = "https://example.test/channel",
                Note = "12,000+ views",
            },
            new FooterItem { Column = 1, Title = "Example Privacy", Link = "--Footer/Privacy.md" },
        ];

        YamlParser.SaveDir2SiteConfig(Path_, config);
        var loaded = YamlParser.DeserializeAs<Dir2SiteModel>(File.ReadAllText(Path_));

        Assert.Equal(2, loaded.FooterItems.Count);
        Assert.Equal("bi-youtube", loaded.FooterItems[0].Icon);
        Assert.Equal("#ff0000", loaded.FooterItems[0].IconColor);
        Assert.Equal("#ffffff", loaded.FooterItems[0].IconBackground);
        Assert.Equal("12,000+ views", loaded.FooterItems[0].Note);
        Assert.Equal(2, loaded.FooterItems[0].Column);
        Assert.Equal("--Footer/Privacy.md", loaded.FooterItems[1].Link);
    }

    [Fact]
    public void FooterItems_LeaveTheRestOfAHandEditedFileAlone()
    {
        File.WriteAllText(Path_,
            """
            # My site config — please don't eat my notes
            title: Old Name
            footer: © 2026
            primaryColor: '#333333'
            """);

        var config = Sample();
        config.FooterItems = [new FooterItem { Title = "Example About", Link = "-Info/About.md" }];
        YamlParser.SaveDir2SiteConfig(Path_, config);

        var text = File.ReadAllText(Path_);
        Assert.Contains("# My site config — please don't eat my notes", text);
        Assert.Contains("footerItems:", text);
        Assert.Contains("Example About", text);
    }

    [Fact]
    public void FooterItems_SavingTwiceIsIdempotent()
    {
        var config = Sample();
        config.FooterItems = [new FooterItem { Title = "Example About", Link = "-Info/About.md" }];

        YamlParser.SaveDir2SiteConfig(Path_, config);
        var before = File.ReadAllText(Path_);
        YamlParser.SaveDir2SiteConfig(Path_, config);

        Assert.Equal(before, File.ReadAllText(Path_));
    }

    [Fact]
    public void ClearingTheFooterItems_RemovesTheKey()
    {
        var config = Sample();
        config.FooterItems = [new FooterItem { Title = "Example About", Link = "-Info/About.md" }];
        YamlParser.SaveDir2SiteConfig(Path_, config);
        Assert.Contains("footerItems:", File.ReadAllText(Path_));

        config.FooterItems = [];
        YamlParser.SaveDir2SiteConfig(Path_, config);

        var text = File.ReadAllText(Path_);
        Assert.DoesNotContain("footerItems", text);
        // Everything else is still there — only the one block went.
        Assert.Contains("title: My Site", text);
    }

    [Fact]
    public void AProjectWithNoFooterItems_NeverGrowsAnEmptyBlock()
    {
        YamlParser.SaveDir2SiteConfig(Path_, Sample());

        Assert.DoesNotContain("footerItems", File.ReadAllText(Path_));
    }

    [Fact]
    public void UnparseableConfig_IsRewrittenRatherThanLost()
    {
        File.WriteAllText(Path_, "title: [broken\n  : nonsense {\n");

        YamlParser.SaveDir2SiteConfig(Path_, Sample());

        var loaded = YamlParser.DeserializeAs<Dir2SiteModel>(File.ReadAllText(Path_));
        Assert.Equal("My Site", loaded.Title);
    }
}
