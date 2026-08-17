// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace dir2site.Services;

/// <summary>
/// Keeps an artifact's sidecar, thumbnails and caption with it when its file is renamed.
/// </summary>
/// <remarks>
/// Everything about an artifact except its bytes is keyed on its filename: the sidecar is
/// <c>Portrait.jpg.yaml</c>, the previews live in <c>.dir2site/Portrait/</c> and are named after the
/// stem again inside. Renaming the photo used to strand all of it — a fresh sidecar was scaffolded
/// for the new name, fresh thumbnails were rendered beside it, and the old set stayed behind for
/// good, along with whatever caption and settings the user had written into it.
/// </remarks>
public static class ArtifactRename
{
    /// <summary>
    /// Moves everything that belongs to <paramref name="oldPath"/> onto <paramref name="newPath"/>.
    /// </summary>
    /// <remarks>
    /// Nothing is overwritten. A destination that already exists means something is there we did not
    /// expect, and quietly replacing a user's file is not a recoverable mistake — each part is
    /// skipped on its own terms, so a rename that half-applies still leaves both halves intact.
    /// </remarks>
    public static void Apply(string oldPath, string newPath, IProgress<string>? progress = null)
    {
        if (string.Equals(oldPath, newPath, StringComparison.Ordinal)) return;

        var yamlPath = MoveSidecar(oldPath, newPath);
        MovePreviews(oldPath, newPath);

        if (yamlPath != null)
            RepointYaml(yamlPath, oldPath, newPath);

        progress?.Report($"Renamed {Path.GetFileName(oldPath)} → {Path.GetFileName(newPath)}");
    }

