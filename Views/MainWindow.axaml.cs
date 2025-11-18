using System;
using Avalonia.Controls;
using OpenSeadragonOverlayEditor.ViewModels;

namespace OpenSeadragonOverlayEditor.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        var topLevel = GetTopLevel(this);
        if(topLevel is null)
        {
            throw new ArgumentNullException(nameof(topLevel));
        }
        DataContext = new ImageViewModel(topLevel, WebViewControl);
        WebViewControl.LoadHtml("Preview...");
    }
}