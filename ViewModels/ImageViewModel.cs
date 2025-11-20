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
    private TabControl? _tabControl;
    private WebView? _webView;
    private Canvas? _overlayCanvas;
    private Image? _overlayCanvasImage;
    
    public ImageViewModel()
    {
    }

    public ImageViewModel(TopLevel topLevel, TabControl tabControl, WebView view, Canvas overlayCanvas, Image overlayCanvasImage)
    {
        _topLevel = topLevel;
        _tabControl = tabControl;
        _webView = view;
        _overlayCanvas = overlayCanvas;
        _overlayCanvasImage = overlayCanvasImage;
    }
    
    [ObservableProperty]    
    private IBrush _defaultFill = new SolidColorBrush(Color.FromArgb(128, 128, 128, 128));
    
    [ObservableProperty]
    private IBrush _defaultStroke = new SolidColorBrush(Color.FromArgb(200, 0, 0, 0));
    
    [ObservableProperty]
    private int _defaultThickness = 2;
    
    [ObservableProperty]
    private bool _noTiles = true;
    
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
    private double _defaultOverlayRadius = 0.03; // TODO: Different defaults depending on aspect ratio of image, or keep in pixels and convert to viewport
    
    [ObservableProperty]
    private OverlayViewModel? _selectedOverlay;

    [ObservableProperty]
    private int? _selectedTabIndex;
    
    [ObservableProperty]
    private string _selectImageTabHeader = "Select Image";
    
    [ObservableProperty]
    private string _makeTilesTabHeader = "Make Tiles";

    [ObservableProperty]
    private string _editOverlaysTabHeader = "Edit Overlays";
    
    [ObservableProperty]
    private string _previewTabHeader = "Preview";

    partial void OnSelectedOverlayChanged(OverlayViewModel? oldValue, OverlayViewModel? newValue)
    {
        if(oldValue != null) oldValue.IsSelected = false;
        if(newValue != null) newValue.IsSelected = true;
    }
    
    partial void OnSelectedTabIndexChanged(int? value)
    {
        if(value == null) return;
        if(_tabControl == null) return;
        if(_tabControl.Items.Count <= value) return;

        var index = (int)value;
        
        if(_tabControl.Items[index] is TabItem tabItem)
        {
            var tab = tabItem.Header as string;
            if(tab == EditOverlaysTabHeader)
            {
                Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    await LoadOverlays();
                });
            }
            else if(tab == PreviewTabHeader)
            {
                Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    await GeneratePreview();
                });
            }
        }
    }

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
        
        var previewPath = await GetPreviewPath();
        if(previewPath != null)
        {
            var previewImagePath = Path.Combine(previewPath, ImageFile.Name);
            if(!File.Exists(previewImagePath))
            {
                File.Copy(ImagePath, previewImagePath);
            }
        } 

        NoTiles = previewPath == null || !Directory.Exists(Path.Combine(previewPath, "tiles"));
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

        var previousCurrentDir = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(tilesPath);
        
        await Task.Run(() =>
        {
            vips.Dzsave(TilesName, suffix: $".webp[Q={q}]");
        });
        
        NoTiles = false;
        Directory.SetCurrentDirectory(previousCurrentDir);
        
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
    private async Task<string?> SaveOverlays()
    {
        var previewPath = await GetPreviewPath();
        if(previewPath == null) return null;
        
        if(_overlayCanvas == null || _overlayCanvasImage == null) return null;
        
        // Openseadragon overlay bounds are in viewpoint coordinates in percentage of the image width only
        // Save x,y as left/top instead of center
        var overlaysForJson = Overlays.Select(o => new OverlayJson
        {
            caption = o.Caption,
            x = o.Left,
            y = o.Top,
            width = o.Radius * 2,
            height = o.Radius * 2,
        }).ToList();

        var json = JsonSerializer.Serialize(overlaysForJson);
        var overlaysJsonPath = Path.Combine(previewPath, "overlays.json");

        // Copy existing as backup
        if(File.Exists(overlaysJsonPath))
        {
            var backupFolder = Path.Combine(previewPath, "backups");
            Directory.CreateDirectory(backupFolder);
            var backupPath = Path.Combine(backupFolder, $"overlays-backup-{Guid.NewGuid()}.json");
            await Task.Run(() => File.Copy(overlaysJsonPath, backupPath));
        }
        
        // Save new data
        await File.WriteAllTextAsync(overlaysJsonPath, json);
        
        // Return JSON
        return json;
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
        // Convert left/top to center coordinates
        Overlays = new ObservableCollection<OverlayViewModel>(overlaysJson.Select(o => new OverlayViewModel
        {
            Radius = o.width / 2,
            X = o.x + o.width / 2,
            Y = o.y + o.height / 2,
            Caption = o.caption,
            Fill = DefaultFill,
            Stroke = DefaultStroke,
            StrokeThickness = DefaultThickness
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
        
        var json = await SaveOverlays();
        await CopyPreviewAssets(previewPath);
        
        var htmlPath = Path.Combine(previewPath, "index.html");

        if(json != null || NoTiles)
        {
            var html = await File.ReadAllTextAsync(htmlPath);

            if(json != null)
            {
                html = html.Replace("<!-- OVERLAYS OVERRIDE -->", $"overlaysData = {json};");
            }

            if(NoTiles)
            {
                html = html.Replace("<!-- TILES OVERRIDE -->", $"tileSources = {{type: 'image', url: '{ImageFile.Name}'}};");
            }
            
            await File.WriteAllTextAsync(htmlPath, html);
        }
        
        
        var uri = new Uri(htmlPath);
        var uriString = uri.AbsoluteUri;
        _webView.Address = uriString;
        _webView.Reload();
    }

    [RelayCommand]
    private void DeleteSelectedOverlay()
    {
        if(SelectedOverlay == null) return;
        Overlays.Remove(SelectedOverlay);
        SelectedOverlay = null;
    }

    [RelayCommand]
    private void OverlayMoveUp()
    {
        if(SelectedOverlay == null) return;
        
        var idx = Overlays.IndexOf(SelectedOverlay);
        if(idx == 0) return;
        
        Overlays.Move(idx, idx - 1);
        
        SelectedOverlay = Overlays[idx - 1];
    }
    
    [RelayCommand]
    private void OverlayMoveDown()
    {
        if(SelectedOverlay == null) return;
        
        var idx = Overlays.IndexOf(SelectedOverlay);
        if(idx+1 == Overlays.Count) return;
        
        Overlays.Move(idx, idx + 1);
        SelectedOverlay = Overlays[idx + 1];
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

    public void OnOverlayCanvasPressed(double px, double py)
    {
        if(_overlayCanvasImage == null) return;
        
        // Convert pixels to viewpoint coordinates (percentage of image width)
        var x = (px - _overlayCanvasImage.Bounds.Left) / _overlayCanvasImage.Bounds.Width;
        var y = (py - _overlayCanvasImage.Bounds.Top) / _overlayCanvasImage.Bounds.Width;
        var r = DefaultOverlayRadius;
            
        var overlay = new OverlayViewModel
        {
            X = x,
            Y = y,
            Radius = r,
            Fill = DefaultFill,
            Stroke = DefaultStroke,
            StrokeThickness = DefaultThickness
        };
        
        overlay.UpdateBounds();
        Overlays.Add(overlay);
        
        SelectedOverlay = overlay;
    }
}