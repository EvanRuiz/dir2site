using CommunityToolkit.Mvvm.ComponentModel;

namespace dir2site.ViewModels;

public partial class DocumentViewModel : ArtifactViewModel
{
    [ObservableProperty] public partial string? Author { get; set; }
}
