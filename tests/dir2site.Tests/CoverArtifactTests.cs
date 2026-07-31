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
/// Which picture represents a folder. Left alone, the generator takes whichever artifact sorts
/// first, which is rarely the one that says what the collection is — <c>cover: true</c> is how an
/// author overrides that. It drives the folder's card on the parent page and the page's own
/// og:image, so a shared link gets the chosen picture too.
/// </summary>
public class CoverArtifactTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "d2s-cover-" + Guid.NewGuid().ToString("N"));

    public CoverArtifactTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string SitePath(params string[] parts) => Path.Combine([_root, "_site", .. parts]);

    private string ReadPage(params string[] parts) =>
        File.ReadAllText(SitePath([.. parts, "index.html"]));

    private string MakeFolder(params string[] parts)
    {
        var path = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(path);
        return path;
    }

    private void MakeArtifact(string folder, string fileName, string caption, bool cover = false)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        File.WriteAllText(Path.Combine(folder, fileName), "not really a jpeg");
        File.WriteAllText(Path.Combine(folder, fileName + ".yaml"),
            $"""
             type: photo
             caption: {caption}
             preview: .dir2site/{stem}/{stem}-preview.jpg
             previewLarge: .dir2site/{stem}/{stem}-preview-large.jpg
             {(cover ? "cover: true" : "")}
             """);
    }

    private void Generate()
    {
        var tree = DirectoryTraverser.BuildTree(_root, new List<string>(), new List<string>());
        SiteGenerator.Generate(_root, tree, new Dir2SiteModel
        {
            Title = "My Site",
            Footer = "© 2026",
            SiteUrl = "https://example.test",
        });
    }

    [AvaloniaFact]
    public void WithoutACover_TheFirstArtifactRepresentsTheFolder()
    {
        var folder = MakeFolder("Photographs");
        MakeArtifact(folder, "Apple.jpg", "Apple");
        MakeArtifact(folder, "Zebra.jpg", "Zebra");

        Generate();

        Assert.Contains("Apple/Apple-preview.jpg", ReadPage());
    }

    [AvaloniaFact]
    public void AMarkedArtifactBecomesTheFoldersCard()
    {
        var folder = MakeFolder("Photographs");
        MakeArtifact(folder, "Apple.jpg", "Apple");
        MakeArtifact(folder, "Zebra.jpg", "Zebra", cover: true);

        Generate();
        var home = ReadPage();

        Assert.Contains("Zebra/Zebra-preview.jpg", home);
        Assert.DoesNotContain("Apple/Apple-preview.jpg", home);
    }

    [AvaloniaFact]
    public void ACoverBeatsTheTypePreference()
    {
        // A photo would normally outrank a PDF whatever the captions say.
        var folder = MakeFolder("Documents");
        MakeArtifact(folder, "Photo.jpg", "A Photo");
        File.WriteAllText(Path.Combine(folder, "Report.pdf"), "not really a pdf");
        File.WriteAllText(Path.Combine(folder, "Report.pdf.yaml"),
            """
            type: pdf
            caption: The Report
            preview: .dir2site/Report/Report-preview.jpg
            previewLarge: .dir2site/Report/Report-preview-large.jpg
            cover: true
            """);

        Generate();

        Assert.Contains("Report/Report-preview.jpg", ReadPage());
    }

    [AvaloniaFact]
    public void TheCoverIsAlsoThePagesOgImage()
    {
        var folder = MakeFolder("Photographs");
        MakeArtifact(folder, "Apple.jpg", "Apple");
        MakeArtifact(folder, "Zebra.jpg", "Zebra", cover: true);

        Generate();
        var page = ReadPage("Photographs");

        Assert.Contains("og:image", page);
        Assert.Contains("Zebra/Zebra-preview-large.jpg", page);
    }

    [AvaloniaFact]
    public void ACoverOnlySpeaksForItsOwnFolder()
    {
        // Marking something nested does not make it the cover of everything above it — the parent
        // still shows its own first artifact.
        var parent = MakeFolder("Photographs");
        MakeArtifact(parent, "Apple.jpg", "Apple");
        var nested = MakeFolder("Photographs", "1890s");
        MakeArtifact(nested, "Zebra.jpg", "Zebra", cover: true);

        Generate();

        Assert.Contains("Apple/Apple-preview.jpg", ReadPage());
        Assert.Contains("Zebra/Zebra-preview.jpg", ReadPage("Photographs"));
    }

    [AvaloniaFact]
    public void ACoverWithNoPreviewIsSkippedRatherThanLeavingTheCardBlank()
    {
        var folder = MakeFolder("Photographs");
        File.WriteAllText(Path.Combine(folder, "Broken.jpg"), "not really a jpeg");
        File.WriteAllText(Path.Combine(folder, "Broken.jpg.yaml"),
            "type: photo\ncaption: Broken\ncover: true\n");
        MakeArtifact(folder, "Apple.jpg", "Apple");

        Generate();

        Assert.Contains("Apple/Apple-preview.jpg", ReadPage());
    }
}
