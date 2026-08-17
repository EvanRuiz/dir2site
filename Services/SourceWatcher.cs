// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace dir2site.Services;

/// <summary>
/// Watches the source project folder and reports what the user did to it.
/// </summary>
/// <remarks>
/// The generator has always had to work out what changed by comparing the site it just built
/// against the one on disk, which cannot tell a move from a delete from a file somebody dropped in
/// by hand — so everything unaccounted for went to one confirmation dialog. Watching supplies the
/// missing input directly: a folder the user dragged is a move, and its pages can follow without
/// anyone being asked to approve deleting content they never deleted.
///
/// What this class does <em>not</em> promise is completeness. Events are lost to buffer overflows,
/// to cloud-sync volumes that under-report, and to the app simply not running when the change
/// happened. That is why every batch carries <see cref="SourceChangeBatch.Witnessed"/>: the tree is
/// always rebuilt from disk, so a missed event costs nothing there, and the only thing riding on
/// the watcher is whether we may act on a classification or must offer it. Missing an event
/// therefore degrades into asking, never into a wrong deletion.
/// </remarks>
public sealed class SourceWatcher : IDisposable
{
    /// <summary>
    /// How long the folder must go quiet before a burst is called finished.
    /// </summary>
    /// <remarks>
    /// Longer than the preview server's 300ms (<see cref="PreviewServerService"/>) because what
    /// happens at the end is a rescan and possibly a rebuild, not a browser refresh — and because
    /// the bursts here are bigger. Copying a folder of photos in emits events for as long as the
    /// copy takes, and every premature wake-up is a scan of a tree that is still moving.
    /// </remarks>
    internal const int DefaultDebounceMs = 1000;

    private readonly string _root;
    private readonly int _debounceMs;
    private readonly object _gate = new();
    private readonly List<RawSourceEvent> _pending = [];

    private FileSystemWatcher? _watcher;
    private System.Timers.Timer? _debounce;
    private bool _lostEvents;
    private bool _disposed;

    /// <summary>Raised once a burst has settled. Never raised for an empty batch.</summary>
    public event EventHandler<SourceChangeBatch>? Changed;

    /// <summary>
    /// Runs inside a settle, after the events are read and before they are delivered.
    /// </summary>
    /// <remarks>
    /// Exists so the disposal guard below can be tested at all. Disposing a watcher while a settle
    /// is part-way through is a window a few instructions wide, and a test that hoped to land in it
    /// would be reporting how loaded the machine is — the bug and the correct behaviour both look
    /// like "a delivery arrived around the time Dispose returned". Given a hook, the test causes
    /// the interleaving instead of waiting for it, and the assertion is exact every run.
    /// </remarks>
    internal Action? SettlingForTests;

    /// <param name="debounceMs">
    /// Overridable so tests don't spend a second per assertion. Production has no reason to pass it.
    /// </param>
    public SourceWatcher(string root, int debounceMs = DefaultDebounceMs)
    {
        _root       = Path.GetFullPath(root);
        _debounceMs = debounceMs;
    }

    public void Start()
    {
        if (_watcher != null) return;

        _debounce = new System.Timers.Timer(_debounceMs) { AutoReset = false };
        _debounce.Elapsed += (_, _) => Settle();

        _watcher = new FileSystemWatcher(_root)
        {
            IncludeSubdirectories = true,
            // DirectoryName is what makes a folder move visible at all, which is the change this
            // whole feature exists for. Size and LastWrite catch a file being written; without both,
            // a copy that preserves timestamps looks like nothing happened.
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                         | NotifyFilters.LastWrite | NotifyFilters.Size,
            // Only meaningful on Windows, where it sizes the ReadDirectoryChangesW buffer and 8KB
            // is a few dozen paths. Elsewhere the backend queues its own way and this is ignored —
            // which is why Error below, not this, is the defence we actually rely on.
            InternalBufferSize = 64 * 1024,
        };

        _watcher.Created += (_, e) => Record(RawChangeKind.Created, e.FullPath);
        _watcher.Changed += (_, e) => Record(RawChangeKind.Changed, e.FullPath);
        _watcher.Deleted += (_, e) => Record(RawChangeKind.Deleted, e.FullPath);
        _watcher.Renamed += (_, e) => Record(RawChangeKind.Renamed, e.FullPath, e.OldFullPath);
        _watcher.Error   += (_, _) => LoseEvents();

        _watcher.EnableRaisingEvents = true;
    }

