# Furchive Rust rewrite — implementation checkpoint

Not a finished application or release. C# remains available unchanged.

## Layout
- `crates/furchive-domain`: tested query/rating/identity/media/template contracts.
- `crates/furchive-engine`: separate versioned SQLite cache/user stores.
- `crates/furchive-e621`: reqwest/rustls identified-UA search, bounded JSON, request pacing/retry, pool/tag mapping and suggestions. Credentials are header-only and never serialized; native secure storage and verified login remain pending.
- `crates/furchive-app`: bounded SQLite worker, so synchronous DB I/O is not on GUI/async executor.
- `apps/desktop`: compilable Tauri2/Svelte qualification screen and basic engine IPC. **Not product parity.**
- `crates/furchive-downloads`: separate active worker-owned crate, not integrated until acceptance.

## Commands
Rust1.99.1, Node/npm, C compiler. Standard Linux Tauri builds need GTK3/WebKitGTK4.1 development packages. End-user native dependencies/platform codecs must be qualified.

```sh
cargo test --manifest-path rust/Cargo.toml --workspace --locked
cargo clippy --manifest-path rust/Cargo.toml --workspace --all-targets --locked -- -D warnings
cargo fmt --manifest-path rust/Cargo.toml --all -- --check
npm ci --prefix rust/apps/desktop
npm run --prefix rust/apps/desktop check
npm test --prefix rust/apps/desktop
npm run --prefix rust/apps/desktop build
cargo test --manifest-path rust/apps/desktop/src-tauri/Cargo.toml --features self-test,custom-protocol --locked
```

A normal `npm run tauri build` is launched from `rust/apps/desktop`. The self-test feature is qualification-only, not for public distribution. Python/Pillow/ffmpeg regenerate the original harmless test media with `python rust/scripts/generate-media-fixtures.py`.

No remote artwork is bundled, no telemetry, no assigned project license yet. The new app identifier/data root is distinct from C# to prevent accidental data migration. The optional `rust/scripts/native-env.sh` accepts FURCHIVE_SYSROOT for an unprivileged extracted SDK; this is a local build aid, not a runtime-bundling or sandbox bypass.

## Verification limits
Read `docs/media-matrix.md`: Linux native compile passed, but this agent container prohibits bubblewrap namespaces and no sandbox was weakened. Native media execution plus Windows/macOS qualification, full UI, credentials/importer/download integration remain incomplete. Do not describe a build as supported until actual acceptance passes.
