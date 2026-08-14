// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Platform;

namespace dir2site.Services;

/// <summary>How an install attempt ended, and what to tell the user about it.</summary>
public sealed record InstallResult(bool Succeeded, string Message, string? RevealPath = null);

/// <summary>
/// Installs the bundled VS Code extension that renders dir2site's <c>^^^</c> figure blocks in the
/// Markdown preview.
///
/// Three routes, in descending order of how good the outcome is:
/// <list type="number">
/// <item>the <c>code</c> CLI, which performs a real install — the extension appears in the
/// Extensions list and uninstalls normally;</item>
/// <item>copying the unpacked extension into the user's extensions folder, which needs no tooling
/// but does need VS Code restarted;</item>
/// <item>revealing the .vsix so it can be installed by hand.</item>
/// </list>
/// The button must never simply do nothing, which is why there is a third.
/// </summary>
public static class VsCodeExtensionInstaller
{
    // internal so BundledVsCodeExtensionTests can check these against the packaged manifest rather
    // than against a literal that has to be remembered separately at every version bump.
    internal const string PublisherAndName = "dir2site.dir2site-figures";
    internal const string Version = "0.1.2";
    private const string BundledVsix = "avares://dir2site/Assets/editors/dir2site-figures.vsix";

    /// <summary>A CLI that never returns shouldn't leave the button disabled for the session.</summary>
    private const int CliTimeoutMs = 60_000;

    public static Task<InstallResult> InstallAsync() => Task.Run(Install);

    private static InstallResult Install()
    {
        string vsix;
        try
        {
            vsix = ExtractBundledVsix();
        }
        catch (Exception ex)
        {
            return new InstallResult(false, $"Could not unpack the bundled extension: {ex.Message}");
        }

        if (TryCli(vsix, out var cliError))
        {
            return new InstallResult(true,
                "Installed. Reload VS Code if it's open, then the preview will render ^^^ figures.");
        }

        string? copyError = null;
        var extensionsDir = ExtensionsDirectory();
        if (extensionsDir != null && TryCopyUnpacked(vsix, extensionsDir, out copyError))
        {
            return new InstallResult(true,
                "Installed to your VS Code extensions folder. Restart VS Code to finish.");
        }

        // Neither route worked; hand the file over rather than leaving the user with nothing.
        //
        // The copy error comes first when there is one: reaching that route at all means we found an
        // extensions folder, so whatever went wrong there is the specific thing the user can act on.
        // The CLI error is nearly always "code isn't on PATH", which is the ordinary state of a
        // machine that never installed the shell command — true, but not the reason install failed.
        var kept = KeepForUser(vsix);
        return new InstallResult(false,
            $"Couldn't install automatically ({copyError ?? cliError ?? "no VS Code found"}). " +
            "The extension file has been saved — in VS Code use Extensions ▸ ⋯ ▸ Install from VSIX.",
            kept);
    }

    /// <summary>Copies the packaged extension out of the app's resources onto disk.</summary>
    private static string ExtractBundledVsix()
    {
        // A private directory rather than a fixed name under /tmp: the path is handed to the code
        // CLI after it is written, and on a shared Unix box a predictable name in a world-writable
        // directory is something another account can get in front of. CreateTempSubdirectory picks
        // an unguessable name and, on Unix, creates it 0700.
        var dir = Directory.CreateTempSubdirectory("dir2site-vscode-").FullName;
        var path = Path.Combine(dir, "dir2site-figures.vsix");

        using var source = AssetLoader.Open(new Uri(BundledVsix));
        using var target = File.Create(path);
        source.CopyTo(target);
        return path;
    }

    /// <summary>What one attempt at the CLI did, which is not the same question as whether it worked.</summary>
    /// <param name="Installed">The CLI ran and reported success.</param>
    /// <param name="Started">The CLI existed and ran at all — a failure here is worth reporting; a
    /// command that was simply absent is not.</param>
    private sealed record CliAttempt(bool Installed, bool Started, string? Error);

