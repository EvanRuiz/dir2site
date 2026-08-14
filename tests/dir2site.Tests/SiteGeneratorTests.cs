// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Headless.XUnit;
using dir2site.Models;
using dir2site.Services;
using dir2site.SftpSync.Core;
using Xunit;

namespace dir2site.Tests;

/// <summary>
/// The menu, the site config and a collection's item set are properties of the whole tree, not of
/// the folder being rendered — so the generator can't decide from a folder's own mtime whether its
/// page is stale. It re-renders everything, every time, and only writes what actually changed.
/// These tests pin both halves of that: the freshness, and the untouched mtimes deploys rely on.
///
/// The same re-render is what lets it say which files the site no longer has any reason to hold —
/// what a deleted or renamed source leaves behind — so the sweep that offers those up is pinned
/// here too, from both directions: that it finds the leftovers, and that it finds nothing else.
/// </summary>
public class SiteGeneratorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "d2s-gen-" + Guid.NewGuid().ToString("N"));

    public SiteGeneratorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string SitePath(params string[] parts) =>
        Path.Combine([_root, "_site", .. parts]);

    private string ReadPage(params string[] parts) =>
        File.ReadAllText(SitePath([.. parts, "index.html"]));

    private static Dir2SiteModel Config(string title = "My Site") => new()
    {
        Title = title,
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
    /// Writes a fake artifact plus the sidecar YAML that makes it show up in the tree. The preview
    /// paths are the ones preview generation would have written, so cards get thumbnails without
    /// these tests having to decode an image.
    /// </summary>
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

    /// <summary>
    /// Writes a fake PDF plus the yaml that makes it show up in the tree, with `publishOriginal`
    /// set either way. No page images: publishing the original is about the source file, so it
    /// must not depend on previews having been generated.
    /// </summary>
    private void MakePdf(string folder, string fileName, string caption, bool publishOriginal)
    {
        File.WriteAllText(Path.Combine(folder, fileName), "not really a pdf");
        File.WriteAllText(Path.Combine(folder, fileName + ".yaml"),
            $"""
             type: pdf
             caption: {caption}
             publishOriginal: {(publishOriginal ? "true" : "false")}
             """);
    }

    private (string Summary, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings,
        IReadOnlyList<string> Orphans) Generate(Dir2SiteModel config)
    {
        var tree = DirectoryTraverser.BuildTree(_root, new List<string>(), new List<string>());
        return SiteGenerator.Generate(_root, tree, config);
    }

    /// <summary>
    /// Generate, then take away everything it reported as no longer belonging — what the app does
    /// when the user accepts the dialog. Returns the orphans, so a test can check both what was
    /// offered and what became of it.
    /// </summary>
    private IReadOnlyList<string> GenerateAndPrune(Dir2SiteModel config)
    {
        var result = Generate(config);
        SiteGenerator.RemoveOrphans(SitePath(), result.Orphans);
        return result.Orphans;
    }

    private Dictionary<string, DateTime> PageMtimes() =>
        Directory.EnumerateFiles(SitePath(), "index.html", SearchOption.AllDirectories)
                 .ToDictionary(p => p, File.GetLastWriteTimeUtc);

    private Dictionary<string, DateTime> AllFileMtimes() =>
        Directory.EnumerateFiles(SitePath(), "*", SearchOption.AllDirectories)
                 .ToDictionary(p => p, File.GetLastWriteTimeUtc);

    [AvaloniaFact]
    public void ANewTopLevelFolder_ReachesTheMenuOnEveryExistingPage()
    {
        var nested = MakeFolder("Photographs", "1890s");
        MakeArtifact(nested, "Portrait.jpg", "A Portrait");
        // A second artifact keeps 1890s a collection: a folder holding one is published as that
        // artifact, and these tests are about the menu and staleness, not about that.
        MakeArtifact(nested, "Landscape.jpg", "A Landscape");
        MakeFolder("Documents");

        Generate(Config());
        Assert.DoesNotContain("Maps/", ReadPage("Photographs", "1890s"));

        // Nothing under Photographs/1890s is touched, so the old mtime check kept its stale menu.
        MakeFolder("Maps");
        Generate(Config());

        Assert.Contains("Maps/", ReadPage("Photographs", "1890s"));
        Assert.Contains("Maps/", ReadPage("Photographs"));
        Assert.Contains("Maps/", ReadPage("Documents"));
        Assert.Contains("Maps/", ReadPage("Photographs", "1890s", "Portrait"));
    }

    [AvaloniaFact]
    public void AConfigChange_ReachesCollectionAndArtifactPagesAlike()
    {
        var nested = MakeFolder("Photographs", "1890s");
        MakeArtifact(nested, "Portrait.jpg", "A Portrait");
        // A second artifact keeps 1890s a collection: a folder holding one is published as that
        // artifact, and these tests are about the menu and staleness, not about that.
        MakeArtifact(nested, "Landscape.jpg", "A Landscape");

        Generate(Config("Old Title"));
        Generate(Config("New Title"));

        Assert.Contains("New Title", ReadPage("Photographs", "1890s"));
        Assert.Contains("New Title", ReadPage("Photographs", "1890s", "Portrait"));
        Assert.DoesNotContain("Old Title", ReadPage("Photographs", "1890s", "Portrait"));
    }

    /// <summary>
    /// The footer is the one site-owner string that reaches the page as raw HTML — a footer nearly
    /// always wants a link, and dir2site.yaml is written by the same hand as the artifacts' markdown.
    /// </summary>
    [AvaloniaFact]
    public void FooterMarkup_ReachesEveryPageAsHtml()
    {
        var nested = MakeFolder("Photographs", "1890s");
        MakeArtifact(nested, "Portrait.jpg", "A Portrait");
        // A second artifact keeps 1890s a collection, so there is both a collection page and an
        // artifact page to check — the footer is included by every template.
        MakeArtifact(nested, "Landscape.jpg", "A Landscape");

        var config = Config();
        config.Footer = "Generated by <a href=\"https://example.test/d2s\">dir2site</a>.";
        Generate(config);

        const string link = "<a href=\"https://example.test/d2s\">dir2site</a>";
        Assert.Contains(link, ReadPage("Photographs", "1890s"));
        Assert.Contains(link, ReadPage("Photographs", "1890s", "Portrait"));
        Assert.DoesNotContain("&lt;a href=", ReadPage("Photographs", "1890s"));

        // The title next to it stays escaped: only the footer is trusted with markup.
        config.Title = "Ampersand & <b>Co</b>";
        Generate(config);
        Assert.Contains("Ampersand &amp; &lt;b&gt;Co&lt;/b&gt;", ReadPage("Photographs", "1890s"));
    }

    [AvaloniaFact]
    public void AddingAnItem_UpdatesTheFolderAndTheAncestorAboveIt()
    {
        var nested = MakeFolder("Photographs", "1890s");
        MakeArtifact(nested, "Portrait.jpg", "A Portrait");
        // Two to begin with, so 1890s is a collection before and after: a folder holding a single
        // artifact publishes it as the folder's own index, and the arrival of a second would move
        // that page rather than simply adding a card.
        MakeArtifact(nested, "Sketch.jpg", "A Sketch");

        Generate(Config());
        Assert.DoesNotContain("A Landscape", ReadPage("Photographs", "1890s"));

        MakeArtifact(nested, "Landscape.jpg", "A Landscape");
        Generate(Config());

        Assert.Contains("A Landscape", ReadPage("Photographs", "1890s"));
        // The ancestor's folder card takes its thumbnail from the first artifact found beneath it,
        // and Landscape now sorts ahead of Portrait — so a page two levels up went stale too.
        Assert.Contains("Landscape-preview.jpg", ReadPage("Photographs"));
    }

    [AvaloniaFact]
    public void RegeneratingWithNoChanges_LeavesEveryPageMtimeAlone()
    {
        var nested = MakeFolder("Photographs", "1890s");
        MakeArtifact(nested, "Portrait.jpg", "A Portrait");
        // A second artifact keeps 1890s a collection: a folder holding one is published as that
        // artifact, and these tests are about the menu and staleness, not about that.
        MakeArtifact(nested, "Landscape.jpg", "A Landscape");
        MakeFolder("Documents");

        Generate(Config());
        var before = PageMtimes();

        Generate(Config());
        var after = PageMtimes();

        // Deploys diff on size + mtime, so an unchanged page must not be rewritten — otherwise
        // every generate would queue the whole site for re-upload.
        Assert.Equal(before.Keys.OrderBy(k => k), after.Keys.OrderBy(k => k));
        foreach (var (path, mtime) in before)
            Assert.Equal(mtime, after[path]);
    }

    [AvaloniaFact]
    public void OnlyThePagesThatChanged_GetRewritten()
    {
        MakeFolder("Photographs", "1890s");
        var documents = MakeFolder("Documents");
        MakeArtifact(documents, "Letter.jpg", "A Letter");
        // Keeps Documents a collection, so Letter has a page of its own whose mtime can be compared.
        MakeArtifact(documents, "Memo.jpg", "A Memo");

        Generate(Config());
        var before = PageMtimes();

        MakeArtifact(Path.Combine(_root, "Photographs", "1890s"), "Portrait.jpg", "A Portrait");
        Generate(Config());
        var after = PageMtimes();

        // The 1890s page gained a card and Photographs' folder card gained a thumbnail; Documents
        // is untouched by any of it.
        Assert.NotEqual(before[SitePath("Photographs", "1890s", "index.html")],
                        after[SitePath("Photographs", "1890s", "index.html")]);
        Assert.NotEqual(before[SitePath("Photographs", "index.html")],
                        after[SitePath("Photographs", "index.html")]);
        Assert.Equal(before[SitePath("Documents", "index.html")],
                     after[SitePath("Documents", "index.html")]);
        Assert.Equal(before[SitePath("Documents", "Letter", "index.html")],
                     after[SitePath("Documents", "Letter", "index.html")]);
    }

    [AvaloniaFact]
    public void PublishOriginal_PutsTheSourcePdfBesideItsPageAndOffersTheDownload()
    {
        var documents = MakeFolder("Documents");
        // A space in the name, because the href has to survive being a URL.
        MakePdf(documents, "Type Specimen.pdf", "A Type Specimen", publishOriginal: true);
        // A second artifact keeps Documents a collection, so each PDF has a page of its own.
        MakePdf(documents, "Letter.pdf", "A Letter", publishOriginal: false);

        Generate(Config());

        Assert.True(File.Exists(SitePath("Documents", "Type Specimen", "Type Specimen.pdf")));
        var page = ReadPage("Documents", "Type Specimen");
        Assert.Contains("href=\"Type%20Specimen.pdf\"", page);
        Assert.Contains("Download PDF", page);
    }

    [AvaloniaFact]
    public void WithoutPublishOriginal_TheSourcePdfStaysOutOfTheSite()
    {
        var documents = MakeFolder("Documents");
        MakePdf(documents, "Letter.pdf", "A Letter", publishOriginal: false);
        // A second artifact keeps Documents a collection, so each PDF has a page of its own.
        MakePdf(documents, "Memo.pdf", "A Memo", publishOriginal: false);

        Generate(Config());

        Assert.Empty(Directory.EnumerateFiles(SitePath(), "*.pdf", SearchOption.AllDirectories));
        Assert.DoesNotContain("Download PDF", ReadPage("Documents", "Letter"));
    }

    [AvaloniaFact]
    public void TurningPublishOriginalBackOff_TakesTheAlreadyPublishedPdfDownAgain()
    {
        // No special case does this any more: with the flag off, nothing asks for the PDF, so the
        // copy already in the site belongs to nothing and the sweep offers it up like any other
        // leftover. Leaving it would be the opposite of what turning the flag off is asking for.
        var documents = MakeFolder("Documents");
        MakePdf(documents, "Letter.pdf", "A Letter", publishOriginal: true);
        MakePdf(documents, "Memo.pdf", "A Memo", publishOriginal: false);

        Generate(Config());
        Assert.True(File.Exists(SitePath("Documents", "Letter", "Letter.pdf")));

        MakePdf(documents, "Letter.pdf", "A Letter", publishOriginal: false);
        GenerateAndPrune(Config());

        Assert.False(File.Exists(SitePath("Documents", "Letter", "Letter.pdf")));
        Assert.True(File.Exists(SitePath("Documents", "Letter", "index.html")));
    }

    /// <summary>
    /// The one that matters most. Generating writes only what changed, so on a second run almost
    /// every asset is left alone as already current — and if being left alone meant going
    /// unrecorded, the sweep would read the whole framework as leftovers and take the site apart.
    /// Each path below is a different one of those "nothing to do" early returns.
    /// </summary>
    [AvaloniaFact]
    public void AnAssetNothingRecopied_IsStillThereAfterTheSweep()
    {
        var nested = MakeFolder("Photographs", "1890s");
        MakeArtifact(nested, "Portrait.jpg", "A Portrait");
        MakeArtifact(nested, "Landscape.jpg", "A Landscape");
        // A preview to copy, so the mtime-skip path in CopyFileIfNewer is exercised too.
        var previews = MakeFolder("Photographs", "1890s", ".dir2site", "Portrait");
        File.WriteAllText(Path.Combine(previews, "Portrait-preview.jpg"), "not really a jpeg");

        Generate(Config());
        var orphans = GenerateAndPrune(Config());

        Assert.Empty(orphans);

        // Rendered templates, skipped because the text is identical.
        Assert.True(File.Exists(SitePath("css", "site.css")));
        Assert.True(File.Exists(SitePath("js", "site.js")));
        Assert.True(File.Exists(SitePath("js", "video.js")));

        // Embedded assets, skipped because they're no older than the app that carries them.
        Assert.True(File.Exists(SitePath("js", "bootstrap", "bootstrap.min.css")));
        Assert.True(File.Exists(SitePath("js", "bootstrap", "bootstrap.bundle.min.js")));
        Assert.True(File.Exists(SitePath("js", "bootstrap-icons", "bootstrap-icons.css")));
        Assert.NotEmpty(Directory.EnumerateFiles(SitePath("js", "bootstrap-icons", "fonts")));
        Assert.True(File.Exists(SitePath("js", "openseadragon", "openseadragon.min.js")));
        Assert.NotEmpty(Directory.EnumerateFiles(SitePath("js", "openseadragon", "images")));
        Assert.True(File.Exists(SitePath("js", "bookreader", "BookReader.js")));
        Assert.NotEmpty(Directory.EnumerateFiles(SitePath("js", "bookreader", "images")));

        // A copied file, skipped because the site's copy is no older than the source.
        Assert.True(File.Exists(SitePath("Photographs", "1890s", "Portrait", "Portrait-preview.jpg")));
    }

    [AvaloniaFact]
    public void ADeletedFolder_TakesItsPagesOutOfTheSiteWithIt()
    {
        var nested = MakeFolder("Photographs", "1890s");
        MakeArtifact(nested, "Portrait.jpg", "A Portrait");
        MakeArtifact(nested, "Landscape.jpg", "A Landscape");
        var documents = MakeFolder("Documents");
        MakeArtifact(documents, "Letter.jpg", "A Letter");
        MakeArtifact(documents, "Memo.jpg", "A Memo");

        Generate(Config());
        Assert.True(File.Exists(SitePath("Photographs", "1890s", "Portrait", "index.html")));

        Directory.Delete(nested, recursive: true);
        GenerateAndPrune(Config());

        // The folder and everything under it goes, down to the now-empty directories.
        Assert.False(Directory.Exists(SitePath("Photographs", "1890s")));

        // And nothing else does. A sweep that took the site with it would pass the check above.
        Assert.True(File.Exists(SitePath("Photographs", "index.html")));
        Assert.True(File.Exists(SitePath("Documents", "index.html")));
        Assert.True(File.Exists(SitePath("Documents", "Letter", "index.html")));
        Assert.True(File.Exists(SitePath("index.html")));
    }

    [AvaloniaFact]
    public void ARenamedArtifact_LeavesNothingAtItsOldAddress()
    {
        var nested = MakeFolder("Photographs", "1890s");
        MakeArtifact(nested, "Portrait.jpg", "A Portrait");
        // A sibling keeps 1890s a collection, so neither name is published as the folder's index.
        MakeArtifact(nested, "Landscape.jpg", "A Landscape");

        Generate(Config());
        Assert.True(File.Exists(SitePath("Photographs", "1890s", "Portrait", "index.html")));

        File.Move(Path.Combine(nested, "Portrait.jpg"), Path.Combine(nested, "Headshot.jpg"));
        File.Move(Path.Combine(nested, "Portrait.jpg.yaml"), Path.Combine(nested, "Headshot.jpg.yaml"));
        GenerateAndPrune(Config());

        // A rename is a delete and an add; without the sweep the old URL kept working and kept
        // being published, which is how a renamed page ends up live at two addresses.
        Assert.False(Directory.Exists(SitePath("Photographs", "1890s", "Portrait")));
        Assert.True(File.Exists(SitePath("Photographs", "1890s", "Headshot", "index.html")));
    }

    [AvaloniaFact]
    public void AStrayFileInTheSite_DoesNotSurviveTheNextGenerate()
    {
        var documents = MakeFolder("Documents");
        MakeArtifact(documents, "Letter.jpg", "A Letter");
        MakeArtifact(documents, "Memo.jpg", "A Memo");

        Generate(Config());

        File.WriteAllText(SitePath("leftover.html"), "from some earlier life");
        Directory.CreateDirectory(SitePath("old"));
        File.WriteAllText(SitePath("old", "index.html"), "likewise");

        var orphans = GenerateAndPrune(Config());

        Assert.Contains("leftover.html", orphans);
        Assert.Contains("old/index.html", orphans);
        Assert.False(File.Exists(SitePath("leftover.html")));
        Assert.False(File.Exists(SitePath("old", "index.html")));
        // The directory goes too, once the last thing in it has — otherwise the site fills up with
        // empty folders that the deploy still has to walk.
        Assert.False(Directory.Exists(SitePath("old")));
    }

    [AvaloniaFact]
    public void AHandPlacedDotFile_IsLeftAlone()
    {
        var documents = MakeFolder("Documents");
        MakeArtifact(documents, "Letter.jpg", "A Letter");
        MakeArtifact(documents, "Memo.jpg", "A Memo");

        Generate(Config());

        // dir2site writes no dot-entries into _site, so these can only have come from a person or
        // a server — and it doesn't delete what it didn't create. The deploy applies the same rule
        // at the far end, so the two halves agree about what is nobody's business to remove.
        File.WriteAllText(SitePath(".htaccess"), "Redirect 301 /old /new");
        Directory.CreateDirectory(SitePath(".well-known", "acme-challenge"));
        File.WriteAllText(SitePath(".well-known", "acme-challenge", "token"), "abc123");

        var orphans = GenerateAndPrune(Config());

        Assert.DoesNotContain(orphans, o => o.Contains(".htaccess"));
        Assert.DoesNotContain(orphans, o => o.Contains("acme-challenge"));
        Assert.True(File.Exists(SitePath(".htaccess")));
        Assert.True(File.Exists(SitePath(".well-known", "acme-challenge", "token")));
    }

    [AvaloniaFact]
    public void RegeneratingWithNoChanges_LeavesEveryFileMtimeAlone()
    {
        var nested = MakeFolder("Photographs", "1890s");
        MakeArtifact(nested, "Portrait.jpg", "A Portrait");
        MakeArtifact(nested, "Landscape.jpg", "A Landscape");
        MakeFolder("Documents");

        Generate(Config());
        var before = AllFileMtimes();

        var orphans = GenerateAndPrune(Config());
        var after = AllFileMtimes();

        // The page-level version of this above guards the same contract for pages. Widened to
        // every file, it is also what says the sweep is inert on a no-op run: nothing reported,
        // nothing removed, nothing rewritten — so a deploy has nothing to re-upload.
        Assert.Empty(orphans);
        Assert.Equal(before.Keys.OrderBy(k => k), after.Keys.OrderBy(k => k));
        foreach (var (path, mtime) in before)
            Assert.Equal(mtime, after[path]);
    }

    [AvaloniaFact]
    public void ALogoDroppedFromTheConfig_LeavesTheSite()
    {
        var documents = MakeFolder("Documents");
        MakeArtifact(documents, "Letter.jpg", "A Letter");
        MakeArtifact(documents, "Memo.jpg", "A Memo");
        File.WriteAllText(Path.Combine(_root, "logo.png"), "not really a png");

        var config = Config();
        config.Logo = "logo.png";
        Generate(config);
        Assert.True(File.Exists(SitePath("logo.png")));

        // The logo is copied by name from the config, so clearing the setting is the only thing
        // that can retire it — nothing about the source folder changed.
        config.Logo = "";
        GenerateAndPrune(config);

        Assert.False(File.Exists(SitePath("logo.png")));
    }

    [AvaloniaFact]
    public void AFileRemovedFromAnUnderscoreFolder_LeavesTheSite()
    {
        var documents = MakeFolder("Documents");
        MakeArtifact(documents, "Letter.jpg", "A Letter");
        MakeArtifact(documents, "Memo.jpg", "A Memo");
        var media = MakeFolder("_media");
        File.WriteAllText(Path.Combine(media, "a.png"), "not really a png");
        File.WriteAllText(Path.Combine(media, "b.png"), "not really a png either");

        Generate(Config());
        Assert.True(File.Exists(SitePath("_media", "b.png")));

        // Underscore folders are copied verbatim rather than scanned as artifacts, so they had no
        // pruning story at all before this.
        File.Delete(Path.Combine(media, "b.png"));
        GenerateAndPrune(Config());

        Assert.True(File.Exists(SitePath("_media", "a.png")));
        Assert.False(File.Exists(SitePath("_media", "b.png")));

        File.Delete(Path.Combine(media, "a.png"));
        GenerateAndPrune(Config());

        Assert.False(Directory.Exists(SitePath("_media")));
    }

    /// <summary>
    /// A folder the generator could not read is not a folder whose contents have gone — but it
    /// contributes nothing to the ledger either way, so without this the sweep reads the two as the
    /// same thing and offers a live site for deletion. A cloud-synced project with dehydrated
    /// files, a network share that blinks, or a scanner holding a handle is enough to trigger it,
    /// and none of those leaves a trace the user could act on.
    /// </summary>
    /// <remarks>
    /// Runs on Windows and Unix alike — see <see cref="UnreadableDirectory"/>. Windows is where
    /// this matters most: cloud-synced folders, network shares and virus scanners are the triggers.
    /// If the machine won't let the directory be made unreadable (an elevated session can bypass a
    /// deny ACE) the test says so rather than passing on a readable folder.
    /// </remarks>
    [AvaloniaFact]
    public void AnUnreadableFolder_TakesTheWholeRemovalOfferOffTheTable()
    {
        var articles = MakeFolder("Articles");
        MakeArtifact(articles, "Letter.jpg", "A Letter");
        MakeArtifact(articles, "Memo.jpg", "A Memo");
        var media = MakeFolder("Articles", "_media");
        File.WriteAllText(Path.Combine(media, "figure.webp"), "not really a webp");

        Assert.Empty(Generate(Config()).Orphans);
        Assert.True(File.Exists(SitePath("Articles", "_media", "figure.webp")));

        using (var denied = UnreadableDirectory.Make(articles))
        {
            // Loud rather than quiet: a pass on a directory that stayed readable would be no
            // coverage at all, on the platform where this failure is most likely.
            Assert.True(denied != null,
                "could not make the folder unreadable, so this test proved nothing");

            var result = Generate(Config());

            // Nothing offered, and the reason said out loud — the alternative was silently
            // proposing to delete every static include the articles still point at.
            Assert.Empty(result.Orphans);
            Assert.Contains(result.Warnings, w => w.Contains("could not be read"));
        }

        Assert.True(File.Exists(SitePath("Articles", "_media", "figure.webp")));
    }

    /// <summary>
    /// A file that won't copy is reported, and everything else still generates. Letting it escape
    /// took the whole generate down — and the caller cleared its busy flag after the await, so the
    /// window came back with every button disabled and nothing said, which is a hang as far as
    /// anyone using it is concerned. Locked and unreadable files are ordinary on Windows.
    /// </summary>
    [AvaloniaFact]
    public void AFileThatWillNotCopy_IsReportedRatherThanEndingTheGenerate()
    {
        var documents = MakeFolder("Documents");
        MakeArtifact(documents, "Letter.jpg", "A Letter");
        MakeArtifact(documents, "Memo.jpg", "A Memo");
        var media = MakeFolder("_media", "figures");
        File.WriteAllText(Path.Combine(media, "figure.webp"), "not really a webp");

        Generate(Config());

        // Block the destination by putting a file where its folder has to go. Portable, and the
        // same shape as anything else that makes a copy fail part-way through a run.
        Directory.Delete(SitePath("_media", "figures"), recursive: true);
        File.WriteAllText(SitePath("_media", "figures"), "in the way");

        var result = Generate(Config());

        Assert.Contains(result.Errors, e => e.Contains("figure.webp"));
        // The rest of the site still generated rather than stopping at the bad file.
        Assert.True(File.Exists(SitePath("Documents", "index.html")));
        Assert.True(File.Exists(SitePath("index.html")));
    }

    /// <summary>
    /// _media mirrors the project folder, and a mirror copies what is there rather than only what
    /// is newer. Anything that swaps a file in while keeping its original timestamp — restoring a
    /// backup, copying off another drive or a camera, `rsync -t`, checking out an older revision —
    /// otherwise left the old copy in the site indefinitely, published, with no error. The server
    /// then agreed with _site, so Verify and Repair saw nothing wrong either.
    /// </summary>
    [AvaloniaFact]
    public void AReplacedFileWithAnOlderTimestamp_StillReachesTheSite()
    {
        var documents = MakeFolder("Documents");
        MakeArtifact(documents, "Letter.jpg", "A Letter");
        MakeArtifact(documents, "Memo.jpg", "A Memo");
        var media = MakeFolder("_media");
        var figure = Path.Combine(media, "figure.webp");
        File.WriteAllText(figure, "ORIGINAL");

        Generate(Config());
        Assert.Equal("ORIGINAL", File.ReadAllText(SitePath("_media", "figure.webp")));

        File.WriteAllText(figure, "REPLACED");
        File.SetLastWriteTimeUtc(figure, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Generate(Config());

        Assert.Equal("REPLACED", File.ReadAllText(SitePath("_media", "figure.webp")));
    }

    [AvaloniaFact]
    public void RemoveOrphans_SaysWhyWhenItRefusesAPath()
    {
        var documents = MakeFolder("Documents");
        MakeArtifact(documents, "Letter.jpg", "A Letter");
        MakeArtifact(documents, "Memo.jpg", "A Memo");
        Generate(Config());

        File.WriteAllText(Path.Combine(_root, "PRECIOUS.txt"), "not part of the site");

        var result = SiteGenerator.RemoveOrphans(SitePath(),
            ["../PRECIOUS.txt", "Photographs/../../PRECIOUS.txt", ".htaccess"]);

        // A refusal that reports nothing looks exactly like "removed 0" and offers the same file
        // again next time, with nothing to explain why.
        Assert.Equal(0, result.Removed);
        Assert.Equal(3, result.Errors.Count);
        Assert.Contains(result.Errors, e => e.Contains("outside _site"));
        Assert.Contains(result.Errors, e => e.Contains("dot-files"));
        Assert.True(File.Exists(Path.Combine(_root, "PRECIOUS.txt")));
    }

    /// <summary>
    /// The sweep works inside _site and nowhere else. Everything it deletes has a twin in the
    /// project folder — _media/logo.png is copied to _site/_media/logo.png — so a sweep that
    /// wandered up out of _site would be deleting the user's originals, not generated copies.
    /// </summary>
    [AvaloniaFact]
    public void TheSweepNeverReachesOutOfTheSiteIntoTheProjectFolder()
    {
        var documents = MakeFolder("Documents");
        MakeArtifact(documents, "Letter.jpg", "A Letter");
        MakeArtifact(documents, "Memo.jpg", "A Memo");
        var media = MakeFolder("_media");
        File.WriteAllText(Path.Combine(media, "logo.png"), "not really a png");

        Generate(Config());

        // Stray copies in _site, named exactly like the sources they came from. Removing these must
        // not take the originals with them.
        File.WriteAllText(SitePath("_media", "orphan.png"), "left over");
        var orphans = GenerateAndPrune(Config());

        Assert.Contains("_media/orphan.png", orphans);
        Assert.False(File.Exists(SitePath("_media", "orphan.png")));

        // The project folder is untouched: sources, their yamls, and the source _media itself.
        Assert.True(File.Exists(Path.Combine(media, "logo.png")));
        Assert.True(Directory.Exists(media));
        Assert.True(File.Exists(Path.Combine(documents, "Letter.jpg")));
        Assert.True(File.Exists(Path.Combine(documents, "Letter.jpg.yaml")));
        // Every reported path stays within _site — nothing climbs out with a "..".
        Assert.DoesNotContain(orphans, o => o.Contains(".."));
    }

    /// <summary>
    /// The point of all of it. The deploy takes the local manifest by walking _site, so while a
    /// deleted page's file stayed there it still looked like part of the site: uploaded on every
    /// sync, and never able to appear as stale on the server, because "stale" means present
    /// remotely and absent locally. Taking it out of _site is what lets the server be told.
    /// </summary>
    [AvaloniaFact]
    public void OnceRemoved_ADeletedPageIsOfferedForDeletionOnTheServerToo()
    {
        var nested = MakeFolder("Photographs", "1890s");
        MakeArtifact(nested, "Portrait.jpg", "A Portrait");
        MakeArtifact(nested, "Landscape.jpg", "A Landscape");
        MakeFolder("Documents");

        Generate(Config());
        // Stands in for what the last deploy left on the server.
        var uploaded = SyncManifestBuilder.BuildLocal(SitePath());
        Assert.Contains("Photographs/1890s/Portrait/index.html", uploaded.Files.Keys);

        Directory.Delete(nested, recursive: true);
        GenerateAndPrune(Config());

        var diff = SyncManifestBuilder.Compare(SyncManifestBuilder.BuildLocal(SitePath()), uploaded);

        Assert.Contains("Photographs/1890s/Portrait/index.html", diff.StaleRemote);
        Assert.Contains("Photographs/1890s/index.html", diff.StaleRemote);
        // And the pages that are still real aren't swept up with them.
        Assert.DoesNotContain("Photographs/index.html", diff.StaleRemote);
        Assert.DoesNotContain("index.html", diff.StaleRemote);
    }

    /// <summary>
    /// The server is meant to end up one-to-one with _site, and _media is the case where that is
    /// easiest to get wrong: it's copied in verbatim rather than generated, and it's the one thing
    /// in the site an article links to by hand. A static include that is still in the project must
    /// never be proposed for deletion on the server — and one that isn't, must be.
    /// </summary>
    [AvaloniaFact]
    public void StaticMediaIsOfferedForRemoteDeletionOnlyOnceItIsGoneLocally()
    {
        var documents = MakeFolder("Documents");
        MakeArtifact(documents, "Letter.jpg", "A Letter");
        MakeArtifact(documents, "Memo.jpg", "A Memo");
        var media = MakeFolder("_media");
        File.WriteAllText(Path.Combine(media, "diagram.png"), "not really a png");
        File.WriteAllText(Path.Combine(media, "chart.png"), "not really a png either");

        Generate(Config());
        var uploaded = SyncManifestBuilder.BuildLocal(SitePath());
        Assert.Contains("_media/diagram.png", uploaded.Files.Keys);
        Assert.Contains("_media/chart.png", uploaded.Files.Keys);

        // Only one of them goes.
        File.Delete(Path.Combine(media, "chart.png"));
        GenerateAndPrune(Config());

        var diff = SyncManifestBuilder.Compare(SyncManifestBuilder.BuildLocal(SitePath()), uploaded);

        Assert.Contains("_media/chart.png", diff.StaleRemote);
        // The one still in the project stays published, and isn't queued for re-upload either —
        // nothing about it changed.
        Assert.DoesNotContain("_media/diagram.png", diff.StaleRemote);
        Assert.DoesNotContain("_media/diagram.png", diff.ToUpload);
    }
}
