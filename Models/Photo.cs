namespace dir2site.Models;

public class Photo : Artifact
{
    public string? Photographer {get; set;}
    public string? Image {get; set;}
    public string? Overlays {get; set;}
}