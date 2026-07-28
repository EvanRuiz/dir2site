// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace dir2site.Tests;

/// <summary>
/// Resolves the <c>rclone</c> binary the SFTP integration tests run against, downloading it once
/// into a gitignored cache and reusing it thereafter.
///
/// The pin is the <see cref="ArchiveSha256"/> table, and it lives here in the repo on purpose:
/// verifying a download against a checksum fetched from the same host proves nothing, because
/// anything able to alter the artifact can alter the checksum beside it. Hashes committed here
/// mean the version cannot change without a reviewed diff, and a mismatch is a hard failure
/// rather than a silently different binary.
/// </summary>
public static class RcloneTool
{
    public const string Version = "v1.74.4";

    /// <summary>Set in CI so an unreachable download fails the run instead of skipping the tests.</summary>
    public const string RequireEnvVar = "DIR2SITE_REQUIRE_SFTP_TESTS";

    private const string BaseUrl = "https://github.com/rclone/rclone/releases/download/" + Version + "/";

    // SHA256 of the official release archives, as published at
    // https://downloads.rclone.org/v1.74.4/SHA256SUMS — confirmed byte-identical to the copy
    // GitHub serves as a release asset.
    private static readonly (string Platform, string Sha256)[] ArchiveSha256 =
    [
        ("windows-amd64", "ef097ef9de37a57feb7d9f9c7afb34148ad3c65be8025f1d8f7f521554a701ea"),
        ("osx-arm64",     "c2100e2d4a4b3be04c55cd45380cafe7647e1ad772bb055f52f00876ed701167"),
        ("osx-amd64",     "4188aa84043d7a6240912923f47639a9d2da21f3b40a521c065c8d92e66563f6"),
        ("linux-amd64",   "fe435e0c36228e7c2f116a8701f01127bb1f694005fc11d1f27186c8bca4115d"),
    ];

    // SHA256 of the extracted executables, so a cached copy is trusted on its own merits rather
    // than because it happens to sit at the expected path.
    private static readonly (string Platform, string Sha256)[] BinarySha256 =
    [
        ("windows-amd64", "492648a3867dbc620188a305e05ff3216aecbf4622bf1a6b5b978ed9c939e18c"),
        ("osx-arm64",     "79dde6096c8d92c31495faac36fc764e3b3d557ee8569ce16c9fb07ce808024e"),
        ("osx-amd64",     "465f240599c276f4542e673a96d052b212da49afb834223d5319f4137a25e585"),
        ("linux-amd64",   "9f56ca5edfac24a3ed37226c2ba1de69f1ec9e05fa2526cddee5cd97e202be6b"),
    ];

    /// <summary>
    /// Returns the path to a verified rclone, or null with a reason when this platform has no pin
    /// or the download could not be reached. A hash mismatch never returns null — it throws.
    /// </summary>
    public static string? Resolve(out string reason)
    {
        var platform = PlatformKey();
        if (platform == null)
        {
            reason = $"No rclone pinned for {RuntimeInformation.OSDescription} " +
                     $"{RuntimeInformation.OSArchitecture} — integration tests skipped.";
            return null;
        }

        var exe = OperatingSystem.IsWindows() ? "rclone.exe" : "rclone";
        var cached = Path.Combine(CacheRoot(), Version, platform, exe);
        var expectedBinary = BinarySha256.First(p => p.Platform == platform).Sha256;

        if (File.Exists(cached) && FileSha256(cached) == expectedBinary)
        {
            MakeExecutable(cached);
            reason = "";
            return cached;
        }

        try
        {
            Download(platform, exe, cached, expectedBinary);
        }
        catch (HttpRequestException ex)
        {
            // Offline developer: skip rather than block. CI sets RequireEnvVar so the same
            // situation there is a failure, because a silent skip would look like a pass.
            if (Environment.GetEnvironmentVariable(RequireEnvVar) == "1")
                throw new InvalidOperationException(
                    $"Could not download rclone {Version} for {platform} and {RequireEnvVar}=1: {ex.Message}", ex);

            reason = $"Could not download rclone {Version} for {platform} ({ex.Message}) — integration tests skipped.";
            return null;
        }

        MakeExecutable(cached);
        reason = "";
        return cached;
    }

    private static void Download(string platform, string exe, string destination, string expectedBinary)
    {
        var archiveName = $"rclone-{Version}-{platform}.zip";
        var expectedArchive = ArchiveSha256.First(p => p.Platform == platform).Sha256;

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var scratch = destination + "." + Environment.ProcessId + ".tmp";
        var archive = scratch + ".zip";

        try
        {
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
            using (var response = http.GetAsync(BaseUrl + archiveName, HttpCompletionOption.ResponseHeadersRead)
                                      .GetAwaiter().GetResult())
            {
                response.EnsureSuccessStatusCode();
                using var file = File.Create(archive);
                response.Content.CopyToAsync(file).GetAwaiter().GetResult();
            }

            // Verify before extracting: never unpack bytes we haven't vouched for.
            var actual = FileSha256(archive);
            if (actual != expectedArchive)
                throw new InvalidOperationException(
                    $"""
                     rclone {Version} {platform} failed verification — refusing to use it.
                       expected {expectedArchive}
                       actual   {actual}
                     The pinned release asset has changed. Do not update the hash to match without
                     establishing why: this is exactly the case the pin exists to catch.
                     """);

            using (var zip = ZipFile.OpenRead(archive))
            {
                var entry = zip.Entries.FirstOrDefault(e => e.Name == exe)
                    ?? throw new InvalidOperationException($"No {exe} inside {archiveName}.");
                entry.ExtractToFile(scratch, overwrite: true);
            }

            var binaryHash = FileSha256(scratch);
            if (binaryHash != expectedBinary)
                throw new InvalidOperationException(
                    $"rclone {Version} {platform}: extracted binary hash {binaryHash} != pinned {expectedBinary}.");

            // Atomic publish, so a parallel test process never observes a half-written file.
            File.Move(scratch, destination, overwrite: true);
        }
        finally
        {
            TryDelete(archive);
            TryDelete(scratch);
        }
    }

    private static string? PlatformKey() =>
        OperatingSystem.IsWindows() && RuntimeInformation.OSArchitecture is Architecture.X64 or Architecture.Arm64 ? "windows-amd64" :
        OperatingSystem.IsMacOS() ? (RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-amd64") :
        OperatingSystem.IsLinux() && RuntimeInformation.OSArchitecture == Architecture.X64 ? "linux-amd64" :
        null;

    /// <summary>Repo-local so CI can cache one directory; gitignored so it never gets committed.</summary>
    private static string CacheRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "dir2site.sln")))
                return Path.Combine(dir.FullName, "tests", "tools", "rclone", ".cache");

        return Path.Combine(Path.GetTempPath(), "dir2site-rclone-cache");
    }

    private static string FileSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static void MakeExecutable(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        var mode = File.GetUnixFileMode(path);
        if ((mode & UnixFileMode.UserExecute) == 0)
            File.SetUnixFileMode(path, mode | UnixFileMode.UserExecute);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