    /// <summary>
    /// Records that this burst can no longer be trusted to describe what happened.
    /// </summary>
    /// <remarks>
    /// Reached on a buffer overflow on Windows and on inotify running out of watches on Linux. Both
    /// mean the same thing — some events exist that we never saw — and the honest response is to
    /// stop claiming to know. The batch still fires, so the tree is rescanned and the UI stays
    /// truthful; it simply arrives unwitnessed, and unwitnessed deletions get offered rather than
    /// applied.
    /// </remarks>
    private void LoseEvents()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _lostEvents = true;
            _pending.Clear();
        }

        // An error is not only lost events: on a deleted or renamed watch root the OS watch itself
        // is gone, and nothing would ever arrive again. Silently, too — auto-generate would simply
        // stop working with the checkbox still ticked. Try to pick the folder up again; if it is
        // genuinely no longer there, say so rather than leaving the user to notice.
        if (!TryRearm()) Stopped?.Invoke(this, EventArgs.Empty);

        Restart();
    }

    /// <summary>
    /// Raised when watching has failed and could not be resumed. The folder is gone or unreadable;
    /// nothing further will arrive until someone rescans or reopens the project.
    /// </summary>
    public event EventHandler? Stopped;

    private bool TryRearm()
    {
        lock (_gate)
        {
            if (_disposed || _watcher == null) return false;
            if (!Directory.Exists(_root)) return false;

            try
            {
                // Re-pointing it at the same path is enough to make the platform establish a new
                // watch, where simply setting EnableRaisingEvents back on is not.
                _watcher.EnableRaisingEvents = false;
                _watcher.Path = _root;
                _watcher.EnableRaisingEvents = true;
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    private void Record(RawChangeKind kind, string path, string? oldPath = null)
    {
        if (!IsWatchable(path) && !(oldPath != null && IsWatchable(oldPath)))
            return;

        lock (_gate) _pending.Add(new RawSourceEvent(kind, path, oldPath));
        Restart();
    }

    /// <remarks>
    /// Under the same lock as disposal, because this runs on a <see cref="FileSystemWatcher"/>
    /// callback thread while <see cref="Dispose"/> runs on the UI thread — and an event landing
    /// between reading the timer and starting it threw <c>ObjectDisposedException</c> on a
    /// background thread, which takes the process down. Narrow, and fatal when it happens.
    /// </remarks>
    private void Restart()
    {
        lock (_gate)
        {
            if (_disposed) return;

            // Stop-then-start rather than a running interval, so the window measures silence rather
            // than elapsed time — a copy still in progress keeps pushing the settle point back.
            _debounce?.Stop();
            _debounce?.Start();
        }
    }

    private void Settle()
    {
        List<RawSourceEvent> events;
        bool lost;

        lock (_gate)
        {
            // Disposing does not wait for an Elapsed handler that is already running, so this can
            // arrive after the watcher was let go — and the caller disposes when the project
            // changes, having just cleared the lists this batch would be added to. A settle from
            // the previous project then lands in them, and its paths are carried through:
            // sidecars moved and preview folders deleted in a project nobody has open.
            //
            // Held by ASettleInterruptedByDisposal_DeliversNothing, which reaches the window through
            // SettlingForTests rather than trying to time its way into it. Watching from outside
            // cannot tell this bug from correct behaviour — both look like a delivery arriving about
            // the moment Dispose returns — so the first attempt at that test failed three times for
            // the code working, and reported how busy the machine was rather than anything true.
            if (_disposed) return;

            events = [.. _pending];
            lost   = _lostEvents;
            _pending.Clear();
            _lostEvents = false;
        }

        if (events.Count == 0 && !lost) return;

        var batch = SourceChangeCoalescer.Coalesce(events, witnessed: !lost);

        // An unwitnessed batch is empty by construction but still has to be delivered: it is the
        // signal to rescan and to stop trusting classifications, which is the opposite of nothing
        // having happened.
        if (batch.IsEmpty && batch.Witnessed) return;

        // A seam, in the manner of PretendWatchingStopped: the window this guard closes is a few
        // instructions wide, so a test that waited for disposal to land inside it would be racing
        // rather than asserting. Called here, a test can put the disposal exactly where it belongs
        // and the outcome stops depending on how busy the machine is.
        SettlingForTests?.Invoke();

        // Checked again on the way out, because everything above happens outside the lock and the
        // window this closes is exactly that long.
        lock (_gate) if (_disposed) return;

        Changed?.Invoke(this, batch);
    }

    /// <summary>
    /// Whether a reported path is one we have any business reacting to.
    /// </summary>
    /// <remarks>
    /// The rules are the walk's own (<see cref="DirectoryTraverser"/>), reused rather than restated
    /// so the watcher cannot drift from what the generator considers content. Two things make this
    /// more than a call-through:
    ///
    /// The ancestor check is the load-bearing one. Events arrive as whole paths, so
    /// <c>.dir2site/Portrait/preview-Portrait.webp</c> has a perfectly ordinary leaf and an ancestor
    /// that means "we wrote this ourselves". Judging the leaf alone would have every generate
    /// trigger the next one.
    ///
    /// Sidecars are the deliberate exception. The walk drops <c>.yaml</c> because it is metadata
    /// rather than a content node, but a hand-edited yaml is precisely a change the UI has to
    /// reflect (#62), so it is watched. <c>.json</c> is not: nothing in the source tree is meant to
    /// be one, and the app's own are all under <c>.dir2site/</c> and excluded above.
    /// </remarks>
    private bool IsWatchable(string fullPath)
    {
        if (DirectoryTraverser.IsUnderIgnoredDirectory(_root, fullPath))
            return false;

        var name = Path.GetFileName(fullPath);
        if (name.Length == 0)
            return false;

        // A directory event names the directory itself, and the ancestor walk above has already
        // cleared everything above it — so the name still has to be judged on its own terms.
        if (DirectoryTraverser.IsIgnoredDirectoryName(name))
            return false;

        if (DirectoryTraverser.IsIgnoredFileName(name))
            return false;

        var ext = Path.GetExtension(name);
        if (ext.Equals(".json", StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    public void Dispose()
    {
        // Silence the source before taking the timer away, so nothing is still arriving to restart
        // it — and hold the lock, so anything already in flight has finished with it.
        var watcher = _watcher;
        if (watcher != null) watcher.EnableRaisingEvents = false;

        lock (_gate)
        {
            _disposed = true;
            _debounce?.Dispose();
            _debounce = null;
        }

        watcher?.Dispose();
        _watcher = null;
    }
}
