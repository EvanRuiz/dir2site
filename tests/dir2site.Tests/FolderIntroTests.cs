// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Headless.XUnit;
using dir2site.Models;
using dir2site.Services;
using dir2site.ViewModels;
using Xunit;

namespace dir2site.Tests;

/// <summary>
/// A folder's <c>index.md</c> is its introduction: prose at the top of the folder's own page,
/// belonging to the folder rather than sitting in it.
/// </summary>
/// <remarks>
/// The claim worth pinning is that it is <em>not an artifact</em>. Everything that goes wrong here
/// goes wrong by treating it as one — a card in its own folder, a page at <c>folder/index/</c>, a
/// sidecar it never asked for, or a published directory that the auto-generate path then moves or
/// deletes when the file is renamed away.
/// </remarks>
public class FolderIntroTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "d2s-intro-" + Guid.NewGuid().ToString("N"));

    public FolderIntroTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private string SitePath(params string[] parts) => Path.Combine([_root, "_site", .. parts]);
    private string ReadPage(params string[] parts) => File.ReadAllText(SitePath([.. parts, "index.html"]));

    private static Dir2SiteModel Config() => new()
    {
        Title = "My Site",
        Footer = "© 2026",
        SiteUrl = "https://example.test",
    };

    private string MakeFolder(params string[] parts)
    {
        var path = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void MakeArtifact(string folder, string fileName, string caption)
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

    private static void MakeIntro(string folder, string markdown) =>
        File.WriteAllText(Path.Combine(folder, "index.md"), markdown);

    private DirectoryTreeItem Scan() =>
        DirectoryTraverser.BuildTree(_root, new List<string>(), new List<string>());

    private (string Summary, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings,
        IReadOnlyList<string> Orphans) Generate() =>
        SiteGenerator.Generate(_root, Scan(), Config());

    // ---- what it puts on the page -----------------------------------------

    [AvaloniaFact]
    public void TheIntroIsRenderedAboveTheFoldersCards()
    {
        var folder = MakeFolder("Photographs");
        MakeArtifact(folder, "Portrait.jpg", "A Portrait");
        MakeIntro(folder, "# The 1993 survey\n\nTwenty-six soundings, taken at the spring tide.\n");

        Generate();

        var page = ReadPage("Photographs");
        Assert.Contains("Twenty-six soundings", page);
        // Above the grid, not among it.
        Assert.True(page.IndexOf("Twenty-six soundings", StringComparison.Ordinal)
                  < page.IndexOf("A Portrait", StringComparison.Ordinal));
    }

    /// <summary>
    /// The introduction is the top of the page, so the generated heading steps aside — that is how a
    /// folder gets a title other than its own name, or none where the name would only be said twice.
    /// </summary>
    [AvaloniaFact]
    public void AnIntroReplacesTheGeneratedHeading()
    {
        var folder = MakeFolder("Photographs");
        MakeArtifact(folder, "Portrait.jpg", "A Portrait");
        MakeIntro(folder, "## Plates from the 1993 survey\n\nTwenty-six soundings.\n");

        Generate();

        var page = ReadPage("Photographs");
        Assert.Contains("Plates from the 1993 survey", page);
        Assert.DoesNotContain("<h2 class=\"mb-4\">Photographs</h2>", page);
        // The folder is still named where a reader needs it to navigate.
        Assert.Contains("Photographs", page);
        Assert.Contains("breadcrumb", page);
    }

    /// <summary>A folder with no introduction keeps the heading it always had.</summary>
    [AvaloniaFact]
    public void WithoutAnIntroTheGeneratedHeadingStays()
    {
        var folder = MakeFolder("Photographs");
        MakeArtifact(folder, "Portrait.jpg", "A Portrait");
        // Two, so the folder stays a collection: one artifact and no intro publishes as the
        // artifact, and an artifact page has no collection heading to keep.
        MakeArtifact(folder, "Landscape.jpg", "A Landscape");

        Generate();

        Assert.Contains("<h2 class=\"mb-4\">Photographs</h2>", ReadPage("Photographs"));
    }

    /// <summary>
    /// The measure belongs to an article, not to rendered markdown: an introduction shares the
    /// typography and takes none of the column, so it fills the page the cards fill.
    /// </summary>
    [AvaloniaFact]
    public void AnIntroTakesTheMarkdownStylingButNotTheArticleColumn()
    {
        var folder = MakeFolder("Photographs");
        MakeArtifact(folder, "Portrait.jpg", "A Portrait");
        MakeIntro(folder, "Prose.\n");

        Generate();

        var page = ReadPage("Photographs");
        Assert.Contains("collection-intro markdown-body", page);
        Assert.DoesNotContain("article-column", page);
    }

    /// <summary>The site root is a collection page too, and the one with no other way to say anything.</summary>
    [AvaloniaFact]
    public void TheRootCanHaveAnIntroduction()
    {
        MakeArtifact(MakeFolder("Photographs"), "Portrait.jpg", "A Portrait");
        MakeIntro(_root, "Welcome to the press.\n");

        Generate();

        Assert.Contains("Welcome to the press.", ReadPage());
    }

    [AvaloniaFact]
    public void TheIntroIsNotACardAndHasNoPageOfItsOwn()
    {
        var folder = MakeFolder("Photographs");
        MakeArtifact(folder, "Portrait.jpg", "A Portrait");
        MakeIntro(folder, "Prose, not an exhibit.\n");

        Generate();

        Assert.False(Directory.Exists(SitePath("Photographs", "index")));
        // One card, for the photo. The badge counts what the page shows.
        Assert.Contains("1 item", ReadPage());
    }

    /// <summary>
    /// The file being there is the whole decision. Whatever it renders to — prose, a picture, a note
    /// to self, nothing at all — the folder's own heading steps aside, because inspecting the content
    /// to decide would give the author a rule with an edge they cannot see in the file.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("Prose.\n")]
    [InlineData("![The chart](_media/chart.png)\n")]
    [InlineData("| a | b |\n|---|---|\n| 1 | 2 |\n")]
    [InlineData("<!-- todo: write this -->\n")]
    [InlineData("   \n\n\t\n")]
    [InlineData("")]
    public void AnIntroAlwaysTakesTheHeading(string markdown)
    {
        var folder = MakeFolder("Photographs");
        MakeArtifact(folder, "Portrait.jpg", "A Portrait");
        MakeArtifact(folder, "Landscape.jpg", "A Landscape");
        MakeIntro(folder, markdown);

        Generate();

        Assert.DoesNotContain("<h2 class=\"mb-4\">Photographs</h2>", ReadPage("Photographs"));
    }

    /// <summary>
    /// Special-cased all the way down: an introduction is prose, so there is nothing to caption,
    /// credit or date, and the scan must not write it a settings file nobody asked for.
    /// </summary>
    [AvaloniaFact]
    public void NoSidecarIsWrittenForAnIntro()
    {
        var folder = MakeFolder("Photographs");
        MakeIntro(folder, "Prose.\n");

        Scan();

        Assert.False(File.Exists(Path.Combine(folder, "index.md.yaml")));
        Assert.Empty(Directory.GetFiles(folder, "*.yaml"));
    }

    /// <summary>
    /// An article is published a directory below its source, an intro at its folder — so the
    /// <c>../</c> that makes the first one's links resolve breaks the second one's.
    /// </summary>
    [AvaloniaFact]
    public void RelativeLinksInAnIntroAreLeftAsWritten()
    {
        var folder = MakeFolder("Photographs");
        Directory.CreateDirectory(Path.Combine(folder, "_media"));
        File.WriteAllText(Path.Combine(folder, "_media", "chart.png"), "not really a png");
        MakeArtifact(folder, "Portrait.jpg", "A Portrait");
        MakeIntro(folder, "![The chart](_media/chart.png)\n");

        Generate();

        var page = ReadPage("Photographs");
        Assert.Contains("\"_media/chart.png\"", page);
        Assert.DoesNotContain("\"../_media/chart.png\"", page);
    }

    /// <summary>
    /// A folder holding one artifact publishes as that artifact — but not when it has prose of its
    /// own, which the collapse would drop with nothing to say where it went.
    /// </summary>
    [AvaloniaFact]
    public void AFolderWithAnIntroKeepsItsOwnPage()
    {
        var folder = MakeFolder("Photographs");
        MakeArtifact(folder, "Portrait.jpg", "A Portrait");
        MakeIntro(folder, "Why this photograph is here.\n");

        Generate();

        Assert.Contains("Why this photograph is here.", ReadPage("Photographs"));
        // The photo still has its own page, rather than being promoted to the folder's.
        Assert.True(Directory.Exists(SitePath("Photographs", "Portrait")));
    }

    // ---- auto-generate ------------------------------------------------------

    /// <summary>
    /// Editing an intro has to re-render the page it sits on. The scope narrows to individual pages
    /// when a folder's membership held still, and an intro belongs to no page of its own — so what
    /// this pins is that the folder index is written anyway.
    /// </summary>
    [AvaloniaFact]
    public void EditingAnIntroRendersTheFolderPageAgain()
    {
        var folder = MakeFolder("Photographs");
        MakeArtifact(folder, "Portrait.jpg", "A Portrait");
        MakeIntro(folder, "First words.\n");
        Generate();

        MakeIntro(folder, "Second words.\n");
        var intro = Path.Combine(folder, "index.md");
        var scope = RenderScope.For(_root, [new SourceChange(SourceChangeKind.Updated, intro)], Scan());

        Assert.True(scope.ShouldRender(SitePath(), SitePath("Photographs", "index.html")));

        SiteGenerator.Generate(_root, Scan(), Config(), scope: scope);
        Assert.Contains("Second words.", ReadPage("Photographs"));
        Assert.DoesNotContain("First words.", ReadPage("Photographs"));
    }

    /// <summary>
    /// Deleting an intro publishes nothing away with it. "Photographs/index" is a real address —
    /// a sub-folder can be called index — so the removal must not be read as an artifact's.
    /// </summary>
    [AvaloniaFact]
    public void RemovingAnIntroTakesNoPublishedFolderWithIt()
    {
        var folder = MakeFolder("Photographs");
        MakeArtifact(folder, "Portrait.jpg", "A Portrait");
        var indexFolder = MakeFolder("Photographs", "index");
        MakeArtifact(indexFolder, "Plate.jpg", "A Plate");
        MakeIntro(folder, "Prose.\n");
        Generate();

        var intro = Path.Combine(folder, "index.md");
        File.Delete(intro);
        SiteChangeApplier.Apply(_root, [new SourceChange(SourceChangeKind.Removed, intro)]);

        // The sub-folder called "index" is still published; only the prose is gone.
        Assert.True(Directory.Exists(SitePath("Photographs", "index")));
        Generate();
        Assert.DoesNotContain("Prose.", ReadPage("Photographs"));
        Assert.Contains("A Plate", ReadPage("Photographs", "index"));
    }

    /// <summary>
    /// Renaming an article to index.md turns it into prose, and prose has no sidecar. Carrying the
    /// old one across would create the file this convention promises never exists.
    /// </summary>
    [AvaloniaFact]
    public void RenamingAnArticleToAnIntroDoesNotCarryItsSidecar()
    {
        var folder = MakeFolder("Photographs");
        var article = Path.Combine(folder, "Introduction.md");
        File.WriteAllText(article, "Prose.\n");
        File.WriteAllText(article + ".yaml", "type: markdown\ncaption: Introduction\n");

        var intro = Path.Combine(folder, "index.md");
        File.Move(article, intro);
        ArtifactRename.Apply(article, intro);

        Assert.False(File.Exists(intro + ".yaml"));
        // The old sidecar is left where it is, for the leftovers sweep to offer.
        Assert.True(File.Exists(article + ".yaml"));
    }
}
