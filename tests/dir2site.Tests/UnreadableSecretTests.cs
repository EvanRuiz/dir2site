// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using dir2site.Models;
using dir2site.SftpSync.Core.Credentials;
using dir2site.SftpSync.Ui;
using Xunit;

namespace dir2site.Tests;

/// <summary>
/// A stored secret that can't be read must never be treated as an absent one. This came from an
/// install that lost its password: the user removed their Windows account password, which destroys
/// the DPAPI master key and orphans every saved secret. The store returned null rather than
/// reporting the failure, the dialog showed an empty password box, and saving from that box
/// deleted the entry — so a recoverable-looking situation became a permanent one.
/// </summary>
public class UnreadableSecretTests : IDisposable
{
    private readonly string _project = Path.Combine(
        Path.GetTempPath(), "d2s-unreadable-" + Guid.NewGuid().ToString("N"));

    public UnreadableSecretTests() => Directory.CreateDirectory(_project);

    public void Dispose()
    {
        try { Directory.Delete(_project, recursive: true); } catch { }
    }

    /// <summary>Records what was asked of it, and can be told how a read should turn out.</summary>
    private sealed class StubStore : ICredentialStore
    {
        public CredentialResult Result = CredentialResult.NotFound;
        public List<string> Deleted { get; } = [];
        public List<(string Key, string Secret)> Written { get; } = [];
        public int Reads { get; private set; }

        public bool IsSecure => true;
        public CredentialResult Read(string key) { Reads++; return Result; }
        public string? Get(string key) => Read(key).Secret;
        public void Set(string key, string secret) => Written.Add((key, secret));
        public void Delete(string key) => Deleted.Add(key);
    }

    private (SftpSettingsView view, SftpSettingsViewModel vm) Show()
    {
        var view = new SftpSettingsView(
            _project, new Dir2SiteModel(), Path.Combine(_project, "dir2site.yaml"));
        view.Show();
        Dispatcher.UIThread.RunJobs();
        return (view, (SftpSettingsViewModel)view.DataContext!);
    }

    [AvaloniaFact]
    public void AnUnreadableSecret_IsReportedInsteadOfShowingAnEmptyBox()
    {
        var stub = new StubStore { Result = CredentialResult.Failed("Windows can no longer decrypt this.") };
        using var _ = CredentialStoreFactory.UseForTesting(stub);

        var (_, vm) = Show();

        Assert.Equal(string.Empty, vm.Password);
        Assert.Contains("no longer decrypt", vm.Status);
    }

    [AvaloniaFact]
    public void SavingWithAnEmptyBox_DoesNotDeleteASecretWeCouldNotRead()
    {
        var stub = new StubStore { Result = CredentialResult.Failed("unreadable") };
        using var _ = CredentialStoreFactory.UseForTesting(stub);

        var (_, vm) = Show();
        vm.Host = "example.invalid";
        vm.Username = "deploy";
        vm.RemotePath = "/var/www";

        // The box is empty because the read failed, not because the user cleared it.
        Assert.Equal(string.Empty, vm.Password);

        vm.SaveCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        // "Nothing was deleted" is also trivially true of a stub nothing ever reached, so pin that
        // the dialog really went through this store before trusting the assertion below.
        Assert.True(stub.Reads > 0);
        Assert.Empty(stub.Deleted);
    }

    [AvaloniaFact]
    public void SavingWithAnEmptyBox_StillClearsASecretTheUserActuallyRemoved()
    {
        // The other side of the same branch: when the read succeeded, an empty box does mean
        // "forget it", and that has to keep working.
        var stub = new StubStore { Result = CredentialResult.Found("hunter2") };
        using var _ = CredentialStoreFactory.UseForTesting(stub);

        var (_, vm) = Show();
        vm.Host = "example.invalid";
        vm.Username = "deploy";
        vm.RemotePath = "/var/www";

        Assert.Equal("hunter2", vm.Password);
        vm.Password = string.Empty;

        vm.SaveCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.NotEmpty(stub.Deleted);
    }

    [AvaloniaFact]
    public void SavingATypedSecret_WritesItEvenAfterAFailedRead()
    {
        // The recovery path a user is told to take: retype the password and save.
        var stub = new StubStore { Result = CredentialResult.Failed("unreadable") };
        using var _ = CredentialStoreFactory.UseForTesting(stub);

        var (_, vm) = Show();
        vm.Host = "example.invalid";
        vm.Username = "deploy";
        vm.RemotePath = "/var/www";
        vm.Password = "retyped-by-hand";

        vm.SaveCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(stub.Written, w => w.Secret == "retyped-by-hand");
        Assert.Empty(stub.Deleted);
    }
}
