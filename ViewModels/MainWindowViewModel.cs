using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using dir2site.Models;
using dir2site.Services;

namespace dir2site.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    public TopLevel? TopLevel { get; set; }

    private readonly PreviewServerService _previewServer = new();

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
        StatusText = "Generating site...";
        var progress = new Progress<string>(msg => StatusText = msg);

        var summary = await Task.Run(() =>
            SiteGenerator.Generate(DirectoryRoot, DirItems[0], Dir2SiteConfig, progress));

        IsLoading = false;
        StatusText = summary;
        StartServerCommand.NotifyCanExecuteChanged();
    }

    private bool CanGenerateSite() =>
        DirectoryRoot != null && DirItems.Count > 0 && Dir2SiteConfig != null && !IsLoading;
}