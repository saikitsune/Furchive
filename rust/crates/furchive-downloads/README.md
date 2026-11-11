# Furchive durable single-download core

**Bounded portable source milestone; native Windows/macOS acceptance pending.**
Not a product UI or a complete Furchive app. See [native contracts](contracts/NATIVE.md)
and the explicit per-platform [test inventory](contracts/test-inventory.json).
This nested workspace intentionally does not edit the parent Cargo.toml/lockfile.
The integrator owns workspace membership and native Actions/runtime acceptance.

## Exact public interface

```rust,no_run
use furchive_downloads::{Engine, Intent, DuplicatePolicy, ExpectedHash, Control};
use furchive_domain::{MediaItem, FilenameTemplate, TemplateValues};
use std::{path::Path, sync::Arc};

async fn download(db: &Path, root: &Path, media: &MediaItem) -> furchive_downloads::Result<()> {
    // Synchronous DB/filesystem methods: call from an owned blocking lane, not GUI.
    let engine = Arc::new(Engine::open(db, root)?);
    let template = FilenameTemplate::parse("{source}/{artist}/{id}.{ext}").unwrap();
    let values = TemplateValues {
        source: media.key.source(),
        artist: media.artists.first().map(String::as_str).unwrap_or("unknown_artist"),
        id: media.key.id(),
        ext: media.file_extension.as_deref().unwrap_or("bin"),
        ..Default::default()
    };
    let intent = Intent::new(media, &template, &values, DuplicatePolicy::Rename, None)?;
    let id: i64 = engine.enqueue(intent)?;
    let worker = engine.start()?; // requires active Tokio runtime
    let paused = worker.control(id, Control::Pause).await?;
    // Receipt is a snapshot; terminal jobs remain terminal if they won the race.
    let _ = paused;
    worker.control(id, Control::Resume).await?;
    let jobs = engine.jobs()?; // persisted ID/FIFO order
    let _ = jobs;
    worker.shutdown().await?; // joins, pausing any active transfer
    Ok(())
}
```

- `Intent::new(&MediaItem, &FilenameTemplate, &TemplateValues<'_>, DuplicatePolicy,
  Option<ExpectedHash>) -> Result<Intent>`. Copies exact domain `PostKey`, `full_url`,
  `file_size_bytes`; renders using the **existing** domain template contract.
  `ExpectedHash::{Sha256(String), Md5(String)}` accepts exact-length hexadecimal,
  case-insensitive comparison. Metadata adapter supplies hash separately because
  the current domain MediaItem has no hash field. No network metadata discovery.
- `Engine::open(db: impl AsRef<Path>, root: impl AsRef<Path>) -> Result<Engine>`.
  Root must already exist. DB parent must exist. Reconciles before returning.
  Windows/macOS require the trusted DB outside the media grant; see native contract.
- `Engine::{enqueue(Intent) -> Result<i64>, jobs() -> Result<Vec<Job>>}`.
- `Engine::start(self: &Arc<Self>) -> Result<Worker>`.
- `Worker::control(&self, id: i64, Control) -> impl Future<Output=Result<Job>>`.
  `Control::{Pause,Cancel,Resume}`; unknown ID errors; terminal jobs no-op and
  return their actual state. Paused/Failed resume at original FIFO position.
- `Worker::shutdown(self) -> impl Future<Output=Result<()>>` joins the worker.
  Drop requests shutdown but is **not** an acknowledged stop. Always await shutdown.
- `Job` exposes `id`, domain `key`, `state`, committed `bytes`, `relative_path`,
  `error`; serialization includes internal persisted resume/publication data.
- `State::{Queued,Running,Paused,Cancelled,Completed,Skipped,Failed,Publishing}`.
  `Publishing` is a durable commit-intent state; if I/O fails in this state, reopen
  for reconciliation rather than retry/cancel an ambiguous publication.
- `Error::{Io,Sql,Json,Invalid,Busy,Stopped}`, `Result<T>` exported.
  Intent creation renders; **enqueue and every persisted read** validate URL,
  portable path, hash and identity. Do not expose serialized intents as a desktop
  arbitrary-URL IPC endpoint: the parent creates authorized MediaItem intents.

## Shared durable semantics (runtime verified here on Linux)

### Ownership / persistence / controls

One exclusive OS file lock per granted root for the lifetime of Engine; one
worker per Engine, one active transfer globally (therefore one writer per job).
Concurrent enqueue commits are mutex-serialized, assigned SQLite AUTOINCREMENT
IDs, and serviced in committed ID order. SQLite bundled, WAL, synchronous FULL,
five-second busy timeout, application ID `0x4655444c`, schema 1. Foreign/nonempty
unversioned/future databases rejected. Native root identity is bound in DB; random
DB identity is bound in a durable private-root marker so a second DB cannot reuse
job IDs/parts. Do not delete/replace the marker/lock while the engine is in use.
A torn initial marker fails closed and needs explicit operator recovery.

