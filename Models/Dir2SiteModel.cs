// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace dir2site.Models;

/// <summary>
/// The project's own settings, as they sit in dir2site.yaml.
/// </summary>
/// <remarks>
/// Observable because the Site Settings panel binds straight into it and the app has to know when a
/// value has been edited — the config used to be written only from inside Generate Site, which
/// stops being a place anything happens once auto-generate takes that button away.
///
/// <see cref="ObservableObject"/> contributes an event and no properties, so what YamlDotNet
/// serializes is unchanged; the file keeps the same shape it always had.
/// </remarks>
public partial class Dir2SiteModel : ObservableObject
{
    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _footer = string.Empty;
    [ObservableProperty] private string _logo = string.Empty;
    [ObservableProperty] private string _primaryColor = "#333333";
    [ObservableProperty] private string _secondaryColor = "#666666";
    [ObservableProperty] private string _backgroundColor = "#ffffff";

    /// <summary>
    /// Background of the footer band. Empty follows <see cref="PrimaryColor"/>, so a project that
    /// never sets it gets a footer matching its navbar rather than a color it didn't choose.
    /// </summary>
    [ObservableProperty] private string _footerColor = string.Empty;

    [ObservableProperty] private bool _navbarDark = true;

    /// <summary>
    /// Whether an ordinary card carries the folders its item sits in, on a line above its name. Off
    /// by default, because on a folder page that trail is the breadcrumb bar directly above the
    /// cards, said again once per card. A card promoted onto the home page keeps its trail either
    /// way — nothing else on that page says where the thing lives.
    /// </summary>
    [ObservableProperty] private bool _cardBreadcrumbs = false;

    [ObservableProperty] private string _siteUrl = string.Empty;
    [ObservableProperty] private bool _pdfResizeEnabled = true;
    [ObservableProperty] private int _pdfMaxWidth = 1600;
    [ObservableProperty] private int _pdfQuality = 80;

    /// <summary>
    /// Rows of the multi-column footer. Empty leaves <see cref="Footer"/> as the whole footer,
    /// which is what every project had before columns existed.
    /// </summary>
    [ObservableProperty] private List<FooterItem> _footerItems = [];

    /// <summary>
    /// Deploy targets. Null when the project has never configured one, so an untouched
    /// dir2site.yaml doesn't grow an empty <c>deploy:</c> block it never asked for.
    /// </summary>
    [ObservableProperty] private DeployConfig? _deploy;
}
