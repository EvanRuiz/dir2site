// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
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

    private readonly RecentProjectsStore _recentProjects = RecentProjectsStore.Default;

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
        _ = LoadRecentProjectsAsync();
        VsCodeExtensionStateReady = RefreshVsCodeExtensionState();
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartServerCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConfigureSftpCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConfigureFooterCommand))]
    [NotifyCanExecuteChangedFor(nameof(QuickSyncCommand))]
    [NotifyCanExecuteChangedFor(nameof(VerifyAndRepairCommand))]
    [NotifyCanExecuteChangedFor(nameof(LoadDirectoryCommand))]
    private string? _directoryRoot;
    
    [ObservableProperty] public partial ObservableCollection<DirectoryTreeItem> DirItems { get; set; } = [];
    
    [ObservableProperty]
    private DirectoryTreeItem? _selectedItem;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GenerateSiteCommand))]
    [NotifyCanExecuteChangedFor(nameof(QuickSyncCommand))]
    [NotifyCanExecuteChangedFor(nameof(VerifyAndRepairCommand))]
    [NotifyCanExecuteChangedFor(nameof(LoadDirectoryCommand))]
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

    /// <summary>Show the install button: there is a VS Code here, and it hasn't got the extension.</summary>
    [ObservableProperty]
    private bool _canInstallVsCodeExtension;

    /// <summary>Show the update banner: the extension is installed, but older than the one we carry.</summary>
    [ObservableProperty]
    private bool _vsCodeExtensionUpdateAvailable;

    /// <summary>The version on offer, for the banner to name.</summary>
    public string VsCodeExtensionVersion => VsCodeExtensionInstaller.Version;

    /// <summary>
    /// The startup scan of the extensions folders. Exposed so a test can wait for it to settle
    /// rather than race it — it looks at the real machine, and what it finds there is nobody's to
    /// predict.
    /// </summary>
    internal Task VsCodeExtensionStateReady { get; }

    /// <summary>
    /// Decides which of the two extension affordances to show, if either.
    ///
    /// Nothing at all when no VS Code can be found or the current version is already installed —
    /// the button used to be permanent furniture offering people something they had.
    ///
    /// Someone still carrying the pre-rename extension counts as having something to update even if
    /// the new one is already current beside it: installing is what clears the old one away, so
    /// until it is gone there is a reason to press the button.
    /// </summary>
    private async Task RefreshVsCodeExtensionState()
    {
        var state = await VsCodeExtensionInstaller.DetectAsync();

        var outdated = state.Installed != null &&
                       state.Installed < VsCodeExtensionInstaller.BundledVersion;

        CanInstallVsCodeExtension = state is { VsCodeFound: true, Installed: null, HasLegacy: false };
        VsCodeExtensionUpdateAvailable = state.VsCodeFound && (outdated || state.HasLegacy);
    }

    /// <summary>
    /// Installs the bundled VS Code extension that makes the Markdown preview match dir2site. Offered
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

        // Whichever affordance was showing should go away once it has been acted on — and stay if
        // the install didn't actually take.
        await RefreshVsCodeExtensionState();
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
        MarkConfigWritten();
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
        RestartSourceWatcher(value);
        RefreshSyncBlockedReason();
    }

    // ---- watching the source folder (#62) -----------------------------------

    private SourceWatcher? _sourceWatcher;

    /// <summary>Everything the user has done to the folder since the last generate.</summary>
    private readonly List<SourceChange> _changesSinceGenerate = [];

    /// <summary>
    /// Whether every difference between the source folder and <c>_site</c> is one we can account for.
    /// </summary>
    /// <remarks>
    /// True only from the end of a generate — when the two agreed — for as long as every batch since
    /// has arrived witnessed. It goes false when a project is opened, because whatever happened to
    /// the folder before we were running is not something we saw, and it goes false again the moment
    /// events are lost.
    ///
    /// This is what separates acting from asking. A move or a deletion we watched happen is the
    /// user's stated intent and can be carried through in silence; the same conclusion reached by
    /// comparing the site against the source afterwards is a guess, and a guess about deletion
    /// belongs in a dialog.
    /// </remarks>
    private bool _siteIsAccountedFor;

    /// <summary>
    /// Whether this view model belongs to a real window, and so should be watching the folder.
    /// </summary>
    /// <remarks>
    /// Watching is a session-long concern with a live OS handle behind it, not something a property
    /// setter should start on its own. Tied to the window instead: a view model built to be asked
    /// one question — and every headless test is that — leaves no watcher behind on a folder it is
    /// about to delete.
    /// </remarks>
    private bool _watching;

    /// <summary>
    /// How long the folder must go quiet before a burst is acted on.
    /// </summary>
    /// <remarks>
    /// Overridable so a test can arrange for a change to land <em>during</em> a run rather than
    /// hoping the timing falls that way. Production has no reason to set it — see
    /// <see cref="SourceWatcher.DefaultDebounceMs"/> for why a second is the right wait.
    /// </remarks>
    internal int WatchDebounceMs { get; set; } = SourceWatcher.DefaultDebounceMs;

    /// <summary>Begins watching the project folder, and keeps watching as projects change.</summary>
    public void StartWatching()
    {
        if (_watching) return;
        _watching = true;
        RestartSourceWatcher(DirectoryRoot);
    }

    /// <summary>
    /// Stops watching and lets go of the OS handle behind it.
    /// </summary>
    /// <remarks>
    /// A watcher is disposed when the project changes, but nothing said what happens when the view
    /// model itself is finished with — so one was left running on the last project opened, holding a
    /// handle and posting to a dispatcher that may be going away. Harmless enough in an app that is
    /// quitting anyway; not harmless in a test, which builds these by the dozen against temp folders
    /// it then deletes.
    /// </remarks>
    public void StopWatching()
    {
        _watching = false;
        _sourceWatcher?.Dispose();
        _sourceWatcher = null;
    }

    /// <summary>
    /// Points the watcher at <paramref name="root"/>, forgetting what was known about the last one.
    /// </summary>
    private void RestartSourceWatcher(string? root)
    {
        // A different project says nothing about this one.
        _changesSinceGenerate.Clear();
        _uncarried.Clear();
        _siteIsAccountedFor = false;
        PendingSiteOrphans = [];
        _configWrittenAt = null;

        RearmSourceWatcher(root);
    }

    /// <summary>
    /// Puts a fresh watcher on the same folder, keeping everything already known about it.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="RestartSourceWatcher"/> because resuming a watch is not the same
    /// event as opening a project: the changes recorded so far, and whether the site is accounted
    /// for, are still true of this folder and must survive.
    /// </remarks>
    private void RearmSourceWatcher(string? root)
    {
        _sourceWatcher?.Dispose();
        _sourceWatcher = null;

        if (!_watching || root == null || !Directory.Exists(root)) return;

        var watcher = new SourceWatcher(root, WatchDebounceMs);
        watcher.Changed += OnSourceChanged;
        watcher.Stopped += OnWatchingStopped;
        watcher.Start();
        _sourceWatcher = watcher;
    }

    /// <summary>
    /// Reacts to a settled burst of changes in the source folder.
    /// </summary>
    /// <remarks>
    /// A batch is always recorded, even when one arrives while a scan or a generate is running. It
    /// used to be dropped, on the grounds that a scan writes yaml itself — <c>EnsureDefaultKeys</c>
    /// brings sidecars up to the current key set, <c>CreateDefaultYamlMeta</c> writes new ones — and
    /// those writes are changes to the folder being watched. That reasoning holds for our writes and
    /// not at all for the user's: a photo dropped in while a generate was running was discarded
    /// outright, never reached <see cref="_changesSinceGenerate"/>, and so was invisible to the next
    /// run's narrowed scope too. It got no page and no card, and nothing said so.
    ///
    /// Recording it costs an extra pass over an idempotent scan and buys never losing a change.
    /// </remarks>
    private void OnSourceChanged(object? sender, SourceChangeBatch batch)
    {
        // Raised on the debounce timer's thread; everything below belongs to the UI.
        Dispatcher.UIThread.Post(() =>
        {
            if (batch.Witnessed)
            {
                // Recorded here, acted on at the top of the next pass. Moving a sidecar and a
                // preview folder is real filesystem work, and this can arrive at any moment — with a
                // generate reading those very directories on background threads. The old IsLoading
                // guard made that impossible by dropping the batch; nothing replaced it when the
                // batch stopped being dropped.
                _uncarried.AddRange(batch.Changes);
                _changesSinceGenerate.AddRange(batch.Changes);
            }
            else
            {
                // Some of what happened was never seen, so the classifications we do hold no longer
                // add up to an explanation. Keeping the ones that arrived would be worse than
                // keeping none: they would look like a complete account of a folder that has since
                // changed in ways nobody recorded.
                _uncarried.Clear();
                _changesSinceGenerate.Clear();
                _siteIsAccountedFor = false;
            }

            // Mid-run, the work in flight is already past the point where it would notice. Ask for
            // another pass afterwards rather than starting one on top of it.
            if (IsLoading)
            {
                _changesArrivedMidRun = true;
                return;
            }

            _ = RespondToChanges();
        });
    }

    /// <summary>Set when a batch lands during a run, so one more follows it.</summary>
    private bool _changesArrivedMidRun;

    /// <summary>Witnessed changes whose sidecars and previews have yet to be moved.</summary>
    private readonly List<SourceChange> _uncarried = [];

    /// <summary>
    /// Picks up a change that landed while something else held the app busy.
    /// </summary>
    /// <remarks>
    /// <see cref="IsLoading"/> is held by more than the scan-and-generate loop: a manual Rescan, a
    /// manual Generate, the leftovers dialog, and every deploy — which holds it for the length of an
    /// upload, exactly when carrying on working is the natural thing to do. A batch arriving during
    /// any of those set a flag that only the loop read, so it sat there until some later change
    /// happened to sweep it up. That is round one's lost change again, in the corner that fix didn't
    /// reach; draining it wherever the app falls idle covers all of them at once.
    /// </remarks>
    private void DrainChangesThatArrivedMidRun()
    {
        if (_responding || !_changesArrivedMidRun || DirectoryRoot == null) return;

        _changesArrivedMidRun = false;
        _ = RespondToChanges();
    }

    /// <summary>
    /// Says out loud that the folder is no longer being watched.
    /// </summary>
    /// <remarks>
    /// The alternative is worse than it sounds: auto-generate would stop working with its checkbox
    /// still ticked, and nothing on screen would differ.
    ///
    /// What happened, and nothing else. The obvious addition is to name Rescan as the way back, and
    /// it was there — but this only fires when the folder itself has gone, and telling someone to
    /// rescan a folder that isn't there is advice that cannot work. Reporting the error is the whole
    /// job; Rescan still resumes watching for anyone who finds it.
    /// </remarks>
    private void OnWatchingStopped(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(() =>
        {
            _watchingLost = true;
            AppendWarning(
                "Stopped watching the project folder — it may have been renamed, moved or removed.");
        });

    /// <summary>
    /// Set when watching has died, so the next Rescan can pick it up again.
    /// </summary>
    /// <remarks>
    /// Rescan is the only thing that could plausibly bring watching back, and it didn't: it runs
    /// <c>LoadDirectory</c>, which reads the folder and touches nothing to do with watching — so the
    /// tree refreshed and the watch stayed dead, with no way to tell from the screen.
    /// </remarks>
    private bool _watchingLost;

    /// <summary>
    /// Acts as though the platform had reported the watch lost.
    /// </summary>
    /// <remarks>
    /// A seam, in the <see cref="SourceListing"/> manner: a real <c>FileSystemWatcher.Error</c>
    /// cannot be provoked reliably — it wants a buffer overflow or a deleted watch root — and the
    /// thing worth testing is not how the watch dies but whether Rescan brings it back.
    /// </remarks>
    internal void PretendWatchingStopped()
    {
        // Genuinely stop, not just report it: a flag alone would leave the real watcher delivering
        // events, and a test could not tell a recovered watch from one that never died.
        _sourceWatcher?.Dispose();
        _sourceWatcher = null;
        OnWatchingStopped(this, EventArgs.Empty);
    }

    /// <summary>
    /// True while a scan-and-maybe-generate is under way, so a second one never starts alongside it.
    /// </summary>
    /// <remarks>
    /// <see cref="IsLoading"/> nearly serves, and doesn't: it goes false between the scan finishing
    /// and the generate starting, and a batch landing in that gap would begin a second pass on top
    /// of the first.
    /// </remarks>
    private bool _responding;

    /// <summary>
    /// Brings the tree — and the site, if it is being kept up to date — into line with what changed.
    /// </summary>
    /// <remarks>
    /// Loops rather than returning after one pass, because a change that lands mid-run arrives too
    /// late for the work already in flight to notice it. One more pass afterwards is what stops it
    /// being lost.
    ///
    /// It terminates because the writing a run does is idempotent: <c>EnsureDefaultKeys</c> and
    /// <c>CreateDefaultYamlMeta</c> add what is missing and then have nothing left to add. So the
    /// pass triggered by our own writes writes nothing, raises nothing, and ends the loop.
    ///
    /// That reasoning is worth distrusting, because it was wrong once. A generate also saved the
    /// config, and <c>SetBlock</c> dirtied a document it had changed nothing in, so any project with
    /// a <c>footerItems:</c> block rebuilt itself for as long as the app was left open. The cap
    /// below did not catch it and could not: the write settles after the run has finished, so it
    /// arrives as a fresh call with the counter back at zero. The cap bounds one call, and a loop
    /// fed by our own writes is never one call.
    ///
    /// So the cap is not the defence. The defence is that nothing a run does writes into the folder
    /// unless something really changed — see <see cref="YamlDocumentEditor"/>, which is where that
    /// is now enforced for every splice rather than remembered per caller.
    /// </remarks>
    private async Task RespondToChanges()
    {
        if (_responding)
        {
            _changesArrivedMidRun = true;
            return;
        }

        _responding = true;
        try
        {
            var passes = 0;
            do
            {
                _changesArrivedMidRun = false;

                CarryRenamedArtifacts();
                await LoadDirectory();
                if (AutoGenerate) await GenerateSite();
            }
            while (_changesArrivedMidRun && ++passes < MaxFollowUpPasses);

            // Reaching the cap means something is writing on every pass, which the reasoning above
            // says cannot happen. Saying nothing would leave the folder and the site quietly out of
            // step, with the checkbox still ticked — the same silence this branch added a warning
            // for when watching dies.
            if (_changesArrivedMidRun)
                AppendWarning(
                    $"Stopped after {MaxFollowUpPasses} rebuilds in a row — something in the folder " +
                    "keeps changing.");
        }
        finally
        {
            _responding = false;
        }
    }

    private const int MaxFollowUpPasses = 10;

    /// <summary>
    /// Rebuild the site whenever the folder changes, without anyone pressing anything.
    /// </summary>
    /// <remarks>
    /// Deliberately not remembered between sessions, per the issue: it is not clear that opening a
    /// project should start rebuilding it, and a setting that survives a restart would decide that
    /// on the user's behalf.
    /// </remarks>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GenerateSiteCommand))]
    private bool _autoGenerate;

    /// <summary>
    /// Leftovers in <c>_site</c> nobody has been asked about yet, because the run that found them
    /// was unattended. Cleared when a run finds none, and when the project changes.
    /// </summary>
    /// <remarks>
    /// They matter because they get published — the deploy walks <c>_site</c> for its manifest, so a
    /// leftover there is uploaded and served exactly like a real page. Until then they are only
    /// visible to the local preview server, which is why holding them until a deploy costs nothing.
    /// </remarks>
    internal IReadOnlyList<string> PendingSiteOrphans { get; set; } = [];

    /// <summary>How much of the site the current run has a reason to re-render.</summary>
    private RenderScope _scope = RenderScope.All;


    // The targets belong to the project config, so they follow it — including when it is reloaded
    // from disk after a hand edit.
    partial void OnDir2SiteConfigChanged(Dir2SiteModel? oldValue, Dir2SiteModel? newValue)
    {
        if (oldValue != null) oldValue.PropertyChanged -= OnConfigEdited;

        // Targets first, subscription second, and the order is the whole point. Resolving them does
        // `config.Deploy ??= new DeployConfig()`, which is a property set like any other — so
        // subscribing first meant merely opening a project counted as an edit, and a hand-written
        // dir2site.yaml came back with eleven keys it never had. It also set the watcher off, which
        // with auto-generate on cost a full rebuild for having opened the folder.
        ReloadDeployTargets();

        if (newValue != null) newValue.PropertyChanged += OnConfigEdited;
    }

    /// <summary>
    /// Writes the project config the moment a setting is finished with.
    /// </summary>
    /// <remarks>
    /// dir2site.yaml used to be written from inside Generate Site, which was fine while that button
    /// was the only way anything happened — and stops being fine the moment auto-generate disables
    /// it, at which point Title, colors and the PDF settings would never be saved at all.
    ///
    /// "Finished with" is the text boxes' doing, not this method's: they commit on focus loss rather
    /// than per keystroke, so what arrives here is a value the user has stopped writing. Saving on
    /// every keystroke instead would publish <c>#33</c> partway through someone typing a colour —
    /// a perfectly valid-looking colour, and the wrong one.
    ///
    /// The rebuild is not triggered from here. The write lands in the folder the watcher is already
    /// watching, so a config edit reaches the site by the same route a hand edit to the same file
    /// does — and tabbing through three fields writes three times but settles into one rebuild.
    /// </remarks>
    private void OnConfigEdited(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (DirectoryRoot == null || Dir2SiteConfig is not { } config) return;
        if (ConfigPath() is not { } path) return;

        try
        {
            YamlParser.SaveDir2SiteConfig(path, config);
            MarkConfigWritten();
        }
        catch (Exception ex)
        {
            AppendError($"Could not save site settings: {ex.Message}");
        }
    }

    // When we last wrote dir2site.yaml ourselves, so a rescan can tell a hand edit from its own echo.
    private DateTime? _configWrittenAt;

    /// <summary>
    /// Records that the config on disk is now our own writing.
    /// </summary>
    /// <remarks>
    /// Every path that writes dir2site.yaml has to call this, and the reason it is a method rather
    /// than a line is that it was a line: only the settings panel's save set the stamp, so the
    /// deploy writers — which splice <c>deploy:</c> in without going through the model — left it
    /// naming an older version of the file. The next scan compared it, concluded the user had edited
    /// by hand, and replaced the config object the panel is bound to, taking anything half-typed
    /// with it. That is the failure the footer dialog's explicit save was removed for.
    /// </remarks>
    private void MarkConfigWritten()
    {
        if (ConfigPath() is { } path) _configWrittenAt = LastWriteOf(path);
    }

    private static DateTime? LastWriteOf(string path)
    {
        try { return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null; }
        catch { return null; }
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

        await OpenProject(folders[0].Path.LocalPath);
    }

    /// <summary>The one way a project is opened, whether picked from disk or from a recent tile.</summary>
    private async Task OpenProject(string path)
    {
        DirectoryRoot = path;
        await LoadDirectory();
    }

    /// <summary>Project folders offered on the welcome screen, newest first.</summary>
    [ObservableProperty] public partial ObservableCollection<RecentProjectItem> RecentProjectItems { get; set; } = [];

    [ObservableProperty] private bool _hasRecentProjects;

    /// <summary>
    /// Rebuilds the welcome-screen tiles: reads the remembered folders, drops the ones that are
    /// gone, and decodes their logos — all on a background thread, so a slow or unmounted volume
    /// delays a shortcut rather than the window appearing.
    /// </summary>
    private async Task LoadRecentProjectsAsync()
    {
        try
        {
            var prepared = await Task.Run(() => _recentProjects.Load()
                .Select(entry => RecentProjectItem.Prepare(entry.Path))
                .OfType<RecentProjectItem.Prepared>()
                .ToList());

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                // The tiles themselves are built here rather than above: their brushes and any SVG
                // logo are Avalonia objects, which refuse to be constructed off the UI thread.
                var items = prepared.Select(RecentProjectItem.Create).ToList();

                // Swap first, dispose after: no tile is ever bound to a bitmap that has already
                // been released.
                var stale = RecentProjectItems;
                RecentProjectItems = new ObservableCollection<RecentProjectItem>(items);
                HasRecentProjects = items.Count > 0;
                foreach (var item in stale) item.Dispose();
            });
        }
        catch
        {
            // A shortcut list that won't build is not worth an error banner on startup.
        }
    }

    [RelayCommand]
    private async Task OpenRecentProject(RecentProjectItem? item)
    {
        if (item == null) return;

        // The tiles were built when the window opened; the folder may have gone since.
        if (!Directory.Exists(item.Path))
        {
            RemoveRecentProjectTile(item);
            AppendWarning($"{item.Path} is no longer available.");
            return;
        }

        await OpenProject(item.Path);
    }

    /// <summary>
    /// Drops a project from the welcome screen. Only the shortcut goes — the project itself is
    /// untouched, and opening it again puts the tile back, so there is nothing to confirm.
    /// </summary>
    [RelayCommand]
    private async Task ForgetRecentProject(RecentProjectItem? item)
    {
        if (item == null) return;

        var path = item.Path;
        RemoveRecentProjectTile(item);
        await Task.Run(() => _recentProjects.Forget(path));
    }

    private void RemoveRecentProjectTile(RecentProjectItem item)
    {
        RecentProjectItems.Remove(item);
        HasRecentProjects = RecentProjectItems.Count > 0;
        item.Dispose();
    }

    /// <summary>
    /// Moves each renamed artifact's sidecar, thumbnails and caption along with it.
    /// </summary>
    /// <remarks>
    /// Called at the top of a pass, before the walk — so it finds the sidecar already sitting beside
    /// its file. Left until after, the scan would see a file with no sidecar, scaffold a fresh one,
    /// and strand the caption and settings the user wrote in the old one, which is the very thing
    /// this exists to prevent.
    ///
    /// And not before that, which is the other half. Moving a sidecar and a whole
    /// <c>.dir2site/{stem}/</c> directory is real filesystem work, and doing it the moment a batch
    /// arrives means doing it while a generate may be reading those directories on background
    /// threads. A lost race there is quiet: <c>MoveSidecar</c> swallows a failed move, so the
    /// sidecar keeps the old name, the next pass scaffolds a fresh one, and the user's caption turns
    /// up in the leftovers dialog. At the top of a pass nothing else is running.
    ///
    /// Directories are skipped: a folder carries its contents with it, so everything inside is
    /// already where it should be and there is nothing to move.
    /// </remarks>
    private void CarryRenamedArtifacts()
    {
        if (_uncarried.Count == 0) return;

        var changes = _uncarried.ToArray();
        _uncarried.Clear();

        foreach (var change in changes)
        {
            switch (change.Kind)
            {
                case SourceChangeKind.Moved when change.From is { } from && !Directory.Exists(change.Path):
                    ArtifactRename.Apply(from, change.Path);
                    break;

                // A deletion we watched happen takes its settings and previews with it. Left behind
                // they are invisible — a hidden folder and a sidecar for a file that isn't there —
                // so they accumulate quietly for as long as a project is worked on.
                case SourceChangeKind.Removed:
                    SourceLeftovers.RemoveFor(change.Path);
                    break;
            }
        }
    }

    /// <summary>
    /// Re-reads the project folder into the tree.
    /// </summary>
    /// <remarks>
    /// A command as well as an internal step, so there is a way back when watching misses something
    /// — a cloud-synced folder that under-reports, a burst that overflowed the watcher's buffer, or
    /// a platform quirk nobody has hit yet. Until now the only way to refresh was to pick the same
    /// folder again from the file dialog, which is a strange thing to have to work out.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanRescan))]
    private async Task LoadDirectory()
    {
        if (DirectoryRoot == null)
        {
            DirItems.Clear();
            return;
        }

        if (!ProjectFolderIsThere()) return;

        DirItems.Clear();

        // Rescan is the way back from a dead watch, so it has to be the thing that puts watching
        // back — reading the folder again and leaving it dead would look like recovery and be none.
        //
        // Silently. Re-arming is plumbing recovering itself: nothing failed, so there is nothing to
        // report, the same as the silent re-arm TryRearm already does after a buffer overflow.
        if (_watchingLost && _watching)
        {
            RearmSourceWatcher(DirectoryRoot);
            _watchingLost = _sourceWatcher == null;
        }

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

            // Remembered only once the project has actually opened, so a folder that failed to
            // scan doesn't earn a tile — and so its dir2site.yaml is known to exist by now, which
            // is what the resolver uses to decide the folder is still a project.
            var opened = DirectoryRoot;
            await Task.Run(() => _recentProjects.Remember(opened));
            _ = LoadRecentProjectsAsync();

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

    private bool CanRescan() => DirectoryRoot != null && !IsLoading;

    /// <summary>
    /// Whether the project folder is still where it was, saying so if it isn't.
    /// </summary>
    /// <remarks>
    /// A folder that isn't there is not a folder with nothing in it, and both of the things this
    /// window does to a project go wrong when it's missing — in opposite directions, which is why
    /// the check belongs somewhere they both pass rather than on the one that was noticed first.
    ///
    /// A scan destroys the last good view: the tree empties, and <c>LoadOrCreateDir2SiteConfig</c>
    /// scaffolds a default config over the one that was loaded, so a title and footer the user wrote
    /// become defaults named after the folder. Touch any setting once the drive is back and those
    /// defaults are what reaches the real file.
    ///
    /// A generate does the opposite and builds the folder back. <c>SiteGenerator.Generate</c> opens
    /// with a <c>CreateDirectory</c> of <c>_site</c>, which creates every missing segment on the way
    /// — so Generate left a phantom project folder at the old path holding a complete site, and
    /// reported success directly underneath the error saying nothing had been changed.
    ///
    /// This is the state the "stopped watching" warning is reported in, so it is the state both
    /// buttons are most likely to be pressed in.
    /// </remarks>
    private bool ProjectFolderIsThere()
    {
        if (DirectoryRoot == null) return false;
        if (Directory.Exists(DirectoryRoot)) return true;

        StatusText = "Project folder not found";
        AppendError(
            $"Could not read {DirectoryRoot} — it may have been renamed, moved or removed. " +
            "Nothing has been changed.");
        return false;
    }

    private async Task LoadOrCreateDir2SiteConfig()
    {
        if (DirectoryRoot == null) return;
        var configPath = Path.Combine(DirectoryRoot, "dir2site.yaml");

        // Re-reading a file we wrote ourselves would replace the object the settings panel is bound
        // to, and anything half-typed in another box would go with it. A rescan set off by some
        // unrelated file has no business disturbing what the user is in the middle of.
        if (Dir2SiteConfig != null && _configWrittenAt is { } written
            && LastWriteOf(configPath) == written)
            return;

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
            MarkConfigWritten();
        }
    }

    [RelayCommand(CanExecute = nameof(CanGenerateSite))]
    private async Task GenerateSite()
    {
        if (DirectoryRoot == null || DirItems.Count == 0 || Dir2SiteConfig == null) return;
        if (!ProjectFolderIsThere()) return;

        // Read once, so the background stages below all build from the same config even if the
        // settings panel is edited while they run.
        //
        // No config *save* here. It was a leftover from when Generate was the only thing that ever
        // wrote dir2site.yaml; every mutation of the model now saves as it happens, through
        // OnConfigEdited. Keeping it meant a build wrote into the folder it had just been asked to
        // build from — which under auto-generate is the watcher's next change, and the next build.
        var config = Dir2SiteConfig;

        IsLoading = true;
        GenerateCounters = string.Empty;

        // Every stage reports through the one tracker, so the message line and the counter line
        // always describe the same moment.
        var sink = new Progress<GenerateProgress>(OnGenerateProgress);
        var tracker = new GenerateProgressTracker(sink);

        (string Summary, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings,
            IReadOnlyList<string> Orphans) result;
        IReadOnlyList<string> explainedByChanges = [];
        IReadOnlyList<string> sourceLeftovers = [];
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

            // The scan above is the freshest picture of the project there is, so the tree on screen
            // should be it. Until now the result was used for the build and then dropped, leaving
            // the panel showing whatever the folder looked like when it was first opened — so a
            // file added since, or a yaml fixed to clear an error, stayed invisible until the user
            // re-picked the same folder from the file dialog.
            DirItems.Clear();
            DirItems.Add(freshRoot);

            // Carry moves and deletions through to _site before anything is written, so the build
            // below runs over a tree that is already in the right shape. Left until afterwards, a
            // moved folder shows up as pages at a new address and unaccounted-for files at the old
            // one, and the user gets asked to confirm deleting content they never deleted.
            explainedByChanges = ApplySourceChanges(freshRoot, tracker);

            // Only worth asking about when nothing witnessed the deletions that would explain them.
            // With the watcher running these were already taken away as they happened, so a run
            // that finds any here has been out of the loop for something.
            if (!_siteIsAccountedFor)
                sourceLeftovers = await Task.Run(() => SourceLeftovers.FindAll(DirectoryRoot!));

            // Generate previews first so site settings (PDF resize/quality) affect output
            tracker.Report("Generating previews...");
            var root = freshRoot;
            await Task.Run(() => DirectoryTraverser.GeneratePreviews(root, config, tracker));

            tracker.Report("Generating site...");
            var scope = _scope;
            result = await Task.Run(() =>
                SiteGenerator.Generate(DirectoryRoot, root, config, tracker, scope));
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

        // The site and the source agree as of now, so from here on the watcher's account of what
        // changes is a complete one — until it tells us otherwise.
        _siteIsAccountedFor = true;
        if (result.Errors.Count > 0)
            AppendError(string.Join("\n", result.Errors));
        if (result.Warnings.Count > 0)
            AppendWarning(string.Join("\n", result.Warnings));

        // A run that finds nothing has settled whatever an earlier one was holding.
        PendingSiteOrphans = [];

        if (result.Orphans.Count > 0)
            await HandleLeftovers(Path.Combine(DirectoryRoot, "_site"), result.Orphans, explainedByChanges);

        await OfferSourceLeftovers(sourceLeftovers);

        StartServerCommand.NotifyCanExecuteChanged();
        QuickSyncCommand.NotifyCanExecuteChanged();
        VerifyAndRepairCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Brings <c>_site</c> into line with what has happened to the source folder, by whichever route
    /// the evidence supports.
    /// </summary>
    /// <remarks>
    /// Two routes, and the difference between them is what we saw rather than what we can work out.
    /// When every change since the last generate was witnessed, the batch says what the user did and
    /// both moves and deletions are carried through. When it wasn't — the app was closed, or events
    /// were lost — only moves are recovered, from the shapes left behind, because a subtree the site
    /// no longer wants that pairs with a place it does want can only be one thing. A subtree with
    /// nothing to pair against could be a deletion or could be anything else, so it goes on to the
    /// sweep and gets offered rather than assumed.
    /// </remarks>
    /// <returns>
    /// The parts of <c>_site</c> the witnessed changes answer for, so the sweep below can tell a
    /// knock-on effect of something the user did from a file nobody can account for.
    /// </returns>
    private IReadOnlyList<string> ApplySourceChanges(
        DirectoryTreeItem freshRoot, GenerateProgressTracker tracker)
    {
        if (DirectoryRoot is not { } root) return [];

        // Both halves matter. The flag says the site and the source agreed at some point and every
        // change since was seen; the count says there is actually an account to act on. Without it a
        // watcher that died quietly would leave the flag standing and let a run skip the leftover
        // sweep on the strength of an empty change list — claiming to have witnessed nothing
        // happening, when in fact it witnessed nothing.
        var witnessed = _siteIsAccountedFor && _changesSinceGenerate.Count > 0;

        var result = witnessed
            ? SiteChangeApplier.Apply(root, [.. _changesSinceGenerate], tracker)
            : SiteChangeApplier.ReconcileMoves(root, freshRoot, tracker);

        var explained = witnessed
            ? SiteChangeApplier.ExplainedBy(root, [.. _changesSinceGenerate])
            : [];

        // The same knowledge, put to a second use: what a change can reach is also the only part of
        // the site worth re-rendering. Without it every save re-renders every page.
        _scope = witnessed
            ? RenderScope.For(root, [.. _changesSinceGenerate], freshRoot)
            : RenderScope.All;

        _changesSinceGenerate.Clear();

        if (result.Errors.Count > 0)
            AppendWarning(string.Join("\n", result.Errors));

        return explained;
    }

    // Disabled while auto-generate is on, per the issue: with the site rebuilding on every change
    // the button has nothing left to do, and leaving it live invites a second run alongside one
    // already under way.
    private bool CanGenerateSite() =>
        DirectoryRoot != null && DirItems.Count > 0 && Dir2SiteConfig != null
        && !IsLoading && !AutoGenerate;

    /// <summary>
    /// Deals with what the generate found in _site but had no reason to put there, splitting it by
    /// whether we can say how it got that way.
    /// </summary>
    /// <remarks>
    /// A page stranded by a change we watched happen is not a question. Deleting one of two photos
    /// from a folder leaves it holding a single item, which publishes as the folder's own index
    /// rather than as a card — so the surviving photo's page moves up a level and the old one is
    /// left behind. Asking about that is asking the user to confirm a consequence of the layout
    /// rules, phrased as though they had deleted something.
    ///
    /// Everything else still gets asked about, and that is the point of splitting rather than
    /// suppressing: <c>_site</c> is not watched, so a file put there by hand or by another tool is
    /// exactly what we would not have seen, and it is exactly what should be asked about.
    /// </remarks>
    private async Task HandleLeftovers(
        string siteRoot, IReadOnlyList<string> orphans, IReadOnlyList<string> explained)
    {
        var accountedFor = orphans.Where(o => SiteChangeApplier.IsExplained(o, explained)).ToList();
        var unexplained  = orphans.Where(o => !SiteChangeApplier.IsExplained(o, explained)).ToList();

        if (accountedFor.Count > 0)
        {
            var removal = await Task.Run(() => SiteGenerator.RemoveOrphans(siteRoot, accountedFor));
            if (removal.Errors.Count > 0)
                AppendWarning(string.Join("\n", removal.Errors));
        }

        if (unexplained.Count == 0) return;

        // Never unattended. A modal appearing because someone reorganized a folder in Finder, with
        // nobody at the keyboard and Select All one click from taking the site apart, is the worst
        // thing this feature could do. Held instead until a deploy, which is the moment these files
        // would actually reach anyone.
        if (AutoGenerate)
        {
            PendingSiteOrphans = unexplained;
            StatusText = unexplained.Count == 1
                ? "1 leftover file in _site"
                : $"{unexplained.Count} leftover files in _site";
            return;
        }

        await HandleOrphanFiles(siteRoot, unexplained);
    }

    /// <summary>
    /// Offers to tidy away sidecars and preview folders whose artifact is no longer in the project.
    /// </summary>
    /// <remarks>
    /// Asked rather than done, because nothing here saw the artifact go: these were found by
    /// noticing it is missing, which is equally what a rename we could not pair looks like. A
    /// deletion the watcher witnessed has already taken them, without a prompt — that is the
    /// difference the whole feature turns on.
    ///
    /// Its own dialog rather than a section in the _site one. Deleting a published page and deleting
    /// a hidden settings file are decisions of very different weight, and running them together
    /// would make the lighter one carry the alarm of the heavier.
    /// </remarks>
    private async Task OfferSourceLeftovers(IReadOnlyList<string> leftovers)
    {
        if (leftovers.Count == 0 || DirectoryRoot == null) return;
        if (TopLevel is not Window owner) return;

        // Same rule as the site's own leftovers: nothing modal with nobody there. These are never
        // published, so unlike those there is nothing to hold them for — they simply wait for a
        // generate the user asked for.
        if (AutoGenerate) return;

        var relative = leftovers
            .Select(p => Path.GetRelativePath(DirectoryRoot, p).Replace(Path.DirectorySeparatorChar, '/'))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        var dialog = new OrphanFilesView(relative, OrphanKind.Source);
        var toRemove = await dialog.ShowDialog<IReadOnlyList<string>?>(owner);
        if (toRemove == null || toRemove.Count == 0) return;

        var errors = new List<string>();
        await Task.Run(() =>
        {
            foreach (var rel in toRemove)
            {
                var full = Path.GetFullPath(Path.Combine(DirectoryRoot, rel));

                // These came from our own walk a moment ago, but this deletes recursively, so the
                // containment check is worth having where the mistake would be unbounded.
                if (!full.StartsWith(Path.GetFullPath(DirectoryRoot) + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add($"{rel}: not removed — it resolves outside the project folder.");
                    continue;
                }

                try
                {
                    if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
                    else if (File.Exists(full)) File.Delete(full);
                }
                catch (Exception ex)
                {
                    errors.Add($"{rel}: {ex.Message}");
                }
            }
        });

        StatusText = toRemove.Count == 1
            ? "1 leftover file tidied away"
            : $"{toRemove.Count} leftover files tidied away";
        if (errors.Count > 0) AppendError(string.Join("\n", errors));
    }

    /// <summary>
    /// Offers to take away what the generate found in _site but had no reason to put there. Asked
    /// rather than done, because removing files is the user's call — but only asked when there is
    /// something to ask about: a generate that changes nothing finds nothing.
    /// </summary>
    private async Task HandleOrphanFiles(string siteRoot, IReadOnlyList<string> orphans)
    {
        var toRemove = await AskAboutOrphans(orphans);
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
    /// they don't fit beside the other site settings the way a color or a title does.
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

        // No explicit save: each assignment above is an edit, and edits are written as they happen.
        // Saving again here wrote a fourth copy without updating the stamp that records what we last
        // wrote — so the next rescan decided the file had been edited by hand and replaced the
        // object the settings panel is bound to, which is the exact thing that stamp exists to stop.

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
            // The dialog wrote deploy: into the same file, through the same model object — so as far
            // as the next scan is concerned that write is ours, not a hand edit.
            MarkConfigWritten();
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
    partial void OnIsLoadingChanged(bool value)
    {
        RefreshSyncBlockedReason();

        // Every path that held the app busy ends here, which is what makes this the one place worth
        // asking whether anything is waiting.
        if (!value) DrainChangesThatArrivedMidRun();
    }

    /// <summary>
    /// Asks about anything an unattended run held back, before a deploy publishes it.
    /// </summary>
    /// <remarks>
    /// Auto-generate takes the Generate button away, so "you'll be asked next time you generate" has
    /// nowhere to land. Deploy is the better moment anyway: a leftover in <c>_site</c> matters
    /// precisely because it gets published — the sync walks that folder to build its manifest, so a
    /// file sitting there is uploaded and served exactly like a real page. Until then it is only
    /// visible to the local preview server, which is why holding it costs nothing.
    ///
    /// Declining is a real answer and is taken as one: the deploy goes ahead with the leftovers in
    /// place. They are offered again next deploy rather than nagged about in between, because the
    /// only thing that changed is that they are about to be published again.
    /// </remarks>
    private async Task SettlePendingLeftovers(string siteRoot)
    {
        if (PendingSiteOrphans.Count == 0) return;

        var pending = PendingSiteOrphans;
        PendingSiteOrphans = [];

        await HandleOrphanFiles(siteRoot, pending);
    }

    /// <summary>
    /// Puts the leftovers to the user and returns what they chose to remove, or null for none.
    /// </summary>
    /// <remarks>
    /// A seam, in the manner of <see cref="SourceListing"/>: a headless test has no window to parent
    /// a modal to, so without this the whole offer would quietly skip and a test asserting that the
    /// user gets asked would pass whether they did or not. What it gives up is evidence that the
    /// dialog itself is wired correctly, which <c>OrphanFilesViewTests</c> covers separately.
    /// </remarks>
    internal Func<IReadOnlyList<string>, Task<IReadOnlyList<string>?>> AskAboutOrphans { get; set; } =
        _ => Task.FromResult<IReadOnlyList<string>?>(null);

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

        // Before anything reaches the server, and before the plan is worked out — a plan approved
        // over files the user is about to remove is a plan for the wrong upload.
        await SettlePendingLeftovers(siteRoot);

        // A secret that exists but can't be read is not the same as no secret. Deploying anyway
        // would fail at the server as an opaque authentication error, telling the user nothing
        // about the real problem or how to fix it.
        var stored = DeployTargets.ReadSecret(CredentialStoreFactory.Create(), DirectoryRoot, target);
        if (stored.Status == CredentialStatus.Failed)
        {
            AppendError(stored.Error ?? "Could not read the saved secret for this deploy target.");
            return;
        }
        var secret = stored.Secret;
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
        MarkConfigWritten();
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