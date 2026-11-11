# Furchive SQLite foundation

Reusable **synchronous** storage, with bundled SQLite and no desktop dependencies.
Call from an owned DB thread / blocking task, not directly on a GUI/async executor.
No network, downloads, legacy importer, DB actor or hidden background tasks here.

## Public API

All below are exported by `furchive_engine::storage` and return `Result<T,
StorageError>`:

```text
CacheStore::{open(path), in_memory(), from_connection(rusqlite::Connection)}
  upsert_posts(&mut self, &[MediaItem]) -> ()
  get_post(&self, &PostKey) -> Option<MediaItem>
  reconcile_pool(&mut self, &Pool) -> ()
  get_pool(&self, &PoolKey) -> Option<Pool>
  pool_entries(&self, &PoolKey) -> Vec<PoolEntry>
  clear(&mut self) -> ()

PoolEntry { key: PostKey, position: u32, post: Option<MediaItem> }

StateStore::{open(path), in_memory(), from_connection(rusqlite::Connection)}
  save_search(&mut self, &SavedSearch) -> ()
  saved_searches(&self) -> Vec<SavedSearch>     # stable ID order
  delete_search(&mut self, &str) -> ()         # idempotent
  set_pin(&mut self, &PoolKey, bool) -> ()     # idempotent
  pinned_pools(&self) -> Vec<PoolKey>          # source/ID order

SavedSearch { id: String, name: String, query: SearchQuery }
```

Use **different local-disk paths** for cache and user state; caller authorizes paths
and creates parent directories. Application IDs reject accidental mixed-store
opens. `clear()` only clears cache metadata; pins/searches need no cached pool.
URLs are metadata, not trusted fetch permissions. No account-dependent cache
isolation is implemented; do not persist private visibility results until the
account-scoping layer exists.

## Invariants

- Composite `(source, post_id)` and `(source, pool_id)` identities.
- Atomic post batches and authoritative whole-pool snapshot replacement.
  Equal-length reorder/replacement, removal and empty snapshots all reconcile.
  Other pools/sources remain untouched. Duplicate/invalid IDs reject the snapshot.
- `pool_posts` has unique member/position and parent-pool foreign key. Positions
  are zero-based. Missing post metadata does not remove/compact membership.
- Validated JSON serialization preserves normalized media/categories. Reads reject
  malformed payloads and metadata identities that disagree with SQL keys rather
  than returning a foreign post or silently skipping a bad row.
- Separate cache/state SQL migrations, `SCHEMA_VERSION=2`, application IDs exported
  as `CACHE_APPLICATION_ID` / `STATE_APPLICATION_ID`. Immediate transaction covers
  every pending migration and version marker; failure rolls the whole upgrade
  back. Version 2 adds indexed fetched timestamps to cache and record version 1 to
  saved searches. Never edit historical SQL to implement a later migration.
- Local-file WAL, full synchronous commits, foreign keys, five-second busy timeout.
  Future schemas, foreign application IDs and nonempty unversioned legacy DBs
  error; explicit read-only legacy import is future work, not silent adoption.
- Saved-search query payloads are validated JSON objects with an explicit supported
  record version. No keys/secrets/settings are stored. No schema repair of a
  database falsely claiming the current version is attempted.

## Verification and retained evidence

After the parent creates root Cargo.lock, from the repository root:

```sh
export HOME=/opt/data/home PATH=/opt/data/home/.cargo/bin:$PATH RUSTUP_AUTO_INSTALL=0
cargo test --manifest-path rust/Cargo.toml -p furchive-domain -p furchive-engine --locked
cargo fmt --manifest-path rust/Cargo.toml -p furchive-domain -p furchive-engine -- --check
cargo clippy --manifest-path rust/Cargo.toml -p furchive-domain -p furchive-engine --all-targets --locked -- -D warnings
```

`python rust/crates/furchive-engine/scripts/check.py LABEL <cargo args...>` copies
only the two owned crates into an ignored workspace, retaining numbered real logs,
its own lockfile and target directory under `test-artifacts/`; no root lock writes.
Set `FURCHIVE_CHECK_ROOT=1` to execute against the real root instead (use `--locked`
and select the two packages to avoid racing ongoing sibling development).
The runner has a 240-second subprocess timeout and explicitly disables toolchain
auto-install. Do not invoke it concurrently against its shared mirror directory.
Generated logs/build outputs/databases are ignored, tests/source are not.

Logs `01`–`27` show successive RED/GREEN slices (initial missing APIs plus explicit
behavior assertion failures); `29`/`30` preserve real Clippy findings corrected in
`31`. `32`/`33` are final isolated fmt/test; `34`–`36` are actual-root test/clippy/fmt.
`python rust/crates/furchive-engine/scripts/evidence.py` summarizes the retained
actual-root test inventory and hashes the owned deliverable files. This is
implementation evidence, not an independent reviewer approval or cross-platform
certification. Linux execution only; Windows/macOS/native media remain downstream.

Initial environment exception: first Cargo invocation inherited the owner's newly
pinned `rust-toolchain.toml` and rustup auto-installed that named toolchain despite
successful default `cargo --version`. No install command was issued; this was
reported on the card and subsequent runner calls set `RUSTUP_AUTO_INSTALL=0`.
No provider/model/fallback changes, commits, staging or remote operations occurred.
