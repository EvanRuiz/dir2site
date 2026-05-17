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
            }
        }
    }

    public ArtifactViewModel? ArtifactViewModel => _artifactViewModel;

    /// <summary>Any errors encountered while parsing the YAML file.</summary>
    public List<string> YamlErrors { get; } = new();

    public ObservableCollection<DirectoryTreeItem> Children { get; } = new();

    public DirectoryTreeItem() { }

    public DirectoryTreeItem(string path)
    {
        FullPath = path;
        Name = Path.GetFileName(path) is { Length: > 0 } n ? n : path;
        IsDirectory = Directory.Exists(path);
    }
}