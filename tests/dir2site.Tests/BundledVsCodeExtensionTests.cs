// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using dir2site.Services;
using Xunit;

namespace dir2site.Tests;

/// <summary>
/// The .vsix the app ships is committed rather than built, so nothing rebuilds it when the
/// extension source changes. These catch the resulting failure mode — editing the extension,
/// forgetting to repackage, and shipping a stale copy that looks fine.
///
/// When one of these fails the fix is <c>scripts/package-vscode-extension.sh</c>.
/// </summary>
public class BundledVsCodeExtensionTests
{
    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "dir2site.sln")))
                return dir.FullName;

        throw new InvalidOperationException("Could not locate the repository root.");
    }

    private static string VsixPath =>
        Path.Combine(RepoRoot(), "Assets", "editors", "dir2site-figures.vsix");

    private static string SourceDir =>
        Path.Combine(RepoRoot(), "editors", "vscode-dir2site-figures");

    /// A failing staleness check should say what to run, not just that something disagrees.
    private const string Repackage =
        " Run scripts/package-vscode-extension.sh and commit the result.";

    private static string ReadEntry(string entryName)
    {
        using var zip = ZipFile.OpenRead(VsixPath);
        var entry = zip.GetEntry(entryName)
            ?? throw new InvalidOperationException($"{entryName} missing from the package.");
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    [Fact]
    public void ThePackageIsShipped()
    {
        Assert.True(File.Exists(VsixPath),
            "Assets/editors/dir2site-figures.vsix is missing — the install button would have nothing to install."
            + Repackage);
    }

    [Fact]
    public void ThePackagedVersionMatchesTheSource()
    {
        var source = JsonDocument.Parse(File.ReadAllText(Path.Combine(SourceDir, "package.json")))
                                 .RootElement.GetProperty("version").GetString();
        var packaged = JsonDocument.Parse(ReadEntry("extension/package.json"))
                                   .RootElement.GetProperty("version").GetString();

        Assert.True(source == packaged,
            $"Packaged version {packaged} but the source says {source}.{Repackage}");
    }

    [Fact]
    public void ThePackagedPluginMatchesTheSourceByteForByte()
    {
        // The version alone wouldn't catch a fix made without bumping it, which is the more likely
        // mistake of the two.
        var source = File.ReadAllText(Path.Combine(SourceDir, "markdown-it-dir2site-figures.js"))
                         .Replace("\r\n", "\n");
        var packaged = ReadEntry("extension/markdown-it-dir2site-figures.js").Replace("\r\n", "\n");

        Assert.True(source == packaged,
            "The packaged plugin differs from editors/vscode-dir2site-figures." + Repackage);
    }

    [Fact]
    public void ThePackagedStylesheetMatchesTheSource()
    {
        var source = File.ReadAllText(Path.Combine(SourceDir, "media", "dir2site-figures.css"))
                         .Replace("\r\n", "\n");
        var packaged = ReadEntry("extension/media/dir2site-figures.css").Replace("\r\n", "\n");

        Assert.True(source == packaged,
            "The packaged stylesheet differs from editors/vscode-dir2site-figures." + Repackage);
    }

    [Fact]
    public void ThePackagedIdentityMatchesWhatTheInstallerWritesToDisk()
    {
        // The fallback route creates a folder named publisher.name-version; if these drift, VS Code
        // ends up with a folder whose name disagrees with the manifest inside it.
        //
        // Read from the installer rather than repeating the string: a version bump should be two
        // edits, the manifest and the constant, not three with a test failure to remind you.
        var manifest = JsonDocument.Parse(ReadEntry("extension/package.json")).RootElement;
        var publisher = manifest.GetProperty("publisher").GetString();
        var name = manifest.GetProperty("name").GetString();
        var version = manifest.GetProperty("version").GetString();

        Assert.Equal(
            $"{VsCodeExtensionInstaller.PublisherAndName}-{VsCodeExtensionInstaller.Version}",
            $"{publisher}.{name}-{version}");
    }

    [Fact]
    public void ThePackageCarriesItsLicence()
    {
        using var zip = ZipFile.OpenRead(VsixPath);

        // AGPL, and a .vsix is redistribution — the licence has to travel with it.
        Assert.Contains(zip.Entries, e =>
            e.FullName.StartsWith("extension/LICENSE", StringComparison.OrdinalIgnoreCase));
    }
}
