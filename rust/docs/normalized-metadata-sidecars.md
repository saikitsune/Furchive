# Normalized metadata sidecars — bounded implementation contract

This milestone implements opt-in **normalized** metadata, not raw upstream JSON.
It does not establish full-application, packaged-WebView or platform acceptance.
The original C# tree and legacy data remain untouched.

## Admission and representation

`Settings.save_metadata` remains false by default. The host captures the current
authorized `MediaItem` when it creates a **new paused** single, visible-pool-page
or whole-pool child. Changing preferences or cached metadata afterward does not
change that snapshot. Existing jobs with absent fields deserialize to
`NotRequested`; reopening them does not adopt/export metadata. Skipped/cancelled
jobs do not export metadata. Raw upstream JSON remains unavailable/deferred; no
`.post.json`, raw-response capture or response-header persistence is introduced.

The UTF-8 JSON document has `representation: "normalized-furchive-v1"` and these
typed fields: `key {source,id}`, `url`, `title`, `description`, `tags` (category to
ordered string list), `artists`, `kind`, `rating`, `created_at`, `fetched_at`,
`score`, `favorite_count`, `file_extension`, `file_size_bytes`, `file_md5`,
`width`, `height`. Optional values are JSON null. Names and enum values are the
Rust domain serialization, not upstream DTOs. Preview/sample URLs, account data,
headers and host filesystem paths are deliberately not part of the schema.
Upstream-authored title/description/tag text is preserved as literal data; it is
not interpreted as paths, requests or settings.

`Intent::with_metadata(&MediaItem)` is a trusted host/library API, never a Tauri
renderer payload. Deserialization rejects unknown snapshot/identity/intent
fields, invalid representation/source/post ID, malformed size/hash/extension,
credential-bearing/noncanonical URLs and URL queries. The snapshot must match
the admitted post identity, exact current media URL and expected size. Validation
runs before queue insertion, including before any group child is inserted.
Serialized snapshot bytes must be at most 1 MiB. The generic core still accepts
only the existing credential-free e621/CDN HTTPS source allowlist.

## Independent lifecycles

| Metadata status | Meaning |
|---|---|
| `NotRequested` | No snapshot was admitted; no automatic export/retry. |
| `Pending` | Snapshot admitted; await successful media publication, then sidecar work. |
| `Written` | Exact sidecar bytes and native file identity acknowledged durably. |
| `Failed` | Optional metadata operation failed; `metadata_error` explains the failure. |

Media `Completed` is committed **before** optional sidecar work. A metadata
collision, filesystem/sync error or acknowledgement error must not erase good
media or convert it to a network-redownload job. `Skipped` and `Cancelled` media
may retain `Pending` snapshot state but cannot publish or retry a sidecar.
`metadata_status`, `metadata_error`, `metadata_attempts` and a native
identity/SHA-256 publication receipt persist in the existing job journal.

The sidecar name is **exact final media path plus `.metadata.json`**, including
its original extension and actual duplicate-policy Rename suffix. For example,
`100 (1).png` produces `100 (1).png.metadata.json`. Media Overwrite policy does
not authorize overwriting an unrelated sidecar. A different existing sidecar is
a visible metadata failure and is preserved byte-for-byte. An exact byte match
to the validated admitted snapshot can be acknowledged idempotently without
replacement. No unrelated-metadata renaming or sidecar-overwrite policy exists.

## Publication and recovery

All opens/traversal use the existing native descriptor-relative nofollow
capabilities, current granted root and native directory/file identities. Metadata
cannot address private `.furchive-downloads` staging or the active database/WAL
namespace; `.furchive-metadata-*` path components are reserved against media
admission. Retry and metadata recovery resolve existing parents read-only and
never recreate missing media directories. Symlink/junction opens fail closed.

1. Verify the completed media native identity, size, expected hash and SHA-256
   publication digest; recheck its namespace identity in the opened parent.
2. Check actual stage existence separately from the receipt: a receipt can name
   a previously published final sidecar that has since been removed. Only reuse
   a receipt to open an existing stage; when both target and stage are absent,
   create a fresh exclusive stage after the unchanged-media checks. A racing
   unclaimed stage still causes refusal, never replacement.
   Sync the directory entry, persist its native identity and expected digest,
   write bounded JSON and sync the file.
3. Reopen/check the stage namespace against the pinned identity and publish with
   native atomic **NOREPLACE** rename in that parent. Sync the parent directory.
4. Persist `Written` with the identity/digest receipt. UI displays acknowledged
   status, never an optimistic success. A failed `Written` SQLite update falls
   back to `Failed` when possible; if SQLite cannot accept any acknowledgement,
   the durable earlier `Pending` record is reconciled on reopen.

The historical `scripts/milestone_evidence.py` freezes the earlier portable-core
milestone only and must not be relabeled as current sidecar evidence. The current
`metadata_source.py` freezes updated source hashes and reviewed platform test
inventories against the isolated metadata verification receipts.

