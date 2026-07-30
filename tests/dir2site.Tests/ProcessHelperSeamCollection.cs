// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using Xunit;

namespace dir2site.Tests;

/// <summary>
/// ProcessHelper.RunOverride is static, so anything that sets it must not run alongside anything
/// that shells out for real — the platform credential-store tests would either be intercepted or
/// would clear the override mid-test. xUnit runs classes in parallel unless they share a
/// collection, so these share one.
/// </summary>
[CollectionDefinition("ProcessHelperSeam", DisableParallelization = true)]
public class ProcessHelperSeamCollection;
