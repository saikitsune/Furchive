# Legacy offline import — independent verification

## Verdict and scope

Linux source capability PASS; native Windows/macOS acceptance pending exact committed-head Actions. This is one integration-ready **library** milestone, not migration UI, first-run migration, settings application, network/session restoration, download integration or full application completion. No blocking defect demonstrated within the documented quiescent-source/trusted-destination boundary.

Reviewed Bram task `t_f0939a14` through existing return `t_d18d39a6`; no duplicate worker/review graph. Base `9d72bc50c7a2ff5a9bf5c330b86786aad13e9b0b`. All10 frozen module/test/doc SHA256 values and4 actual C# source values independently matched the parent handoff before executing tests. Implementation bytes were not edited by verifier. Detailed API/identity/limits/source hashes are in `legacy-import.md`.

## Independent execution

On the installed Rust1.99.1 with explicit HOME/RUSTUP_HOME and auto-install disabled:

- Original31integration tests PASS, original complete42app tests PASS (including2new private helper tests). Root workspace tests PASS, root workspace fmt PASS, workspace/app all-target strict Clippy PASS. `git diff --check` PASS.
- Added `tests/legacy_import_verifier.rs`: a separate real-SQLite fixture uses the exact13-column C# downloads schema, exact pin/session schemas, synthetic settings and art. Verifies both queued and completed rows remain quarantine count only; destination adds only the receipt table; receipts contain only identities; no queue/credential/directory strings in destination file bytes; settings/session hints do not create the directory; an edited imported search is not overwritten by repeat import; complete old file listing and bytes unchanged. First run PASS, a verification probe of existing behavior, not a claimed new RED/GREEN production change.
- Parent actual ABORT/IGNORE trigger rollback, WAL corruption refusal, WAL-only pin preservation, view/virtual/generated-column refusal, collision skip, deletion nonresurrection, strict selection/digest, duplicate keys and actual-reader cap tests were independently executed, not merely read.

Raw commands/transcripts: `rust/test-artifacts/legacy-verifier-t_d18d39a6/`. Final counts are recorded with exact native source/head in that evidence root after admission. Root workspace deliberately excludes desktop/downloads/catalog; do not treat its tests as those crates' acceptance.

## Formats and trust boundaries

Verified C# `SettingsService.cs` saves a dictionary: PropertyNamingPolicy does not rename dictionary keys; SavedSearches is an embedded JSON string. DTO/rating mappings confirmed in MainViewModel. Actual pin file is `cache/e621/pinned_pools.sqlite`; session is `last_session.sqlite` meta key `last_session`; download store is root `downloads.sqlite`. The importer does not execute the old C# application.

Sources receive bounded regular-file reads only; never an original SQLite handle. Stable main+WAL snapshots are privately staged; old SHM remains untouched. Only readonly/query_only SQLite static ordinary-table queries run on snapshots. Selected candidates are immutable after confirm. Destination requires existing checkpointed Rust schema markers outside old canonical ancestry, then one IMMEDIATE transaction containing inserts+receipts. Existing name/ID/pin collisions skip; imported deletion/edits remain authoritative. Preview preferences/session DTOs have no filesystem/network grant and importer has no network/download runner call path.

## Required integration/native follow-through

Current existing `.github/workflows/rust-qualification.yml` already runs the entire root workspace on Linux, Windows, macOS, then desktop/package qualification. Preserve the bounded15/35minute jobs/5day artifact retention and original download outage/failed Windows fixture evidence. Exact source must be committed together with approved app rusqlite/sha2 dependency seam and both lockfiles; no independent dependency push. Read actual named legacy results in the three native job logs, not a global green badge or old7deb877 package receipt. Existing7deb877/run43114513874 all6job success and downloaded package receipts predate this source and **do not prove legacy native coverage**.

Readback current Desktop via session_search: `20261111_114505_42cbbb`, source desktop. Current-only task binding `tui`, matching chat, `notify+wake`; no Discord. Latest live owner transcript assigned tests/native integration to this existing verifier. Verifier owns bounded legacy source admission/Actions, not desktop/media/root planning mutations. `main`, releases, license, profiles/models, credentials, real user stores/art, production services untouched.

## Unsupported / not certified

- No automatic or UI migration, credentials/vault migration, applied settings, restored search/pool/session/network or queued download continuation; original art/cache/downloads stay in place.
- Pin names/counts are preview-only; Rust destination stores pool identities. Unknown preferences, blacklist, caches and other old formats are not imported.
- Requires quiescent local source and authorized trusted existing destination. No hostile hardlink/mount/same-user replacement/racing-writer sandbox; canonical ancestry is not inode fencing. Caller must never authorize artwork as destination.
- Conservative WAL admission rejects stale/reset/uncommitted tails, nonempty rollback journal and malformed containers; manual stopped recovery may be needed. Caps:1MiB settings,16MiB each DB/WAL/journal,1000records per kind; no unbounded bulk migration claim.
- Destination rollback is logical, not byte-identical destination sidecars/power-loss proof. ASCII name collision folding only.
- Private snapshots may transiently contain arbitrary unknown-table bytes; normal-drop cleanup is not crash cleanup/forensic/secure-memory erasure. Raw JSON exists transiently; credential fields are excluded from public objects/digests/receipts, not a claim to scrub secrets deliberately put in user content fields.
- No syscall-trace assertion (strace unavailable), native import UI exercise, broader media/lifecycle/performance/signing/installer/full-app acceptance inferred.
