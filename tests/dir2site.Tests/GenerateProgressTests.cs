// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using dir2site.Models;
using dir2site.Services;
using dir2site.ViewModels;
using dir2site.Views;
using Xunit;

namespace dir2site.Tests;

/// <summary>
/// A generate is four long stages, and "Generating cats/index.html" tells you nothing about how
/// much is left. These tests pin the overall view: that each stage's total matches the work that
/// actually happens, that a stage stays off the line until its total is known, and that new and
/// updated mean what they say — read off the site's own output, so a new artifact is one the site
/// had no page for, an updated one is a page that now reads differently, and rebuilding a missing
/// thumbnail is neither.
/// </summary>
public class GenerateProgressTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "d2s-prog-" + Guid.NewGuid().ToString("N"));

    public GenerateProgressTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

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
    /// An artifact whose previews already exist on disk — the "already up to date" case, which the
    /// artifacts stage should count as done-but-not-new without doing any work.
    /// </summary>
    private void MakeArtifactWithPreviews(string folder, string fileName, string caption)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        File.WriteAllText(Path.Combine(folder, fileName), "not really a jpeg");
        File.WriteAllText(Path.Combine(folder, fileName + ".yaml"),
            $"""
             type: photo
             caption: {caption}
             preview: .dir2site/{stem}/{stem}-preview.jpg
             previewLarge: .dir2site/{stem}/{stem}-preview-large.jpg
             image: .dir2site/{stem}/{stem}.webp
             """);

        var previewDir = Path.Combine(folder, ".dir2site", stem);
        Directory.CreateDirectory(previewDir);
        foreach (var name in new[] { $"{stem}-preview.jpg", $"{stem}-preview-large.jpg", $"{stem}.webp" })
            File.WriteAllText(Path.Combine(previewDir, name), "preview bytes");
    }

    private GenerateProgressTracker Generate()
    {
        var tracker = new GenerateProgressTracker();
        var tree = DirectoryTraverser.BuildTree(_root, new List<string>(), new List<string>());
        DirectoryTraverser.GeneratePreviews(tree, Config(), tracker);
        SiteGenerator.Generate(_root, tree, Config(), tracker);
        return tracker;
    }

    private int PagesOnDisk() =>
        Directory.EnumerateFiles(Path.Combine(_root, "_site"), "index.html", SearchOption.AllDirectories)
                 .Count();

    [Fact]
    public void AStageStaysOffTheLine_UntilItsTotalIsKnown()
    {
        var tracker = new GenerateProgressTracker();
        Assert.Equal("", tracker.Counters);

        tracker.SetArtifactTotal(3);
        Assert.Equal("Artifacts 0/3", tracker.Counters);

        tracker.SetPageTotal(5);
        Assert.Equal("Artifacts 0/3 · Pages 0/5", tracker.Counters);

        // Previews sits between them wherever it is learned, so the line keeps pipeline order.
        tracker.SetPreviewTotal(2);
        Assert.Equal("Artifacts 0/3 · Previews 0/2 · Pages 0/5", tracker.Counters);
    }

    [Fact]
    public void WorkThatWasAlreadyDone_IsCountedWithoutANewParenthetical()
    {
        var tracker = new GenerateProgressTracker();
        tracker.SetArtifactTotal(3);
        tracker.SetPreviewTotal(3);
        tracker.SetPageTotal(5);
        tracker.SetFileTotal(2);

        tracker.AddArtifactsDone(3, Change.None);
        tracker.ArtifactChanged(Change.New);
        tracker.ArtifactChanged(Change.Updated);
        tracker.ArtifactChanged(Change.Updated);
        tracker.AddPreviewsDone(2, Change.None);
        tracker.PreviewDone(Change.New);
        for (var i = 0; i < 5; i++) tracker.PageDone(Change.New);
        tracker.FileDone(Change.New);
        tracker.FileDone(Change.None);

        Assert.Equal(
            "Artifacts 3/3 (1 new, 2 updated) · Previews 3/3 (1 new) · Pages 5/5 (5 new) · Files 2/2 (1 new)",
            tracker.Counters);
    }

    [Fact]
    public void TheSnapshotCarriesBothTheMessageAndTheCounters()
    {
        var tracker = new GenerateProgressTracker();
        tracker.SetPageTotal(2);
        tracker.Report("Generating index.html...");
        tracker.PageDone(Change.New);

        var snapshot = tracker.Snapshot();
        Assert.Equal("Generating index.html...", snapshot.Message);
        Assert.Equal("Pages 1/2 (1 new)", snapshot.Counters);
    }

    [AvaloniaFact]
    public void ThePageTotal_MatchesThePagesActuallyWritten()
    {
        var nested = MakeFolder("Photographs", "1890s");
        MakeArtifactWithPreviews(nested, "Portrait.jpg", "A Portrait");
        MakeArtifactWithPreviews(nested, "Landscape.jpg", "A Landscape");
        MakeFolder("Documents");

        var pages = Generate().Pages;

        // root + Photographs + 1890s + Documents + two artifact pages
        Assert.Equal(6, pages.Total);
        Assert.Equal(pages.Total, pages.Done);
        Assert.Equal(pages.Total, PagesOnDisk());
        Assert.Equal(pages.Total, pages.New);   // first run: every page is written
    }

    [AvaloniaFact]
    public void ASecondGenerateOverAnUnchangedTree_ReportsNothingNew()
    {
        var nested = MakeFolder("Photographs", "1890s");
        MakeArtifactWithPreviews(nested, "Portrait.jpg", "A Portrait");

        Generate();
        var tracker = Generate();

        Assert.Equal(tracker.Pages.Total, tracker.Pages.Done);
        Assert.Equal(0, tracker.Pages.New);
        Assert.Equal(tracker.Files.Total, tracker.Files.Done);
        Assert.Equal(0, tracker.Files.New);
        Assert.DoesNotContain("new", tracker.Counters);
    }

    /// <summary>
    /// New and updated are read off the site's own output: an artifact the site has never rendered
    /// a page for is new, and one whose page now reads differently is updated. On a first generate
    /// every artifact is therefore new; on the next, with nothing touched, none of them is.
    /// </summary>
    [AvaloniaFact]
    public void EveryArtifactIsNewTheFirstTimeAndNoneOfThemTheSecond()
    {
        var nested = MakeFolder("Photographs", "1890s");
        MakeArtifactWithPreviews(nested, "Portrait.jpg", "A Portrait");
        MakeArtifactWithPreviews(nested, "Landscape.jpg", "A Landscape");

        var first = Generate().Artifacts;
        Assert.Equal(2, first.Total);
        Assert.Equal(2, first.Done);
        Assert.Equal(2, first.New);
        Assert.Equal(0, first.Updated);

        var second = Generate().Artifacts;
        Assert.Equal(2, second.Done);
        Assert.Equal(0, second.New);
        Assert.Equal(0, second.Updated);
    }

    /// <summary>
    /// Rebuilding thumbnails is not a change to the artifact. An archive whose previews were never
    /// generated — or were deleted — would otherwise report every one of its photos as new, which
    /// is what sent this counter wrong in the first place. That work belongs to previews, which is
    /// why previews carry their own count.
    /// </summary>
    [AvaloniaFact]
    public void RebuildingMissingPreviews_CountsUnderPreviewsNotArtifacts()
    {
        var folder = MakeFolder("Photographs");
        MakeArtifactWithPreviews(folder, "Portrait.jpg", "A Portrait");
        Generate();

        // The thumbnails go missing; the artifact itself is untouched.
        Directory.Delete(Path.Combine(folder, ".dir2site", "Portrait"), recursive: true);

        var tracker = Generate();

        Assert.Equal(1, tracker.Artifacts.Done);
        Assert.Equal(0, tracker.Artifacts.New);
        Assert.Equal(0, tracker.Artifacts.Updated);

        // The work really did happen — it is just previews work.
        Assert.Equal(1, tracker.Previews.Total);
        Assert.Equal(1, tracker.Previews.Done);
        Assert.Equal(1, tracker.Previews.New);
    }

    /// <summary>
    /// Editing a caption changes what the artifact's page says, which is a different thing from a
    /// file arriving for the first time. They get their own counts.
    /// </summary>
    [AvaloniaFact]
    public void AnArtifactWhosePageNowReadsDifferently_CountsAsUpdatedNotNew()
    {
        var folder = MakeFolder("Photographs");
        MakeArtifactWithPreviews(folder, "Portrait.jpg", "A Portrait");
        MakeArtifactWithPreviews(folder, "Landscape.jpg", "A Landscape");
        Generate();

        File.WriteAllText(Path.Combine(folder, "Portrait.jpg.yaml"),
            """
            type: photo
            caption: A Portrait, retitled
            preview: .dir2site/Portrait/Portrait-preview.jpg
            previewLarge: .dir2site/Portrait/Portrait-preview-large.jpg
            image: .dir2site/Portrait/Portrait.webp
            """);

        var artifacts = Generate().Artifacts;

        Assert.Equal(2, artifacts.Total);
        Assert.Equal(0, artifacts.New);
        Assert.Equal(1, artifacts.Updated);

        // And it settles: nothing has been touched since that generate.
        Assert.Equal(0, Generate().Artifacts.Updated);
    }

    [AvaloniaFact]
    public void ArtifactsThatCannotHavePreviews_StayOutOfThePreviewsTotal()
    {
        var folder = MakeFolder("Photographs");
        MakeArtifactWithPreviews(folder, "Portrait.jpg", "A Portrait");
        // A deep zoom set: catalogued, but .dzi has no preview pipeline of its own.
        File.WriteAllText(Path.Combine(folder, "Tapestry.dzi"), "<Image/>");
        File.WriteAllText(Path.Combine(folder, "Tapestry.dzi.yaml"), "type: deepzoom\ncaption: A Tapestry\n");

        var tracker = Generate();

        Assert.Equal(2, tracker.Artifacts.Total);
        Assert.Equal(1, tracker.Previews.Total);
    }

    /// <summary>
    /// The case that started this: a file dropped into a folder of already-published artifacts is
    /// the one new thing, however much preview work the run does around it.
    /// </summary>
    [AvaloniaFact]
    public void AFileTheSiteHasNeverRendered_IsTheOnlyNewArtifact()
    {
        var folder = MakeFolder("Photographs");
        MakeArtifactWithPreviews(folder, "Portrait.jpg", "A Portrait");
        Generate();

        File.WriteAllText(Path.Combine(folder, "Newcomer.md"), "# Newcomer\n\nJust dropped in.\n");

        var artifacts = Generate().Artifacts;
        Assert.Equal(2, artifacts.Total);
        Assert.Equal(1, artifacts.New);
        Assert.Equal(0, artifacts.Updated);

        // It has a page from here on, so it is never new again.
        Assert.Equal(0, Generate().Artifacts.New);
    }

    [AvaloniaFact]
    public void TheFileTotal_CountsEveryContentAssetIncludingSkippedOnes()
    {
        var nested = MakeFolder("Photographs", "1890s");
        MakeArtifactWithPreviews(nested, "Portrait.jpg", "A Portrait");
        File.WriteAllText(Path.Combine(MakeFolder("_media"), "note.txt"), "static include");

        var first = Generate().Files;

        // three preview files + one _media file
        Assert.Equal(4, first.Total);
        Assert.Equal(4, first.Done);
        Assert.Equal(4, first.New);

        // Second run copies nothing, but the total still describes the whole set — the old counter
        // called every skipped file a copy.
        var second = Generate().Files;
        Assert.Equal(4, second.Total);
        Assert.Equal(4, second.Done);
        Assert.Equal(0, second.New);
    }

    [AvaloniaFact]
    public void TheCounterLine_AppearsInTheStatusBarOnlyWhenThereIsSomethingToCount()
    {
        var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var line = window.GetVisualDescendants().OfType<TextBlock>()
                         .First(t => t.Name == "GenerateCountersText");
        Assert.False(line.IsEffectivelyVisible);

        vm.GenerateCounters = "Artifacts 3/3 · Pages 5/5 (5 new)";
        Dispatcher.UIThread.RunJobs();

        Assert.True(line.IsEffectivelyVisible);
        Assert.Equal("Artifacts 3/3 · Pages 5/5 (5 new)", line.Text);
    }
}
