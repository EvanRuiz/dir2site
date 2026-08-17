// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using Xunit;

// Avalonia relies on global state recreated around every test, so its headless UI tests cannot run
// in parallel: https://github.com/AvaloniaUI/Avalonia/discussions/18289
//
// If the suite ever needs to be faster, the lever is AvaloniaTestIsolation(PerAssembly) — one
// Application for the assembly rather than one per test — not this.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
