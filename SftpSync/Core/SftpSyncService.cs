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
/// SFTP deployment engine. Synchronous and <see cref="IProgress{T}"/>-driven, like the app's other
/// services (callers invoke these via <c>Task.Run</c>).
///
/// Progress is reported as <see cref="SyncProgress"/> rather than a string: a deploy can run for
/// minutes, and a caller needs the counts to show a real bar. A formatted sentence throws that away
/// and leaves the UI re-parsing its own text to get it back.
/// </summary>
public static class SftpSyncService
{
    /// <summary>
    /// Default manifest filename. The <c>.ht</c> prefix is deliberate: Apache's stock config
    /// refuses to serve anything matching <c>^\.ht</c>, so on the shared hosting that most SFTP
    /// deployments target this file is unreachable over HTTP with no configuration at all.
    ///
    /// It matters because the manifest lists every deployed path with its size and mtime — the
    /// files are already public, but the index isn't, and it would reveal anything uploaded but
    /// never linked to. Other servers need a rule; see the guidance in the settings dialog.
    /// </summary>
    private const string DefaultManifestName = ".ht-dir2site";

    /// <summary>The default manifest filename, for UI that has to talk about it.</summary>
    public static string DefaultManifestFileName => DefaultManifestName;

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

    /// <summary>
    /// Lists the directories inside <paramref name="path"/>, so the user can find a deploy target
    /// by looking rather than by typing a path they have to already know.
    /// </summary>
    /// <remarks>
    /// Directories only: this exists to choose a deploy destination, and listing every file on a
    /// web server would bury the one thing being looked for.
    /// </remarks>
    public static RemoteListing ListDirectories(
        SftpProfile profile, string? secret, string path,
        IHostKeyVerifier? hostKeyVerifier = null, CancellationToken ct = default)
    {
        using var client = Connect(profile, secret, hostKeyVerifier);
        try
        {
            // "." resolves to wherever the server drops us, usually the account's home.
            var resolved = client.WorkingDirectory;
            if (!string.IsNullOrWhiteSpace(path) && path != ".")
                resolved = NormalizeDir(path);

            var names = new List<string>();
            foreach (var entry in client.ListDirectory(resolved))
            {
                ct.ThrowIfCancellationRequested();
                if (entry.Name is "." or "..") continue;
                if (entry.IsDirectory) names.Add(entry.Name);
            }

            names.Sort(StringComparer.OrdinalIgnoreCase);
            return new RemoteListing(resolved, names);
        }
        finally
        {
            client.Disconnect();
        }
    }

