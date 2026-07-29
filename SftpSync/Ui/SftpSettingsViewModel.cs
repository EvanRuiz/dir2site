// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using dir2site.Models;
using dir2site.Services;
using dir2site.SftpSync.Core;
using dir2site.SftpSync.Core.Credentials;
using dir2site.ViewModels;

namespace dir2site.SftpSync.Ui;

/// <summary>Backing view-model for the SFTP connection settings dialog.</summary>
public partial class SftpSettingsViewModel : ViewModelBase
{
    private readonly Window _window;
    private readonly string _projectRoot;
    private readonly string _configPath;
    private readonly DeployConfig _deploy;
    private readonly ICredentialStore _credentials = CredentialStoreFactory.Create();

    /// <summary>Every configured target, so the dialog can switch, add and delete them.</summary>
    public ObservableCollection<DeployTarget> Targets { get; } = [];

    public SftpSettingsViewModel(Window window, string projectRoot, Dir2SiteModel config, string configPath)
    {
        _window = window;
        _projectRoot = projectRoot;
        _configPath = configPath;
        _deploy = DeployTargets.Resolve(projectRoot, config);

        if (_deploy.Targets.Count == 0)
            _deploy.Targets.Add(new DeployTarget { Name = "default" });

        foreach (var t in _deploy.Targets) Targets.Add(t);
        _selectedTarget = DeployTargets.Active(_deploy) ?? _deploy.Targets[0];
        LoadFrom(_selectedTarget);

        if (!_credentials.IsSecure)
            _status = "Note: no OS keychain available — the secret is stored in an encrypted file.";
    }

    /// <summary>The target being edited. Switching writes the current edits back first.</summary>
    [ObservableProperty] private DeployTarget _selectedTarget;

    partial void OnSelectedTargetChanging(DeployTarget? oldValue, DeployTarget newValue)
    {
        // Don't silently lose what the user typed just because they changed the dropdown.
        if (oldValue != null && Targets.Contains(oldValue)) ApplyTo(oldValue);
    }

    partial void OnSelectedTargetChanged(DeployTarget value)
    {
        LoadFrom(value);
        CanCreateRemotePath = false;   // says nothing about the newly selected server
        Status = string.Empty;
    }

    private void LoadFrom(DeployTarget t)
    {
        TargetName = t.Name;
        Host = t.Host;
        Port = t.Port;
        Username = t.Username;
        RemotePath = t.RemotePath;
        ManifestPath = t.ManifestPath;
        PrivateKeyPath = DeployLocalStore.GetPrivateKeyPath(_projectRoot, t.Name);
        IsKeyAuth = t.IsKeyAuth;

        // Assign the backing field: the setter clears the pin whenever host or port changes, which
        // is right for a user edit but would wipe a stored fingerprint just for loading it.
        _hostKeyFingerprint = t.HostKeyFingerprint;
        OnPropertyChanged(nameof(HostKeyFingerprintDisplay));
        OnPropertyChanged(nameof(HasPinnedHostKey));

        var secret = _credentials.Get(DeployTargets.CredentialKey(_projectRoot, t));
        if (IsKeyAuth) { Passphrase = secret ?? string.Empty; Password = string.Empty; }
        else { Password = secret ?? string.Empty; Passphrase = string.Empty; }
    }

    private void ApplyTo(DeployTarget t)
    {
        t.Name = string.IsNullOrWhiteSpace(TargetName) ? t.Name : TargetName.Trim();
        t.Host = Host.Trim();
        t.Port = Port <= 0 ? 22 : Port;
        t.Username = Username.Trim();
        t.RemotePath = RemotePath.Trim();
        t.ManifestPath = ManifestPath.Trim();
        t.Auth = IsKeyAuth ? "key" : "password";
        t.HostKeyFingerprint = _hostKeyFingerprint;
    }

    [ObservableProperty] private string _targetName = "default";
    [ObservableProperty] private string _host = string.Empty;
    [ObservableProperty] private int _port = 22;
    [ObservableProperty] private string _username = string.Empty;
    [ObservableProperty] private string _remotePath = string.Empty;
    [ObservableProperty] private string _manifestPath = string.Empty;

    [ObservableProperty] private bool _isKeyAuth;
    [ObservableProperty] private string _password = string.Empty;
    [ObservableProperty] private string _privateKeyPath = string.Empty;
    [ObservableProperty] private string _passphrase = string.Empty;

    [ObservableProperty] private string _status = string.Empty;
    [ObservableProperty] private bool _isBusy;

    /// <summary>True when a create-it button should be offered for a missing remote path.</summary>
    [ObservableProperty] private bool _canCreateRemotePath;

    // The trusted host key travels with the profile. Pointing the profile at a different server
    // must drop it, otherwise the new host would inherit the old one's trust.
    private string _hostKeyFingerprint = string.Empty;

    public bool HasPinnedHostKey => !string.IsNullOrEmpty(_hostKeyFingerprint);

    /// <summary>The pinned fingerprint, so the user can compare it against `ssh-keygen -lf`.</summary>
    public string HostKeyFingerprintDisplay =>
        HasPinnedHostKey ? _hostKeyFingerprint : "Not yet trusted";

    partial void OnHostChanged(string value) => ResetHostKey();
    partial void OnPortChanged(int value) => ResetHostKey();

    private void ResetHostKey()
    {
        _hostKeyFingerprint = string.Empty;
        CanCreateRemotePath = false;   // a different server says nothing about the old path
        OnPropertyChanged(nameof(HostKeyFingerprintDisplay));
        OnPropertyChanged(nameof(HasPinnedHostKey));
    }