Per-job commands pass through one worker mailbox, receive a durable state receipt
**after** the affected response/file future is dropped. Paused retains partial;
Cancelled commits cancellation before unlinking partial; reopen completes cleanup.
No original is deleted on queue/cancel. A control arriving after publication returns
Completed instead of falsely acknowledging cancellation. Controls can interrupt
waiting for headers/body; unrelated-job commands do not drop the current transfer.
Publication/hash/fsync/SQLite operations are synchronous and non-preemptible;
controls wait for them, so this is not a hard real-time latency guarantee.

Every chunk is written and fsynced **before** its committed byte offset is saved.
Restart converts Running to Queued, preserves Paused, cleans Cancelled/Skipped
partials. Before resume, extra uncommitted file tail is truncated; short/missing
partial or missing validator restarts from zero. Completed historical jobs are
not automatically redownloaded if users subsequently delete their files.

### HTTP and integrity

Reqwest rustls, no proxy/cookies/auth headers, `Accept-Encoding: identity`, no
redirect following at all. Production exact HTTPS hosts: `e621.net`,
`static1.e621.net`, `static2.e621.net`, `static3.e621.net`; no userinfo, nondefault
ports, fragments, foreign suffixes, HTTP or literal IPs. TLS validates host/cert.
Test-only private `cfg(test)` maps an admitted URL to one injected 127.0.0.1 fixture
origin; no feature or public production bypass. No live service fetch in tests.

Resume uses strong syntactically valid ETag, otherwise valid Last-Modified proven
at least 60 seconds older than Date (RFC9110 conservative strong-date condition).
Sends `Range: bytes=N-` and exact `If-Range`. A 206 requires exact start, known
numeric total, end at total-1, unchanged validator/known total and matching length
when supplied. Malformed/missing/changed validators or mismatched ranges fail
closed **without appending**; explicit retry remains failed if server keeps lying.
A 200 on resume truncates and replaces the partial, never appends. A 416 completes
only if `bytes */N`, committed/local length, prior total, and validator all agree;
expected hash/size validation still runs. Otherwise Failed, never error-page media.
Unexpected status/redirect/encoding fails. Connect timeout15s/request timeout60s;
no automatic retry/backoff/rate limiter (caller may Resume Failed).

Before publication: local byte count vs total and optional expected metadata size,
plus optional SHA256/MD5, are checked; full-file SHA256 is always retained for
publication recovery. Short bodies/oversized chunks fail; no expected-size overrun
is written. Without a supplied content hash, correctness relies on TLS/server
validator/framing; this is not authenticity of the source's claimed content.

### Filesystem / atomic publication

Linux `openat2(BENEATH|NO_SYMLINKS|NO_MAGICLINKS)`, dirfd-relative operations,
`O_NOFOLLOW`, private regular/nlink1 `.part` files. Symlinks in granted root path,
child parents, part paths are refused. No resolve-then-open media writer. Root DB
and SQLite companion names are reserved if located under root. Template generated
names are portable-validated even after deserialization. Internal staging name is
reserved (also by opened-directory identity, covering native short-name aliases).
macOS traverses pinned directories with single-component `openat(O_NOFOLLOW)`;
Windows uses single-component `NtCreateFile(RootDirectory)` with reparse rejection.
Duplicate collisions use native filesystem semantics; no portable equivalence claim.

`Skip`: existing regular file skips without network; publication races use atomic
NOREPLACE. `Rename`: atomic NOREPLACE chooses `stem (N).ext` (1..9999), never
clobbers a newly arriving file. `Overwrite`: atomic rename replaces the *directory
entry* only after full validation; never opens/truncates the original, and never
follows a leaf symlink. Only explicit Overwrite may replace a destination. Skip on
a nonregular/symlink destination fails; overwrite may replace a symlink entry
without touching its target. Directory symlink ancestors always fail.

Full part fsync; persisted publication intent records path, native identity, digest;
rename, then source/destination directory fsync, then Completed commit. Reopen
checks inode+size+hash and either finishes a still-present part or recognizes the
published file. Changed/missing ambiguous files fail closed instead of falsely
claiming completion or clobbering a replacement. Tests inject failures before
rename, after rename, and after directory fsync; separate test exits an actual
child process after partial commit and resumes from the durable DB on reopen.
This is process-crash evidence, **not** power-loss/filesystem fault-injection proof.

## Explicit boundaries / missing work

- Linux requires openat2/renameat2 and a local filesystem supporting atomic
  NOREPLACE, locking, directory fsync and SQLite WAL. macOS and Windows now have
  real native backends but have **not been executed on this Linux host**. Windows
  is local NTFS/drive-letter only, rejects all reparse points; directory-flush
  reopen and FileRenameInformationEx availability must pass native runtime gates.
  Unsupported capability errors are fatal, never converted to successful sync.
