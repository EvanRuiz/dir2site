using System;
using System.Collections.Generic;
using System.IO;
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

    /// <summary>
    /// Looks for a YAML meta file next to <paramref name="filePath"/> (same name, .yaml/.yml extension).
    /// If found, tries to deserialize it into the best-fit <see cref="Artifact"/> subtype.
    /// Returns the parsed object (or null), and populates <paramref name="errors"/> on failure.
    /// </summary>
    public static Artifact? TryParseYamlMeta(string filePath, List<string> errors)
    {
        var yamlPath = FindYamlMeta(filePath);
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
        // The first successful parse wins.
        foreach (var attempt in ParseAttempts)
        {
            try
            {
                var artifact = attempt(yaml);
                return artifact;
            }
            catch (Exception ex)
            {
                // Collect but keep trying narrower types
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
        yaml => Deserializer.Deserialize<Article>(yaml),
        yaml => Deserializer.Deserialize<Artifact>(yaml),
    ];

    /// <summary>
    /// Updates (or adds) the preview and previewLarge keys in an existing YAML meta file,
    /// preserving all other fields.
    /// </summary>
    public static void UpdatePreviewFields(string yamlPath, string previewFileName, string previewLargeFileName)
    {
        string yaml;
        try { yaml = File.ReadAllText(yamlPath); }
        catch { return; }

        Dictionary<object, object> doc;
        try { doc = DictDeserializer.Deserialize<Dictionary<object, object>>(yaml) ?? new(); }
        catch { doc = new(); }

        doc["preview"] = previewFileName;
        doc["previewLarge"] = previewLargeFileName;

        File.WriteAllText(yamlPath, Serializer.Serialize(doc));
    }

    public static string? FindYamlMetaPath(string filePath) => FindYamlMeta(filePath);

    private static string? FindYamlMeta(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath) ?? string.Empty;
        var fileName = Path.GetFileName(filePath);
        var stem = Path.GetFileNameWithoutExtension(filePath);

        foreach (var ext in new[] { ".yaml", ".yml" })
        {
            // Prefer {filename}.yaml (e.g. Portrait.jpg → Portrait.jpg.yaml)
            var fullCandidate = Path.Combine(dir, fileName + ext);
            if (File.Exists(fullCandidate))
                return fullCandidate;

            // Fall back to {stem}.yaml (e.g. Portrait.jpg → Portrait.yaml)
            var stemCandidate = Path.Combine(dir, stem + ext);
            if (File.Exists(stemCandidate) &&
                !string.Equals(stemCandidate, filePath, StringComparison.OrdinalIgnoreCase))
                return stemCandidate;
        }

        return null;
    }
}