    /// <summary>
    /// Runs <c>code --install-extension</c>, trying each place the CLI is known to live until one
    /// works.
    /// </summary>
    private static bool TryCli(string vsixPath, out string? error)
    {
        string? ranAndFailed = null, neverStarted = null;

        foreach (var command in CliCandidates())
        {
            // An absolute candidate that isn't there is a guess that didn't pay off, not an error.
            if (Path.IsPathRooted(command) && !File.Exists(command)) continue;

            var attempt = RunCli(command, vsixPath);
            if (attempt.Installed) { error = null; return true; }

            if (attempt.Started) ranAndFailed ??= attempt.Error;
            else neverStarted ??= attempt.Error;
        }

        // What a real CLI said about a real failure beats "no such file" from a command that was
        // never going to exist.
        error = ranAndFailed ?? neverStarted ?? "no code CLI found";
        return false;
    }

    /// <summary>
    /// Where to look for the <c>code</c> CLI, best first.
    ///
    /// PATH first, because a user who set the CLI up deliberately should get what they configured.
    /// After that it is absolute paths, which exist for macOS: an app launched from Finder inherits
    /// launchd's PATH — <c>/usr/bin:/bin:/usr/sbin:/sbin</c> — and not the user's shell PATH, so
    /// <c>code</c> in /usr/local/bin never resolves however well the CLI is installed. Without these
    /// every macOS user silently drops to the folder-copy route and is told to restart VS Code.
    /// The in-bundle path works even for someone who never ran "Shell Command: Install 'code'".
    /// </summary>
    private static IEnumerable<string> CliCandidates()
    {
        yield return "code";

        // On Windows the shim goes through cmd.exe, which does its own PATH lookup; there is no
        // second place to look.
        if (OperatingSystem.IsWindows()) yield break;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (OperatingSystem.IsMacOS())
        {
            const string InBundle = "Contents/Resources/app/bin/code";
            yield return "/usr/local/bin/code";
            yield return "/opt/homebrew/bin/code";
            yield return $"/Applications/Visual Studio Code.app/{InBundle}";
            if (!string.IsNullOrEmpty(home))
                yield return Path.Combine(home, "Applications", "Visual Studio Code.app", InBundle);
        }
        else
        {
            yield return "/usr/bin/code";
            yield return "/snap/bin/code";
        }
    }

