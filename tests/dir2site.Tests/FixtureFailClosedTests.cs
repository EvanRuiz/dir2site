// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using Xunit;

namespace dir2site.Tests;

/// <summary>
/// A fixture that cannot start used to make every integration test skip, so the suite went green
/// while covering nothing — the exact failure the require-flag exists to prevent. Guarding only the
/// rclone download left the larger hole open: a port-wait timeout on a loaded runner, or a missing
/// ssh-keygen, both skipped silently.
/// </summary>
public class FixtureFailClosedTests
{
    [Fact]
    public void WithoutTheRequireFlag_AStartupFailureIsJustASkipReason()
    {
        var previous = Environment.GetEnvironmentVariable(RcloneTool.RequireEnvVar);
        Environment.SetEnvironmentVariable(RcloneTool.RequireEnvVar, null);
        try
        {
            Assert.Equal("no sshd here", SftpServerFixture.ReasonOrThrow("no sshd here"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(RcloneTool.RequireEnvVar, previous);
        }
    }

    [Fact]
    public void WithTheRequireFlag_AStartupFailureIsAHardFailure()
    {
        var previous = Environment.GetEnvironmentVariable(RcloneTool.RequireEnvVar);
        Environment.SetEnvironmentVariable(RcloneTool.RequireEnvVar, "1");
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(
                () => SftpServerFixture.ReasonOrThrow("rclone did not accept connections in time."));
            Assert.Contains("must run", ex.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable(RcloneTool.RequireEnvVar, previous);
        }
    }
}
