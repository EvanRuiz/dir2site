// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Diagnostics;
using System.Threading;

namespace dir2site.SftpSync.Core.Credentials;

/// <summary>Small helper to run a CLI tool, optionally feeding a secret on stdin.</summary>
internal static class ProcessHelper
{
    public sealed record Result(int ExitCode, string StdOut, string StdErr);

    // AsyncLocal rather than a plain static, matching SourceListing and CredentialStoreFactory:
    // xunit runs test classes in parallel, and a plain static would reach a class that never asked
    // for it — silently answering its real subprocess calls with a stub, which here means exit 0
    // and empty output rather than an obvious failure.
    private static readonly AsyncLocal<Func<string, string[], string?, Result>?> _intercept = new();

    /// <summary>
    /// Hands invocations to <paramref name="intercept"/> instead of launching a process, until the
    /// returned scope is disposed, so a test can assert what would have reached the command line.
    /// </summary>
    /// <remarks>
    /// Keeping secrets out of argv is the whole point of the stdin plumbing in these stores, and
    /// argv is visible to every other process on the machine. Without a seam, a regression that
    /// moved a secret into ArgumentList would pass every existing test, round-trips included.
    ///
    /// Open the scope inside the test method rather than a constructor. It would in fact reach the
    /// test from there — xunit constructs the instance and invokes the method on one execution
    /// context — but that is xunit's business rather than a promise, and a scope whose extent you
    /// can see is worth more than one that works by arrangement. The real limit of an AsyncLocal is
    /// the other direction: a value set inside an awaited call is gone once that call returns.
    /// </remarks>
    internal static IDisposable UseForTesting(Func<string, string[], string?, Result> intercept) =>
        new Scope(intercept);

    public static Result Run(string fileName, string[] args, string? stdin = null)
    {
        if (_intercept.Value is { } intercept) return intercept(fileName, args, stdin);


        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardInput  = stdin != null,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start '{fileName}'.");

        if (stdin != null)
        {
            p.StandardInput.Write(stdin);
            p.StandardInput.Close();
        }

        var outStr = p.StandardOutput.ReadToEnd();
        var errStr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return new Result(p.ExitCode, outStr, errStr);
    }

    /// <summary>True if <paramref name="tool"/> resolves on PATH (via <c>which</c>).</summary>
    public static bool OnPath(string tool)
    {
        try
        {
            return Run("/usr/bin/which", [tool]).ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private sealed class Scope : IDisposable
    {
        private readonly Func<string, string[], string?, Result>? _previous;

        public Scope(Func<string, string[], string?, Result> intercept)
        {
            _previous = _intercept.Value;
            _intercept.Value = intercept;
        }

        public void Dispose() => _intercept.Value = _previous;
    }
}
