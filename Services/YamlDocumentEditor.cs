// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Linq;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;

namespace dir2site.Services;

/// <summary>
/// Edits a YAML document in place by splicing new values into the original text, so everything
/// the edit doesn't touch — comments, blank lines, key order, quoting style, and keys this app
/// knows nothing about — survives byte for byte.
///
/// Serializing a model over the top of a file cannot do this: a model only carries the values it
/// declares, so every rewrite discards the rest. That matters because dir2site's YAML is meant to
/// be hand-edited, and a user's own comments are the first casualty.
///
/// Edits are located by <see cref="YamlDotNet.Core.Mark.Index"/>, an absolute character offset.
/// Line/column arithmetic would drift on tabs and surrogate pairs, and would not survive
/// multi-line block scalars.
/// </summary>
public sealed class YamlDocumentEditor
{
    // Emits a scalar the way YamlDotNet would, so quoting rules (colons, '#', leading spaces,
    // strings that would otherwise read as numbers or booleans) come from the library.
    private static readonly ISerializer ScalarSerializer = new SerializerBuilder().Build();

    private string _text;

    private YamlDocumentEditor(string text) => _text = text;

    /// <summary>The current document text.</summary>
    public string Text => _text;

    /// <summary>True once an edit has actually changed the text.</summary>
    public bool IsModified { get; private set; }