These are per-file atomic publications plus durable journal transitions, **not**
an atomic media-plus-sidecar transaction. Crash cases:

- Before media publication: existing verified media journal reconciliation runs;
  it does not start HTTP. Host restoration still holds network jobs paused.
- After media publication but before metadata begins: `Completed` + `Pending`
  verifies the media receipt and attempts the sidecar on explicit root reopen.
- After stage receipt/full write but before rename: recovery opens the exact
  stage read-only, requires the native identity, private regular single-link
  file and exact bounded bytes, then NOREPLACE-publishes it.
- After stage creation but before a durable receipt, or during a partial stage
  write: unclaimed/changed stage is preserved and status becomes `Failed`.
  This is deliberate fail-closed partial failure, not guessed ownership cleanup.
- After sidecar publication but before `Written`: exact existing validated bytes
  are acknowledged idempotently. Different bytes remain unchanged and fail.
- Reopening `Written` verifies sidecar native identity, size and exact bytes;
  missing/replaced/tampered sidecars become `Failed`, leaving media alone.
  Explicit retry may recreate a removed final sidecar using unchanged verified
  media, even if the journal retains its former final publication receipt.
  Existing unknown/changed stages and colliding targets are still preserved.
- Reopening `Failed` does not silently retry. No recovery branch grants a root,
  starts network transport or adopts legacy jobs.

Unknown/unclaimed/changed stages are never automatically removed or rewritten.
An owned stale stage may therefore require deliberate user cleanup; the retry
command is not a destructive cleanup tool. No guarantees are made against an
actor with same-user open writable file descriptors mutating bytes concurrently
with publication; identity/hash checks detect the covered recovery boundaries,
not a general immutable filesystem transaction against a malicious OS peer.

## Retry, ownership and UI

`download_control {id, verb: "retry_metadata"}` uses only an existing job ID and
the currently selected host grant. It accepts neither paths nor snapshot data.
Only `Completed` + admitted snapshot + `Failed` with fewer than three persisted
explicit retry attempts is eligible. It verifies the unchanged completed-media
receipt before publication, performs no media fetch and never changes media
state. Exhausted retries fail explicitly. Invalid/running/legacy/skipped jobs
cannot retry. A retry also refuses while another Running/Publishing job owns
the same native destination directory.

The checkbox uses existing Rust preference save/acknowledgement. Queue rows show
independent metadata status/error and a bounded retry button only for eligible
host-reported failures. Older TypeScript fixtures may omit metadata fields; the
UI does not invent `Written` or an opt-in default for them.

Sidecar work is synchronous inside the existing owned transfer/control flow;
there are no detached sidecar tasks. Pause/cancel/shutdown acknowledgements still
wait for writer termination. Existing 1–4 worker limits and native-directory
serialization remain in force; RetryMetadata never aborts an active media writer.

Supplied runtime route: nous/openai/gpt-6.1-sol, no fallback or provider/settings
change. The first attempt verified Codex CLI absent; implementation continued
through the supplied model and direct tools. A container/gateway restart
interrupted that attempt; retained work/evidence was re-audited and gates rerun.

## Evidence and remaining acceptance

Retained original RED/GREEN logs are under `rust/test-artifacts/metadata-evidence`
in the isolated checkout; these and the original review BLOCK remain unchanged.
REY-META-01 correction receipts are under `rust/test-artifacts/metadata-repair`:
the byte-preserved reviewer regression first fails 1/2 (exit101), then passes 2/2.
Six supplementary tests cover deleted Written-sidecar retry, tamper/removal,
acknowledgement failure/removal, unchanged-media enforcement, hostile existing
stages/targets, persisted attempt caps and actual retry-process kills before
creation, before/after the stage receipt, before/after publication. Empty or
unclaimed stages remain preserved failures. New core regressions also cover Rename/reopen, hostile admission,
forged lifecycle, collision/bounded retry, idempotence/no adoption, symlink and
hardlink/unclaimed staging, tamper, real child-process kill before/after sidecar
rename, SQLite acknowledgement refusal, active-writer/directory exclusion and
missing-parent preservation. App tests cover snapshot-at-new-admission on all
three enqueue paths. Compiled DOM tests cover preference/status/retry intent.

The updated native browse campaign now requires **27 phase-one + 11 restart**
checks, including normalized-metadata Rename and restart. It creates an owned
unrelated sidecar sentinel, verifies failure and explicit retry without overwrite,
checks the renamed sidecar payload and byte-exact preservation across restart,
and refuses old receipts. Its local component run uses explicitly labeled
in-memory provider/media doubles; host parser/byte-validator unit tests are not
native product acceptance. **Native Linux/macOS/Windows packaged WebView runtime
for this candidate is UNRUN.** No CI dispatch, primary checkout modification,
push, production operation, license decision or release is included.
