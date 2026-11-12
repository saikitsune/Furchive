# Bounded three-OS native viewer/gallery milestone

Exact candidate: 4d78636982e0b8930a7bb1be434c7a9a6ba6d3fa.
Run: https://github.com/saikitsune/Furchive/actions/runs/43313955868 . All six native/download jobs completed SUCCESS. Original failing runs remain retained, not rewritten.

Owner independently retrieved all three provider artifact ZIPs, verified their SHA256 against GitHub metadata, inspected actual nested Debian/portable Windows/macOS app payloads, extracted the exact executable bytes and matched them to package and raw-capture receipt hashes. Owner reran `validateMediaEvidence` and `media-validate-capture.py::validate` over actual receipts and all 36 PNGs per platform. All passed. No browser surrogate counted as native.

| Platform | Actual package SHA256 | Initial render, 10000 items |
|---|---|---|
| Linux | `061cd4ce627847af5bf4d6fe0d8db79d84e3dae599b1bb8171aeb8a92b9a8c05` |408ms|
| Windows |`1eb8ebef138d54b8688bb81b5b53f2e05fb918bb4b4da6a17d25af2008680eb2`|59.6ms|
| macOS |`7369ad470e318dea96d083b5b8769c15425c05c78c69e4934114d7a5bd3ccb0e`|688ms|

Linux artifact `Furchive Rust Alpha_2.0.0-alpha.1_amd64.deb`; Windows `Furchive-qualification-Windows.zip` requires installed WebView2; macOS `Furchive-qualification-macOS.tar.gz` contains unsigned complete app. These are self-test engineering payloads, not finished product releases. Receipt/readback files retained under `rust/test-artifacts/ci-run-43313955868`.

Each platform: GIF/APNG/WebP actual host pixel-state progression; image/corrupt/oversize UI refusal; three-codec decode/play/seek/speed/mute; 20 release/stale-load cycles; stable keyboard selection; bounded gallery DOM; exact client geometry. macOS actual capture intervals95.4–128.1ms after paced-completion fix, within unchanged30–200ms gate. This proves bounded tested media behavior and permits product UI integration; not universal frame-rate, memory, audio or accessibility qualification.

Remaining broader platform gates: Linux Wayland, macOS both architecture claims separately, HiDPI/multidisplay, audible output/full audio controls, fullscreen, deeper animation disposal corpus and memory/CPU/cache soak, comprehensive keyboard/accessibility. Product search/favorites/pool/download/settings/migration workflows remain unfinished, and no production/release acceptance is claimed.
