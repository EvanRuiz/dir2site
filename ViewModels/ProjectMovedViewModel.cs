// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.IO;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace dir2site.ViewModels;

/// <summary>What the user chose to do about a project folder that has moved or gone.</summary>
public enum ProjectMovedAnswer
{
    /// <summary>Work on it where it is now.</summary>
    Follow,

    /// <summary>It is back where it was — carry on with the same path.</summary>
    StayedPut,

    /// <summary>Let it go and return to the launch screen.</summary>
    Close,
}

/// <summary>
/// Asks what to do about a project folder that has been renamed, moved or deleted.
/// </summary>
/// <remarks>
/// A question rather than a rule, because the three answers are all reasonable and which one is
/// right is not something the app can know. Renaming a folder to tidy it up wants following;
/// renaming it by accident wants putting back; a folder deleted on purpose wants closing. Guessing
/// would be wrong a third of the time in a way that writes to disk.
/// </remarks>
public partial class ProjectMovedViewModel : ViewModelBase
{
    private readonly Window _window;
    private readonly string _oldPath;
    private readonly string? _newPath;

    public ProjectMovedViewModel(Window window, string oldPath, string? newPath)
    {
        _window  = window;
        _oldPath = oldPath;
        _newPath = newPath;
    }

    /// <summary>
    /// Offered only when we know where it went.
    /// </summary>
    /// <remarks>
    /// A rename hands over the new name; a folder deleted, or moved somewhere the watch has no view
    /// of, does not. Showing the button anyway and failing on it would be offering something we
    /// cannot do.
    /// </remarks>
    public bool CanFollow => _newPath != null;

    public string Headline => _newPath == null
        ? "Your project folder is no longer there."
        : "Your project folder has moved.";

    public string Detail => _newPath == null
        ? $"{_oldPath} has been renamed, moved or deleted. Nothing has been changed, and anything " +
          "that was running has stopped."
        : $"It was {_oldPath}, and is now {_newPath}. Nothing has been changed, and anything that " +
          "was running has stopped.";

    public string FollowLabel => _newPath == null
        ? "Follow It"
        : $"Work on {Path.GetFileName(_newPath)}";

    /// <summary>
    /// Said only after Try Again has been pressed and the folder still isn't there.
    /// </summary>
    /// <remarks>
    /// Empty until then: an explanation of a failure that hasn't happened reads as a warning about
    /// the button rather than as the answer to pressing it.
    /// </remarks>
    [ObservableProperty] private string _retryMessage = string.Empty;

    public ProjectMovedAnswer? Chosen { get; private set; }

    [RelayCommand]
    private void Follow()
    {
        if (_newPath == null) return;
        Close(ProjectMovedAnswer.Follow);
    }

    /// <summary>
    /// Checks whether the folder is back, and stays open when it isn't.
    /// </summary>
    /// <remarks>
    /// The one answer the app can verify, so it does. Closing on the press and finding out later
    /// would put the user back in front of a window pointed at nothing, with no way to tell that
    /// from having been believed. It checks and nothing else — putting the folder back does not
    /// mean the user wants a generate, and starting one on their behalf here would be answering a
    /// question they weren't asked.
    /// </remarks>
    [RelayCommand]
    private void TryAgain()
    {
        if (Directory.Exists(_oldPath))
        {
            Close(ProjectMovedAnswer.StayedPut);
            return;
        }

        RetryMessage = $"Still nothing at {_oldPath}.";
    }

    [RelayCommand]
    private void CloseProject() => Close(ProjectMovedAnswer.Close);

    private void Close(ProjectMovedAnswer answer)
    {
        Chosen = answer;
        _window.Close(answer);
    }
}
