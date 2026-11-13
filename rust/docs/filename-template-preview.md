# Filename-template preview slice

The Preferences panel has an explicit Preview filenames button. It renders both unsaved ordinary and pool templates through the same Rust domain parser/renderer used by download intents. Sample values are fixed and disclosed (e621, sample_artist, 12345, Sample artwork, Sample pool, page7, png). Outputs are illustrative relative paths, not filesystem grants, promised final names or reservations. Actual metadata and duplicate Rename may differ. No settings save, native picker, filesystem read/write, credential or network action is performed by preview.

The host command is registered in the Tauri app manifest, handler list and bundled-main product capability. It accepts exactly two strings; domain validation bounds template/render paths and refuses absolute/drive/UNC/traversal/badtoken inputs. Only escaped text is shown. Results/errors are fenced against template edits, new preview, preference request start AND acknowledged preference replacement, and unmount.

## Retained correction
Original stagedtree dbe02b66 / archive911704a0 source review BLOCKED on MAJOR NYX-TP-01: successful Load/Save receipt replaced inputs without invalidating previews requested while that operation waited. Nyx independently reproduced8variants (Load/Save x success/error x both receipt orders), no filesystem/network/credential escalation. Owner reproduced4fails/2unmountpasses with unchanged reviewer cases then invalidated preview at successful preference replacement. Canonical component suite now includes all8ordering and2unmount controls,67tests total. Originalreview/archive/probes/defaultDebian package remain unchanged; its build did not approve stale UI behavior.

Local corrected frontend67component15unit/Svelte0errors0warnings/buildPASS; original pureRust3previewtests/138workspace6catalog9desktopRust/default+allfeatureClippyPASS. Corrected source re-review and actual new packaged native30+11 checks remain pending. Existing6ecfaf2/run43393039602threeplatform evidence is historical baseline only, not approval of this feature. No full-app/release completion claim.

## Independent advisory work
Owner cargo-audit scan foundRUSTSEC-2026-0459 in baseline bundledSQLite3.50.2/libsqlite3-sys0.35.0. Isolated milestone/sqlite-advisory upgrade resolves0.39.1/SQLite3.54.1 and preserves checks. It is not yet integrated here; do not call current release dependency-audit clean. Devonly deepmergeLOW/GLibunsoundness/unmaintained warnings remain separately inventoried. No advisoryignore or safety weakening.
