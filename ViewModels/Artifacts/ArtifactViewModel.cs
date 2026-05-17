using System.IO;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

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
    [ObservableProperty] public partial string? RootFolder {get; set;}

    public string? PreviewPath => RootFolder == null || Preview == null
        ? null
        : Path.Combine(RootFolder, Preview);
    
    public Bitmap? PreviewBitmap => PreviewPath == null ? null : new Bitmap(PreviewPath);
}
