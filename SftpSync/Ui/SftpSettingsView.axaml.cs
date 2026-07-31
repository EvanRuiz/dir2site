// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using dir2site.Models;

namespace dir2site.SftpSync.Ui;

public partial class SftpSettingsView : Window
{
    // Parameterless ctor for the XAML previewer / designer.
    public SftpSettingsView()
    {
        InitializeComponent();
    }

    public SftpSettingsView(string projectRoot, Dir2SiteModel config, string configPath) : this()
    {
        DataContext = new SftpSettingsViewModel(this, projectRoot, config, configPath);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
