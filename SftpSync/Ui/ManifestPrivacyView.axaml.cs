// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace dir2site.SftpSync.Ui;

public partial class ManifestPrivacyView : Window
{
    public ManifestPrivacyView()
    {
        InitializeComponent();
        DataContext = new ManifestPrivacyViewModel(this);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
