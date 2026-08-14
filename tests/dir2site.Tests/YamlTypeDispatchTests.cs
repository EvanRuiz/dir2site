// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using dir2site.Models;
using dir2site.Services;
using Xunit;

namespace dir2site.Tests;

/// <summary>
/// The yaml's <c>type:</c> token has to select the model that holds that type's fields.
///
/// It didn't used to. The parser tried each model in turn, but its deserializer ignores unmatched
/// properties, so the first attempt always succeeded and every artifact in the app came back as a
/// <see cref="Deepzoom"/> that merely carried the right value in <see cref="Artifact.Type"/>. That
/// went unnoticed for a long time because the generator switches on <c>Type</c> rather than on the
/// CLR type — but it silently dropped every subtype-specific field, and made the
/// <c>is MarkdownPage</c> test in DirectoryTreeItem permanently false. Video was the type that
/// forced the fix, since its id lives in exactly such a field.
/// </summary>
public class YamlTypeDispatchTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "d2s-yaml-" + Guid.NewGuid().ToString("N"));

    public YamlTypeDispatchTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // Writes a yaml next to a stand-in source file and parses it the way traversal would.
    private Artifact? Parse(string fileName, string yaml, List<string>? errors = null)
    {
        var path = Path.Combine(_dir, fileName);
        File.WriteAllText(path, "stand-in");
        File.WriteAllText(path + ".yaml", yaml);
        return YamlParser.TryParseYamlMeta(path, errors ?? new List<string>());
    }

    [Fact]
    public void AVideoYamlKeepsItsVideoId()
    {
        var artifact = Parse("Talk.url",
            "type: video\ncaption: A Talk\nprovider: youtube\nvideoId: AbCdEfGhIjK\nstart: 40\n");

        var video = Assert.IsType<Video>(artifact);
        Assert.Equal(ArtifactType.Video, video.Type);
        Assert.Equal("AbCdEfGhIjK", video.VideoId);
        Assert.Equal("youtube", video.Provider);
        Assert.Equal(40, video.Start);
    }

    [Fact]
    public void APhotoYamlKeepsItsPhotographer()
    {
        var photo = Assert.IsType<Photo>(
            Parse("Portrait.jpg", "type: photo\ncaption: A Portrait\nphotographer: A. Nother\n"));

        Assert.Equal("A. Nother", photo.Photographer);
    }

    [Fact]
    public void APdfYamlKeepsItsAuthorAndPublishFlag()
    {
        var pdf = Assert.IsType<Pdf>(
            Parse("Report.pdf", "type: pdf\ncaption: A Report\nauthor: A. Nother\npublishOriginal: true\n"));

        Assert.Equal("A. Nother", pdf.Author);
        Assert.True(pdf.PublishOriginal);
    }

    [Fact]
    public void AMarkdownYamlParsesAsTheMarkdownModel()
    {
        // Not cosmetic: DirectoryTreeItem starts the in-app article render off an `is MarkdownPage`
        // test, which could never be true while everything came back as a Deepzoom.
        Assert.IsType<MarkdownPage>(Parse("Article.md", "type: markdown\ncaption: An Article\n"));
    }

    [Fact]
    public void ADeepzoomYamlStillParsesAsDeepzoom()
    {
        var deepzoom = Assert.IsType<Deepzoom>(
            Parse("Map.dzi", "type: deepzoom\ncaption: A Map\noriginal: Map.tif\n"));

        Assert.Equal("Map.tif", deepzoom.Original);
    }

    [Fact]
    public void AYamlWithNoTypeStillParses()
    {
        // The fallback path. Nothing that parsed before the dispatch was added should stop parsing.
        var artifact = Parse("Portrait.jpg", "caption: A Portrait\ncredit: Someone\n");

        Assert.NotNull(artifact);
        Assert.Equal("A Portrait", artifact!.Caption);
    }

    [Fact]
    public void TheHyphenatedUrlTextKeyActuallyBinds()
    {
        // YamlDotNet applies the deserializer's camelCase convention to explicit aliases too, so
        // the "url-text" alias was being turned back into "urlText" and the hyphenated key this
        // field exists to read never matched. Nothing consumed the value, so it stayed hidden until
        // something finally needed to read it.
        var artifact = Parse(
            "Portrait.jpg",
            "type: photo\ncaption: A Portrait\nurl: https://example.org/portrait\nurl-text: See the original\n");

        Assert.Equal("See the original", artifact!.UrlText);
        Assert.Equal("https://example.org/portrait", artifact.Url);
    }

    [Fact]
    public void AnUnknownTypeIsReportedRatherThanThrown()
    {
        // No model claims it and no enum member matches, so every attempt fails. The parser has to
        // hand back null with the reasons attached, not let the exception escape into traversal.
        var errors = new List<string>();
        var artifact = Parse("Thing.jpg", "type: gizmo\ncaption: A Thing\n", errors);

        Assert.Null(artifact);
        Assert.NotEmpty(errors);
    }
}
