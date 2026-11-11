# Explicit legacy user-data recovery (library milestone)

This is an offline library capability, **not** migration UI, automatic first-run migration, session/network restore, or full C# application parity. All fixtures are synthetic. No real user stores, credentials or artwork were used.

## Source-grounded formats

The inspected C# sources are the authority, not historical README names:

- `src/Furchive.Core/Services/SettingsService.cs`: `LocalApplicationData/Furchive/settings.json`, a JSON object with case-sensitive PascalCase dictionary keys. `SavedSearches` is itself a JSON **string** containing an array, not an outer array.
- `src/Furchive.Avalonia/ViewModels/MainViewModel.cs`: `SavedSearch { Name, IncludeTags, ExcludeTags, RatingFilterIndex }`. Rating index 0=any, 1=explicit, 2=questionable, 3=safe. Missing fields use C# DTO defaults; supplied invalid fields are not coerced. `LastSession { IsPoolMode, PoolId, Include, Exclude, RatingFilterIndex, Page }`.
- `src/Furchive.Core/Services/SqlitePoolsCacheStore.cs`: `cache/e621/pinned_pools.sqlite` (not `pinned_pools.db`), ordinary `pinned_pools(id INTEGER PRIMARY KEY,name TEXT NOT NULL,post_count INTEGER NOT NULL)`; `cache/e621/last_session.sqlite`, ordinary `meta(key TEXT PRIMARY KEY,value TEXT)`, key `last_session`, value the DTO JSON. No inference from the unrelated pools/posts cache.
- `src/Furchive.Core/Services/SqliteDownloadsStore.cs`: root `downloads.sqlite`, ordinary `downloads` table. Only a bounded count is inventoried, **all** statuses quarantined for manual recovery; destinations, URLs, errors, media objects and queue jobs are never decoded/imported/resumed.

## Callable API and explicit consent

```rust,no_run
use furchive_app::legacy::{preview, ImportSelection, import_into};
use furchive_engine::storage::StateStore;
use std::path::Path;

fn recover(old_root: &Path, authorized_new_state: &Path) -> Result<(), String> {
    // Integration/UI must stop C# first and let the user review inventory/warnings.
    let inventory = preview(old_root)?;
    let selection = ImportSelection {
        // A UI should provide exactly the user's checked IDs; none are defaulted.
        search_ids: inventory.searches().iter().take(1).map(|s| s.id.clone()).collect(),
        pool_ids: vec![],
    };
    let plan = inventory.select(selection)?; // empty/unknown/duplicate IDs refused
    // A separate caller action confirms the digest displayed with this inventory.
    let confirmed = plan.confirm(inventory.content_digest())?;
    // Initialize/authorize explicitly OUTSIDE the old tree; close/checkpoint first.
    drop(StateStore::open(authorized_new_state).map_err(|e| e.to_string())?);
    let report = import_into(confirmed, authorized_new_state)?;
    assert_eq!(report.imported_pins, 0);
    Ok(())
}
```

`Inventory` exposes read-only `searches()`, `pins()`, `settings()`, `session()`, `queue()`, `warnings()`, `content_digest()`. Its candidate vectors are private. `ImportPlan` and `ConfirmedImport` cannot be forged/edited through public fields. Selection and digest confirmation are separate operations. The confirmed immutable candidate snapshot is consumed; source changes after preview **cannot substitute new content** at write time. Confirmation cannot prove a human clicked; presentation belongs to the downstream application. Import reports imported/skipped search and pin counts, no raw source data.

`read_settings(root)` remains a compatibility read-only preview (`LegacyPreview { searches, warnings }`) with existing malformed-shape/chip/secret tests preserved. Its warning list is item/setting diagnostics only, to retain existing contracts; the full `preview()` always warns to re-enter credentials through the native vault.

### Preview-only nonsecret preferences

Strict optional allowlist: `FilenameTemplate`, `PoolFilenameTemplate` (domain template validation), `ConcurrentDownloads` (1–4), `DownloadDuplicatesPolicy` (exact skip/overwrite/rename), `GalleryScale` (0.5–2), `SaveMetadataJson`, `LoadLastSessionEnabled`, `DefaultDownloadDirectory` (bounded inert text hint). Unknown settings are ignored, never copied. An invalid individual field is absent and warned, not default-converted. Settings/session previews **are not Settings**, are never saved/applied by the importer, do not grant directories, never create/traverse a media path and never perform network requests. Session query/page/pool are inert DTOs, not actions. Unsupported settings, cache data and art stay in place.

