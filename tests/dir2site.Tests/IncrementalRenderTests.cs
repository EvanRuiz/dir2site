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
/// Rendering only what a change can reach, and the sweep still being right afterwards.
/// </summary>
/// <remarks>
/// The saving is easy to demonstrate and the danger is easy to miss, so most of these are about the
/// danger. A page that isn't rendered is a page the run never claimed — and the orphan sweep offers
/// everything unclaimed for deletion. Get this wrong and generating a site quietly proposes deleting
/// most of it.
/// </remarks>
public class IncrementalRenderTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "d2s-incr-" + Guid.NewGuid().ToString("N"));

    public IncrementalRenderTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private string At(params string[] parts) => Path.Combine([_root, .. parts]);
    private string SitePath(params string[] parts) => Path.Combine([_root, "_site", .. parts]);

    private static Dir2SiteModel Config() => new()
    {
        Title = "My Site",
        Footer = "© 2026",
        SiteUrl = "https://example.test",
    };

    private string MakeFolder(params string[] parts)
    {
        var path = At(parts);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>A PDF with an author line, so it reserves two rows on its own page and no others.</summary>
    private static void MakePdfWithCredit(string folder, string fileName, string caption)
    {
        File.WriteAllText(Path.Combine(folder, fileName), "not really a pdf");
        File.WriteAllText(Path.Combine(folder, fileName + ".yaml"),
            $"type: pdf\ncaption: {caption}\ncredit: Someone\n");
    }

    private static void MakePdf(string folder, string fileName, string caption)
    {
        File.WriteAllText(Path.Combine(folder, fileName), "not really a pdf");
        File.WriteAllText(Path.Combine(folder, fileName + ".yaml"), $"type: pdf\ncaption: {caption}\n");
    }

    private static void MakeArtifact(string folder, string fileName, string caption)
    {
        File.WriteAllText(Path.Combine(folder, fileName), "not really a jpeg");
        File.WriteAllText(Path.Combine(folder, fileName + ".yaml"),
            $"type: photo\ncaption: {caption}\n");
    }

    private (string Summary, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings,
        IReadOnlyList<string> Orphans) Generate(RenderScope? scope = null) =>
        SiteGenerator.Generate(
            _root,
            DirectoryTraverser.BuildTree(_root, new List<string>(), new List<string>()),
            Config(),
            null,
            scope);

    /// <summary>
    /// A scope built the way the app builds one — with the freshly scanned tree, without which it
    /// cannot tell an edit from a membership change and every change takes the whole folder.
    /// </summary>
    private RenderScope Scope(params SourceChange[] changes) =>
        RenderScope.For(_root, changes,
            DirectoryTraverser.BuildTree(_root, new List<string>(), new List<string>()));

    private Dictionary<string, DateTime> PageMtimes() =>
        Directory.EnumerateFiles(SitePath(), "index.html", SearchOption.AllDirectories)
            .ToDictionary(p => p, File.GetLastWriteTimeUtc);

    /// <summary>A project with two folders, so a change in one can be seen not to touch the other.</summary>
    private void MakeProject()
    {
        var photos = MakeFolder("Photographs");
        MakeArtifact(photos, "Portrait.jpg", "A Portrait");
        MakeArtifact(photos, "Landscape.jpg", "A Landscape");

        var documents = MakeFolder("Documents");
        MakeArtifact(documents, "Letter.jpg", "A Letter");
        MakeArtifact(documents, "Memo.jpg", "A Memo");
    }

    // ---- the sweep must stay right ------------------------------------------

    [AvaloniaFact]
    public void AnIncrementalRun_ReportsNothingAsOrphaned()
    {
        // The one that matters. Pages this run had no reason to render are still pages the site
        // wants, and the ledger has to say so — it is registered before the render is skipped, not
        // as a consequence of it. Were that the wrong way round, a run touching one article would
        // offer every other page in the site for deletion.
        MakeProject();
        Generate();

        File.WriteAllText(At("Photographs", "Portrait.jpg"), "a different jpeg");

        var result = Generate(Scope(new SourceChange(SourceChangeKind.Updated, At("Photographs", "Portrait.jpg"))));

        Assert.Empty(result.Orphans);
    }

    [AvaloniaFact]
    public void AnIncrementalRun_LeavesEveryPageStandingOnDisk()
    {
        MakeProject();
        Generate();
        var before = PageMtimes().Keys.OrderBy(p => p, StringComparer.Ordinal).ToList();

        Generate(Scope(new SourceChange(SourceChangeKind.Updated, At("Photographs", "Portrait.jpg"))));

        var after = PageMtimes().Keys.OrderBy(p => p, StringComparer.Ordinal).ToList();
        Assert.Equal(before, after);
    }

    // ---- what a change reaches ----------------------------------------------

    [AvaloniaFact]
    public void EditingOneArticle_LeavesTheOtherFoldersPagesAlone()
    {
        MakeProject();
        Generate();

        // A caption change, so the page really would differ if it were rendered — this is about
        // whether it was looked at, not about whether the write was skipped.
        File.WriteAllText(At("Photographs", "Portrait.jpg.yaml"), "type: photo\ncaption: Changed\n");

        var before = PageMtimes();
        Generate(Scope(new SourceChange(SourceChangeKind.Updated, At("Photographs", "Portrait.jpg"))));
        var after = PageMtimes();

        Assert.NotEqual(before[SitePath("Photographs", "Portrait", "index.html")],
                        after[SitePath("Photographs", "Portrait", "index.html")]);

        Assert.Equal(before[SitePath("Documents", "Letter", "index.html")],
                     after[SitePath("Documents", "Letter", "index.html")]);
        Assert.Equal(before[SitePath("Documents", "index.html")],
                     after[SitePath("Documents", "index.html")]);
    }

    [AvaloniaFact]
    public void AnOrdinaryEdit_LeavesItsSiblingsAlone()
    {
        // What the folder's other pages depend on is its *membership* — prev/next links point at
        // stems, and the single-item collapse fires on a count. An edit to an artifact already there
        // moves none of that, so its siblings would render byte for byte what they rendered before.
        //
        // This is the case that matters at scale: a photo archive is often one big folder, so the
        // folder is the site, and taking it on every caption edit meant the narrowing saved nothing
        // at all.
        MakeProject();
        Generate();

        var scope = Scope(new SourceChange(SourceChangeKind.Updated, At("Photographs", "Portrait.jpg")));

        Assert.True(scope.ShouldRender(SitePath(), SitePath("Photographs", "Portrait", "index.html")));
        Assert.True(scope.ShouldRender(SitePath(), SitePath("Photographs", "index.html")));
        Assert.True(scope.ShouldRender(SitePath(), SitePath("index.html")));

        Assert.False(scope.ShouldRender(SitePath(), SitePath("Photographs", "Landscape", "index.html")));
    }

    [AvaloniaFact]
    public void AnEditToAPhotoWithNoPageYet_TakesTheFolder()
    {
        // Something that has only just arrived is a membership change however the watcher labelled
        // it — the links either side of it move, and the folder may stop being a single item.
        MakeProject();
        Generate();

        MakeArtifact(At("Photographs"), "Newcomer.jpg", "Just arrived");

        var scope = Scope(new SourceChange(SourceChangeKind.Updated, At("Photographs", "Newcomer.jpg")));

        Assert.True(scope.ShouldRender(SitePath(), SitePath("Photographs", "Landscape", "index.html")));
    }

    [AvaloniaFact]
    public void WithNothingBuiltYet_AnEditTakesTheFolder()
    {
        // The previous caption band is read off a sibling that is already on disk. Before a first
        // build there is nothing to read, and not knowing has to mean taking the folder.
        MakeProject();

        var scope = Scope(new SourceChange(SourceChangeKind.Updated, At("Photographs", "Portrait.jpg")));

        Assert.True(scope.ShouldRender(SitePath(), SitePath("Photographs", "Landscape", "index.html")));
    }

    [AvaloniaFact]
    public void AChangeReachesEveryIndexAboveIt()
    {
        // Each one lists what is underneath and carries the trail down to it — and a folder's own
        // card image can be drawn from an artifact several levels below.
        var nested = MakeFolder("Photographs", "1890s");
        MakeArtifact(nested, "Portrait.jpg", "A Portrait");
        MakeArtifact(nested, "Landscape.jpg", "A Landscape");
        Generate();

        var scope = Scope(new SourceChange(SourceChangeKind.Updated, At("Photographs", "1890s", "Portrait.jpg")));

        Assert.True(scope.ShouldRender(SitePath(), SitePath("index.html")));
        Assert.True(scope.ShouldRender(SitePath(), SitePath("Photographs", "index.html")));
        Assert.True(scope.ShouldRender(SitePath(), SitePath("Photographs", "1890s", "index.html")));
    }

    [AvaloniaFact]
    public void AddingAPhoto_FixesTheNeighboursLinksToo()
    {
        // Prev/Next chains a folder's photos together, so a page is no longer only about its own
        // artifact — adding one changes the page either side of it. The folder is the unit precisely
        // so a change reaches them; narrowing this to the changed page alone would leave the
        // neighbours pointing past the new photo, and nothing would ever put that right.
        var photos = MakeFolder("Photographs");
        MakeArtifact(photos, "A.jpg", "First");
        MakeArtifact(photos, "C.jpg", "Third");
        Generate();

        // Sorts between the two, so both neighbours' links have to change.
        MakeArtifact(photos, "B.jpg", "Second");

        var scope = Scope(new SourceChange(SourceChangeKind.Updated, At("Photographs", "B.jpg")));

        // Otherwise this proves nothing: a scope that fell back to everything would pass whatever
        // the narrowing rule did.
        Assert.False(scope.IsEverything);

        Generate(scope);

        var first = File.ReadAllText(SitePath("Photographs", "A", "index.html"));
        var third = File.ReadAllText(SitePath("Photographs", "C", "index.html"));

        Assert.Contains("../B/", first, StringComparison.Ordinal);
        Assert.Contains("../B/", third, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void AddingACreditToOnePhoto_ResizesItsSiblingsCaptionBand()
    {
        // The reason the folder is the unit, rather than a cautious guess at one. Every page in a
        // chain reserves the same rows of caption so the picture holds still as you arrow through,
        // and that number is computed across the whole chain — so giving one photo a credit line
        // changes the layout of all of them. Re-render only the edited page and its neighbours keep
        // the old band, which is the jumping the reservation exists to stop.
        var photos = MakeFolder("Photographs");
        MakeArtifact(photos, "A.jpg", "First");
        MakeArtifact(photos, "B.jpg", "Second");
        Generate();

        var before = File.ReadAllText(SitePath("Photographs", "A", "index.html"));

        File.WriteAllText(Path.Combine(photos, "B.jpg.yaml"),
            "type: photo\ncaption: Second\ncredit: Family album\n");

        var scope = Scope(new SourceChange(SourceChangeKind.Updated, At("Photographs", "B.jpg")));
        Assert.False(scope.IsEverything);

        Generate(scope);

        // A said nothing itself and still had to be rewritten, because what B now says changed how
        // much room A reserves.
        Assert.NotEqual(before, File.ReadAllText(SitePath("Photographs", "A", "index.html")));
    }

    [AvaloniaTheory]
    [InlineData("AAA.pdf", "x1.jpg", "x2.jpg")]
    [InlineData("Scan.pdf", "x1.jpg", "x2.jpg")]
    [InlineData("01.pdf", "Nine.jpg", "Ten.jpg")]
    [InlineData("zz.pdf", "Alpha.jpg", "Beta.jpg")]
    public void APdfBesideThePhotos_DoesNotAnswerForTheChain(string pdf, string first, string second)
    {
        // A page off the chain carries a band computed from itself alone — deliberately, so one
        // PDF's author line doesn't cost every photo a row. Reading the folder's band from whatever
        // the directory listing returned first therefore answered with the PDF's number, and the
        // folder was narrowed against the wrong value: the edited photo reserved two rows while its
        // sibling still reserved one, and the picture resized as you arrowed between them.
        //
        // Several namings, because the old bug only bit when the listing happened to hand back the
        // PDF first — and directory order is the filesystem's to choose. One shape would have caught
        // it here and missed it on someone else's disk.
        var photos = MakeFolder("Photographs");
        MakePdfWithCredit(photos, pdf, "A scan");
        MakeArtifact(photos, first, "First");
        MakeArtifact(photos, second, "Second");
        Generate();

        // A credit on one photo takes the chain's band from one row to two, so every photo must
        // follow. The PDF's own band never had anything to say about that.
        var stem = Path.GetFileNameWithoutExtension(second);
        File.WriteAllText(Path.Combine(photos, second + ".yaml"),
            $"type: photo\ncaption: Second\ncredit: Family album\n");

        var scope = Scope(new SourceChange(
            SourceChangeKind.Updated, Path.Combine(photos, second + ".yaml")));

        var untouched = Path.GetFileNameWithoutExtension(first);
        Assert.True(scope.ShouldRender(SitePath(), SitePath("Photographs", untouched, "index.html")),
            $"{untouched} kept the old caption band while {stem} moved to a new one");
    }

    [AvaloniaFact]
    public void EditingASidecar_NarrowsJustAsAnArtifactEditDoes()
    {
        // How a caption is edited: there is no editor in the app, so it means writing the sidecar,
        // and the watcher reports the path that was written. Taking the stem of "Portrait.jpg.yaml"
        // gives "Portrait.jpg", which is not a page — so every caption edit failed the "has a page
        // already" test and took its whole folder, which is the one case this was measured against.
        MakeProject();
        Generate();

        File.WriteAllText(At("Photographs", "Portrait.jpg.yaml"), "type: photo\ncaption: Changed\n");

        var scope = Scope(new SourceChange(
            SourceChangeKind.Updated, At("Photographs", "Portrait.jpg.yaml")));

        Assert.True(scope.ShouldRender(SitePath(), SitePath("Photographs", "Portrait", "index.html")));
        Assert.True(scope.ShouldRender(SitePath(), SitePath("Photographs", "index.html")));

        Assert.False(scope.ShouldRender(SitePath(), SitePath("Photographs", "Landscape", "index.html")));
    }

    [AvaloniaFact]
    public void AnArtifactJoiningTheChain_TakesTheFolder()
    {
        // `type:` is a sidecar key, and which side of the prev/next flag an artifact falls on is
        // decided by its type — so editing it moves the chain without anything being added, removed
        // or renamed. Every other refusal passes: the path exists, its page exists, and the watcher
        // calls it an ordinary update.
        //
        // Left narrowed, B's page claims A and C as neighbours while neither of them is rewritten,
        // so arrowing forward from A jumps straight past B and back from C does the same. B is on
        // the chain and unreachable along it, and nothing puts it right.
        var photos = MakeFolder("Photographs");
        MakeArtifact(photos, "A.jpg", "First");
        MakePdf(photos, "B.pdf", "A document");
        MakeArtifact(photos, "C.jpg", "Third");
        Generate();

        File.WriteAllText(Path.Combine(photos, "B.pdf.yaml"), "type: photo\ncaption: A document\n");

        var scope = Scope(new SourceChange(
            SourceChangeKind.Updated, At("Photographs", "B.pdf.yaml")));

        Assert.True(scope.ShouldRender(SitePath(), SitePath("Photographs", "A", "index.html")),
            "A still arrows straight past B");
        Assert.True(scope.ShouldRender(SitePath(), SitePath("Photographs", "C", "index.html")));
    }

    [AvaloniaFact]
    public void AnArtifactLeavingTheChain_TakesTheFolder()
    {
        // The worse half: a neighbour left pointing at a page that no longer carries arrows at all,
        // which is the dead end the flag exists to prevent.
        var photos = MakeFolder("Photographs");
        MakeArtifact(photos, "A.jpg", "First");
        MakeArtifact(photos, "B.jpg", "Second");
        MakeArtifact(photos, "C.jpg", "Third");
        Generate();

        File.WriteAllText(Path.Combine(photos, "B.jpg.yaml"), "type: pdf\ncaption: Second\n");

        var scope = Scope(new SourceChange(
            SourceChangeKind.Updated, At("Photographs", "B.jpg.yaml")));

        Assert.True(scope.ShouldRender(SitePath(), SitePath("Photographs", "A", "index.html")),
            "A still points at a page that has stopped carrying arrows");
    }

    // ---- when narrowing is refused -------------------------------------------

    [AvaloniaFact]
    public void AConfigChange_RendersEverything()
    {
        Assert.True(Scope(new SourceChange(SourceChangeKind.Updated, At("dir2site.yaml"))).IsEverything);
    }

    [AvaloniaFact]
    public void AFolderMove_RendersEverything()
    {
        // The menu is on every page, so rearranging folders changes every page.
        MakeFolder("Archive");
        Assert.True(Scope(new SourceChange(SourceChangeKind.Moved, At("Archive"), At("Photographs"))).IsEverything);
    }

    [AvaloniaFact]
    public void WithNothingKnown_EverythingIsRendered()
    {
        // No change set means the app was closed, or events were lost. Narrowing on no information
        // is exactly the guess this design exists to avoid.
        Assert.True(RenderScope.For(_root, []).IsEverything);
        Assert.True(RenderScope.All.IsEverything);
    }

    [AvaloniaFact]
    public void RenderingEverything_IsStillWhatAFullRunDoes()
    {
        // The old behaviour has to survive intact, since it is what every unwitnessed run gets.
        MakeProject();
        Generate();

        File.WriteAllText(At("Documents", "Letter.jpg.yaml"), "type: photo\ncaption: Changed\n");

        var before = PageMtimes();
        Generate(RenderScope.All);
        var after = PageMtimes();

        Assert.NotEqual(before[SitePath("Documents", "Letter", "index.html")],
                        after[SitePath("Documents", "Letter", "index.html")]);
    }

    [AvaloniaFact]
    public void AFullRunOverAnUnchangedTree_StillRewritesNothing()
    {
        MakeProject();
        Generate();

        var before = PageMtimes();
        Generate(RenderScope.All);

        Assert.Equal(before, PageMtimes());
    }
}
