// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia.Controls;
using CommunityToolkit.Mvvm.Input;
using dir2site.SftpSync.Core;
using dir2site.ViewModels;

namespace dir2site.SftpSync.Ui;

/// <summary>
/// Asks the user to accept a server's SSH host key. Closing returns true only when the user
/// explicitly trusts the key — the window's close button and Cancel both return false, so the
/// connection is refused unless someone deliberately said yes.
/// </summary>
public partial class HostKeyPromptViewModel : ViewModelBase
{
    private readonly Window _window;

    public HostKeyPromptViewModel(Window window, HostKeyInfo info)
    {
        _window = window;
        Info = info;
    }

    public HostKeyInfo Info { get; }

    public bool IsChanged => Info.IsChanged;
    public bool IsFirstContact => !Info.IsChanged;

    public string Title => Info.IsChanged
        ? "Host key CHANGED — do not continue unless you expected this"
        : "Unrecognised server — verify the fingerprint";

    public string Explanation => Info.IsChanged
        ? $"{Info.Host} is presenting a different host key than the one you trusted before. " +
          "This happens if the server was rebuilt or migrated — but it is also exactly what an " +
          "impersonation attack looks like. If you did not expect this, cancel and check with " +
          "whoever runs the server before continuing."
        : $"dir2site has not connected to {Info.Host} before. Confirm the fingerprint below " +
          "matches the server you intend to deploy to, then trust it. Compare it against the " +
          "server's own output (ssh-keygen -lf on the host key) or your provider's records.";

    public string Endpoint => $"{Info.Host}:{Info.Port}";
    public string KeyType => $"{Info.KeyAlgorithm} ({Info.KeyLength} bits)";
    public string Fingerprint => Info.Fingerprint;
    public string? KnownFingerprint => Info.KnownFingerprint;

    public string TrustButtonText => Info.IsChanged ? "Accept New Key" : "Trust and Connect";

    [RelayCommand]
    private void Trust() => _window.Close(true);

    [RelayCommand]
    private void Cancel() => _window.Close(false);
}
