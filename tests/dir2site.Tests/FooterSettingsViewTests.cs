// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using dir2site.Models;
using dir2site.ViewModels;
using dir2site.Views;
using Xunit;

namespace dir2site.Tests;

/// <summary>
/// Exercises the footer dialog through its real XAML, so a binding that doesn't resolve — a renamed
/// property, a control the project doesn't actually reference — fails here rather than showing up
/// as a dialog that won't open.
/// </summary>
public class FooterSettingsViewTests : IDisposable
{
    private readonly string _project = Path.Combine(
        Path.GetTempPath(), "d2s-footdlg-" + Guid.NewGuid().ToString("N"));

    public FooterSettingsViewTests() => Directory.CreateDirectory(_project);

    public void Dispose()
    {
        try { Directory.Delete(_project, recursive: true); } catch { }
    }

    private (FooterSettingsView view, FooterSettingsViewModel vm) Show(Dir2SiteModel? config = null)
    {
        var view = new FooterSettingsView(_project, config ?? new Dir2SiteModel());
        view.Show();
        Dispatcher.UIThread.RunJobs();
        return (view, (FooterSettingsViewModel)view.DataContext!);
    }

    private static Dir2SiteModel ConfigWith(params FooterItem[] items) => new()
    {
        FooterColor = "#101c32",
        FooterItems = [.. items],
    };

    /// <summary>
    /// The button that opens all of the above. Its command depends on two properties, and a
    /// RelayCommand only re-tests that when something tells it to — so without both
    /// NotifyCanExecuteChangedFor attributes the button is evaluated once at construction, when a
    /// project is not open yet, and stays greyed out for the whole session however much is loaded.
    /// Every other test here drives the view model directly and so cannot see that.
    /// </summary>
    [AvaloniaFact]
    public void TheFooterButtonBecomesUsableOnceAProjectIsOpen()
    {
        // The window opens before a project is chosen, so the button is bound while its command
        // says no. Asserting on CanExecute would prove nothing — that calls the predicate and is
        // always current. What was broken is the button, which keeps the answer it was last given
        // until a CanExecuteChanged tells it otherwise.
        var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // Chosen afterwards, in the order the app sets them: the folder, then the config read from it.
        vm.DirectoryRoot = _project;
        vm.Dir2SiteConfig = new Dir2SiteModel { Title = "Test" };
        Dispatcher.UIThread.RunJobs();

        var button = window.GetVisualDescendants().OfType<Button>()
            .First(b => (b.Content as string) == "Edit Footer…");
        Assert.True(button.IsEffectivelyEnabled);
    }

    [AvaloniaFact]
    public void TheDialogOpensAndShowsTheConfiguredRows()
    {
        var (view, vm) = Show(ConfigWith(
            new FooterItem { Column = 1, Title = "Example About", Link = "-Info/About.md" },
            new FooterItem { Column = 2, Title = "Example Privacy", Link = "--Footer/Privacy.md" }));

        Assert.Equal(2, vm.Items.Count);
        Assert.Equal("#101c32", vm.FooterColor);

        // The rows are templated, so a broken template shows up as no editors in the tree.
        var boxes = view.GetVisualDescendants().OfType<TextBox>().ToList();
        Assert.Contains(boxes, b => b.Text == "Example About");
        Assert.Contains(boxes, b => b.Text == "--Footer/Privacy.md");
    }

    [AvaloniaFact]
    public void TheFooterColourPlaceholderIsTheColourAnEmptyBoxActuallyGives()
    {
        var config = new Dir2SiteModel { PrimaryColor = "#223355", FooterColor = string.Empty };
        var (view, vm) = Show(config);

        // The generator falls back to PrimaryColor, so anything else here is the dialog claiming a
        // default the site does not have.
        Assert.Equal("#223355", vm.FooterColorPlaceholder);

        var box = view.GetVisualDescendants().OfType<TextBox>()
            .First(b => b.Watermark == "#223355");
        Assert.True(string.IsNullOrEmpty(box.Text));
    }

    [AvaloniaFact]
    public void TheIconColourBoxesClaimNoDefaultOfTheirOwn()
    {
        var (view, _) = Show(ConfigWith(
            new FooterItem { Title = "Row", Icon = "bi-youtube", Link = "https://example.test/a" }));

        // Empty means "inherit, or the brand's own colour" — never a fixed red or white, so a
        // placeholder naming one would be describing behaviour that doesn't exist.
        var watermarks = view.GetVisualDescendants().OfType<TextBox>()
            .Select(b => b.Watermark)
            .ToList();
        Assert.DoesNotContain("#ff0000", watermarks);
        Assert.DoesNotContain("#ffffff", watermarks);
    }

