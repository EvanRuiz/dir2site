// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.IO;
using Avalonia.Headless.XUnit;
using dir2site.Models;
using dir2site.Services;
using dir2site.SftpSync.Core;
using dir2site.ViewModels;
using Xunit;

namespace dir2site.Tests;

/// <summary>
/// Pinning only works if the accepted key is actually written down. When it isn't, the trust
/// prompt returns on every deploy — and a dialog that always appears is one people learn to click
/// through, which is worse than not having it.
/// </summary>
public class HostKeyPinPersistenceTests : IDisposable
{
    private readonly string _project = Path.Combine(
        Path.GetTempPath(), "d2s-pin-" + Guid.NewGuid().ToString("N"));

    public HostKeyPinPersistenceTests()
    {
        Directory.CreateDirectory(_project);
        File.WriteAllText(ConfigPath, "# keep me\ntitle: Test Site\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_project, recursive: true); } catch { }
    }

    private string ConfigPath => Path.Combine(_project, "dir2site.yaml");

    [AvaloniaFact]
    public void AnAcceptedKey_IsWrittenToTheProjectConfig()
    {
        var target = new DeployTarget { Name = "production", Host = "127.0.0.1", Username = "u" };
        var config = new Dir2SiteModel
        {
            Deploy = new DeployConfig { Active = "production", Targets = [target] },
        };
        var vm = new MainWindowViewModel { DirectoryRoot = _project, Dir2SiteConfig = config };

        vm.PersistAcceptedHostKey(target, "SHA256:accepted-value", ConfigPath);

        var reloaded = YamlParser.DeserializeAs<Dir2SiteModel>(File.ReadAllText(ConfigPath));
        Assert.Equal("SHA256:accepted-value", reloaded.Deploy!.Targets[0].HostKeyFingerprint);
        Assert.Contains("# keep me", File.ReadAllText(ConfigPath));
    }

    [AvaloniaFact]
    public void ReplacingAStalePin_OverwritesIt()
    {
        var target = new DeployTarget
        {
            Name = "production", Host = "127.0.0.1", Username = "u",
            HostKeyFingerprint = "SHA256:old-server",
        };
        var config = new Dir2SiteModel
        {
            Deploy = new DeployConfig { Active = "production", Targets = [target] },
        };
        var vm = new MainWindowViewModel { DirectoryRoot = _project, Dir2SiteConfig = config };

        vm.PersistAcceptedHostKey(target, "SHA256:rebuilt-server", ConfigPath);

        var reloaded = YamlParser.DeserializeAs<Dir2SiteModel>(File.ReadAllText(ConfigPath));
        Assert.Equal("SHA256:rebuilt-server", reloaded.Deploy!.Targets[0].HostKeyFingerprint);
    }
}

/// <summary>
/// Locks down the ordering that made the pin-persistence bug possible.
/// </summary>
public class HostKeyCallbackOrderingTests(SftpServerFixture fx) : IClassFixture<SftpServerFixture>
{
    private sealed class ObservingVerifier(Func<HostKeyInfo, bool> onVerify) : IHostKeyVerifier
    {
        public bool Verify(HostKeyInfo info) => onVerify(info);
    }

    [SkippableFact]
    public void TheProfileIsNotPinnedUntilVerifyHasReturned()
    {
        Skip.IfNot(fx.Available, fx.Reason);
        var d = fx.NewDeployment();
        d.Profile.HostKeyFingerprint = "";

        // PromptVerifier calls its onAccepted callback from inside Verify, before returning. This
        // is what the profile looks like at that moment — which is why such a callback must use
        // HostKeyInfo.Fingerprint and never read the profile.
        string? seenDuringVerify = null;
        HostKeyInfo? seenInfo = null;
        var verifier = new ObservingVerifier(info =>
        {
            seenDuringVerify = d.Profile.HostKeyFingerprint;
            seenInfo = info;
            return true;
        });

        SftpSyncService.TestConnection(d.Profile, null, verifier);

        Assert.Equal("", seenDuringVerify);                              // still unpinned mid-Verify
        Assert.Equal(fx.HostKeyFingerprint, seenInfo!.Fingerprint);      // but the info is correct
        Assert.Equal(fx.HostKeyFingerprint, d.Profile.HostKeyFingerprint); // pinned once it returns
    }
}
