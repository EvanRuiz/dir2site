// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Renci.SshNet;
using Renci.SshNet.Sftp;

namespace dir2site.SftpSync.Core;

/// <summary>
/// SFTP deployment engine. Mirrors the synchronous, <c>IProgress&lt;string&gt;</c>-driven style of
/// the app's other services (callers invoke these via <c>Task.Run</c>).
/// </summary>
public static class SftpSyncService
{
    private const string DefaultManifestName = ".dir2site-manifest.json";

    /// <param name="StaleRemote">Files present remotely but not locally — reported, never auto-deleted.</param>
    public sealed record SyncResult(
        string Summary,
        int Uploaded,
        IReadOnlyList<string> StaleRemote,
        IReadOnlyList<string> Errors);

    // ---- public operations -------------------------------------------------

    /// <summary>Verifies the connection and credentials. Throws on failure.</summary>
    public static void TestConnection(SftpProfile profile, string? secret, IHostKeyVerifier? hostKeyVerifier = null)
    {
        using var client = Connect(profile, secret, hostKeyVerifier);
        client.Disconnect();
    }

    /// <summary>
    /// Verifies the connection <em>and</em> that the profile's remote path is somewhere we could
    /// actually deploy to. Throws on connection or auth failure; a path problem comes back in the
    /// result rather than as an exception, because it is a thing the user can fix in the dialog.
    /// </summary>
    /// <remarks>
    /// Checking only the connection was misleading: "connection succeeded" told the user nothing
    /// about a mistyped remote path, which then failed at the first real deploy.
    /// </remarks>
    public static ConnectionCheck CheckConnection(
        SftpProfile profile, string? secret, IHostKeyVerifier? hostKeyVerifier = null)
    {
        using var client = Connect(profile, secret, hostKeyVerifier);
        try
        {
            var path = string.IsNullOrWhiteSpace(profile.RemotePath) ? "." : profile.RemotePath;

            if (!client.Exists(path))
                return new ConnectionCheck(RemotePathState.Missing, path);

            if (!client.GetAttributes(path).IsDirectory)
                return new ConnectionCheck(RemotePathState.NotADirectory, path);

            // Nothing in SFTP reports "can I write here" directly, and permission bits lie when
            // the account maps to a different uid, so probe with a real create and clean up.
            var probe = CombineRemote(path, ".dir2site-write-test-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                using (var s = client.Create(probe)) { }
                client.DeleteFile(probe);
            }
            catch
            {
                return new ConnectionCheck(RemotePathState.NotWritable, path);
            }

            return new ConnectionCheck(RemotePathState.Writable, path);
        }
        finally
        {
            client.Disconnect();
        }
    }

    /// <summary>Creates the profile's remote path, including any missing parents.</summary>
    public static void CreateRemotePath(
        SftpProfile profile, string? secret, IHostKeyVerifier? hostKeyVerifier = null)
    {
        using var client = Connect(profile, secret, hostKeyVerifier);
        try
        {
            EnsureDir(client, profile.RemotePath, new HashSet<string>(StringComparer.Ordinal));
        }
        finally
        {
            client.Disconnect();
        }
    }

    /// <summary>
    /// Fast-path deploy: diff the local site against the server manifest (last-uploaded snapshot)
    /// and upload only new/changed files. Reports — but never deletes — stale remote files.
    /// When <paramref name="forceFull"/> is true, ignores the manifest and re-uploads everything.
    /// </summary>
    public static SyncResult QuickSync(
        string siteRoot,
        SftpProfile profile,
        string? secret,
        bool forceFull = false,
        IProgress<string>? progress = null,
        CancellationToken ct = default,
        IHostKeyVerifier? hostKeyVerifier = null)
    {
        var local = SyncManifestBuilder.BuildLocal(siteRoot);
        if (local.Files.Count == 0)
            return new SyncResult("Nothing to sync — _site/ is empty.", 0, [], []);

        using var client = Connect(profile, secret, hostKeyVerifier);

        var manifestPath = ManifestRemotePath(profile);
        var note = "";

        SyncManifest reference;
        if (forceFull)
        {
            reference = new SyncManifest();
            note = " (forced full upload)";
        }
        else if (TryExists(client, manifestPath))
        {
            reference = DownloadManifest(client, manifestPath);
        }
        else
        {
            reference = new SyncManifest();
            note = " (no remote manifest — uploaded everything; run Verify & Repair to confirm)";
        }

        var diff = SyncManifestBuilder.Compare(local, reference);
        var errors = UploadFiles(client, siteRoot, profile, local, diff.ToUpload, progress, ct);

        WriteManifest(client, manifestPath, local, errors);

        var summary = $"Quick Sync: {diff.ToUpload.Count - errors.Count} uploaded, " +
                      $"{diff.StaleRemote.Count} stale → {profile.Host}{note}";
        client.Disconnect();
        return new SyncResult(summary, diff.ToUpload.Count - errors.Count, diff.StaleRemote, errors);
    }

