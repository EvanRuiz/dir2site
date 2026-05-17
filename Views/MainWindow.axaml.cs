using Avalonia.Controls;
using dir2site.ViewModels;

namespace dir2site.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        DataContextChanged += (sender, args) =>
        {
            if(DataContext is MainWindowViewModel viewModel)
            {
                viewModel.TopLevel = GetTopLevel(this);
            }
        };
    }
}