    /// <summary>
    /// One attempt. On Windows <c>code</c> is a .cmd shim, so it cannot be started directly — it has
    /// to go through the shell, which is the detail that otherwise makes a perfectly good CLI look
    /// absent.
    /// </summary>
    private static CliAttempt RunCli(string command, string vsixPath)
    {
        try
        {
            var psi = OperatingSystem.IsWindows()
                ? new ProcessStartInfo("cmd.exe")
                {
                    ArgumentList = { "/c", command, "--install-extension", vsixPath, "--force" },
                    CreateNoWindow = true,
                }
                : new ProcessStartInfo(command)
                {
                    ArgumentList = { "--install-extension", vsixPath, "--force" },
                };

            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.UseShellExecute = false;

            using var p = Process.Start(psi);
            if (p == null) return new CliAttempt(false, false, "could not start the code CLI");

            // Both pipes have to be draining before we wait. Reading one to the end first blocks
            // until that stream closes, so a CLI that hangs — or simply fills the other pipe's
            // buffer — never reaches the timeout below, which is the failure the timeout is for.
            // stdout is drained rather than read: nothing here needs it, but an undrained pipe is
            // exactly what deadlocks.
            _ = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();

            if (!p.WaitForExit(CliTimeoutMs))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return new CliAttempt(false, true, "the code CLI timed out");
            }

            // The overload taking a timeout returns on process exit; the argument-less one also
            // waits for the redirected streams to finish, which is what makes the reads safe to
            // take the result of.
            p.WaitForExit();

            if (p.ExitCode == 0) return new CliAttempt(true, true, null);

            var message = stderr.IsCompletedSuccessfully ? stderr.Result.Trim() : string.Empty;
            return new CliAttempt(false, true,
                string.IsNullOrWhiteSpace(message) ? $"code exited {p.ExitCode}" : message);
        }
        catch (Exception ex)
        {
            // Most often: code isn't on PATH. That's expected, not exceptional.
            return new CliAttempt(false, false, ex.Message);
        }
    }

    /// <summary>
    /// Unpacks the vsix's <c>extension/</c> folder into the extensions directory, which is how
    /// side-loading worked long before the CLI existed.
    /// </summary>
    private static bool TryCopyUnpacked(string vsixPath, string extensionsDir, out string? error)
    {
        error = null;
        try
        {
            var target = Path.Combine(extensionsDir, $"{PublisherAndName}-{Version}");
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            Directory.CreateDirectory(target);

            using var zip = ZipFile.OpenRead(vsixPath);
            foreach (var entry in zip.Entries)
            {
                if (!entry.FullName.StartsWith("extension/", StringComparison.Ordinal)) continue;
                if (entry.Length == 0 && entry.FullName.EndsWith('/')) continue;

                var rel = entry.FullName["extension/".Length..];
                var dest = Path.Combine(target, rel.Replace('/', Path.DirectorySeparatorChar));

                // Never let an archive path escape the directory it is being unpacked into.
                var full = Path.GetFullPath(dest);
                if (!full.StartsWith(Path.GetFullPath(target) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    continue;

                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                entry.ExtractToFile(full, overwrite: true);
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>The first VS Code-family extensions folder that exists, or null.</summary>
    private static string? ExtensionsDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home)) return null;

        // VS Code proper first; the variants are here so a VSCodium or Cursor user isn't told
        // nothing could be found when something obviously could.
        //
        // The Flatpak builds are last and are the reason this list isn't just ~/.vscode: a Flatpak
        // VS Code puts its extensions under ~/.var/app and ships no `code` on PATH, so without an
        // entry here both install routes miss and the user is left doing it by hand.
        string[] candidates =
        [
            Path.Combine(home, ".vscode", "extensions"),
            Path.Combine(home, ".vscode-insiders", "extensions"),
            Path.Combine(home, ".vscode-oss", "extensions"),
            Path.Combine(home, ".cursor", "extensions"),
            Path.Combine(home, ".var", "app", "com.visualstudio.code", "data", "vscode", "extensions"),
            Path.Combine(home, ".var", "app", "com.vscodium.codium", "data", "codium", "extensions"),
        ];

        return candidates.FirstOrDefault(Directory.Exists);
    }

    /// <summary>
    /// Puts the vsix somewhere durable so the reveal isn't pointing into a temp sweep.
    ///
    /// Documents first because that is where someone will think to look for it. It is also
    /// TCC-protected on macOS, so the copy can be refused outright — hence the second destination,
    /// which is app data and needs no consent. Returning the temp path is the last resort it always
    /// was, and now genuinely a last resort.
    /// </summary>
    private static string KeepForUser(string vsixPath)
    {
        foreach (var folder in new[] { Environment.SpecialFolder.MyDocuments,
                                       Environment.SpecialFolder.ApplicationData })
        {
            try
            {
                var dir = Environment.GetFolderPath(folder);
                if (string.IsNullOrEmpty(dir)) continue;

                if (folder == Environment.SpecialFolder.ApplicationData)
                {
                    dir = Path.Combine(dir, "dir2site");
                    Directory.CreateDirectory(dir);
                }

                var dest = Path.Combine(dir, "dir2site-figures.vsix");
                File.Copy(vsixPath, dest, overwrite: true);
                return dest;
            }
            catch
            {
                // Try the next one; a file the user can't reach is the only real failure here.
            }
        }

        return vsixPath;
    }
}