    /// <summary>
    /// Renames the sidecar, and returns where it now is.
    /// </summary>
    /// <remarks>
    /// Both spellings are looked for, matching <c>YamlParser.FindYamlMeta</c>, but only the current
    /// one is ever written: a legacy <c>Portrait.yaml</c> is renamed to <c>Headshot.jpg.yaml</c>
    /// rather than to <c>Headshot.yaml</c>, so the file quietly joins the convention it is already
    /// being read under instead of being carried further.
    /// </remarks>
    private static string? MoveSidecar(string oldPath, string newPath)
    {
        var destination = newPath + ".yaml";
        if (File.Exists(destination) || File.Exists(newPath + ".yml")) return null;

        foreach (var candidate in SidecarCandidates(oldPath))
        {
            if (!File.Exists(candidate)) continue;

            try
            {
                File.Move(candidate, destination);
                return destination;
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    private static IEnumerable<string> SidecarCandidates(string path)
    {
        var dir  = Path.GetDirectoryName(path) ?? string.Empty;
        var name = Path.GetFileName(path);
        var stem = Path.GetFileNameWithoutExtension(path);

        foreach (var ext in new[] { ".yaml", ".yml" })
        {
            yield return Path.Combine(dir, name + ext);

            // The legacy form, guarded against a file naming itself — "Portrait.yaml" is both a
            // sidecar for "Portrait.jpg" and a plausible artifact in its own right.
            var legacy = Path.Combine(dir, stem + ext);
            if (!string.Equals(legacy, path, StringComparison.OrdinalIgnoreCase))
                yield return legacy;
        }
    }

    /// <summary>
    /// Moves <c>.dir2site/{stem}/</c> and renames the files inside that carry the stem in their own
    /// names.
    /// </summary>
    private static void MovePreviews(string oldPath, string newPath)
    {
        var oldStem = Path.GetFileNameWithoutExtension(oldPath);
        var newStem = Path.GetFileNameWithoutExtension(newPath);

        var oldDir = PreviewDir(oldPath, oldStem);
        var newDir = PreviewDir(newPath, newStem);

        if (!Directory.Exists(oldDir) || Directory.Exists(newDir)) return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(newDir)!);
            Directory.Move(oldDir, newDir);
        }
        catch
        {
            return;
        }

        if (string.Equals(oldStem, newStem, StringComparison.Ordinal)) return;

        // preview-Portrait.webp, Portrait_q90.webp, Portrait_pages/, Portrait.bookreader.json —
        // every one of them spells the stem out again, so the folder moving is only half of it.
        foreach (var entry in Entries(newDir))
        {
            var name = Path.GetFileName(entry);
            if (RenamedAsset(name, oldStem, newStem) is not { } newName) continue;

            var renamed = Path.Combine(newDir, newName);
            if (File.Exists(renamed) || Directory.Exists(renamed)) continue;

            try
            {
                if (Directory.Exists(entry)) Directory.Move(entry, renamed);
                else File.Move(entry, renamed);
            }
            catch
            {
                // One asset that won't move is not worth abandoning the rest for: whatever is left
                // under the old name is regenerated on the next run, which is the same outcome as
                // never having had it.
            }
        }

        RepointBookReader(newDir, oldStem, newStem);
    }

    /// <summary>
    /// Rewrites the page addresses inside a PDF's reader manifest.
    /// </summary>
    /// <remarks>
    /// The only asset here whose <em>contents</em> name the stem: <c>PreviewGenerator</c> writes
    /// each page as <c>{stem}_pages/page-0001.webp</c>, and the site emits those verbatim. Renaming
    /// the folder and the file left every one of them pointing at a directory that no longer
    /// existed, so the reader opened to a document of broken images.
    ///
    /// It could not heal itself either. The PDF's own timestamp doesn't move when it is renamed, so
    /// the short-circuit in <c>GeneratePdfPreviewsAndPages</c> found preview, preview-lg and the
    /// manifest all present and current, and never rebuilt the pages.
    ///
    /// Rewritten as JSON rather than as text, which is the only way it can be right. The manifest is
    /// written through a serializer whose encoder escapes every non-ASCII character to a numeric
    /// sequence, so an accented or CJK name is not present in the file as anyone would type it — a
    /// substitution searching for the name as typed matches nothing, and the reader goes on pointing
    /// at a folder that has gone. The same gap in reverse lets a new name carrying a quote or a
    /// backslash in unescaped, which leaves the file unparseable and the reader silently empty.
    ///
    /// Reading it the way <c>SiteGenerator.BuildBookReaderData</c> already does drops the escaping
    /// question entirely: both ends are plain strings by the time we see them.
    ///
    /// Anchored to the front of the address for the same reason <see cref="RenamedAsset"/> is: a
    /// document called <c>page</c> or <c>webp</c> would otherwise rewrite its own addresses into
    /// nonsense.
    /// </remarks>
    private static void RepointBookReader(string previewDir, string oldStem, string newStem)
    {
        var manifest = Path.Combine(previewDir, $"{newStem}.bookreader.json");
        if (!File.Exists(manifest)) return;

        try
        {
            var document = JsonNode.Parse(File.ReadAllText(manifest));
            if (document?["data"]?.AsArray() is not { } spreads) return;

            var oldPrefix = $"{oldStem}_pages/";
            var newPrefix = $"{newStem}_pages/";
            var changed = false;

            foreach (var spread in spreads)
            {
                if (spread is not JsonArray pages) continue;

                foreach (var page in pages)
                {
                    if (page?["uri"] is not JsonValue address) continue;

                    var uri = address.GetValue<string>();
                    if (!uri.StartsWith(oldPrefix, StringComparison.Ordinal)) continue;

                    page["uri"] = newPrefix + uri[oldPrefix.Length..];
                    changed = true;
                }
            }

            if (changed)
                File.WriteAllText(manifest,
                    document!.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // A manifest we cannot rewrite is one the next run regenerates once the pages are
            // missing — worse than fixing it, better than abandoning the rename half done.
        }
    }

    /// <summary>
    /// What a generated asset should be called once its artifact has been renamed, or null when the
    /// name is not one we produced and so not ours to touch.
    /// </summary>
    /// <remarks>
    /// Rebuilt from the convention rather than substituted into. Replacing the stem wherever it
    /// appeared quietly wrecked short names, which is the ordinary case for a scanned collection:
    /// renaming <c>2.jpg</c> to <c>3.jpg</c> turned <c>.dir2site</c> into <c>.dir3site</c>, and
    /// <c>e.jpg</c> to <c>f.jpg</c> turned <c>preview-e.webp</c> into <c>prfvifw-f.wfbp</c>. Any stem
    /// that is a substring of <c>preview</c>, <c>webp</c>, <c>lg</c> or <c>dir2site</c> hit it.
    ///
    /// The prefixed forms are tested first: a photo actually named <c>preview.jpg</c> has assets
    /// called <c>preview-preview.webp</c>, and the bare rule would rename the wrong half of it.
    /// </remarks>
    internal static string? RenamedAsset(string name, string oldStem, string newStem)
    {
        // preview-{stem}.webp and preview-lg-{stem}.webp
        foreach (var prefix in new[] { "preview-lg-", "preview-" })
        {
            if (!name.StartsWith(prefix, StringComparison.Ordinal)) continue;

            var rest = name[prefix.Length..];
            if (rest.StartsWith(oldStem, StringComparison.Ordinal))
                return prefix + newStem + rest[oldStem.Length..];
        }

        // {stem}_q90.webp, {stem}_pages/, {stem}.bookreader.json — anything the stem opens.
        if (name.StartsWith(oldStem, StringComparison.Ordinal))
            return newStem + name[oldStem.Length..];

        return null;
    }

    private static string PreviewDir(string artifactPath, string stem) =>
        Path.Combine(Path.GetDirectoryName(artifactPath) ?? string.Empty, ".dir2site", stem);

    private static IEnumerable<string> Entries(string dir)
    {
        try { return [.. Directory.EnumerateFileSystemEntries(dir)]; }
        catch { return []; }
    }

    /// <summary>
    /// Brings the sidecar's own contents into line with the new name — the preview paths, and the
    /// caption if it was ours to change.
    /// </summary>
    private static void RepointYaml(string yamlPath, string oldPath, string newPath)
    {
        var updates = new List<KeyValuePair<string, string>>();

        var newStem = Path.GetFileNameWithoutExtension(newPath);
        var (preview, previewLarge) = PreviewGenerator.CanonicalPreviewNames(newStem);

        var existing = Read(yamlPath);

        // Only what we generated. A hand-written path points at an image the user chose, which has
        // nothing to do with this file's name and does not move because it was renamed.
        if (PreviewGenerator.IsCanonicalPreview(oldPath, existing.GetValueOrDefault("preview")))
            updates.Add(new("preview", preview));

        if (PreviewGenerator.IsCanonicalPreview(oldPath, existing.GetValueOrDefault("previewLarge")))
            updates.Add(new("previewLarge", previewLarge));

        // A photo's full-resolution web copy has no fixed name — it is the stem plus a quality
        // suffix — so both halves of its path move: the folder, and the file within it. Rebuilt the
        // same way the files on disk were, so the yaml cannot disagree with them.
        var oldStem = Path.GetFileNameWithoutExtension(oldPath);
        var oldFolder = $".dir2site/{oldStem}/";

        if (existing.GetValueOrDefault("image") is { Length: > 0 } image
            && image.StartsWith(oldFolder, StringComparison.OrdinalIgnoreCase))
        {
            var assetName = image[oldFolder.Length..];
            updates.Add(new("image",
                $".dir2site/{newStem}/{RenamedAsset(assetName, oldStem, newStem) ?? assetName}"));
        }

        if (RederivedCaption(existing.GetValueOrDefault("caption"), oldPath, newPath) is { } caption)
            updates.Add(new("caption", caption));

        YamlParser.UpdateFields(yamlPath, updates);
    }

    /// <summary>
    /// The caption this file should now have, or null to leave it alone.
    /// </summary>
    /// <remarks>
    /// A scaffolded sidecar seeds its caption from the filename, so an untouched caption is just the
    /// old name spelled nicely — and after a rename it announces the wrong thing on the card. But a
    /// caption the user wrote is the whole point of having the field, and rewriting it because they
    /// tidied a filename would be worse than leaving it stale.
    ///
    /// Told apart by asking the very function that produced it. Matching what
    /// <c>PrettifyFilename</c> makes of the <em>old</em> name means nobody has touched it since it
    /// was scaffolded; anything else is theirs. A caption written by an older version of that
    /// function simply won't match and is left alone, which is the right way round to be wrong.
    /// </remarks>
    internal static string? RederivedCaption(string? current, string oldPath, string newPath)
    {
        if (string.IsNullOrWhiteSpace(current)) return null;

        var wasDerived = string.Equals(current, DerivedCaption(oldPath), StringComparison.Ordinal);
        if (!wasDerived) return null;

        var rederived = DerivedCaption(newPath);
        return string.Equals(rederived, current, StringComparison.Ordinal) ? null : rederived;
    }

    // Videos have their provider suffix trimmed before prettifying, so a ".url" caption only ever
    // matches if we trim it the same way here.
    private static string DerivedCaption(string path) =>
        YamlParser.PrettifyFilename(
            Path.GetExtension(path).Equals(".url", StringComparison.OrdinalIgnoreCase)
                ? YamlParser.StripVideoProviderSuffix(path)
                : path);

    /// <summary>The sidecar's top-level scalars, for deciding what is safe to change.</summary>
    private static Dictionary<string, string> Read(string yamlPath)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        string[] lines;
        try { lines = File.ReadAllLines(yamlPath); }
        catch { return values; }

        foreach (var line in lines)
        {
            if (line.Length == 0 || char.IsWhiteSpace(line[0]) || line[0] == '#') continue;

            var colon = line.IndexOf(':');
            if (colon <= 0) continue;

            values[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        return values;
    }
}
