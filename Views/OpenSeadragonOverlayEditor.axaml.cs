using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using dir2site.ViewModels;

namespace dir2site.Views;

public partial class OpenSeadragonOverlayEditor : Window
{
    private ImageViewModel _viewModel;
    
    public OpenSeadragonOverlayEditor()
    {
        InitializeComponent();
        var topLevel = GetTopLevel(this);
        if(topLevel is null)
        {
            throw new ArgumentNullException(nameof(topLevel));
        }
        
        _viewModel = new ImageViewModel(topLevel, MainTabControl, WebViewControl, OverlayCanvas, OverlayCanvasImage);
        DataContext = _viewModel;
        WebViewControl.LoadHtml("Preview...");

        OverlayCanvas.PointerPressed += (sender, e) =>
        {
            var position = e.GetPosition(OverlayCanvas);
            _viewModel.OnOverlayCanvasPressed(position.X, position.Y);
        };
    }
    
    private OverlayViewModel? _draggingOverlay;
    private Point _dragStartPoint;
    private Point _totalDelta;
    
    private void Overlay_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Ellipse ellipse && ellipse.DataContext is OverlayViewModel overlay)
        {
            _draggingOverlay = overlay;
            _dragStartPoint = e.GetPosition(ellipse.Parent as Visual);
            e.Pointer.Capture(ellipse); // Important: capture pointer
            e.Handled = true;
            _viewModel.SelectedOverlay = overlay;
        }
    }

    private void Overlay_OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_draggingOverlay != null && sender is Ellipse ellipse)
        {
            var currentPoint = e.GetPosition(ellipse.Parent as Visual);
            var delta = currentPoint - _dragStartPoint;
            
            _totalDelta += delta;
            
            ellipse.RenderTransform = new TranslateTransform(_totalDelta.X, _totalDelta.Y);

            _dragStartPoint = currentPoint;
            e.Handled = true;
        }
    }

    private void Overlay_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is Ellipse ellipse)
        {
            if(_draggingOverlay != null) {
                // Apply in Viewport Coordinates
                _draggingOverlay.X += _totalDelta.X / OverlayCanvasImage.Bounds.Width;
                _draggingOverlay.Y += _totalDelta.Y / OverlayCanvasImage.Bounds.Width;
            
                ellipse.RenderTransform = null;
                _totalDelta = default;
                _draggingOverlay = null;
            }
            
            e.Pointer.Capture(null); // Release capture
            _draggingOverlay = null;
            e.Handled = true;
        }
    }
}