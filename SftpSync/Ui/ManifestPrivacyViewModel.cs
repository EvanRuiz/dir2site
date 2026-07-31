// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia.Controls;
using CommunityToolkit.Mvvm.Input;
using dir2site.SftpSync.Core;
using dir2site.ViewModels;

namespace dir2site.SftpSync.Ui;

/// <summary>
/// Server rules for keeping the deploy manifest unreadable over HTTP. Tucked behind a button
/// rather than shown inline: it matters to the few people whose server isn't Apache, and would be
/// noise for everyone else.
/// </summary>
public partial class ManifestPrivacyViewModel(Window window) : ViewModelBase
{
    /// <summary>The filename the snippets refer to, so they can't drift from the engine.</summary>
    public string ManifestName => SftpSyncService.DefaultManifestFileName;

    public string ApacheSnippet =>
        $"""
         # .htaccess in the web root
         <Files "{ManifestName}">
             Require all denied
         </Files>
         """;

    // $$ so the config's own braces stay literal and {{ }} does the interpolating.
    public string NginxSnippet =>
        $$"""
          location ~ /{{ManifestName.Replace(".", "\\.")}}$ {
              deny all;
          }
          """;

    public string CaddySnippet =>
        $"""
         # inside your site block
         @manifest path /{ManifestName}
         respond @manifest 404
         """;

    public string IisSnippet =>
        $"""
         <!-- web.config -->
         <system.webServer><security><requestFiltering>
           <hiddenSegments><add segment="{ManifestName}" /></hiddenSegments>
         </requestFiltering></security></system.webServer>
         """;

    [RelayCommand]
    private void Close() => window.Close();
}
