// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using dir2site.SftpSync.Core;
using dir2site.ViewModels;

namespace dir2site.SftpSync.Ui;

/// <summary>
/// Lets the user find a deploy directory by looking at the server instead of typing a path from
/// memory. A mistyped remote path was the most common way a deploy target was wrong, and nothing
/// caught it until the first real deploy.
/// </summary>
public partial class RemoteBrowseViewModel : ViewModelBase
{
    private readonly Window _window;
    private readonly SftpProfile _profile;
    private readonly string? _secret;
    private readonly IHostKeyVerifier? _verifier;

    public ObservableCollection<string> Directories { get; } = [];

    public RemoteBrowseViewModel(Window window, SftpProfile profile, string? secret, IHostKeyVerifier? verifier)
    {
        _window = window;
        _profile = profile;
        _secret = secret;
        _verifier = verifier;

        // Start where the profile points, or wherever the server drops us if that's empty.
        _ = LoadAsync(profile.RemotePath);
    }

    [ObservableProperty] private string _currentPath = string.Empty;
    [ObservableProperty] private string? _selectedDirectory;
    [ObservableProperty] private string _status = string.Empty;
    [ObservableProperty] private bool _isBusy;

    /// <summary>False at the filesystem root, where there is nowhere further up to go.</summary>
    public bool CanGoUp => CurrentPath.Length > 1 && CurrentPath != "/";

    partial void OnCurrentPathChanged(string value) => OnPropertyChanged(nameof(CanGoUp));

    private async Task LoadAsync(string path)
    {
        IsBusy = true;
        Status = "Listing…";
        try
        {
            var listing = await Task.Run(
                () => SftpSyncService.ListDirectories(_profile, _secret, path, _verifier));

            CurrentPath = listing.Path;
            Directories.Clear();
            foreach (var d in listing.Directories) Directories.Add(d);
            Status = Directories.Count == 0 ? "No subfolders here." : string.Empty;
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
    private Task Open()
    {
        if (string.IsNullOrEmpty(SelectedDirectory)) return Task.CompletedTask;
        var next = CurrentPath.TrimEnd('/') + "/" + SelectedDirectory;
        SelectedDirectory = null;
        return LoadAsync(next);
    }

    [RelayCommand]
    private Task GoUp()
    {
        var trimmed = CurrentPath.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        var parent = slash <= 0 ? "/" : trimmed[..slash];
        return LoadAsync(parent);
    }

    [RelayCommand]
    private Task Refresh() => LoadAsync(CurrentPath);

    [RelayCommand]
    private async Task NewFolder()
    {
        if (string.IsNullOrWhiteSpace(NewFolderName))
        {
            Status = "Type a name for the new folder.";
            return;
        }

        IsBusy = true;
        try
        {
            var name = NewFolderName.Trim();
            await Task.Run(() => SftpSyncService.CreateRemoteDirectory(
                _profile, _secret, CurrentPath, name, _verifier));
            NewFolderName = string.Empty;
            await LoadAsync(CurrentPath);
            SelectedDirectory = name;
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

    [ObservableProperty] private string _newFolderName = string.Empty;

    /// <summary>What Choose settled on, or null if the dialog was cancelled.</summary>
    public string? ChosenPath { get; private set; }

    /// <summary>Returns the chosen path — the folder highlighted, or the one being viewed.</summary>
    [RelayCommand]
    private void Choose()
    {
        ChosenPath = string.IsNullOrEmpty(SelectedDirectory)
            ? CurrentPath
            : CurrentPath.TrimEnd('/') + "/" + SelectedDirectory;
        _window.Close(ChosenPath);
    }

    [RelayCommand]
    private void Cancel() => _window.Close(null);
}
