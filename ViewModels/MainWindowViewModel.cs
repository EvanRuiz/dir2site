// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using dir2site.Models;
using dir2site.Services;
using dir2site.SftpSync.Core;
using dir2site.SftpSync.Core.Credentials;
using dir2site.SftpSync.Ui;
using Velopack;
using Velopack.Sources;

namespace dir2site.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    public TopLevel? TopLevel { get; set; }

    private readonly PreviewServerService _previewServer = new();

    private readonly UpdateManager _updateManager = new(
        new GithubSource("https://github.com/EvanRuiz/dir2site", null, false),
        new UpdateOptions { ExplicitChannel = RuntimeInformation.RuntimeIdentifier });
    private UpdateInfo? _pendingUpdate;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadUpdateCommand))]
    private bool _updateAvailable;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadUpdateCommand))]
    [NotifyCanExecuteChangedFor(nameof(RestartAndUpdateCommand))]
    private bool _updateReady;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadUpdateCommand))]
    private bool _isDownloading;

    [ObservableProperty]
    private int _updateProgress;

    [ObservableProperty]
    private string _updateVersion = string.Empty;

    public MainWindowViewModel()
    {
        _statusText = _updateManager.IsInstalled
            ? $"v{_updateManager.CurrentVersion}"
            : "Development Build";
        _ = CheckForUpdatesAsync();
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartServerCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConfigureSftpCommand))]
    [NotifyCanExecuteChangedFor(nameof(QuickSyncCommand))]
    [NotifyCanExecuteChangedFor(nameof(VerifyAndRepairCommand))]
    private string? _directoryRoot;
    
    [ObservableProperty] public partial ObservableCollection<DirectoryTreeItem> DirItems { get; set; } = [];
    
    [ObservableProperty]
    private DirectoryTreeItem? _selectedItem;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GenerateSiteCommand))]
    [NotifyCanExecuteChangedFor(nameof(QuickSyncCommand))]
    [NotifyCanExecuteChangedFor(nameof(VerifyAndRepairCommand))]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusText = "...";

    [ObservableProperty]
    private string _errorText = string.Empty;

    [ObservableProperty]
    private bool _hasErrors;

    [RelayCommand]
    private void DismissErrors()
    {
        ErrorText = string.Empty;
        HasErrors = false;
    }

    private void AppendError(string message)
    {
        ErrorText = HasErrors ? $"{ErrorText}\n{message}" : message;
        HasErrors = true;
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartServerCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopServerCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenBrowserCommand))]
    private bool _isServerRunning;

    [ObservableProperty]
    private string _serverUrl = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(QuickSyncCommand))]
    [NotifyCanExecuteChangedFor(nameof(VerifyAndRepairCommand))]
    private bool _hasSftpProfile;

    [ObservableProperty]
    private bool _forceFullReupload;

    /// <summary>Why the deploy buttons are disabled, or empty when they aren't.</summary>
    [ObservableProperty]
    private string _syncBlockedReason = string.Empty;

    /// <summary>True while a sync is running, so the UI can offer Cancel.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelSyncCommand))]
    private bool _isSyncing;

    // Non-null only for the duration of a sync. Cancel is a user action on an operation that can
    // run for minutes over a slow link, so the token has to reach the engine — which has always
    // honoured it; the UI simply never supplied one.
    private CancellationTokenSource? _syncCancellation;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GenerateSiteCommand))]
    [NotifyCanExecuteChangedFor(nameof(ChooseLogoCommand))]
    private Dir2SiteModel? _dir2SiteConfig;
    
    partial void OnDirectoryRootChanged(string? value)
    {
        if (_previewServer.IsRunning)
            _ = StopServer();
        RefreshSyncBlockedReason();
    }

    [RelayCommand(CanExecute = nameof(CanStartServer))]
    private async Task StartServer()
    {
        if (DirectoryRoot == null) return;
        var siteRoot = Path.Combine(DirectoryRoot, "_site");
        await _previewServer.StartAsync(siteRoot);
        ServerUrl = _previewServer.ServerUrl.TrimEnd('/');
        IsServerRunning = true;
        StatusText = $"Preview server at {ServerUrl}";
    }

    private bool CanStartServer() =>
        DirectoryRoot != null &&
        Directory.Exists(Path.Combine(DirectoryRoot, "_site")) &&
        !IsServerRunning;

    [RelayCommand(CanExecute = nameof(CanStopServer))]
    private async Task StopServer()
    {
        await _previewServer.StopAsync();
        IsServerRunning = false;
        ServerUrl = string.Empty;
        StatusText = "Preview server stopped";
    }

    private bool CanStopServer() => IsServerRunning;

    [RelayCommand(CanExecute = nameof(CanOpenBrowser))]
    private void OpenBrowser()
    {
        if (string.IsNullOrEmpty(ServerUrl)) return;
        Process.Start(new ProcessStartInfo(ServerUrl) { UseShellExecute = true });
    }

    private bool CanOpenBrowser() => IsServerRunning && !string.IsNullOrEmpty(ServerUrl);

    [RelayCommand(CanExecute = nameof(CanChooseLogo))]
    private async Task ChooseLogo()
    {
        if (TopLevel == null || DirectoryRoot == null || Dir2SiteConfig == null) return;

        var files = await TopLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select Logo Image",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Images") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.svg", "*.webp", "*.gif"] }]
        });

        if (files.Count == 0) return;

        var fullPath = files[0].Path.LocalPath;
        Dir2SiteConfig.Logo = Path.GetRelativePath(DirectoryRoot, fullPath);
    }

    private bool CanChooseLogo() => DirectoryRoot != null && Dir2SiteConfig != null;

    [RelayCommand]
    private async Task SelectDirectory()
    {
        if (TopLevel == null) return;

        var folders = await TopLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select the Directory Root of Your Site Content",
            AllowMultiple = false
        });

        if (folders.Count == 0) return;

        DirectoryRoot = folders[0].Path.LocalPath;
        await LoadDirectory();
    }
    
    private async Task LoadDirectory()
    {
        DirItems.Clear();
        if (DirectoryRoot == null) return;

        IsLoading = true;
        StatusText = "Scanning...";

        try
        {
            var progress = new Progress<string>(msg => StatusText = msg);

            var (root, files, artifacts) = await Task.Run(() =>
            {
                var collected = new List<string>();
                var collectedArtifacts = new List<string>();
                var tree = DirectoryTraverser.BuildTree(DirectoryRoot, collected, collectedArtifacts, progress);
                return (tree, collected, collectedArtifacts);
            });

            DirItems.Add(root);

            await LoadOrCreateDir2SiteConfig();
            HasSftpProfile = DirectoryRoot != null && SftpProfileStore.Exists(DirectoryRoot);

            IsLoading = false;
            StatusText = $"{files.Count:N0} files · {artifacts.Count:N0} artifacts";
        }
        catch (Exception ex)
        {
            IsLoading = false;
            StatusText = "Load failed";
            AppendError(ex.Message);
        }
    }

    private async Task LoadOrCreateDir2SiteConfig()
    {
        if (DirectoryRoot == null) return;
        var configPath = Path.Combine(DirectoryRoot, "dir2site.yaml");

        if (File.Exists(configPath))
        {
            var yaml = await File.ReadAllTextAsync(configPath);
            Dir2SiteConfig = YamlParser.DeserializeAs<Dir2SiteModel>(yaml) ?? new Dir2SiteModel
            {
                Title = Path.GetFileName(DirectoryRoot) is { Length: > 0 } n ? n : "My Site",
                Footer = $"© {DateTime.Now.Year}",
            };
        }
        else
        {
            Dir2SiteConfig = new Dir2SiteModel
            {
                Title = Path.GetFileName(DirectoryRoot) is { Length: > 0 } n ? n : "My Site",
                Footer = $"© {DateTime.Now.Year}",
            };
            var created = Dir2SiteConfig;
            await Task.Run(() => YamlParser.SaveDir2SiteConfig(configPath, created));
        }
    }

    [RelayCommand(CanExecute = nameof(CanGenerateSite))]
    private async Task GenerateSite()
    {
        if (DirectoryRoot == null || DirItems.Count == 0 || Dir2SiteConfig == null) return;

        // Surgical: only changed values are rewritten, so a hand-edited config keeps its comments.
        var config = Dir2SiteConfig;
        var configPath = Path.Combine(DirectoryRoot, "dir2site.yaml");
        await Task.Run(() => YamlParser.SaveDir2SiteConfig(configPath, config));

        IsLoading = true;
        var progress = new Progress<string>(msg => StatusText = msg);

        // Re-scan from disk so any YAML edits since last load are picked up
        StatusText = "Scanning for changes...";
        var freshRoot = await Task.Run(() =>
        {
            var files     = new List<string>();
            var artifacts = new List<string>();
            return DirectoryTraverser.BuildTree(DirectoryRoot!, files, artifacts, progress);
        });

        // Generate previews first so site settings (PDF resize/quality) affect output
        StatusText = "Generating previews...";
        var root = freshRoot;
        await Task.Run(() => DirectoryTraverser.GeneratePreviews(root, config, progress));

        StatusText = "Generating site...";
        var result = await Task.Run(() =>
            SiteGenerator.Generate(DirectoryRoot, root, config, progress));

        IsLoading = false;
        StatusText = result.Summary;
        if (result.Errors.Count > 0)
            AppendError(string.Join("\n", result.Errors));
        StartServerCommand.NotifyCanExecuteChanged();
        QuickSyncCommand.NotifyCanExecuteChanged();
        VerifyAndRepairCommand.NotifyCanExecuteChanged();
    }

    private bool CanGenerateSite() =>
        DirectoryRoot != null && DirItems.Count > 0 && Dir2SiteConfig != null && !IsLoading;

    // ---- SFTP deploy --------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanConfigureSftp))]
    private async Task ConfigureSftp()
    {
        if (DirectoryRoot == null || TopLevel is not Window owner) return;
        var dialog = new SftpSettingsView(DirectoryRoot);
        var saved = await dialog.ShowDialog<bool>(owner);
        if (saved)
        {
            HasSftpProfile = true;
            StatusText = "SFTP settings saved";
        }
    }

    private bool CanConfigureSftp() => DirectoryRoot != null;

    [RelayCommand(CanExecute = nameof(CanSync))]
    private Task QuickSync() => RunSync(verify: false, force: ForceFullReupload);

    [RelayCommand(CanExecute = nameof(CanSync))]
    private Task VerifyAndRepair() => RunSync(verify: true, force: false);

    [RelayCommand(CanExecute = nameof(CanCancelSync))]
    private void CancelSync()
    {
        StatusText = "Cancelling…";
        _syncCancellation?.Cancel();
    }

    private bool CanCancelSync() => IsSyncing;

    private bool CanSync() => BlockedReason() == null;

    /// <summary>
    /// The single source of truth for both whether a sync can start and what to tell the user when
    /// it can't — a disabled button with no explanation is its own small bug.
    /// </summary>
    private string? BlockedReason()
    {
        if (DirectoryRoot == null) return "Choose a project folder first.";
        if (!Directory.Exists(Path.Combine(DirectoryRoot, "_site")))
            return "Generate the site first — there is no _site folder to deploy.";
        if (!HasSftpProfile) return "No SFTP profile configured. Use Configure… first.";
        if (IsLoading) return "Busy.";
        return null;
    }

    // CanExecute is re-evaluated by the toolkit on the properties above; keep the message in step.
    private void RefreshSyncBlockedReason()
    {
        var reason = BlockedReason();
        SyncBlockedReason = reason is null or "Busy." ? string.Empty : reason;
    }

    partial void OnHasSftpProfileChanged(bool value) => RefreshSyncBlockedReason();
    partial void OnIsLoadingChanged(bool value) => RefreshSyncBlockedReason();

    private async Task RunSync(bool verify, bool force)
    {
        if (DirectoryRoot == null) return;

        var profile = SftpProfileStore.Load(DirectoryRoot);
        if (profile == null)
        {
            AppendError("No SFTP profile configured. Use Configure… first.");
            return;
        }

        var siteRoot = Path.Combine(DirectoryRoot, "_site");
        var secret = CredentialStoreFactory.Create()
            .Get(SftpProfileStore.CredentialKey(DirectoryRoot, profile));
        var verifier = CreateHostKeyVerifier(profile);

        IsLoading = true;
        IsSyncing = true;
        _syncCancellation = new CancellationTokenSource();
        var token = _syncCancellation.Token;
        var progress = new Progress<string>(msg => StatusText = msg);

        try
        {
            var result = await Task.Run(() => verify
                ? SftpSyncService.VerifyAndRepair(siteRoot, profile, secret, progress, token, verifier)
                : SftpSyncService.QuickSync(siteRoot, profile, secret, force, progress, token, verifier));

            StatusText = result.Summary;
            if (result.Errors.Count > 0)
                AppendError(string.Join("\n", result.Errors));

            if (result.StaleRemote.Count > 0)
                await HandleStaleFiles(profile, secret, siteRoot, result.StaleRemote);
        }
        catch (OperationCanceledException)
        {
            // The user asked for this, so it isn't an error. Files already uploaded stay put; the
            // next Quick Sync picks up where this left off.
            StatusText = "Sync cancelled";
        }
        catch (Exception ex)
        {
            StatusText = "Sync failed";
            AppendError(ex.Message);
        }
        finally
        {
            _syncCancellation?.Dispose();
            _syncCancellation = null;
            IsSyncing = false;
            IsLoading = false;
        }
    }

    private async Task HandleStaleFiles(
        SftpProfile profile, string? secret, string siteRoot, IReadOnlyList<string> stale)
    {
        if (TopLevel is not Window owner) return;

        var dialog = new StaleFilesView(stale);
        var toDelete = await dialog.ShowDialog<IReadOnlyList<string>?>(owner);
        if (toDelete == null || toDelete.Count == 0) return;

        var verifier = CreateHostKeyVerifier(profile);

        IsLoading = true;
        IsSyncing = true;
        _syncCancellation = new CancellationTokenSource();
        var token = _syncCancellation.Token;
        var progress = new Progress<string>(msg => StatusText = msg);
        try
        {
            var result = await Task.Run(() =>
                SftpSyncService.DeleteRemote(siteRoot, profile, secret, toDelete, progress, token, verifier));
            StatusText = result.Summary;
            if (result.Errors.Count > 0)
                AppendError(string.Join("\n", result.Errors));
        }
        catch (OperationCanceledException)
        {
            StatusText = "Delete cancelled";
        }
        catch (Exception ex)
        {
            StatusText = "Delete failed";
            AppendError(ex.Message);
        }
        finally
        {
            _syncCancellation?.Dispose();
            _syncCancellation = null;
            IsSyncing = false;
            IsLoading = false;
        }
    }

    // Prompts on the main window for an unknown/changed host key. SftpSyncService pins the
    // accepted fingerprint onto the in-memory profile; persisting it here is what stops the
    // prompt reappearing on every sync.
    private IHostKeyVerifier? CreateHostKeyVerifier(SftpProfile profile)
    {
        if (TopLevel is not Window owner) return null;
        return HostKeyPromptView.CreateVerifier(owner, _ =>
        {
            if (DirectoryRoot != null)
                SftpProfileStore.Save(DirectoryRoot, profile);
        });
    }

    public async Task CheckForUpdatesAsync()
    {
        try
        {
            _pendingUpdate = await _updateManager.CheckForUpdatesAsync();
            if (_pendingUpdate != null)
            {
                UpdateVersion = _pendingUpdate.TargetFullRelease.Version.ToString();
                UpdateAvailable = true;
            }
        }
        catch
        {
            // silently ignore — no network, no GitHub release, dev environment, etc.
        }
    }

    [RelayCommand(CanExecute = nameof(CanDownloadUpdate))]
    private async Task DownloadUpdate()
    {
        if (_pendingUpdate == null) return;
        IsDownloading = true;
        try
        {
            await _updateManager.DownloadUpdatesAsync(_pendingUpdate, p => UpdateProgress = p);
            UpdateAvailable = false;
            UpdateReady = true;
        }
        finally
        {
            IsDownloading = false;
        }
    }

    private bool CanDownloadUpdate() => UpdateAvailable && !UpdateReady && !IsDownloading;

    [RelayCommand(CanExecute = nameof(CanRestartAndUpdate))]
    private void RestartAndUpdate()
    {
        if (_pendingUpdate == null) return;
        _updateManager.ApplyUpdatesAndRestart(_pendingUpdate);
    }

    private bool CanRestartAndUpdate() => UpdateReady;
}