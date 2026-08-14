// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using dir2site.Models;
using dir2site.ViewModels;

namespace dir2site.Views;

public partial class FooterSettingsView : Window
{
    // Parameterless ctor for the XAML previewer / designer.
    public FooterSettingsView()
    {
        InitializeComponent();
    }

    public FooterSettingsView(string projectRoot, Dir2SiteModel config) : this()
    {
        DataContext = new FooterSettingsViewModel(this, projectRoot, config);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
