using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
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
    private Canvas? _overlayCanvas;
    private Image? _overlayCanvasImage;

    private IBrush _defaultFill = new SolidColorBrush(Color.FromArgb(128, 0, 0, 0));
    private IBrush _defaultStroke = new SolidColorBrush(Color.FromArgb(200, 0, 0, 0));
    private int _defaultThickness = 2;

    public ImageViewModel()
    {
    }

    public ImageViewModel(TopLevel topLevel, WebView view, Canvas overlayCanvas, Image overlayCanvasImage)
    {
        _topLevel = topLevel;
        _webView = view;
        _overlayCanvas = overlayCanvas;
        _overlayCanvasImage = overlayCanvasImage;
    }

    [ObservableProperty]
    private IStorageFile? _imageFile;
    
    [ObservableProperty]
    private string? _imagePath;
    
    [ObservableProperty]
    private Bitmap? _imageBitmap;

    [ObservableProperty]
    private string? _tilesName = "mydz";
    
    [ObservableProperty]
    private double _qualityLevel = 80;

    [ObservableProperty]
    private ObservableCollection<OverlayViewModel> _overlays = [];
    
    [ObservableProperty]
    private double _defaultOverlayRadius = 50;
    
    [ObservableProperty]
    private OverlayViewModel? _selectedOverlay;
    
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

        ImageBitmap = new Bitmap(ImagePath);
        await LoadOverlays();
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
    
    private class OverlayJson
    {
        public string? caption { get; set; }
        public double x { get; set; }
        public double y { get; set; }
        public double width { get; set; }
        public double height { get; set; }
    }

    [RelayCommand]
    private async Task SaveOverlays()
    {
        var previewPath = await GetPreviewPath();
        if(previewPath == null) return;
        
        if(_overlayCanvas == null || _overlayCanvasImage == null) return;
        
        // Openseadragon overlay bounds are in viewpoint coordinates in percentage of the image width only
        var overlaysForJson = Overlays.Select(o => new OverlayJson
        {
            caption = o.Caption,
            x = (o.Left - _overlayCanvasImage.Bounds.Left) / _overlayCanvasImage.Bounds.Width,
            y = (o.Top - _overlayCanvasImage.Bounds.Top) / _overlayCanvasImage.Bounds.Width,
            width = o.Radius * 2 / _overlayCanvasImage.Bounds.Width,
            height = o.Radius * 2 / _overlayCanvasImage.Bounds.Width,
        }).ToList();

        var json = JsonSerializer.Serialize(overlaysForJson);
        
        var overlaysJsonPath = Path.Combine(previewPath, "overlays.json");
        await File.WriteAllTextAsync(overlaysJsonPath, json);
    }
    
    [RelayCommand]
    private async Task LoadOverlays()
    {
        Overlays.Clear();
        if(_overlayCanvasImage == null) return;
        
        var previewPath = await GetPreviewPath();
        if(previewPath == null) return;
        
        var overlaysJsonPath = Path.Combine(previewPath, "overlays.json");
        if(!File.Exists(overlaysJsonPath)) return;

        var overlaysJsonString = await File.ReadAllTextAsync(overlaysJsonPath);
        
        var overlaysJson = JsonSerializer.Deserialize<List<OverlayJson>>(overlaysJsonString);
        if(overlaysJson == null) return;
        
        // Openseadragon overlay bounds are in viewpoint coordinates in percentage of the image width only
        Overlays = new ObservableCollection<OverlayViewModel>(overlaysJson.Select(o => new OverlayViewModel
        {
            Radius = o.width * _overlayCanvasImage.Bounds.Width / 2,
            X = (o.x + o.width / 2) * _overlayCanvasImage.Bounds.Width + _overlayCanvasImage.Bounds.Left,
            Y = (o.y + o.height / 2) * _overlayCanvasImage.Bounds.Width + _overlayCanvasImage.Bounds.Top,
            Caption = o.caption,
            Fill = _defaultFill,
            Stroke = _defaultStroke,
            StrokeThickness = _defaultThickness
        }).ToList());

        foreach(var o in Overlays)
        {
            o.UpdateBounds();
        }
    }

    [RelayCommand]
    private async Task GeneratePreview()
    {
        if(ImageFile == null) return;
        if(_webView == null) return;
        
        var previewPath = await GetPreviewPath();
        if(previewPath == null) return;
        
        await SaveOverlays();
        await CopyPreviewAssets(previewPath);
        
        var htmlPath = Path.Combine(previewPath, "index.html");
        
        var uri = new Uri(htmlPath);
        var uriString = uri.AbsoluteUri;
        _webView.Address = uriString;
    }

    [RelayCommand]
    private void DeleteSelectedOverlay()
    {
        if(SelectedOverlay == null) return;
        Overlays.Remove(SelectedOverlay);
        SelectedOverlay = null;
    }
    
    private async Task CopyPreviewAssets(string parentPath)
    {
        if(ImageFile == null) return;
        
        var assets = AssetLoader.GetAssets(new Uri($"avares://OpenSeadragonOverlayEditor/Assets/preview/"), null);
        foreach(var asset in assets)
        {
            // Get the relative path of the asset
            var relativePath = asset.AbsolutePath.Replace("/Assets/preview/", "");
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

    public void OnOverlayCanvasPressed(double x, double y)
    {
        Console.WriteLine($"Canvas clicked at: X={x}, Y={y}");

        var overlay = new OverlayViewModel
        {
            X = x,
            Y = y,
            Radius = DefaultOverlayRadius,
            Fill = _defaultFill,
            Stroke = _defaultStroke,
            StrokeThickness = _defaultThickness
        };
        
        Overlays.Add(overlay);
    }
}