- Windows/macOS require DB outside the grant (object-identity ancestor check),
  rather than unsafe lexical reservation for case/normalization/8.3 aliases.
  Linux retains its existing in-grant DB reservation and adds ASCII case rejection.
  New publication receipts retain full Windows64-bit volume +128-bit file ID or
  Unix device/inode. Existing schema1 Unix tuple receipts are still readable;
  older binaries do not understand new receipt objects (no downgrade claim).
- Granted media root, ancestors, DB path, staging/lock/marker must not be actively
  renamed/replaced/mounted by a hostile same-user/admin process. Open dirfds pin
  authority but cannot prevent a separately privileged actor relocating a granted
  directory. No OS sandbox against malicious same-UID interference, hardlink
  insertion during a write, bind mounts or database-file replacement. DB path is
  trusted application state, not a user-supplied media path. Prefer DB outside root.
- No groups, sidecars, batch atomicity, parallel workers, bandwidth scheduling,
  pool intent discovery, credentialed downloads, retries/backoff, queue reordering,
  cleanup UI, subscriptions/progress event stream or product integration.
- Synchronous fsync/hash/DB work is not throughput optimized; use a dedicated Tokio
  runtime/thread rather than GUI or shared latency-sensitive executor. Unknown
  lengths have no quota beyond i64 byte-count ceiling; integration must enforce
  storage quotas and user grant policy. Partial content tampered while stopped is
  detectable only when expected hash/publication digest exists.
- Read/list currently loads all jobs; no pagination or pruning. Schema version1 is
  this new crate's contract; no legacy importer. Root identity/DB marker relocation
  or missing/corrupt ambiguous publication requires explicit recovery, not repair.
- Native playback sandbox gate and entire app acceptance remain separate/incomplete.

## Historical Linux implementation receipts (preserved)

From repository root:

```sh
python rust/crates/furchive-downloads/scripts/check.py reviewer-test test --all-targets --locked
python rust/crates/furchive-downloads/scripts/check.py reviewer-doc test --doc --locked
python rust/crates/furchive-downloads/scripts/check.py reviewer-fmt fmt -- --check
python rust/crates/furchive-downloads/scripts/check.py reviewer-clippy clippy --all-targets --locked -- -D warnings
```

Runner sets existing Rust HOME/PATH, RUSTUP_AUTO_INSTALL=0, owned CARGO_HOME,
target/logs, 240s subprocess timeout. Initial runs01–41 inherited the existing
shared Cargo registry cache and downloaded missing registry packages there; this
was corrected to a crate-owned Cargo cache for42 onward. No toolchain install,
global configuration, model/provider or dependency install command. Nested
Cargo.lock is owned here. Logs
under ignored `test-artifacts/` retain real numbered RED/GREEN outputs. Initial
01/05/07 REDs are missing APIs; 03/09/11/13/15/17/19/21/28/31/33/35 are actual
behavior assertions (queue boundaries, restart,416,duplicates,crash,encoding,
schema,validator,record corruption,root namespace,portable names,DB collision).
Other edge regressions immediately passed the preceding generalized implementation;
they are additional verification, not claimed as separate observed RED cycles.
Initial fmt23/Clippy24 preserve real findings; corrections precede final checks.
`process_crash_child` is one counted harness test that is a no-op outside its
subprocess environment; its real assertions are driven by the parent process test.
No commits/staging/push, production, user media or sibling source changes.
Codex CLI absent from this worker; direct implementation/test tooling used.

Original worker final receipts: `56-final-fmt.log`, `57-final-clippy.log`, `58-final-test.log`
(**31 unit tests**, zero failed/ignored), `59-final-doc.log` (1 compiled README
example). One unit test is the explicitly documented subprocess harness entry;
its parent actually spawns it and requires exit77 plus durable resume. Runs50–52
repeat the then-current30-test suite three times cleanly. Runtime admission
RED43/GREEN44 and impossible-HTTP-total queue-poison RED53/GREEN54 extend the
behavior evidence. `python rust/crates/furchive-downloads/scripts/evidence.py`
validates final receipts, checks unique named inventory, and emits source hashes
in `test-artifacts/evidence.json`. These are implementation checks, not independent
approval; the existing dependent verifier remains the next lane.

## Historical verdict, owner repair, and current milestone

The original independent **DL-01 BLOCK** remains preserved in
`test-artifacts/verify-t_e10269a2-verdict.md`; it was not rewritten into a PASS.
The owner subsequently repaired maximum-length Rename queue poisoning, then
verified33 Linux tests at starting HEAD1367d403efe94799a4ae44ca643cfdd1fb6cb6f1
(repository receipt: `rust/docs/download-DL01-repair.md`, outside this crate's scope).
This task's new observed RED/GREEN receipts and exact commands/hashes are separate:
see [contracts/NATIVE.md](contracts/NATIVE.md), `contracts/test-inventory.json`,
and `contracts/source-manifest.json`. Neither repair nor portable source checks
constitute independent approval, native certification, desktop integration or
whole-app completion. Do not rerun the historical `scripts/evidence.py` as the
current inventory: it deliberately describes the original31-test snapshot.
