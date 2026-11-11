# Native CI attempt 1 and bounded recovery

Commit 6f9b390935791b4370abee2d063e169071c51cb4, run https://github.com/saikitsune/Furchive/actions/runs/43088440335.

- macos-15: PASS, including actual WKWebView receipt. Five images decode320x200; VP8/VP9/H264 each play and seek to0.5 then advance to~0.75s.10,000items render40mounted tiles after20,000px scroll. This is NOT animation-frame/performance/product acceptance.
- Windows: core/catalog/frontend pass, native build stops because icons/icon.ico is absent. Added real multi-size ICO derived from existing project PNG. Preflight test observed missing-file RED then valid256px embedded PNG GREEN; workflow runs it before expensive builds.
- Ubuntu24.04: core/catalog/download/frontend/native locked compile+tests+Clippy pass. Runtime image/grid pass; all three videos return MEDIA_ERR_SRC_NOT_SUPPORTED4. No sandbox change, no assertion weakening.

## Transport evidence and correction

The first receipt uses /fixtures/video against bundled tauri://localhost. Upstream maintainer explanation matches the observed failure:
https://github.com/tauri-apps/tauri/issues/3725#issuecomment-1747970925 (no URI handler for tauri)
https://github.com/tauri-apps/tauri/issues/3725#issuecomment-2147476960 (HTTP for large files)
https://github.com/tauri-apps/tauri/issues/3725#issuecomment-5153873042 (canPlayType/fetch pass, custom-scheme code4, loopback restores playback).
These support a hypothesis, not proof of corrected Linux playback. Attempt2 native receipt is required.

Add a Rust, IPv4-loopback-only ephemeral-port transport serving ONLY embedded vp8/vp9/h264 fixtures.256bit per-launch OS-random capability prefix, exact Host validation, foreign Origin rejection, no CORS grant, GET/HEAD only,8KiB request limit, bounded read/write deadlines, existing exact Range semantics, joined shutdown. No remote fetch, filesystem paths, API/account/IPC exposure, sandbox disabling, blob copies or wildcard file scope. Preserve base CSP, add only this launch's exact origin to media-src in context. Frontend requests URL via explicit bundled-only IPC permission and uses same transport in visible fixture viewer and automation. No broad wildcard CSP change. Production media serving requires a separate granted-media design/review; fixture server does not claim that scope.

New real TCP range/security test observed connection-refused RED then exact4byte range/HEAD/foreign-origin/traversal/missing-capability/method negatives GREEN. Both native feature Clippy gates pass after rustfmt/test-module ordering cleanup. Frontend4tests and0error/0warning Svelte check/build pass. All initial failure artifacts retained ignored under rust/test-artifacts/ci-run-43088440335. Overall application still incomplete, replacement media transport not natively proven until new CI run.
