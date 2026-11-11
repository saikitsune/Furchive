# Portable download verification — owner lane t_b8082b2d

## Frozen source and local verification

Bram's source-only completion t_d5d44259 was read back, not treated as native
certification. Base HEAD `10c9e330a676541f32b2e7714053469d3f846aa6`.
Manifest SHA256 `c49809ff29b88140b05da16746faa2e25acc4e1d646b8d6a32e1971d094e6f00`;
all24 file hashes and byte lengths independently match. Source review traced
platform-relative grant traversal, identity, staging, atomic publication,
publication recovery and the platform-specific DB-outside-grant policy.
Historical DL-01 BLOCK and subsequent owner repair remain unchanged.

Verifier copied the exact crate plus its domain path dependency to owned scratch,
without target/history/cache directories, and independently ran the actual gate
with installed Rust1.99.1 and the existing crate registry cache:

- `python scripts/test_native_check.py -v`:3 guard tests PASS.
- `python scripts/native_check.py`:44 named Linux tests,1 doctest, fmt check,
  all-targets Clippy `-D warnings` PASS. Discovery and successful names match the
  reviewed inventory. Source hashes rechecked unchanged after execution.
- No Windows or macOS runtime pass inferred from local execution or GNU cfg checks.

Independent receipts retained under
`rust/test-artifacts/download-verifier-t_b8082b2d/` (ignored), separately from
Bram's retained receipts. Original failed evidence was not overwritten.

## Native Actions admission

Existing `.github/workflows/rust-qualification.yml` extended with a separate
three-OS download matrix (Ubuntu24.04, Windows hosted MSVC, macos15),15minute
job limit, fail-fast:false, contents:read and5day exact logs/receipts. The prior
Linux-only duplicate download step was replaced, not retained as certification.
Existing native desktop and package gates stay intact and may evolve separately.
Checkout line endings are pinned LF on the ephemeral runner so source hashes are
comparable. No secrets, live e621 or user media are used. No skip/fallback for
Windows symlink/junction fixtures or directory flush/rename availability.

Sai's existing rewrite/rust push and native Actions authorization applies; public
repository visibility and authenticated push:true were re-read before admission.
No main merge, release, license, profile/provider or production change authorized.

Native acceptance is PENDING. Download artifacts must be read back against their
exact run/head, all platform names/counts/source hashes and real successful tests.
A public source commit or green cfg build alone is not portable download support.
Current Desktop subscription was independently read back as current-only
`tui:20261111_114505_42cbbb`, default notifier, notify+wake; no Discord routing.
Live Desktop owner explicitly confirmed this verifier is sole download CI owner.

## Remaining scope

Windows local NTFS only; all reparse points refused. macOS physical nofollow
paths; database outside media root on Windows/macOS. Strict real directory flush
is mandatory. No power-loss test, same-user/admin sandbox, broader platform
architecture claim, desktop Downloads integration or whole-application acceptance.
Furchive remains incomplete.

## First admitted native run: external queue incident

Scoped source/workflow commit `2992d62b819652210973cf1e5a2da13b3f5e90c3`
was pushed to rewrite/rust and independently read back through both Git remote
refs and GitHub tree API (all26 source/manifest/workflow blobs agree). Main
remained `f395735d2e0441613a21205a99913ab29e1c7685`.
Run [43098258533](https://github.com/saikitsune/Furchive/actions/runs/43098258533)
was admitted at2026-11-11T20:49:07Z but remained queued with zero jobs/checks/
artifacts during bounded supervision. GitHub's authoritative status reports
Actions major_outage since20:48:25Z in incident
[dvy0t3mvh1lg](https://stspg.io/jdjlq5zjyl48), before this push. Its21:07:26Z
update reports recovery and an Actions backlog still being processed. This is an
external execution gap, not an observed product compile/test failure or PASS.
Raw queue, status and remote-source readbacks retained in
`rust/test-artifacts/ci-run-43098258533/`. No duplicate dispatch/retry, speculative
source fix, paid runner or relaxed acceptance gate was used.

Native acceptance cannot close while the admitted run has zero jobs/artifacts.
Return this exact queued-run checkpoint to the already-subscribed live Desktop
owner; resume the existing card/run after backlog drain, with no duplicate worker
or native dispatch. Do not push this documentation-only checkpoint while the
candidate run is pending: workflow concurrency would cancel the exact run under
observation. A local documentation-only checkpoint does not change the public
candidate or waive native acceptance.

Verifier tooling notes: optional PyYAML import unavailable; the installed
`hermes_yaml.safe_load` parsed and checked the actual workflow successfully.
A status-summary one-liner had a syntax typo and was corrected before any
interpretation; neither failure changed product source or native outcomes.
