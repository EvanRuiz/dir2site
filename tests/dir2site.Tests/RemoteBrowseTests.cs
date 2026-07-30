// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.IO;
using System.Linq;
using dir2site.SftpSync.Core;
using Xunit;

namespace dir2site.Tests;

/// <summary>
/// The remote path was typed blind — you had to already know it, and a typo passed every check
/// until the first real deploy. These back the browse dialog.
/// </summary>
public class RemoteBrowseTests(SftpServerFixture fx) : IClassFixture<SftpServerFixture>
{
    [SkippableFact]
    public void ListsDirectories_SortedAndWithoutDotEntries()
    {
        Skip.IfNot(fx.Available, fx.Reason);
        var d = fx.NewDeployment();
        Directory.CreateDirectory(Path.Combine(d.RemoteDir, "zebra"));
        Directory.CreateDirectory(Path.Combine(d.RemoteDir, "Alpha"));
        Directory.CreateDirectory(Path.Combine(d.RemoteDir, "middle"));

        var listing = SftpSyncService.ListDirectories(d.Profile, null, d.Profile.RemotePath);

        Assert.Equal(["Alpha", "middle", "zebra"], listing.Directories);
        Assert.DoesNotContain(".", listing.Directories);
        Assert.DoesNotContain("..", listing.Directories);
    }

    [SkippableFact]
    public void OmitsFiles_SoTheChoiceIsntBuriedInAWebRoot()
    {
        Skip.IfNot(fx.Available, fx.Reason);
        var d = fx.NewDeployment();
        Directory.CreateDirectory(Path.Combine(d.RemoteDir, "public_html"));
        File.WriteAllText(Path.Combine(d.RemoteDir, "index.html"), "x");
        File.WriteAllText(Path.Combine(d.RemoteDir, "readme.txt"), "x");

        var listing = SftpSyncService.ListDirectories(d.Profile, null, d.Profile.RemotePath);

        Assert.Equal(["public_html"], listing.Directories);
    }

    [SkippableFact]
    public void AnEmptyPath_ResolvesToWhereverTheServerPutsUs()
    {
        Skip.IfNot(fx.Available, fx.Reason);
        var d = fx.NewDeployment();

        var listing = SftpSyncService.ListDirectories(d.Profile, null, "");

        // Whatever it resolved to, it must be a concrete path the UI can show and descend from.
        Assert.NotEmpty(listing.Path);
        Assert.StartsWith("/", listing.Path);
    }

    [SkippableFact]
    public void DescendingIntoASubdirectory_ListsItsContents()
    {
        Skip.IfNot(fx.Available, fx.Reason);
        var d = fx.NewDeployment();
        Directory.CreateDirectory(Path.Combine(d.RemoteDir, "sites", "example.com"));

        var child = SftpSyncService.ListDirectories(
            d.Profile, null, d.Profile.RemotePath + "/sites");

        Assert.Equal(["example.com"], child.Directories);
    }

    [SkippableFact]
    public void CreateRemoteDirectory_ReturnsThePathItMade_AndItIsThenListed()
    {
        Skip.IfNot(fx.Available, fx.Reason);
        var d = fx.NewDeployment();

        var created = SftpSyncService.CreateRemoteDirectory(
            d.Profile, null, d.Profile.RemotePath, "new-folder");

        Assert.EndsWith("/new-folder", created);
        Assert.True(Directory.Exists(Path.Combine(d.RemoteDir, "new-folder")));
        Assert.Contains("new-folder",
            SftpSyncService.ListDirectories(d.Profile, null, d.Profile.RemotePath).Directories);
    }

    [SkippableFact]
    public void ListingSomewhereThatDoesNotExist_Throws()
    {
        Skip.IfNot(fx.Available, fx.Reason);
        var d = fx.NewDeployment();

        Assert.ThrowsAny<System.Exception>(() =>
            SftpSyncService.ListDirectories(d.Profile, null, d.Profile.RemotePath + "/nope"));
    }
}
