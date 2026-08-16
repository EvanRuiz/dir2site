// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using dir2site.Models;
using dir2site.Services;
using Xunit;

namespace dir2site.Tests;

/// <summary>
/// The JSON side. Each test gets its own directory, so nothing here touches the real
/// %AppData%/dir2site/ui or collides with the other test classes xUnit runs in parallel.
/// </summary>
public class RecentProjectsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "d2s-recent-" + Guid.NewGuid().ToString("N"));

    private RecentProjectsStore Store => new(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>An absolute path that normalizes to itself, without needing to exist.</summary>
    private static string SomeFolder(string name) => Path.Combine(Path.GetTempPath(), "d2s-proj-" + name);

    [Fact]
    public void Load_WhenNothingWasEverSaved_ReturnsEmpty()
    {
        Assert.Empty(Store.Load());
    }

    [Fact]
    public void WhatIsSavedComesBack()
    {
        var path = SomeFolder("alpha");
        Store.Remember(path);

        var loaded = Store.Load();

        Assert.Equal(path, Assert.Single(loaded).Path);
    }

    [Fact]
    public void SavingCreatesTheDirectory()
    {
        Assert.False(Directory.Exists(_dir));

        Store.Remember(SomeFolder("alpha"));

        Assert.True(File.Exists(Path.Combine(_dir, "recent.json")));
    }

    [Fact]
    public void AHalfWrittenFileIsIgnoredRatherThanThrowing()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "recent.json"), "{ \"Version\": 1, \"Projects\": [");

        Assert.Empty(Store.Load());
    }

    [Fact]
    public void ARecordFromAnUnknownShapeVersionIsIgnored()
    {
        Store.Remember(SomeFolder("alpha"));
        var file = Path.Combine(_dir, "recent.json");
        File.WriteAllText(file, File.ReadAllText(file)
            .Replace($"\"Version\": {RecentProjects.CurrentVersion}",
                     $"\"Version\": {RecentProjects.CurrentVersion + 1}"));

        Assert.Empty(Store.Load());
    }

    [Fact]
    public void RememberingTheSameFolderTwiceKeepsOneEntry()
    {
        var path = SomeFolder("alpha");
        Store.Remember(path);
        Store.Remember(path);

        Assert.Single(Store.Load());
    }

    [Fact]
    public void RememberingAPathWithATrailingSeparatorMatchesTheOneWithout()
    {
        var path = SomeFolder("alpha");
        Store.Remember(path);
        Store.Remember(path + Path.DirectorySeparatorChar);

        Assert.Single(Store.Load());
    }

    [Fact]
    public void ARelativeStepIsResolvedBeforeMatching()
    {
        var path = SomeFolder("alpha");
        Store.Remember(path);
        Store.Remember(Path.Combine(path, "sub", ".."));

        Assert.Single(Store.Load());
    }

    [Fact]
    public void TheMostRecentlyOpenedFolderComesBackFirst()
    {
        Store.Remember(SomeFolder("alpha"));
        Store.Remember(SomeFolder("beta"));

        var loaded = Store.Load();

        Assert.Equal(SomeFolder("beta"), loaded[0].Path);
        Assert.Equal(SomeFolder("alpha"), loaded[1].Path);
    }

    [Fact]
    public void ReopeningAnOlderFolderMovesItToTheFront()
    {
        Store.Remember(SomeFolder("alpha"));
        Store.Remember(SomeFolder("beta"));
        Store.Remember(SomeFolder("alpha"));

        var loaded = Store.Load();

        Assert.Equal(2, loaded.Count);
        Assert.Equal(SomeFolder("alpha"), loaded[0].Path);
    }

    [Fact]
    public void OnlyTheNewestEntriesAreKept()
    {
        for (var i = 0; i < RecentProjects.MaxEntries + 3; i++)
            Store.Remember(SomeFolder($"p{i:00}"));

        var loaded = Store.Load();

        Assert.Equal(RecentProjects.MaxEntries, loaded.Count);
        Assert.DoesNotContain(loaded, entry => entry.Path == SomeFolder("p00"));
    }

    [Fact]
    public void AForgottenFolderIsGoneFromTheList()
    {
        Store.Remember(SomeFolder("alpha"));
        Store.Remember(SomeFolder("beta"));

        Store.Forget(SomeFolder("alpha"));

        Assert.Equal(SomeFolder("beta"), Assert.Single(Store.Load()).Path);
    }

    [Fact]
    public void ForgettingUsesTheSamePathMatchingAsRemembering()
    {
        Store.Remember(SomeFolder("alpha"));

        Store.Forget(SomeFolder("alpha") + Path.DirectorySeparatorChar);

        Assert.Empty(Store.Load());
    }

    [Fact]
    public void ForgettingSomethingThatWasNeverThereChangesNothing()
    {
        Store.Remember(SomeFolder("alpha"));

        Store.Forget(SomeFolder("never-opened"));

        Assert.Single(Store.Load());
    }

    [Fact]
    public void AForgottenFolderComesBackIfItIsOpenedAgain()
    {
        // Forgetting is about the shortcut list, not about banning the project.
        Store.Remember(SomeFolder("alpha"));
        Store.Forget(SomeFolder("alpha"));

        Store.Remember(SomeFolder("alpha"));

        Assert.Single(Store.Load());
    }

    [Fact]
    public void ALeftoverTempFileFromACrashedWriteIsIgnored()
    {
        Store.Remember(SomeFolder("alpha"));
        File.WriteAllText(Path.Combine(_dir, "recent.json.tmp"), "half a fi");

        Assert.Single(Store.Load());
    }

    [Fact]
    public void SavingSomewhereUnwritableIsSwallowed()
    {
        // A file where the directory should be — Save must not take the app down with it.
        var blocked = Path.Combine(_dir, "blocked");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(blocked, "not a directory");

        new RecentProjectsStore(blocked).Remember(SomeFolder("alpha"));

        Assert.Empty(new RecentProjectsStore(blocked).Load());
    }

    [Fact]
    public void ANonsensePathIsNotRemembered()
    {
        Store.Remember("   ");

        Assert.Empty(Store.Load());
    }
}

