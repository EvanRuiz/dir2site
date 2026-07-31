// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia.Controls;
using CommunityToolkit.Mvvm.Input;

namespace dir2site.ViewModels;

/// <summary>
/// A last check before something irreversible. Only the explicit confirm button returns true —
/// Cancel and the window's close button both decline.
/// </summary>
public partial class ConfirmViewModel : ViewModelBase
{
    private readonly Window _window;

    public ConfirmViewModel(Window window, string heading, string detail, string confirmText)
    {
        _window = window;
        Heading = heading;
        Detail = detail;
        ConfirmText = confirmText;
    }

    public string Heading { get; }
    public string Detail { get; }
    public string ConfirmText { get; }

    /// <summary>What the user decided, or null while the dialog is still open.</summary>
    public bool? Answer { get; private set; }

    [RelayCommand]
    private void Confirm()
    {
        Answer = true;
        _window.Close(true);
    }

    [RelayCommand]
    private void Cancel()
    {
        Answer = false;
        _window.Close(false);
    }
}