    /// <summary>Creates a directory below <paramref name="parent"/> and returns its full path.</summary>
    public static string CreateRemoteDirectory(
        SftpProfile profile, string? secret, string parent, string name,
        IHostKeyVerifier? hostKeyVerifier = null)
    {
        using var client = Connect(profile, secret, hostKeyVerifier);
        try
        {
            var full = CombineRemote(NormalizeDir(parent), name.Trim());
            EnsureDir(client, full, new HashSet<string>(StringComparer.Ordinal));
            return full;
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
    /// <summary>
    /// Works out what <see cref="QuickSync"/> would do, without changing anything on the server.
    /// </summary>
    /// <remarks>
    /// The result is a snapshot of a moment, not a lock — see <see cref="SyncPlan"/>.
    /// </remarks>
    public static SyncPlan Preview(
        string siteRoot,
        SftpProfile profile,
        string? secret,
        bool forceFull = false,
        IProgress<SyncProgress>? progress = null,
        CancellationToken ct = default,
        IHostKeyVerifier? hostKeyVerifier = null)
    {
        var local = SyncManifestBuilder.BuildLocal(siteRoot);
        if (local.Files.Count == 0)
            return new SyncPlan([], [], 0, "_site/ is empty — nothing to deploy.");

        using var client = Connect(profile, secret, hostKeyVerifier);
        try
        {
            var (diff, note) = Diff(client, profile, local, forceFull, progress, ct);
            var bytes = diff.ToUpload.Sum(rel => local.Files.TryGetValue(rel, out var e) ? e.Size : 0);
            return new SyncPlan(diff.ToUpload, diff.StaleRemote, bytes, note.Trim());
        }
        finally
        {
            client.Disconnect();
        }
    }

    /// <summary>
    /// Deploys, after a <see cref="Preview"/>. Re-diffs rather than trusting the plan: the server
    /// can change while the user reads it, and the current local state is always what they meant to
    /// upload. When the fresh diff differs from what was approved, the result says so.
    /// </summary>
    public static SyncResult Apply(
        SyncPlan approved,
        string siteRoot,
        SftpProfile profile,
        string? secret,
        bool forceFull = false,
        IProgress<SyncProgress>? progress = null,
        CancellationToken ct = default,
        IHostKeyVerifier? hostKeyVerifier = null)
    {
        var result = QuickSync(siteRoot, profile, secret, forceFull, progress, ct, hostKeyVerifier);

        var approvedSet = approved.ToUpload.ToHashSet(StringComparer.Ordinal);
        var actual = result.Uploaded;
        if (actual != approvedSet.Count)
        {
            return result with
            {
                Summary = result.Summary +
                          $" — note: {approvedSet.Count} file(s) were listed when you previewed, " +
                          $"{actual} were uploaded; the site or the server changed in between.",
            };
        }

        return result;
    }

    public static SyncResult QuickSync(
        string siteRoot,
        SftpProfile profile,
        string? secret,
        bool forceFull = false,
        IProgress<SyncProgress>? progress = null,
        CancellationToken ct = default,
        IHostKeyVerifier? hostKeyVerifier = null)
    {
        var local = SyncManifestBuilder.BuildLocal(siteRoot);
        if (local.Files.Count == 0)
            return new SyncResult("Nothing to sync — _site/ is empty.", 0, [], []);

        using var client = Connect(profile, secret, hostKeyVerifier);

        var manifestPath = ManifestRemotePath(profile);
        var (diff, note) = Diff(client, profile, local, forceFull, progress, ct);
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
        IProgress<SyncProgress>? progress = null,
        CancellationToken ct = default,
        IHostKeyVerifier? hostKeyVerifier = null)
    {
        var local = SyncManifestBuilder.BuildLocal(siteRoot);

        using var client = Connect(profile, secret, hostKeyVerifier);

        var manifestPath = ManifestRemotePath(profile);
        var remoteRoot   = NormalizeDir(profile.RemotePath);

        progress?.Report(new SyncProgress(SyncPhase.Listing, "Listing remote files…"));
        var remote = new SyncManifest();
        var listErrors = new List<string>();
        if (TryExists(client, remoteRoot))
            ListRecursive(client, remoteRoot, remote, manifestPath, listErrors, ct);

        // Treat the listed remote tree as the reference: anything local that's missing or
        // mismatched needs (re)uploading; anything remote-only is stale.
        var diff = SyncManifestBuilder.Compare(local, remote);
        var errors = UploadFiles(client, siteRoot, profile, local, diff.ToUpload, progress, ct);
        errors.InsertRange(0, listErrors);

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
        IProgress<SyncProgress>? progress = null,
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
            progress?.Report(new SyncProgress(
                SyncPhase.Deleting, "Deleting", i + 1, relPaths.Count, rel));
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
        IProgress<SyncProgress>? progress,
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
            progress?.Report(new SyncProgress(
                SyncPhase.Uploading, "Uploading", i + 1, toUpload.Count, rel));

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

    private static void ListRecursive(
        SftpClient client, string dir, SyncManifest into, string manifestPath,
        List<string>? errors = null, CancellationToken ct = default)
    {
        // Track each directory's path relative to the root rather than deriving it from the
        // server's absolute FullName — servers may canonicalize paths (symlinks, ~, /tmp →
        // /private/tmp) so FullName cannot be assumed to start with the configured root.
        var manifestName = ManifestFileName(manifestPath);
        var stack = new Stack<(string ServerPath, string RelPrefix)>();
        stack.Push((NormalizeDir(dir), string.Empty));

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();

            var (current, relPrefix) = stack.Pop();
            IEnumerable<ISftpFile> entries;
            try { entries = client.ListDirectory(current); }
            catch (Exception ex)
            {
                // Swallowing this silently made Verify under-report stale files and re-upload
                // needlessly, with nothing to explain why — a permission problem looked like an
                // empty directory.
                errors?.Add($"Could not list {current}: {ex.Message}");
                continue;
            }

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

    /// <summary>
    /// The upload/stale diff against the reference manifest, shared by Preview and QuickSync so
    /// the plan a user is shown is produced by the same code that acts on it.
    /// </summary>
    private static (SyncManifestBuilder.Diff Diff, string Note) Diff(
        SftpClient client, SftpProfile profile, SyncManifest local,
        bool forceFull, IProgress<SyncProgress>? progress, CancellationToken ct)
    {
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
            progress?.Report(new SyncProgress(SyncPhase.Listing, "Reading remote manifest…"));
            reference = DownloadManifest(client, manifestPath);
        }
        else
        {
            reference = new SyncManifest();
            note = " (no remote manifest — uploaded everything; run Verify & Repair to confirm)";
        }

        ct.ThrowIfCancellationRequested();
        return (SyncManifestBuilder.Compare(local, reference), note);
    }

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
