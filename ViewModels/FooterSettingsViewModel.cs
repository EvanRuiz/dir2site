// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using dir2site.Models;

namespace dir2site.ViewModels;

/// <summary>
/// One footer row while it is being edited.
/// </summary>
/// <remarks>
/// A wrapper rather than binding <see cref="FooterItem"/> directly: that is a plain serialization
/// model with no change notification, so "Choose link…" writing a path into it would update the
/// yaml and leave the box on screen showing the old value.
/// </remarks>
public partial class FooterItemRow : ObservableObject
{
    [ObservableProperty] private int _column = 1;
    [ObservableProperty] private string _icon = string.Empty;
    [ObservableProperty] private string _iconColor = string.Empty;
    [ObservableProperty] private string _iconBackground = string.Empty;
    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _link = string.Empty;
    [ObservableProperty] private string _note = string.Empty;

    public static FooterItemRow From(FooterItem item) => new()
    {
        Column = item.Column,
        Icon = item.Icon ?? string.Empty,
        IconColor = item.IconColor ?? string.Empty,
        IconBackground = item.IconBackground ?? string.Empty,
        Title = item.Title ?? string.Empty,
        Link = item.Link ?? string.Empty,
        Note = item.Note ?? string.Empty,
    };

    public FooterItem ToItem() => new()
    {
        Column = Column,
        Icon = Icon.Trim(),
        IconColor = IconColor.Trim(),
        IconBackground = IconBackground.Trim(),
        Title = Title.Trim(),
        Link = Link.Trim(),
        Note = Note.Trim(),
    };
}

/// <summary>
/// Edits the footer's rows. They live in <c>dir2site.yaml</c> like every other site setting, but a
/// list of records doesn't fit beside the single-value fields in the main window, so it gets a
/// dialog of its own — the same shape the deploy targets use.
/// </summary>
public partial class FooterSettingsViewModel : ViewModelBase
{
    private readonly Window _window;
    private readonly string _projectRoot;

    /// <summary>The rows being edited. Order within a column is the order shown.</summary>
    public ObservableCollection<FooterItemRow> Items { get; } = [];

    /// <summary>
    /// What an empty <see cref="FooterColor"/> actually produces, shown as the box's placeholder.
    /// A fixed example there claimed a default the site does not have.
    /// </summary>
    public string FooterColorPlaceholder { get; }

    public FooterSettingsViewModel(Window window, string projectRoot, Dir2SiteModel config)
    {
        _window = window;
        _projectRoot = projectRoot;
        _footerText = config.Footer;
        _footerColor = config.FooterColor;
        FooterColorPlaceholder = config.PrimaryColor;

        // Copies, so Cancel really cancels — the config keeps its own list until Save.
        foreach (var item in config.FooterItems) Items.Add(FooterItemRow.From(item));

        _selectedItem = Items.FirstOrDefault();
    }

    /// <summary>
    /// The closing line under the columns, usually the copyright. The one footer field that is
    /// written to the page as raw HTML, so it can hold a link or a line break.
    /// </summary>
    [ObservableProperty] private string _footerText;

    /// <summary>Empty follows the primary color, which is what an unconfigured project wants.</summary>
    [ObservableProperty] private string _footerColor;

    [ObservableProperty] private FooterItemRow? _selectedItem;

    partial void OnSelectedItemChanged(FooterItemRow? value)
    {
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
        RemoveItemCommand.NotifyCanExecuteChanged();
        ChooseLinkCommand.NotifyCanExecuteChanged();
        SetWebLinkCommand.NotifyCanExecuteChanged();
        SetMailtoLinkCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void AddItem()
    {
        // Lands in the column the user was last looking at, which is nearly always the one they
        // meant to add to.
        var row = new FooterItemRow { Column = SelectedItem?.Column ?? 1 };
        Items.Add(row);
        SelectedItem = row;
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void RemoveItem()
    {
        if (SelectedItem is not { } row) return;
        var index = Items.IndexOf(row);
        Items.Remove(row);
        SelectedItem = Items.Count == 0 ? null : Items[Math.Min(index, Items.Count - 1)];
    }

    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private void MoveUp() => Move(-1);

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private void MoveDown() => Move(1);

    private void Move(int delta)
    {
        if (SelectedItem is not { } row) return;
        var from = Items.IndexOf(row);
        var to = from + delta;
        if (to < 0 || to >= Items.Count) return;
        Items.Move(from, to);
        SelectedItem = row;
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
    }

    private bool HasSelection() => SelectedItem != null;
    private bool CanMoveUp() => SelectedItem != null && Items.IndexOf(SelectedItem) > 0;
    private bool CanMoveDown() => SelectedItem != null && Items.IndexOf(SelectedItem) < Items.Count - 1;

    /// <summary>
    /// Puts the row on the web-address branch, keeping whatever address was already typed.
    /// </summary>
    /// <remarks>
    /// The three link forms are told apart by how the string starts, which is a rule you have to
    /// know before the box helps you. These two buttons and the artifact picker are that rule made
    /// visible: one per form, so the shape is chosen rather than remembered.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void SetWebLink()
    {
        if (SelectedItem is not { } row) return;
        row.Link = "https://" + WithoutScheme(row.Link);
        Status = "Type the rest of the address after https://";
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void SetMailtoLink()
    {
        if (SelectedItem is not { } row) return;
        row.Link = "mailto:" + WithoutScheme(row.Link);
        Status = "Type the address after mailto:";
    }

    // What was typed, with any scheme this dialog might have put there taken back off, so switching
    // between the two doesn't stack them up as "https://mailto:someone@example.org".
    private static string WithoutScheme(string link)
    {
        var rest = (link ?? string.Empty).Trim();
        foreach (var scheme in (string[])["https://", "http://", "mailto:"])
        {
            if (rest.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
                return rest[scheme.Length..];
        }
        return rest.TrimStart('/');
    }

    /// <summary>
    /// Picks the artifact a row points at, and stores it the way the yaml wants it: a path relative
    /// to the project, so the file stays portable between machines.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task ChooseLink()
    {
        if (SelectedItem is not { } row) return;

        var start = await _window.StorageProvider.TryGetFolderFromPathAsync(_projectRoot);
        var files = await _window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose the page this footer link opens",
            AllowMultiple = false,
            SuggestedStartLocation = start,
        });

        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;

        var relative = Path.GetRelativePath(_projectRoot, path).Replace('\\', '/');
        if (relative.StartsWith("..", StringComparison.Ordinal))
        {
            Status = "That file is outside the project, so the site can't link to it.";
            return;
        }

        row.Link = relative;
        if (row.Title.Length == 0) row.Title = Path.GetFileNameWithoutExtension(path);
        Status = string.Empty;
    }

    [ObservableProperty] private string _status = string.Empty;

    [RelayCommand]
    private void Save() =>
        _window.Close(new FooterSettingsResult(
            FooterText,
            FooterColor.Trim(),
            [.. Items.Select(row => row.ToItem())]));

    [RelayCommand]
    private void Cancel() => _window.Close(null);
}

/// <summary>What the dialog hands back. Null instead means the user cancelled.</summary>
public sealed record FooterSettingsResult(
    string FooterText, string FooterColor, IReadOnlyList<FooterItem> Items);
