// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace dir2site.SftpSync.Core;

/// <summary>The server host key offered during a connection, for the user to accept or refuse.</summary>
/// <param name="KnownFingerprint">
/// The fingerprint this profile already trusts, or <c>null</c> if the host has never been accepted.
/// When non-null it differs from <paramref name="Fingerprint"/>, i.e. the key has <em>changed</em> —
/// either the server was rebuilt or someone is impersonating it.
/// </param>
public sealed record HostKeyInfo(
    string Host,
    int Port,
    string KeyAlgorithm,
    int KeyLength,
    string Fingerprint,
    string? KnownFingerprint)
{
    /// <summary>True when a previously trusted key exists and the server is now offering a different one.</summary>
    public bool IsChanged => KnownFingerprint is not null;
}

/// <summary>
/// Decides whether an offered host key should be trusted. Implemented outside this assembly —
/// by the desktop app's prompt, or by whatever a future consumer of this engine wants — which is
/// what keeps <c>SftpSync.Core</c> free of any UI dependency.
/// </summary>
public interface IHostKeyVerifier
{
    /// <summary>
    /// Returns true to connect and pin <paramref name="info"/>'s fingerprint, false to refuse.
    /// Called on the connection's background thread and may block (e.g. to prompt the user).
    /// </summary>
    bool Verify(HostKeyInfo info);
}

/// <summary>What a connection test found at the profile's remote path.</summary>
public enum RemotePathState
{
    /// <summary>Exists, is a directory, and a file could be created in it.</summary>
    Writable,

    /// <summary>Exists and is a directory, but nothing can be written there.</summary>
    NotWritable,

    /// <summary>Nothing at that path — offer to create it.</summary>
    Missing,

    /// <summary>Something is there, but it's a file, not a directory.</summary>
    NotADirectory,
}

/// <summary>
/// Result of <see cref="SftpSyncService.CheckConnection"/>: the connection and credentials worked,
/// and this is what was found at <paramref name="Path"/>.
/// </summary>
public sealed record ConnectionCheck(RemotePathState State, string Path)
{
    public bool CanDeploy => State == RemotePathState.Writable;

    /// <summary>A message suitable for showing directly in the settings dialog.</summary>
    public string Describe() => State switch
    {
        RemotePathState.Writable      => $"✓ Connected. {Path} is writable.",
        RemotePathState.NotWritable   => $"Connected, but {Path} is not writable by this account.",
        RemotePathState.Missing       => $"Connected, but {Path} does not exist.",
        RemotePathState.NotADirectory => $"Connected, but {Path} is a file, not a directory.",
        _                             => "Connected.",
    };
}

/// <summary>
/// One level of the remote filesystem: the path actually listed — which may differ from what was
/// asked for, since "." resolves to the account's home — and the directories inside it.
/// </summary>
public sealed record RemoteListing(string Path, IReadOnlyList<string> Directories);

/// <summary>
/// What a deploy would do, worked out without changing anything, so the user can look before
/// committing.
/// </summary>
/// <remarks>
/// A plan is an observation, not a reservation. SFTP has no snapshots, so nothing stops the server
/// changing between previewing and applying — holding the connection open would not help, it would
/// only stop us noticing. <see cref="SftpSyncService.Apply"/> therefore re-diffs and reports when
/// what it found no longer matches what was approved.
/// </remarks>
public sealed record SyncPlan(
    IReadOnlyList<string> ToUpload,
    IReadOnlyList<string> StaleRemote,
    long BytesToUpload,
    string Note)
{
    public bool IsEmpty => ToUpload.Count == 0;

    public string Summary => IsEmpty
        ? "Everything is already up to date."
        : $"{ToUpload.Count} file{(ToUpload.Count == 1 ? "" : "s")} to upload"
          + (BytesToUpload > 0 ? $" ({FormatBytes(BytesToUpload)})" : "")
          + (StaleRemote.Count > 0 ? $", {StaleRemote.Count} stale on the server" : "");

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
    };
}

/// <summary>What a sync is doing at the moment.</summary>
public enum SyncPhase
{
    Connecting,
    Listing,
    Uploading,
    Deleting,
    WritingManifest,
    Done,
}

/// <summary>
/// A progress report from a running sync. Replaces a bare status string so the UI can show a real
/// bar and a file count: "142 of 380" tells you whether to wait; "Uploading css/site.css" does not.
/// </summary>
/// <param name="Index">1-based position within the phase, or 0 when there is nothing to count.</param>
/// <param name="Total">Items in this phase, or 0 when unknown.</param>
public sealed record SyncProgress(
    SyncPhase Phase,
    string Message,
    int Index = 0,
    int Total = 0,
    string? CurrentFile = null)
{
    /// <summary>True when Index/Total are meaningful enough to drive a determinate bar.</summary>
    public bool HasCount => Total > 0;

    /// <summary>Completion within the phase, 0–100, or null when it can't be known.</summary>
    public double? Percent => HasCount ? Index * 100.0 / Total : null;

    public override string ToString() =>
        HasCount ? $"{Message} ({Index}/{Total})" : Message;
}

/// <summary>Thrown when a host key was not trusted, so the connection was refused.</summary>
public sealed class SftpHostKeyRejectedException(string message) : Exception(message);

/// <summary>Formats an SSH host key blob the way OpenSSH does: <c>SHA256:</c> + unpadded base64.</summary>
public static class HostKeyFingerprintFormatter
{
    public static string Format(byte[] hostKey) =>
        "SHA256:" + Convert.ToBase64String(SHA256.HashData(hostKey)).TrimEnd('=');
}
