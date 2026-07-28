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
/// Spins up a throwaway, unprivileged OpenSSH <c>sshd</c> on a free loopback port with key-only
/// auth and the in-process <c>internal-sftp</c> subsystem. Used by <see cref="SftpSyncServiceTests"/>.
///
/// If a usable <c>sshd</c>/<c>ssh-keygen</c> isn't present (e.g. Windows, or a CI image without
/// openssh-server), <see cref="Available"/> is false and the integration tests skip cleanly.
/// </summary>
public sealed class SftpServerFixture : IDisposable
{
    private Process? _sshd;

    public bool Available { get; }
    public string Reason { get; } = "";

    public int Port { get; }
    public string User { get; } = Environment.UserName;
    public string BaseDir { get; }
    public string ClientKeyPath { get; }
    public string WrongKeyPath { get; }

    /// <summary>The server's real host key fingerprint, in the SHA256:base64 form profiles pin.</summary>
    public string HostKeyFingerprint { get; } = "";

    private readonly string _keygenPath = "";
    private readonly string _hostKeyPubPath = "";

    public SftpServerFixture()
    {
        BaseDir = Path.Combine(Path.GetTempPath(), "d2s-sshd-" + Guid.NewGuid().ToString("N"));
        ClientKeyPath = Path.Combine(BaseDir, "clientkey");
        WrongKeyPath = Path.Combine(BaseDir, "wrongkey");

        var sshd = FindBinary("sshd", "/usr/sbin/sshd", "/usr/local/sbin/sshd");
        var keygen = FindBinary("ssh-keygen", "/usr/bin/ssh-keygen", "/usr/local/bin/ssh-keygen");
        if (sshd == null || keygen == null)
        {
            Reason = "sshd/ssh-keygen not found on this platform — integration tests skipped.";
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.Combine(BaseDir, "srv"));
            Directory.CreateDirectory(Path.Combine(BaseDir, "run"));

            var hostKey = Path.Combine(BaseDir, "hostkey");
            RunOrThrow(keygen, ["-q", "-t", "ed25519", "-f", hostKey, "-N", ""]);
            RunOrThrow(keygen, ["-q", "-t", "ed25519", "-f", ClientKeyPath, "-N", ""]);
            RunOrThrow(keygen, ["-q", "-t", "ed25519", "-f", WrongKeyPath, "-N", ""]);

            HostKeyFingerprint = ReadPublicKeyFingerprint(hostKey + ".pub");
            _keygenPath = keygen;
            _hostKeyPubPath = hostKey + ".pub";

            var authKeys = Path.Combine(BaseDir, "authorized_keys");
            File.WriteAllText(authKeys, File.ReadAllText(ClientKeyPath + ".pub"));
            Chmod600(hostKey, ClientKeyPath, WrongKeyPath, authKeys);

            Port = FreePort();
            var config = Path.Combine(BaseDir, "sshd_config");
            File.WriteAllText(config, $"""
                Port {Port}
                ListenAddress 127.0.0.1
                HostKey {hostKey}
                PidFile {Path.Combine(BaseDir, "run", "sshd.pid")}
                AuthorizedKeysFile {authKeys}
                PasswordAuthentication no
                KbdInteractiveAuthentication no
                PubkeyAuthentication yes
                StrictModes no
                UsePAM no
                LogLevel ERROR
                Subsystem sftp internal-sftp
                """);

            _sshd = Process.Start(new ProcessStartInfo(sshd, $"-D -f \"{config}\" -E \"{Path.Combine(BaseDir, "sshd.log")}\"")
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            });

            if (!WaitForPort(Port, TimeSpan.FromSeconds(8)))
            {
                Reason = "sshd did not accept connections in time.";
                Dispose();
                return;
            }

            Available = true;
        }
        catch (Exception ex)
        {
            Reason = $"Could not start sshd: {ex.Message}";
            Dispose();
        }
    }

    /// <summary>A fresh isolated deployment: its own remote dir under the server and a local site dir.</summary>
    public Deployment NewDeployment()
    {
        var id = Guid.NewGuid().ToString("N");
        var siteDir = Path.Combine(BaseDir, "site", id);
        var remoteDir = Path.Combine(BaseDir, "srv", id);
        Directory.CreateDirectory(siteDir);
        Directory.CreateDirectory(remoteDir);

        var profile = new SftpProfile
        {
            Host = "127.0.0.1",
            Port = Port,
            Username = User,
            RemotePath = remoteDir,
            AuthMethod = SftpAuthMethod.Key,
            PrivateKeyPath = ClientKeyPath,
            // Pin the real key so the tests exercise the trusted-key path rather than an
            // accept-everything stub. Host-key refusal is covered explicitly in its own tests.
            HostKeyFingerprint = HostKeyFingerprint,
        };
        return new Deployment(profile, siteDir, remoteDir);
    }

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
        try { if (_sshd is { HasExited: false }) _sshd.Kill(entireProcessTree: true); } catch { }
        try { _sshd?.Dispose(); } catch { }
        try { Directory.Delete(BaseDir, recursive: true); } catch { }
    }

    // ---- helpers -----------------------------------------------------------

    private static string? FindBinary(string name, params string[] candidates)
    {
        foreach (var c in candidates)
            if (File.Exists(c)) return c;
        return null;
    }

    private static void Chmod600(params string[] paths)
    {
        if (OperatingSystem.IsWindows()) return;
        foreach (var p in paths)
            File.SetUnixFileMode(p, UnixFileMode.UserRead | UnixFileMode.UserWrite);
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
