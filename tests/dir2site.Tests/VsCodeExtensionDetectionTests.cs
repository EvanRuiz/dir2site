// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.IO;
using dir2site.Services;
using Xunit;

namespace dir2site.Tests;

/// <summary>
/// Deciding whether to offer the extension at all. Everything here runs against temp directories
/// and a stubbed CLI — the real ones belong to whoever is running the tests.
/// </summary>
public class VsCodeExtensionDetectionTests : IDisposable
{
    private readonly string _scratch = Directory.CreateTempSubdirectory("dir2site-detect-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch { }
    }

    /// <summary>An extensions folder with nothing in it yet.</summary>
    private string Root(string name)
    {
        var path = Path.Combine(_scratch, name);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Detection on a machine with no code CLI, where the folders are the whole story.</summary>
    private static ExtensionState Scan(string[] roots) =>
        VsCodeExtensionInstaller.Detect(roots, NoCli);

    private static VsCodeExtensionInstaller.CliExtensions NoCli() => new(false, false, null);

    /// <summary>An installed extension: the folder VS Code makes, and the manifest inside it.</summary>
    private static string Installed(string root, string version, string? manifestVersion = null)
    {
        var dir = Path.Combine(root, $"dir2site.dir2site-markdown-{version}");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "package.json"),
            $$"""{"name":"dir2site-markdown","publisher":"dir2site","version":"{{manifestVersion ?? version}}"}""");
        return dir;
    }

    /// <summary>The extension as it was before the rename, which VS Code keeps as a separate one.</summary>
    private static string LegacyInstalled(string root, string version = "0.1.3")
    {
        var dir = Path.Combine(root, $"dir2site.dir2site-figures-{version}");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "package.json"),
            $$"""{"name":"dir2site-figures","publisher":"dir2site","version":"{{version}}"}""");
        return dir;
    }

    [Fact]
    public void NoExtensionsFolderMeansNoVsCode()
    {
        var state = Scan([]);

        Assert.False(state.VsCodeFound);
        Assert.Null(state.Installed);
    }

    [Fact]
    public void AFolderThatIsNotThereIsNotVsCode()
    {
        // Detect is handed candidate paths in tests; in the app they are pre-filtered, but a path
        // that vanished between the two shouldn't be reported as an install target.
        var state = Scan([Path.Combine(_scratch, "never-created")]);

        Assert.False(state.VsCodeFound);
    }

    [Fact]
    public void AnEmptyExtensionsFolderIsVsCodeWithoutTheExtension()
    {
        var state = Scan([Root("vscode")]);

        Assert.True(state.VsCodeFound);
        Assert.Null(state.Installed);
    }

    [Fact]
    public void OtherExtensionsAreNotOurs()
    {
        var root = Root("vscode");
        Directory.CreateDirectory(Path.Combine(root, "ms-python.python-2024.1.0"));

        Assert.Null(Scan([root]).Installed);
    }

    [Fact]
    public void TheInstalledVersionIsRead()
    {
        var root = Root("vscode");
        Installed(root, "0.1.0");

        Assert.Equal(new Version(0, 1, 0), Scan([root]).Installed);
    }

    [Fact]
    public void TheFolderNameIsMatchedWithoutRegardToCase()
    {
        // Windows and Linux disagree about what a directory search pattern matches, so the match is
        // made in code. This is the case that would silently stop finding anything on Linux.
        var root = Root("vscode");
        var dir = Path.Combine(root, "DIR2SITE.dir2site-markdown-0.1.0");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "package.json"), """{"version":"0.1.0"}""");

        Assert.Equal(new Version(0, 1, 0), Scan([root]).Installed);
    }

    [Fact]
    public void TheManifestOutranksTheFolderName()
    {
        // Only the folder-copy route guarantees the two agree. A hand-installed or repacked
        // extension can carry a manifest saying something else, and the manifest is what VS Code
        // actually runs.
        var root = Root("vscode");
        Installed(root, "0.1.0", manifestVersion: "0.2.0");

        Assert.Equal(new Version(0, 2, 0), Scan([root]).Installed);
    }

    [Fact]
    public void AnUnreadableManifestFallsBackToTheFolderName()
    {
        var root = Root("vscode");
        var dir = Installed(root, "0.1.0");
        File.WriteAllText(Path.Combine(dir, "package.json"), "{ this is not json");

        Assert.Equal(new Version(0, 1, 0), Scan([root]).Installed);
    }

    [Fact]
    public void TheNewestInstallWinsAcrossEditors()
    {
        // VS Code and Cursor side by side, the extension added to each at different times.
        var code = Root("vscode");
        var cursor = Root("cursor");
        Installed(code, "0.1.0");
        Installed(cursor, "0.3.0");

        Assert.Equal(new Version(0, 3, 0), Scan([code, cursor]).Installed);
    }

    [Fact]
    public void AFolderMarkedObsoleteIsNotInstalled()
    {
        // Uninstalling flags the folder and leaves it behind until VS Code next sweeps up. Counting
        // it would keep the install offer hidden from someone who just removed the extension.
        var root = Root("vscode");
        Installed(root, "0.1.0");
        File.WriteAllText(Path.Combine(root, ".obsolete"),
            """{"dir2site.dir2site-markdown-0.1.0":true}""");

        Assert.Null(Scan([root]).Installed);
    }

    [Fact]
    public void TheCliOutranksTheFolders()
    {
        // The case that decides the order. A portable VS Code keeps its extensions beside the
        // executable, invisible to any scan, while a leftover ~/.vscode still holds an older copy.
        // Believing the folders would show an update banner that installs into the portable copy
        // that already has it — and so never clears.
        var root = Root("vscode");
        Installed(root, "0.1.0");

        var state = VsCodeExtensionInstaller.Detect(
            [root], () => new VsCodeExtensionInstaller.CliExtensions(true, true, new Version(0, 1, 1)));

        Assert.Equal(new Version(0, 1, 1), state.Installed);
    }

    [Fact]
    public void TheCliIsBelievedWhenItSaysNothingIsInstalled()
    {
        // Same reasoning the other way round: the extension is in some other editor's folder, but
        // the install would go through the CLI, and the CLI hasn't got it.
        var root = Root("cursor");
        Installed(root, "0.1.1");

        var state = VsCodeExtensionInstaller.Detect(
            [root], () => new VsCodeExtensionInstaller.CliExtensions(true, true, null));

        Assert.True(state.VsCodeFound);
        Assert.Null(state.Installed);
    }

    [Fact]
    public void ACliThatAnswersProvesThereIsAVsCodeEvenWithNoFolders()
    {
        var state = VsCodeExtensionInstaller.Detect(
            [], () => new VsCodeExtensionInstaller.CliExtensions(true, true, null));

        Assert.True(state.VsCodeFound);
        Assert.Null(state.Installed);
    }

    [Fact]
    public void ACliThatSaidNothingIsNotTreatedAsAnAnswer()
    {
        // An empty extension list and a read that came back empty look identical from here, and the
        // second one happened: the stdout task hadn't been observed as complete, so a full listing
        // arrived as "". Believing it ends detection at "nothing installed" and offers a fresh
        // install to someone who has the extension. Handing to the folders costs a listing and is
        // right either way — a genuinely empty VS Code has empty folders too.
        var root = Root("vscode");
        Installed(root, "0.1.0");

        var state = VsCodeExtensionInstaller.Detect(
            [root], () => new VsCodeExtensionInstaller.CliExtensions(true, false, null));

        Assert.Equal(new Version(0, 1, 0), state.Installed);
    }

    [Fact]
    public void ACliThatErroredHandsBackToTheFolders()
    {
        // It ran, so there is a VS Code; it just couldn't say what was in it.
        var root = Root("vscode");
        Installed(root, "0.1.0");

        var state = VsCodeExtensionInstaller.Detect(
            [root], () => new VsCodeExtensionInstaller.CliExtensions(true, false, null));

        Assert.True(state.VsCodeFound);
        Assert.Equal(new Version(0, 1, 0), state.Installed);
    }

    [Fact]
    public void ACliThatErroredStillProvesThereIsAVsCode()
    {
        var state = VsCodeExtensionInstaller.Detect(
            [], () => new VsCodeExtensionInstaller.CliExtensions(true, false, null));

        Assert.True(state.VsCodeFound);
        Assert.Null(state.Installed);
    }

    [Fact]
    public void ThePreRenameExtensionReadsAsSomethingToUpdate()
    {
        // It is a different extension as far as VS Code is concerned, so it does not show up as an
        // old version of the new one. It still has to be found, or nobody ever gets migrated off it.
        var root = Root("vscode");
        LegacyInstalled(root);

        var state = Scan([root]);

        Assert.True(state.HasLegacy);
        Assert.Null(state.Installed);
    }

    [Fact]
    public void ThePreRenameExtensionCountsEvenBesideACurrentInstall()
    {
        // Installing puts the new one in and takes the old one out. If the old one survived that —
        // a copy-route install that could not delete it — there is still something to do.
        var root = Root("vscode");
        Installed(root, "0.2.0");
        LegacyInstalled(root);

        var state = Scan([root]);

        Assert.True(state.HasLegacy);
        Assert.Equal(new Version(0, 2, 0), state.Installed);
    }

    [Fact]
    public void ThePreRenameExtensionIsFoundThroughTheCliToo()
    {
        var state = VsCodeExtensionInstaller.Detect(
            [], () => new VsCodeExtensionInstaller.CliExtensions(true, true, null, HasLegacy: true));

        Assert.True(state.HasLegacy);
        Assert.Null(state.Installed);
    }

    [Fact]
    public void APreRenameFolderMarkedObsoleteIsNotCounted()
    {
        var root = Root("vscode");
        LegacyInstalled(root);
        File.WriteAllText(Path.Combine(root, ".obsolete"),
            """{"dir2site.dir2site-figures-0.1.3":true}""");

        Assert.False(Scan([root]).HasLegacy);
    }

    [Fact]
    public void RemovingTheOldExtensionLeavesOtherEditorsAlone()
    {
        // The copy route writes into one directory, so it may only clear the old extension out of
        // that one. A user running VS Code and Cursor with the old extension in both would
        // otherwise be left with a second editor that has neither — worse off for having installed.
        var code = Root("vscode");
        var cursor = Root("cursor");
        var installedInto = LegacyInstalled(code);
        var untouched = LegacyInstalled(cursor);

        VsCodeExtensionInstaller.RemoveLegacyFolders(code);

        Assert.False(Directory.Exists(installedInto));
        Assert.True(Directory.Exists(untouched));
    }

    [Fact]
    public void RemovingTheOldExtensionSparesTheNewOne()
    {
        var root = Root("vscode");
        var current = Installed(root, "0.2.0");
        LegacyInstalled(root);

        VsCodeExtensionInstaller.RemoveLegacyFolders(root);

        Assert.True(Directory.Exists(current));
        Assert.False(Scan([root]).HasLegacy);
    }

    [Fact]
    public void RemovingFromADirectoryThatIsNotThereIsNotAnError()
    {
        VsCodeExtensionInstaller.RemoveLegacyFolders(Path.Combine(_scratch, "never-created"));
    }

    [Fact]
    public void TheWindowsCommandLineSurvivesAPathWithSpaces()
    {
        // Every default VS Code install is under "Program Files" or "Microsoft VS Code", so this is
        // the normal case rather than an awkward one. Without /s and the outer pair of quotes, cmd
        // takes the path's own quotes off and runs "C:\Program".
        var line = VsCodeExtensionInstaller.WindowsCommandLine(
            @"C:\Program Files\Microsoft VS Code\bin\code.cmd", ["--list-extensions"]);

        // Escaped rather than a raw literal: every character here is a quote or a backslash, and
        // the point of the test is exactly where they fall.
        Assert.Equal(
            "/s /c \"\"C:\\Program Files\\Microsoft VS Code\\bin\\code.cmd\" \"--list-extensions\"\"",
            line);
    }

    [Fact]
    public void EveryWindowsCandidateIsEitherThePlainNameOrAnAbsolutePath()
    {
        // A relative path here would be resolved against whatever directory the app happens to be
        // running in, which is nobody's idea of where VS Code is.
        foreach (var candidate in VsCodeExtensionInstaller.CliCandidates())
            Assert.True(candidate == "code" || Path.IsPathRooted(candidate), candidate);
    }

    [Fact]
    public void TheBundledVersionMatchesTheManifestConstant()
    {
        // The comparison the banner is made of; if this parse ever stopped agreeing with the string
        // the app would quietly offer an update to nothing.
        Assert.Equal(VsCodeExtensionInstaller.Version, VsCodeExtensionInstaller.BundledVersion.ToString());
    }
}
