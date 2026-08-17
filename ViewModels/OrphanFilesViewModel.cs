// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace dir2site.ViewModels;

/// <summary>Where the leftovers were found, which decides how the dialog describes them.</summary>
public enum OrphanKind
{
    /// <summary>Pages and assets in <c>_site</c> — published, and reachable by anyone with the URL.</summary>
    Site,

    /// <summary>Sidecars and <c>.dir2site</c> folders in the project — clutter, never published.</summary>
    Source,
}

/// <summary>
/// One leftover file, with a checkbox.
/// </summary>
/// <remarks>
/// Ticked to begin with only when we could make it again. That is true of everything in
/// <c>_site</c> and of a <c>.dir2site</c> preview folder — all of it is output, and leaving it is
/// what keeps deleted content on the published site. It is emphatically not true of a sidecar: the
/// caption, credit and date in it were typed by a person, and if the artifact was renamed rather
/// than deleted that file is the only surviving copy of them. Removing it is still offered, because
/// after a real deletion it is just clutter — but not on a default nobody chose.
/// </remarks>
public partial class OrphanFileItem : ObservableObject
{
    public OrphanFileItem(string path, bool selected = true)
    {
        Path = path;
        _isSelected = selected;
    }

    public string Path { get; }

    [ObservableProperty] private bool _isSelected;
}

/// <summary>
/// Lists files in _site that the last generate had no reason to put there — what a deleted or
/// renamed source leaves behind. Closing returns the chosen paths, or null when they're kept.
/// </summary>
public partial class OrphanFilesViewModel : ViewModelBase
{
    private readonly Window _window;

    private readonly OrphanKind _kind;

    public OrphanFilesViewModel(
        Window window, IEnumerable<string> orphanPaths, OrphanKind kind = OrphanKind.Site)
    {
        _window = window;
        _kind = kind;
        Items = new ObservableCollection<OrphanFileItem>(
            orphanPaths.Select(p => new OrphanFileItem(p, selected: CanBeMadeAgain(p))));
    }

    public ObservableCollection<OrphanFileItem> Items { get; }

    /// <summary>
    /// Whether losing this file would cost the user anything they can't get back.
    /// </summary>
    /// <remarks>
    /// Only a sidecar fails this, and only in the project folder — everything else here is
    /// generated. See <see cref="OrphanFileItem"/> for why that decides the tick.
    /// </remarks>
    private static bool CanBeMadeAgain(string path) =>
        !path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
        && !path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// What the dialog closed with, alongside handing the same answer back through the window —
    /// null until <see cref="Decided"/>, and null after it when the files are being kept.
    /// </summary>
    public IReadOnlyList<string>? Chosen { get; private set; }

    public bool Decided { get; private set; }

    /// <summary>
    /// Which of the two kinds of leftover this is. They read very differently to someone deciding:
    /// a page in the published site is content visitors can still reach, whereas a stray sidecar is
    /// invisible clutter — and the second is much less alarming to be asked about.
    /// </summary>
    public string Headline => (_kind, Items.Count) switch
    {
        (OrphanKind.Site, 1) => "1 file in your site no longer comes from anything in your folder.",
        (OrphanKind.Site, _) => $"{Items.Count} files in your site no longer come from anything in your folder.",
        (_, 1) => "1 settings or preview file is left over from something no longer in your folder.",
        _ => $"{Items.Count} settings and preview files are left over from things no longer in your folder.",
    };

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
