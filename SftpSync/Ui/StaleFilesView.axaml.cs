// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace dir2site.SftpSync.Ui;

public partial class StaleFilesView : Window
{
    // Parameterless ctor for the XAML previewer / designer.
    public StaleFilesView()
    {
        InitializeComponent();
    }

    public StaleFilesView(IEnumerable<string> stalePaths) : this()
    {
        DataContext = new StaleFilesViewModel(this, stalePaths);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
