using CommunityToolkit.Mvvm.ComponentModel;

namespace dir2site.ViewModels;

public partial class PdfViewModel : DocumentViewModel
{
    [ObservableProperty] public partial bool PublishOriginal { get; set; }
}
