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
/// What happens to <c>_site</c> when the source folder is reorganized.
/// </summary>
/// <remarks>
/// The claim these tests exist to hold is not "the site ends up right" — the sweep already managed
/// that, by asking. It is that a move stops being a question. So most of them assert on
/// <c>Orphans</c> being empty as much as on where the pages ended up: a run that produces the right
/// site and still opens a dialog has failed at the thing this was for.
/// </remarks>
public class SiteChangeApplierTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "dir2site-apply-" + Guid.NewGuid().ToString("N"));

    public SiteChangeApplierTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private string SitePath(params string[] parts) => Path.Combine([_root, "_site", .. parts]);

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

    /// <summary>
    /// A fake photo, its sidecar, and the preview files generation would have produced — so the
    /// copy stage has something to carry into the site, which is where the weight of a real project
    /// actually is.
    /// </summary>
    private static void MakeArtifact(string folder, string fileName, string caption)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        File.WriteAllText(Path.Combine(folder, fileName), "not really a jpeg");

        var previews = Path.Combine(folder, ".dir2site", stem);
        Directory.CreateDirectory(previews);
        File.WriteAllText(Path.Combine(previews, $"{stem}-preview.jpg"), "not really a preview");
        File.WriteAllText(Path.Combine(previews, $"{stem}-preview-large.jpg"), "not really a preview");

        File.WriteAllText(Path.Combine(folder, fileName + ".yaml"),
            $"""
             type: photo
             caption: {caption}
             preview: .dir2site/{stem}/{stem}-preview.jpg
             previewLarge: .dir2site/{stem}/{stem}-preview-large.jpg
             """);
    }

    private DirectoryTreeItem Scan() =>
        DirectoryTraverser.BuildTree(_root, new List<string>(), new List<string>());

    private (string Summary, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings,
        IReadOnlyList<string> Orphans) Generate() =>
        SiteGenerator.Generate(_root, Scan(), Config());

    /// <summary>
    /// A generate that first carries a witnessed batch through, as the app does, and reports what
    /// would actually reach the confirmation dialog afterwards.
    /// </summary>
    /// <remarks>
    /// Not the raw orphan list. A page stranded by the layout rules — the surviving photo moving up
    /// a level when its folder drops to a single item — is still reported by the sweep, because the
    /// sweep only knows what the run claimed. What matters is whether the user gets asked about it,
    /// and that is what the caller filters on.
    /// </remarks>
    private IReadOnlyList<string> WouldAsk(params SourceChange[] changes)
    {
        SiteChangeApplier.Apply(_root, changes);
        var result = Generate();
        var explained = SiteChangeApplier.ExplainedBy(_root, changes);
        return [.. result.Orphans.Where(o => !SiteChangeApplier.IsExplained(o, explained))];
    }

    /// <summary>A generate that first recovers what it can from the shapes, as the app does when nothing was watching.</summary>
    private (string Summary, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings,
        IReadOnlyList<string> Orphans) GenerateUnwitnessed()
    {
        SiteChangeApplier.ReconcileMoves(_root, Scan());
        return Generate();
    }

    // ---- a witnessed move --------------------------------------------------

    [AvaloniaFact]
    public void AMovedFolder_TakesItsPagesWithItAndAsksNothing()
    {
        var nested = MakeFolder("Photographs", "1890s");
        MakeArtifact(nested, "Portrait.jpg", "A Portrait");
        MakeArtifact(nested, "Landscape.jpg", "A Landscape");
        var documents = MakeFolder("Documents");
        // Two, so Documents stays a collection: a folder holding a single item publishes it as its
        // own index instead, and the assertion below would be about a page that never existed.
        MakeArtifact(documents, "Letter.jpg", "A Letter");
        MakeArtifact(documents, "Memo.jpg", "A Memo");

        Generate();
        Assert.True(File.Exists(SitePath("Photographs", "1890s", "Portrait", "index.html")));

        MakeFolder("Archive");
        Directory.Move(Path.Combine(_root, "Photographs", "1890s"), Path.Combine(_root, "Archive", "1890s"));

        var asked = WouldAsk(new SourceChange(
            SourceChangeKind.Moved,
            Path.Combine(_root, "Archive", "1890s"),
            Path.Combine(_root, "Photographs", "1890s")));

        // Where it went.
        Assert.True(File.Exists(SitePath("Archive", "1890s", "Portrait", "index.html")));
        Assert.False(Directory.Exists(SitePath("Photographs", "1890s")));

        // And what was not asked. This is the claim.
        Assert.Empty(asked);

        // Nothing else disturbed.
        Assert.True(File.Exists(SitePath("Documents", "Letter", "index.html")));
        Assert.True(File.Exists(SitePath("index.html")));
    }

    [AvaloniaFact]
    public void AMovedFolder_CarriesItsImagesAcrossUntouched()
    {
        // The SFTP sync decides what to send by size and mtime, so anything rewritten is re-sent.
        // Rebuilding at the new address would give every file a fresh timestamp and re-upload the
        // whole subtree; moving it means only what genuinely differs is sent.
        //
        // The pages themselves are expected to change — their breadcrumbs now name a different
        // parent, so they really are different pages. The images are not: they are the same bytes at
        // a new address, and they are where the weight of a photo project actually is.
        var nested = MakeFolder("Photographs", "1890s");
        MakeArtifact(nested, "Portrait.jpg", "A Portrait");
        MakeArtifact(nested, "Landscape.jpg", "A Landscape");

        Generate();
        var before = File.GetLastWriteTimeUtc(
            SitePath("Photographs", "1890s", "Portrait", "Portrait-preview.jpg"));

        MakeFolder("Archive");
        Directory.Move(Path.Combine(_root, "Photographs", "1890s"), Path.Combine(_root, "Archive", "1890s"));

        WouldAsk(new SourceChange(
            SourceChangeKind.Moved,
            Path.Combine(_root, "Archive", "1890s"),
            Path.Combine(_root, "Photographs", "1890s")));

        var moved = SitePath("Archive", "1890s", "Portrait", "Portrait-preview.jpg");
        Assert.True(File.Exists(moved));
        Assert.Equal(before, File.GetLastWriteTimeUtc(moved));
    }

    [AvaloniaFact]
    public void ARenamedArtifact_MovesItsPageAndAsksNothing()
    {
        var nested = MakeFolder("Photographs");
        MakeArtifact(nested, "Portrait.jpg", "A Portrait");
        MakeArtifact(nested, "Landscape.jpg", "A Landscape");

        Generate();
        Assert.True(File.Exists(SitePath("Photographs", "Portrait", "index.html")));

        File.Move(Path.Combine(nested, "Portrait.jpg"), Path.Combine(nested, "Headshot.jpg"));
        File.Move(Path.Combine(nested, "Portrait.jpg.yaml"), Path.Combine(nested, "Headshot.jpg.yaml"));

        var asked = WouldAsk(new SourceChange(
            SourceChangeKind.Moved,
            Path.Combine(nested, "Headshot.jpg"),
            Path.Combine(nested, "Portrait.jpg")));

        Assert.True(File.Exists(SitePath("Photographs", "Headshot", "index.html")));
        Assert.False(Directory.Exists(SitePath("Photographs", "Portrait")));
        Assert.Empty(asked);
    }

    // ---- a witnessed delete ------------------------------------------------

    [AvaloniaFact]
    public void AWitnessedDelete_NeedsNoConfirmation()
    {
        var nested = MakeFolder("Photographs");
        MakeArtifact(nested, "Portrait.jpg", "A Portrait");
        MakeArtifact(nested, "Landscape.jpg", "A Landscape");

        Generate();
        Assert.True(File.Exists(SitePath("Photographs", "Portrait", "index.html")));

        File.Delete(Path.Combine(nested, "Portrait.jpg"));
        File.Delete(Path.Combine(nested, "Portrait.jpg.yaml"));

        var asked = WouldAsk(new SourceChange(
            SourceChangeKind.Removed, Path.Combine(nested, "Portrait.jpg")));

        Assert.False(Directory.Exists(SitePath("Photographs", "Portrait")));

        // Nothing asked — including the knock-on. Photographs is down to one item, so Landscape now
        // publishes as the folder's own index and the page it used to have is stranded. Nobody
        // deleted that; the layout rules moved it, as a consequence of a deletion we watched happen.
        Assert.Empty(asked);
    }

    [AvaloniaFact]
    public void AnUnwitnessedDelete_IsStillOffered()
    {
        // The other half of the pair above, and the reason the dialog stays. Nothing saw this
        // happen, so "the site no longer wants these files" is all we know — which is not the same
        // as knowing the user deleted anything.
        var nested = MakeFolder("Photographs");
        MakeArtifact(nested, "Portrait.jpg", "A Portrait");
        MakeArtifact(nested, "Landscape.jpg", "A Landscape");

        Generate();

        File.Delete(Path.Combine(nested, "Portrait.jpg"));
        File.Delete(Path.Combine(nested, "Portrait.jpg.yaml"));

        var result = GenerateUnwitnessed();

        Assert.NotEmpty(result.Orphans);
        Assert.Contains(result.Orphans, o => o.Contains("Portrait", StringComparison.Ordinal));
        // Offered, not taken: the page is still there until someone says so.
        Assert.True(File.Exists(SitePath("Photographs", "Portrait", "index.html")));
    }

    // ---- an unwitnessed move -----------------------------------------------

    [AvaloniaFact]
    public void AnUnwitnessedMove_IsRecoveredFromTheShapesAndAsksNothing()
    {
        // Reorganizing in Finder with the app shut is ordinary. A subtree the site no longer wants,
        // beside a place it does want that isn't there, under one name, can only be the one thing.
        var nested = MakeFolder("Photographs", "1890s");
        MakeArtifact(nested, "Portrait.jpg", "A Portrait");
        MakeArtifact(nested, "Landscape.jpg", "A Landscape");

        Generate();

        MakeFolder("Archive");
        Directory.Move(Path.Combine(_root, "Photographs", "1890s"), Path.Combine(_root, "Archive", "1890s"));

        var result = GenerateUnwitnessed();

        Assert.True(File.Exists(SitePath("Archive", "1890s", "Portrait", "index.html")));
        Assert.False(Directory.Exists(SitePath("Photographs", "1890s")));
        Assert.Empty(result.Orphans);
    }

    [AvaloniaFact]
    public void AnAmbiguousUnwitnessedMove_IsLeftToTheDialog()
    {
        // Two folders called "1890s" leaving and two arriving: nothing here says which went where,
        // and a wrong pairing publishes pages at addresses the user never chose. Not guessing costs
        // a rebuild and a question; guessing wrong costs a wrong site.
        var a = MakeFolder("Photographs", "1890s");
        var b = MakeFolder("Documents", "1890s");
        MakeArtifact(a, "Portrait.jpg", "A Portrait");
        MakeArtifact(b, "Letter.jpg", "A Letter");

        Generate();

        MakeFolder("Archive");
        MakeFolder("Storage");
        Directory.Move(Path.Combine(_root, "Photographs", "1890s"), Path.Combine(_root, "Archive", "1890s"));
        Directory.Move(Path.Combine(_root, "Documents", "1890s"), Path.Combine(_root, "Storage", "1890s"));

        var result = GenerateUnwitnessed();

        Assert.NotEmpty(result.Orphans);
    }

    // ---- what must never be touched ----------------------------------------

    [AvaloniaFact]
    public void AHandPlacedDotFile_SurvivesAWitnessedDelete()
    {
        // The generator writes no dot-entries, so anything with one got there from a person or a
        // server. Applying a change must honour that as the sweep does, or the fast path becomes a
        // way around the one rule protecting a hand-written .htaccess.
        var nested = MakeFolder("Photographs");
        MakeArtifact(nested, "Portrait.jpg", "A Portrait");
        MakeArtifact(nested, "Landscape.jpg", "A Landscape");

        Generate();

        Directory.CreateDirectory(SitePath(".well-known"));
        File.WriteAllText(SitePath(".well-known", "challenge"), "token");
        File.WriteAllText(SitePath(".htaccess"), "# mine");

        File.Delete(Path.Combine(nested, "Portrait.jpg"));
        File.Delete(Path.Combine(nested, "Portrait.jpg.yaml"));

        var asked = WouldAsk(new SourceChange(
            SourceChangeKind.Removed, Path.Combine(nested, "Portrait.jpg")));

        Assert.True(File.Exists(SitePath(".htaccess")));
        Assert.True(File.Exists(SitePath(".well-known", "challenge")));
        Assert.DoesNotContain(asked, o => o.Contains(".htaccess", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public void AChangeNamingSomethingOutsideTheProject_MovesNothing()
    {
        // This class deletes directories recursively, so a path that has escaped the project — via a
        // symlink, or a batch assembled from somewhere unexpected — must not resolve to anywhere
        // outside _site.
        var nested = MakeFolder("Photographs");
        MakeArtifact(nested, "Portrait.jpg", "A Portrait");
        Generate();

        var outsider = Path.Combine(Path.GetTempPath(), "dir2site-outsider-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outsider);
        try
        {
            var result = SiteChangeApplier.Apply(_root, [
                new SourceChange(SourceChangeKind.Removed, outsider),
            ]);

            Assert.Equal(0, result.Removed);
            Assert.True(Directory.Exists(outsider));
        }
        finally
        {
            Directory.Delete(outsider, recursive: true);
        }
    }

    [AvaloniaFact]
    public void AMoveOntoSomethingThatIsAlreadyThere_IsRefused()
    {
        // Merging two trees on a guess is not a mistake anyone can undo from here.
        var photos = MakeFolder("Photographs");
        MakeArtifact(photos, "Portrait.jpg", "A Portrait");
        MakeArtifact(photos, "Landscape.jpg", "A Landscape");
        Generate();

        var result = SiteChangeApplier.Apply(_root, [
            new SourceChange(
                SourceChangeKind.Moved,
                Path.Combine(_root, "Photographs", "Landscape.jpg"),
                Path.Combine(_root, "Photographs", "Portrait.jpg")),
        ]);

        Assert.Equal(0, result.Moved);
        Assert.True(File.Exists(SitePath("Photographs", "Portrait", "index.html")));
        Assert.True(File.Exists(SitePath("Photographs", "Landscape", "index.html")));
    }

    [AvaloniaFact]
    public void WithNoSiteYet_ApplyingChangesIsHarmless()
    {
        var photos = MakeFolder("Photographs");
        MakeArtifact(photos, "Portrait.jpg", "A Portrait");

        var result = SiteChangeApplier.Apply(_root, [
            new SourceChange(SourceChangeKind.Removed, Path.Combine(photos, "Gone.jpg")),
        ]);

        Assert.False(result.DidAnything);
        Assert.Empty(result.Errors);
    }
}