Credentials (username/API key/password/token and all unknown fields) are not candidates, debug output, error strings, receipts, digests or destination records. Raw bounded JSON necessarily exists transiently in process memory to parse it; no secret-retention/logging or secure-memory-erasure guarantee is claimed. SQLite snapshots may temporarily contain arbitrary database bytes, including unknown tables; their values are not selected or exported and the private temp directory is removed on normal drop. No raw settings snapshot is written to disk. A crash/OS failure can leave temporary snapshots; callers must use private encrypted scratch storage if forensic erasure is required.

## Identity, collision and transaction policy

- Protocol namespace `legacy-v1-` + SHA256 of canonical JSON `(validated exact name, validated SearchQuery)` is the search ID. It is independent of source path, array position, unknown fields and credentials. Exact duplicate content is deduplicated with a warning. Names/tags are preserved, not silently trimmed/normalized.
- `content_digest()` is SHA256 of canonical validated inventory `(searches,pins,settings,session,queue)`, excluding warnings, source path and raw input. It is a **nonsecret content identity**, not a hash of credential-bearing settings. Array order is significant for preview digest; reordering does not change item IDs. Same accepted content in another source directory maps to the same items. No source-location identifier or credential hash is persisted.
- Pin identity is existing domain `(e621, positive decimal i32 pool ID)`. Pin metadata is preview-only; the Rust state schema stores identities, not legacy names/counts. Duplicate pin identities fail closed.
- A current search ID **or SQLite ASCII-case-insensitive name** collision is skipped; there is no overwrite/rename/merge. Unicode case folding is not claimed. Exact pin key collisions are skipped; same ID under another source is independent.
- Successfully imported items get `legacy_import_v1(kind,item_id)` receipts in the authorized state database. Receipts contain IDs only. Repeated import never duplicates/overwrites or resurrects an imported item later edited/deleted/unpinned by the user. Collision skips do not get receipts; if that collision is subsequently removed a later explicit import can succeed. Changed legacy search content has a new ID but is still subject to name collision.
- Destination must already exist with checkpointed Rust state `application_id`/schema markers. A fixed 100-byte header is read before a writable SQLite open (foreign old WAL databases must not be checkpointed just to reject them); the markers are rechecked **inside** the transaction. No `StateStore` migration is run on legacy inputs. The destination must canonicalize outside the preserved old tree.
- One SQLite `IMMEDIATE` transaction covers ledger DDL, selected search inserts, pin inserts and receipts. Plain parameterized INSERT, never UPDATE/REPLACE/INSERT OR IGNORE. Every admitted insert must affect exactly one row; trigger-aborted/ignored inserts fail and rollback everything. Only existing collisions are skips. The destination is trusted app state, not a malicious schema sandbox; old-source triggers never run. A failed transaction preserves logical current state; SQLite may alter authorized destination WAL/SHM/journal bytes. No power-loss fault injection claim.

## Preservation and resource boundary

Original databases are **never opened by SQLite**, even with mode=ro: SQLite read-only WAL opens can otherwise create/change SHM. Fixed known leaves receive regular-file, bounded `Read::take(limit+1)` reads only. Main/WAL/journal are read twice and compared to refuse moving inputs. Old SHM is never opened/copied; private SQLite recreates its own scratch SHM. Main+WAL snapshots are opened `SQLITE_OPEN_READ_ONLY` with `query_only=ON`, `trusted_schema=OFF`, busy timeout zero; no URI strings from legacy data, ATTACH, extensions, user SQL, migrations or writes to old files. Present nonempty rollback journal, orphan sidecars, malformed DB/container/schema fail closed; stop C# and use manual offline recovery instead.

WAL validation checks magic/version/page size, exact frame lengths, header/frame rolling checksums, salts and a final committed frame. This intentionally rejects valid-but-stale/reset/uncommitted WAL tails rather than accepting a partial inventory; normal C# close/checkpoint or manual recovery may be necessary. It is not a general SQLite recovery tool.

Bounds: settings 1 MiB of actual read bytes; each main/WAL/journal 16 MiB actual bytes; 1,000 searches/pins/queue rows; JSON nesting 32 checked before recursive parsing, duplicate JSON keys rejected recursively; name/directory/template 4,096 UTF-8 bytes; 128 tags per include/exclude list, 256 bytes each; session JSON at most64KiB. SQLite returns bounded CASE-guarded text (not whole huge values), limits length2MiB, SQL8192, columns64, expression depth32, VDBE ops10000, attached DBs0, variables32, trigger depth0, workers0, compound selects0. Progress hook interrupts after approximately1million VM instructions per snapshot. Oversized individual values within SQLite's hard limits warn/reject; hard SQLite limits fail the container. Only ordinary tables with no generated/hidden columns are read; views/virtual tables and their expressions are refused. No arbitrary old row values are used in SQL. Total work is bounded by the fixed store inventory and these limits, not metadata size alone.

