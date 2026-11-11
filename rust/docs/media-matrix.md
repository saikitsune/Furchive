# Native media qualification

Status: NOT PASSED; no platform media support certified.

- Linux x64: Tauri 2.12.5 compile succeeds against extracted GTK 3.24.49/WebKitGTK 2.54.0; two Range unit tests pass.
- Runtime: WebKit helper paths do not relocate via WEBKIT_EXEC_PATH in this build. Local proot path binding allowed Xvfb/GTK startup, but WebKit did not finish loading; standalone bubblewrap reports denied namespace creation. Container policy blocks a faithful sandboxed WebKit qualification here. Sandbox was not disabled.
- Windows x64/macOS arm64+x64: not built/run yet.
- Test corpus generated locally: JPEG, PNG, GIF, APNG, WebP, VP8/VP9 WebM, H.264 MP4. No remote artwork.
- Gallery range calculation: 10,000 metadata entries, viewport-only range test passes. Native rendered DOM/frame-time/memory still NOT RUN.
- Qualification-only UI is clearly labeled engineering screen, not feature parity.

Next: use an authorized native desktop runner with WebKit sandbox support; do not equate Chrome tests or a compile with native media acceptance. Durable logs currently under rust/test-artifacts/native-media-* (ignored runtime evidence).
