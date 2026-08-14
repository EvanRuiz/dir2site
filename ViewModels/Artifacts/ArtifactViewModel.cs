// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.IO;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using dir2site.Services;

namespace dir2site.ViewModels;

public partial class ArtifactViewModel : ViewModelBase
{
    [ObservableProperty] public partial string Id { get; set; } = string.Empty;
    [ObservableProperty] public partial string? Caption { get; set; }
    [ObservableProperty] public partial string? Credit { get; set; }
    [ObservableProperty] public partial string? Url { get; set; }
    [ObservableProperty] public partial string? UrlText { get; set; }
    [ObservableProperty] public partial string? Date { get; set; }

    /// <summary>
    /// What the pane shows for the source link. A url with no text falls back to the address, the
    /// same way the generated page does; text with no url is a video, whose address lives in the
    /// .url shortcut rather than the yaml.
    /// </summary>
    public string? UrlDisplay => (Url, UrlText) switch
    {
        ({ Length: > 0 } url, { Length: > 0 } text) => $"{text} — {url}",
        ({ Length: > 0 } url, _)                    => url,
        (_, { Length: > 0 } text)                   => text,
        _                                           => null,
    };

    partial void OnUrlChanged(string? value) => OnPropertyChanged(nameof(UrlDisplay));
    partial void OnUrlTextChanged(string? value) => OnPropertyChanged(nameof(UrlDisplay));
    [ObservableProperty] public partial string? Preview { get; set; }
    [ObservableProperty] public partial string? PreviewLarge { get; set; }

    // Runtime only
    [ObservableProperty] public partial string? RootFolder { get; set; }
    [ObservableProperty] public partial string? TraversalRoot { get; set; }

    // Markdown live preview (runtime only) — populated when the artifact is a MarkdownPage.
    [ObservableProperty] public partial bool IsMarkdown { get; set; }
    public string? MarkdownFilePath { get; private set; }

    /// <summary>
    /// The article rendered to a bitmap by the Skia float-flow engine (no site generation
    /// required). Null until <see cref="BeginMarkdownRender"/> completes, and for anything that is
    /// not Markdown or that failed to render; the view binds to it and fills in when it changes.
    /// </summary>
    [ObservableProperty] public partial Bitmap? MarkdownImage { get; set; }

    private bool _markdownRenderStarted;

    /// <summary>
    /// Marks this artifact as Markdown and renders its preview on a background thread.
    /// Laying an article out reads the file, decodes its lead image and runs the whole Skia flow —
    /// far too much to do inside a property getter reached from a binding, which froze the window
    /// while a long article was selected. Failures are swallowed: the pane simply stays empty.
    /// </summary>
    public void BeginMarkdownRender(string markdownFilePath)
    {
        MarkdownFilePath = markdownFilePath;
        IsMarkdown = true;

        if (_markdownRenderStarted) return;
        _markdownRenderStarted = true;

        Task.Run(() =>
        {
            byte[]? png;
            try { png = MarkdownPreviewRenderer.RenderArticlePng(markdownFilePath); }
            catch { return; }
            if (png == null) return;

            Dispatcher.UIThread.Post(() =>
            {
                try { using var ms = new MemoryStream(png); MarkdownImage = new Bitmap(ms); }
                catch { /* an undecodable render is not worth tearing the app down for */ }
            });
        });
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
