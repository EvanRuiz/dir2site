// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.IO;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using dir2site.Services;

namespace dir2site.ViewModels;

public partial class ArtifactViewModel : ViewModelBase
{
    [ObservableProperty] public partial string Id { get; set; } = string.Empty;
    [ObservableProperty] public partial string? Caption { get; set; }
    [ObservableProperty] public partial string? Credit { get; set; }
    [ObservableProperty] public partial string? UrlText { get; set; }
    [ObservableProperty] public partial string? Date { get; set; }
    [ObservableProperty] public partial string? Preview { get; set; }
    [ObservableProperty] public partial string? PreviewLarge { get; set; }

    // Runtime only
    [ObservableProperty] public partial string? RootFolder { get; set; }
    [ObservableProperty] public partial string? TraversalRoot { get; set; }

    public string? PreviewPath => TraversalRoot == null || RootFolder == null || Preview == null
        ? null
        : PreviewGenerator.ResolvePreviewPath(TraversalRoot, RootFolder, Preview);

    public string? PreviewLargePath => TraversalRoot == null || RootFolder == null || PreviewLarge == null
        ? null
        : PreviewGenerator.ResolvePreviewPath(TraversalRoot, RootFolder, PreviewLarge);

    public Bitmap? PreviewBitmap
    {
        get
        {
            var path = PreviewPath;
            if (path == null || !File.Exists(path)) return null;
            try { return new Bitmap(path); }
            catch { return null; }
        }
    }
}
