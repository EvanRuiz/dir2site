// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia.Controls;
using CommunityToolkit.Mvvm.Input;

namespace dir2site.ViewModels;

/// <summary>
/// Asks whether to restart into a freshly downloaded update. Only an explicit "Restart Now" returns
/// true — the close button and "Later" both decline, because restarting the app out from under
/// someone mid-edit is not something to do on an ambiguous answer.
/// </summary>
public partial class UpdateConfirmViewModel : ViewModelBase
{
    private readonly Window _window;

    public UpdateConfirmViewModel(Window window, string version)
    {
        _window = window;
        Version = version;
    }

    public string Version { get; }

    public string Title => $"Install v{Version}?";

    public string Explanation =>
        $"Version {Version} has finished downloading. dir2site needs to restart to install it. " +
        "Any unsaved work in the app should be saved first — you can install later from the " +
        "banner if now is a bad time.";

    /// <summary>What the user decided, or null while the dialog is still open.</summary>
    public bool? Answer { get; private set; }

    [RelayCommand]
    private void Restart()
    {
        Answer = true;
        _window.Close(true);
    }

    [RelayCommand]
    private void Later()
    {
        Answer = false;
        _window.Close(false);
    }
}
