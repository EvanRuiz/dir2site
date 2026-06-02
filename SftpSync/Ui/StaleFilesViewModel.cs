// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using dir2site.ViewModels;

namespace dir2site.SftpSync.Ui;

/// <summary>One stale remote file with a checkbox (unchecked by default).</summary>
public partial class StaleFileItem : ObservableObject
{
    public StaleFileItem(string path) => Path = path;

    public string Path { get; }

    [ObservableProperty] private bool _isSelected;
}

/// <summary>
/// Lists remote files that no longer exist locally. The user may select some and delete them,
/// or ignore. Closing returns the chosen paths (or null when ignored).
/// </summary>
public partial class StaleFilesViewModel : ViewModelBase
{
    private readonly Window _window;

    public StaleFilesViewModel(Window window, IEnumerable<string> stalePaths)
    {
        _window = window;
        Items = new ObservableCollection<StaleFileItem>(stalePaths.Select(p => new StaleFileItem(p)));
    }

    public ObservableCollection<StaleFileItem> Items { get; }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var item in Items) item.IsSelected = true;
    }

    [RelayCommand]
    private void DeselectAll()
    {
        foreach (var item in Items) item.IsSelected = false;
    }

    [RelayCommand]
    private void DeleteSelected()
    {
        var selected = Items.Where(i => i.IsSelected).Select(i => i.Path).ToList();
        _window.Close(selected.Count > 0 ? (IReadOnlyList<string>)selected : null);
    }

    [RelayCommand]
    private void Ignore() => _window.Close(null);
}
