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
/// A video is the one artifact type that has no page of its own — it plays inline on the collection
/// index. That makes it the exception to two rules the generator otherwise applies to everything:
/// every artifact gets a page, and every card is a link to that page. These tests pin the exception
/// in both directions, because getting either half wrong produces something that still renders and
/// still looks fine: an orphan page nothing links to, or a card whose caption is a dead link.
///
/// Video ids here are synthetic. They only ever have to satisfy the 11-character format check, and
/// nothing in the repo should point at a real video.
/// </summary>
public class VideoArtifactTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "d2s-video-" + Guid.NewGuid().ToString("N"));

    public VideoArtifactTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string SitePath(params string[] parts) => Path.Combine([_root, "_site", .. parts]);

    private string ReadPage(params string[] parts) =>
        File.ReadAllText(SitePath([.. parts, "index.html"]));

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

    private static string Shortcut(string url) => $"[InternetShortcut]\r\nURL={url}\r\n";

    /// <summary>
    /// Writes a .url plus the yaml the app would have created for it. The preview paths are the
    /// ones the poster download would have written, so cards get thumbnails without any test having
    /// to reach the network.
    /// </summary>
    private string MakeVideo(
        string folder, string fileName, string url, string caption = "A Talk", string extra = "")
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var path = Path.Combine(folder, fileName);
        File.WriteAllText(path, Shortcut(url));
        File.WriteAllText(path + ".yaml",
            $"""
             type: video
             caption: {caption}
             preview: .dir2site/{stem}/preview-{stem}.webp
             previewLarge: .dir2site/{stem}/preview-lg-{stem}.webp
             {extra}
             """);
        return path;
    }

    private void MakePhoto(string folder, string fileName, string caption)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        File.WriteAllText(Path.Combine(folder, fileName), "not really a jpeg");
        File.WriteAllText(Path.Combine(folder, fileName + ".yaml"),
            $"""
             type: photo
             caption: {caption}
             preview: .dir2site/{stem}/preview-{stem}.webp
             previewLarge: .dir2site/{stem}/preview-lg-{stem}.webp
             """);
    }

    private (string Summary, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings) Generate()
    {
        var tree = DirectoryTraverser.BuildTree(_root, new List<string>(), new List<string>());
        return SiteGenerator.Generate(_root, tree, Config());
    }

    [AvaloniaFact]
    public void TheVideoIsEmbeddedInTheParentIndex()
    {
        var folder = MakeFolder("Videos");
        MakeVideo(folder, "Talk.url", "https://www.youtube.com/watch?v=AbCdEfGhIjK&t=40s");

        Generate();
        var page = ReadPage("Videos");

        Assert.Contains("""data-video-id="AbCdEfGhIjK" """.TrimEnd(), page);
        Assert.Contains("""data-start="40" """.TrimEnd(), page);
        Assert.Contains("class=\"video-poster\"", page);
        Assert.Contains("preview-Talk.webp", page);
        // The card needs something in it between load and the player being ready, or the wait reads
        // as a page that has finished loading and simply does nothing.
        Assert.Contains("class=\"video-spinner\"", page);
    }

    [AvaloniaFact]
    public void TheVideoGetsNoPageOfItsOwn()
    {
        var folder = MakeFolder("Videos");
        MakeVideo(folder, "Talk.url", "https://youtu.be/AbCdEfGhIjK");
        MakePhoto(folder, "Portrait.jpg", "A Portrait");

        Generate();

        // The photo still gets one — this is a video-only exception, not a blanket change.
        Assert.True(File.Exists(SitePath("Videos", "Portrait", "index.html")));
        Assert.False(File.Exists(SitePath("Videos", "Talk", "index.html")));
    }

    [AvaloniaFact]
    public void TheVideoCardIsNotALinkToAPageThatDoesNotExist()
    {
        var folder = MakeFolder("Videos");
        MakeVideo(folder, "Talk.url", "https://youtu.be/AbCdEfGhIjK", caption: "A Talk");

        Generate();
        var page = ReadPage("Videos");

        // The card is the player. A stretched-link over it would both dead-end and swallow the
        // click that is supposed to start playback.
        Assert.DoesNotContain("href=\"Talk/\"", page);
        Assert.DoesNotContain("stretched-link", page);
        Assert.Contains("A Talk", page);
    }

    [AvaloniaFact]
    public void OnlyPagesWithAVideoPullInTheYouTubeGlue()
    {
        var videos = MakeFolder("Videos");
        MakeVideo(videos, "Talk.url", "https://youtu.be/AbCdEfGhIjK");
        var photos = MakeFolder("Photographs");
        MakePhoto(photos, "Portrait.jpg", "A Portrait");

        Generate();

        // Written unconditionally like every other asset...
        Assert.True(File.Exists(SitePath("js", "video.js")));
        // ...but only referenced where it is needed, so a photo gallery loads nothing extra.
        Assert.Contains("js/video.js", ReadPage("Videos"));
        Assert.DoesNotContain("js/video.js", ReadPage("Photographs"));
    }

    [AvaloniaFact]
    public void AVideoWithNoStartOffsetOmitsTheAttributeEntirely()
    {
        var folder = MakeFolder("Videos");
        MakeVideo(folder, "Talk.url", "https://youtu.be/AbCdEfGhIjK");

        Generate();

        // An empty data-start would parse to NaN in the player and read as a deliberate zero here.
        Assert.DoesNotContain("data-start", ReadPage("Videos"));
    }

    [AvaloniaFact]
    public void TheShortcutWinsOverAStaleIdInTheYaml()
    {
        var folder = MakeFolder("Videos");
        // Re-pointing the .url at a different video has to move the card with it, otherwise the
        // yaml quietly keeps serving the video the user just replaced.
        MakeVideo(folder, "Talk.url", "https://youtu.be/NewNewNewNe",
            extra: "videoId: OldOldOldOl\nprovider: youtube");

        Generate();
        var page = ReadPage("Videos");

        Assert.Contains("NewNewNewNe", page);
        Assert.DoesNotContain("OldOldOldOl", page);
    }

    [AvaloniaFact]
    public void AHandSetStartOffsetSurvivesTheShortcutNotHavingOne()
    {
        var folder = MakeFolder("Videos");
        MakeVideo(folder, "Talk.url", "https://youtu.be/AbCdEfGhIjK", extra: "start: 95");

        Generate();

        Assert.Contains("""data-start="95" """.TrimEnd(), ReadPage("Videos"));
    }

    [AvaloniaFact]
    public void AYamlIsCreatedFromTheShortcutWithTheIdAndOffsetFilledIn()
    {
        var folder = MakeFolder("Videos");
        var path = Path.Combine(folder, "Talk.url");
        File.WriteAllText(path, Shortcut("https://www.youtube.com/watch?v=AbCdEfGhIjK&t=1m30s"));

        Generate();
        var yaml = File.ReadAllText(path + ".yaml");

        Assert.Contains("type: video", yaml);
        Assert.Contains("videoId: AbCdEfGhIjK", yaml);
        Assert.Contains("start: 90", yaml);
        Assert.Contains("provider: youtube", yaml);
    }

    [AvaloniaFact]
    public void AnOrdinaryBookmarkIsNotAnArtifact()
    {
        var folder = MakeFolder("Videos");
        MakeVideo(folder, "Talk.url", "https://youtu.be/AbCdEfGhIjK");

        var bookmark = Path.Combine(folder, "Homepage.url");
        File.WriteAllText(bookmark, Shortcut("https://example.com/"));

        Generate();

        // No yaml written, and nothing on the page — a web bookmark filed next to some videos
        // is not an error, it is simply not catalogued.
        Assert.False(File.Exists(bookmark + ".yaml"));
        Assert.DoesNotContain("Homepage", ReadPage("Videos"));
    }

    [AvaloniaFact]
    public void TheCardLinksBackToTheUrlTheUserActuallySaved()
    {
        var folder = MakeFolder("Videos");
        var url = "https://www.youtube.com/watch?v=AbCdEfGhIjK&list=PL6yBRGthjpACu";
        MakeVideo(folder, "Talk.url", url, extra: "url-text: View on YouTube");

        Generate();
        var page = ReadPage("Videos");

        // Whatever the shortcut said, playlist parameters and all — not a URL rebuilt from the id.
        Assert.Contains(url.Replace("&", "&amp;"), page);
        Assert.Contains("View on YouTube", page);
    }

    [AvaloniaFact]
    public void WithoutUrlTextThereIsNoOutboundLink()
    {
        // Opting in via url-text keeps the card free of a link the site owner didn't ask for.
        var folder = MakeFolder("Videos");
        MakeVideo(folder, "Talk.url", "https://youtu.be/AbCdEfGhIjK");

        Generate();

        Assert.DoesNotContain("video-source-link", ReadPage("Videos"));
    }

    [Fact]
    public void ThePosterWriteBackBringsTheYamlsIdAlongWithIt()
    {
        // The poster is re-fetched whenever the .url changes, and the id rides along on that write
        // so the file on disk doesn't end up naming a different video than the page does. The
        // surgical editor is doing the work, so the user's comments have to survive it.
        var folder = MakeFolder("Videos");
        var yaml = Path.Combine(folder, "Talk.url.yaml");
        File.WriteAllText(yaml,
            """
            # Hand-written, keep me
            type: video
            caption: A Talk
            provider: youtube
            videoId: OldOldOldOl
            """);

        YamlParser.UpdatePreviewFields(
            yaml,
            ".dir2site/Talk/preview-Talk.webp",
            ".dir2site/Talk/preview-lg-Talk.webp",
            extra: [new("videoId", "NewNewNewNe"), new("provider", "youtube")]);

        var updated = File.ReadAllText(yaml);
        Assert.Contains("videoId: NewNewNewNe", updated);
        Assert.DoesNotContain("OldOldOldOl", updated);
        Assert.Contains("# Hand-written, keep me", updated);
        Assert.Contains("caption: A Talk", updated);
    }

    [AvaloniaFact]
    public void AVideoCanStandInAsAFolderThumbnail()
    {
        // The poster is a real file in .dir2site like any other preview, so the existing
        // first-artifact-with-a-preview logic picks it up with no special case.
        var folder = MakeFolder("Videos");
        MakeVideo(folder, "Talk.url", "https://youtu.be/AbCdEfGhIjK");

        Generate();

        Assert.Contains("Videos/Talk/preview-Talk.webp", ReadPage());
    }
}
