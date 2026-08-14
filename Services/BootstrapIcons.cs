// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia.Platform;

namespace dir2site.Services;

/// <summary>One icon: the name that goes in the yaml, and the character that draws it.</summary>
public sealed record IconChoice(string Name, string Glyph);

/// <summary>
/// Every icon the vendored Bootstrap Icons set offers, for the footer editor to complete against.
/// </summary>
/// <remarks>
/// Read from the stylesheet that ships with the font rather than kept as a list here, so the names
/// cannot drift from the glyphs actually available: upgrading the vendored set is then a matter of
/// dropping in the new folder, and nobody has to remember to retype two thousand names.
/// </remarks>
public static class BootstrapIcons
{
    private const string CssUri =
        "avares://dir2site/Assets/icons/bootstrap-icons-1.13.1/font/bootstrap-icons.css";

    /// <summary>
    /// The font as an sfnt, which is what Skia can read — the site's WOFF it cannot. See the note
    /// in <c>Assets/icons/bootstrap-icons-1.13.1/app-font/README.md</c>.
    /// </summary>
    public const string FontFamily =
        "avares://dir2site/Assets/icons/bootstrap-icons-1.13.1/app-font#bootstrap-icons";

    // ".bi-youtube::before { content: "\f62b"; }" — the name and the codepoint that draws it.
    private static readonly Regex IconPattern = new(
        @"^\.(bi-[a-z0-9-]+)::before\s*\{\s*content:\s*""\\([0-9a-fA-F]+)""",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static IReadOnlyList<IconChoice>? _icons;

    /// <summary>
    /// The icons, alphabetical by name, each carrying its <c>bi-</c> prefix — the spelling that goes
    /// in the yaml. Empty if the stylesheet can't be read, which leaves the box an ordinary text
    /// field rather than taking the dialog down over a completion list.
    /// </summary>
    public static IReadOnlyList<IconChoice> Icons => _icons ??= Load();

    /// <summary>Just the names, for anything that doesn't draw them.</summary>
    public static IReadOnlyList<string> Names => [.. Icons.Select(i => i.Name)];

    private static IReadOnlyDictionary<string, string>? _byName;

    /// <summary>
    /// The glyph for a name, or empty when the name isn't one — a half-typed name, or a misspelling
    /// the generator would go on to warn about.
    /// </summary>
    /// <param name="name">With or without the <c>bi-</c> prefix, as the yaml allows either.</param>
    public static string GlyphFor(string? name)
    {
        var key = (name ?? string.Empty).Trim();
        if (key.Length == 0) return string.Empty;
        if (!key.StartsWith("bi-", StringComparison.Ordinal)) key = "bi-" + key;

        _byName ??= Icons.ToDictionary(i => i.Name, i => i.Glyph, StringComparer.Ordinal);
        return _byName.TryGetValue(key, out var glyph) ? glyph : string.Empty;
    }

    private static IReadOnlyList<IconChoice> Load()
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri(CssUri));
            using var reader = new StreamReader(stream);
            var css = reader.ReadToEnd();

            return [.. IconPattern.Matches(css)
                .Select(m => new IconChoice(
                    m.Groups[1].Value,
                    char.ConvertFromUtf32(
                        int.Parse(m.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture))))
                .DistinctBy(i => i.Name, StringComparer.Ordinal)
                .OrderBy(i => i.Name, StringComparer.Ordinal)];
        }
        catch
        {
            return [];
        }
    }
}
