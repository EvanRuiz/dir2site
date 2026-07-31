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
/// The caption a file gets before anyone edits it. For video it comes from a filename the author
/// didn't choose — a browser saving a YouTube shortcut names it "&lt;title&gt; - YouTube.url" — so
/// the provider's own branding has to come back off before it becomes a card title.
/// </summary>
public class DefaultCaptionTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "d2s-caption-" + Guid.NewGuid().ToString("N"));

    public DefaultCaptionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>Writes a shortcut, lets the parser create the default yaml, and returns the caption.</summary>
    private string? CaptionFor(string fileName)
    {
        var path = Path.Combine(_dir, fileName);
        File.WriteAllText(path, "[InternetShortcut]\r\nURL=https://youtu.be/AbCdEfGhIjK\r\n");

        var errors = new List<string>();
        var artifact = YamlParser.TryParseYamlMeta(path, errors);
        Assert.Empty(errors);
        return artifact?.Caption;
    }

    [Fact]
    public void TheYouTubeSuffixIsDropped()
    {
        Assert.Equal("Never Gonna Give You Up", CaptionFor("Never Gonna Give You Up - YouTube.url"));
    }

    [Theory]
    [InlineData("A Talk - youtube.url")]
    [InlineData("A Talk - YOUTUBE.url")]
    public void TheMatchIgnoresCase(string fileName)
    {
        // Whatever the browser or the filesystem did to the casing, it is the same suffix.
        Assert.Equal("A Talk", CaptionFor(fileName));
    }

    [Fact]
    public void OnlyTheTrailingSuffixGoes_NotAnEarlierHyphen()
    {
        Assert.Equal("Dogs - A Study", CaptionFor("Dogs - A Study - YouTube.url"));
    }

    [Fact]
    public void TheShapeMostVideoTitlesArriveIn_KeepsItsSpacing()
    {
        Assert.Equal("Rick Astley - Never Gonna Give You Up",
                     CaptionFor("Rick Astley - Never Gonna Give You Up - YouTube.url"));
    }

    [Fact]
    public void ACompoundNameKeepsItsTightHyphen()
    {
        // The distinction is the space: "annual-report" is one name, not two parts.
        Assert.Equal("Annual-Report", CaptionFor("annual-report - YouTube.url"));
    }

    [Fact]
    public void ATitleThatMerelyMentionsYouTubeIsLeftAlone()
    {
        // Anchored to the end and requires the separator, so this is a title, not a suffix.
        Assert.Equal("You Tube At 20", CaptionFor("YouTube at 20.url"));
    }

    [Fact]
    public void AShortcutNamedOnlyForTheProviderKeepsSomethingToBeCalled()
    {
        Assert.Equal("You Tube", CaptionFor("YouTube.url"));
    }

    [Fact]
    public void NonVideoFilesAreUntouched()
    {
        // The rule belongs to the .url path; a photo that happens to be named this way is just a
        // photo with an odd name.
        var path = Path.Combine(_dir, "Holiday - YouTube.jpg");
        File.WriteAllText(path, "not really a jpeg");

        var errors = new List<string>();
        var artifact = YamlParser.TryParseYamlMeta(path, errors);

        Assert.Equal("Holiday - You Tube", artifact?.Caption);
    }
}
