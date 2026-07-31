// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using YamlDotNet.Serialization;

namespace dir2site.Models;

public enum ArtifactType
{
    Photo,
    Deepzoom,
    Directory,
    Pdf,
    // Serialized as "markdown" — the camelCase of the enum name must match the YAML type token.
    Markdown,
    Video,
}

public class Artifact
{
    public string? Id {get; set;}
    public ArtifactType Type {get; set;}
    public string? Caption {get; set;}
    public string? Credit {get; set;}

    // ApplyNamingConventions is off because the deserializer's camelCase convention is otherwise
    // applied to the alias as well, turning "url-text" back into "urlText" — so the hyphenated key
    // this alias exists to support never actually matched anything.
    [YamlMember(Alias = "url-text", ApplyNamingConventions = false)]
    public string? UrlText {get; set;}

    public string? Date {get; set;}

    public string? Preview {get; set;}
    public string? PreviewLarge {get; set;}

    /// <summary>
    /// Marks this artifact as the picture for its folder's card, in place of the one the generator
    /// would otherwise pick. Without it a collection is represented by whichever photo happens to
    /// sort first, which is rarely the one that says what the collection is.
    ///
    /// Only meaningful on an artifact that has a preview, and only for the folder it sits in —
    /// marking something deep in a subtree does not make it the cover of everything above it.
    /// </summary>
    public bool Cover {get; set;}

    // Runtime Only — not persisted to YAML
    [YamlIgnore] public string? RootFolder {get; set;}
    [YamlIgnore] public string? TraversalRoot {get; set;}
}