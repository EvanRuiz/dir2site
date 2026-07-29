// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.IO;
using dir2site.Models;
using dir2site.Services;
using Xunit;

namespace dir2site.Tests;

/// <summary>
/// Generate Site writes the project config on every run, so this is the path that used to eat a
/// hand-edited dir2site.yaml the first time the user clicked the button.
/// </summary>
public class SaveDir2SiteConfigTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "d2s-cfg-" + Guid.NewGuid().ToString("N"));

    public SaveDir2SiteConfigTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string Path_ => System.IO.Path.Combine(_dir, "dir2site.yaml");

    private static Dir2SiteModel Sample() => new()
    {
        Title = "My Site",
        Footer = "© 2026",
        PrimaryColor = "#333333",
        SecondaryColor = "#666666",
        BackgroundColor = "#ffffff",
        NavbarDark = true,
        PdfResizeEnabled = true,
        PdfMaxWidth = 1600,
        PdfQuality = 80,
    };

    [Fact]
    public void CreatesTheFileWhenAbsent()
    {
        YamlParser.SaveDir2SiteConfig(Path_, Sample());

        var loaded = YamlParser.DeserializeAs<Dir2SiteModel>(File.ReadAllText(Path_));
        Assert.Equal("My Site", loaded.Title);
        Assert.Equal(1600, loaded.PdfMaxWidth);
    }

    [Fact]
    public void ChangingATitle_KeepsCommentsAndUnknownKeys()
    {
        File.WriteAllText(Path_,
            """
            # My site config — please don't eat my notes
            title: Old Name
            footer: © 2026

            # Colours picked to match the logo
            primaryColor: '#333333'
            secondaryColor: '#666666'
            backgroundColor: '#ffffff'
            navbarDark: true
            pdfResizeEnabled: true
            pdfMaxWidth: 1600
            pdfQuality: 80
            experimentalThing: yes please
            """);

        var config = Sample();
        config.Title = "New Name";
        YamlParser.SaveDir2SiteConfig(Path_, config);

        var result = File.ReadAllText(Path_);
        Assert.Contains("# My site config — please don't eat my notes", result);
        Assert.Contains("# Colours picked to match the logo", result);
        Assert.Contains("experimentalThing: yes please", result);
        Assert.Contains("title: New Name", result);
        Assert.DoesNotContain("Old Name", result);
    }

    [Fact]
    public void BooleansAndNumbers_StayBareNotQuoted()
    {
        YamlParser.SaveDir2SiteConfig(Path_, Sample());
        var config = Sample();
        config.NavbarDark = false;
        config.PdfMaxWidth = 1200;

        YamlParser.SaveDir2SiteConfig(Path_, config);

        var result = File.ReadAllText(Path_);
        Assert.Contains("navbarDark: false", result);
        Assert.Contains("pdfMaxWidth: 1200", result);
        Assert.DoesNotContain("\"false\"", result);
        Assert.DoesNotContain("'1200'", result);

        var loaded = YamlParser.DeserializeAs<Dir2SiteModel>(result);
        Assert.False(loaded.NavbarDark);
        Assert.Equal(1200, loaded.PdfMaxWidth);
    }

    [Fact]
    public void NoChanges_LeaveTheFileByteIdentical()
    {
        YamlParser.SaveDir2SiteConfig(Path_, Sample());
        var before = File.ReadAllText(Path_);

        YamlParser.SaveDir2SiteConfig(Path_, Sample());

        Assert.Equal(before, File.ReadAllText(Path_));
    }

    [Fact]
    public void UnparseableConfig_IsRewrittenRatherThanLost()
    {
        File.WriteAllText(Path_, "title: [broken\n  : nonsense {\n");

        YamlParser.SaveDir2SiteConfig(Path_, Sample());

        var loaded = YamlParser.DeserializeAs<Dir2SiteModel>(File.ReadAllText(Path_));
        Assert.Equal("My Site", loaded.Title);
    }
}