The caller must supply a quiescent local legacy root and a trusted local destination. This is not an OS capability sandbox against same-user/admin path replacement, hardlink aliases, mount changes or change-and-restore racing writers; file contents/listing are preserved, access-time metadata is not promised. Parent directories/root may be symlinked by the caller; known input leaves that are symlinks/nonregular are refused. Destination ancestry is canonical-path based, not native inode/file-ID alias fencing. Never authorize artwork files as a destination. UI/native media grants/vault integration remain separate milestones.

## Verification and handoff

Tests live in new `tests/legacy_import*.rs` plus two private helper unit tests in `src/legacy/tests.rs`; existing legacy tests remain unchanged. Synthetic fixtures exercise actual bundled SQLite files/WAL, real StateStore reopening, destructive old triggers, hostile views/virtual/generated columns, byte-exact old tree/art preservation, bounded invalid data, idempotency after deletion, source mutation after confirmation, collision preservation and transaction rollback via real destination ABORT/IGNORE triggers.

Important commands (Rust1.99.1, bounded invocations):

```sh
cd /opt/data/projects/Furchive/rust
# Existing installed toolchain, no auto-install; prefix local commands with:
export HOME=/opt/data/home RUSTUP_HOME=/opt/data/home/.rustup RUSTUP_AUTO_INSTALL=0
export PATH=/opt/data/home/.cargo/bin:$PATH
cargo +1.99.1 test --locked -p furchive-app --test legacy_import --test legacy_import_bounds --test legacy_import_sqlite
cargo +1.99.1 test --locked -p furchive-app
cargo +1.99.1 fmt --all -- --check
cargo +1.99.1 clippy --locked -p furchive-app --all-targets -- -D warnings
```

The first import test observed missing API RED, then actual import GREEN. Subsequent observed behavior RED→GREEN corrections cover generated columns, old-tree destination refusal, foreign-WAL target preservation, corrupt WAL omission, ignored admitted writes, duplicate content, duplicate JSON keys and duplicate pin identities. Other added regression tests passed first time against these policies; not every assertion is claimed as a separately observed RED. Linux execution is local evidence only; Windows/macOS tests are portable standard Rust/rusqlite fixtures without platform skips, but native runs remain the existing return owner's gate.

Dependency seam: parent explicitly added app direct `rusqlite0.37` bundled/limits/hooks and `sha2 0.10`, reconciled root/desktop locks. Bram never edited manifests/locks, staged, committed or pushed. An initial toolchain discovery invocation used the wrong HOME and rustup unexpectedly auto-installed1.99.1 there; subsequent commands use the existing explicit toolchain environment above. No model/provider/profile configuration changed.

<!-- VERIFIED_EVIDENCE -->

### Final local verification receipt

- Assigned base `7deb877701bdc0f13c7cf3567648c4858017df98`; final observed HEAD `9d72bc50c7a2ff5a9bf5c330b86786aad13e9b0b` is an owner docs/progress-only advance (verified diff touches no Rust code). Worker made no commits.
- Targeted integration tests: **31 passed**, 0 failed/ignored (4 +10 +17).
- Added private unit tests: **2 passed**, 0 failed/ignored.
- Entire app: **42 unique named tests passed**, 0 failed/ignored, 0 doctests.
- `cargo +1.99.1 fmt --all -- --check`: exit0.
- `cargo +1.99.1 clippy --locked -p furchive-app --all-targets -- -D warnings`: exit0, no diagnostics.
- Final commands used the explicit installed toolchain environment above. One attempted Python transcript aggregation was refused by unattended command policy and did not run; normal direct tool commands supplied the evidence instead. No policy/config bypass.

Owned code/test SHA256 (paths relative to `rust/crates/furchive-app`):

```text
2e12f22c1a024c2c4646a78388eeb4a55b682bb6e6173d14b235940953387a37  src/legacy.rs
0bf3777a8ec58c4605fd909d8aed8fe530d1dcf4fafb83c5708306d6a61b9ebe  src/legacy/json.rs
e0d9e5f4f366ffec778d5959c984e96420a310314857798870fd7bf8292308b7  src/legacy/sqlite.rs
2af6910cc112e80362b94a22942b64fac49fd45ad65a6e335f42101f5fc700ea  src/legacy/tests.rs
97ecd786c46b97f3823fe6c7166a420253118f669b0680b147e4b7056be63268  src/legacy/transfer.rs
e64bc098e28dd9e0c885b78441a8866331b85675c39354d97fa5947affa57458  src/legacy/wal.rs
63d20973d502ceeadcbc07f9dc28063c42dffaf01de39817c3dff086b3947ff5  tests/legacy_import.rs
1fbd4f4045794c44e93c0aae778ecdd1fac454f177cabcb446962e9a052274b6  tests/legacy_import_bounds.rs
1bc3f5d228c7bdb8e9f621d1c213ccf4f4fecf1b5f453a40f684fbedc1552a63  tests/legacy_import_sqlite.rs
```

