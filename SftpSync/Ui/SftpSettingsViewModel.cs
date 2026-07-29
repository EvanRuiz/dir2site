// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using dir2site.SftpSync.Core;
using dir2site.SftpSync.Core.Credentials;
using dir2site.ViewModels;

namespace dir2site.SftpSync.Ui;

/// <summary>Backing view-model for the SFTP connection settings dialog.</summary>
public partial class SftpSettingsViewModel : ViewModelBase
{
    private readonly Window _window;
    private readonly string _projectRoot;
    private readonly ICredentialStore _credentials = CredentialStoreFactory.Create();

    public SftpSettingsViewModel(Window window, string projectRoot)
    {
        _window = window;
        _projectRoot = projectRoot;

        var profile = SftpProfileStore.Load(projectRoot) ?? new SftpProfile();
        _host = profile.Host;
        _port = profile.Port;
        _username = profile.Username;
        _remotePath = profile.RemotePath;
        _manifestPath = profile.ManifestPath;
        _privateKeyPath = profile.PrivateKeyPath;
        _isKeyAuth = profile.AuthMethod == SftpAuthMethod.Key;
        _hostKeyFingerprint = profile.HostKeyFingerprint;

        var existingSecret = _credentials.Get(SftpProfileStore.CredentialKey(projectRoot, profile));
        if (_isKeyAuth) _passphrase = existingSecret ?? string.Empty;
        else _password = existingSecret ?? string.Empty;

        if (!_credentials.IsSecure)
            _status = "Note: no OS keychain available — the secret is stored in an encrypted file.";
    }

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

    [RelayCommand]
    private void Save()
    {
        var profile = BuildProfile();
        if (string.IsNullOrWhiteSpace(profile.Host) || string.IsNullOrWhiteSpace(profile.Username))
        {
            Status = "Host and username are required.";
            return;
        }

        try
        {
            SftpProfileStore.Save(_projectRoot, profile);
            var key = SftpProfileStore.CredentialKey(_projectRoot, profile);
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
