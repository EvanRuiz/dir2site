// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.IO;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace dir2site.Tests;

/// <summary>
/// Makes a directory exist but refuse to be listed, and puts it back afterwards. This is the state
/// the generator has to survive without offering the site for deletion, and it is the one thing
/// here with no single cross-platform API: permissions are genuinely different models, mode bits
/// on Unix and an access-control list on Windows. Both are reachable from the BCL, so each branch
/// is typed and checked rather than a command line built out of strings.
/// </summary>
/// <remarks>
/// Windows matters most: the failure this guards against is triggered by cloud-synced project
/// folders, network shares and virus scanners, which describes an ordinary Windows desktop. A test
/// that only ran on macOS would be watching the wrong machine.
/// </remarks>
public sealed class UnreadableDirectory : IDisposable
{
    private readonly string _path;
    private readonly FileSystemAccessRule? _denyRule;

    private UnreadableDirectory(string path, FileSystemAccessRule? denyRule)
    {
        _path = path;
        _denyRule = denyRule;
    }

    /// <summary>
    /// Denies listing on <paramref name="path"/>, or returns null when this machine won't allow it.
    /// Returning null rather than throwing lets the caller decide: a test that quietly passed on a
    /// directory that stayed readable would be worse than one that says it proved nothing.
    /// </summary>
    public static UnreadableDirectory? Make(string path)
    {
        FileSystemAccessRule? denyRule = null;
        if (OperatingSystem.IsWindows()) denyRule = Deny(path);
        else File.SetUnixFileMode(path, UnixFileMode.None);

        var handle = new UnreadableDirectory(path, denyRule);

        // Prove it took, rather than trusting that setting a rule had the intended effect — an
        // elevated session or an inherited allow can outrank the deny.
        try
        {
            Directory.EnumerateDirectories(path).GetEnumerator().MoveNext();
        }
        catch
        {
            return handle;
        }

        handle.Dispose();
        return null;
    }

    public void Dispose()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (_denyRule != null) Allow(_path, _denyRule);
            }
            else
            {
                File.SetUnixFileMode(_path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
        catch
        {
            // Best effort. The fixture's own cleanup is what would report a genuinely stuck
            // directory, and leaving a temp folder behind is better than failing a passing test.
        }
    }

    [SupportedOSPlatform("windows")]
    private static FileSystemAccessRule Deny(string path)
    {
        var info = new DirectoryInfo(path);
        var security = info.GetAccessControl();

        // The current user specifically, not a group: it's the identity the test runs as, and it
        // outranks any inherited allow because deny is evaluated first.
        var rule = new FileSystemAccessRule(
            WindowsIdentity.GetCurrent().User!,
            FileSystemRights.ListDirectory | FileSystemRights.ReadData,
            AccessControlType.Deny);

        security.AddAccessRule(rule);
        info.SetAccessControl(security);
        return rule;
    }

    [SupportedOSPlatform("windows")]
    private static void Allow(string path, FileSystemAccessRule rule)
    {
        var info = new DirectoryInfo(path);
        var security = info.GetAccessControl();
        security.RemoveAccessRule(rule);
        info.SetAccessControl(security);
    }
}
