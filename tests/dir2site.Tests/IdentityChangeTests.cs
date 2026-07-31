// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.IO;
using System.Linq;
using Avalonia.Headless.XUnit;
using dir2site.Models;
using dir2site.Services;
using dir2site.SftpSync.Core;
using dir2site.SftpSync.Core.Credentials;
using dir2site.SftpSync.Ui;
using Xunit;

namespace dir2site.Tests;

/// <summary>
/// The keychain entry is addressed by host, port and username, so editing any of them changes
/// where the secret lives. Without cleanup the old entry stays in the user's keychain — the app
/// can't reach it, they can't see it, and it still holds a working password.
/// </summary>
public class IdentityChangeTests : IDisposable
{
    private readonly string _project = Path.Combine(
        Path.GetTempPath(), "d2s-ident-" + Guid.NewGuid().ToString("N"));
    private readonly ICredentialStore _credentials = CredentialStoreFactory.Create();

    public IdentityChangeTests()
    {
        Directory.CreateDirectory(_project);
        File.WriteAllText(ConfigPath, "title: Test Site\n");
    }

    public void Dispose()
    {
        foreach (var t in Config().Deploy?.Targets ?? [])
        {
            try { _credentials.Delete(DeployTargets.CredentialKey(_project, t)); } catch { }
            DeployLocalStore.Remove(_project, t.Name);
        }
        try { Directory.Delete(_project, recursive: true); } catch { }
    }

    private string ConfigPath => Path.Combine(_project, "dir2site.yaml");

    private Dir2SiteModel Config() =>
        YamlParser.DeserializeAs<Dir2SiteModel>(File.ReadAllText(ConfigPath));

    private SftpSettingsViewModel Open()
    {
        var view = new SftpSettingsView(_project, Config(), ConfigPath);
        view.Show();
        return (SftpSettingsViewModel)view.DataContext!;
    }

    [AvaloniaFact]
    public void ChangingTheHost_MovesTheSecretInsteadOfStrandingIt()
    {
        var first = Open();
        first.TargetName = "production";
        first.Host = "old.invalid";
        first.Username = "deploy";
        first.Password = "s3cret";
        first.SaveCommand.Execute(null);

        var before = Config().Deploy!.Targets[0];
        var oldKey = DeployTargets.CredentialKey(_project, before);
        Assert.Equal("s3cret", _credentials.Get(oldKey));

        var second = Open();
        second.Host = "new.invalid";
        second.SaveCommand.Execute(null);

        var after = Config().Deploy!.Targets[0];
        var newKey = DeployTargets.CredentialKey(_project, after);
        Assert.NotEqual(oldKey, newKey);
        Assert.Equal("s3cret", _credentials.Get(newKey));   // carried over, not lost
        Assert.Null(_credentials.Get(oldKey));              // and nothing left behind
        try { _credentials.Delete(newKey); } catch { }
    }

    [AvaloniaFact]
    public void RenamingATarget_MovesItsPrivateKeyPath()
    {
        var first = Open();
        first.TargetName = "production";
        first.Host = "127.0.0.1";
        first.Username = "deploy";
        first.IsKeyAuth = true;
        first.PrivateKeyPath = "/home/me/.ssh/id_ed25519";
        first.SaveCommand.Execute(null);
        Assert.Equal("/home/me/.ssh/id_ed25519",
                     DeployLocalStore.GetPrivateKeyPath(_project, "production"));

        var second = Open();
        second.TargetName = "prod";
        second.SaveCommand.Execute(null);

        Assert.Equal("/home/me/.ssh/id_ed25519", DeployLocalStore.GetPrivateKeyPath(_project, "prod"));
        Assert.Equal("", DeployLocalStore.GetPrivateKeyPath(_project, "production"));
    }

    [AvaloniaFact]
    public void SavingWithNothingChanged_LeavesTheSecretWhereItIs()
    {
        var first = Open();
        first.TargetName = "production";
        first.Host = "127.0.0.1";
        first.Username = "deploy";
        first.Password = "s3cret";
        first.SaveCommand.Execute(null);
        var key = DeployTargets.CredentialKey(_project, Config().Deploy!.Targets[0]);

        Open().SaveCommand.Execute(null);

        Assert.Equal("s3cret", _credentials.Get(key));
    }
}
