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
/// A password is stored against the server account it opens, so it is shared with every other
/// target and project using that account. Editing a target must therefore leave other people's
/// secrets alone: retargeting to a different host must not carry the old host's password over it,
/// and renaming must still bring this machine's private key path along.
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
    public void RetargetingToAnotherHost_LeavesBothHostsPasswordsAlone()
    {
        var first = Open();
        first.TargetName = "production";
        first.Host = "old.invalid";
        first.Username = "deploy";
        first.Password = "s3cret";
        first.SaveCommand.Execute(null);

        var oldKey = DeployTargets.CredentialKey(_project, Config().Deploy!.Targets[0]);
        Assert.Equal("s3cret", _credentials.Get(oldKey));

        // Point the target at a different server without touching the password box.
        var second = Open();
        second.Host = "new.invalid";
        second.SaveCommand.Execute(null);

        var newKey = DeployTargets.CredentialKey(_project, Config().Deploy!.Targets[0]);
        Assert.NotEqual(oldKey, newKey);

        // old.invalid's password still belongs to old.invalid — another project may deploy there.
        Assert.Equal("s3cret", _credentials.Get(oldKey));
        // And new.invalid did not silently inherit a password that was never meant for it.
        Assert.Null(_credentials.Get(newKey));

        try { _credentials.Delete(oldKey); } catch { }
    }

    [AvaloniaFact]
    public void RetargetingOntoAHostThatAlreadyHasAPassword_DoesNotOverwriteIt()
    {
        // The sharp edge of a shared entry: someone edits a hostname, never touches the password
        // box, and saves. Writing the loaded secret back would clobber the other host's real one.
        var first = Open();
        first.TargetName = "production";
        first.Host = "a.invalid";
        first.Username = "deploy";
        first.Password = "password-for-a";
        first.SaveCommand.Execute(null);
        var keyForA = DeployTargets.CredentialKey(_project, Config().Deploy!.Targets[0]);

        var second = Open();
        second.Host = "b.invalid";
        second.Password = "password-for-b";
        second.SaveCommand.Execute(null);
        var keyForB = DeployTargets.CredentialKey(_project, Config().Deploy!.Targets[0]);

        // Back to a.invalid, changing nothing else.
        var third = Open();
        third.Host = "a.invalid";
        third.SaveCommand.Execute(null);

        Assert.Equal("password-for-a", _credentials.Get(keyForA));
        Assert.Equal("password-for-b", _credentials.Get(keyForB));

        try { _credentials.Delete(keyForA); } catch { }
        try { _credentials.Delete(keyForB); } catch { }
    }

    [AvaloniaFact]
    public void DeletingATarget_LeavesTheAccountsPasswordForWhoeverElseUsesIt()
    {
        var first = Open();
        first.TargetName = "production";
        first.Host = "shared.invalid";
        first.Username = "deploy";
        first.Password = "s3cret";
        first.SaveCommand.Execute(null);
        var key = DeployTargets.CredentialKey(_project, Config().Deploy!.Targets[0]);

        // A second, fully configured target — Save refuses to write while the selected target has
        // no host, so deleting down to a blank one would never reach the yaml.
        var second = Open();
        second.AddTargetCommand.Execute(null);
        second.TargetName = "staging";
        second.Host = "staging.invalid";
        second.Username = "deploy";
        second.SaveCommand.Execute(null);

        // AddTarget selects the target it created, and DeleteTarget deletes whatever is selected —
        // so without switching back this would delete "staging" and prove nothing about the target
        // that actually owns the password.
        var third = Open();
        third.SelectedTarget = third.Targets.Single(t => t.Name == "production");
        Assert.Equal(key, DeployTargets.CredentialKey(_project, third.SelectedTarget));

        third.DeleteTargetCommand.Execute(null);
        third.SaveCommand.Execute(null);

        Assert.DoesNotContain(Config().Deploy!.Targets, t => t.Name == "production");

        // Deleting one target must not disarm another project deploying to the same account.
        Assert.Equal("s3cret", _credentials.Get(key));

        try { _credentials.Delete(key); } catch { }
    }

    [AvaloniaFact]
    public void RetargetingAnUntouchedBox_ShowsTheNewAccountRatherThanTheOldOnesPassword()
    {
        // Finding 4: leaving the old account's password on screen meant the user closed a dialog
        // whose field was visibly filled, while nothing had been saved for the account they were
        // now pointing at — surfacing later as an opaque authentication failure.
        var first = Open();
        first.TargetName = "production";
        first.Host = "old.invalid";
        first.Username = "deploy";
        first.Password = "s3cret";
        first.SaveCommand.Execute(null);
        var oldKey = DeployTargets.CredentialKey(_project, Config().Deploy!.Targets[0])!;

        var second = Open();
        Assert.Equal("s3cret", second.Password);

        second.Host = "new.invalid";

        // new.invalid has nothing stored, and the box now says so.
        Assert.Equal(string.Empty, second.Password);

        try { _credentials.Delete(oldKey); } catch { }
    }

    [AvaloniaFact]
    public void SwitchingToKeyAuthAfterTypingAPassword_DoesNotDeleteTheKeysPassphrase()
    {
        // Finding 3: one "edited" flag for two boxes let an edit to the password authorise a write
        // — here a delete — against the passphrase's entry. The passphrase box was never loaded,
        // so it is empty, and the key it belongs to may be shared with other targets.
        var keyFile = Path.Combine(_project, "id_ed25519");
        File.WriteAllText(keyFile, "-----BEGIN RSA PRIVATE KEY-----\nnot-a-real-key\n-----END RSA PRIVATE KEY-----\n");

        var seeded = new DeployTarget
        {
            Name = "seed", Host = "somewhere.invalid", Username = "deploy", Auth = "key",
        };
        DeployLocalStore.SetPrivateKeyPath(_project, "seed", keyFile);
        var passphraseKey = DeployTargets.CredentialKey(_project, seeded)!;
        _credentials.Set(passphraseKey, "the-passphrase");

        var vm = Open();
        vm.TargetName = "production";
        vm.Host = "elsewhere.invalid";
        vm.Username = "deploy";
        vm.Password = "typed-password";     // password box edited
        vm.IsKeyAuth = true;                // ...then switch to key auth
        vm.PrivateKeyPath = keyFile;
        vm.SaveCommand.Execute(null);

        // The passphrase box was never touched, so nothing may be written to the key's entry.
        Assert.Equal("the-passphrase", _credentials.Get(passphraseKey));

        try { _credentials.Delete(passphraseKey); } catch { }
        try { _credentials.Delete(CredentialKeys.ForPassword("elsewhere.invalid", "deploy")); } catch { }
        DeployLocalStore.Remove(_project, "seed");
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
