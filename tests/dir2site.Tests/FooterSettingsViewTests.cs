// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using dir2site.Models;
using dir2site.Services;
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

    /// <summary>
    /// A press on the control itself. The handler tunnels, so raising it on the box is what a click
    /// landing anywhere inside it amounts to.
    /// </summary>
    private static void Click(AutoCompleteBox box)
    {
        box.RaiseEvent(new PointerPressedEventArgs(
            box, new Pointer(0, PointerType.Mouse, true), box, default,
            0, new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None));
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// Types into the box the way a person does. The inner text box is what holds focus and takes
    /// the characters, so typing at the AutoCompleteBox itself goes nowhere.
    /// </summary>
    private static void Type(Window window, AutoCompleteBox box, string text)
    {
        box.GetVisualDescendants().OfType<TextBox>().First().Focus();
        Dispatcher.UIThread.RunJobs();
        window.KeyTextInput(text);
        Dispatcher.UIThread.RunJobs();
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
    public void TheOnlyPlaceholderIsTheOneThatStatesARealDefault()
    {
        var (view, _) = Show(ConfigWith(
            new FooterItem { Title = "Row", Icon = "bi-youtube", Link = "https://example.test/a" }));

        // An example in a box reads as a default. Only the footer color has one — the primary color
        // it genuinely falls back to; every other field is simply empty when empty.
        var watermarks = view.GetVisualDescendants().OfType<TextBox>()
            .Select(b => b.Watermark)
            .Where(w => !string.IsNullOrEmpty(w))
            .ToList();

        Assert.Equal(["#333333"], watermarks);
    }

    [AvaloniaFact]
    public void TheIconBoxCompletesAgainstEveryAvailableIcon()
    {
        var (view, _) = Show(ConfigWith(new FooterItem { Title = "Row", Link = "https://example.test/a" }));

        var box = view.GetVisualDescendants().OfType<AutoCompleteBox>().First();
        var offered = box.ItemsSource!.Cast<IconChoice>().ToList();

        Assert.True(offered.Count > 1500, $"expected the whole icon set, got {offered.Count}");
        Assert.Contains(offered, i => i.Name == "bi-youtube");

        // Clicking in with nothing typed has to show the set, since nobody knows a name to start
        // from. The matching itself is pinned by the two filter tests below.
        Assert.Equal(0, box.MinimumPrefixLength);
    }

    [AvaloniaFact]
    public void ClickingIntoAFilledIconBoxOffersEveryIconNotJustTheOneAlreadyThere()
    {
        var (view, _) = Show(ConfigWith(
            new FooterItem { Title = "Row", Icon = "bi-youtube", Link = "https://example.test/a" }));

        var box = view.GetVisualDescendants().OfType<AutoCompleteBox>().First();
        Click(box);

        Assert.True(box.IsDropDownOpen);

        // With ordinary Contains filtering the one match for "bi-youtube" is itself, so the list
        // opened on the single icon the user clicked in to change. A click suspends the filter.
        var filter = box.ItemFilter;
        Assert.NotNull(filter);
        Assert.True(filter("bi-youtube", new IconChoice("bi-lock", "x")));
        Assert.True(filter("bi-youtube", new IconChoice("bi-envelope", "y")));
    }

    [AvaloniaFact]
    public void ChoosingAnIconClosesTheListRatherThanReopeningIt()
    {
        var (view, _) = Show(ConfigWith(
            new FooterItem { Title = "Row", Icon = "bi-youtube", Link = "https://example.test/a" }));

        var box = view.GetVisualDescendants().OfType<AutoCompleteBox>().First();
        Click(box);
        Assert.True(box.IsDropDownOpen);

        // Picking from the popup closes it and hands focus back to the box. Opening on focus made
        // that look like a fresh click, so the action that should end the interaction restarted it.
        box.SelectedItem = BootstrapIcons.Icons.First(i => i.Name == "bi-lock");
        box.IsDropDownOpen = false;
        box.Focus();
        Dispatcher.UIThread.RunJobs();

        Assert.False(box.IsDropDownOpen);
    }

    [AvaloniaFact]
    public void TypingNarrowsTheIconListAgain()
    {
        var (view, _) = Show(ConfigWith(
            new FooterItem { Title = "Row", Icon = "bi-youtube", Link = "https://example.test/a" }));

        var box = view.GetVisualDescendants().OfType<AutoCompleteBox>().First();
        Click(box);
        Type(view, box, "question");

        var matching = BootstrapIcons.Icons.Count(i => box.ItemFilter!(box.SearchText, i));

        // Typed for real rather than by raising a key event, because that is what found this: the
        // filter used to hang off a KeyDown handler, and characters arrive as text input, so the
        // list stayed on all two thousand icons however much was typed.
        Assert.InRange(matching, 1, 50);
        Assert.True(box.ItemFilter!(box.SearchText, new IconChoice("bi-question", "x")));
        Assert.False(box.ItemFilter!(box.SearchText, new IconChoice("bi-envelope", "y")));
    }

    [AvaloniaFact]
    public void TypingNarrowsAnIconBoxThatAlreadyHadAValue()
    {
        var (view, _) = Show(ConfigWith(
            new FooterItem { Title = "Row", Icon = "bi-lock", Link = "https://example.test/a" }));

        var box = view.GetVisualDescendants().OfType<AutoCompleteBox>().First();
        Click(box);
        Type(view, box, "question");

        Assert.True(box.ItemFilter!(box.SearchText, new IconChoice("bi-question", "x")));
        Assert.False(box.ItemFilter!(box.SearchText, new IconChoice("bi-lock", "y")));
    }

    [AvaloniaFact]
    public void MatchingIsAnywhereInTheNameAndIgnoresCase()
    {
        var (view, _) = Show(ConfigWith(new FooterItem { Title = "Row", Link = "https://example.test/a" }));

        var box = view.GetVisualDescendants().OfType<AutoCompleteBox>().First();
        Click(box);
        Type(view, box, "TUBE");

        Assert.True(box.ItemFilter!(box.SearchText, new IconChoice("bi-youtube", "x")));
        Assert.False(box.ItemFilter!(box.SearchText, new IconChoice("bi-lock", "y")));
    }

    [AvaloniaFact]
    public void TheChosenIconIsDrawnBesideItsField()
    {
        var (view, vm) = Show(ConfigWith(
            new FooterItem { Title = "Row", Icon = "bi-youtube", Link = "https://example.test/a" }));

        var glyph = BootstrapIcons.GlyphFor("bi-youtube");
        var preview = view.GetVisualDescendants().OfType<TextBlock>()
            .FirstOrDefault(t => t.Text == glyph);
        Assert.NotNull(preview);

        // It follows the field, so a name typed over the top redraws rather than going stale.
        vm.Items[0].Icon = "bi-lock";
        Dispatcher.UIThread.RunJobs();

        var updated = BootstrapIcons.GlyphFor("bi-lock");
        Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == updated);
        Assert.DoesNotContain(view.GetVisualDescendants().OfType<TextBlock>(),
            t => t.Text == glyph && !ReferenceEquals(t, preview));
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
