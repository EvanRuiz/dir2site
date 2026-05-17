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

    /// <summary>
    /// Looks for a YAML sidecar file next to <paramref name="filePath"/> (same name, .yaml/.yml extension).
    /// If found, tries to deserialize it into the best-fit <see cref="Artifact"/> subtype.
    /// Returns the parsed object (or null), and populates <paramref name="errors"/> on failure.
    /// </summary>
    public static Artifact? TryParseSidecar(string filePath, List<string> errors)
    {
        var yamlPath = FindSidecar(filePath);
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

    private static string? FindSidecar(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(filePath);

        foreach (var ext in new[] { ".yaml", ".yml" })
        {
            var candidate = Path.Combine(dir, stem + ext);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }
}
