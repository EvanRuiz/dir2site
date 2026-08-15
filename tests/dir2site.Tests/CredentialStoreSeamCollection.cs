// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using Xunit;

namespace dir2site.Tests;

/// <summary>
/// CredentialStoreFactory.CreateOverride is static, so a test that installs a stub must not run
/// alongside one that expects the real platform store — it would silently intercept the other's
/// reads and writes. xUnit runs classes in parallel unless they share a collection.
/// </summary>
[CollectionDefinition("CredentialStoreSeam", DisableParallelization = true)]
public class CredentialStoreSeamCollection;
