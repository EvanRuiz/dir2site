// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace dir2site.Services;

/// <summary>
/// Reads the source project folder, with a seam that lets a test make one listing fail the way an
/// unreadable folder does.
/// </summary>
/// <remarks>
/// The generator treats a folder it couldn't read as a gap rather than as content that has gone,
/// which is what stops it offering a live site for deletion. Testing that needs a listing to fail,
/// and permissions are the one thing Unix and Windows genuinely model differently — mode bits
/// against an access-control list — so reproducing it at the OS level means two fixtures and two
/// behaviours to keep true. Reproducing it here instead is the same code on every platform.
///
/// What that gives up is evidence that the OS really throws rather than quietly returning an empty
/// listing. That turns out to be a framework guarantee rather than a per-platform one: the
/// <see cref="SearchOption"/> overloads use <c>EnumerationOptions.Compatible</c>, which sets
/// <c>IgnoreInaccessible = false</c>, so an entry that can't be read raises
/// <see cref="UnauthorizedAccessException"/> instead of being skipped.
/// </remarks>
internal static class SourceListing
{
    // AsyncLocal rather than a plain static: xunit runs test classes in parallel and several of
    // them generate sites, so a plain static would leak a simulated failure into an unrelated run.
    private static readonly AsyncLocal<string?> _unreadable = new();

    /// <summary>Makes listings of <paramref name="path"/> fail until the returned scope is disposed.</summary>
    internal static IDisposable PretendUnreadable(string path) => new Scope(Path.GetFullPath(path));

    /// <summary>
    /// The immediate subdirectories of <paramref name="path"/>, read eagerly so a failure surfaces
    /// here rather than part-way through the caller's loop, where it would escape the caller's
    /// guard entirely.
    /// </summary>
    internal static List<string> Directories(string path)
    {
        Refuse(path);
        return [.. Directory.EnumerateDirectories(path)];
    }

    /// <summary>Every file at or below <paramref name="path"/>, read eagerly for the same reason.</summary>
    internal static List<string> FilesRecursive(string path)
    {
        Refuse(path);
        return [.. Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)];
    }

    private static void Refuse(string path)
    {
        if (_unreadable.Value is not { } denied) return;
        if (!string.Equals(Path.GetFullPath(path), denied, StringComparison.OrdinalIgnoreCase)) return;

        throw new UnauthorizedAccessException($"Access to the path '{path}' is denied.");
    }

    private sealed class Scope : IDisposable
    {
        private readonly string? _previous;

        public Scope(string path)
        {
            _previous = _unreadable.Value;
            _unreadable.Value = path;
        }

        public void Dispose() => _unreadable.Value = _previous;
    }
}
