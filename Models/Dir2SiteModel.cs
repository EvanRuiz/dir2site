// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.Generic;

namespace dir2site.Models;

public class Dir2SiteModel
{
    public string Title           { get; set; } = string.Empty;
    public string Footer          { get; set; } = string.Empty;
    public string Logo            { get; set; } = string.Empty;
    public string PrimaryColor    { get; set; } = "#333333";
    public string SecondaryColor  { get; set; } = "#666666";
    public string BackgroundColor { get; set; } = "#ffffff";

    /// <summary>
    /// Background of the footer band. Empty follows <see cref="PrimaryColor"/>, so a project that
    /// never sets it gets a footer matching its navbar rather than a colour it didn't choose.
    /// </summary>
    public string FooterColor     { get; set; } = string.Empty;

    public bool   NavbarDark      { get; set; } = true;
    public string SiteUrl         { get; set; } = string.Empty;
    public bool   PdfResizeEnabled { get; set; } = true;
    public int    PdfMaxWidth      { get; set; } = 1600;
    public int    PdfQuality       { get; set; } = 80;

    /// <summary>
    /// Rows of the multi-column footer. Empty leaves <see cref="Footer"/> as the whole footer,
    /// which is what every project had before columns existed.
    /// </summary>
    public List<FooterItem> FooterItems { get; set; } = [];

    /// <summary>
    /// Deploy targets. Null when the project has never configured one, so an untouched
    /// dir2site.yaml doesn't grow an empty <c>deploy:</c> block it never asked for.
    /// </summary>
    public DeployConfig? Deploy { get; set; }
}
