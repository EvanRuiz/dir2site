// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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
    /// <param name="Attempted">
    /// What this run decided to upload, before any of them failed. Compared against an approved
    /// plan to detect drift — using <see cref="Uploaded"/> for that would mistake a failed upload
    /// for the site having changed.
    /// </param>
    public sealed record SyncResult(
        string Summary,
        int Uploaded,
        IReadOnlyList<string> StaleRemote,
        IReadOnlyList<string> Errors,
        IReadOnlyList<string> Attempted);

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
            EnsureDir(client, full, new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));
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
            EnsureDir(client, profile.RemotePath, new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));
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
            var (diff, note, _) = Diff(client, profile, local, forceFull, progress, ct);
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

        // Compare the plans, not the counts. A partial upload failure leaves fewer files uploaded
        // than approved while the plan was identical, and reporting that as "the site changed"
        // would be wrong — failures are surfaced separately as errors.
        var approvedSet = approved.ToUpload.ToHashSet(StringComparer.Ordinal);
        if (approvedSet.SetEquals(result.Attempted)) return result;

        var added = result.Attempted.Count(f => !approvedSet.Contains(f));
        var dropped = approvedSet.Count(f => !result.Attempted.Contains(f));
        var what = (added, dropped) switch
        {
            (> 0, > 0) => $"{added} file(s) appeared and {dropped} no longer needed uploading",
            (> 0, _)   => $"{added} file(s) appeared",
            (_, > 0)   => $"{dropped} file(s) no longer needed uploading",
            _          => "the file list differed",
        };

        return result with
        {
            Summary = result.Summary +
                      $" — note: {what} since you previewed; the site or the server changed in between.",
        };
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
            return new SyncResult("Nothing to sync — _site/ is empty.", 0, [], [], []);

        using var client = Connect(profile, secret, hostKeyVerifier);

        var manifestPath = ManifestRemotePath(profile);
        var (diff, note, reference) = Diff(client, profile, local, forceFull, progress, ct);
        var delivered = new ManifestUpdate(reference, diff.ToUpload);

        List<string> errors;
        try
        {
            errors = UploadFiles(
                client, () => Connect(profile, secret, hostKeyVerifier),
                siteRoot, profile, local, delivered, diff.ToUpload, progress, ct);
        }
        catch
        {
            // Cancelled or dropped part-way. What did arrive is on the server whatever happens
            // next, so it has to be written down — a manifest that forgets it is as wrong as one
            // that invents it, and the files it forgot could never be reported stale again.
            WriteManifest(client, manifestPath, delivered.Snapshot(), []);
            throw;
        }

        WriteManifest(client, manifestPath, delivered.Snapshot(), errors);

        // "0 stale" was a promise Quick Sync is in no position to make: it compares against the
        // file list it wrote last time, never against the server, so all it really knows is that
        // its own records name nothing extra. A count is only stated when there is something to
        // count; silence claims nothing.
        var stale = diff.StaleRemote.Count > 0 ? $", {diff.StaleRemote.Count} stale" : "";
        var summary = $"Quick Sync: {diff.ToUpload.Count - errors.Count} uploaded{stale} " +
                      $"→ {profile.Host}{note}";
        client.Disconnect();
        return new SyncResult(
            summary, diff.ToUpload.Count - errors.Count, diff.StaleRemote, errors, diff.ToUpload);
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

        // The listing is the truth about the server, so it is what this run's manifest starts from
        // — including the stale files, which are still up there until someone removes them.
        var delivered = new ManifestUpdate(remote, diff.ToUpload);

        List<string> errors;
        try
        {
            errors = UploadFiles(
                client, () => Connect(profile, secret, hostKeyVerifier),
                siteRoot, profile, local, delivered, diff.ToUpload, progress, ct);
        }
        catch
        {
            WriteManifest(client, manifestPath, delivered.Snapshot(), []);
            throw;
        }
        errors.InsertRange(0, listErrors);

        WriteManifest(client, manifestPath, delivered.Snapshot(), errors);

        // States the count even at zero, unlike Quick Sync, which stays silent because it only
        // consulted its own records. This one listed the server, so "0 stale" is a finding it can
        // stand behind — and confirming there is nothing up there that shouldn't be is most of the
        // reason for running it.
        var summary = $"Verify & Repair: {diff.ToUpload.Count - errors.Count} repaired, " +
                      $"{diff.StaleRemote.Count} stale → {profile.Host}";
        client.Disconnect();
        return new SyncResult(
            summary, diff.ToUpload.Count - errors.Count, diff.StaleRemote, errors, diff.ToUpload);
    }

    /// <summary>
    /// Deletes the given relative paths from the server (used by the stale-file dialog), prunes
    /// emptied directories, and takes the deleted paths out of the manifest.
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
        var deleted = 0;
        var done = 0;
        var touchedDirs = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);

        // Which paths are known not to be up there any more — the ones this run removed, plus any
        // that turned out to be gone already. Only these leave the record: a delete that failed
        // leaves the file on the server, and forgetting it there would mean it could never be
        // offered again, which is the same silence the record was corrected to stop.
        var gone = new ConcurrentBag<string>();

        // Deleting costs two round trips a file, same as uploading, so it gets the same treatment
        // and the same per-target connection count.
        ForEachInParallel(client, () => Connect(profile, secret, hostKeyVerifier),
            profile, relPaths.Count, errors, ct, (worker, i) =>
        {
            var rel = relPaths[i];
            var full = CombineRemote(remoteRoot, rel);
            try
            {
                if (TryExists(worker, full))
                {
                    worker.DeleteFile(full);
                    Interlocked.Increment(ref deleted);
                }
                // Reached only when the delete succeeded, or when there was nothing there to
                // delete — both of which are knowing it is gone.
                gone.Add(rel);
                touchedDirs[ParentOf(full)] = 0;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lock (errors) errors.Add($"Delete '{rel}': {ex.Message}");
            }

            Report(progress, SyncPhase.Deleting, "Deleting",
                Interlocked.Increment(ref done), relPaths.Count, rel);
        });

        // Sequential, on the one connection still open: pruning walks up the tree deleting parents
        // as they empty, and two workers doing that on overlapping paths would race.
        PruneEmptyDirs(client, touchedDirs.Keys, remoteRoot, errors, progress, ct);

        // Take the deleted paths out of the manifest, and change nothing else.
        //
        // This used to rebuild it from the local folder, which claimed every file in _site was on
        // the server — including any the sync moments earlier had failed to upload and deliberately
        // dropped from the manifest so it would be retried. Putting those back said "already
        // uploaded" about a file that had never arrived, and because the local copy never changes,
        // its size and mtime kept matching that record: every later Quick Sync skipped it, for
        // good. Only Verify & Repair, which lists the server, could find it again.
        //
        // Removing is all this can honestly do. The files being deleted are remote-only — that is
        // what made them stale — so they were never in the local manifest to begin with, and the
        // sync that just ran already wrote an accurate one.
        //
        // With no manifest present, nothing is written rather than one being invented from the
        // local folder. That is deliberate: an absent manifest makes the next Quick Sync upload
        // everything, which errs towards sending too much, while a made-up one would state as fact
        // a delivery nobody performed.
        var manifestPath = ManifestRemotePath(profile);
        if (TryExists(client, manifestPath))
        {
            progress?.Report(new SyncProgress(SyncPhase.WritingManifest, "Updating the file list"));
            var manifest = DownloadManifest(client, manifestPath);
            var changed = false;
            foreach (var rel in gone) changed |= manifest.Files.Remove(rel);
            if (changed) WriteManifest(client, manifestPath, manifest, errors);
        }

        client.Disconnect();
        return new SyncResult($"Deleted {deleted} remote file(s).", 0, [], errors, []);
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

    /// <summary>
    /// Uploads <paramref name="toUpload"/>, spreading the work over
    /// <see cref="SftpProfile.UploadConcurrency"/> connections.
    ///
    /// Small files are latency-bound rather than bandwidth-bound — each costs several serialized
    /// round trips to upload and stamp — so a site of many small assets goes a long way faster with
    /// several in flight. It has to be several *connections*: SSH.NET's
    /// <see cref="SftpClient"/> is not thread-safe, and one SSH channel would serialize the
    /// requests anyway.
    /// </summary>
    /// <param name="primary">
    /// The already-connected client, reused as the first worker. Extras come from
    /// <paramref name="connect"/> and are disposed here; the caller keeps owning the primary.
    /// </param>
    private static List<string> UploadFiles(
        SftpClient primary,
        Func<SftpClient> connect,
        string siteRoot,
        SftpProfile profile,
        SyncManifest local,
        ManifestUpdate delivered,
        IReadOnlyList<string> toUpload,
        IProgress<SyncProgress>? progress,
        CancellationToken ct)
    {
        var errors = new List<string>();
        var remoteRoot = NormalizeDir(profile.RemotePath);

        // Shared, because a directory another worker already made is one this worker needn't check
        // for. EnsureDir's check-then-create was always racy and already tolerates losing.
        var knownDirs = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);

        var done = 0;
        ForEachInParallel(primary, connect, profile, toUpload.Count, errors, ct, (client, i) =>
        {
            var rel = toUpload[i];
            var localFull = Path.Combine(siteRoot, rel.Replace('/', Path.DirectorySeparatorChar));
            var remoteFull = CombineRemote(remoteRoot, rel);

            try
            {
                EnsureDir(client, ParentOf(remoteFull), knownDirs);
                using (var fs = File.OpenRead(localFull))
                    client.UploadFile(fs, remoteFull, canOverride: true);

                // Preserve mtime so Verify's size+mtime comparison is meaningful. Costs a stat as
                // well as the setstat — SSH.NET reads the current attributes before writing them
                // back, and there is no supported way to skip that.
                client.SetLastWriteTimeUtc(remoteFull, File.GetLastWriteTimeUtc(localFull));

                // Recorded here, on the file that actually arrived, rather than assumed later for
                // the whole batch. It is what makes the manifest true at every moment of the run
                // instead of only at the end of one that got to finish.
                if (local.Files.TryGetValue(rel, out var entry)) delivered.Delivered(rel, entry);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Nothing to retract here: deciding to send it already took it out of the
                // record, and only arriving puts it back.
                lock (errors) errors.Add($"Upload '{rel}': {ex.Message}");
            }

            // Counted on completion, not on start: with several in flight, the index a worker
            // happens to be holding is not "how far through are we".
            Report(progress, SyncPhase.Uploading, "Uploading",
                Interlocked.Increment(ref done), toUpload.Count, rel);
        });

        return errors;
    }

    /// <summary>
    /// Runs <paramref name="body"/> for every index below <paramref name="itemCount"/>, spread over
    /// a pool of connections. Workers pull from a shared counter rather than taking a fixed slice,
    /// so one slow file doesn't leave a worker idle while another still has a queue.
    /// </summary>
    private static void ForEachInParallel(
        SftpClient primary,
        Func<SftpClient> connect,
        SftpProfile profile,
        int itemCount,
        List<string> errors,
        CancellationToken ct,
        Action<SftpClient, int> body)
    {
        var workers = OpenWorkers(primary, connect, profile, itemCount, errors);
        try
        {
            var next = -1;

            void Run(SftpClient client)
            {
                int i;
                while ((i = Interlocked.Increment(ref next)) < itemCount)
                {
                    ct.ThrowIfCancellationRequested();
                    body(client, i);
                }
            }

            if (workers.Count == 1)
            {
                Run(workers[0]);
                return;
            }

            var running = workers.Select(c => Task.Run(() => Run(c), CancellationToken.None))
                                 .ToArray();
            try
            {
                Task.WaitAll(running, CancellationToken.None);
            }
            catch (AggregateException ex)
            {
                var first = ex.Flatten().InnerExceptions[0];
                if (first is OperationCanceledException) throw new OperationCanceledException(ct);
                throw first;
            }
        }
        finally
        {
            // Everything past the first is ours to clean up; the caller owns the primary.
            foreach (var extra in workers.Skip(1))
            {
                try { extra.Disconnect(); } catch { /* going away regardless */ }
                extra.Dispose();
            }
        }
    }

    /// <summary>
    /// The clients to upload on, always including <paramref name="primary"/> first.
    ///
    /// Extras are opened one at a time rather than concurrently: <see cref="Connect"/> writes the
    /// accepted fingerprint back onto the profile and may prompt, neither of which wants several
    /// threads in it at once. The primary has already settled the host key by now, so no extra
    /// connection ever prompts.
    /// </summary>
    private static List<SftpClient> OpenWorkers(
        SftpClient primary,
        Func<SftpClient> connect,
        SftpProfile profile,
        int fileCount,
        List<string> errors)
    {
        var workers = new List<SftpClient> { primary };

        // No point opening a connection that would sit idle — a two-file change isn't worth the
        // handshakes.
        var wanted = Math.Min(profile.EffectiveUploadConcurrency, fileCount);

        for (var i = 1; i < wanted; i++)
        {
            try
            {
                workers.Add(connect());
            }
            catch (Exception ex)
            {
                // Servers cap concurrent sessions per user. Hitting that cap is a reason to go
                // slower, not to fail the deploy.
                errors.Add(
                    $"Only {workers.Count} of {wanted} upload connections opened ({ex.Message}); " +
                    "continuing at reduced concurrency.");
                break;
            }
        }

        return workers;
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
            EnsureDir(client, ParentOf(manifestPath), new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));
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

    /// <summary>
    /// Removes directories left empty by a delete, deepest first.
    /// </summary>
    /// <remarks>
    /// Every candidate is visited exactly once. Walking up from each deleted file's parent
    /// separately looks equivalent and is not: siblings share ancestors, so a folder of 50,000
    /// pages re-listed its parent 50,000 times, and <c>ListDirectory</c> pulls the whole listing
    /// before <c>Any</c> can short-circuit. That is quadratic in the number of deletions, on one
    /// connection, with nothing reported — which is what a large take-down spent its time in,
    /// looking to the user like a finished progress bar attached to a hung app.
    ///
    /// Collecting the ancestors up front is what makes one visit enough: deepest-first then
    /// guarantees a directory is only reached after everything inside it has been dealt with, so
    /// what it holds at that moment is final.
    /// </remarks>
    private static void PruneEmptyDirs(
        SftpClient client, IEnumerable<string> dirs, string remoteRoot, List<string> errors,
        IProgress<SyncProgress>? progress = null, CancellationToken ct = default)
    {
        var ordered = PruneCandidates(dirs, remoteRoot);
        var done = 0;
        foreach (var dir in ordered)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (TryExists(client, dir) &&
                    !client.ListDirectory(dir).Any(f => f.Name is not ("." or "..")))
                {
                    client.DeleteDirectory(dir);
                }
            }
            catch (Exception ex)
            {
                errors.Add($"Prune '{dir}': {ex.Message}");
            }

            Report(progress, SyncPhase.Deleting, "Tidying up empty folders",
                ++done, ordered.Count, dir);
        }
    }

    /// <summary>
    /// Every directory a prune should look at — the ones a delete touched plus their ancestors up
    /// to (but not including) the remote root — each appearing exactly once, deepest first.
    /// </summary>
    /// <remarks>
    /// The dedupe is the whole point, and the ordering is what makes it safe. Siblings share
    /// ancestors, so walking up from each touched directory independently visits a shared parent
    /// once per sibling: 50,000 pages under one folder listed that folder 50,000 times. Emitting
    /// each directory once, with every child of it earlier in the list, means a single look
    /// settles it.
    /// </remarks>
    internal static List<string> PruneCandidates(IEnumerable<string> dirs, string remoteRoot)
    {
        var candidates = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dir in dirs)
        {
            var current = dir;
            // Stops early on an ancestor already collected — that ancestor's own chain is
            // therefore already in the set, so there is nothing above it left to add.
            while (!string.IsNullOrEmpty(current) &&
                   current.Length > remoteRoot.Length &&
                   current.StartsWith(remoteRoot, StringComparison.Ordinal) &&
                   candidates.Add(current))
            {
                current = ParentOf(current);
            }
        }

        return [.. candidates.OrderByDescending(d => d.Length)];
    }

    /// <summary>How many progress reports a counted phase is allowed, however many items it has.</summary>
    private const int ReportSteps = 200;

    /// <summary>
    /// Posts a counted progress report, but not one per item. <see cref="Progress{T}"/> marshals
    /// every report onto the UI thread, so a 50,000-file phase queues 50,000 posts and the
    /// dispatcher is still working through them after the transfer itself has finished — the app
    /// stays unresponsive with the bar sitting at 100%. Two hundred updates is more than a
    /// progress bar can show anyway.
    /// </summary>
    private static void Report(
        IProgress<SyncProgress>? progress, SyncPhase phase, string message,
        int index, int total, string? currentFile)
    {
        if (progress == null) return;

        // The last one always goes, so the bar finishes and the final filename is the real one.
        var step = total <= ReportSteps ? 1 : total / ReportSteps;
        if (index != total && index % step != 0) return;

        progress.Report(new SyncProgress(phase, message, index, total, currentFile));
    }

    private static void EnsureDir(SftpClient client, string dir, ConcurrentDictionary<string, byte> known)
    {
        if (string.IsNullOrEmpty(dir) || dir is "/" or "." || known.ContainsKey(dir)) return;
        try
        {
            if (TryExists(client, dir)) { known[dir] = 0; return; }
        }
        catch { /* fall through to create */ }

        EnsureDir(client, ParentOf(dir), known);
        try { client.CreateDirectory(dir); }
        catch { /* created concurrently or already present */ }
        known[dir] = 0;
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
    /// <param name="Reference">
    /// What the server was believed to hold before this run. Handed back because it is the
    /// starting point for the manifest this run will write: a deploy changes what is up there, it
    /// does not replace it, and the files it never touched are still there.
    /// </param>
    private static (SyncManifestBuilder.Diff Diff, string Note, SyncManifest Reference) Diff(
        SftpClient client, SftpProfile profile, SyncManifest local,
        bool forceFull, IProgress<SyncProgress>? progress, CancellationToken ct)
    {
        var manifestPath = ManifestRemotePath(profile);
        var note = "";

        // What the server is believed to hold, read whether or not a full upload was asked for.
        // Forcing is a statement about what to send, not about what is up there: a file this run
        // doesn't touch is still on the server, and dropping it from the record would strand it
        // published and unreportable — the very thing the record was corrected to stop.
        var known = new SyncManifest();
        if (TryExists(client, manifestPath))
        {
            progress?.Report(new SyncProgress(SyncPhase.Listing, "Reading remote manifest…"));
            known = DownloadManifest(client, manifestPath);
        }
        else if (!forceFull)
        {
            note = " (no remote manifest — uploaded everything; run Verify & Repair to confirm)";
        }

        if (forceFull) note = " (forced full upload)";

        // Comparing against nothing is what makes every file count as needing to be sent. Only the
        // comparison is emptied; `known` keeps its separate job of saying what is already there.
        var reference = forceFull ? new SyncManifest() : known;
        var sending = SyncManifestBuilder.Compare(local, reference);

        // Which leaves stale files, and they are not part of what forcing decides. Taken from
        // `known` either way, so that a forced upload — the thing someone reaches for when they
        // suspect the server has drifted — doesn't go quiet about the files that don't belong.
        var stale = forceFull ? SyncManifestBuilder.Compare(local, known).StaleRemote : sending.StaleRemote;

        ct.ThrowIfCancellationRequested();
        return (new SyncManifestBuilder.Diff(sending.ToUpload, stale), note, known);
    }

    /// <summary>
    /// The manifest this run should leave behind: what the server was believed to hold, updated
    /// with what actually arrived. Successful uploads are recorded into it as they happen.
    /// </summary>
    /// <remarks>
    /// Deriving it from the local folder instead — "assume it all arrived" — made it wrong in
    /// three ways at once. A file whose upload failed was recorded as delivered. A run that was
    /// cancelled recorded files it never attempted. And every file on the server that isn't in the
    /// site was dropped from the record, so it could be reported stale exactly once and then never
    /// again, while it stayed published.
    ///
    /// Starting from the reference and applying outcomes keeps the manifest true at every moment,
    /// which is what lets it be written even when the run is cancelled part-way.
    /// </remarks>
    private sealed class ManifestUpdate
    {
        private readonly Dictionary<string, SyncEntry> _files;

        /// <param name="sending">
        /// Everything this run decided to send. All of it leaves the record immediately, and comes
        /// back only by arriving.
        /// </param>
        /// <remarks>
        /// Deciding a file needs sending is itself a statement that what is up there is not known
        /// to be right — so the old entry, from whenever it was last delivered, has stopped being
        /// evidence. Retracting up front rather than on each way a send can go wrong is what makes
        /// this hold for all of them at once: one that failed, one abandoned when the run was
        /// cancelled, and any future way of not arriving that nobody has thought of yet. Each was
        /// its own silent bug while they were handled one at a time.
        ///
        /// Being wrong here costs an upload. Being wrong the other way costs the file: an entry
        /// left standing is written from the local copy, which doesn't change, so it keeps
        /// matching and no later Quick Sync can ever see anything to act on.
        /// </remarks>
        public ManifestUpdate(SyncManifest reference, IEnumerable<string> sending)
        {
            _files = new Dictionary<string, SyncEntry>(reference.Files, StringComparer.Ordinal);
            foreach (var rel in sending) _files.Remove(rel);
        }

        /// <summary>Records a file as now being on the server. Called from upload workers.</summary>
        public void Delivered(string rel, SyncEntry entry)
        {
            lock (_files) _files[rel] = entry;
        }

        public SyncManifest Snapshot()
        {
            lock (_files) return new SyncManifest { Files = new Dictionary<string, SyncEntry>(_files) };
        }
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
