// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using dir2site.Models;
using dir2site.Services;
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

    /// <summary>
    /// Gives each icon box the click-to-open behaviour and the filter it uses, once, as it appears.
    /// </summary>
    /// <remarks>
    /// Wired here rather than in XAML because the press handler has to tunnel — the text box inside
    /// handles a press itself, so a bubbling handler is not reliably reached — and because the
    /// state it keeps is per box: the rows are templated, and each needs its own record of what it
    /// held when its list was last raised.
    /// </remarks>
    private void OnIconBoxAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is not AutoCompleteBox box || box.Tag is StrongBox<string>) return;

        // What the box held when the list was raised. Per box, because the rows are templated.
        var openedWith = new StrongBox<string>(box.Text ?? string.Empty);
        box.Tag = openedWith;

        // Tunnelled: the text box inside handles the press to place a caret, so waiting for it to
        // bubble means sometimes never hearing about it.
        box.AddHandler(PointerPressedEvent, (_, _) => OpenIfClosed(box, openedWith), RoutingStrategies.Tunnel);

        // Replaces FilterMode, which cannot express "everything, until they start looking".
        //
        // The test is the search text against the value the list was opened on, rather than a flag
        // set by a keystroke: characters arrive as text input, not as key presses, so a KeyDown
        // handler here never fired and the list stayed unfiltered however much was typed.
        box.ItemFilter = (search, item) =>
            string.Equals(search ?? string.Empty, openedWith.Value, StringComparison.Ordinal)
            || item is not IconChoice icon
            || icon.Name.Contains(search ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Raises the icon list when the field is clicked and the list isn't already up, showing every
    /// icon until the user starts narrowing it.
    /// </summary>
    /// <remarks>
    /// A click is the ask, not focus. Opening on focus meant choosing an item reopened the list —
    /// picking one closes the popup and hands focus back to the box, which looked exactly like a
    /// fresh click, so the one action that should end the interaction restarted it. Only opening
    /// when the list is down leaves choosing as the close, and leaves a second click free to put
    /// the caret somewhere.
    ///
    /// The open is posted because the control does its own dropdown handling around a press, and
    /// setting the flag inside that is undone before it settles.
    /// </remarks>
    private static void OpenIfClosed(AutoCompleteBox box, StrongBox<string> openedWith)
    {
        if (box.IsDropDownOpen) return;

        // Everything on offer until the text moves off what is already there — otherwise a box
        // holding "bi-youtube" opens on that one icon, which is not why anyone clicked.
        openedWith.Value = box.Text ?? string.Empty;
        Dispatcher.UIThread.Post(() => box.IsDropDownOpen = true, DispatcherPriority.Input);
    }
}
