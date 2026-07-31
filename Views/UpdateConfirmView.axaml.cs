// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using dir2site.ViewModels;

namespace dir2site.Views;

public partial class UpdateConfirmView : Window
{
    // Parameterless ctor for the XAML previewer / designer.
    public UpdateConfirmView()
    {
        InitializeComponent();
    }

    public UpdateConfirmView(string version) : this()
    {
        DataContext = new UpdateConfirmViewModel(this, version);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
