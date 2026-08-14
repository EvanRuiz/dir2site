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
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using dir2site.Models;
using dir2site.Services;
using dir2site.SftpSync.Core;
using dir2site.SftpSync.Core.Credentials;
using dir2site.SftpSync.Ui;
using dir2site.Views;
using Velopack;
using Velopack.Sources;

namespace dir2site.ViewModels;

/// <summary>
/// How an update check went. "Nothing to install" has three quite different causes — already
/// current, uninstallable dev build, and couldn't reach GitHub — and a user who pressed a button
/// deserves to be told which.
/// </summary>
public enum UpdateCheckResult { Available, UpToDate, NotSupported, Failed }

public partial class MainWindowViewModel : ViewModelBase
{
    public TopLevel? TopLevel { get; set; }

    private readonly PreviewServerService _previewServer = new();

    // Null when Velopack isn't initialised — a test host, or anything that didn't run
    // VelopackApp.Build(). Auto-update is then simply unavailable, rather than the whole view
    // model being impossible to construct.
    private readonly UpdateManager? _updateManager = TryCreateUpdateManager();
    private UpdateInfo? _pendingUpdate;

    private static UpdateManager? TryCreateUpdateManager()
    {
        try
        {
            return new UpdateManager(
                new GithubSource("https://github.com/EvanRuiz/dir2site", null, false),
                new UpdateOptions { ExplicitChannel = RuntimeInformation.RuntimeIdentifier });
        }
        catch
        {
            return null;
        }
    }

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
        _statusText = _updateManager is { IsInstalled: true }
            ? $"v{_updateManager.CurrentVersion}"
            : "Development Build";
        // The property-changed handlers only fire on change, so without this the very first state
        // — no project open — would show disabled buttons and no explanation.
        RefreshSyncBlockedReason();
        _ = CheckForUpdatesAsync();
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartServerCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConfigureSftpCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConfigureFooterCommand))]
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

    [ObservableProperty]
    private string _warningText = string.Empty;

    [ObservableProperty]
    private bool _hasWarnings;

    [RelayCommand]
    private void DismissErrors()
    {
        ErrorText = string.Empty;
        HasErrors = false;
    }

    [RelayCommand]
    private void DismissWarnings()
    {
        WarningText = string.Empty;
        HasWarnings = false;
    }

    private void AppendError(string message)
    {
        ErrorText = HasErrors ? $"{ErrorText}\n{message}" : message;
        HasErrors = true;
    }

    /// <summary>
    /// Says once that the scan brought yaml files up to the current key set. Nothing has gone
    /// wrong — the settings are added blank and every value already there is untouched — but the
    /// app has written to files the user owns and very likely has in version control, and finding
    /// that out from a diff is worse than being told.
    /// </summary>
    /// <remarks>
    /// The banner rather than the status line, because the status line is overwritten by the next
    /// thing that happens. One line however many files: naming them all would be a wall of text on
    /// the first scan after an upgrade, which is exactly when this fires for a whole project.
    /// </remarks>
    private void ReportUpdatedYamls(IReadOnlyList<string> updatedYamls)
    {
        if (updatedYamls.Count == 0) return;

        var subject = updatedYamls.Count == 1
            ? $"1 yaml file ({Path.GetFileName(updatedYamls[0])})"
            : $"{updatedYamls.Count:N0} yaml files";
        AppendWarning(
            $"Added the settings that were missing to {subject}. " +
            "Values you had already written are unchanged.");
    }

    /// <summary>
    /// Things that didn't stop the site being generated but didn't do what was written either —
    /// a misspelled setting, two folders competing for one address. Kept off the error banner so
    /// a typo doesn't announce itself as a failed build.
    /// </summary>
    private void AppendWarning(string message)
    {
        WarningText = HasWarnings ? $"{WarningText}\n{message}" : message;
        HasWarnings = true;
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

    /// <summary>
    /// Installs the bundled VS Code extension that previews dir2site's ^^^ figure syntax. Offered
    /// here rather than hidden in a menu because the people writing articles are the ones who need
    /// it, and nothing else in the app tells them it exists.
    /// </summary>
    [RelayCommand]
    private async Task InstallVsCodeExtension()
    {
        StatusText = "Installing VS Code extension…";
        var result = await VsCodeExtensionInstaller.InstallAsync();

        StatusText = result.Message;
        if (!result.Succeeded)
        {
            AppendError(result.Message);
            if (result.RevealPath != null) Reveal(result.RevealPath);
        }
    }

    /// <summary>Shows a file in Finder / Explorer / the desktop file manager.</summary>
    private static void Reveal(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                // One argument, not two: ArgumentList quotes each element separately, and explorer
                // reads "/select," with the path split off as a flag with nothing selected — it
                // opens a window on the wrong folder rather than highlighting the file.
                Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { "/select," + path } });
            else if (OperatingSystem.IsMacOS())
                Process.Start(new ProcessStartInfo("open") { ArgumentList = { "-R", path } });
            else
                Process.Start(new ProcessStartInfo("xdg-open") { ArgumentList = { Path.GetDirectoryName(path)! } });
        }
        catch
        {
            // Revealing is a courtesy; the path is already in the message.
        }
    }

    /// <summary>Show what a Quick Sync would do, and confirm, before anything is uploaded.</summary>
    [ObservableProperty]
    private bool _previewBeforeDeploy;

    /// <summary>Every configured deploy target, for the picker in the deploy row.</summary>
    public ObservableCollection<DeployTarget> DeployTargetList { get; } = [];

    /// <summary>The target Quick Sync and Verify act on.</summary>
    [ObservableProperty]
    private DeployTarget? _selectedTarget;

    /// <summary>Only worth showing the picker when there is a choice to make.</summary>
    public bool HasMultipleTargets => DeployTargetList.Count > 1;

    // Set while loading a project, so populating the picker isn't mistaken for the user choosing.
    private bool _loadingTargets;

    partial void OnSelectedTargetChanged(DeployTarget? value)
    {
        if (_loadingTargets) return;
        if (value == null || DirectoryRoot == null || Dir2SiteConfig?.Deploy == null) return;
        if (string.Equals(Dir2SiteConfig.Deploy.Active, value.Name, StringComparison.Ordinal)) return;

        // Remember the choice, so the next session deploys where the user left off.
        Dir2SiteConfig.Deploy.Active = value.Name;
        DeployTargets.Save(ConfigPath()!, Dir2SiteConfig.Deploy);
        StatusText = $"Deploy target: {value.Name}";
    }

    private string? ConfigPath() =>
        DirectoryRoot == null ? null : Path.Combine(DirectoryRoot, "dir2site.yaml");

    private void ReloadDeployTargets()
    {
        DeployTargetList.Clear();
        if (DirectoryRoot == null || Dir2SiteConfig == null)
        {
            HasSftpProfile = false;
            OnPropertyChanged(nameof(HasMultipleTargets));
            return;
        }

        var deploy = DeployTargets.Resolve(DirectoryRoot, Dir2SiteConfig);
        foreach (var t in deploy.Targets) DeployTargetList.Add(t);

        _loadingTargets = true;
        try { SelectedTarget = DeployTargets.Active(deploy); }
        finally { _loadingTargets = false; }

        OnPropertyChanged(nameof(HasMultipleTargets));
        HasSftpProfile = DeployTargetList.Count > 0 && !string.IsNullOrWhiteSpace(SelectedTarget?.Host);
    }

    /// <summary>Why the deploy buttons are disabled, or empty when they aren't.</summary>
    [ObservableProperty]
    private string _syncBlockedReason = string.Empty;

    /// <summary>0–100 within the current sync phase, for a determinate bar.</summary>
    [ObservableProperty] private double _syncProgressPercent;

    /// <summary>True while the running phase can report a real position, not just a heartbeat.</summary>
    [ObservableProperty] private bool _syncProgressIsDeterminate;

    /// <summary>The file currently being transferred, for a second line under the bar.</summary>
    [ObservableProperty] private string _syncCurrentFile = string.Empty;

    /// <summary>
    /// The overall view of a generate — "Artifacts 340/340 · Pages 12/500 (3 new)" — shown under
    /// the status message. Empty until a generate has something to count, which is what keeps the
    /// status bar a single line the rest of the time.
    /// </summary>
    [ObservableProperty] private string _generateCounters = string.Empty;

    private void OnGenerateProgress(GenerateProgress p)
    {
        StatusText = p.Message;
        GenerateCounters = p.Counters;
    }

    // Reports arrive per file, which on a large site is thousands of UI updates a second — far more
    // than anyone can read and enough to starve the render thread. One update per file is fine for
    // the text; the bar only needs to move when the whole number of percent changes.
    private void OnSyncProgress(SyncProgress p)
    {
        StatusText = p.ToString();
        SyncCurrentFile = p.CurrentFile ?? string.Empty;
        SyncProgressIsDeterminate = p.HasCount;
        if (p.Percent is { } pct) SyncProgressPercent = pct;
    }

    private void ResetSyncProgress()
    {
        SyncProgressPercent = 0;
        SyncProgressIsDeterminate = false;
        SyncCurrentFile = string.Empty;
    }

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
    [NotifyCanExecuteChangedFor(nameof(ConfigureFooterCommand))]
    private Dir2SiteModel? _dir2SiteConfig;
    
    partial void OnDirectoryRootChanged(string? value)
    {
        if (_previewServer.IsRunning)
            _ = StopServer();
        RefreshSyncBlockedReason();
    }

    // The targets belong to the project config, so they follow it — including when it is reloaded
    // from disk after a hand edit.
    partial void OnDir2SiteConfigChanged(Dir2SiteModel? value) => ReloadDeployTargets();

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
        GenerateCounters = string.Empty;

        try
        {
            var progress = new Progress<string>(msg => StatusText = msg);

            var (root, files, artifacts, updatedYamls) = await Task.Run(() =>
            {
                var collected = new List<string>();
                var collectedArtifacts = new List<string>();
                var updated = new List<string>();
                var tree = DirectoryTraverser.BuildTree(
                    DirectoryRoot, collected, collectedArtifacts, progress, updated);
                return (tree, collected, collectedArtifacts, updated);
            });

            DirItems.Add(root);
            ReportUpdatedYamls(updatedYamls);

            await LoadOrCreateDir2SiteConfig();
            ReloadDeployTargets();

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

            // Said at load rather than at Generate: a misspelled setting is wrong the moment the
            // project opens, and waiting until a generate run to mention it is a slower loop.
            var configWarnings = new List<string>();
            YamlParser.ReportUnknownConfigKeys(yaml, configPath, configWarnings);
            foreach (var warning in configWarnings) AppendWarning(warning);
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
        GenerateCounters = string.Empty;

        // Every stage reports through the one tracker, so the message line and the counter line
        // always describe the same moment.
        var sink = new Progress<GenerateProgress>(OnGenerateProgress);
        var tracker = new GenerateProgressTracker(sink);

        (string Summary, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings,
            IReadOnlyList<string> Orphans) result;
        try
        {
            // Re-scan from disk so any YAML edits since last load are picked up
            tracker.Report("Scanning for changes...");
            var updatedYamls = new List<string>();
            var freshRoot = await Task.Run(() =>
            {
                var files     = new List<string>();
                var artifacts = new List<string>();
                return DirectoryTraverser.BuildTree(DirectoryRoot!, files, artifacts, tracker, updatedYamls);
            });
            ReportUpdatedYamls(updatedYamls);

            // Generate previews first so site settings (PDF resize/quality) affect output
            tracker.Report("Generating previews...");
            var root = freshRoot;
            await Task.Run(() => DirectoryTraverser.GeneratePreviews(root, config, tracker));

            tracker.Report("Generating site...");
            result = await Task.Run(() =>
                SiteGenerator.Generate(DirectoryRoot, root, config, tracker));
        }
        catch (Exception ex)
        {
            // Whatever went wrong, the app has to come back. IsLoading gates every button on the
            // window, so an escaping exception left it stuck on with nothing said — indisting-
            // uishable from a hang, and the scan and preview stages both touch every file in the
            // project, which is where a locked or unreadable one shows up.
            IsLoading = false;
            StatusText = "Generate failed";
            AppendError(ex.Message);
            return;
        }

        IsLoading = false;
        StatusText = result.Summary;
        // Straight from the tracker: reports reach the UI through a Progress<T> post, so the last
        // one can still be in flight and would otherwise overwrite the final line a moment later.
        GenerateCounters = tracker.Snapshot().Counters;
        if (result.Errors.Count > 0)
            AppendError(string.Join("\n", result.Errors));
        if (result.Warnings.Count > 0)
            AppendWarning(string.Join("\n", result.Warnings));

        if (result.Orphans.Count > 0)
            await HandleOrphanFiles(Path.Combine(DirectoryRoot, "_site"), result.Orphans);

        StartServerCommand.NotifyCanExecuteChanged();
        QuickSyncCommand.NotifyCanExecuteChanged();
        VerifyAndRepairCommand.NotifyCanExecuteChanged();
    }

    private bool CanGenerateSite() =>
        DirectoryRoot != null && DirItems.Count > 0 && Dir2SiteConfig != null && !IsLoading;

    /// <summary>
    /// Offers to take away what the generate found in _site but had no reason to put there. Asked
    /// rather than done, because removing files is the user's call — but only asked when there is
    /// something to ask about: a generate that changes nothing finds nothing.
    /// </summary>
    private async Task HandleOrphanFiles(string siteRoot, IReadOnlyList<string> orphans)
    {
        if (TopLevel is not Window owner) return;

        var dialog = new OrphanFilesView(orphans);
        var toRemove = await dialog.ShowDialog<IReadOnlyList<string>?>(owner);
        if (toRemove == null || toRemove.Count == 0)
        {
            // Saying nothing here would read as though the dialog had done something.
            StatusText = $"Site generated → _site/ — kept {orphans.Count} leftover file(s)";
            return;
        }

        // Deleting tens of thousands of files takes seconds even on a fast disk, and longer on a
        // network or cloud-synced folder. Without the busy flag and a running count the window sat
        // there looking finished, still showing the line from the generate that preceded it.
        IsLoading = true;
        var progress = new Progress<string>(message => StatusText = message);
        try
        {
            var result = await Task.Run(() => SiteGenerator.RemoveOrphans(siteRoot, toRemove, progress));
            StatusText = $"Site generated → _site/ — removed {result.Removed} file(s)";
            if (result.Errors.Count > 0)
                AppendError(string.Join("\n", result.Errors));
        }
        catch (Exception ex)
        {
            StatusText = "Removing files failed";
            AppendError(ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    // ---- Footer -------------------------------------------------------------

    /// <summary>
    /// Opens the footer's own dialog. Its rows are a list of records rather than a single value, so
    /// they don't fit beside the other site settings the way a colour or a title does.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanConfigureFooter))]
    private async Task ConfigureFooter()
    {
        if (DirectoryRoot == null || Dir2SiteConfig == null || TopLevel is not Window owner) return;

        var dialog = new FooterSettingsView(DirectoryRoot, Dir2SiteConfig);
        if (await dialog.ShowDialog<FooterSettingsResult?>(owner) is not { } result) return;

        Dir2SiteConfig.Footer = result.FooterText;
        Dir2SiteConfig.FooterColor = result.FooterColor;
        Dir2SiteConfig.FooterItems = [.. result.Items];

        // Written now rather than at the next Generate, so closing the app doesn't lose the edit.
        if (ConfigPath() is { } path)
        {
            var config = Dir2SiteConfig;
            await Task.Run(() => YamlParser.SaveDir2SiteConfig(path, config));
        }

        StatusText = result.Items.Count == 1
            ? "Footer saved — 1 item"
            : $"Footer saved — {result.Items.Count} items";
    }

    private bool CanConfigureFooter() => DirectoryRoot != null && Dir2SiteConfig != null;

    // ---- SFTP deploy --------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanConfigureSftp))]
    private async Task ConfigureSftp()
    {
        if (DirectoryRoot == null || Dir2SiteConfig == null || TopLevel is not Window owner) return;
        var dialog = new SftpSettingsView(DirectoryRoot, Dir2SiteConfig, ConfigPath()!);
        var saved = await dialog.ShowDialog<bool>(owner);
        if (saved)
        {
            ReloadDeployTargets();
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

        var target = SelectedTarget;
        if (target == null)
        {
            AppendError("No deploy target configured. Use Configure… first.");
            return;
        }

        var profile = DeployTargets.ToProfile(DirectoryRoot, target);

        var siteRoot = Path.Combine(DirectoryRoot, "_site");
        var secret = CredentialStoreFactory.Create()
            .Get(DeployTargets.CredentialKey(DirectoryRoot, target));
        var verifier = CreateHostKeyVerifier(target, profile);

        // Verify & Repair reconciles against the live server rather than uploading a computed
        // plan, so there is nothing meaningful to preview for it.
        SyncPlan? approved = null;
        if (PreviewBeforeDeploy && !verify)
        {
            approved = await ConfirmPlan(siteRoot, profile, secret, force, verifier);
            if (approved == null) return;
        }

        IsLoading = true;
        IsSyncing = true;
        _syncCancellation = new CancellationTokenSource();
        var token = _syncCancellation.Token;
        ResetSyncProgress();
        var progress = new Progress<SyncProgress>(OnSyncProgress);

        try
        {
            // Apply re-diffs and reports when the deploy no longer matches what was approved; with
            // no preview there is nothing to compare against, so QuickSync directly.
            var result = await Task.Run(() => verify
                ? SftpSyncService.VerifyAndRepair(siteRoot, profile, secret, progress, token, verifier)
                : approved != null
                    ? SftpSyncService.Apply(approved, siteRoot, profile, secret, force, progress, token, verifier)
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
            ResetSyncProgress();
        }
    }

    /// <summary>
    /// Shows the plan and waits for a yes, returning the approved plan so the deploy can be checked
    /// against it. Null means don't proceed — the user backed out, or there was nothing to do, in
    /// which case saying so beats running a deploy that uploads nothing.
    /// </summary>
    private async Task<SyncPlan?> ConfirmPlan(
        string siteRoot, SftpProfile profile, string? secret, bool force, IHostKeyVerifier? verifier)
    {
        if (TopLevel is not Window owner) return null;

        // Previewing connects, so it can hang on an unreachable host just as a deploy can. Same
        // cancellation source, same Cancel button.
        IsLoading = true;
        IsSyncing = true;
        _syncCancellation?.Dispose();
        _syncCancellation = new CancellationTokenSource();
        var token = _syncCancellation.Token;

        StatusText = "Working out what would change…";
        SyncPlan plan;
        try
        {
            plan = await Task.Run(() =>
                SftpSyncService.Preview(siteRoot, profile, secret, force, null, token, verifier));
        }
        catch (OperationCanceledException)
        {
            StatusText = "Preview cancelled";
            return null;
        }
        catch (Exception ex)
        {
            StatusText = "Preview failed";
            AppendError(ex.Message);
            return null;
        }
        finally
        {
            _syncCancellation?.Dispose();
            _syncCancellation = null;
            IsSyncing = false;
            IsLoading = false;
        }

        if (plan.IsEmpty)
        {
            StatusText = plan.Summary;
            return null;
        }

        return await new SyncPreviewView(plan).ShowDialog<bool>(owner) ? plan : null;
    }

    private async Task HandleStaleFiles(
        SftpProfile profile, string? secret, string siteRoot, IReadOnlyList<string> stale)
    {
        if (TopLevel is not Window owner) return;

        var dialog = new StaleFilesView(stale);
        var toDelete = await dialog.ShowDialog<IReadOnlyList<string>?>(owner);
        if (toDelete == null || toDelete.Count == 0) return;

        // Nothing in the app can undo this, and Select All puts it one click away.
        var confirm = new ConfirmView(
            "Delete Remote Files",
            $"Permanently delete {toDelete.Count} file(s) on the server?",
            $"They will be removed from {profile.Host} immediately. This cannot be undone from " +
            "dir2site — restoring them would mean re-uploading, and anything the server holds that " +
            "isn't in your local site would be gone for good.",
            $"Delete {toDelete.Count} File(s)");
        if (!await confirm.ShowDialog<bool>(owner)) return;

        var verifier = CreateHostKeyVerifier(SelectedTarget, profile);

        IsLoading = true;
        IsSyncing = true;
        _syncCancellation?.Dispose();   // RunSync's has already finished with; don't leak it
        _syncCancellation = new CancellationTokenSource();
        var token = _syncCancellation.Token;
        ResetSyncProgress();
        var progress = new Progress<SyncProgress>(OnSyncProgress);
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
            ResetSyncProgress();
        }
    }

    // Prompts on the main window for an unknown/changed host key. SftpSyncService pins the
    // accepted fingerprint onto the in-memory profile; persisting it here is what stops the
    // prompt reappearing on every sync.
    private IHostKeyVerifier? CreateHostKeyVerifier(DeployTarget? target, SftpProfile profile)
    {
        if (TopLevel is not Window owner) return null;
        return HostKeyPromptView.CreateVerifier(owner, info =>
        {
            if (target == null) return;

            // Use info.Fingerprint, not profile.HostKeyFingerprint. PromptVerifier invokes this
            // callback *before* it returns, and Connect only writes the pin onto the profile after
            // Verify returns — so the profile still holds the previous value here: empty on first
            // contact, the stale one after a key change. Persisting that meant the target was never
            // pinned and the trust prompt reappeared on every single deploy, which trains people to
            // click through the one dialog that must not become routine.
            var accepted = info.Fingerprint;

            // Hop to the UI thread: this runs on SSH.NET's connection thread, and the config is
            // owned by the view model.
            Dispatcher.UIThread.Post(() =>
            {
                if (ConfigPath() is { } path) PersistAcceptedHostKey(target, accepted, path);
            });
        });
    }

    /// <summary>Pins an accepted host key onto a target and writes it to the project config.</summary>
    internal void PersistAcceptedHostKey(DeployTarget target, string fingerprint, string configPath)
    {
        if (Dir2SiteConfig?.Deploy == null) return;
        target.HostKeyFingerprint = fingerprint;
        DeployTargets.Save(configPath, Dir2SiteConfig.Deploy);
    }

    /// <summary>
    /// Runs the update check and reports what it found. The startup call ignores the result, which
    /// is the behaviour it always had; only a user who asked for a check needs an answer when the
    /// answer is "nothing changed".
    /// </summary>
    public async Task<UpdateCheckResult> CheckForUpdatesAsync()
    {
        try
        {
            if (_updateManager == null) return UpdateCheckResult.NotSupported;
            _pendingUpdate = await _updateManager.CheckForUpdatesAsync();
            if (_pendingUpdate != null)
            {
                UpdateVersion = _pendingUpdate.TargetFullRelease.Version.ToString();
                UpdateAvailable = true;
                return UpdateCheckResult.Available;
            }

            return UpdateCheckResult.UpToDate;
        }
        catch
        {
            // No network, no GitHub release, dev environment, etc. Still not worth a banner at
            // startup; the caller decides whether it's worth saying out loud.
            return UpdateCheckResult.Failed;
        }
    }

    /// <summary>
    /// The startup check, on demand. A check that quietly finds nothing looks identical to a button
    /// that does nothing, so this one always says how it went — the banner only ever appears when
    /// there is something to install.
    /// </summary>
    [RelayCommand]
    private async Task CheckForUpdates()
    {
        StatusText = "Checking for updates…";

        StatusText = await CheckForUpdatesAsync() switch
        {
            UpdateCheckResult.Available => $"Update available: v{UpdateVersion}.",
            UpdateCheckResult.UpToDate => $"Up to date (v{_updateManager?.CurrentVersion}).",
            UpdateCheckResult.NotSupported => "This is a development build — it doesn't update itself.",
            _ => "Couldn't check for updates — no connection, or no release to compare against.",
        };
    }

    [RelayCommand(CanExecute = nameof(CanDownloadUpdate))]
    private async Task DownloadUpdate()
    {
        if (_pendingUpdate == null || _updateManager == null) return;
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

        // Ask straight away rather than making the user hunt for a second button. Declining leaves
        // UpdateReady set, so the "ready to install" banner is still there when they are ready.
        await ConfirmRestartAsync();
    }

    private bool CanDownloadUpdate() => UpdateAvailable && !UpdateReady && !IsDownloading;

    /// <summary>
    /// Offers to restart into the downloaded update. With no owner window — a test host, or any
    /// path where the view model outlives its window — the prompt is skipped rather than restarting
    /// unasked.
    /// </summary>
    private async Task ConfirmRestartAsync()
    {
        if (TopLevel is not Window owner) return;
        if (await new UpdateConfirmView(UpdateVersion).ShowDialog<bool>(owner))
            RestartAndUpdate();
    }

    [RelayCommand(CanExecute = nameof(CanRestartAndUpdate))]
    private void RestartAndUpdate()
    {
        if (_pendingUpdate == null || _updateManager == null) return;
        _updateManager.ApplyUpdatesAndRestart(_pendingUpdate);
    }

    private bool CanRestartAndUpdate() => UpdateReady;
}