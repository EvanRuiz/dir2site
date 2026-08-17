// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.IO;
using System.Linq;
using dir2site.Services;
using Xunit;

namespace dir2site.Tests;

/// <summary>
/// Noticing sidecars and preview folders whose artifact is no longer beside them.
/// </summary>
/// <remarks>
/// Both are named after the file they belong to, so a rename or a deletion carried out while
/// dir2site wasn't running leaves them behind with nothing pointing at them — and, crucially,
/// leaves them looking identical either way. What is left over says that something happened, never
/// what. Deciding is the dialog's job.
/// </remarks>
public class SourceLeftoversTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "d2s-left-" + Guid.NewGuid().ToString("N"));

    public SourceLeftoversTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private string At(params string[] parts) => Path.Combine([_root, .. parts]);

    private void MakePhoto(string fileName, string caption)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        File.WriteAllText(At(fileName), "not really a jpeg");
        Directory.CreateDirectory(At(".dir2site", stem));
        File.WriteAllText(At(".dir2site", stem, $"preview-{stem}.webp"), "thumb");
        File.WriteAllText(At(fileName + ".yaml"), $"type: photo\ncaption: {caption}\n");
    }

    // ---- what cannot be told apart is not guessed at ------------------------

    [Fact]
    public void ARenameMadeWhileNothingWasWatching_IsNotGuessedAt()
    {
        // One leftover sidecar beside one file without one is a rename — and equally a photo
        // deleted and a different photo added, which is an ordinary way to work. Pairing them moved
        // the deleted photo's caption, credit and date onto an unrelated picture. Only the watcher
        // can tell the two apart, and it wasn't running.
        MakePhoto("Portrait.jpg", "Aunt Mary, 1912");
        File.Move(At("Portrait.jpg"), At("Headshot.jpg"));

        var analysis = SourceLeftovers.InDirectory(_root);

        Assert.Contains(analysis.Sidecars, s => s.EndsWith("Portrait.jpg.yaml", StringComparison.Ordinal));
        Assert.False(File.Exists(At("Headshot.jpg.yaml")));
    }

    [Fact]
    public void ADeleteAndAnAddInOneFolder_KeepsTheirMetadataApart()
    {
        // The case that made pairing untenable: an unrelated new photo must not inherit the deleted
        // one's caption.
        MakePhoto("Portrait.jpg", "Aunt Mary, 1912");
        File.Delete(At("Portrait.jpg"));
        File.WriteAllText(At("Sunset.jpg"), "an unrelated photo");

        var analysis = SourceLeftovers.InDirectory(_root);

        Assert.False(File.Exists(At("Sunset.jpg.yaml")));
        Assert.Contains(analysis.Sidecars, s => s.EndsWith("Portrait.jpg.yaml", StringComparison.Ordinal));
    }

    // ---- what is reported ---------------------------------------------------

    [Fact]
    public void ADeletedPhotoLeavesItsSidecarAndPreviewsToBeAskedAbout()
    {
        MakePhoto("Portrait.jpg", "Grandmother");
        File.Delete(At("Portrait.jpg"));

        var analysis = SourceLeftovers.InDirectory(_root);

        Assert.Contains(analysis.Sidecars, s => s.EndsWith("Portrait.jpg.yaml", StringComparison.Ordinal));
        Assert.Contains(analysis.PreviewDirs, d => Path.GetFileName(d) == "Portrait");
    }

    [Fact]
    public void ALegacySidecarIsNeverReportedAsLeftOver()
    {
        // "Portrait.yaml" beside a missing "Portrait.jpg" is indistinguishable from a hand-written
        // file that happens to share the name. Offering it for deletion on that evidence is not a
        // mistake anyone can undo.
        File.WriteAllText(At("Portrait.yaml"), "type: photo\ncaption: Grandmother\n");

        Assert.Empty(SourceLeftovers.InDirectory(_root).Sidecars);
    }

    [Fact]
    public void ASettledFolderHasNothingLeftOver()
    {
        MakePhoto("Portrait.jpg", "Grandmother");
        MakePhoto("Landscape.jpg", "The valley");

        var analysis = SourceLeftovers.InDirectory(_root);

        Assert.Empty(analysis.Sidecars);
        Assert.Empty(analysis.PreviewDirs);
    }

    [Fact]
    public void ANewlyAddedPhotoIsNotALeftover()
    {
        // It has no sidecar yet because nothing has scanned it, which is not the same as having
        // lost one.
        File.WriteAllText(At("New.jpg"), "not really a jpeg");

        var analysis = SourceLeftovers.InDirectory(_root);

        Assert.Empty(analysis.Sidecars);
        Assert.Empty(analysis.PreviewDirs);
    }

    [Fact]
    public void FindAllReportsLeftoversFromEveryFolder()
    {
        MakePhoto("Portrait.jpg", "Grandmother");
        Directory.CreateDirectory(At("Photographs"));
        File.WriteAllText(At("Photographs", "Letter.jpg"), "jpeg");
        File.WriteAllText(At("Photographs", "Letter.jpg.yaml"), "type: photo\ncaption: A letter\n");

        File.Delete(At("Portrait.jpg"));
        File.Delete(At("Photographs", "Letter.jpg"));

        var found = SourceLeftovers.FindAll(_root);

        Assert.Contains(found, f => f.EndsWith("Portrait.jpg.yaml", StringComparison.Ordinal));
        Assert.Contains(found, f => f.EndsWith("Letter.jpg.yaml", StringComparison.Ordinal));
        Assert.Contains(found, f => Path.GetFileName(f) == "Portrait");
    }

    [Fact]
    public void ASettledProjectHasNothingToTidy()
    {
        MakePhoto("Portrait.jpg", "Grandmother");
        MakePhoto("Landscape.jpg", "The valley");

        Assert.Empty(SourceLeftovers.FindAll(_root));
    }

    // ---- taking them away, once we know -------------------------------------

    [Fact]
    public void AWitnessedDelete_TakesTheSidecarAndPreviewsWithIt()
    {
        MakePhoto("Portrait.jpg", "Grandmother");
        MakePhoto("Landscape.jpg", "The valley");
        File.Delete(At("Portrait.jpg"));

        SourceLeftovers.RemoveFor(At("Portrait.jpg"));

        Assert.False(File.Exists(At("Portrait.jpg.yaml")));
        Assert.False(Directory.Exists(At(".dir2site", "Portrait")));

        // And nothing else went with it.
        Assert.True(File.Exists(At("Landscape.jpg.yaml")));
        Assert.True(Directory.Exists(At(".dir2site", "Landscape")));
    }

    [Fact]
    public void ALegacySidecarIsNeverDeletedEvenForAWitnessedDelete()
    {
        // Same reasoning as never reporting it: being sure the artifact went says nothing about
        // what this file is.
        File.WriteAllText(At("Portrait.jpg"), "jpeg");
        File.WriteAllText(At("Portrait.yaml"), "type: photo\ncaption: Grandmother\n");
        File.Delete(At("Portrait.jpg"));

        SourceLeftovers.RemoveFor(At("Portrait.jpg"));

        Assert.True(File.Exists(At("Portrait.yaml")));
    }
}
