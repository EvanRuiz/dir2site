using CommunityToolkit.Mvvm.ComponentModel;

namespace dir2site.ViewModels;

public partial class ArticleViewModel : ArtifactViewModel
{
    [ObservableProperty] public partial string? Author { get; set; }
    [ObservableProperty] public partial string? Image { get; set; }
}