/// <summary>
/// Turning a remembered folder into a tile. Real folders on disk, since every rule here is about
/// what is or isn't present in one.
/// </summary>
public class RecentProjectResolverTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "d2s-resolve-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>A project folder with a config written by the app's own writer.</summary>
    private string MakeProject(string name, string title = "", string logo = "", Dir2SiteModel? config = null)
    {
        var root = Path.Combine(_dir, name);
        Directory.CreateDirectory(root);
        config ??= new Dir2SiteModel();
        config.Title = title;
        config.Logo = logo;
        YamlParser.SaveDir2SiteConfig(Path.Combine(root, "dir2site.yaml"), config);
        return root;
    }

    private static void WriteFile(string path, string content = "x")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public void AFolderThatIsGoneProducesNoTile()
    {
        Assert.Null(RecentProjectResolver.Resolve(Path.Combine(_dir, "never-existed")));
    }

    [Fact]
    public void AFolderWithoutAConfigProducesNoTile()
    {
        var root = Path.Combine(_dir, "plain");
        Directory.CreateDirectory(root);

        Assert.Null(RecentProjectResolver.Resolve(root));
    }

    [Fact]
    public void TheResolverNeverCreatesAConfig()
    {
        var root = Path.Combine(_dir, "plain");
        Directory.CreateDirectory(root);

        RecentProjectResolver.Resolve(root);

        // Listing the welcome screen must not quietly turn a folder into a project.
        Assert.False(File.Exists(Path.Combine(root, "dir2site.yaml")));
    }

    [Fact]
    public void TheConfigTitleIsUsed()
    {
        var root = MakeProject("holiday", title: "Summer 2026");

        Assert.Equal("Summer 2026", RecentProjectResolver.Resolve(root)!.Title);
    }

    [Fact]
    public void AConfigWithoutATitleFallsBackToTheFolderName()
    {
        var root = MakeProject("holiday");

        Assert.Equal("holiday", RecentProjectResolver.Resolve(root)!.Title);
    }

    [Fact]
    public void UnreadableYamlStillProducesATileTitledAfterTheFolder()
    {
        var root = Path.Combine(_dir, "broken");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "dir2site.yaml"), "title: [unclosed\n  : :");

        var info = RecentProjectResolver.Resolve(root);

        Assert.NotNull(info);
        Assert.Equal("broken", info!.Title);
        Assert.Null(info.LogoPath);
    }

    [Fact]
    public void ARelativeLogoResolvesAgainstTheProjectRoot()
    {
        var root = MakeProject("branded", logo: "assets/logo.png");
        WriteFile(Path.Combine(root, "assets", "logo.png"));

        Assert.Equal(
            Path.Combine(root, "assets", "logo.png"),
            RecentProjectResolver.Resolve(root)!.LogoPath);
    }

    [Fact]
    public void AProjectWithoutALogoHasNoLogoPath()
    {
        var root = MakeProject("plain-project");

        Assert.Null(RecentProjectResolver.Resolve(root)!.LogoPath);
    }

    [Fact]
    public void AMissingLogoFileLeavesNoLogo()
    {
        var root = MakeProject("branded", logo: "logo.png");

        Assert.Null(RecentProjectResolver.Resolve(root)!.LogoPath);
    }

    [Fact]
    public void AnSvgLogoIsUsed()
    {
        // Site logos are routinely vector, which is why the app carries an SVG renderer.
        var root = MakeProject("vector", logo: "logo.svg");
        WriteFile(Path.Combine(root, "logo.svg"), "<svg/>");

        Assert.Equal(Path.Combine(root, "logo.svg"), RecentProjectResolver.Resolve(root)!.LogoPath);
    }

    [Fact]
    public void ALogoInAFormatNothingCanDrawIsIgnored()
    {
        var root = MakeProject("odd", logo: "logo.psd");
        WriteFile(Path.Combine(root, "logo.psd"));

        Assert.Null(RecentProjectResolver.Resolve(root)!.LogoPath);
    }

    [Fact]
    public void ALogoOutsideTheProjectIsIgnored()
    {
        var root = MakeProject("escaping", logo: Path.Combine("..", "outside.png"));
        WriteFile(Path.Combine(_dir, "outside.png"));

        Assert.Null(RecentProjectResolver.Resolve(root)!.LogoPath);
    }

    [Fact]
    public void AnAbsoluteLogoPathOutsideTheProjectIsIgnored()
    {
        var outside = Path.Combine(_dir, "outside.png");
        WriteFile(outside);
        var root = MakeProject("absolute", logo: outside);

        Assert.Null(RecentProjectResolver.Resolve(root)!.LogoPath);
    }

    [Fact]
    public void ALogoThatIsASymlinkOutOfTheProjectIsIgnored()
    {
        // The containment check is about where the file is, not how the path is spelled: a link
        // sitting inside the project reads as contained until you follow it.
        var secret = Path.Combine(_dir, "id_rsa");
        WriteFile(secret, "PRIVATE KEY");
        var root = MakeProject("linked", logo: "logo.png");
        File.CreateSymbolicLink(Path.Combine(root, "logo.png"), secret);

        Assert.Null(RecentProjectResolver.Resolve(root)!.LogoPath);
    }

    [Fact]
    public void ALogoThatIsASymlinkWithinTheProjectIsStillUsed()
    {
        var root = MakeProject("linked-inside", logo: "logo.png");
        WriteFile(Path.Combine(root, "assets", "real.png"));
        File.CreateSymbolicLink(Path.Combine(root, "logo.png"), Path.Combine(root, "assets", "real.png"));

        Assert.Equal(Path.Combine(root, "logo.png"), RecentProjectResolver.Resolve(root)!.LogoPath);
    }

    // The tile is meant to read as the site's own header, so it follows the same rule the
    // generated stylesheet does: a dark navbar is the primary color with white on it, a light one
    // is white with the primary color on it.

    [Fact]
    public void ADarkNavbarIsThePrimaryColorWithWhiteOnIt()
    {
        var root = MakeProject("dark", config: new Dir2SiteModel
        {
            PrimaryColor = "#123456", NavbarDark = true,
        });

        var info = RecentProjectResolver.Resolve(root)!;

        Assert.Equal("#123456", info.HeaderBackground);
        Assert.Equal("#ffffff", info.HeaderForeground);
    }

    [Fact]
    public void ALightNavbarIsWhiteWithThePrimaryColorOnIt()
    {
        var root = MakeProject("light", config: new Dir2SiteModel
        {
            PrimaryColor = "#123456", NavbarDark = false,
        });

        var info = RecentProjectResolver.Resolve(root)!;

        Assert.Equal("#ffffff", info.HeaderBackground);
        Assert.Equal("#123456", info.HeaderForeground);
    }

    [Fact]
    public void APrimaryColorThatIsNotAColorFallsBackToTheDefault()
    {
        var root = MakeProject("bogus", config: new Dir2SiteModel
        {
            PrimaryColor = "octarine; }", NavbarDark = true,
        });

        Assert.Equal(new Dir2SiteModel().PrimaryColor, RecentProjectResolver.Resolve(root)!.HeaderBackground);
    }

    [Fact]
    public void AProjectWithNoConfigColorsUsesTheDefaults()
    {
        var root = MakeProject("plain-colors");
        var defaults = new Dir2SiteModel();

        var info = RecentProjectResolver.Resolve(root)!;

        Assert.Equal(defaults.NavbarDark ? defaults.PrimaryColor : "#ffffff", info.HeaderBackground);
    }

    [Fact]
    public void ATrailingSeparatorResolvesToTheSameProject()
    {
        var root = MakeProject("holiday", title: "Summer 2026");

        var info = RecentProjectResolver.Resolve(root + Path.DirectorySeparatorChar);

        Assert.Equal(root, info!.Path);
    }

    [Fact]
    public void AStoreRoundTripFeedsTheResolver()
    {
        // The two halves in one go: remember real folders, then build tiles for the survivors.
        var storeDir = Path.Combine(_dir, "store");
        var store = new RecentProjectsStore(storeDir);
        var kept = MakeProject("kept", title: "Kept");
        var gone = Path.Combine(_dir, "gone");
        Directory.CreateDirectory(gone);
        YamlParser.SaveDir2SiteConfig(Path.Combine(gone, "dir2site.yaml"), new Dir2SiteModel());

        store.Remember(kept);
        store.Remember(gone);
        Directory.Delete(gone, recursive: true);

        var tiles = store.Load()
            .Select(entry => RecentProjectResolver.Resolve(entry.Path))
            .OfType<RecentProjectInfo>()
            .ToList();

        Assert.Equal("Kept", Assert.Single(tiles).Title);
    }
}
