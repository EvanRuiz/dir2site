// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;
using dir2site.SftpSync.Core.Credentials;
using Xunit;

namespace dir2site.Tests;

/// <summary>
/// Command-line arguments are readable by any other process on the machine, so a password passed
/// as an argument is a password disclosed. The stores feed secrets on stdin instead — this is the
/// guard that keeps it that way.
/// </summary>
[Collection("ProcessHelperSeam")]
public class SecretsNotInArgvTests : IDisposable
{
    private readonly List<(string File, string[] Args, string? Stdin)> _calls = [];

    public SecretsNotInArgvTests()
    {
        ProcessHelper.RunOverride = (file, args, stdin) =>
        {
            _calls.Add((file, args, stdin));
            return new ProcessHelper.Result(0, "", "");
        };
    }

    public void Dispose() => ProcessHelper.RunOverride = null;

    private const string Secret = "hunter2-with spaces-and-$pecials";

    private void AssertSecretNeverInArgv()
    {
        Assert.NotEmpty(_calls);
        foreach (var (file, args, _) in _calls)
        {
            Assert.DoesNotContain(args, a => a.Contains(Secret, StringComparison.Ordinal));
            Assert.DoesNotContain(Secret, file, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void MacStore_PassesTheSecretOnStdin_NeverAsAnArgument()
    {
        new MacCredentialStore().Set("some-key", Secret);

        AssertSecretNeverInArgv();
        // It has to reach the tool somehow; stdin is the only acceptable route.
        Assert.Contains(_calls, c => c.Stdin?.Contains(Secret, StringComparison.Ordinal) == true);
    }

    [Fact]
    public void LinuxStore_PassesTheSecretOnStdin_NeverAsAnArgument()
    {
        new LinuxCredentialStore().Set("some-key", Secret);

        AssertSecretNeverInArgv();
        Assert.Contains(_calls, c => c.Stdin?.Contains(Secret, StringComparison.Ordinal) == true);
    }

    [Fact]
    public void ReadingBack_DoesNotPutTheKeyOrSecretOnTheCommandLineEither()
    {
        new MacCredentialStore().Get("some-key");
        new LinuxCredentialStore().Get("some-key");

        AssertSecretNeverInArgv();
    }
}
