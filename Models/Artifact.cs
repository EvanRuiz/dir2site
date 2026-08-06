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
    /// The original, unqualified spelling of <see cref="ParentCover"/>. Kept so projects written
    /// before the rename keep the cover they chose; new yaml should say "parent-cover".
    /// </summary>
    public bool Cover {get; set;}

    /// <remarks>
    /// Nullable so that "absent" and "false" are different answers. A project carrying the legacy
    /// <see cref="Cover"/> key otherwise had no way to un-choose its cover: "parent-cover: false"
    /// would be read as the default and the legacy true would win, leaving hand-editing a key the
    /// docs say not to use as the only way out.
    /// </remarks>

    /// <summary>
    /// Marks this artifact as the picture for its own folder's card, in place of the one the
    /// generator would otherwise pick. Without it a collection is represented by whichever photo
    /// happens to sort first, which is rarely the one that says what the collection is.
    ///
    /// Only meaningful on an artifact that has a preview, and only for the folder it sits in.
    /// </summary>
    [YamlMember(Alias = "parent-cover", ApplyNamingConventions = false)]
    public bool? ParentCover {get; set;}

    /// <summary>
    /// The same, one level further up: this artifact becomes the picture for its grandparent's
    /// card. A folder holding nothing but sub-folders has no direct children of its own to choose
    /// from, so this is the only way to say what it should look like.
    ///
    /// It never outranks a real direct child — a folder with its own photos still uses those.
    /// </summary>
    [YamlMember(Alias = "grandparent-cover", ApplyNamingConventions = false)]
    public bool GrandparentCover {get; set;}

    /// <summary>
    /// Either spelling of the parent-cover marker. Written out, "parent-cover" decides — including
    /// when it says false; the legacy "cover" is only consulted when it is absent.
    /// </summary>
    [YamlIgnore] public bool IsParentCover => ParentCover ?? Cover;

    /// <summary>
    /// Also show this artifact on the home page, wherever in the tree it actually lives. The card
    /// links to the artifact's real page — a video plays in place, as it does anywhere else — and
    /// the artifact keeps its ordinary card in its own folder, so nothing moves.
    /// </summary>
    public bool Home {get; set;}

    // Runtime Only — not persisted to YAML
    [YamlIgnore] public string? RootFolder {get; set;}
    [YamlIgnore] public string? TraversalRoot {get; set;}
}