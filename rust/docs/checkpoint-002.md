# Checkpoint 002 — settings, credentials, catalog parsing

Overall application remains INCOMPLETE. Full product UI is deliberately not built past the native-media gate.

## Verified local work
- Main Rust workspace: 42 named tests pass (23 foundation,12 e621/API/account contracts,7 app worker/settings/vault/legacy tests). Actual native secure stores NOT exercised; vault behavior tests use explicit memory and unavailable doubles.
- Non-secret Settings validates template/concurrency/cache/scale and stages same-directory atomic replacement; test proves rejected settings preserve old bytes.
- Explicit keyring-core provider adapters compile on Linux: Secret Service; target-gated Windows Credential Manager/macOS Keychain implementations remain unbuilt/unrun. No global keyring default-store mutation or plaintext fallback.
- Verified identity uses users/me.json and checks a positive ID plus matching configured username, proven by local HTTP mismatch→match test. Not yet integrated into desktop login lifecycle.
- Legacy chip type corruption now rejected instead of silently dropping invalid values; original settings stay unchanged.
- Separate furchive-catalog nested crate:5 tests cover multiline quoted CSV, authoritative post order, inflation/row limits, duplicate pools/members and invalid gzip CRC. Caller must stage a DB transaction and commit only on complete success; export download/transactional catalog integration remains pending.
- `tauri build --debug --bundles deb` produced an actual Linux qualification-screen Debian package: rust/apps/desktop/src-tauri/target/debug/bundle/deb/Furchive Rust Alpha_2.0.0-alpha.1_amd64.deb,73,165,456 bytes. `dpkg-deb --info` inspected metadata/dependencies. It is NOT a completed app or a successful native run/install test.
- Package build reports a production-only unused payload warning, tracked for fix after frozen review. No lint waiver was added.

## Active verification and freeze
- Frozen core/settings/credential/IPC snapshot under rust/test-artifacts/review-core-001 with98 source/input SHA256 entries; Nyx t_7e6bee16, read-only return t_5c43ec26. Reviewed inputs frozen; only catalog/this new note may change while review runs. Building desktop changed live desktop Cargo.lock by adding existing app dependencies; snapshot untouched and exception reported to reviewer.
- Download implementation t_179a90c5 completed Linux-only core;31 unit tests (one documented child harness) and1 compiled README example self-reported, read-only t_e10269a2 independently verifies. No integration or Windows/macOS download claim. Do not mutate download crate during verification.
- Notification routes read back to live tui/desktop20261111_100026_a9adfe, default notifier, notify+wake; no Discord.

## Remaining human/platform boundary
No reply to branch push/GitHub Actions permission request; no push or remote workflow created. Native media qualification remains blocked by prohibited container user namespaces, no sandbox weakening. A sandbox-capable native runner is required before committing the full UI/media choice. Continue independent core work only; Windows/macOS/download portability/signing and project license remain explicit gaps.

## Next integration
After exact snapshot reviews return, reproduce findings once, fix genuine issues with tests, reconcile locks, then commit a scoped verified milestone on rewrite/rust. Preserve the original C# source and old data. Native-media gate still blocks complete product/UI acceptance; a checkpoint artifact is not completion.