Read-only C# source hashes:

```text
ff63b989838b9bfb09ef7a5b80d45fbbc18f4007c978e7f4a7c170c94c553d48  SettingsService.cs
cf75c6df80dab53a1deac9d58e1cfa5d340609d416dc868477f688b7b7f226ac  SqlitePoolsCacheStore.cs
4fae3f4c0a1ab3dafb72e9ce726cf080d6a83e0b044fd21a5d49dae34145a585  SqliteDownloadsStore.cs
f2ffef401bb13fb510679d0c58e6113fabae6221443afe021afd8f338b92bd72  MainViewModel.cs
```

New unique test inventory (**33**):

```text
legacy::tests::actual_reader_is_stopped_at_limit_plus_one_without_metadata
legacy::tests::wal_header_only_and_uncommitted_tail_are_refused
legacy_import::preview_inventories_allowlisted_settings_without_authority
legacy_import::repeated_import_never_duplicates_or_resurrects_deleted_rows
legacy_import::explicit_selected_search_import_is_persistent
legacy_import::current_id_and_ascii_name_collisions_are_never_overwritten
legacy_import_bounds::ambiguous_duplicate_json_keys_fail_closed
legacy_import_bounds::duplicate_legacy_searches_are_deduplicated_with_a_warning
legacy_import_bounds::json_depth_budget_ignores_brackets_inside_escaped_strings
legacy_import_bounds::invalid_individual_fields_warn_without_coercing_ratings_or_types
legacy_import_bounds::selection_and_confirmation_are_separate_strict_steps
legacy_import_bounds::secret_changes_do_not_affect_digest_or_item_identity
legacy_import_bounds::actual_settings_bytes_are_capped
legacy_import_bounds::malformed_containers_depth_and_record_limits_fail_closed
legacy_import_bounds::exact_user_selected_subset_does_not_import_other_records
legacy_import_bounds::confirmed_inventory_is_not_replaced_by_changed_source
legacy_import_sqlite::rollback_journal_refuses_automatic_recovery_without_mutation
legacy_import_sqlite::import_refuses_any_destination_inside_the_legacy_tree
legacy_import_sqlite::source_generated_columns_are_not_evaluated
legacy_import_sqlite::source_pin_duplicates_fail_closed
legacy_import_sqlite::source_views_and_virtual_tables_are_not_executed
legacy_import_sqlite::foreign_destination_with_wal_is_refused_without_sidecar_mutation
legacy_import_sqlite::oversized_pin_values_are_rejected_without_losing_valid_rows
legacy_import_sqlite::malformed_wal_never_silently_loses_committed_pins
legacy_import_sqlite::source_triggers_and_other_tables_never_run_or_leak
legacy_import_sqlite::sqlite_pin_queue_and_database_limits_fail_closed
legacy_import_sqlite::truncated_and_checksum_corrupt_wal_fail_closed
legacy_import_sqlite::ignored_admitted_write_rolls_back_instead_of_recording_success
legacy_import_sqlite::admitted_pin_failure_rolls_back_search_pin_and_receipts
legacy_import_sqlite::pin_collisions_and_deleted_imported_pins_preserve_current_decisions
legacy_import_sqlite::old_main_wal_shm_settings_and_art_remain_byte_exact
legacy_import_sqlite::session_container_errors_fail_but_invalid_fields_warn
legacy_import_sqlite::real_sqlite_pins_session_and_queue_are_previewed_offline
```

Final changed files owned by this worker: `src/legacy.rs`; new `src/legacy/{json,sqlite,tests,transfer,wal}.rs`; new `tests/legacy_import.rs`, `tests/legacy_import_bounds.rs`, `tests/legacy_import_sqlite.rs`; this new `docs/legacy-import.md`. Parent-owned manifest/lock/progress/doc changes visible in shared git status are not worker edits. No existing tests, `lib.rs`, `settings.rs`, domain/engine/downloads/desktop/workflow files changed by worker. Existing verifier child `t_d18d39a6` is the return lane; no additional review/delegation graph.
