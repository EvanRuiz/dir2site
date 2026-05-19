namespace dir2site.Models;

public class Dir2SiteModel
{
    public string Title           { get; set; } = string.Empty;
    public string Footer          { get; set; } = string.Empty;
    public string Logo            { get; set; } = string.Empty;
    public string PrimaryColor    { get; set; } = "#333333";
    public string SecondaryColor  { get; set; } = "#666666";
    public string BackgroundColor { get; set; } = "#ffffff";
    public bool   NavbarDark      { get; set; } = true;
    public string SiteUrl         { get; set; } = string.Empty;
}
