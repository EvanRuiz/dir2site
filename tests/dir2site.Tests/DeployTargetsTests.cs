// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.IO;
using dir2site.Models;
using dir2site.Services;
using dir2site.SftpSync.Core;
using Xunit;

namespace dir2site.Tests;

public class DeployTargetsTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "d2s-deploy-" + Guid.NewGuid().ToString("N"));

    public DeployTargetsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string ConfigPath => Path.Combine(_dir, "dir2site.yaml");

    private static DeployConfig TwoTargets() => new()
    {
        Active = "production",
        Targets =
        [
            new DeployTarget
            {
                Name = "production", Host = "127.0.0.1", Port = 22, Username = "deploy",
                RemotePath = "/var/www/html", Auth = "key", HostKeyFingerprint = "SHA256:abc",
            },
            new DeployTarget
            {
                Name = "staging", Host = "127.0.0.1", Port = 2222, Username = "stage",
                RemotePath = "/srv/staging", Auth = "password",
            },
        ],
    };

    [Fact]
    public void SavingTargets_LeavesTheRestOfTheConfigAlone()
    {
        File.WriteAllText(ConfigPath,
            """
            # my notes survive this
            title: My Site
            footer: © 2026
            """);

        DeployTargets.Save(ConfigPath, TwoTargets());

        var text = File.ReadAllText(ConfigPath);
        Assert.Contains("# my notes survive this", text);
        Assert.Contains("title: My Site", text);

        var reloaded = YamlParser.DeserializeAs<Dir2SiteModel>(text);
        Assert.Equal("My Site", reloaded.Title);
        Assert.Equal(2, reloaded.Deploy!.Targets.Count);
        Assert.Equal("production", reloaded.Deploy.Active);
        Assert.Equal("/srv/staging", reloaded.Deploy.Targets[1].RemotePath);
        Assert.Equal(2222, reloaded.Deploy.Targets[1].Port);
    }

    [Fact]
    public void RewritingTargets_ReplacesTheBlockRatherThanAppendingASecondOne()
    {
        File.WriteAllText(ConfigPath, "title: My Site\n");
        DeployTargets.Save(ConfigPath, TwoTargets());

        var oneLeft = TwoTargets();
        oneLeft.Targets.RemoveAt(1);
        DeployTargets.Save(ConfigPath, oneLeft);

        var text = File.ReadAllText(ConfigPath);
        Assert.Equal(1, text.Split("deploy:").Length - 1);
        Assert.DoesNotContain("staging", text);
        Assert.Single(YamlParser.DeserializeAs<Dir2SiteModel>(text).Deploy!.Targets);
    }

    [Fact]
    public void RemovingEveryTarget_DropsTheDeployBlockEntirely()
    {
        File.WriteAllText(ConfigPath, "title: My Site\n");
        DeployTargets.Save(ConfigPath, TwoTargets());

        DeployTargets.Save(ConfigPath, new DeployConfig());

        var text = File.ReadAllText(ConfigPath);
        Assert.DoesNotContain("deploy:", text);
        Assert.Contains("title: My Site", text);
    }

    [Fact]
    public void ActivePicksTheNamedTarget_AndFallsBackToTheFirst()
    {
        var deploy = TwoTargets();
        Assert.Equal("production", DeployTargets.Active(deploy)!.Name);

        deploy.Active = "staging";
        Assert.Equal("staging", DeployTargets.Active(deploy)!.Name);

        deploy.Active = "a name nobody configured";
        Assert.Equal("production", DeployTargets.Active(deploy)!.Name);

        Assert.Null(DeployTargets.Active(new DeployConfig()));
    }

    [Fact]
    public void PrivateKeyPath_ComesFromTheMachine_NotTheProjectFile()
    {
        var deploy = TwoTargets();
        DeployTargets.Save(ConfigPath, deploy);

        // The path names a file on one computer, so it must not travel in the committed config.
        Assert.DoesNotContain("privateKeyPath", File.ReadAllText(ConfigPath), StringComparison.OrdinalIgnoreCase);

        DeployLocalStore.SetPrivateKeyPath(_dir, "production", "/home/me/.ssh/id_ed25519");
        try
        {
            var profile = DeployTargets.ToProfile(_dir, deploy.Targets[0]);
            Assert.Equal("/home/me/.ssh/id_ed25519", profile.PrivateKeyPath);
            Assert.Equal(SftpAuthMethod.Key, profile.AuthMethod);

            // A target with nothing recorded locally simply has no key path.
            Assert.Equal("", DeployTargets.ToProfile(_dir, deploy.Targets[1]).PrivateKeyPath);
        }
        finally
        {
            DeployLocalStore.Remove(_dir, "production");
        }
    }

    [Fact]
    public void ALegacyProfile_IsImportedSoNobodyReconfiguresAWorkingDeployment()
    {
        var legacy = new SftpProfile
        {
            Host = "127.0.0.1", Port = 2022, Username = "old", RemotePath = "/var/www",
            AuthMethod = SftpAuthMethod.Key, PrivateKeyPath = "/home/me/.ssh/legacy",
            HostKeyFingerprint = "SHA256:legacy",
        };
        SftpProfileStore.Save(_dir, legacy);
        try
        {
            var config = new Dir2SiteModel();

            var deploy = DeployTargets.Resolve(_dir, config);

            var target = Assert.Single(deploy.Targets);
            Assert.Equal("default", target.Name);
            Assert.Equal(2022, target.Port);
            Assert.Equal("SHA256:legacy", target.HostKeyFingerprint);
            Assert.Equal("key", target.Auth);
            // The machine-specific part goes to the machine-local store, not the yaml.
            Assert.Equal("/home/me/.ssh/legacy", DeployLocalStore.GetPrivateKeyPath(_dir, "default"));
            // And the original file is left alone as a fallback.
            Assert.True(SftpProfileStore.Exists(_dir));
        }
        finally
        {
            DeployLocalStore.Remove(_dir, "default");
            try { File.Delete(Path.Combine(SftpProfileStore.ProfilesDir, SftpProfileStore.ProjectKey(_dir) + ".json")); } catch { }
        }
    }

    [Fact]
    public void ExistingYamlTargets_WinOverALegacyProfile()
    {
        SftpProfileStore.Save(_dir, new SftpProfile { Host = "legacy.invalid", Username = "old" });
        try
        {
            var config = new Dir2SiteModel { Deploy = TwoTargets() };

            var deploy = DeployTargets.Resolve(_dir, config);

            Assert.Equal(2, deploy.Targets.Count);
            Assert.DoesNotContain(deploy.Targets, t => t.Host == "legacy.invalid");
        }
        finally
        {
            try { File.Delete(Path.Combine(SftpProfileStore.ProfilesDir, SftpProfileStore.ProjectKey(_dir) + ".json")); } catch { }
        }
    }

    [Fact]
    public void UniqueName_AvoidsCollisions()
    {
        var deploy = TwoTargets();
        Assert.Equal("new target", DeployTargets.UniqueName(deploy, "new target"));
        Assert.Equal("production 2", DeployTargets.UniqueName(deploy, "production"));
    }

    [Fact]
    public void CredentialKey_DiffersBetweenTargets()
    {
        var deploy = TwoTargets();
        Assert.NotEqual(
            DeployTargets.CredentialKey(_dir, deploy.Targets[0]),
            DeployTargets.CredentialKey(_dir, deploy.Targets[1]));
    }
}
