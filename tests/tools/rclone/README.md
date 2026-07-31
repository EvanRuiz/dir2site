<!-- SPDX-FileCopyrightText: 2026 Evan Ruiz -->
<!-- SPDX-License-Identifier: AGPL-3.0-or-later -->

# rclone (test fixture)

`rclone serve sftp` is the SFTP server the integration tests run against. It is not committed —
`RcloneTool` downloads it on first use into `.cache/` (gitignored) and reuses it afterwards.

It is a test fixture only: never shipped with dir2site, never referenced by application code, and
it only ever listens on loopback.

## The pin

rclone **v1.74.4**, official release builds, MIT licensed.

The expected SHA256 of every archive and every extracted binary is hardcoded in
`tests/dir2site.Tests/RcloneTool.cs`. That placement is the point — verifying a download against a
checksum fetched from the same host proves nothing, since anything able to alter the artifact can
alter the checksum next to it. Hashes in the repo mean the version cannot change without a
reviewed diff.

A mismatch is a **hard failure**, never a skip or a silent substitution.

The published hashes came from <https://downloads.rclone.org/v1.74.4/SHA256SUMS>, confirmed
byte-identical to the copy GitHub serves as a release asset.

## Skips versus failures

If a platform has no pin, or the download can't be reached, the integration tests skip with a
reason. That keeps an offline developer unblocked — but in CI a silent skip would look exactly
like a pass while covering nothing, so CI sets `DIR2SITE_REQUIRE_SFTP_TESTS=1`, which turns an
unreachable download into a failure.

Pinned platforms: `windows-amd64`, `osx-arm64`, `osx-amd64`, `linux-amd64`. ARM Linux has no pin
and will skip.

## Updating

Pinning means security fixes are not picked up automatically. That is a deliberate trade for a
loopback-only test fixture; bumping is a conscious act:

1. Pick the new version and download its archives from `https://downloads.rclone.org/<version>/`.
2. Verify them against that version's `SHA256SUMS`.
3. Update `Version`, `ArchiveSha256` and `BinarySha256` in `RcloneTool.cs`, and the version above.
4. Run the tests on a cold cache so the new hashes are proven before the change is reviewed.
