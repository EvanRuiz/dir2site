// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
namespace dir2site.SftpSync.Core.Credentials;

/// <summary>
/// Reads a target's secret at the key <see cref="CredentialKeys"/> chooses, copying it forward from
/// the old project-scoped key the first time it is asked for.
/// </summary>
/// <remarks>
/// The migration is lazy and per-target: nothing reads a secret until its project is opened, so a
/// startup sweep would have nothing to sweep and no target to attribute a failure to.
///
/// Only the old keys were per-project, so only they can disagree: two projects deploying to one
/// account each kept their own copy, and those copies could hold different values. Consolidating
/// them means the first project opened copies its value across, and a project opened later finds
/// the account entry already populated and leaves its own legacy copy alone.
///
/// That needs no arbitration and nothing said about it. A server has one password for an account, so
/// the two copies were never rival answers — one is current and the other went stale when the
/// password was last changed. Picking the stale one costs a failed deploy, which is how a wrong
/// password has always announced itself, and retyping it then fixes every project at once. Going
/// forward there is one entry per account and nothing left to diverge.
/// </remarks>
public static class TargetSecret
{
    /// <summary>
    /// The secret for <paramref name="profile"/>, migrating it from the legacy key if that is where
    /// it still lives.
    /// </summary>
    public static CredentialResult Read(ICredentialStore store, string projectRoot, SftpProfile profile)
    {
        // No key file chosen yet, or one we can't read: there is nowhere for a passphrase to live,
        // and no shared slot to accidentally read someone else's out of.
        if (CredentialKeys.For(profile) is not { } key) return CredentialResult.NotFound;

        var current = store.Read(key);

        // Found or Failed, the legacy key has nothing to add: one says we're done, the other says
        // something is there and broken, and neither wants overwriting from an older copy. Reading
        // it anyway would cost a second subprocess on macOS and Linux, on every deploy.
        if (current.Status != CredentialStatus.NotFound) return current;

        var legacyKey = CredentialKeys.Legacy(projectRoot, profile);
        if (legacyKey == key) return current;

        var legacy = store.Read(legacyKey);
        if (legacy.Status != CredentialStatus.Found || string.IsNullOrEmpty(legacy.Secret))
            return legacy.Status == CredentialStatus.Failed
                ? legacy          // there is a secret here, we just can't read it — say so
                : current;

        // Copy, don't move. Deleting the original would be the only destructive step in this path,
        // and it buys nothing: the old entry sits in the same store under the same protection, so
        // it is extra rows rather than extra exposure, and legacy keys are only ever read — never
        // written — so what's left behind is a fixed one-time residue that cannot grow.
        //
        // What deleting would cost is real: an older build looks the secret up at the legacy key,
        // so removing it breaks a downgrade every time. Leaving it only misleads a downgrade that
        // happens after the password was also changed, which needs both to go wrong at once.
        store.Set(key, legacy.Secret);

        return legacy;
    }
}
