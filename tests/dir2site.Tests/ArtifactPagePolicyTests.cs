// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Linq;
using Avalonia.Headless.XUnit;
using dir2site.Models;
using dir2site.Services;
using Xunit;

namespace dir2site.Tests;

/// <summary>
/// Two things about an artifact type's page — whether its media is sized to the window, and
/// whether it is on the folder's prev/next chain — are answered by one table rather than by
/// <c>type is Photo or Deepzoom</c> written out wherever either is needed.
///
/// A table has a way of going quiet: a type added later gets no row, reads as false for both, and
/// nothing says so. This is what makes that a failing build instead of a photo page someone
/// eventually notices behaves like an article.
/// </summary>
public class ArtifactPagePolicyTests
{
    [AvaloniaFact]
    public void EveryArtifactTypeHasBeenDecidedOn()
    {
        var missing = Enum.GetValues<ArtifactType>()
            .Where(t => !SiteGenerator.PagePolicies.ContainsKey(t))
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"SiteGenerator.PagePolicies has no row for: {string.Join(", ", missing)}. " +
            "Decide whether each fits the viewport and whether it carries prev/next.");
    }

    /// <summary>
    /// The chain's two directions are one flag on purpose. Splitting them — pages that carry the
    /// arrows, pages the arrows lead to — is what would let a reader arrive somewhere with nothing
    /// to leave by, so there is nothing here to keep in step; this pins that the flag still says
    /// what today's arrows do.
    /// </summary>
    [AvaloniaFact]
    public void OnlyPhotosAreOnThePrevNextChain()
    {
        var onChain = SiteGenerator.PagePolicies
            .Where(p => p.Value.HasPrevNextNav)
            .Select(p => p.Key)
            .OrderBy(t => t)
            .ToList();

        Assert.Equal([ArtifactType.Photo, ArtifactType.Deepzoom], onChain);
    }

    /// A page on the chain must be one whose caption is on screen, or its arrows are below the fold.
    [AvaloniaFact]
    public void EverythingOnTheChainIsAlsoSizedToTheWindow()
    {
        Assert.All(
            SiteGenerator.PagePolicies.Where(p => p.Value.HasPrevNextNav),
            p => Assert.True(
                p.Value.FitsViewport,
                $"{p.Key} carries prev/next but is not sized to the window, so they would sit " +
                "below the fold."));
    }
}
