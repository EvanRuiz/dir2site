// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.IO;

namespace dir2site.Services;

/// <summary>What became of the project folder itself.</summary>
public enum ProjectFolderChange
{
    /// <summary>It is still there, under a new name. <see cref="ProjectFolderEvent.NewPath"/> says where.</summary>
    Renamed,

    /// <summary>It was deleted, or moved somewhere we cannot see.</summary>
    Gone,
}

/// <param name="NewPath">Where the folder is now. Set only for <see cref="ProjectFolderChange.Renamed"/>.</param>
public sealed record ProjectFolderEvent(ProjectFolderChange Kind, string? NewPath = null);

/// <summary>
/// Watches the project folder's own existence, from the folder above it.
/// </summary>
/// <remarks>
/// <see cref="SourceWatcher"/> watches inside the project and cannot answer this: on macOS a watcher
/// whose root is renamed, deleted or moved raises no error and delivers no events — it simply falls
/// silent, so nothing distinguishes "the project has gone" from "nobody has touched anything".
/// Measured on all three, and on the fourth case that matters more than it sounds: rename the folder
/// away, put a new one at the same path, and the old watcher carries on delivering events for the
/// new folder as though it were this project.
///
/// The parent has none of that trouble, because the parent is still there. A project leaving it is
/// an ordinary entry change in a directory that is very much alive, and the platform reports it —
/// with the new name attached when there is one, which is what lets the app offer to follow rather
/// than only to give up.
///
/// Narrow on purpose: one directory, no subdirectories, names only. A sibling folder appearing is
/// the entire noise budget, and a file written next door produces nothing at all.
///
/// What it still cannot see is an unmount, where the parent goes with the volume. Watching further
/// up would mean walking to the mount point, which is a worse rule than the problem deserves —
/// and the write that would follow fails anyway, because <c>/Volumes</c> is not ours to create in.
/// </remarks>
public sealed class ProjectFolderWatcher : IDisposable
{
    private readonly string _parent;
    private readonly string _name;
    private readonly object _gate = new();

    private FileSystemWatcher? _watcher;
    private bool _disposed;

    /// <summary>Raised once, on the folder leaving. Never raised for anything inside it.</summary>
    public event EventHandler<ProjectFolderEvent>? Changed;

    /// <param name="projectRoot">The folder to keep an eye on, not the one that is watched.</param>
    public ProjectFolderWatcher(string projectRoot)
    {
        var full = Path.GetFullPath(projectRoot);
        _parent  = Path.GetDirectoryName(full) ?? string.Empty;
        _name    = Path.GetFileName(full);
    }

    /// <summary>
    /// Begins watching. Does nothing when there is no parent to watch — a project opened at the root
    /// of a volume has nowhere above it, and that is a fact about the path rather than a failure.
    /// </summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _watcher != null) return;
            if (_parent.Length == 0 || _name.Length == 0) return;
            if (!Directory.Exists(_parent)) return;

            var watcher = new FileSystemWatcher(_parent)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.DirectoryName,
            };

            watcher.Renamed += OnRenamed;
            watcher.Deleted += OnDeleted;

            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        if (!IsOurs(e.OldFullPath)) return;
        Raise(new ProjectFolderEvent(ProjectFolderChange.Renamed, e.FullPath));
    }

    /// <remarks>
    /// A folder moved somewhere this watch has no view of arrives here rather than as a rename:
    /// there is no destination to report, which is why the two are separate kinds rather than a
    /// rename with a missing half.
    ///
    /// Where the line falls is the platform's to decide, and they disagree — dragging the project
    /// into a folder beside it is a rename with a full destination on macOS, and a plain departure
    /// on a Windows or Linux watch scoped to one directory. Nothing here depends on which: a
    /// platform that says less costs the user the offer to follow and nothing else.
    /// </remarks>
    private void OnDeleted(object sender, FileSystemEventArgs e)
    {
        if (!IsOurs(e.FullPath)) return;
        Raise(new ProjectFolderEvent(ProjectFolderChange.Gone));
    }

    /// <summary>
    /// Whether an event names the project folder rather than one of its siblings.
    /// </summary>
    /// <remarks>
    /// Case-insensitively, matching how the rest of the app compares paths (see
    /// <c>SourceChangeCoalescer</c>): on the case-folding filesystems the app ships to, the same
    /// folder is reported under either spelling, and a project that stopped being watched because
    /// the event said "Riverbend" would be the worst kind of intermittent.
    ///
    /// The trade is on a case-sensitive filesystem, where a sibling differing only in case would be
    /// read as this project leaving. It costs one dialog about a folder that hasn't moved, which is
    /// the cheaper way round to be wrong.
    /// </remarks>
    private bool IsOurs(string fullPath) =>
        string.Equals(Path.GetFileName(fullPath), _name, StringComparison.OrdinalIgnoreCase);

    /// <remarks>
    /// Raised outside the lock, and only while we are still the live watcher. What follows this is
    /// a modal question and the cancelling of whatever is running, none of which should happen
    /// twice because two events described one departure.
    /// </remarks>
    private void Raise(ProjectFolderEvent change)
    {
        EventHandler<ProjectFolderEvent>? handler;

        lock (_gate)
        {
            if (_disposed || _watcher == null) return;

            // One report per watcher. The folder can only leave once, and a rename that arrives as
            // a delete followed by a create — which is how some platforms spell a move — would
            // otherwise ask the same question twice.
            _watcher.EnableRaisingEvents = false;
            handler = Changed;
        }

        handler?.Invoke(this, change);
    }

    public void Dispose()
    {
        FileSystemWatcher? watcher;

        lock (_gate)
        {
            _disposed = true;
            watcher = _watcher;
            _watcher = null;
        }

        if (watcher == null) return;

        watcher.EnableRaisingEvents = false;
        watcher.Renamed -= OnRenamed;
        watcher.Deleted -= OnDeleted;
        watcher.Dispose();
    }
}
