// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace dir2site.Services;

/// <summary>
/// Reads a Windows internet shortcut (<c>.url</c>) and decides whether it points at a video we can
/// embed. This is the only place in the app that knows anything about a specific video provider —
/// adding a second one should mean adding a branch to <see cref="TryParseVideo"/> and nothing else.
/// </summary>
/// <remarks>
/// A shortcut whose target is not a supported video is not an error, it is simply not an artifact.
/// Every entry point here returns null rather than throwing, so an ordinary web bookmark sitting in
/// a photo folder is skipped the same way an unrecognized file extension is.
/// </remarks>
public static class InternetShortcutParser
{
    public const string YouTube = "youtube";

    /// <param name="Provider">Currently always <see cref="YouTube"/>.</param>
    /// <param name="VideoId">The provider's id, already validated for shape.</param>
    /// <param name="Start">Playback offset in seconds, or null when the URL didn't ask for one.</param>
    public sealed record VideoRef(string Provider, string VideoId, int? Start);

    public sealed record ShortcutVideo(string Url, VideoRef Video);

    // YouTube ids are exactly 11 characters of base64url. Validating the shape means a mangled URL
    // fails here, where it can be skipped, rather than producing a card with an embed that 404s.
    private static readonly Regex VideoIdPattern =
        new(@"^[A-Za-z0-9_-]{11}$", RegexOptions.Compiled);

    // "1h2m3s", "90s", "2m" — YouTube's own share links use this form for the t= parameter.
    private static readonly Regex ClockOffsetPattern =
        new(@"^(?:(\d+)h)?(?:(\d+)m)?(?:(\d+)s)?$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Reads <paramref name="urlFilePath"/> and returns its target URL together with the video it
    /// references, or null if the file is unreadable, has no URL, or points somewhere we can't embed.
    /// </summary>
    public static ShortcutVideo? TryReadVideo(string urlFilePath)
    {
        var url = TryReadUrl(urlFilePath);
        if (url is null) return null;

        var video = TryParseVideo(url);
        return video is null ? null : new ShortcutVideo(url, video);
    }

    /// <summary>
    /// Returns the <c>URL=</c> value from the <c>[InternetShortcut]</c> section, or null.
    /// </summary>
    /// <remarks>
    /// These files are written by browsers, file managers and by hand, so the parse is deliberately
    /// lax: any casing, leading whitespace, CRLF or LF, a BOM, and unrelated keys (<c>IconFile</c>,
    /// <c>IDList</c>, <c>HotKey</c>) all pass through. The section header is honoured when present —
    /// a URL under some other section is not ours — but a file that omits headers entirely still
    /// works, because plenty of hand-written ones do.
    /// </remarks>
    public static string? TryReadUrl(string urlFilePath)
    {
        string[] lines;
        try { lines = File.ReadAllLines(urlFilePath); }
        catch { return null; }

        var inShortcutSection = true; // until a header says otherwise

        foreach (var raw in lines)
        {
            // ReadAllLines strips a BOM when it detects the encoding, but not when the file was
            // written as Latin-1-with-a-BOM, so drop a stray one explicitly.
            var line = raw.Trim().TrimStart('﻿').Trim();
            if (line.Length == 0 || line[0] == ';') continue;

            if (line[0] == '[' && line[^1] == ']')
            {
                inShortcutSection = line.Equals("[InternetShortcut]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inShortcutSection) continue;

            var eq = line.IndexOf('=');
            if (eq <= 0) continue;

            if (!line[..eq].Trim().Equals("URL", StringComparison.OrdinalIgnoreCase)) continue;

            var value = line[(eq + 1)..].Trim();
            return value.Length == 0 ? null : value;
        }

        return null;
    }

    /// <summary>
    /// Recognizes a supported video URL and extracts its id and start offset, or returns null.
    /// </summary>
    /// <remarks>
    /// Accepts every form YouTube's own share buttons emit — <c>watch?v=</c>, <c>youtu.be/</c>,
    /// <c>/embed/</c>, <c>/shorts/</c> and <c>/live/</c> — with or without <c>www.</c>/<c>m.</c>.
    /// Extra query parameters are ignored rather than rejected, which matters because a link copied
    /// out of a playlist carries <c>&amp;list=</c> and <c>&amp;index=</c> along with the video id.
    /// </remarks>
    public static VideoRef? TryParseVideo(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;

        var host = uri.Host.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal)) host = host[4..];
        if (host.StartsWith("m.",   StringComparison.Ordinal)) host = host[2..];

        var query    = ParseQuery(uri.Query);
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        string? id = null;

        if (host is "youtube.com" or "youtube-nocookie.com")
        {
            if (segments.Length == 1 && segments[0].Equals("watch", StringComparison.OrdinalIgnoreCase))
                query.TryGetValue("v", out id);
            else if (segments.Length == 2 && segments[0] is "embed" or "shorts" or "live")
                id = segments[1];
        }
        else if (host == "youtu.be" && segments.Length == 1)
        {
            id = segments[0];
        }

        if (id is null || !VideoIdPattern.IsMatch(id)) return null;

        query.TryGetValue("t", out var offset);
        if (string.IsNullOrEmpty(offset)) query.TryGetValue("start", out offset);

        return new VideoRef(YouTube, id, ParseStartSeconds(offset));
    }

    /// <summary>
    /// Converts a YouTube time offset to whole seconds. Accepts a bare count ("90"), a seconds
    /// suffix ("90s") and the clock form ("1m30s"). Returns null for anything else, and for zero —
    /// starting at the beginning is the same as not asking for a start at all.
    /// </summary>
    public static int? ParseStartSeconds(string? offset)
    {
        if (string.IsNullOrWhiteSpace(offset)) return null;
        offset = offset.Trim();

        if (int.TryParse(offset, out var plain))
            return plain > 0 ? plain : null;

        var m = ClockOffsetPattern.Match(offset);
        if (!m.Success || !(m.Groups[1].Success || m.Groups[2].Success || m.Groups[3].Success))
            return null;

        var total = Part(m, 1) * 3600 + Part(m, 2) * 60 + Part(m, 3);
        return total > 0 ? total : null;

        static int Part(Match m, int group) =>
            m.Groups[group].Success && int.TryParse(m.Groups[group].Value, out var v) ? v : 0;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(query)) return result;

        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0) continue;

            var key = Uri.UnescapeDataString(pair[..eq]);
            if (!result.ContainsKey(key))
                result[key] = Uri.UnescapeDataString(pair[(eq + 1)..]);
        }

        return result;
    }
}
