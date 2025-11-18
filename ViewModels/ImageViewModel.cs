using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MsBox.Avalonia;
using MsBox.Avalonia.Enums;
using WebViewControl;

namespace OpenSeadragonOverlayEditor.ViewModels;

public partial class ImageViewModel : ViewModelBase
{
    private TopLevel? _topLevel;
    private WebView? _webView;

    public ImageViewModel()
    {
    }

    public ImageViewModel(TopLevel topLevel, WebView view)
    {
        _topLevel = topLevel;
        _webView = view;
    }

    [ObservableProperty]
    private IStorageFile? _imageFile;
    
    [ObservableProperty]
    private string? _imagePath;

    [ObservableProperty]
    private string? _tilesName = "mydz";
    
    [ObservableProperty]
    private double _qualityLevel = 80;
    
    private async Task<string?> GetPreviewPath()
    {
        if(ImageFile == null) return null;
        
        var parentFolder = await ImageFile.GetParentAsync();
        var parentPath = parentFolder?.TryGetLocalPath();
        if(parentPath == null) return null;
    
        var previewPath = Path.Combine(parentPath, $"{ImageFile.Name}-preview");
        Directory.CreateDirectory(previewPath);

        return previewPath;
    }

   
    
    [RelayCommand]
    private async Task OpenImageFile()
    {
        if(_topLevel == null) return;
        if(!_topLevel.StorageProvider.CanOpen) return;

        var files = await _topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions()
        {
            Title = "Open Image File",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Images")
                {
                    Patterns = ["*.jpg", "*.jpeg", "*.png", "*.tif", "*.bmp", "*.webp"]
                }
            ]
        });

        if(files == null || !files.Any())
        {
            ImagePath = null;
            ImageFile = null;
            return;
        }

        ImageFile = files[0];
        ImagePath = ImageFile.Path.AbsolutePath;
    }

    [RelayCommand]
    private async Task GenerateTiles()
    {
        if(ImageFile == null || ImagePath == null) return;
        
        using var vips = NetVips.Image.NewFromFile(ImagePath);

        var q = (int)QualityLevel;

        var previewPath = await GetPreviewPath();
        if(previewPath == null) return;
        
        var tilesPath = Path.Combine(previewPath, "tiles");
        Directory.CreateDirectory(tilesPath);
        
        Directory.SetCurrentDirectory(tilesPath);
        
        await Task.Run(() =>
        {
            vips.Dzsave(TilesName, suffix: $".webp[Q={q}]");
        });

        var box = MessageBoxManager.GetMessageBoxStandard("Tiles Generated", "Tiles Generated Successfully.", ButtonEnum.Ok);
        await box.ShowAsync();


    }

    [RelayCommand]
    private async Task GeneratePreview()
    {
        if(ImageFile == null) return;
        if(_webView == null) return;
        
        var previewPath = await GetPreviewPath();
        if(previewPath == null) return;
        
        await CopyOpenSeagdragonAssets(previewPath);
        
        var htmlPath = Path.Combine(previewPath, "index.html");
        var html = @"
<html>
<head>
     <script src=""openseadragon/openseadragon.min.js""></script>
</head>
<body>
    <div id=""openseadragon-view"">
        <script>
            OpenSeadragon({
                id:            ""openseadragon-view"",
                prefixUrl:     ""openseadragon/images/"",
                tileSources:   [
                    ""tiles/mydz.dzi""
                ]
            });
        </script>
        <noscript>
            <p>OpenSeadragon is not available unless JavaScript is enabled.</p>
        </noscript>
    </div>
</body>
</html>
";
        File.WriteAllText(htmlPath, html);
        
        var uri = new Uri(htmlPath);
        var uriString = uri.AbsoluteUri;
        _webView.Address = uriString;
    }
    
    private async Task CopyOpenSeagdragonAssets(string parentPath)
    {
        if(ImageFile == null) return;
        
        var assets = AssetLoader.GetAssets(new Uri($"avares://OpenSeadragonOverlayEditor/Assets/openseadragon/"), null);

        foreach(var asset in assets)
        {
            // Get the relative path of the asset
            var relativePath = asset.AbsolutePath.Replace("/Assets/openseadragon/", "openseadragon/");
            var destinationPath = Path.Combine(parentPath, relativePath);
    
            // Create subdirectories if needed
            var destinationDir = Path.GetDirectoryName(destinationPath);
            if(destinationDir != null)
            {
                Directory.CreateDirectory(destinationDir);
            }
    
            await using var stream = AssetLoader.Open(asset);
            await using var fileStream = File.Create(destinationPath);
            await stream.CopyToAsync(fileStream);
        }
    }
}