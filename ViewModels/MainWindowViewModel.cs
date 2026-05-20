using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using dir2site.Models;
using dir2site.Services;
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
        _ = CheckForUpdatesAsync();
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartServerCommand))]
    private string? _directoryRoot;
    
    [ObservableProperty] public partial ObservableCollection<DirectoryTreeItem> DirItems { get; set; } = [];
    
    [ObservableProperty]
    private DirectoryTreeItem? _selectedItem;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GenerateSiteCommand))]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusText = "...";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartServerCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopServerCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenBrowserCommand))]
    private bool _isServerRunning;

    [ObservableProperty]
    private string _serverUrl = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GenerateSiteCommand))]
    [NotifyCanExecuteChangedFor(nameof(ChooseLogoCommand))]
    private Dir2SiteModel? _dir2SiteConfig;
    
    partial void OnDirectoryRootChanged(string? value)
    {
        if (_previewServer.IsRunning)
            _ = StopServer();
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

        if(folders.Count > 0)
        {
            DirectoryRoot = folders[0].Path.LocalPath;
        }
        else
        {
            DirectoryRoot = null;
        }

        await LoadDirectory();
    }
    
    private async Task LoadDirectory()
    {
        DirItems.Clear();
        if(DirectoryRoot == null) return;

        IsLoading = true;
        StatusText = "Scanning...";

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

        IsLoading = false;
        StatusText = $"{files.Count:N0} files · {artifacts.Count:N0} artifacts";
    }

    private async Task LoadOrCreateDir2SiteConfig()
    {
        if (DirectoryRoot == null) return;
        var configPath = Path.Combine(DirectoryRoot, "dir2site.yaml");

        if (File.Exists(configPath))
        {
            var yaml = await File.ReadAllTextAsync(configPath);
            Dir2SiteConfig = YamlParser.DeserializeAs<Dir2SiteModel>(yaml);
        }
        else
        {
            Dir2SiteConfig = new Dir2SiteModel
            {
                Title = Path.GetFileName(DirectoryRoot) is { Length: > 0 } n ? n : "My Site",
                Footer = $"© {DateTime.Now.Year}",
            };
            await File.WriteAllTextAsync(configPath, YamlParser.SerializeToYaml(Dir2SiteConfig));
        }
    }

    [RelayCommand(CanExecute = nameof(CanGenerateSite))]
    private async Task GenerateSite()
    {
        if (DirectoryRoot == null || DirItems.Count == 0 || Dir2SiteConfig == null) return;

        await File.WriteAllTextAsync(
            Path.Combine(DirectoryRoot, "dir2site.yaml"),
            YamlParser.SerializeToYaml(Dir2SiteConfig));

        IsLoading = true;
        var progress = new Progress<string>(msg => StatusText = msg);

        // Generate previews first so site settings (PDF resize/quality) affect output
        StatusText = "Generating previews...";
        var config = Dir2SiteConfig;
        var root   = DirItems[0];
        await Task.Run(() => DirectoryTraverser.GeneratePreviews(root, config, progress));

        StatusText = "Generating site...";
        var summary = await Task.Run(() =>
            SiteGenerator.Generate(DirectoryRoot, root, config, progress));

        IsLoading = false;
        StatusText = summary;
        StartServerCommand.NotifyCanExecuteChanged();
    }

    private bool CanGenerateSite() =>
        DirectoryRoot != null && DirItems.Count > 0 && Dir2SiteConfig != null && !IsLoading;

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