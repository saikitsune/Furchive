# Native media qualification matrix

## Latest fully inspected packaged implementation checkpoint

**c007fa5d5bfd8d073233b61c3d741315a476bb4d / Actions43418057594** passed bounded extracted-application qualification on three hosted OSes. This supersedes the original spike's unrun-host status below; it does not establish final product/release support or qualify later revisions.

| Host runner / webview | Extracted payload | Media result | Initial / max scroll | Host frames |
|---|---|---|---|---:|
| ubuntu-24.04 / WebKitGTK | Debian filesystem | PASS bounded | 540 / 96 ms | 36 |
| windows-latest / WebView2 | portable ZIP (system WebView2 required) | PASS bounded | 54.9 / 31.8 ms | 36 |
| macos-15 / WKWebView | unsigned .app archive | PASS bounded | 661 / 99 ms | 36 |

Unchanged gates: initial <=1000ms, scroll sample <=250ms, mounted cells <=108 with10,000uniqueIDs. Actual animation progression, video play/seek/mute/speed, corrupt/oversized media refusal and20cleanup cycles were checked. Owner verified all nine actual provider ZIP digests, contained package/executable hashes, source fingerprints, account cleanup and108host PNGs. Product31+13restart and real native-account6checks per OS passed separately from media. Evidence: `rust/test-artifacts/ci-run-43418057594/owner-report.md` and JSON receipts (ignored local artifact directory; hosted artifacts have finite retention).

Prior combined run43390505171 remains FAILED: macOS initial1151ms exceeded unchanged1000ms gate despite browse/account passing. Startup repair eliminated a discarded BrowsePanel mount and its queued IPC, followed by the fresh successful measurement above; sole historical causality and arbitrary machine-load performance are not claimed.

## Scope limits

- These engineering builds use real system webviews but self-test fixture routes. Actual default installed application/upgrade/uninstall/signature/notarization acceptance is separate.
- Linux container's native GUI remains UNRUN under namespace sandbox policy; it was never disabled. Local SDK/package/Clippy success is not hosted GUI proof.
- Linux Wayland versus X11, HiDPI/multimonitor, screen reader, audible audio/GPU/resource-soak and architecture breadth remain open. Platform name alone is not both macOS arm64 and x64 proof.
- Windows shutdown1412 and Linux accessibility/ALSA/EGL/GLsink diagnostics remain retained; bounded muted playback is not clean teardown/audio/accessibility release approval. Windows filesystem contracts are scoped to local NTFS.
- Filename-preview, patched SQLite, cache clear and opt-in remembered controls passed their fresh exact-head campaigns. Current macOS payload is thin arm64, not x64/universal proof. No historical receipt accepts a later source revision.

## Original spike record (historical, not current support table)

The original Linux proof compiled Tauri2.12.5 against extracted GTK3.24.49/WebKitGTK2.54.0 with Range unit tests. Local WebKit namespace creation was denied and Windows/macOS were then unrun. That evidence was correctly NOT PASSED at the time; subsequent hosted qualifications supply the narrower positive evidence above. Git history and original logs are preserved.

Harmless generated corpus includes JPEG/PNG/GIF/APNG/WebP, VP8/VP9WebM andH.264MP4; do not infer every container/codec/animation control from a corpus filename. Executed closed runtime/capture inventories define actual coverage.
