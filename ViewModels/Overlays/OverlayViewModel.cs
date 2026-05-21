// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace dir2site.ViewModels;

public partial class OverlayViewModel : ViewModelBase
{
    //
    // Stored in Viewport Coordinates (percentage of viewport width)
    // 
    [ObservableProperty]
    private double _x;

    [ObservableProperty]
    private double _y;

    [ObservableProperty]
    private double _left;

    [ObservableProperty]
    private double _top;

    [ObservableProperty]
    private double _width;
    
    [ObservableProperty]
    private double _height;
    
    [ObservableProperty]
    private double _radius;
    
    [ObservableProperty]
    private IBrush _fill = Brushes.Transparent;

    [ObservableProperty]
    private IBrush _stroke = Brushes.Transparent;
    
    [ObservableProperty]
    private int _strokeThickness;
    
    [ObservableProperty]
    private string? _caption;

    [ObservableProperty]
    private bool _isSelected = false;

    [ObservableProperty]
    private int _index;
    
    [ObservableProperty]
    private IBrush _selectedFill = new SolidColorBrush(Color.FromArgb(128, 0, 100, 0));

    [ObservableProperty]
    private IBrush _selectedStroke = new SolidColorBrush(Color.FromArgb(128, 100, 255, 100));

    partial void OnXChanged(double value)
    {
        UpdateBounds();
    }
    
    partial void OnYChanged(double value)
    {
        UpdateBounds();
    }

    partial void OnRadiusChanged(double value)
    {
        UpdateBounds();
    }

    public void UpdateBounds()
    {
        Left = X - Radius;
        Top = Y - Radius;
        Width = Radius * 2;
        Height = Radius * 2;
    }
}