    /// <summary>
    /// Source-of-truth reconcile: list the actual remote tree and re-upload anything missing or
    /// changed, then report extras as stale. Also the correct path when no manifest exists.
    /// </summary>
    public static SyncResult VerifyAndRepair(
        string siteRoot,
        SftpProfile profile,
        string? secret,
        IProgress<string>? progress = null,
        CancellationToken ct = default,
        IHostKeyVerifier? hostKeyVerifier = null)
    {
        var local = SyncManifestBuilder.BuildLocal(siteRoot);

        using var client = Connect(profile, secret, hostKeyVerifier);

        var manifestPath = ManifestRemotePath(profile);
        var remoteRoot   = NormalizeDir(profile.RemotePath);

        progress?.Report("Listing remote files…");
        var remote = new SyncManifest();
        if (TryExists(client, remoteRoot))
            ListRecursive(client, remoteRoot, remote, manifestPath);

        // Treat the listed remote tree as the reference: anything local that's missing or
        // mismatched needs (re)uploading; anything remote-only is stale.
        var diff = SyncManifestBuilder.Compare(local, remote);
        var errors = UploadFiles(client, siteRoot, profile, local, diff.ToUpload, progress, ct);

        WriteManifest(client, manifestPath, local, errors);

        var summary = $"Verify & Repair: {diff.ToUpload.Count - errors.Count} repaired, " +
                      $"{diff.StaleRemote.Count} stale → {profile.Host}";
        client.Disconnect();
        return new SyncResult(summary, diff.ToUpload.Count - errors.Count, diff.StaleRemote, errors);
    }

    /// <summary>
    /// Deletes the given relative paths from the server (used by the stale-file dialog), prunes
    /// emptied directories, and rewrites the manifest from the current local site.
    /// </summary>
    public static SyncResult DeleteRemote(
        string siteRoot,
        SftpProfile profile,
        string? secret,
        IReadOnlyList<string> relPaths,
        IProgress<string>? progress = null,
        CancellationToken ct = default,
        IHostKeyVerifier? hostKeyVerifier = null)
    {
        using var client = Connect(profile, secret, hostKeyVerifier);

        var remoteRoot = NormalizeDir(profile.RemotePath);
        var errors = new List<string>();
        int deleted = 0;
        var touchedDirs = new HashSet<string>(StringComparer.Ordinal);

        for (int i = 0; i < relPaths.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var rel = relPaths[i];
            var full = CombineRemote(remoteRoot, rel);
            progress?.Report($"Deleting {i + 1}/{relPaths.Count}: {rel}");
            try
            {
                if (TryExists(client, full))
                {
                    client.DeleteFile(full);
                    deleted++;
                }
                touchedDirs.Add(ParentOf(full));
            }
            catch (Exception ex)
            {
                errors.Add($"Delete '{rel}': {ex.Message}");
            }
        }

        PruneEmptyDirs(client, touchedDirs, remoteRoot, errors);

        // Rewrite the manifest to match what remains locally.
        var local = SyncManifestBuilder.BuildLocal(siteRoot);
        WriteManifest(client, ManifestRemotePath(profile), local, errors);

        client.Disconnect();
        return new SyncResult($"Deleted {deleted} remote file(s).", 0, [], errors);
    }

    // ---- connection --------------------------------------------------------