    public bool IsPasswordAuth => !IsKeyAuth;
    partial void OnIsKeyAuthChanged(bool value) => OnPropertyChanged(nameof(IsPasswordAuth));

    // Snapshot of what's on screen, for a connection test that hasn't been saved yet.
    private SftpProfile BuildProfile() => new()
    {
        Host = Host.Trim(),
        Port = Port <= 0 ? 22 : Port,
        Username = Username.Trim(),
        RemotePath = RemotePath.Trim(),
        ManifestPath = ManifestPath.Trim(),
        AuthMethod = IsKeyAuth ? SftpAuthMethod.Key : SftpAuthMethod.Password,
        PrivateKeyPath = PrivateKeyPath.Trim(),
        HostKeyFingerprint = _hostKeyFingerprint,
    };

    private string CurrentSecret => IsKeyAuth ? Passphrase : Password;

    [RelayCommand]
    private async Task BrowseKey()
    {
        var files = await _window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select SSH Private Key",
            AllowMultiple = false,
        });
        if (files.Count > 0)
            PrivateKeyPath = files[0].Path.LocalPath;
    }

    [RelayCommand]
    private async Task TestConnection()
    {
        IsBusy = true;
        Status = "Connecting…";
        CanCreateRemotePath = false;
        var profile = BuildProfile();
        var secret = CurrentSecret;
        var verifier = HostKeyPromptView.CreateVerifier(_window);
        try
        {
            var check = await Task.Run(() => SftpSyncService.CheckConnection(profile, secret, verifier));
            // The service pins the accepted key onto the profile it was handed; carry it back so
            // Save persists it and the user isn't asked again.
            _hostKeyFingerprint = profile.HostKeyFingerprint;
            OnPropertyChanged(nameof(HostKeyFingerprintDisplay));
            OnPropertyChanged(nameof(HasPinnedHostKey));

            Status = check.Describe();
            // Offering to create it is the whole point of checking — a missing path is the most
            // common way a deploy target is wrong, and it's one click to fix.
            CanCreateRemotePath = check.State == RemotePathState.Missing;
        }
        catch (Exception ex)
        {
            Status = $"✗ {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task CreateRemotePath()
    {
        IsBusy = true;
        Status = "Creating…";
        var profile = BuildProfile();
        var secret = CurrentSecret;
        var verifier = HostKeyPromptView.CreateVerifier(_window);
        try
        {
            await Task.Run(() => SftpSyncService.CreateRemotePath(profile, secret, verifier));
            var check = await Task.Run(() => SftpSyncService.CheckConnection(profile, secret, verifier));
            Status = check.Describe();
            CanCreateRemotePath = check.State == RemotePathState.Missing;
        }
        catch (Exception ex)
        {
            Status = $"✗ {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Drops the pinned host key. A server that was legitimately rebuilt otherwise leaves the user
    /// facing a "KEY CHANGED" warning with no way to say "yes, I know".
    /// </summary>
    [RelayCommand]
    private void ForgetHostKey()
    {
        _hostKeyFingerprint = string.Empty;
        OnPropertyChanged(nameof(HostKeyFingerprintDisplay));
        OnPropertyChanged(nameof(HasPinnedHostKey));
        Status = "Host key forgotten — you'll be asked to confirm it on the next connection.";
    }

    /// <summary>Adds a target and switches to it, so the user can fill it in straight away.</summary>
    [RelayCommand]
    private void AddTarget()
    {
        ApplyTo(SelectedTarget);

        var added = new DeployTarget { Name = DeployTargets.UniqueName(_deploy, "new target"), Port = 22 };
        _deploy.Targets.Add(added);
        Targets.Add(added);
        SelectedTarget = added;
    }

    /// <summary>Deletes the current target, along with its secret and its local key path.</summary>
    [RelayCommand]
    private void DeleteTarget()
    {
        if (Targets.Count <= 1)
        {
            Status = "A project needs at least one target. Edit this one instead.";
            return;
        }

        var doomed = SelectedTarget;
        // Leaving the keychain entry behind would strand a password nothing can reach or clear.
        try { _credentials.Delete(DeployTargets.CredentialKey(_projectRoot, doomed)); } catch { }
        DeployLocalStore.Remove(_projectRoot, doomed.Name);

        _deploy.Targets.Remove(doomed);
        Targets.Remove(doomed);
        SelectedTarget = Targets[0];
        Status = $"Deleted “{doomed.Name}”. Save to write the change to dir2site.yaml.";
    }

    [RelayCommand]
    private void Save()
    {
        ApplyTo(SelectedTarget);

        if (string.IsNullOrWhiteSpace(SelectedTarget.Host) ||
            string.IsNullOrWhiteSpace(SelectedTarget.Username))
        {
            Status = "Host and username are required.";
            return;
        }

        var duplicate = Targets
            .GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate != null)
        {
            Status = $"Two targets are both called “{duplicate.Key}”. Names have to be unique.";
            return;
        }

        try
        {
            _deploy.Active = SelectedTarget.Name;
            DeployTargets.Save(_configPath, _deploy);

            // The key path is per-machine, so it never goes into the project file.
            DeployLocalStore.SetPrivateKeyPath(_projectRoot, SelectedTarget.Name, PrivateKeyPath.Trim());

            var key = DeployTargets.CredentialKey(_projectRoot, SelectedTarget);
            if (string.IsNullOrEmpty(CurrentSecret))
                _credentials.Delete(key);
            else
                _credentials.Set(key, CurrentSecret);

            _window.Close(true);
        }
        catch (Exception ex)
        {
            Status = $"Could not save: {ex.Message}";
        }
    }

    [RelayCommand]
    private void Cancel() => _window.Close(false);
}
