using CommunityToolkit.Mvvm.ComponentModel;

namespace dir2site.ViewModels;

public partial class PhotoViewModel : ArtifactViewModel
{
    [ObservableProperty] public partial string? Photographer { get; set; }
    [ObservableProperty] public partial string? Image { get; set; }
    [ObservableProperty] public partial string? Overlays { get; set; }
}
