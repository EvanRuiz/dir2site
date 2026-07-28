// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Linq;
using System.Threading;
using dir2site.SftpSync.Core;

namespace dir2site.Tests;

/// <summary>
/// Spins up a throwaway <c>rclone serve sftp</c> on a free loopback port with key-only auth,
/// serving a temp directory. Used by <see cref="SftpSyncServiceTests"/>.
///
/// rclone authenticates against its own <c>authorized_keys</c> rather than OS accounts, so this
/// runs unprivileged and identically on Windows, macOS and Linux — which <c>sshd</c> cannot do on
/// Windows, where it needs privileges to mint a user token. The binary is fetched and verified
/// against a pinned hash by <see cref="RcloneTool"/>; see <c>tests/tools/rclone/README.md</c>.
///
/// If no rclone is pinned for this platform, <see cref="Available"/> is false and the integration
/// tests skip cleanly.
/// </summary>
public sealed class SftpServerFixture : IDisposable
{
    private Process? _server;

    public bool Available { get; }
    public string Reason { get; } = "";

    public int Port { get; }
    public string User { get; } = Environment.UserName;
    public string BaseDir { get; }
    public string ClientKeyPath { get; }
    public string WrongKeyPath { get; }

    /// <summary>The directory rclone serves. Remote paths in profiles are relative to this.</summary>
    public string ServedRoot { get; }

    /// <summary>The server's real host key fingerprint, in the SHA256:base64 form profiles pin.</summary>
    public string HostKeyFingerprint { get; } = "";

    private readonly string _keygenPath = "";
    private readonly string _hostKeyPubPath = "";

    public SftpServerFixture()
    {
        BaseDir = Path.Combine(Path.GetTempPath(), "d2s-sftp-" + Guid.NewGuid().ToString("N"));
        ServedRoot = Path.Combine(BaseDir, "srv");
        ClientKeyPath = Path.Combine(BaseDir, "clientkey");
        WrongKeyPath = Path.Combine(BaseDir, "wrongkey");

        var rclone = RcloneTool.Resolve(out var rcloneReason);
        var keygen = FindKeygen();
        if (rclone == null || keygen == null)
        {
            Reason = rclone == null
                ? rcloneReason
                : "ssh-keygen not found on this platform — integration tests skipped.";
            return;
        }

        try
        {
            Directory.CreateDirectory(ServedRoot);

            var hostKey = Path.Combine(BaseDir, "hostkey");
            RunOrThrow(keygen, ["-q", "-t", "ed25519", "-f", hostKey, "-N", ""]);
            RunOrThrow(keygen, ["-q", "-t", "ed25519", "-f", ClientKeyPath, "-N", ""]);
            RunOrThrow(keygen, ["-q", "-t", "ed25519", "-f", WrongKeyPath, "-N", ""]);

            HostKeyFingerprint = ReadPublicKeyFingerprint(hostKey + ".pub");
            _keygenPath = keygen;
            _hostKeyPubPath = hostKey + ".pub";

            var authKeys = Path.Combine(BaseDir, "authorized_keys");
            File.WriteAllText(authKeys, File.ReadAllText(ClientKeyPath + ".pub"));

            Port = FreePort();

            var psi = new ProcessStartInfo(rclone)
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            foreach (var a in new[]
            {
                "serve", "sftp",
                "--addr", $"127.0.0.1:{Port}",
                "--key", hostKey,
                "--authorized-keys", authKeys,
                // Keep rclone off the developer's real config and cache.
                "--config", Path.Combine(BaseDir, "rclone.conf"),
                "--cache-dir", Path.Combine(BaseDir, "cache"),
                // Tests mutate the served tree directly to simulate server-side changes, so the
                // VFS must never answer from a cached listing.
                "--dir-cache-time", "0s",
                "--log-file", Path.Combine(BaseDir, "rclone.log"),
                ServedRoot,
            }) psi.ArgumentList.Add(a);

            _server = Process.Start(psi);

            if (!WaitForPort(Port, TimeSpan.FromSeconds(15)))
            {
                Reason = "rclone did not accept connections in time.";
                Dispose();
                return;
            }

            Available = true;
        }
        catch (Exception ex)
        {
            Reason = $"Could not start rclone: {ex.Message}";
            Dispose();
        }
    }

    /// <summary>A fresh isolated deployment: its own remote dir under the server and a local site dir.</summary>
    public Deployment NewDeployment()
    {
        var id = Guid.NewGuid().ToString("N");
        var siteDir = Path.Combine(BaseDir, "site", id);
        var remoteDir = Path.Combine(ServedRoot, id);
        Directory.CreateDirectory(siteDir);
        Directory.CreateDirectory(remoteDir);

        var profile = new SftpProfile
        {
            Host = "127.0.0.1",
            Port = Port,
            Username = User,
            RemotePath = "/" + id,
            AuthMethod = SftpAuthMethod.Key,
            PrivateKeyPath = ClientKeyPath,
            // Pin the real key so the tests exercise the trusted-key path rather than an
            // accept-everything stub. Host-key refusal is covered explicitly in its own tests.
            HostKeyFingerprint = HostKeyFingerprint,
        };
        return new Deployment(profile, siteDir, remoteDir);
    }

    /// <summary>Maps a server-relative remote path to where it actually lands on disk.</summary>
    public string LocalPathFor(string remotePath) =>
        Path.Combine(ServedRoot, remotePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));

    public sealed record Deployment(SftpProfile Profile, string SiteDir, string RemoteDir);

    /// <summary>
    /// What OpenSSH itself reports for the host key, used to prove our formatter agrees with the
    /// string a user would compare against. `ssh-keygen -lf` prints "&lt;bits&gt; SHA256:&lt;b64&gt; &lt;comment&gt;".
    /// </summary>
    public string SshKeygenHostKeyFingerprint()
    {
        var psi = new ProcessStartInfo(_keygenPath)
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-lf");
        psi.ArgumentList.Add(_hostKeyPubPath);

        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit();

        return stdout.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                     .First(f => f.StartsWith("SHA256:", StringComparison.Ordinal));
    }

    // An OpenSSH .pub line is "<algo> <base64 keyblob> [comment]"; the fingerprint is taken over
    // the decoded blob, which is the same bytes SSH.NET hands to HostKeyReceived.
    private static string ReadPublicKeyFingerprint(string pubKeyPath)
    {
        var fields = File.ReadAllText(pubKeyPath).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return HostKeyFingerprintFormatter.Format(Convert.FromBase64String(fields[1]));
    }

    public void Dispose()
    {
        try { if (_server is { HasExited: false }) _server.Kill(entireProcessTree: true); } catch { }
        try { _server?.Dispose(); } catch { }
        try { Directory.Delete(BaseDir, recursive: true); } catch { }
    }

    // ---- helpers -----------------------------------------------------------

    private static string? FindKeygen()
    {
        string[] candidates = OperatingSystem.IsWindows()
            ? [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "OpenSSH", "ssh-keygen.exe")]
            : ["/usr/bin/ssh-keygen", "/usr/local/bin/ssh-keygen", "/opt/homebrew/bin/ssh-keygen"];

        return candidates.FirstOrDefault(File.Exists);
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static bool WaitForPort(int port, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var c = new TcpClient();
                c.Connect(IPAddress.Loopback, port);
                return true;
            }
            catch
            {
                Thread.Sleep(150);
            }
        }
        return false;
    }

    private static void RunOrThrow(string file, string[] args)
    {
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"{file} failed ({p.ExitCode}): {err}");
    }
}
