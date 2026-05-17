using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using dir2site.Services;

namespace dir2site.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    public TopLevel? TopLevel { get; set; }

    [ObservableProperty]
    private string? _directoryRoot;
    
    [ObservableProperty] public partial ObservableCollection<DirectoryTreeItem> DirItems { get; set; } = [];
    
    [ObservableProperty]
    private DirectoryTreeItem? _selectedItem;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusText = "...";
    
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

        var (root, files) = await Task.Run(() =>
        {
            var collected = new List<string>();
            var tree = DirectoryTraverser.BuildTree(DirectoryRoot, collected, progress);
            return (tree, collected);
        });

        DirItems.Add(root);

        IsLoading = false;
        StatusText = $"{files.Count:N0} files loaded";
    }
}