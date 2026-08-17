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
/// Which artifacts the previews stage decides it has work to do for.
/// </summary>
/// <remarks>
/// Asserted through the progress tracker rather than by decoding images: the question here is what
/// the collect pass <em>chose</em>, and choosing wrongly is the bug either way. A thumbnail that
/// should have been rebuilt and wasn't shows up as zero work; one rebuilt for no reason shows up as
/// work on an unchanged tree.
/// </remarks>
public class PreviewStalenessTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "d2s-stale-" + Guid.NewGuid().ToString("N"));

    public PreviewStalenessTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private static Dir2SiteModel Config() => new() { Title = "S", Footer = "f" };

    /// <summary>
    /// A photo with the thumbnails generation would have left behind, both stamped later than the
    /// source so the tree starts out settled.
    /// </summary>
    private string MakePhotoWithPreviews(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var source = Path.Combine(_root, fileName);
        File.WriteAllText(source, "not really a jpeg");

        var (preview, previewLarge) = PreviewGenerator.CanonicalPreviewNames(stem);
        Write(preview);
        Write(previewLarge);
        Write($".dir2site/{stem}/{stem}_q90.webp");

        File.WriteAllText(source + ".yaml",
            $"""
             type: photo
             caption: {stem}
             preview: {preview}
             previewLarge: {previewLarge}
             image: .dir2site/{stem}/{stem}_q90.webp
             """);

        Touch(source, DateTime.UtcNow.AddHours(-1));
        return source;

        void Write(string relative)
        {
            var full = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, "not really a preview");
        }
    }

    private static void Touch(string path, DateTime whenUtc) =>
        File.SetLastWriteTimeUtc(path, whenUtc);

    /// <summary>How many previews the collect pass decided needed generating.</summary>
    private int PreviewJobs()
    {
        var tree = DirectoryTraverser.BuildTree(_root, new List<string>(), new List<string>());
        var tracker = new GenerateProgressTracker();
        DirectoryTraverser.GeneratePreviews(tree, Config(), tracker);

        // Total minus the ones counted as already current up front is what the stage set out to do.
        var (_, total, @new, updated) = tracker.Previews;
        _ = total;
        return @new + updated;
    }

    // ---- the bug this commit is about --------------------------------------

    [AvaloniaFact]
    public void AReplacedPhoto_GetsANewThumbnail()
    {
        // Drop a corrected scan over the old one, keeping the filename. The file is new; the
        // thumbnail beside it is a picture of what used to be there. Checking only that a preview
        // exists kept showing the previous image for good.
        var source = MakePhotoWithPreviews("Portrait.jpg");
        Assert.Equal(0, PreviewJobs());

        File.WriteAllText(source, "a different jpeg entirely");
        Touch(source, DateTime.UtcNow.AddMinutes(5));

        Assert.Equal(1, PreviewJobs());
    }

    [AvaloniaFact]
    public void AnUntouchedTree_StillDoesNoPreviewWork()
    {
        // The other side of it. Reorganizing a project has to stay cheap, and a staleness rule that
        // fires when nothing changed would re-render every thumbnail on every generate.
        MakePhotoWithPreviews("Portrait.jpg");
        MakePhotoWithPreviews("Landscape.jpg");

        Assert.Equal(0, PreviewJobs());
        Assert.Equal(0, PreviewJobs());
    }

    // ---- what the user chose is theirs -------------------------------------

    [AvaloniaFact]
    public void AHandWrittenPreviewPath_IsLeftAloneEvenWhenTheSourceIsNewer()
    {
        // Pointing `preview:` at an image of your own breaks the link between source and thumbnail,
        // so the source's timestamp says nothing about whether that image is still right. Rebuilding
        // would burn the work and change nothing — NeedsPath rightly keeps the hand-written value —
        // and would then do it again on every single run.
        var source = Path.Combine(_root, "Portrait.jpg");
        File.WriteAllText(source, "not really a jpeg");

        // In an underscore folder, which the walk skips and the generator still copies into the
        // site — where a hand-picked image belongs. Left in the scanned tree it would be an artifact
        // in its own right, with a card and thumbnails of its own.
        Directory.CreateDirectory(Path.Combine(_root, "_media"));
        File.WriteAllText(Path.Combine(_root, "_media", "mine.jpg"), "my own thumbnail");

        File.WriteAllText(source + ".yaml",
            """
            type: photo
            caption: Portrait
            preview: _media/mine.jpg
            previewLarge: _media/mine.jpg
            image: _media/mine.jpg
            """);

        Touch(Path.Combine(_root, "_media", "mine.jpg"), DateTime.UtcNow.AddHours(-1));
        Touch(source, DateTime.UtcNow.AddMinutes(5));

        Assert.Equal(0, PreviewJobs());

        // And it is still the user's file afterwards.
        Assert.Equal("my own thumbnail", File.ReadAllText(Path.Combine(_root, "_media", "mine.jpg")));
    }

    [AvaloniaFact]
    public void APreviewThatWasDeleted_IsRebuiltWhateverItsPathSays()
    {
        // Existence is still the first question, and it applies to a hand-written path too: a value
        // naming a file that isn't there is broken rather than chosen.
        var source = Path.Combine(_root, "Portrait.jpg");
        File.WriteAllText(source, "not really a jpeg");
        File.WriteAllText(source + ".yaml",
            """
            type: photo
            caption: Portrait
            preview: _media/gone.jpg
            previewLarge: _media/gone.jpg
            """);

        Assert.Equal(1, PreviewJobs());
    }

    // ---- the helpers the rule is built on ----------------------------------

    [Fact]
    public void CanonicalPreviewNames_AreWhatTheGeneratorsWrite()
    {
        // Four places used to build this pair by hand. If any of them drifts from the helper,
        // IsCanonicalPreview starts calling our own thumbnails somebody else's and quietly stops
        // refreshing them.
        var (preview, previewLarge) = PreviewGenerator.CanonicalPreviewNames("Portrait");

        Assert.Equal(".dir2site/Portrait/preview-Portrait.webp", preview);
        Assert.Equal(".dir2site/Portrait/preview-lg-Portrait.webp", previewLarge);
    }

    [Fact]
    public void IsCanonicalPreview_TellsOursFromTheirs()
    {
        var source = Path.Combine(_root, "Portrait.jpg");

        Assert.True(PreviewGenerator.IsCanonicalPreview(source, ".dir2site/Portrait/preview-Portrait.webp"));
        Assert.True(PreviewGenerator.IsCanonicalPreview(source, ".dir2site/Portrait/preview-lg-Portrait.webp"));

        Assert.False(PreviewGenerator.IsCanonicalPreview(source, "_media/mine.jpg"));
        Assert.False(PreviewGenerator.IsCanonicalPreview(source, null));
        Assert.False(PreviewGenerator.IsCanonicalPreview(source, ""));

        // Another artifact's thumbnail is not ours either, even though it looks the part.
        Assert.False(PreviewGenerator.IsCanonicalPreview(source, ".dir2site/Landscape/preview-Landscape.webp"));
    }
}
