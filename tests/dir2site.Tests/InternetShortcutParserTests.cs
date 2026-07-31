// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.IO;
using dir2site.Services;
using Xunit;

namespace dir2site.Tests;

/// <summary>
/// A .url is the only artifact source the app doesn't own the format of — these files are written
/// by browsers, by file managers and by hand, and the one in front of us may well point at an
/// ordinary web page rather than a video. Everything here is about being liberal with the shapes we
/// accept and strict about what we agree to call a video, because a wrong id doesn't fail loudly:
/// it produces a card with an embed that silently 404s for every visitor.
/// </summary>
public class InternetShortcutParserTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "d2s-url-" + Guid.NewGuid().ToString("N"));

    public InternetShortcutParserTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string WriteShortcut(string content, string name = "Talk.url")
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    // ---- reading the INI ----

    [Fact]
    public void ReadsTheUrlFromAConventionalShortcut()
    {
        var path = WriteShortcut("[InternetShortcut]\r\nURL=https://youtu.be/AbCdEfGhIjK\r\n");
        Assert.Equal("https://youtu.be/AbCdEfGhIjK", InternetShortcutParser.TryReadUrl(path));
    }

    [Theory]
    // Windows writes CRLF; a file that has been through a Unix tool has LF.
    [InlineData("[InternetShortcut]\nURL=https://youtu.be/AbCdEfGhIjK\n")]
    // Real shortcuts carry other keys, and the URL is not always first.
    [InlineData("[InternetShortcut]\r\nIDList=\r\nIconFile=C:\\icon.ico\r\nIconIndex=0\r\nURL=https://youtu.be/AbCdEfGhIjK\r\nHotKey=0\r\n")]
    // Casing is not guaranteed by anything.
    [InlineData("[internetshortcut]\r\nurl=https://youtu.be/AbCdEfGhIjK\r\n")]
    // Hand-edited files pick up stray indentation and blank lines.
    [InlineData("\r\n  [InternetShortcut]  \r\n\r\n   URL = https://youtu.be/AbCdEfGhIjK   \r\n")]
    // A BOM survives some editors even when the rest of the file is plain ASCII.
    [InlineData("\uFEFF[InternetShortcut]\r\nURL=https://youtu.be/AbCdEfGhIjK\r\n")]
    // Plenty of hand-written files omit the section header entirely.
    [InlineData("URL=https://youtu.be/AbCdEfGhIjK\r\n")]
    public void ToleratesTheShapesRealShortcutsComeIn(string content)
    {
        var path = WriteShortcut(content);
        Assert.Equal("https://youtu.be/AbCdEfGhIjK", InternetShortcutParser.TryReadUrl(path));
    }

    [Fact]
    public void IgnoresAUrlBelongingToADifferentSection()
    {
        // The GUID section is written by Windows and can carry its own keys. Only ours counts.
        var path = WriteShortcut(
            "[{000214A0-0000-0000-C000-000000000046}]\r\nURL=https://youtu.be/ZZZZZZZZZZZ\r\n" +
            "[InternetShortcut]\r\nURL=https://youtu.be/AbCdEfGhIjK\r\n");

        Assert.Equal("https://youtu.be/AbCdEfGhIjK", InternetShortcutParser.TryReadUrl(path));
    }

    [Theory]
    [InlineData("[InternetShortcut]\r\nIconFile=C:\\icon.ico\r\n")]  // no URL at all
    [InlineData("[InternetShortcut]\r\nURL=\r\n")]                   // present but empty
    [InlineData("")]
    public void ReturnsNullWhenThereIsNoUsableUrl(string content) =>
        Assert.Null(InternetShortcutParser.TryReadUrl(WriteShortcut(content)));

    [Fact]
    public void AMissingFileIsNullRatherThanAnException() =>
        Assert.Null(InternetShortcutParser.TryReadUrl(Path.Combine(_dir, "nope.url")));

    // ---- recognising the video ----

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=AbCdEfGhIjK")]
    [InlineData("https://youtube.com/watch?v=AbCdEfGhIjK")]
    [InlineData("https://m.youtube.com/watch?v=AbCdEfGhIjK")]
    [InlineData("http://www.youtube.com/watch?v=AbCdEfGhIjK")]
    [InlineData("https://youtu.be/AbCdEfGhIjK")]
    [InlineData("https://www.youtube.com/embed/AbCdEfGhIjK")]
    [InlineData("https://www.youtube.com/shorts/AbCdEfGhIjK")]
    [InlineData("https://www.youtube.com/live/AbCdEfGhIjK")]
    [InlineData("https://www.youtube-nocookie.com/embed/AbCdEfGhIjK")]
    public void RecognisesEveryFormYouTubeHandsOut(string url)
    {
        var video = InternetShortcutParser.TryParseVideo(url);

        Assert.NotNull(video);
        Assert.Equal(InternetShortcutParser.YouTube, video!.Provider);
        Assert.Equal("AbCdEfGhIjK", video.VideoId);
    }

    [Fact]
    public void IgnoresTheExtraParametersAShareLinkCarries()
    {
        // Copying a link out of a playlist appends list= and index=. Rejecting the URL over them
        // would break the most common way anyone actually gets a link.
        var video = InternetShortcutParser.TryParseVideo(
            "https://www.youtube.com/watch?v=AbCdEfGhIjK&list=PL6yBRGthjpACu&index=3&pp=iAQB");

        Assert.NotNull(video);
        Assert.Equal("AbCdEfGhIjK", video!.VideoId);
    }

    [Theory]
    [InlineData("https://vimeo.com/123456789")]                       // provider we don't support
    [InlineData("https://example.com/")]                              // an ordinary bookmark
    [InlineData("https://www.youtube.com/watch?v=tooshort")]          // id isn't 11 chars
    [InlineData("https://www.youtube.com/watch?v=AbCdEfGhIjKLMNOP")]  // id is too long
    [InlineData("https://www.youtube.com/watch?v=AbCdEfGh!jK")]       // id has a character it can't
    [InlineData("https://www.youtube.com/watch")]                     // no v= at all
    [InlineData("https://www.youtube.com/feed/subscriptions")]        // youtube, but not a video
    [InlineData("file:///Users/someone/movie.mp4")]                   // not even http
    [InlineData("not a url")]
    [InlineData("")]
    [InlineData(null)]
    public void RefusesAnythingItCannotEmbed(string? url) =>
        Assert.Null(InternetShortcutParser.TryParseVideo(url));

    // ---- the start offset ----

    [Theory]
    [InlineData("https://youtu.be/AbCdEfGhIjK?t=90", 90)]        // youtu.be uses a bare count
    [InlineData("https://youtu.be/AbCdEfGhIjK?t=90s", 90)]       // the watch page adds the suffix
    [InlineData("https://youtu.be/AbCdEfGhIjK?t=1m30s", 90)]
    [InlineData("https://youtu.be/AbCdEfGhIjK?t=1h1m1s", 3661)]
    [InlineData("https://youtu.be/AbCdEfGhIjK?t=2m", 120)]
    [InlineData("https://www.youtube.com/embed/AbCdEfGhIjK?start=40", 40)]
    public void ReadsTheStartOffsetInEveryFormatYouTubeWrites(string url, int expected) =>
        Assert.Equal(expected, InternetShortcutParser.TryParseVideo(url)!.Start);

    [Theory]
    [InlineData("https://youtu.be/AbCdEfGhIjK")]         // absent
    [InlineData("https://youtu.be/AbCdEfGhIjK?t=0")]     // zero is the same as not asking
    [InlineData("https://youtu.be/AbCdEfGhIjK?t=abc")]   // unparseable
    [InlineData("https://youtu.be/AbCdEfGhIjK?t=")]
    public void LeavesTheStartOffsetUnsetWhenThereIsntARealOne(string url) =>
        Assert.Null(InternetShortcutParser.TryParseVideo(url)!.Start);

    [Fact]
    public void ANonVideoShortcutYieldsNothingEvenThoughTheFileParsedFine()
    {
        // The distinction that keeps an ordinary bookmark from becoming a broken card: the INI is
        // perfectly readable, it just doesn't point anywhere we can embed.
        var path = WriteShortcut("[InternetShortcut]\r\nURL=https://example.com/\r\n");

        Assert.Equal("https://example.com/", InternetShortcutParser.TryReadUrl(path));
        Assert.Null(InternetShortcutParser.TryReadVideo(path));
    }

    [Fact]
    public void TryReadVideoReturnsTheOriginalUrlAlongsideTheVideo()
    {
        // The card links back to whatever the user actually saved, playlist parameters and all —
        // not to a URL we reassembled from the id.
        var original = "https://www.youtube.com/watch?v=AbCdEfGhIjK&list=PL6yBRGthjpACu&t=40s";
        var shortcut = InternetShortcutParser.TryReadVideo(
            WriteShortcut($"[InternetShortcut]\r\nURL={original}\r\n"));

        Assert.NotNull(shortcut);
        Assert.Equal(original, shortcut!.Url);
        Assert.Equal("AbCdEfGhIjK", shortcut.Video.VideoId);
        Assert.Equal(40, shortcut.Video.Start);
    }
}
