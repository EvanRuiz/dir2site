using CommunityToolkit.Mvvm.ComponentModel;

namespace dir2site.ViewModels;

public partial class DeepzoomViewModel : PhotoViewModel
{
    [ObservableProperty] public partial string? Original { get; set; }
    [ObservableProperty] public partial string? Tiles { get; set; }
}
