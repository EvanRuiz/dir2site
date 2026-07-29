// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.IO;
using System.Threading;
using dir2site.Services;
using Xunit;

namespace dir2site.Tests;

/// <summary>
/// Artifact sidecars are the files users hand-annotate, and this runs over every one of them on
/// every generate — so what it leaves behind matters more than what it writes.
/// </summary>
public class UpdatePreviewFieldsTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "d2s-yaml-" + Guid.NewGuid().ToString("N"));

    public UpdatePreviewFieldsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string WriteSidecar(string content)
    {
        var path = Path.Combine(_dir, "photo.jpg.yaml");
        File.WriteAllText(path, content);
        return path;
    }

    private const string HandAnnotated =
        """
        type: photo
        title: Sunset over the bay

        # Shot on the old 50mm — remember to credit Dana
        caption: |
          The light here only works
          for about ten minutes.
        tags: [travel, evening]
        preview: old-preview.jpg
        previewLarge: old-preview-large.jpg
        """;

    [Fact]
    public void PreservesCommentsBlockScalarsAndUnknownKeys()
    {
        var path = WriteSidecar(HandAnnotated);

        YamlParser.UpdatePreviewFields(path, "new-preview.jpg", "new-large.jpg");

        var result = File.ReadAllText(path);
        Assert.Contains("# Shot on the old 50mm — remember to credit Dana", result);
        Assert.Contains("tags: [travel, evening]", result);   // flow sequence style kept
        Assert.Contains("caption: |", result);                // block scalar still a block
        Assert.Contains("The light here only works", result);
        Assert.Contains("title: Sunset over the bay", result);
        Assert.Contains("preview: new-preview.jpg", result);
        Assert.Contains("previewLarge: new-large.jpg", result);

        // Key order is the user's, not the serializer's.
        Assert.True(result.IndexOf("type:", StringComparison.Ordinal)
                  < result.IndexOf("title:", StringComparison.Ordinal));
    }

    [Fact]
    public void AddsMissingKeysWithoutDisturbingTheRest()
    {
        var path = WriteSidecar(
            """
            type: photo
            # keep me
            title: No preview keys yet
            """);

        YamlParser.UpdatePreviewFields(path, "p.jpg", "pl.jpg", "img.jpg");

        var result = File.ReadAllText(path);
        Assert.Contains("# keep me", result);
        Assert.Contains("preview: p.jpg", result);
        Assert.Contains("previewLarge: pl.jpg", result);
        Assert.Contains("image: img.jpg", result);
    }

    [Fact]
    public void UnchangedValues_LeaveTheFileByteIdenticalAndUntouched()
    {
        var path = WriteSidecar(HandAnnotated);
        var before = File.ReadAllText(path);
        var stamp = File.GetLastWriteTimeUtc(path);
        Thread.Sleep(20);

        YamlParser.UpdatePreviewFields(path, "old-preview.jpg", "old-preview-large.jpg");

        Assert.Equal(before, File.ReadAllText(path));
        // SiteGenerator compares sidecar mtime against the generated page to decide what to
        // rebuild, so a no-op write would cause pointless regeneration.
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public void UnparseableFile_StillGetsItsPreviewKeys()
    {
        var path = WriteSidecar("type: photo\n  : broken indentation [\n");

        YamlParser.UpdatePreviewFields(path, "p.jpg", "pl.jpg");

        var result = File.ReadAllText(path);
        Assert.Contains("p.jpg", result);
        Assert.Contains("pl.jpg", result);
    }

    [Fact]
    public void MissingFile_IsIgnored()
    {
        var path = Path.Combine(_dir, "does-not-exist.yaml");

        YamlParser.UpdatePreviewFields(path, "p.jpg", "pl.jpg");   // must not throw

        Assert.False(File.Exists(path));
    }
}
