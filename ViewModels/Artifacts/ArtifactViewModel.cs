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

    // Markdown live preview (runtime only) — populated when the artifact is a MarkdownPage.
    [ObservableProperty] public partial bool IsMarkdown { get; set; }
    public string? MarkdownFilePath { get; set; }

    private Bitmap? _markdownImage;
    private bool _markdownRendered;

    /// <summary>
    /// The article rendered to a bitmap by the Skia float-flow engine, produced on first access
    /// (no site generation required). Null when this artifact is not Markdown or rendering failed.
    /// </summary>
    public Bitmap? MarkdownImage
    {
        get
        {
            if (!IsMarkdown || MarkdownFilePath == null) return null;
            if (!_markdownRendered)
            {
                _markdownRendered = true;
                try
                {
                    var png = MarkdownPreviewRenderer.RenderArticlePng(MarkdownFilePath);
                    if (png != null) { using var ms = new MemoryStream(png); _markdownImage = new Bitmap(ms); }
                }
                catch { _markdownImage = null; }
            }
            return _markdownImage;
        }
    }

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
