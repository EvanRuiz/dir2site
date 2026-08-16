// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Generic;
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

        foreach (var t in _deploy.Targets)
        {
            Targets.Add(t);
            RememberIdentity(t);
        }

        _selectedTarget = DeployTargets.Active(_deploy) ?? _deploy.Targets[0];
        LoadFrom(_selectedTarget);

        // LoadFrom may already have reported an unreadable secret, which is the more urgent of the
        // two — don't overwrite it with the standing note about the fallback store.
        if (!_credentials.IsSecure && string.IsNullOrEmpty(_status))
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
        // Clearing unconditionally would wipe the warning LoadFrom just raised about an
        // unreadable secret — the one thing on this dialog the user has to act on.
        if (!_secretReadFailed) Status = string.Empty;
    }

    // Set while LoadFrom is filling the form. Each assignment below raises its own change
    // notification, and Host, Username and the key path each ask to reload the secret — so without
    // this a single dropdown switch reads the store several times, once against a half-updated form
    // that briefly puts the wrong account's password on screen.
    private bool _loadingForm;

    private void LoadFrom(DeployTarget t)
    {
        _loadingForm = true;
        try { FillForm(t); } finally { _loadingForm = false; }

        LoadSecretForTheFormsAccount();
    }

    private void FillForm(DeployTarget t)
    {
        TargetName = t.Name;
        Host = t.Host;
        Port = t.Port;
        Username = t.Username;
        RemotePath = t.RemotePath;
        ManifestPath = t.ManifestPath;
        PrivateKeyPath = DeployLocalStore.GetPrivateKeyPath(_projectRoot, t.Name);
        IsKeyAuth = t.IsKeyAuth;
        UploadConcurrency = t.UploadConcurrency;

        // Assign the backing field: the setter clears the pin whenever host or port changes, which
        // is right for a user edit but would wipe a stored fingerprint just for loading it.
        _hostKeyFingerprint = t.HostKeyFingerprint;
        OnPropertyChanged(nameof(HostKeyFingerprintDisplay));
        OnPropertyChanged(nameof(HasPinnedHostKey));
    }

    /// <summary>
    /// Fills the secret box from whatever is stored for the account — or key file — the form now
    /// describes, and reports when that could not be read.
    /// </summary>
    private void LoadSecretForTheFormsAccount()
    {
        var profile = BuildProfile();

        // Read, not Get: an unreadable secret must not look like an absent one, or Save below
        // deletes it on the user's behalf.
        var result = TargetSecret.Read(_credentials, _projectRoot, profile);
        _secretReadFailed = result.Status == CredentialStatus.Failed;
        if (_secretReadFailed) Status = result.Error ?? "Could not read the saved secret.";

        var secret = result.Secret;
        if (IsKeyAuth) { Passphrase = secret ?? string.Empty; Password = string.Empty; }
        else { Password = secret ?? string.Empty; Passphrase = string.Empty; }

        // Loading is not the user typing. Cleared last so the assignments above don't set them.
        _passwordEdited = false;
        _passphraseEdited = false;
        _secretLoadedFor = (profile.Host, profile.Username, profile.PrivateKeyPath, profile.AuthMethod);
    }

    // Whether the user has touched each secret box since it was loaded. A secret now lives with the
    // thing it belongs to rather than with this project's target, so it is shared with other targets
    // and other projects — writing it back on every Save would let someone who merely retyped a
    // hostname overwrite a password they never looked at.
    //
    // Two flags, not one: Save writes to whichever key the current auth method selects, so a flag
    // shared between the boxes would let an edit to the password authorise a write — or a delete —
    // against the passphrase's key, and the other way round.
    private bool _passwordEdited;
    private bool _passphraseEdited;

    /// <summary>Whether the box feeding <see cref="CurrentSecret"/> is the one the user edited.</summary>
    private bool CurrentSecretEdited => IsKeyAuth ? _passphraseEdited : _passwordEdited;

    partial void OnPasswordChanged(string value) => _passwordEdited = true;
    partial void OnPassphraseChanged(string value) => _passphraseEdited = true;

    // What the box on screen was filled for. When the form moves to a different account or key
    // file, an untouched box is still showing the previous one's secret: leaving it would offer to
    // save one server's password against another, and would show a populated field for an account
    // that has nothing stored — which then saves nothing and fails at deploy time with an opaque
    // authentication error.
    private (string Host, string Username, string KeyPath, SftpAuthMethod Auth) _secretLoadedFor;

    private void ReloadSecretIfAccountChanged()
    {
        if (_loadingForm) return;

        var profile = BuildProfile();
        var now = (profile.Host, profile.Username, profile.PrivateKeyPath, profile.AuthMethod);
        if (now == _secretLoadedFor) return;

        // Never discard what the user typed — they presumably mean it for wherever they are
        // pointing now. Only an untouched box gets refilled.
        if (CurrentSecretEdited) { _secretLoadedFor = now; return; }

        LoadSecretForTheFormsAccount();
    }

    // True when the selected target has a stored secret we could not read. Set per target by
    // LoadFrom, so switching the dropdown re-evaluates it.
    private bool _secretReadFailed;

    // What each target was called when the dialog opened, so a rename can carry its private key
    // path — which is stored per machine, under the target's name — across with it.
    //
    // Secrets used to be reconciled here as well, because the credential key included the project
    // path, the port and the target's identity, so editing any of them stranded the secret under a
    // key nothing could reach again. A secret is now stored against the thing it actually belongs
    // to — a password with the server account, a passphrase with the key pair — which no longer
    // moves when a target is edited. Carrying one across would in fact be wrong now that entries
    // are shared: copying a password from the old host to the new one would overwrite whatever the
    // new host's real password was.
    private readonly Dictionary<DeployTarget, string> _nameOnOpen = new();

    private void RememberIdentity(DeployTarget t) => _nameOnOpen[t] = t.Name;

    /// <summary>Carries a target's per-machine private key path across a rename.</summary>
    private void ReconcileIdentity(DeployTarget t)
    {
        if (!_nameOnOpen.TryGetValue(t, out var wasName)) { RememberIdentity(t); return; }

        if (!string.Equals(wasName, t.Name, StringComparison.Ordinal))
        {
            var keyPath = DeployLocalStore.GetPrivateKeyPath(_projectRoot, wasName);
            if (!string.IsNullOrEmpty(keyPath))
                DeployLocalStore.SetPrivateKeyPath(_projectRoot, t.Name, keyPath);
            DeployLocalStore.Remove(_projectRoot, wasName);
        }

        RememberIdentity(t);
    }

    private void ApplyTo(DeployTarget t)
    {
        t.Name = string.IsNullOrWhiteSpace(TargetName) ? t.Name : TargetName.Trim();
        t.Host = Host.Trim();
        t.Port = Port <= 0 ? 22 : Port;
        t.UploadConcurrency = ClampConcurrency(UploadConcurrency);
        t.Username = Username.Trim();
        t.RemotePath = RemotePath.Trim();
        t.ManifestPath = ManifestPath.Trim();
        t.Auth = IsKeyAuth ? "key" : "password";
        t.HostKeyFingerprint = _hostKeyFingerprint;
    }

    [ObservableProperty] private string _targetName = "default";
    [ObservableProperty] private string _host = string.Empty;
    [ObservableProperty] private int _port = 22;
    [ObservableProperty] private int _uploadConcurrency = SftpProfile.DefaultUploadConcurrency;
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

    partial void OnHostChanged(string value)
    {
        ResetHostKey();
        ReloadSecretIfAccountChanged();
    }

    partial void OnPortChanged(int value) => ResetHostKey();

    // The account the password belongs to, and the key file the passphrase belongs to.
    partial void OnUsernameChanged(string value) => ReloadSecretIfAccountChanged();
    partial void OnPrivateKeyPathChanged(string value) => ReloadSecretIfAccountChanged();

    private void ResetHostKey()
    {
        _hostKeyFingerprint = string.Empty;
        CanCreateRemotePath = false;   // a different server says nothing about the old path
        OnPropertyChanged(nameof(HostKeyFingerprintDisplay));
        OnPropertyChanged(nameof(HasPinnedHostKey));
    }

    public bool IsPasswordAuth => !IsKeyAuth;

    partial void OnIsKeyAuthChanged(bool value)
    {
        OnPropertyChanged(nameof(IsPasswordAuth));
        // Switching auth mode switches which secret — and so which entry — is in play.
        ReloadSecretIfAccountChanged();
    }

    // Snapshot of what's on screen, for a connection test that hasn't been saved yet.
    private SftpProfile BuildProfile() => new()
    {
        Host = Host.Trim(),
        Port = Port <= 0 ? 22 : Port,
        UploadConcurrency = ClampConcurrency(UploadConcurrency),
        Username = Username.Trim(),
        RemotePath = RemotePath.Trim(),
        ManifestPath = ManifestPath.Trim(),
        AuthMethod = IsKeyAuth ? SftpAuthMethod.Key : SftpAuthMethod.Password,
        PrivateKeyPath = PrivateKeyPath.Trim(),
        HostKeyFingerprint = _hostKeyFingerprint,
    };

    /// <summary>
    /// Keeps a hand-edited or half-typed value in range. The spinner already bounds it, but the
    /// same value arrives from YAML, which nothing bounds.
    /// </summary>
    private static int ClampConcurrency(int value) =>
        value <= 0 ? SftpProfile.DefaultUploadConcurrency
        : value > SftpProfile.MaxUploadConcurrency ? SftpProfile.MaxUploadConcurrency
        : value;

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

    /// <summary>Server rules for keeping the deploy manifest unreadable over HTTP.</summary>
    [RelayCommand]
    private async Task ManifestPrivacy() =>
        await new ManifestPrivacyView().ShowDialog(_window);

    /// <summary>
    /// Opens the server so a deploy folder can be picked by looking rather than typed from memory.
    /// </summary>
    [RelayCommand]
    private async Task BrowseRemote()
    {
        if (string.IsNullOrWhiteSpace(Host) || string.IsNullOrWhiteSpace(Username))
        {
            Status = "Enter a host and username first — browsing needs to connect.";
            return;
        }

        var dialog = new RemoteBrowseView(
            BuildProfile(), CurrentSecret, HostKeyPromptView.CreateVerifier(_window));
        var chosen = await dialog.ShowDialog<string?>(_window);
        if (chosen == null) return;

        RemotePath = chosen;
        CanCreateRemotePath = false;   // it was picked from the server, so it exists
        Status = $"Remote path set to {chosen}";
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
        RememberIdentity(added);
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

        // The secret deliberately outlives the target. It belongs to the server account, not to
        // this target, so another target or another project may still be using it — and deleting
        // it here would break their deploy. It is not stranded either: adding a target for the
        // same host and username finds it again, which is what made deleting it right back when
        // the key was a per-project hash nothing else could reach.
        DeployLocalStore.Remove(_projectRoot, doomed.Name);

        _deploy.Targets.Remove(doomed);
        Targets.Remove(doomed);
        _nameOnOpen.Remove(doomed);
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
            foreach (var t in Targets) ReconcileIdentity(t);

            _deploy.Active = SelectedTarget.Name;
            DeployTargets.Save(_configPath, _deploy);

            // The key path is per-machine, so it never goes into the project file.
            DeployLocalStore.SetPrivateKeyPath(_projectRoot, SelectedTarget.Name, PrivateKeyPath.Trim());

            // Only write the box the user actually typed in, and only to the key that box feeds.
            // An untouched box holds whatever was loaded for a previous account, and entries are
            // shared with anything else using the same account — so writing it back after a
            // hostname edit would overwrite the real password of the server just pointed at.
            if (CurrentSecretEdited &&
                CredentialKeys.For(DeployTargets.ToProfile(_projectRoot, SelectedTarget)) is { } key)
            {
                // Record an emptied box as an empty secret rather than as no secret. "Nothing
                // stored" is what migration keys on, so deleting the entry reads as never having
                // had one, and the next open copies the old value back out of the legacy key —
                // undoing the clearing entirely on an upgraded install. Storing the blank keeps
                // "the answer is empty" distinct from "there is no answer yet", leaves the legacy
                // copy alone for a downgrade, and needs nothing remembered.
                //
                // Blank and absent are already indistinguishable at the wire: a password is sent as
                // secret ?? "", and an empty passphrase takes the unencrypted-key branch, which is
                // the correct answer for a key that has none.
                //
                // Guarded either way: if the read failed, the box is empty because we couldn't fill
                // it, and writing over it would destroy a secret that was merely unreadable.
                var secret = string.IsNullOrEmpty(CurrentSecret) ? string.Empty : CurrentSecret;
                if (secret.Length > 0 || !_secretReadFailed)
                    _credentials.Set(key, secret);
            }

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