    /// <summary>
    /// Parses <paramref name="text"/>. Returns null when it isn't a YAML document with a mapping
    /// at the root, in which case the caller should fall back rather than guess.
    /// </summary>
    public static YamlDocumentEditor? TryLoad(string text)
    {
        // An empty or whitespace-only file is an empty mapping: there is nothing to preserve and
        // everything to add, so editing it is well defined.
        if (string.IsNullOrWhiteSpace(text))
            return new YamlDocumentEditor("");

        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(text));
            if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode)
                return null;
        }
        catch
        {
            return null;
        }

        return new YamlDocumentEditor(text);
    }

    /// <summary>
    /// Sets a top-level key, adding it if absent. Returns false when the document can't be edited
    /// surgically — an existing non-scalar value, or a splice that wouldn't re-parse — leaving the
    /// text untouched so the caller can fall back.
    /// </summary>
    public bool Set(string key, string value) => Set(key, value, emitted: null);

    /// <summary>
    /// Sets a boolean. Written bare so it stays a boolean — routing it through the string overload
    /// would quote it, since <c>"true"</c> as a string needs quotes to survive as one.
    /// </summary>
    public bool Set(string key, bool value) =>
        Set(key, value ? "true" : "false", emitted: value ? "true" : "false");

    /// <summary>Sets an integer, written bare so it stays a number.</summary>
    public bool Set(string key, int value)
    {
        var text = value.ToString(CultureInfo.InvariantCulture);
        return Set(key, text, emitted: text);
    }

    // `value` is the semantic value, compared against the parsed node to detect a no-op regardless
    // of how it happens to be quoted on disk. `emitted` is the literal text to splice in; null
    // means "let the emitter decide", which is what strings want.
    private bool Set(string key, string value, string? emitted)
    {
        var root = Root();
        if (root == null) return false;

        var keyNode = root.Children.Keys
            .OfType<YamlScalarNode>()
            .FirstOrDefault(k => k.Value == key);

        return keyNode == null
            ? AddKey(root, key, value, emitted)
            : ReplaceValue(root, keyNode, value, emitted);
    }

    /// <summary>Applies several top-level keys, stopping at the first that can't be applied.</summary>
    public bool SetAll(IEnumerable<KeyValuePair<string, string>> values) =>
        values.All(kv => Set(kv.Key, kv.Value));

    /// <summary>
    /// Replaces (or appends) a whole top-level block — a nested mapping or sequence — given its
    /// body as YAML at zero indentation.
    /// </summary>
    /// <remarks>
    /// Everything outside the block keeps its comments and formatting, which is the point.
    /// Comments <em>inside</em> the block do not survive, because the block is rewritten wholesale
    /// from app-owned data rather than edited value by value. That is the accepted trade for
    /// sections the app manages, like <c>deploy:</c>.
    /// </remarks>
    public bool SetBlock(string key, string blockBody)
    {
        var root = Root();
        if (root == null) return false;

        var indented = string.Join('\n',
            blockBody.TrimEnd('\r', '\n').Split('\n').Select(l => l.Length == 0 ? l : "  " + l));
        var replacement = key + ":\n" + indented;

        var keyNode = root.Children.Keys.OfType<YamlScalarNode>().FirstOrDefault(k => k.Value == key);
        if (keyNode == null)
        {
            var trimmed = _text.TrimEnd('\r', '\n');
            var separator = trimmed.Length == 0 ? "" : "\n";
            return TryCommit(trimmed + separator + replacement + "\n");
        }

        if (!TryEntryExtent(root, keyNode, out var start, out var end)) return false;
        return TryCommit(_text[..start] + replacement + "\n" + _text[end..]);
    }

    /// <summary>Removes a top-level key and its value. No-op when the key isn't there.</summary>
    public bool RemoveKey(string key)
    {
        var root = Root();
        if (root == null) return false;

        var keyNode = root.Children.Keys.OfType<YamlScalarNode>().FirstOrDefault(k => k.Value == key);
        if (keyNode == null) return true;

        if (!TryEntryExtent(root, keyNode, out var start, out var end)) return false;
        return TryCommit(_text[..start] + _text[end..]);
    }

    /// <summary>
    /// The character span of a whole top-level entry, key through value, ending just past its
    /// final newline.
    /// </summary>
    /// <remarks>
    /// A nested mapping or sequence cannot supply this itself: YamlDotNet reports its End mark at
    /// the <em>start</em> of its content, so `deploy:` spans only "deploy:\n  ". The reliable
    /// bound is the next top-level key — minus any comment or blank lines directly above it, which
    /// belong to that key and must not be swallowed.
    /// </remarks>
    private bool TryEntryExtent(YamlMappingNode root, YamlScalarNode keyNode, out int start, out int end)
    {
        var entryStart = (int)keyNode.Start.Index;
        start = entryStart;
        end = 0;
        if (entryStart < 0 || entryStart > _text.Length) return false;

        var next = root.Children.Keys
            .OfType<YamlScalarNode>()
            .Select(k => (int)k.Start.Index)
            .Where(i => i > entryStart)
            .DefaultIfEmpty(_text.Length)
            .Min();

        if (next > _text.Length) return false;

        if (next < _text.Length)
        {
            // Rewind over lines leading up to the next key that are blank or pure comment.
            var cursor = next;
            while (cursor > entryStart)
            {
                var lineStart = _text.LastIndexOf('\n', cursor - 2) + 1;
                if (lineStart <= entryStart) break;

                var line = _text[lineStart..cursor].Trim();
                if (line.Length != 0 && !line.StartsWith('#')) break;

                cursor = lineStart;
            }
            next = cursor;
        }

        end = next;
        return end >= entryStart;
    }

    // ---- internals ---------------------------------------------------------

    // Re-parsed before every edit: indices from a previous parse are stale the moment the text
    // changes. These documents are small, so this is cheaper than tracking offset deltas.
    private YamlMappingNode? Root()
    {
        if (_text.Length == 0) return new YamlMappingNode();

        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(_text));
            return stream.Documents[0].RootNode as YamlMappingNode;
        }
        catch
        {
            return null;
        }
    }

    private bool ReplaceValue(YamlMappingNode root, YamlScalarNode keyNode, string value, string? emitted)
    {
        if (root.Children[keyNode] is not YamlScalarNode valueNode)
            return false; // a mapping or sequence lives here; not ours to overwrite

        if (valueNode.Value == value)
            return true;  // no-op: never dirty a file to write what it already says

        var start = (int)valueNode.Start.Index;
        var end   = (int)valueNode.End.Index;
        if (start < 0 || end > _text.Length || end < start)
            return false;

        // A block scalar's span swallows its trailing newline; dropping it would weld the next
        // line onto this one.
        var replaced = _text[start..end];
        var suffix = replaced.EndsWith('\n') ? "\n" : "";

        var indent = (int)keyNode.Start.Column - 1;
        return TryCommit(_text[..start] + (emitted ?? Emit(value, indent)) + suffix + _text[end..]);
    }

    private bool AddKey(YamlMappingNode root, string key, string value, string? emitted)
    {
        // Append after the last entry so existing order — and anything trailing it — is left alone.
        var lastValue = root.Children.Values.LastOrDefault();
        var insertAt = lastValue == null ? _text.Length : (int)lastValue.End.Index;
        if (insertAt > _text.Length) return false;

        var indent = root.Children.Keys.OfType<YamlScalarNode>().FirstOrDefault() is { } firstKey
            ? (int)firstKey.Start.Column - 1
            : 0;

        var line = new string(' ', indent) + key + ": " + (emitted ?? Emit(value, indent));

        // Land on a line of our own without inventing blank lines the user didn't have.
        var needsLeadingNewline = insertAt > 0 && _text[insertAt - 1] != '\n';
        var prefix = needsLeadingNewline ? "\n" : "";
        var suffix = insertAt < _text.Length ? "\n" : "";

        return TryCommit(_text[..insertAt] + prefix + line + suffix + _text[insertAt..]);
    }

    /// <summary>Renders a value as YAML, as a literal block when it spans lines.</summary>
    private static string Emit(string value, int indent)
    {
        if (value.Contains('\n'))
        {
            var body = value.TrimEnd('\n');
            var pad = new string(' ', indent + 2);
            return "|\n" + string.Join('\n', body.Split('\n').Select(l => pad + l));
        }

        // Serialize() round-trips through the emitter, which decides the quoting.
        var emitted = ScalarSerializer.Serialize(value).TrimEnd('\r', '\n');

        // Serializing a bare scalar yields a whole document, and for some values — the empty
        // string among them — the emitter includes the explicit "--- " start marker.
        return emitted.StartsWith("--- ", StringComparison.Ordinal) ? emitted[4..] : emitted;
    }

    /// <summary>Accepts an edit only if the result still parses, so a bad splice can't reach disk.</summary>
    private bool TryCommit(string candidate)
    {
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(candidate));
            if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode)
                return false;
        }
        catch
        {
            return false;
        }

        _text = candidate;
        IsModified = true;
        return true;
    }
}
