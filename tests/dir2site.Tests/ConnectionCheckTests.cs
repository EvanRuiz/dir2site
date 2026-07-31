// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.IO;
using dir2site.SftpSync.Core;
using Xunit;

namespace dir2site.Tests;

/// <summary>
/// "Connection succeeded" used to mean only that the credentials worked, which told the user
/// nothing about a mistyped remote path — the most common way a deploy target is wrong.
/// </summary>
public class ConnectionCheckTests(SftpServerFixture fx) : IClassFixture<SftpServerFixture>
{
    [SkippableFact]
    public void ExistingWritableDirectory_ReportsWritable()
    {
        Skip.IfNot(fx.Available, fx.Reason);
        var d = fx.NewDeployment();

        var check = SftpSyncService.CheckConnection(d.Profile, null);

        Assert.Equal(RemotePathState.Writable, check.State);
        Assert.True(check.CanDeploy);
    }

    [SkippableFact]
    public void MissingDirectory_IsReportedNotThrown()
    {
        Skip.IfNot(fx.Available, fx.Reason);
        var d = fx.NewDeployment();
        d.Profile.RemotePath = d.Profile.RemotePath + "/does/not/exist";

        var check = SftpSyncService.CheckConnection(d.Profile, null);

        Assert.Equal(RemotePathState.Missing, check.State);
        Assert.False(check.CanDeploy);
        Assert.Contains("does not exist", check.Describe());
    }

    [SkippableFact]
    public void APathThatIsAFile_IsReportedAsNotADirectory()
    {
        Skip.IfNot(fx.Available, fx.Reason);
        var d = fx.NewDeployment();
        File.WriteAllText(Path.Combine(d.RemoteDir, "afile.txt"), "x");
        d.Profile.RemotePath = d.Profile.RemotePath + "/afile.txt";

        var check = SftpSyncService.CheckConnection(d.Profile, null);

        Assert.Equal(RemotePathState.NotADirectory, check.State);
    }

    [SkippableFact]
    public void CreateRemotePath_MakesMissingParentsAndThenItIsWritable()
    {
        Skip.IfNot(fx.Available, fx.Reason);
        var d = fx.NewDeployment();
        d.Profile.RemotePath = d.Profile.RemotePath + "/deep/nested/target";
        Assert.Equal(RemotePathState.Missing, SftpSyncService.CheckConnection(d.Profile, null).State);

        SftpSyncService.CreateRemotePath(d.Profile, null);

        Assert.Equal(RemotePathState.Writable, SftpSyncService.CheckConnection(d.Profile, null).State);
        Assert.True(Directory.Exists(Path.Combine(d.RemoteDir, "deep", "nested", "target")));
    }

    [SkippableFact]
    public void TheWriteProbe_LeavesNothingBehind()
    {
        Skip.IfNot(fx.Available, fx.Reason);
        var d = fx.NewDeployment();

        SftpSyncService.CheckConnection(d.Profile, null);

        Assert.Empty(Directory.GetFileSystemEntries(d.RemoteDir));
    }

    [SkippableFact]
    public void WrongCredentials_StillThrow_RatherThanReturningAState()
    {
        Skip.IfNot(fx.Available, fx.Reason);
        var d = fx.NewDeployment();
        d.Profile.PrivateKeyPath = fx.WrongKeyPath;

        Assert.ThrowsAny<System.Exception>(() => SftpSyncService.CheckConnection(d.Profile, null));
    }
}