    [AvaloniaFact]
    public void TheClosingLineIsEditedHereAndKeptVerbatim()
    {
        // It moved out of Site Settings into this dialog, so this is now the only way to reach it —
        // and it must not be trimmed or escaped on the way, being the one field that is raw HTML.
        var config = new Dir2SiteModel { Footer = "&copy; 2026<br>Everyone" };
        var (view, vm) = Show(config);

        Assert.Equal("&copy; 2026<br>Everyone", vm.FooterText);

        var box = view.GetVisualDescendants().OfType<TextBox>()
            .First(b => b.Text == "&copy; 2026<br>Everyone");
        Assert.NotNull(box);

        vm.FooterText = "  &copy; 2027 <b>Everyone</b>  ";
        vm.SaveCommand.Execute(null);
        Assert.Equal("  &copy; 2027 <b>Everyone</b>  ", vm.FooterText);
    }

    [AvaloniaFact]
    public void TheLinkButtonsPutTheRowOnTheFormTheyName()
    {
        var (_, vm) = Show(ConfigWith(new FooterItem { Title = "Row", Link = string.Empty }));
        vm.SelectedItem = vm.Items[0];

        vm.SetWebLinkCommand.Execute(null);
        Assert.Equal("https://", vm.Items[0].Link);

        vm.SetMailtoLinkCommand.Execute(null);
        Assert.Equal("mailto:", vm.Items[0].Link);
    }

    [AvaloniaFact]
    public void SwitchingLinkFormKeepsWhatWasTypedRatherThanStackingSchemes()
    {
        var (_, vm) = Show(ConfigWith(
            new FooterItem { Title = "Row", Link = "https://example.test/channel" }));
        vm.SelectedItem = vm.Items[0];

        vm.SetMailtoLinkCommand.Execute(null);
        Assert.Equal("mailto:example.test/channel", vm.Items[0].Link);

        vm.SetWebLinkCommand.Execute(null);
        Assert.Equal("https://example.test/channel", vm.Items[0].Link);
    }

    [AvaloniaFact]
    public void TheLinkButtonsNeedARowToActOn()
    {
        var (_, vm) = Show(ConfigWith(new FooterItem { Title = "Row", Link = "/a/" }));

        vm.SelectedItem = null;
        Assert.False(vm.SetWebLinkCommand.CanExecute(null));
        Assert.False(vm.SetMailtoLinkCommand.CanExecute(null));

        vm.SelectedItem = vm.Items[0];
        Assert.True(vm.SetWebLinkCommand.CanExecute(null));
        Assert.True(vm.SetMailtoLinkCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void EditsAreDiscardedWhenTheDialogIsCancelled()
    {
        var config = ConfigWith(new FooterItem { Title = "Example About", Link = "-Info/About.md" });
        var (_, vm) = Show(config);

        vm.Items[0].Title = "Changed";
        vm.CancelCommand.Execute(null);

        // The dialog edits copies, so the config it was handed is untouched.
        Assert.Equal("Example About", config.FooterItems[0].Title);
    }

    [AvaloniaFact]
    public void ARowIsTrimmedOnItsWayBackToTheConfig()
    {
        // Stray spaces around a hex colour or an icon name would fail the generator's checks and be
        // dropped with a warning, which is a baffling result for something typed into a text box.
        var row = new FooterItemRow
        {
            Title = "  Renamed  ",
            Link = "  -Info/About.md  ",
            Icon = "  bi-envelope  ",
            IconColor = "  #ff0000  ",
        };

        var item = row.ToItem();

        Assert.Equal("Renamed", item.Title);
        Assert.Equal("-Info/About.md", item.Link);
        Assert.Equal("bi-envelope", item.Icon);
        Assert.Equal("#ff0000", item.IconColor);
    }

    [AvaloniaFact]
    public void AddingARowPutsItInTheColumnBeingLookedAt()
    {
        var (_, vm) = Show(ConfigWith(new FooterItem { Column = 3, Title = "Example Privacy", Link = "/privacy/" }));

        vm.SelectedItem = vm.Items[0];
        vm.AddItemCommand.Execute(null);

        Assert.Equal(2, vm.Items.Count);
        Assert.Equal(3, vm.Items[1].Column);
        Assert.Same(vm.Items[1], vm.SelectedItem);
    }

    [AvaloniaFact]
    public void MovingARowIsBoundedByTheEndsOfTheList()
    {
        var (_, vm) = Show(ConfigWith(
            new FooterItem { Title = "First", Link = "/a/" },
            new FooterItem { Title = "Second", Link = "/b/" }));

        vm.SelectedItem = vm.Items[0];
        Assert.False(vm.MoveUpCommand.CanExecute(null));
        Assert.True(vm.MoveDownCommand.CanExecute(null));

        vm.MoveDownCommand.Execute(null);

        Assert.Equal("Second", vm.Items[0].Title);
        Assert.Equal("First", vm.Items[1].Title);
        Assert.False(vm.MoveDownCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void RemovingTheLastRowLeavesNothingSelectedRatherThanThrowing()
    {
        var (_, vm) = Show(ConfigWith(new FooterItem { Title = "Only", Link = "/a/" }));

        vm.SelectedItem = vm.Items[0];
        vm.RemoveItemCommand.Execute(null);

        Assert.Empty(vm.Items);
        Assert.Null(vm.SelectedItem);
        Assert.False(vm.RemoveItemCommand.CanExecute(null));
    }
}
