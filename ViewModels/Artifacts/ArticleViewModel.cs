using CommunityToolkit.Mvvm.ComponentModel;

namespace dir2site.ViewModels;

public partial class ArticleViewModel : DocumentViewModel
{
    [ObservableProperty] public partial string? Image { get; set; }
}
