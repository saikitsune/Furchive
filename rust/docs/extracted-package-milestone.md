# Extracted desktop packages — verified milestone

Source commit: `1367d403efe94799a4ae44ca643cfdd1fb6cb6f1`
Run: https://github.com/saikitsune/Furchive/actions/runs/43093280146
Result: all three native jobs succeeded, including launching each actual extracted payload.

The owner downloaded all three artifacts, recomputed each package SHA256, read the executable from the package archive and matched its SHA256 to the recorded executed binary, then independently ran `validateQualification` against each extracted-package `native.json`. An initial owner archive-reader attempt used POSIX separators for the Windows receipt and failed to locate the ZIP member; corrected the reader to use `PureWindowsPath` for that native path. This was an owner inspection-path failure, not an application failure; no package bytes or runtime results changed.

| Platform / payload | GitHub artifact ID | Artifact SHA256 |
|---|---|---|
| Linux / `Furchive Rust Alpha_2.0.0-alpha.1_amd64.deb` | 13889828034 | `7c0987ec68b4c2840601161ebc4df2d2849df7c8ed01fed2584368847ee3a150` |
| macOS / `Furchive-qualification-macOS.tar.gz` containing unsigned `.app` | 13889833446 | `bf1812ce514ca6bc53ab92d1b55805e870cb63df76a772042900e5ea9601bcb9` |
| Windows / `Furchive-qualification-Windows.zip` portable executable | 13890920546 | `ce3a612e6fb4e3061483d6ccfa4a84ad8ba82e0817ee9cb4b855c4f8e6425df7` |

## What passed

For each actual extracted executable: JPEG/PNG/GIF/APNG/WebP image decode; VP8/VP9/H264 muted video play, seek to0.5seconds and subsequent progression to approximately0.75seconds;10,000 gallery records with40mounted DOM tiles after20,000px scroll. Core/catalog/frontend/native tests and strict quality gates also passed; Linux download integrity contracts passed separately. No sandbox disabling, real credentials, user artwork or live API/media used by this qualification.

Owner evidence: `rust/test-artifacts/ci-run-43093280146/owner-package-verification.json`, platform directories contain the exact downloaded packages and native/package JSON/logs. GitHub retention5days; locally preserved under project-owned test artifacts. These are engineering self-test packages and explicitly `release_ready:false`, not user-facing finished Furchive distributions.

## Acceptance limits still open

- No installer execution/integration, signing/notarization, Gatekeeper/SmartScreen or clean-machine system-runtime provisioning claim. Windows portable ZIP needs WebView2; Linux/macOS used hosted runners with build dependencies already present.
- GIF/APNG first-image decode does not prove animated-frame progression. No FPS/decoded-memory ceiling, long lifecycle, large/corrupt input, zoom/pan/fullscreen/speed/keyboard/high-DPI or audible-output proof.
- Linux X11/Xvfb, one hosted macOS architecture and Windows hosted target only; no Wayland/all-architecture certification. Preserve previously recorded accessibility/GPU/audio/shutdown diagnostics.
- The visible app remains the explicitly labeled qualification UI, not feature parity. Search/pools/downloads/settings/migration integration is not completed.
- Portable download filesystem backend is a distinct next milestone. Current shipped download crate still only guarantees Linux; do not infer Windows/macOS download support from the GUI/media green matrix.

## Existing authorized next lane

Bram implementation `t_d5d44259` owns only `rust/crates/furchive-downloads/**`: capability-safe Windows/macOS backends while retaining existing Linux contracts. Default verifier/continuation `t_b8082b2d` depends on that implementation and owns exact native CI integration and return. Both finite3600second caps, current Desktop notify+wake bindings verified; no duplicate reviewer graph. Parent owns desktop/media and overall integration independently. Full media gate remains open before the full product UI rewrite; full application remains incomplete. No new user permission is required for this already-authorized source/test work. No main merge, release publication or license choice performed.