    /// <summary>
    /// Builds a client and connects it, refusing unless the server's host key is either already
    /// pinned on the profile or accepted by <paramref name="verifier"/>. Without this check
    /// SSH.NET trusts any key, so anyone on the network path could impersonate the server and
    /// collect the password during the handshake.
    /// </summary>
    private static SftpClient Connect(SftpProfile p, string? secret, IHostKeyVerifier? verifier)
    {
        var client = CreateClient(p, secret);

        // Captured so a refusal can be reported as itself, rather than as SSH.NET's generic
        // "key exchange negotiation failed", which gives the user nothing to act on.
        HostKeyInfo? refused = null;

        client.HostKeyReceived += (_, e) =>
        {
            var offered = HostKeyFingerprintFormatter.Format(e.HostKey);
            var known   = string.IsNullOrWhiteSpace(p.HostKeyFingerprint) ? null : p.HostKeyFingerprint;

            if (known == offered)
            {
                e.CanTrust = true;
                return;
            }

            var info = new HostKeyInfo(
                p.Host, p.Port <= 0 ? 22 : p.Port, e.HostKeyName, e.KeyLength, offered, known);

            // No verifier means nobody is able to answer the question, so fail closed.
            e.CanTrust = verifier is not null && verifier.Verify(info);
            if (e.CanTrust) p.HostKeyFingerprint = offered;
            else refused = info;
        };

        try
        {
            client.Connect();
        }
        catch when (refused is not null)
        {
            client.Dispose();
            throw new SftpHostKeyRejectedException(
                refused.IsChanged
                    ? $"The host key for {refused.Host} has CHANGED and was not accepted.\n" +
                      $"Expected {refused.KnownFingerprint}\n" +
                      $"Offered  {refused.Fingerprint}\n" +
                      "If you did not rebuild or migrate this server, do not connect — " +
                      "someone may be impersonating it."
                    : $"The host key for {refused.Host} ({refused.Fingerprint}) was not accepted, " +
                      "so the connection was refused.");
        }
        catch
        {
            client.Dispose();
            throw;
        }

        return client;
    }

    private static SftpClient CreateClient(SftpProfile p, string? secret)
    {
        AuthenticationMethod auth;
        if (p.AuthMethod == SftpAuthMethod.Key)
        {
            var keyFile = string.IsNullOrEmpty(secret)
                ? new PrivateKeyFile(p.PrivateKeyPath)
                : new PrivateKeyFile(p.PrivateKeyPath, secret);
            auth = new PrivateKeyAuthenticationMethod(p.Username, keyFile);
        }
        else
        {
            auth = new PasswordAuthenticationMethod(p.Username, secret ?? string.Empty);
        }

        var info = new ConnectionInfo(p.Host, p.Port <= 0 ? 22 : p.Port, p.Username, auth)
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        return new SftpClient(info);
    }

    // ---- upload / manifest -------------------------------------------------

    private static List<string> UploadFiles(
        SftpClient client,
        string siteRoot,
        SftpProfile profile,
        SyncManifest local,
        IReadOnlyList<string> toUpload,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        var errors = new List<string>();
        var remoteRoot = NormalizeDir(profile.RemotePath);
        var knownDirs = new HashSet<string>(StringComparer.Ordinal);

        for (int i = 0; i < toUpload.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var rel = toUpload[i];
            var localFull = Path.Combine(siteRoot, rel.Replace('/', Path.DirectorySeparatorChar));
            var remoteFull = CombineRemote(remoteRoot, rel);
            progress?.Report($"Uploading {i + 1}/{toUpload.Count}: {rel}");

            try
            {
                EnsureDir(client, ParentOf(remoteFull), knownDirs);
                using (var fs = File.OpenRead(localFull))
                    client.UploadFile(fs, remoteFull, canOverride: true);

                // Preserve mtime so Verify's size+mtime comparison is meaningful.
                var attrs = client.GetAttributes(remoteFull);
                attrs.LastWriteTimeUtc = File.GetLastWriteTimeUtc(localFull);
                client.SetAttributes(remoteFull, attrs);
            }
            catch (Exception ex)
            {
                errors.Add($"Upload '{rel}': {ex.Message}");
                // Drop from the manifest snapshot so the next sync retries it.
                local.Files.Remove(rel);
            }
        }

        return errors;
    }

    private static SyncManifest DownloadManifest(SftpClient client, string manifestPath)
    {
        try
        {
            using var ms = new MemoryStream();
            client.DownloadFile(manifestPath, ms);
            return JsonSerializer.Deserialize<SyncManifest>(ms.ToArray()) ?? new SyncManifest();
        }
        catch
        {
            return new SyncManifest();
        }
    }

