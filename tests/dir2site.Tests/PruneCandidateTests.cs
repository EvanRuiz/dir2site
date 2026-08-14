// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.Generic;
using System.Linq;
using dir2site.SftpSync.Core;
using Xunit;

namespace dir2site.Tests;

/// <summary>
/// After a delete, folders left empty are removed from the server. Each one costs a round trip to
/// look at, sequentially, on one connection — so how many times a directory turns up in the list is
/// not a detail. Walking up from every deleted file separately re-visits shared ancestors once per
/// sibling, which is quadratic in the number of deletions and is what left a big take-down looking
/// like a hung app with a full progress bar.
/// </summary>
public class PruneCandidateTests
{
    private const string Root = "/var/www/site";

    [Fact]
    public void SiblingsDoNotMakeTheirSharedParentsComeUpTwice()
    {
        // 5,000 pages in one folder, the shape a collection of photographs takes.
        var touched = Enumerable.Range(0, 5000)
            .Select(i => $"{Root}/Photographs/1890s/p{i}")
            .ToList();

        var candidates = SftpSyncService.PruneCandidates(touched, Root);

        // The 5,000 leaves, plus 1890s and Photographs once each — not once per sibling.
        Assert.Equal(5002, candidates.Count);
        Assert.Equal(candidates.Count, candidates.Distinct().Count());
        Assert.Single(candidates, d => d == $"{Root}/Photographs/1890s");
        Assert.Single(candidates, d => d == $"{Root}/Photographs");
    }

    [Fact]
    public void EveryDirectoryComesAfterEverythingInsideIt()
    {
        var touched = new List<string>
        {
            $"{Root}/a/b/c",
            $"{Root}/a/b/d",
            $"{Root}/a/e",
            $"{Root}/f",
        };

        var candidates = SftpSyncService.PruneCandidates(touched, Root);

        // Deepest first is what lets one look settle a directory: by the time a parent is reached,
        // everything under it has already been dealt with, so what it holds then is final.
        foreach (var (dir, index) in candidates.Select((d, i) => (d, i)))
            foreach (var deeper in candidates.Where(d => d.StartsWith(dir + "/")))
                Assert.True(candidates.IndexOf(deeper) < index,
                    $"{deeper} should be visited before its ancestor {dir}");
    }

    [Fact]
    public void TheRemoteRootAndAnythingAboveItAreLeftAlone()
    {
        var candidates = SftpSyncService.PruneCandidates([$"{Root}/a/b"], Root);

        // Pruning past the root would start deleting the directory the site was deployed into,
        // and then its parents.
        Assert.DoesNotContain(Root, candidates);
        Assert.DoesNotContain("/var/www", candidates);
        Assert.DoesNotContain("/var", candidates);
        Assert.Equal([$"{Root}/a/b", $"{Root}/a"], candidates);
    }

    [Fact]
    public void ADirectoryTouchedTwiceIsStillOnlyVisitedOnce()
    {
        var candidates = SftpSyncService.PruneCandidates(
            [$"{Root}/a/b", $"{Root}/a/b", $"{Root}/a"], Root);

        Assert.Equal([$"{Root}/a/b", $"{Root}/a"], candidates);
    }

    [Fact]
    public void NothingTouchedMeansNothingToPrune()
    {
        Assert.Empty(SftpSyncService.PruneCandidates([], Root));
    }
}
