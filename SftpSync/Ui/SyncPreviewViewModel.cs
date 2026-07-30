// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.ObjectModel;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.Input;
using dir2site.SftpSync.Core;
using dir2site.ViewModels;

namespace dir2site.SftpSync.Ui;

/// <summary>
/// Shows what a deploy is about to do. The diff was always computed; it just went straight into
/// action without anyone being given the chance to look at it first.
/// </summary>
public partial class SyncPreviewViewModel : ViewModelBase
{
    private readonly Window _window;

    public SyncPreviewViewModel(Window window, SyncPlan plan)
    {
        _window = window;
        Summary = plan.Summary;
        Note = plan.Note;
        CanDeploy = !plan.IsEmpty;
        HasStale = plan.StaleRemote.Count > 0;
        StaleHeading =
            $"{plan.StaleRemote.Count} file(s) on the server are no longer in your site. " +
            "Deploying leaves them alone — you'll be asked about removing them afterwards.";

        foreach (var f in plan.ToUpload) Uploads.Add(f);
    }

    public ObservableCollection<string> Uploads { get; } = [];
    public string Summary { get; }
    public string Note { get; }
    public bool CanDeploy { get; }
    public bool HasStale { get; }
    public string StaleHeading { get; }

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
