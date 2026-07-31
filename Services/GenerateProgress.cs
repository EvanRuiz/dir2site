// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace dir2site.Services;

/// <summary>
/// A progress report from a running generate: the detail message the pipeline is currently on, plus
/// an overall view across all four stages. "Generating cats/index.html" tells you the machine is
/// alive; "Pages 12/500" tells you whether to go get a coffee.
/// </summary>
public sealed record GenerateProgress(string Message, string Counters);

/// <summary>What a counted item turned out to be: untouched, brand new, or changed since last time.</summary>
public enum Change { None, New, Updated }

/// <summary>
/// Counts the work of a generate as it happens, and formats it as
/// <c>Artifacts 340/340 (2 new, 5 updated) · Previews 120/338 (118 new) · Pages 12/500 (3 new)</c>.
///
/// It is an <see cref="IProgress{T}"/> of string, so every existing <c>progress?.Report("…")</c>
/// deep in the pipeline keeps working untouched — those calls set the message line, while the
/// stage methods here move the counters.
///
/// A stage is absent from the line until its total is known: a half-drawn "Files 0/0" would read
/// as "nothing to do" rather than "not counted yet". Within a stage, the parenthetical is read off
/// what happened to the output — new means the site had no such thing before, updated means it had
/// one and it now differs. Nothing is inferred from source timestamps, so a rebuilt thumbnail
/// doesn't masquerade as a changed artifact.
/// </summary>
public sealed class GenerateProgressTracker(IProgress<GenerateProgress>? sink = null) : IProgress<string>
{
    private sealed class Stage(string label)
    {
        public string Label { get; } = label;
        private int _total = -1;     // -1 = not counted yet
        private int _done;
        private int _new;
        private int _updated;

        public int Total   => Volatile.Read(ref _total);
        public int Done    => Volatile.Read(ref _done);
        public int New     => Volatile.Read(ref _new);
        public int Updated => Volatile.Read(ref _updated);
        public bool HasTotal => Total >= 0;

        public void SetTotal(int total) => Volatile.Write(ref _total, total);

        public void Add(int count, Change change)
        {
            if (count <= 0) return;
            Interlocked.Add(ref _done, count);
            Note(change, count);
        }

        public void Note(Change change, int count = 1)
        {
            if (count <= 0) return;
            if (change == Change.New)     Interlocked.Add(ref _new, count);
            if (change == Change.Updated) Interlocked.Add(ref _updated, count);
        }
    }

    private readonly Stage _artifacts = new("Artifacts");
    private readonly Stage _previews  = new("Previews");
    private readonly Stage _pages     = new("Pages");
    private readonly Stage _files     = new("Files");

    private string _message = string.Empty;

    /// <summary>Sets the detail line. Called by the existing pipeline progress reports.</summary>
    public void Report(string value)
    {
        _message = value;
        Push();
    }

    public void SetArtifactTotal(int total) { _artifacts.SetTotal(total); Push(); }
    public void SetPreviewTotal(int total)  { _previews.SetTotal(total);  Push(); }
    public void SetPageTotal(int total)     { _pages.SetTotal(total);     Push(); }
    public void SetFileTotal(int total)     { _files.SetTotal(total);     Push(); }

    public void AddArtifactsDone(int count, Change change) { _artifacts.Add(count, change); Push(); }
    public void AddPreviewsDone(int count, Change change)  { _previews.Add(count, change);  Push(); }
    public void PreviewDone(Change change)                 { _previews.Add(1, change);      Push(); }
    public void PageDone(Change change)                    { _pages.Add(1, change);         Push(); }
    public void FileDone(Change change)                    { _files.Add(1, change);         Push(); }

    /// <summary>
    /// Records what happened to one artifact without moving its progress: the artifacts stage
    /// completes at scan time, but whether an artifact is new or updated is only known later, when
    /// its page is rendered and we see whether the site had one before and whether it now differs.
    /// </summary>
    public void ArtifactChanged(Change change) { _artifacts.Note(change); Push(); }

    /// <summary>
    /// The counters as they stand right now, e.g. <c>Artifacts 340/340 (2 new, 5 updated)</c>
    /// sections. Whichever of new/updated is zero is left out rather than printed as "0".
    /// </summary>
    public string Counters
    {
        get
        {
            var parts = new List<string>(4);
            foreach (var stage in new[] { _artifacts, _previews, _pages, _files })
            {
                if (!stage.HasTotal) continue;
                var sb = new StringBuilder();
                sb.Append(stage.Label).Append(' ').Append(stage.Done).Append('/').Append(stage.Total);

                var changes = new List<string>(2);
                if (stage.New > 0)     changes.Add($"{stage.New} new");
                if (stage.Updated > 0) changes.Add($"{stage.Updated} updated");
                if (changes.Count > 0) sb.Append(" (").Append(string.Join(", ", changes)).Append(')');

                parts.Add(sb.ToString());
            }
            return string.Join(" · ", parts);
        }
    }

    /// <summary>
    /// The current state, read synchronously. Reports reach the UI through a
    /// <see cref="Progress{T}"/> post, so the last one can still be in flight when a generate
    /// finishes — the final line has to be taken from here, not from whatever arrived last.
    /// </summary>
    public GenerateProgress Snapshot() => new(_message, Counters);

    // Counts for tests and for the run summary.
    public (int Done, int Total, int New, int Updated) Artifacts =>
        (_artifacts.Done, _artifacts.Total, _artifacts.New, _artifacts.Updated);
    public (int Done, int Total, int New, int Updated) Previews =>
        (_previews.Done, _previews.Total, _previews.New, _previews.Updated);
    public (int Done, int Total, int New, int Updated) Pages =>
        (_pages.Done, _pages.Total, _pages.New, _pages.Updated);
    public (int Done, int Total, int New, int Updated) Files =>
        (_files.Done, _files.Total, _files.New, _files.Updated);

    private void Push() => sink?.Report(Snapshot());
}
