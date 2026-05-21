using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

        // Try each concrete type from most-specific to least-specific.
        foreach (var attempt in ParseAttempts)
        {
            try
            {
                var artifact = attempt(yaml);
                return artifact;
            }
            catch (Exception ex)
            {
                errors.Add($"[{attempt.Method.ReturnType.Name}] {ex.Message}");
            }
        }

        errors.Add($"Could not parse '{yamlPath}' into any known model type.");
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
    /// Updates (or adds) the preview and previewLarge keys in an existing YAML meta file,
    /// preserving all other fields.
    /// </summary>
    public static void UpdatePreviewFields(
        string yamlPath,
        string previewFileName,
        string previewLargeFileName,
        string? imageFileName = null)
    {
        string yaml;
        try { yaml = File.ReadAllText(yamlPath); }
        catch { return; }

        Dictionary<object, object> doc;
        try { doc = DictDeserializer.Deserialize<Dictionary<object, object>>(yaml) ?? new(); }
        catch { doc = new(); }

        doc["preview"] = previewFileName;
        doc["previewLarge"] = previewLargeFileName;
        if (imageFileName != null)
            doc["image"] = imageFileName;

        File.WriteAllText(yamlPath, Serializer.Serialize(doc));
    }

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

        var caption  = PrettifyFilename(filePath);
        var template = BuildTemplate(artifactType, caption);

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

    private static string BuildTemplate(string artifactType, string caption) => artifactType switch
    {
        "photo"    => $"type: photo\ncaption: {caption}\ncredit:\nphotographer:\n",
        "deepzoom" => $"type: deepzoom\ncaption: {caption}\ncredit:\nphotographer:\n",
        "pdf"      => $"type: pdf\ncaption: {caption}\ncredit:\nauthor:\npublishOriginal: false\n",
        "markdown" => $"type: markdown\ncaption: {caption}\ncredit:\n",
        _          => $"type: {artifactType}\ncaption: {caption}\ncredit:\n",
    };

    /// <summary>
    /// Converts a filename stem into a human-readable caption using simple deterministic rules:
    /// underscores and hyphens become spaces, camelCase boundaries are split,
    /// and each word is title-cased.
    /// </summary>
    /// <example>
    /// "annual-report"        → "Annual-Report"
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

        return string.Join('-', segments);
    }
}
