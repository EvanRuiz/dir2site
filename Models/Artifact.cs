using System.IO;
using Avalonia.Media.Imaging;
using YamlDotNet.Serialization;

namespace dir2site.Models;

public enum ArtifactType
{
    Photo,
    Deepzoom,
    Directory,
    Pdf,
    MarkdownPage,
}

public class Artifact
{
    public string? Id {get; set;}
    public ArtifactType Type {get; set;}
    public string? Caption {get; set;}
    public string? Credit {get; set;}
    
    [YamlMember(Alias = "url-text")]
    public string? UrlText {get; set;}

    public string? Date {get; set;}

    public string? Preview {get; set;}
    public string? PreviewLarge {get; set;}
    
    // Runtime Only
    public string? RootFolder {get; set;}
}