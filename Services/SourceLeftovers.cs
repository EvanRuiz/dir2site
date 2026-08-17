// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace dir2site.Services;

/// <summary>
/// Finds sidecars and preview folders whose artifact is no longer beside them.
/// </summary>
/// <remarks>
/// Both are named after the file they belong to, so a rename or a deletion carried out while
/// dir2site wasn't running leaves them behind with nothing pointing at them. The watcher would have
/// said which of the two happened; with nothing watching, the shapes are all there is — and they can
/// answer the rename case but not the deletion one. A leftover paired against a file that has
/// appeared under a new name is a rename. A leftover with nothing to pair against could be a
/// deletion or could be almost anything, so it is offered rather than assumed.
/// </remarks>
public static class SourceLeftovers
{
    /// <param name="Sidecars">Sidecar files in <c>name.ext.yaml</c> form with no <c>name.ext</c> beside them.</param>
    /// <param name="PreviewDirs">Folders under <c>.dir2site/</c> named for a stem nothing in the folder has.</param>
    public sealed record Analysis(
        IReadOnlyList<string> Sidecars,
        IReadOnlyList<string> PreviewDirs);

    public static readonly Analysis Nothing = new([], []);

    /// <summary>
    /// What is left over in one directory: sidecars and preview folders whose artifact is gone.
    /// </summary>
    public static Analysis InDirectory(string dir)
    {
        string[] files;
        try { files = Directory.GetFiles(dir); }
        catch { return Nothing; }

        var present = new HashSet<string>(files.Select(Path.GetFileName)!, StringComparer.OrdinalIgnoreCase);

        // "Portrait.jpg.yaml" belongs to "Portrait.jpg". The legacy "Portrait.yaml" form is
        // deliberately not considered: beside a missing Portrait.jpg it is indistinguishable from a
        // hand-written file that happens to share the name, and there is no way to tell which
        // without asking.
        var sidecars = files
            .Where(f => IsCurrentConventionSidecar(Path.GetFileName(f))
                     && !present.Contains(Path.GetFileNameWithoutExtension(Path.GetFileName(f))))
            .ToList();

        var stems = new HashSet<string>(
            files.Where(f => !DirectoryTraverser.IsSidecarName(Path.GetFileName(f)))
                 .Select(Path.GetFileNameWithoutExtension)!,
            StringComparer.OrdinalIgnoreCase);

        var previewDirs = new List<string>();
        var dir2site = Path.Combine(dir, ".dir2site");
        if (Directory.Exists(dir2site))
        {
            try
            {
                foreach (var sub in Directory.GetDirectories(dir2site))
                    if (!stems.Contains(Path.GetFileName(sub)))
                        previewDirs.Add(sub);
            }
            catch { /* unreadable is not the same as empty; say nothing about this folder */ }
        }

        return new Analysis(sidecars, previewDirs);
    }

    private static bool IsCurrentConventionSidecar(string name)
    {
        var ext = Path.GetExtension(name);
        if (!ext.Equals(".yaml", StringComparison.OrdinalIgnoreCase) &&
            !ext.Equals(".yml", StringComparison.OrdinalIgnoreCase))
            return false;

        // name.ext.yaml has a second extension underneath; the legacy name.yaml does not.
        return Path.GetExtension(Path.GetFileNameWithoutExtension(name)).Length > 0;
    }

    /// <summary>
    /// Takes away the sidecar and previews belonging to an artifact the user deleted.
    /// </summary>
    /// <remarks>
    /// Only ever called for a deletion the watcher saw happen, which is what makes doing it rather
    /// than offering it defensible. The same files reached by inference — noticed only because the
    /// artifact is missing — could equally be a rename we failed to pair or a file that never had
    /// one, so those go to <see cref="InDirectory"/> and get asked about.
    /// </remarks>
    public static void RemoveFor(string sourcePath, IProgress<string>? progress = null)
    {
        var dir  = Path.GetDirectoryName(sourcePath) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(sourcePath);
        var name = Path.GetFileName(sourcePath);
        var removed = false;

        // The current convention only. A legacy "Portrait.yaml" could just as easily be a file the
        // user wrote and named for the same subject, and nothing here can tell the difference.
        foreach (var ext in new[] { ".yaml", ".yml" })
        {
            var sidecar = Path.Combine(dir, name + ext);
            if (!File.Exists(sidecar)) continue;

            try { File.Delete(sidecar); removed = true; } catch { /* leave it for the sweep */ }
        }

        var previews = Path.Combine(dir, ".dir2site", stem);
        if (Directory.Exists(previews))
        {
            try { Directory.Delete(previews, recursive: true); removed = true; } catch { }
        }

        if (removed) progress?.Report($"Removed the settings and previews for {name}");
    }

    /// <summary>Everything left over beneath <paramref name="root"/>, as one list of paths.</summary>
    public static IReadOnlyList<string> FindAll(string root)
    {
        var found = new List<string>();

        foreach (var dir in Walk(root))
        {
            var analysis = InDirectory(dir);
            found.AddRange(analysis.Sidecars);
            found.AddRange(analysis.PreviewDirs);
        }

        return found;
    }

    /// <summary>Every folder the tree walk would visit, root included.</summary>
    private static IEnumerable<string> Walk(string root)
    {
        yield return root;

        IEnumerable<string> children;
        try { children = Directory.GetDirectories(root); }
        catch { yield break; }

        foreach (var child in children)
        {
            if (DirectoryTraverser.IsIgnoredDirectoryName(Path.GetFileName(child))) continue;
            foreach (var nested in Walk(child)) yield return nested;
        }
    }
}
