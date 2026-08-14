// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Diagnostics;
using System.IO;

namespace dir2site.Tests;

/// <summary>
/// Makes a directory exist but refuse to be listed, and puts it back afterwards. This is the state
/// the generator has to survive without offering the site for deletion, and there is no portable
/// API for it — Unix has the mode bits, Windows has an ACL. Both are reachable without a new
/// dependency: <see cref="File.SetUnixFileMode"/> on one, the built-in <c>icacls</c> on the other.
/// </summary>
/// <remarks>
/// Windows matters most here: the failure this guards against is triggered by cloud-synced project
/// folders, network shares and virus scanners, which is a description of an ordinary Windows
/// desktop. A test that only ran on macOS would be watching the wrong machine.
/// </remarks>
public sealed class UnreadableDirectory : IDisposable
{
    private readonly string _path;
    private readonly bool _windows = OperatingSystem.IsWindows();

    private UnreadableDirectory(string path) => _path = path;

    /// <summary>
    /// Denies listing on <paramref name="path"/>, or returns null when this machine won't allow it
    /// — an elevated Windows session can bypass a deny ACE, and a test that quietly passed on a
    /// readable directory would be worse than one that says it couldn't run.
    /// </summary>
    public static UnreadableDirectory? Make(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!Icacls($"\"{path}\" /deny \"%USERNAME%\":(RX) /Q")) return null;
        }
        else
        {
            File.SetUnixFileMode(path, UnixFileMode.None);
        }

        // Prove it actually took, rather than trusting the tool's exit code.
        try
        {
            Directory.EnumerateDirectories(path).GetEnumerator().MoveNext();
            new UnreadableDirectory(path).Dispose();   // still readable — undo and report failure
            return null;
        }
        catch
        {
            return new UnreadableDirectory(path);
        }
    }

    public void Dispose()
    {
        try
        {
            if (_windows) Icacls($"\"{_path}\" /remove:d \"%USERNAME%\" /Q");
            else File.SetUnixFileMode(_path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch { /* best effort — the fixture's own cleanup reports if this leaks */ }
    }

    private static bool Icacls(string arguments)
    {
        try
        {
            // cmd /c so %USERNAME% is expanded; icacls takes the account name, not a SID.
            using var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c icacls {arguments}")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p == null) return false;
            p.WaitForExit(15_000);
            return p.HasExited && p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
