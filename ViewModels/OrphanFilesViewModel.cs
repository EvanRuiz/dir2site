// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace dir2site.ViewModels;

/// <summary>
/// One file left over in _site, with a checkbox. Checked to begin with, unlike its counterpart in
/// the remote stale-files dialog: these are generated files that can be made again from the source
/// folder, and leaving them is what keeps deleted content on the published site.
/// </summary>
public partial class OrphanFileItem : ObservableObject
{
    public OrphanFileItem(string path) => Path = path;

    public string Path { get; }

    [ObservableProperty] private bool _isSelected = true;
}

/// <summary>
/// Lists files in _site that the last generate had no reason to put there — what a deleted or
/// renamed source leaves behind. Closing returns the chosen paths, or null when they're kept.
/// </summary>
public partial class OrphanFilesViewModel : ViewModelBase
{
    private readonly Window _window;

    public OrphanFilesViewModel(Window window, IEnumerable<string> orphanPaths)
    {
        _window = window;
        Items = new ObservableCollection<OrphanFileItem>(orphanPaths.Select(p => new OrphanFileItem(p)));
    }

    public ObservableCollection<OrphanFileItem> Items { get; }

    /// <summary>
    /// What the dialog closed with, alongside handing the same answer back through the window —
    /// null until <see cref="Decided"/>, and null after it when the files are being kept.
    /// </summary>
    public IReadOnlyList<string>? Chosen { get; private set; }

    public bool Decided { get; private set; }

    public string Headline => Items.Count == 1
        ? "1 file in your site no longer comes from anything in your folder."
        : $"{Items.Count} files in your site no longer come from anything in your folder.";

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
    private void RemoveSelected()
    {
        var selected = Items.Where(i => i.IsSelected).Select(i => i.Path).ToList();
        // Unticking everything and pressing Remove says the same thing as keeping them; an empty
        // list must never reach the caller, which would have to guess whether it meant "all".
        Chosen = selected.Count > 0 ? selected : null;
        Decided = true;
        _window.Close(Chosen);
    }

    [RelayCommand]
    private void Keep()
    {
        Decided = true;
        _window.Close(null);
    }
}
