// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using dir2site.Models;
using Mapster;

namespace dir2site.ViewModels;

public partial class DirectoryTreeItem : ObservableObject
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _fullPath = string.Empty;

    [ObservableProperty]
    private bool _isDirectory;

    [ObservableProperty]
    private bool _isExpanded;

    private Artifact? _artifact;
    private ArtifactViewModel? _artifactViewModel;

    /// <summary>Parsed YAML metadata for this item, or null if no YAML was found.</summary>
    public Artifact? Artifact
    {
        get => _artifact;
        set
        {
            _artifact = value;
            _artifactViewModel = value.Adapt<ArtifactViewModel?>();
            // Explicitly copy runtime-only fields — Mapster may skip properties
            // tagged with non-Mapster ignore attributes like [YamlIgnore].
            if (_artifactViewModel is { } vm && value is { } art)
            {
                vm.TraversalRoot = art.TraversalRoot;
                vm.RootFolder = art.RootFolder;

                if (art is MarkdownPage)
                    vm.BeginMarkdownRender(FullPath);
            }
        }
    }

    public ArtifactViewModel? ArtifactViewModel => _artifactViewModel;

    /// <summary>
    /// The full path of this folder's <c>index.md</c>, or null. It is prose belonging to the folder
    /// page rather than an item in it, so it is held here instead of among the children — nothing
    /// that walks <see cref="Children"/> should have to know to skip it.
    /// </summary>
    public string? IntroPath { get; set; }

    /// <summary>Any errors encountered while parsing the YAML file.</summary>
    public List<string> YamlErrors { get; } = new();

    /// <summary>
    /// Things in the YAML that parsed but do nothing — a misspelled setting. The artifact loaded,
    /// so this is not an error, and saying so in the same breath would make a typo look like a
    /// failure.
    /// </summary>
    public List<string> YamlWarnings { get; } = new();

    /// <summary>
    /// Both, as one line each for the tree to show. Filled in while the tree is built, before
    /// anything binds to them, so they need no change notification.
    /// </summary>
    public bool HasYamlErrors => YamlErrors.Count > 0;

    public string YamlErrorText => string.Join("  ", YamlErrors);

    public bool HasYamlWarnings => YamlWarnings.Count > 0;

    public string YamlWarningText => string.Join("  ", YamlWarnings);

    public ObservableCollection<DirectoryTreeItem> Children { get; } = new();

    public DirectoryTreeItem() { }

    public DirectoryTreeItem(string path)
    {
        FullPath = path;
        Name = Path.GetFileName(path) is { Length: > 0 } n ? n : path;
        IsDirectory = Directory.Exists(path);
    }
}