// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using dir2site.ViewModels;

namespace dir2site.Views;

public partial class OrphanFilesView : Window
{
    // Parameterless ctor for the XAML previewer / designer.
    public OrphanFilesView()
    {
        InitializeComponent();
    }

    public OrphanFilesView(IEnumerable<string> orphanPaths, OrphanKind kind = OrphanKind.Site) : this()
    {
        DataContext = new OrphanFilesViewModel(this, orphanPaths, kind);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
