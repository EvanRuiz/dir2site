// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using dir2site.ViewModels;

namespace dir2site.Views;

public partial class ProjectMovedView : Window
{
    // Parameterless ctor for the XAML previewer / designer.
    public ProjectMovedView()
    {
        InitializeComponent();
    }

    /// <param name="newPath">Where the folder is now, or null when nobody can say.</param>
    public ProjectMovedView(string oldPath, string? newPath) : this()
    {
        DataContext = new ProjectMovedViewModel(this, oldPath, newPath);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
