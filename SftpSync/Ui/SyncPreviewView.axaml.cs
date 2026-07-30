// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using dir2site.SftpSync.Core;

namespace dir2site.SftpSync.Ui;

public partial class SyncPreviewView : Window
{
    public SyncPreviewView()
    {
        InitializeComponent();
    }

    public SyncPreviewView(SyncPlan plan) : this()
    {
        DataContext = new SyncPreviewViewModel(this, plan);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
