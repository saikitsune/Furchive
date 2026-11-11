# Native runtime milestone — attempt 2 verified

Source: b1e5b40f765a11b464b9a7128702f8ff007909f3
Run: https://github.com/saikitsune/Furchive/actions/runs/43091317909
Result: success, all three jobs. Owner re-read exact run/head and downloaded all three diagnostic artifacts without forwarding GitHub credentials to the storage redirect. Independently reran the repository receipt validator against each downloaded native JSON.

| Platform | Actual engine | Verified runtime scope |
|---|---|---|
| ubuntu-24.04 | WebKitGTK | Five images decode, VP8/VP9/H264 play and seek; gallery10,000 records/40mounted tiles at20,000px scroll |
| windows-latest | WebView2 | Same five-image/three-video/virtualized-DOM contract |
| macos-15 | WKWebView | Same five-image/three-video/virtualized-DOM contract |

Artifacts: Linux13889861378, macOS13888483495, Windows13888354191. Retained exact JSON/logs: rust/test-artifacts/ci-run-43091317909/{Linux,macOS,Windows}. All three videos per platform advanced past the seek target to approximately0.75s. No browser surrogate or sandbox disabling used.

## Explicit limits

This is a native **debug executable** qualification, not yet an installed or extracted distribution. GIF/APNG load is verified, animated frames are not.10,000 records/40mounted DOM nodes proves virtualization shape, not FPS, decoded-memory ceilings or large/corrupt-image handling. Muted playback does not establish audible output. Linux Xvfb logs retain missing accessibility bus, GPU/GL sink and audio-device warnings; Windows retains a shutdown class-unregistration error. These diagnostics remain open for their relevant acceptance scope, not silently discarded. Linux Wayland, macOS both CPU architectures, fullscreen/speed/keyboard/high-DPI/lifecycle/memory and real vault remain separate gates. Full Furchive features are not yet connected to the product UI.

## Automatic continuation: extracted packages

Extend the same bounded native CI workflow to build Debian and unsigned macOS.app payloads and a clearly labeled Windows portable ZIP. Re-extract the exact delivered artifact and launch **that executable** through the unchanged runtime receipt validator. Store package/binary SHA256, actual runtime result, and5day qualification artifacts. No installer execution, production install, signing/notarization, release publication, license choice or main merge. Packaging scripts refuse stale output and inspect closed payload layouts before launch. A local Debian package built/extracted successfully; local receipt explicitly says NOT RUN because this container cannot safely run sandboxed WebKit. The new CI run must supply the extracted-runtime proof.

Do not promote the basic runtime milestone to the full media/packaging acceptance gate or begin the full product UI rewrite on that mistaken basis.
