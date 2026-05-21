// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using CommunityToolkit.Mvvm.ComponentModel;

namespace dir2site.ViewModels;

public partial class PhotoViewModel : ArtifactViewModel
{
    [ObservableProperty] public partial string? Photographer { get; set; }
    [ObservableProperty] public partial string? Image { get; set; }
    [ObservableProperty] public partial string? Overlays { get; set; }
}
