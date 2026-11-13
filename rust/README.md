# Furchive Rust rewrite — development alpha

A reusable Rust engine with a Tauri 2 / Svelte / TypeScript desktop adapter, currently e621-only. **Not a finished replacement or public release.** Original C# source, history and existing collections remain preserved; no automatic migration or download resume.

## Current implemented behavior

Search/include-exclude tags/ratings/sort, suggestions, saved searches, favorites browsing, ordered pools/pins/whole-pool paused groups, virtualized galleries, scoped image/animation/video viewing and cross-page navigation, persistent safe downloads, duplicate policies/templates, applied preferences, explicit selected legacy saved-search/pin migration, protected account verify/save/explicit restore/logout, verified local completed-media viewing and opt-in normalized JSON sidecars are integrated. Filename-template sample preview has passed corrected source review; its fresh packaged-platform campaign is still pending.

The last owner-verified extracted-app checkpoint is implementation **6ecfaf2**, Actions **43393039602**: all nine jobs/artifacts, 29 first-run + 11 restart product checks and six native account-UI checks per OS, and 108 independently checked host media frames. This is bounded engineering evidence, not installed release/platform breadth acceptance. See [media matrix](docs/media-matrix.md), [parity and remaining work](docs/parity.md), `progress.md` and `task_plan.md` at repository root. Later source changes do not inherit that acceptance.

## Layout

- `crates/furchive-domain`: portable query/rating/identity/media/template contracts.
- `crates/furchive-engine`: separate versioned SQLite metadata cache and durable user state.
- `crates/furchive-e621`: identified-UA reqwest/rustls boundary, shared pacing/bounded retry, search/pool/tag mapping and header-only API credentials.
- `crates/furchive-app`: bounded DB actor, accounts, validated preferences, browse cursor, current-generation media authorization, local-reader and durable-download integration, conservative legacy importer.
- `crates/furchive-downloads`: persistent safe partial/resume/ACK/atomic publication, native directory identities, bounded parallel workers, groups and normalized sidecars. Its standalone test gate retains a closed named platform inventory.
- `crates/furchive-catalog`: separately tested bounded gzip/CSV catalog ingestion.
- `apps/desktop`: bundled Svelte product UI and thin validated Tauri intents; no generic shell/fetch/file IPC.

## Development commands

Pinned Rust 1.99.1 and Node 24, npm, Python 3 and native C/toolkit dependencies. Linux builds require GTK3/WebKitGTK4.1 development libraries; end-user system webview/codecs remain platform prerequisites.

```sh
cargo test --manifest-path rust/Cargo.toml --workspace --all-features --locked
cargo fmt --manifest-path rust/Cargo.toml --all -- --check
cargo clippy --manifest-path rust/Cargo.toml --workspace --all-targets --all-features --locked -- -D warnings
cargo test --manifest-path rust/crates/furchive-catalog/Cargo.toml --locked
python rust/crates/furchive-downloads/scripts/native_check.py
npm ci --prefix rust/apps/desktop
npm run --prefix rust/apps/desktop check
npm test --prefix rust/apps/desktop
npm run --prefix rust/apps/desktop test:components
npm run --prefix rust/apps/desktop build
cargo test --manifest-path rust/apps/desktop/src-tauri/Cargo.toml --all-features --locked
cargo clippy --manifest-path rust/apps/desktop/src-tauri/Cargo.toml --all-targets --all-features --locked -- -D warnings
```

Build frontend before compiling the adapter: Tauri embeds `dist`. Normal default build from `rust/apps/desktop`: `npm run tauri -- build`. `self-test` and fixture features are engineering-only, not ordinary production distribution. Workflow `.github/workflows/rust-qualification.yml` runs actual native extracted application campaigns on the three hosted OSes.

No remote artwork or real credentials are bundled, no telemetry, and no project license has been selected. App identifier/data root is distinct from C#. Optional `scripts/native-env.sh` accepts an unprivileged extracted SDK; it is neither runtime bundling nor permission to weaken a sandbox. This agent container still cannot faithfully run sandboxed WebKit, so local compile/package output is not GUI proof.

## Remaining acceptance

Actual default installed builds/install-upgrade-uninstall/data retention, supported architectures/display stacks/accessibility/audio/GPU, real locked or denied protected-store prompts, explicit session/privacy controls, broader migration and final whole-product review remain open. The dependency scan found RUSTSEC-2026-0459 in baseline SQLite; a source-reviewed correction to SQLite3.54.1 is now integrated and locally verified, pending fresh native qualification. GLib unsoundness/unmaintained and dev-only LOW advisory warnings are retained separately. Do not describe the alpha as dependency-audit clean, fully supported, or ready to replace C#.
