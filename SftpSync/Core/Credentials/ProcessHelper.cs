// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Diagnostics;

namespace dir2site.SftpSync.Core.Credentials;

/// <summary>Small helper to run a CLI tool, optionally feeding a secret on stdin.</summary>
internal static class ProcessHelper
{
    public sealed record Result(int ExitCode, string StdOut, string StdErr);

    public static Result Run(string fileName, string[] args, string? stdin = null)
    {
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
}
