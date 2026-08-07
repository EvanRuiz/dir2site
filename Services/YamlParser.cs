// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using dir2site.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace dir2site.Services;

public static class YamlParser
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private static readonly IDeserializer DictDeserializer = new DeserializerBuilder()
        .Build();

    private static readonly ISerializer Serializer = new SerializerBuilder()
        .Build();

    private static readonly ISerializer CamelCaseSerializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();

    // Maps media file extensions to their artifact type name (lowercase).
    public static readonly IReadOnlyDictionary<string, string> ExtensionToType =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // Raster images → photo
            { ".jpg",  "photo" },
            { ".jpeg", "photo" },
            { ".png",  "photo" },
            { ".tif",  "photo" },
            { ".tiff", "photo" },
            { ".bmp",  "photo" },
            { ".webp", "photo" },
            { ".gif",  "photo" },

            // Deep zoom image sets → deepzoom
            { ".dzi",  "deepzoom" },

            // Documents
            { ".pdf",  "pdf"      },
            { ".md",   "markdown" },

            // Windows internet shortcuts → video, but only when they point at a provider we can
            // embed; see CreateDefaultYamlMeta.
            { ".url",  "video"    },
        };

    /// <summary>
    /// Looks for a YAML meta file next to <paramref name="filePath"/>.
    /// If none exists and the file extension is a known media type, creates one from the default template.
    /// Returns the parsed <see cref="Artifact"/> (or null), and populates <paramref name="errors"/> on failure.
    /// </summary>
    public static Artifact? TryParseYamlMeta(string filePath, List<string> errors)
    {
        var yamlPath = FindYamlMeta(filePath);

        if (yamlPath is null)
            yamlPath = CreateDefaultYamlMeta(filePath, errors);

        if (yamlPath is null)
            return null;

        string yaml;
        try
        {
            yaml = File.ReadAllText(yamlPath);
        }
        catch (Exception ex)
        {
            errors.Add($"Could not read '{yamlPath}': {ex.Message}");
            return null;
        }

        // Held back until every route has failed. A model that didn't fit is how the fallback chain
        // below finds the one that does, so reporting each miss as it happens would bury the file
        // that really is broken under complaints about files that parsed perfectly.
        var attemptErrors = new List<string>();

        // The type token names the model, so use it when there is one.
        if (PeekTypeToken(yaml) is { } token && TypeTokenToParser.TryGetValue(token, out var parse))
        {
            try
            {
                if (parse(yaml) is { } artifact)
                {
                    ReportUnknownKeys(yaml, yamlPath, artifact.GetType(), errors);
                    return artifact;
                }
            }
            catch (Exception ex) { attemptErrors.Add($"[{token}] {ex.Message}"); }
        }

        // Try each concrete type from most-specific to least-specific.
        foreach (var attempt in ParseAttempts)
        {
            try
            {
                if (attempt(yaml) is { } artifact)
                {
                    ReportUnknownKeys(yaml, yamlPath, artifact.GetType(), errors);
                    return artifact;
                }
            }
            catch (Exception ex)
            {
                attemptErrors.Add($"[{attempt.Method.ReturnType.Name}] {ex.Message}");
            }
        }

        errors.AddRange(attemptErrors);
        errors.Add($"Could not parse '{yamlPath}' into any known model type.");
        return null;
    }

    /// <summary>
    /// Reports keys the model doesn't declare. The deserializer ignores whatever it doesn't
    /// recognise (see its construction above), which keeps an unfamiliar file readable but means a
    /// misspelling — <c>parentcover</c>, <c>grandparent_cover</c> — is accepted and then does
    /// nothing at all, with the artifact looking exactly as if the setting had never been written.
    /// </summary>
    /// <remarks>
    /// Read back as a plain map, so a commented-out setting is not a key and says nothing.
    /// </remarks>
    private static void ReportUnknownKeys(string yaml, string yamlPath, Type modelType, List<string> errors)
    {
        Dictionary<object, object>? doc;
        // A document that won't read as a map has a real problem, and it isn't this one.
        try { doc = DictDeserializer.Deserialize<Dictionary<object, object>>(yaml); }
        catch { return; }
        if (doc == null) return;

        var declared = DeclaredKeys(modelType);
        var unknown = doc.Keys
            .Select(k => k?.ToString())
            .Where(k => !string.IsNullOrEmpty(k) && !declared.Contains(k))
            .ToList();

        if (unknown.Count == 0) return;

        var subject = unknown.Count == 1 ? "is not a setting" : "are not settings";
        errors.Add(
            $"{Path.GetFileName(yamlPath)}: {string.Join(", ", unknown)} {subject} dir2site knows, so nothing was done with it.");
    }

    // Reflected once per model — the same handful of types are parsed for every file in a project.
    private static readonly ConcurrentDictionary<Type, HashSet<string>> DeclaredKeysByType = new();

    /// <summary>The keys a model accepts, spelled the way the deserializer expects to see them.</summary>
    private static HashSet<string> DeclaredKeys(Type modelType) =>
        DeclaredKeysByType.GetOrAdd(modelType, static type =>
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.GetCustomAttribute<YamlIgnoreAttribute>() != null) continue;

                var member = property.GetCustomAttribute<YamlMemberAttribute>();
                // An alias only escapes the naming convention when it says so — the same rule the
                // deserializer applies, which is why "url-text" has to turn it off.
                keys.Add(member?.Alias is { Length: > 0 } alias
                    ? member.ApplyNamingConventions ? CamelCaseNamingConvention.Instance.Apply(alias) : alias
                    : CamelCaseNamingConvention.Instance.Apply(property.Name));
            }
            return keys;
        });

    /// <summary>
    /// Maps the yaml's <c>type:</c> token to the model that actually holds that type's fields.
    /// </summary>
    /// <remarks>
    /// <see cref="ParseAttempts"/> cannot do this on its own. The deserializer ignores unmatched
    /// properties (see its construction above), so the first attempt always succeeds and every
    /// artifact came back as a <see cref="Deepzoom"/> that happened to carry the right value in
    /// <see cref="Artifact.Type"/>. That went unnoticed because the generator switches on
    /// <c>Type</c> rather than on the CLR type, but it silently discarded every subtype-specific
    /// field — a photo's <c>photographer</c>, a PDF's <c>author</c> — and made the
    /// <c>is MarkdownPage</c> test in DirectoryTreeItem permanently false. Dispatching on the token
    /// first fixes all of those; <see cref="ParseAttempts"/> remains the fallback for a yaml with
    /// no <c>type:</c> or an unrecognized one, so nothing that parses today stops parsing.
    /// </remarks>
    private static readonly IReadOnlyDictionary<string, Func<string, Artifact>> TypeTokenToParser =
        new Dictionary<string, Func<string, Artifact>>(StringComparer.OrdinalIgnoreCase)
        {
            { "photo",     yaml => Deserializer.Deserialize<Photo>(yaml)               },
            { "deepzoom",  yaml => Deserializer.Deserialize<Deepzoom>(yaml)            },
            { "pdf",       yaml => Deserializer.Deserialize<Pdf>(yaml)                 },
            { "markdown",  yaml => Deserializer.Deserialize<MarkdownPage>(yaml)        },
            { "video",     yaml => Deserializer.Deserialize<Video>(yaml)               },
            { "directory", yaml => Deserializer.Deserialize<DirectoryCollection>(yaml) },
        };

    // Reads just the type token, tolerating anything else in the document being unparseable.
    private static string? PeekTypeToken(string yaml)
    {
        try
        {
            var doc = DictDeserializer.Deserialize<Dictionary<object, object>>(yaml);
            if (doc != null && doc.TryGetValue("type", out var value) && value is string token)
            {
                token = token.Trim();
                return token.Length > 0 ? token : null;
            }
        }
        catch
        {
            // Fall through to ParseAttempts, which reports its own errors.
        }

        return null;
    }

    // Ordered most-specific → least-specific so the right subtype is chosen.
    private static readonly Func<string, Artifact>[] ParseAttempts =
    [
        yaml => Deserializer.Deserialize<Deepzoom>(yaml),
        yaml => Deserializer.Deserialize<Photo>(yaml),
        yaml => Deserializer.Deserialize<Pdf>(yaml),
        yaml => Deserializer.Deserialize<Article>(yaml),
        yaml => Deserializer.Deserialize<Document>(yaml),
        yaml => Deserializer.Deserialize<MarkdownPage>(yaml),
        yaml => Deserializer.Deserialize<Artifact>(yaml),
    ];

    /// <summary>
    /// Updates (or adds) the preview and previewLarge keys in an existing YAML meta file, leaving
    /// the rest of the document exactly as the user wrote it — comments, key order and all.
    /// </summary>
    /// <remarks>
    /// This runs for every artifact on every generate, so it is the app's most frequent YAML
    /// write. It used to round-trip the file through a dictionary, which kept other fields' values
    /// but discarded every comment and any formatting the user had applied. Sidecars are exactly
    /// the files people annotate by hand, so the edit is now surgical
    /// (<see cref="YamlDocumentEditor"/>), with the old whole-file rewrite kept only as a fallback
    /// for documents that cannot be edited in place.
    /// </remarks>
    /// <param name="extra">
    /// Further keys to bring into line while we are already rewriting the file — used by videos,
    /// whose id and provider are re-derived from the .url on every run and would otherwise leave
    /// the yaml quietly disagreeing with the page after the shortcut is re-pointed.
    /// </param>
    public static void UpdatePreviewFields(
        string yamlPath,
        string previewFileName,
        string previewLargeFileName,
        string? imageFileName = null,
        IEnumerable<KeyValuePair<string, string>>? extra = null)
    {
        string yaml;
        try { yaml = File.ReadAllText(yamlPath); }
        catch { return; }

        var updates = new List<KeyValuePair<string, string>>
        {
            new("preview", previewFileName),
            new("previewLarge", previewLargeFileName),
        };
        if (imageFileName != null)
            updates.Add(new("image", imageFileName));
        if (extra != null)
            updates.AddRange(extra);

        var editor = YamlDocumentEditor.TryLoad(yaml);
        if (editor != null && editor.SetAll(updates))
        {
            // Nothing to do when the values already match — rewriting would only churn mtimes
            // and dirty the file for no reason.
            if (editor.IsModified)
                File.WriteAllText(yamlPath, editor.Text);
            return;
        }

        FallbackRewrite(yamlPath, yaml, updates);
    }

    // Last resort for a file the editor cannot splice (unparseable, or a non-scalar sitting on one
    // of these keys). Preserves other fields' values but not comments or formatting.
    private static void FallbackRewrite(
        string yamlPath, string yaml, List<KeyValuePair<string, string>> updates)
    {
        Dictionary<object, object> doc;
        try { doc = DictDeserializer.Deserialize<Dictionary<object, object>>(yaml) ?? new(); }
        catch { doc = new(); }

        foreach (var (key, value) in updates)
            doc[key] = value;

        File.WriteAllText(yamlPath, Serializer.Serialize(doc));
    }

    /// <summary>
    /// Writes the project config back to <paramref name="configPath"/>, changing only the values
    /// that actually differ and leaving the user's comments, key order and formatting in place.
    /// Creates the file from scratch when it doesn't exist yet.
    /// </summary>
    /// <remarks>
    /// Generate Site calls this every run, so a whole-file rewrite here meant a hand-edited
    /// dir2site.yaml lost its comments the first time the user clicked Generate.
    /// </remarks>
    public static void SaveDir2SiteConfig(string configPath, Dir2SiteModel config)
    {
        string existing;
        try { existing = File.Exists(configPath) ? File.ReadAllText(configPath) : ""; }
        catch { existing = ""; }

        if (existing.Length > 0 && YamlDocumentEditor.TryLoad(existing) is { } editor && Apply(editor, config))
        {
            if (editor.IsModified)
                File.WriteAllText(configPath, editor.Text);
            return;
        }

        File.WriteAllText(configPath, SerializeToYaml(config));
    }

    // Ordered as the model declares them, which is also the order a freshly created file uses,
    // so an appended key lands where a reader would expect it.
    private static bool Apply(YamlDocumentEditor editor, Dir2SiteModel c) =>
        editor.Set("title", c.Title) &&
        editor.Set("footer", c.Footer) &&
        editor.Set("logo", c.Logo) &&
        editor.Set("primaryColor", c.PrimaryColor) &&
        editor.Set("secondaryColor", c.SecondaryColor) &&
        editor.Set("backgroundColor", c.BackgroundColor) &&
        editor.Set("navbarDark", c.NavbarDark) &&
        editor.Set("siteUrl", c.SiteUrl) &&
        editor.Set("pdfResizeEnabled", c.PdfResizeEnabled) &&
        editor.Set("pdfMaxWidth", c.PdfMaxWidth) &&
        editor.Set("pdfQuality", c.PdfQuality);

    public static T DeserializeAs<T>(string yaml) where T : new() =>
        Deserializer.Deserialize<T>(yaml);

    public static string SerializeToYaml<T>(T obj) =>
        CamelCaseSerializer.Serialize(obj);

    public static string? FindYamlMetaPath(string filePath) => FindYamlMeta(filePath);

    /// <summary>
    /// Returns the path of an existing YAML meta file for <paramref name="filePath"/>, or null.
    /// Checks the new convention first (<c>filename.ext.yaml</c>) then the legacy form
    /// (<c>stem.yaml</c>) for backward compatibility.
    /// </summary>
    private static string? FindYamlMeta(string filePath)
    {
        var dir      = Path.GetDirectoryName(filePath) ?? string.Empty;
        var fileName = Path.GetFileName(filePath);
        var stem     = Path.GetFileNameWithoutExtension(filePath);

        foreach (var ext in new[] { ".yaml", ".yml" })
        {
            // New convention: Portrait.jpg → Portrait.jpg.yaml
            var fullCandidate = Path.Combine(dir, fileName + ext);
            if (File.Exists(fullCandidate))
                return fullCandidate;

            // Legacy fallback: Portrait.jpg → Portrait.yaml (guard against self-reference)
            var stemCandidate = Path.Combine(dir, stem + ext);
            if (File.Exists(stemCandidate) &&
                !string.Equals(stemCandidate, filePath, StringComparison.OrdinalIgnoreCase))
                return stemCandidate;
        }

        return null;
    }

    /// <summary>
    /// Creates a default YAML meta file at <c>filePath + ".yaml"</c> if the file's extension
    /// is a recognized media type. Returns the created path, or null if skipped or on error.
    /// </summary>
    private static string? CreateDefaultYamlMeta(string filePath, List<string> errors)
    {
        var ext = Path.GetExtension(filePath);
        if (!ExtensionToType.TryGetValue(ext, out var artifactType))
            return null;

        InternetShortcutParser.VideoRef? video = null;
        if (artifactType == "video")
        {
            // A .url earns a yaml only when it points at a provider we can embed. An ordinary
            // web bookmark filed alongside some photos is not an error — it just isn't catalogued,
            // the same as any other unrecognized file.
            video = InternetShortcutParser.TryReadVideo(filePath)?.Video;
            if (video is null)
                return null;
        }

        var caption  = PrettifyFilename(
            artifactType == "video" ? StripVideoProviderSuffix(filePath) : filePath);
        var template = BuildTemplate(artifactType, caption, video);

        var yamlMetaPath = filePath + ".yaml";
        try
        {
            File.WriteAllText(yamlMetaPath, template);
            return yamlMetaPath;
        }
        catch (Exception ex)
        {
            errors.Add($"Could not create yaml meta '{yamlMetaPath}': {ex.Message}");
            return null;
        }
    }

    // The video arm needs the shortcut's target, which the caller has already parsed — re-reading
    // the .url here would just be a second chance to disagree with it.
    private static string BuildTemplate(
        string artifactType,
        string caption,
        InternetShortcutParser.VideoRef? video = null) => artifactType switch
    {
        "photo"    => $"type: photo\ncaption: {caption}\ncredit:\nphotographer:\n",
        "deepzoom" => $"type: deepzoom\ncaption: {caption}\ncredit:\nphotographer:\n",
        "pdf"      => $"type: pdf\ncaption: {caption}\ncredit:\nauthor:\npublishOriginal: false\n",
        "markdown" => $"type: markdown\ncaption: {caption}\ncredit:\n",
        // url-text is left blank on purpose: the player carries YouTube's own affordance, so a card
        // only gets an outbound link when the site owner asks for one by filling this in.
        "video"    => $"type: video\ncaption: {caption}\ncredit:\nurl-text:\n"
                      + $"provider: {video?.Provider ?? InternetShortcutParser.YouTube}\n"
                      + $"videoId: {video?.VideoId}\n"
                      + $"start:{(video?.Start is { } s ? $" {s}" : "")}\n",
        _          => $"type: {artifactType}\ncaption: {caption}\ncredit:\n",
    };

    /// <summary>
    /// Drops the provider suffix a browser appends when it saves a video shortcut, so
    /// "Never Gonna Give You Up - YouTube.url" is captioned "Never Gonna Give You Up".
    /// </summary>
    /// <remarks>
    /// Returns a path rather than a stem so it can be handed straight to
    /// <see cref="PrettifyFilename"/>, which is what turns the trimmed name into a caption. Only
    /// the suffix goes: a video legitimately titled "YouTube at 20" keeps its name, because the
    /// match is anchored to the end and requires the separator.
    /// </remarks>
    private static string StripVideoProviderSuffix(string filePath)
    {
        const string suffix = " - YouTube";
        var stem = Path.GetFileNameWithoutExtension(filePath);
        if (!stem.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return filePath;

        var trimmed = stem[..^suffix.Length].TrimEnd();
        // A shortcut named nothing but the suffix still needs something to be called.
        return trimmed.Length == 0 ? filePath : trimmed;
    }

    /// <summary>
    /// Converts a filename stem into a human-readable caption using simple deterministic rules:
    /// underscores and hyphens become spaces, camelCase boundaries are split,
    /// and each word is title-cased.
    /// </summary>
    /// <example>
    /// "annual-report"        → "Annual-Report"
    /// "Artist - Song"        → "Artist - Song"
    /// "my_beautiful_photo"   → "My Beautiful Photo"
    /// "myBeautifulPhoto"     → "My Beautiful Photo"
    /// "TheQuickBrownFox"     → "The Quick Brown Fox"
    /// "IMG_1234"             → "IMG 1234"
    /// "XMLParser"            → "XML Parser"
    /// </example>
    public static string PrettifyFilename(string filePath)
    {
        var stem = Path.GetFileNameWithoutExtension(filePath);
        if (string.IsNullOrWhiteSpace(stem))
            return stem;

        // Whether each hyphen was written with space around it, which is the difference between a
        // compound word and a real separator: "annual-report" is one name, "Artist - Song" is two
        // parts — and the latter is the shape most video titles arrive in.
        var spacedSeparators = new List<bool>();
        for (var i = 0; i < stem.Length; i++)
        {
            if (stem[i] != '-') continue;
            spacedSeparators.Add(
                (i > 0 && char.IsWhiteSpace(stem[i - 1])) ||
                (i + 1 < stem.Length && char.IsWhiteSpace(stem[i + 1])));
        }

        // Process each dash-separated segment independently, preserving the dash as a separator
        var segments = stem.Split('-').Select(segment =>
        {
            var s = segment.Replace('_', ' ');
            s = Regex.Replace(s, @"([a-z])([A-Z])", "$1 $2");
            s = Regex.Replace(s, @"([A-Z]{2,})([A-Z][a-z])", "$1 $2");
            s = Regex.Replace(s, @"\s+", " ").Trim();
            if (s.Length == 0) return segment;
            return string.Join(' ', s.Split(' ')
                .Select(w =>
                {
                    if (w.Length == 0) return w;
                    // Preserve all-caps abbreviations (e.g., IMG, XML, NASA)
                    if (w.All(c => !char.IsLetter(c) || char.IsUpper(c))) return w;
                    return char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant();
                }));
        });

        // Rejoin with the spacing each hyphen was written with.
        var parts = segments.ToList();
        var result = new StringBuilder(parts[0]);
        for (var i = 1; i < parts.Count; i++)
        {
            result.Append(spacedSeparators[i - 1] ? " - " : "-").Append(parts[i]);
        }
        return result.ToString();
    }
}
