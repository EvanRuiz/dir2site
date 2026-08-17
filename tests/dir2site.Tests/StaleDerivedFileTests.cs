// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia.Headless.XUnit;
using dir2site.Services;
using Xunit;

namespace dir2site.Tests;

/// <summary>
/// Replacing a file in place, and the derived copies that have to follow it.
/// </summary>
/// <remarks>
/// Dropping a corrected scan over the old one under the same name is the ordinary way to replace a
/// photo, and it is the case "does the thumbnail exist" gets wrong. The survey already asks the
/// right question — it enqueues the artifact — so a generator that asked a narrower one took the
/// work and declined it, on every run, for as long as the project existed.
/// </remarks>
public class StaleDerivedFileTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "d2s-stale-" + Guid.NewGuid().ToString("N"));

    public StaleDerivedFileTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private static void MakeJpeg(string path, string colour)
    {
        using var process = Process.Start(new ProcessStartInfo("magick",
            $"-size 400x300 xc:{colour} \"{path}\"") { RedirectStandardError = true })!;
        process.WaitForExit();
    }

    [AvaloniaFact]
    public void ReplacingAPhotoRemakesItsThumbnailAndItsWebCopy()
    {
        var photos = Directory.CreateDirectory(Path.Combine(_root, "Photographs")).FullName;
        var jpeg = Path.Combine(photos, "Portrait.jpg");
        MakeJpeg(jpeg, "red");

        Assert.NotNull(PreviewGenerator.GeneratePreviews(jpeg, _root));

        var preview = Path.Combine(photos, ".dir2site", "Portrait", "preview-Portrait.webp");
        var large   = Path.Combine(photos, ".dir2site", "Portrait", "preview-lg-Portrait.webp");
        // The one the viewer actually shows, so getting this wrong publishes the wrong picture
        // rather than merely a wrong thumbnail.
        var webCopy = Path.Combine(photos, ".dir2site", "Portrait", "Portrait_q90.webp");

        var before = new[] { preview, large, webCopy }.Select(File.ReadAllBytes).ToList();

        // A whole second, because the check is a timestamp comparison and a filesystem that stores
        // them to the second would otherwise call the new file the same age as the old one.
        Thread.Sleep(1100);
        MakeJpeg(jpeg, "blue");

        PreviewGenerator.GeneratePreviews(jpeg, _root);

        var after = new[] { preview, large, webCopy }.Select(File.ReadAllBytes).ToList();

        Assert.False(after[0].SequenceEqual(before[0]), "the thumbnail is still of the old photo");
        Assert.False(after[1].SequenceEqual(before[1]), "the large thumbnail is still of the old photo");
        Assert.False(after[2].SequenceEqual(before[2]), "the published web copy is still the old photo");
    }

    [AvaloniaFact]
    public void AnUntouchedPhotoIsLeftAlone()
    {
        // The other half: staleness must not mean "rebuild every run", which would burn the work
        // and rewrite the site's assets on every save.
        var photos = Directory.CreateDirectory(Path.Combine(_root, "Photographs")).FullName;
        var jpeg = Path.Combine(photos, "Portrait.jpg");
        MakeJpeg(jpeg, "red");

        PreviewGenerator.GeneratePreviews(jpeg, _root);
        var preview = Path.Combine(photos, ".dir2site", "Portrait", "preview-Portrait.webp");
        var stamp = File.GetLastWriteTimeUtc(preview);

        Thread.Sleep(1100);
        PreviewGenerator.GeneratePreviews(jpeg, _root);

        Assert.Equal(stamp, File.GetLastWriteTimeUtc(preview));
    }
}
