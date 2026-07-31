// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using dir2site.SftpSync.Core;

namespace dir2site.SftpSync.Ui;

public partial class RemoteBrowseView : Window
{
    // Parameterless ctor for the XAML previewer / designer.
    public RemoteBrowseView()
    {
        InitializeComponent();
    }

    public RemoteBrowseView(SftpProfile profile, string? secret, IHostKeyVerifier? verifier) : this()
    {
        DataContext = new RemoteBrowseViewModel(this, profile, secret, verifier);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    // Double-click descends, which is what every file browser does.
    private void OnDirectoryDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is RemoteBrowseViewModel vm)
            vm.OpenCommand.Execute(null);
    }
}
