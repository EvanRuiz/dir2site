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
        var profile = BuildProfile();
        var secret = CurrentSecret;
        try
        {
            await Task.Run(() => SftpSyncService.TestConnection(profile, secret));
            Status = "✓ Connection succeeded.";
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
