// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Linq;
using Avalonia.Headless.XUnit;
using dir2site.Services;
using Xunit;

namespace dir2site.Tests;

/// <summary>
/// The footer editor completes icon names against the vendored stylesheet rather than a list typed
/// out here, so what it offers cannot drift from the glyphs that actually exist. These pin that it
/// really reads, since a silent failure would leave the box looking like an ordinary text field.
/// </summary>
public class BootstrapIconNamesTests
{
    [AvaloniaFact]
    public void TheNamesAreReadFromTheVendoredStylesheet()
    {
        var names = BootstrapIcons.Names;

        // The 1.13.1 set is a little over two thousand; an exact count would fail on every upgrade
        // for no reason, but an empty or tiny list means the asset lookup broke.
        Assert.True(names.Count > 1500, $"expected the whole icon set, got {names.Count}");
    }

    [AvaloniaFact]
    public void TheNamesCarryThePrefixTheYamlUses()
    {
        var names = BootstrapIcons.Names;

        Assert.All(names, n => Assert.StartsWith("bi-", n));
        Assert.Contains("bi-youtube", names);
        Assert.Contains("bi-envelope", names);
        Assert.Contains("bi-lock", names);
    }

    [AvaloniaFact]
    public void EveryIconCarriesTheGlyphThatDrawsIt()
    {
        var icons = BootstrapIcons.Icons;

        Assert.All(icons, i => Assert.False(string.IsNullOrEmpty(i.Glyph)));

        // The codepoints come from the same stylesheet as the names, so a parse that silently
        // matched only names would leave these blank and the picker would list empty boxes.
        var youtube = icons.Single(i => i.Name == "bi-youtube");
        Assert.Equal("\uf62b", youtube.Glyph);
    }

    [AvaloniaFact]
    public void TheNamesAreSortedAndFreeOfDuplicates()
    {
        var names = BootstrapIcons.Names;

        // Each icon has a ::before rule and some appear twice in the stylesheet; a completion list
        // showing the same name twice is a small thing that looks broken.
        Assert.Equal(names.Distinct().Count(), names.Count);
        Assert.Equal([.. names.OrderBy(n => n, System.StringComparer.Ordinal)], names);
    }

    [AvaloniaFact]
    public void EveryBrandIconTheGeneratorColorsIsOfferedByName()
    {
        // The generator fills in house colors for these; offering a name it would then color is the
        // pairing that makes brand marks come out right without anyone knowing a hex code.
        foreach (var brand in (string[])["bi-youtube", "bi-facebook", "bi-instagram", "bi-linkedin",
                                         "bi-github", "bi-mastodon", "bi-bluesky"])
            Assert.Contains(brand, BootstrapIcons.Names);
    }

    [AvaloniaFact]
    public void AGlyphIsFoundByNameWithOrWithoutThePrefix()
    {
        Assert.Equal("\uf62b", BootstrapIcons.GlyphFor("bi-youtube"));
        // The yaml takes either spelling, so the picker's preview has to as well.
        Assert.Equal("\uf62b", BootstrapIcons.GlyphFor("youtube"));
        Assert.Equal("\uf62b", BootstrapIcons.GlyphFor("  bi-youtube  "));
    }

    [AvaloniaFact]
    public void ANameThatIsNotOneHasNoGlyphRatherThanAWrongOne()
    {
        // Half-typed, misspelled, or empty: nothing to draw, and nothing thrown.
        Assert.Equal(string.Empty, BootstrapIcons.GlyphFor("bi-youtu"));
        Assert.Equal(string.Empty, BootstrapIcons.GlyphFor("bi-not-an-icon"));
        Assert.Equal(string.Empty, BootstrapIcons.GlyphFor(""));
        Assert.Equal(string.Empty, BootstrapIcons.GlyphFor(null));
    }
}