    private static void WriteManifest(SftpClient client, string manifestPath, SyncManifest manifest, List<string> errors)
    {
        try
        {
            EnsureDir(client, ParentOf(manifestPath), new HashSet<string>(StringComparer.Ordinal));
            var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest);
            var tmp = manifestPath + ".tmp";
            using (var up = new MemoryStream(bytes))
                client.UploadFile(up, tmp, canOverride: true);

            // Rename first and only fall back to delete-then-rename if the server refuses to
            // clobber. Deleting up front would leave no manifest at all if the link dropped
            // before the rename landed, forcing the next run into a full re-upload.
            try
            {
                client.RenameFile(tmp, manifestPath);
            }
            catch
            {
                if (TryExists(client, manifestPath))
                    client.DeleteFile(manifestPath);
                client.RenameFile(tmp, manifestPath);
            }
        }
        catch (Exception ex)
        {
            errors.Add($"Write manifest: {ex.Message}");
        }
    }

    // ---- remote traversal --------------------------------------------------

    private static void ListRecursive(SftpClient client, string dir, SyncManifest into, string manifestPath)
    {
        // Track each directory's path relative to the root rather than deriving it from the
        // server's absolute FullName — servers may canonicalize paths (symlinks, ~, /tmp →
        // /private/tmp) so FullName cannot be assumed to start with the configured root.
        var manifestName = ManifestFileName(manifestPath);
        var stack = new Stack<(string ServerPath, string RelPrefix)>();
        stack.Push((NormalizeDir(dir), string.Empty));

        while (stack.Count > 0)
        {
            var (current, relPrefix) = stack.Pop();
            IEnumerable<ISftpFile> entries;
            try { entries = client.ListDirectory(current); }
            catch { continue; }

            foreach (var f in entries)
            {
                if (f.Name is "." or "..") continue;
                var rel = relPrefix.Length == 0 ? f.Name : relPrefix + "/" + f.Name;

                if (f.IsDirectory) { stack.Push((f.FullName, rel)); continue; }
                if (!f.IsRegularFile) continue;
                // Skip the manifest (and its temp file) wherever it lands inside the tree.
                if (relPrefix.Length == 0 && (f.Name == manifestName || f.Name == manifestName + ".tmp")) continue;

                into.Files[rel] = new SyncEntry
                {
                    Size  = f.Length,
                    Mtime = new DateTimeOffset(f.Attributes.LastWriteTimeUtc).ToUnixTimeSeconds(),
                };
            }
        }
    }

    private static string ManifestFileName(string manifestPath)
    {
        var idx = manifestPath.LastIndexOf('/');
        return idx < 0 ? manifestPath : manifestPath[(idx + 1)..];
    }

    private static void PruneEmptyDirs(SftpClient client, HashSet<string> dirs, string remoteRoot, List<string> errors)
    {
        // Walk deepest-first so a parent can empty after its child is removed.
        foreach (var dir in dirs.OrderByDescending(d => d.Length))
        {
            var current = dir;
            while (!string.IsNullOrEmpty(current) &&
                   current.Length > remoteRoot.Length &&
                   current.StartsWith(remoteRoot, StringComparison.Ordinal))
            {
                try
                {
                    if (!TryExists(client, current)) break;
                    if (client.ListDirectory(current).Any(f => f.Name is not ("." or ".."))) break;
                    client.DeleteDirectory(current);
                }
                catch (Exception ex)
                {
                    errors.Add($"Prune '{current}': {ex.Message}");
                    break;
                }
                current = ParentOf(current);
            }
        }
    }

    private static void EnsureDir(SftpClient client, string dir, HashSet<string> known)
    {
        if (string.IsNullOrEmpty(dir) || dir is "/" or "." || known.Contains(dir)) return;
        try
        {
            if (TryExists(client, dir)) { known.Add(dir); return; }
        }
        catch { /* fall through to create */ }

        EnsureDir(client, ParentOf(dir), known);
        try { client.CreateDirectory(dir); }
        catch { /* created concurrently or already present */ }
        known.Add(dir);
    }

    private static bool TryExists(SftpClient client, string path)
    {
        try { return client.Exists(path); }
        catch { return false; }
    }

    // ---- path helpers ------------------------------------------------------

    private static string ManifestRemotePath(SftpProfile p) =>
        string.IsNullOrWhiteSpace(p.ManifestPath)
            ? CombineRemote(NormalizeDir(p.RemotePath), DefaultManifestName)
            : p.ManifestPath.Trim();

    /// <summary>Trailing-slash-free directory path; empty/"." becomes ".".</summary>
    private static string NormalizeDir(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return ".";
        dir = dir.Trim().Replace('\\', '/');
        if (dir.Length > 1) dir = dir.TrimEnd('/');
        return dir.Length == 0 ? "/" : dir;
    }

    private static string CombineRemote(string baseDir, string rel)
    {
        rel = rel.TrimStart('/');
        if (baseDir is "." or "") return rel;
        return baseDir.TrimEnd('/') + "/" + rel;
    }

    private static string ParentOf(string path)
    {
        var idx = path.LastIndexOf('/');
        if (idx < 0) return ".";
        if (idx == 0) return "/"; // path was "/something" at root
        return path[..idx];
    }
}
