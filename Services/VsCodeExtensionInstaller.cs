// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Platform;

namespace dir2site.Services;

/// <summary>How an install attempt ended, and what to tell the user about it.</summary>
public sealed record InstallResult(bool Succeeded, string Message, string? RevealPath = null);

/// <summary>What the machine already has, which decides whether to offer the extension at all.</summary>
/// <param name="VsCodeFound">An extensions folder exists — there is something to install into.</param>
/// <param name="Installed">The highest version of our extension found there, or null.</param>
public sealed record ExtensionState(bool VsCodeFound, Version? Installed);

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
    internal const string Version = "0.1.3";
    private const string BundledVsix = "avares://dir2site/Assets/editors/dir2site-figures.vsix";

    /// <summary>A CLI that never returns shouldn't leave the button disabled for the session.</summary>
    private const int CliTimeoutMs = 60_000;

    /// <summary>
    /// Shorter than an install's: this one runs unbidden at startup, and nobody is waiting on the
    /// answer. A CLI that hasn't listed its extensions in this long isn't going to.
    /// </summary>
    private const int QueryTimeoutMs = 15_000;

    public static Task<InstallResult> InstallAsync() => Task.Run(Install);

    /// <summary>The version this build carries, for comparing against what is already installed.</summary>
    internal static System.Version BundledVersion { get; } = System.Version.Parse(Version);

    /// <summary>Works out whether the extension is already there, and how old.</summary>
    public static Task<ExtensionState> DetectAsync() =>
        Task.Run(() => Detect(ExtensionsDirectories(), QueryCli));

    /// <summary>What the code CLI said when asked what is installed.</summary>
    /// <param name="Ran">A CLI was found and started — which itself proves there is a VS Code.</param>
    /// <param name="Answered">It also succeeded, so <paramref name="Installed"/> is the whole truth.
    /// A CLI that ran and errored knows, but didn't say.</param>
    internal sealed record CliExtensions(bool Ran, bool Answered, System.Version? Installed);

    /// <summary>
    /// Finds the installed version by asking whichever route would install it — the same order
    /// <see cref="Install"/> tries, for the same reason.
    ///
    /// The CLI goes first because when there is a CLI, the CLI is what installs, and it installs
    /// wherever the <c>code</c> on PATH points. For a portable VS Code that is a data folder beside
    /// the executable, which no amount of looking under the home directory will find. Reading the
    /// folders first would be cheaper, but on a machine with two VS Codes it answers about the one
    /// we are not installing into: a stale <c>~/.vscode</c> beside a portable install produces an
    /// update banner that installs successfully and then comes straight back, for ever.
    ///
    /// With no CLI the folder copy is what installs, into a folder we choose ourselves, so the
    /// folders are then the authority and cannot disagree with anything.
    ///
    /// One seam is left: a CLI that ran and errored hands back to the folders, so the stale-folder-
    /// beside-a-portable-install case can still raise an update banner that never clears. Narrow,
    /// and the alternative — treating a broken CLI as proof of nothing installed — hides an update
    /// from everyone whose CLI hiccuped once.
    /// </summary>
    /// <param name="extensionRoots">Extensions directories to look in; the real ones in normal use.</param>
    /// <param name="queryCli">Asks the code CLI what it has; the real one in normal use.</param>
    internal static ExtensionState Detect(IReadOnlyList<string> extensionRoots, Func<CliExtensions> queryCli)
    {
        var cli = queryCli();
        if (cli is { Ran: true, Answered: true }) return new ExtensionState(true, cli.Installed);

        System.Version? best = null;

        foreach (var root in extensionRoots)
        {
            try
            {
                var obsolete = ObsoleteFolders(root);

                // Filtered here rather than by a search pattern: EnumerateDirectories matches the
                // way the platform's filesystem does, so a pattern would be case-insensitive on
                // Windows and case-sensitive on Linux. VS Code lowercases these folder names and
                // the copy route writes whatever the constant says, and the same answer is wanted
                // on every platform.
                foreach (var dir in Directory.EnumerateDirectories(root))
                {
                    var name = Path.GetFileName(dir);
                    if (!name.StartsWith($"{PublisherAndName}-", StringComparison.OrdinalIgnoreCase)) continue;

                    // A folder VS Code has already marked for removal is not an install.
                    if (obsolete.Contains(name)) continue;

                    var found = ManifestVersion(dir)
                                ?? ParseVersion(name[(PublisherAndName.Length + 1)..]);

                    if (found != null && (best == null || found > best)) best = found;
                }
            }
            catch
            {
                // An unreadable root has nothing to tell us; the others still might.
            }
        }

        // A CLI that ran and failed still proves there is a VS Code here, whatever the folders say.
        return new ExtensionState(cli.Ran || extensionRoots.Any(Directory.Exists), best);
    }

    /// <summary>
    /// Asks the first working <c>code</c> CLI what it has installed.
    ///
    /// <c>--list-extensions --show-versions</c> prints one <c>publisher.name@version</c> per line.
    /// </summary>
    private static CliExtensions QueryCli()
    {
        foreach (var command in CliCandidates())
        {
            if (Path.IsPathRooted(command) && !File.Exists(command)) continue;

            var (started, exitCode, output, _) =
                RunCli(command, ["--list-extensions", "--show-versions"], QueryTimeoutMs);

            // A CLI that ran and failed has still told us there is a VS Code here; it just can't say
            // what is in it, so the folders get their turn.
            if (!started) continue;
            if (exitCode != 0) return new CliExtensions(true, false, null);

            System.Version? best = null;
            foreach (var line in output.Split('\n'))
            {
                var entry = line.Trim();
                if (!entry.StartsWith($"{PublisherAndName}@", StringComparison.OrdinalIgnoreCase)) continue;

                var found = ParseVersion(entry[(PublisherAndName.Length + 1)..]);
                if (found != null && (best == null || found > best)) best = found;
            }

            return new CliExtensions(true, true, best);
        }

        return new CliExtensions(false, false, null);
    }

    /// <summary>
    /// The folder names VS Code has flagged for deletion, from <c>.obsolete</c> in the extensions
    /// directory. Without this, an extension the user removed goes on looking installed until VS
    /// Code next gets around to sweeping the folder away.
    /// </summary>
    private static HashSet<string> ObsoleteFolders(string root)
    {
        var marked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, ".obsolete")));
            foreach (var entry in doc.RootElement.EnumerateObject())
                if (entry.Value.ValueKind == JsonValueKind.True) marked.Add(entry.Name);
        }
        catch
        {
            // No file, or one we can't read: nothing is known to be obsolete.
        }

        return marked;
    }

    /// <summary>The version inside an installed extension's manifest, which outranks its folder name.</summary>
    private static System.Version? ManifestVersion(string extensionDir)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(extensionDir, "package.json")));
            return doc.RootElement.TryGetProperty("version", out var version)
                ? ParseVersion(version.GetString())
                : null;
        }
        catch
        {
            // Missing or malformed; the caller falls back to the folder name.
            return null;
        }
    }

    /// <summary>Anything that isn't a plain numeric version is treated as no answer at all.</summary>
    private static System.Version? ParseVersion(string? text) =>
        System.Version.TryParse(text, out var parsed) ? parsed : null;

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

            var attempt = RunInstall(command, vsixPath);
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
    /// After that it is absolute paths, which exist because PATH is not where the CLI reliably is.
    /// On macOS an app launched from Finder inherits launchd's PATH — <c>/usr/bin:/bin:/usr/sbin:
    /// /sbin</c> — and not the user's shell PATH, so <c>code</c> in /usr/local/bin never resolves
    /// however well the CLI is installed. On Windows the shim is on PATH only if "Add to PATH" was
    /// left ticked at install time, and the installer's own directories are where it always is.
    ///
    /// Without these, those users silently drop to the folder-copy route and are told to restart VS
    /// Code — and, since detection asks the same CLI, get read off the extensions folders instead of
    /// off the install that would actually receive the extension.
    /// </summary>
    internal static IEnumerable<string> CliCandidates()
    {
        yield return "code";

        if (OperatingSystem.IsWindows())
        {
            // The shim itself, not the exe: code.cmd is what sets up the CLI's arguments, and it is
            // what "Add to PATH" puts on PATH. User install first — it is the installer's default,
            // and the one someone without admin rights will have.
            const string Shim = @"Microsoft VS Code\bin\code.cmd";
            foreach (var root in new[] { Environment.SpecialFolder.LocalApplicationData,
                                         Environment.SpecialFolder.ProgramFiles,
                                         Environment.SpecialFolder.ProgramFilesX86 })
            {
                var dir = Environment.GetFolderPath(root);
                if (string.IsNullOrEmpty(dir)) continue;

                // Only the user install nests under a Programs folder.
                yield return root == Environment.SpecialFolder.LocalApplicationData
                    ? Path.Combine(dir, "Programs", Shim)
                    : Path.Combine(dir, Shim);
            }

            yield break;
        }

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

    /// <summary>One install attempt, in the terms the install route reports in.</summary>
    private static CliAttempt RunInstall(string command, string vsixPath)
    {
        var (started, exitCode, _, error) =
            RunCli(command, ["--install-extension", vsixPath, "--force"], CliTimeoutMs);

        if (!started) return new CliAttempt(false, false, error);
        if (exitCode == 0) return new CliAttempt(true, true, null);

        return new CliAttempt(false, true,
            string.IsNullOrWhiteSpace(error) ? $"code exited {exitCode}" : error);
    }

    /// <summary>
    /// Builds what cmd.exe is given for a candidate that is a path, which is fussier than it looks
    /// and cannot be left to ArgumentList. The bare <c>code</c> on PATH does not come through here.
    ///
    /// Given <c>/c</c> and a line whose first character is a quote, cmd strips the first and last
    /// quote in the whole line before running it — so quoting a path containing spaces, which is
    /// what every default VS Code install has, takes the quotes back off again and the command
    /// becomes "C:\Program" with arguments. <c>/s</c> plus one extra pair of quotes around the
    /// entire line is the documented way out: cmd removes exactly those two characters and runs the
    /// rest as written.
    ///
    /// The escaping below is C-runtime style, which is not how cmd escapes a quote. It never comes
    /// up: the tokens are a Windows path, which cannot contain a quote, and literal flags. Anyone
    /// passing something quotable through here needs to revisit it.
    /// </summary>
    internal static string WindowsCommandLine(string command, string[] arguments)
    {
        var quoted = new[] { command }.Concat(arguments)
                                      .Select(token => $"\"{token.Replace("\"", "\\\"")}\"");

        return $"/s /c \"{string.Join(" ", quoted)}\"";
    }

    /// <summary>What running the CLI produced.</summary>
    /// <param name="Started">The CLI existed and ran at all — a failure here is worth reporting; a
    /// command that was simply absent is not.</param>
    /// <param name="ExitCode">Meaningful only when it started; -1 stands in when it did not.</param>
    private sealed record CliRun(bool Started, int ExitCode, string Output, string? Error);

    /// <summary>
    /// One attempt. On Windows <c>code</c> is a .cmd shim, so it cannot be started directly — it has
    /// to go through the shell, which is the detail that otherwise makes a perfectly good CLI look
    /// absent.
    /// </summary>
    private static CliRun RunCli(string command, string[] arguments, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo(OperatingSystem.IsWindows() ? "cmd.exe" : command);
            if (OperatingSystem.IsWindows())
            {
                psi.CreateNoWindow = true;

                // Only a path needs the careful form, and only a path can be broken by its absence.
                // The bare word has worked for every Windows user so far; leave it exactly as it was.
                if (Path.IsPathRooted(command))
                {
                    psi.Arguments = WindowsCommandLine(command, arguments);
                }
                else
                {
                    psi.ArgumentList.Add("/c");
                    psi.ArgumentList.Add(command);
                    foreach (var argument in arguments) psi.ArgumentList.Add(argument);
                }
            }
            else
            {
                foreach (var argument in arguments) psi.ArgumentList.Add(argument);
            }

            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.UseShellExecute = false;

            using var p = Process.Start(psi);
            if (p == null) return new CliRun(false, -1, string.Empty, "could not start the code CLI");

            // Both pipes have to be draining before we wait. Reading one to the end first blocks
            // until that stream closes, so a CLI that hangs — or simply fills the other pipe's
            // buffer — never reaches the timeout below, which is the failure the timeout is for.
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();

            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return new CliRun(true, -1, string.Empty, "the code CLI timed out");
            }

            // The overload taking a timeout returns on process exit; the argument-less one also
            // waits for the redirected streams to finish, which is what makes the reads safe to
            // take the result of.
            p.WaitForExit();

            return new CliRun(true, p.ExitCode,
                stdout.IsCompletedSuccessfully ? stdout.Result : string.Empty,
                stderr.IsCompletedSuccessfully ? stderr.Result.Trim() : string.Empty);
        }
        catch (Exception ex)
        {
            // Most often: code isn't on PATH. That's expected, not exceptional.
            return new CliRun(false, -1, string.Empty, ex.Message);
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
    private static string? ExtensionsDirectory() => ExtensionsDirectories().FirstOrDefault();

    /// <summary>
    /// Every VS Code-family extensions folder that exists, best first.
    ///
    /// Installing only ever uses the first — one copy is enough. Detection wants them all, but only
    /// on the machines it reads folders on at all: with no CLI to ask, someone running both VS Code
    /// and Cursor installed the extension into whichever they were using, and finding it in the
    /// second is still finding it. When the CLI answers, none of this is consulted and an install
    /// belonging to some other editor correctly reads as absent.
    /// </summary>
    private static IReadOnlyList<string> ExtensionsDirectories()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home)) return [];

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

        return candidates.Where(Directory.Exists).ToList();
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
