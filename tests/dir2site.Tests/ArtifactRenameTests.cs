// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.IO;
using dir2site.Services;
using Xunit;

namespace dir2site.Tests;

/// <summary>
/// What follows an artifact when its file is renamed.
/// </summary>
/// <remarks>
/// Everything about an artifact except its bytes is keyed on the filename — the sidecar is named
/// after it, the preview folder is named after its stem, and the files inside spell the stem out
/// again. So a rename either carries all of it or strands all of it.
/// </remarks>
public class ArtifactRenameTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "d2s-rename-" + Guid.NewGuid().ToString("N"));

    public ArtifactRenameTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private string At(params string[] parts) => Path.Combine([_root, .. parts]);

    /// <summary>A photo with the sidecar and thumbnails generation would have left behind.</summary>
    private string MakePhoto(string fileName, string caption)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var source = At(fileName);
        File.WriteAllText(source, "not really a jpeg");

        var previews = At(".dir2site", stem);
        Directory.CreateDirectory(previews);
        File.WriteAllText(Path.Combine(previews, $"preview-{stem}.webp"), "thumb");
        File.WriteAllText(Path.Combine(previews, $"preview-lg-{stem}.webp"), "thumb-lg");
        File.WriteAllText(Path.Combine(previews, $"{stem}_q90.webp"), "web copy");

        var (preview, previewLarge) = PreviewGenerator.CanonicalPreviewNames(stem);
        File.WriteAllText(source + ".yaml",
            $"""
             # a comment the user wrote
             type: photo
             caption: {caption}
             preview: {preview}
             previewLarge: {previewLarge}
             image: .dir2site/{stem}/{stem}_q90.webp
             """);

        return source;
    }

    private static string Yaml(string artifactPath) => File.ReadAllText(artifactPath + ".yaml");

    // ---- the sidecar and its assets ---------------------------------------

    [Fact]
    public void TheSidecarAndPreviewsFollowTheFile()
    {
        MakePhoto("Portrait.jpg", "Portrait");
        File.Move(At("Portrait.jpg"), At("Headshot.jpg"));

        ArtifactRename.Apply(At("Portrait.jpg"), At("Headshot.jpg"));

        Assert.True(File.Exists(At("Headshot.jpg.yaml")));
        Assert.False(File.Exists(At("Portrait.jpg.yaml")));

        Assert.True(Directory.Exists(At(".dir2site", "Headshot")));
        Assert.False(Directory.Exists(At(".dir2site", "Portrait")));

        // The stem is spelled out again inside, so moving the folder is only half the job.
        Assert.True(File.Exists(At(".dir2site", "Headshot", "preview-Headshot.webp")));
        Assert.True(File.Exists(At(".dir2site", "Headshot", "preview-lg-Headshot.webp")));
        Assert.True(File.Exists(At(".dir2site", "Headshot", "Headshot_q90.webp")));
    }

    [Fact]
    public void ThePreviewPathsInTheYamlArePointedAtTheNewNames()
    {
        MakePhoto("Portrait.jpg", "Portrait");
        File.Move(At("Portrait.jpg"), At("Headshot.jpg"));
        ArtifactRename.Apply(At("Portrait.jpg"), At("Headshot.jpg"));

        var yaml = Yaml(At("Headshot.jpg"));
        Assert.Contains(".dir2site/Headshot/preview-Headshot.webp", yaml, StringComparison.Ordinal);
        Assert.Contains(".dir2site/Headshot/preview-lg-Headshot.webp", yaml, StringComparison.Ordinal);
        Assert.Contains(".dir2site/Headshot/Headshot_q90.webp", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Portrait", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void TheUsersCommentsSurvive()
    {
        MakePhoto("Portrait.jpg", "Portrait");
        File.Move(At("Portrait.jpg"), At("Headshot.jpg"));
        ArtifactRename.Apply(At("Portrait.jpg"), At("Headshot.jpg"));

        Assert.Contains("# a comment the user wrote", Yaml(At("Headshot.jpg")), StringComparison.Ordinal);
    }

    [Fact]
    public void ALegacySidecarJoinsTheCurrentConvention()
    {
        // Portrait.yaml is still read as Portrait.jpg's sidecar. Renaming is a natural moment to
        // stop carrying the old spelling forward, rather than producing Headshot.yaml and keeping
        // the ambiguity alive for another round.
        File.WriteAllText(At("Portrait.jpg"), "not really a jpeg");
        File.WriteAllText(At("Portrait.yaml"), "type: photo\ncaption: Grandmother\n");

        File.Move(At("Portrait.jpg"), At("Headshot.jpg"));
        ArtifactRename.Apply(At("Portrait.jpg"), At("Headshot.jpg"));

        Assert.True(File.Exists(At("Headshot.jpg.yaml")));
        Assert.False(File.Exists(At("Portrait.yaml")));
        Assert.Contains("Grandmother", Yaml(At("Headshot.jpg")), StringComparison.Ordinal);
    }

    [Fact]
    public void AShortStem_DoesNotCorruptTheRestOfEveryName()
    {
        // The stem used to be substituted wherever it appeared, which is fine for "Portrait" and
        // ruinous for the numbered files a scanned collection is full of. "2" rewrote ".dir2site"
        // itself, leaving the yaml pointing at ".dir3site" — a path that exists nowhere.
        MakePhoto("2.jpg", "2");
        File.Move(At("2.jpg"), At("3.jpg"));

        ArtifactRename.Apply(At("2.jpg"), At("3.jpg"));

        Assert.True(File.Exists(At(".dir2site", "3", "preview-3.webp")));
        Assert.True(File.Exists(At(".dir2site", "3", "3_q90.webp")));

        var yaml = Yaml(At("3.jpg"));
        Assert.Contains(".dir2site/3/3_q90.webp", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("dir3site", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void ASingleLetterStem_DoesNotEatTheExtension()
    {
        // "e" once turned preview-e.webp into prfvifw-f.wfbp — every "e" in the name, extension
        // included. Any stem that is a substring of preview, webp, lg or dir2site did this.
        MakePhoto("e.jpg", "E");
        File.Move(At("e.jpg"), At("f.jpg"));

        ArtifactRename.Apply(At("e.jpg"), At("f.jpg"));

        Assert.True(File.Exists(At(".dir2site", "f", "preview-f.webp")));
        Assert.True(File.Exists(At(".dir2site", "f", "preview-lg-f.webp")));
        Assert.True(File.Exists(At(".dir2site", "f", "f_q90.webp")));
        Assert.Contains(".dir2site/f/f_q90.webp", Yaml(At("f.jpg")), StringComparison.Ordinal);
    }

    [Fact]
    public void APhotoActuallyCalledPreview_RenamesTheRightHalfOfItsAssets()
    {
        // preview-preview.webp is ambiguous to a naive rule, which would rename the prefix instead
        // of the stem and leave the file unreachable.
        Assert.Equal("preview-Headshot.webp",
            ArtifactRename.RenamedAsset("preview-preview.webp", "preview", "Headshot"));

        Assert.Equal("Headshot_q90.webp",
            ArtifactRename.RenamedAsset("preview_q90.webp", "preview", "Headshot"));
    }

    [Fact]
    public void SomethingWeDidNotWrite_IsLeftWhereItIs()
    {
        // A file the user dropped into the preview folder is not ours to rename.
        Assert.Null(ArtifactRename.RenamedAsset("notes.txt", "Portrait", "Headshot"));
    }

    // ---- the caption -------------------------------------------------------

    [Fact]
    public void ACaptionThatWasJustTheFilename_FollowsTheNewName()
    {
        MakePhoto("Portrait.jpg", "Portrait");
        File.Move(At("Portrait.jpg"), At("Headshot.jpg"));
        ArtifactRename.Apply(At("Portrait.jpg"), At("Headshot.jpg"));

        Assert.Contains("caption: Headshot", Yaml(At("Headshot.jpg")), StringComparison.Ordinal);
    }

    [Fact]
    public void ACaptionTheUserWrote_IsLeftAlone()
    {
        // The one that matters. A caption is the whole point of the field, and rewriting it because
        // someone tidied a filename is worse than leaving it saying the old name.
        MakePhoto("Portrait.jpg", "Grandmother, 1912");
        File.Move(At("Portrait.jpg"), At("Headshot.jpg"));
        ArtifactRename.Apply(At("Portrait.jpg"), At("Headshot.jpg"));

        Assert.Contains("caption: Grandmother, 1912", Yaml(At("Headshot.jpg")), StringComparison.Ordinal);
    }

    [Fact]
    public void TheComparisonIsAgainstThePrettifiedName_NotTheRawStem()
    {
        // "my_beautiful_photo" is scaffolded as "My Beautiful Photo", so matching on the stem itself
        // would decide the user had written it and never update a caption again.
        MakePhoto("my_beautiful_photo.jpg", "My Beautiful Photo");
        File.Move(At("my_beautiful_photo.jpg"), At("my_new_photo.jpg"));
        ArtifactRename.Apply(At("my_beautiful_photo.jpg"), At("my_new_photo.jpg"));

        Assert.Contains("caption: My New Photo", Yaml(At("my_new_photo.jpg")), StringComparison.Ordinal);
    }

    [Fact]
    public void AVideoCaption_IsComparedTheWayItWasWritten()
    {
        // A shortcut's caption has " - YouTube" trimmed before prettifying. Comparing without that
        // trim would never match, so a renamed video would keep the old name for good.
        var current = YamlParser.PrettifyFilename(
            YamlParser.StripVideoProviderSuffix(At("Never Gonna Give You Up - YouTube.url")));

        var rederived = ArtifactRename.RederivedCaption(
            current,
            At("Never Gonna Give You Up - YouTube.url"),
            At("Rickroll - YouTube.url"));

        Assert.Equal("Rickroll", rederived);
    }

    [Fact]
    public void AnEmptyCaption_IsLeftAlone() =>
        Assert.Null(ArtifactRename.RederivedCaption("", At("Portrait.jpg"), At("Headshot.jpg")));

    [Fact]
    public void ACaptionMatchingNeitherName_IsLeftAlone() =>
        Assert.Null(ArtifactRename.RederivedCaption(
            "Something else entirely", At("Portrait.jpg"), At("Headshot.jpg")));

    // ---- what the user chose is theirs -------------------------------------

    [Fact]
    public void AHandWrittenPreviewPath_IsNotRepointed()
    {
        // It names an image the user picked, which has nothing to do with this file's name and did
        // not move because the file was renamed.
        File.WriteAllText(At("Portrait.jpg"), "not really a jpeg");
        File.WriteAllText(At("Portrait.jpg.yaml"),
            """
            type: photo
            caption: Grandmother
            preview: _media/mine.jpg
            previewLarge: _media/mine.jpg
            """);

        File.Move(At("Portrait.jpg"), At("Headshot.jpg"));
        ArtifactRename.Apply(At("Portrait.jpg"), At("Headshot.jpg"));

        var yaml = Yaml(At("Headshot.jpg"));
        Assert.Contains("preview: _media/mine.jpg", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain(".dir2site/Headshot", yaml, StringComparison.Ordinal);
    }

    // ---- refusing to overwrite ---------------------------------------------

    [Fact]
    public void ASidecarAlreadyAtTheDestination_IsNotReplaced()
    {
        MakePhoto("Portrait.jpg", "Portrait");
        MakePhoto("Headshot.jpg", "Somebody else");

        ArtifactRename.Apply(At("Portrait.jpg"), At("Headshot.jpg"));

        // Both intact, and the one that was already there still says what it said.
        Assert.Contains("caption: Somebody else", Yaml(At("Headshot.jpg")), StringComparison.Ordinal);
        Assert.True(File.Exists(At("Portrait.jpg.yaml")));
        Assert.True(Directory.Exists(At(".dir2site", "Portrait")));
    }

    /// <summary>Writes a reader manifest exactly as the generator does, escaping and all.</summary>
    /// <remarks>
    /// Through the serializer rather than as literal text, because the escaping is the point: the
    /// default encoder turns anything non-ASCII into <c>\uXXXX</c>, so a manifest for "Café Menu"
    /// does not contain the string "Café Menu" anywhere on disk.
    /// </remarks>
    private static void WriteManifest(string path, string stem) =>
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(
            new
            {
                data = new[]
                {
                    new[] { new { width = 800, height = 600, uri = $"{stem}_pages/page-0001.webp", pageNum = "1" } },
                },
            },
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

    private static string ManifestUri(string path) =>
        System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!
            ["data"]!.AsArray()[0]!.AsArray()[0]!["uri"]!.GetValue<string>();

    [Theory]
    [InlineData("Report", "Annual Report")]
    // Accented and CJK names are ordinary, and legal on every platform we ship to. They are also
    // the case a text substitution cannot see: the manifest holds "Café Menu_pages/", so a
    // search for "Café Menu_pages/" matches nothing and the reader keeps pointing at a folder that
    // no longer exists.
    [InlineData("Café Menu", "Bistro Menu")]
    [InlineData("Rapport Année", "Compte Rendu")]
    [InlineData("年次報告", "Annual")]
    public void APdfsReaderManifestIsRepointedWhateverItIsCalled(string oldStem, string newStem)
    {
        var dir = Path.Combine(_root, "Documents");
        Directory.CreateDirectory(dir);

        var previews = Path.Combine(dir, ".dir2site", oldStem);
        Directory.CreateDirectory(Path.Combine(previews, $"{oldStem}_pages"));
        File.WriteAllText(Path.Combine(previews, $"{oldStem}_pages", "page-0001.webp"), "page");
        WriteManifest(Path.Combine(previews, $"{oldStem}.bookreader.json"), oldStem);

        var oldPath = Path.Combine(dir, $"{oldStem}.pdf");
        var newPath = Path.Combine(dir, $"{newStem}.pdf");
        File.WriteAllText(oldPath, "pdf");
        File.WriteAllText(oldPath + ".yaml", $"type: pdf\ncaption: {oldStem}\n");

        File.Move(oldPath, newPath);
        ArtifactRename.Apply(oldPath, newPath);

        var manifest = Path.Combine(dir, ".dir2site", newStem, $"{newStem}.bookreader.json");
        Assert.True(File.Exists(manifest), "the manifest did not follow the rename");
        Assert.Equal($"{newStem}_pages/page-0001.webp", ManifestUri(manifest));
    }

    [Fact]
    public void WithNoSidecarOrPreviews_NothingHappensAndNothingThrows()
    {
        File.WriteAllText(At("Portrait.jpg"), "not really a jpeg");
        File.Move(At("Portrait.jpg"), At("Headshot.jpg"));

        ArtifactRename.Apply(At("Portrait.jpg"), At("Headshot.jpg"));

        Assert.False(File.Exists(At("Headshot.jpg.yaml")));
    }
}
