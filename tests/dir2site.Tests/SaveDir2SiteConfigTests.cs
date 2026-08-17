// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.IO;
using dir2site.Models;
using dir2site.Services;
using Xunit;

namespace dir2site.Tests;

/// <summary>
/// The project config's write path — reached whenever a setting is edited, and the one that used to
/// eat a hand-edited dir2site.yaml the first time the user clicked Generate.
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

    /// <summary>
    /// Saving an unchanged config with a footer must not touch the file at all.
    /// </summary>
    /// <remarks>
    /// Byte equality is not enough, and asserting only that is how this was missed. The bytes were
    /// always identical — <c>SetBlock</c> rewrote the block with exactly what it already said. What
    /// changed was the mtime, and under auto-generate the mtime is the whole story: the file sits in
    /// the watched folder, so a rewrite is a change, a change is a rebuild, and a rebuild used to
    /// save the config again. A project with a footer never stopped rebuilding.
    ///
    /// So the assertion is about whether the file was written, not about what it says.
    /// </remarks>
    [Fact]
    public void FooterItems_SavingTwiceDoesNotRewriteTheFile()
    {
        var config = Sample();
        config.FooterItems = [new FooterItem { Title = "Example About", Link = "-Info/About.md" }];

        YamlParser.SaveDir2SiteConfig(Path_, config);
        var before = File.ReadAllText(Path_);

        // Backdated so a rewrite is unmissable rather than a sub-millisecond difference.
        var untouched = DateTime.UtcNow.AddMinutes(-10);
        File.SetLastWriteTimeUtc(Path_, untouched);

        YamlParser.SaveDir2SiteConfig(Path_, config);

        Assert.Equal(before, File.ReadAllText(Path_));
        Assert.Equal(untouched, File.GetLastWriteTimeUtc(Path_));
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

    /// <summary>
    /// A setting whose text is a YAML null token has to come back as that text.
    /// </summary>
    /// <remarks>
    /// YamlDotNet quotes a string that would re-read as a number or a boolean, and does not quote one
    /// that would re-read as null — so a title of <c>~</c> was written bare, came back as null on the
    /// next load, and the save after that threw a <see cref="NullReferenceException"/> from inside
    /// the emitter. While Generate wrote the config that throw was outside its try/catch and escaped
    /// as an unobserved task exception, which is why nobody saw a message.
    /// </remarks>
    [Theory]
    [InlineData("~")]
    [InlineData("null")]
    [InlineData("NULL")]
    [InlineData("Null")]
    public void ATitleThatSpellsNull_SurvivesTheRoundTrip(string title)
    {
        YamlParser.SaveDir2SiteConfig(Path_, new Dir2SiteModel { Title = title });

        var loaded = YamlParser.DeserializeAs<Dir2SiteModel>(File.ReadAllText(Path_));
        Assert.Equal(title, loaded.Title);

        // And saving what came back has to be an ordinary no-op, not a crash.
        YamlParser.SaveDir2SiteConfig(Path_, loaded);
        Assert.Equal(title, YamlParser.DeserializeAs<Dir2SiteModel>(File.ReadAllText(Path_)).Title);
    }

    /// <summary>
    /// A hand-written null still has to be saveable, whatever we would have written ourselves.
    /// </summary>
    /// <remarks>
    /// Quoting on the way out stops us creating this, and does nothing about a file someone wrote by
    /// hand — <c>title:</c> with nothing after it is the ordinary way to spell an empty setting, and
    /// it deserializes to null just the same.
    /// </remarks>
    [Fact]
    public void AHandWrittenNullTitle_DoesNotThrowOnTheNextSave()
    {
        File.WriteAllText(Path_, "title: ~\nfooter: © 2026\n");
        var loaded = YamlParser.DeserializeAs<Dir2SiteModel>(File.ReadAllText(Path_));

        YamlParser.SaveDir2SiteConfig(Path_, loaded);

        Assert.Contains("© 2026", File.ReadAllText(Path_), StringComparison.Ordinal);
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
