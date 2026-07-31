// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using dir2site.ViewModels;

namespace dir2site.Views;

public partial class ConfirmView : Window
{
    // Parameterless ctor for the XAML previewer / designer.
    public ConfirmView()
    {
        InitializeComponent();
    }

    public ConfirmView(string title, string heading, string detail, string confirmText) : this()
    {
        Title = title;
        DataContext = new ConfirmViewModel(this, heading, detail, confirmText);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
