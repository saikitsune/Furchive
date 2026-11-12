# Execution progress

## Initial checkpoint
- Approved rewrite implementation, local branch rewrite/rust in /opt/data/projects/Furchive.
- Original C# tree clean, original plan read.
- Loaded development/TDD/planning/design and handoff procedures.
- Native dependency gap found; no root escalation or production changes attempted.
- Overall project NOT COMPLETE.

## Foundation checkpoint
- Bram finished domain/storage foundation with 23 named tests and retained RED/GREEN logs. Live parent independently reran workspace: 26 tests passed including three new e621 mapping/query/request tests.
- Frontend Svelte check: zero errors/warnings; Vite production build succeeds. Grid viewport math test passed after observed RED.
- Tauri 2.12.5 native binary compiles, Range helper tests 2/2 passed. This is a qualification screen, NOT product UI.
- Native execution attempts and logs retained under rust/test-artifacts/native-media-*. Container forbids bwrap user namespaces; no sandbox relaxation. A suitable native test resource is needed before media gate closes.
- Scoped foundation return t_f73a945a completed read-only verification: all29 files and36 original receipts verified; independent23/23 tests/fmt/clippy pass. Parent owns root/e621/desktop.
- e621 client expanded to11 named tests (account metadata safety, logout headers, suggestions/pool order, request identity, bounded metadata, retry429, raw pagination); live identified-UA smoke returned2 safe records.
- Added bounded owned DB worker and tested shutdown/reopen user state; basic Tauri command wiring compiles, with explicit command permissions. Added read-only legacy saved-search preview that does not retain API keys or mutate old settings. Product UI remains qualification-only pending native media gate.
- Original download worker t_179a90c5 and verifier t_e10269a2 done. Original DL-01 BLOCK preserved; owner repair33Linux tests passes. New portable backend owner t_d5d44259 now owns ONLY rust/crates/furchive-downloads/** with dependent verifier t_b8082b2d; do not overlap ownership.
- Earlier push/Actions permission prompt timed out; Sai subsequently EXPLICITLY APPROVED rewrite/rust push + GitHub Actions. Accepted exact Furchive write invitation and verified push:true. CI admission details in rust/docs/ci-admission.md; no main/release/license authority.

## Native CI attempt 1
- Pushed6f9b390 to rewrite/rust; main untouched. Run43088440335 finished: macOS PASS including actual image/video play+seek and virtualized DOM; Windows missing required ICO; Linux custom-scheme video code4 while images/grid and all build/test gates pass.
- Corrections and RED/GREEN evidence in rust/docs/native-ci-attempt-1.md. Linux transport correction subsequently verified by attempt2; no sandbox or gate weakening. Full application still incomplete.

## Native runtime attempt2
- Run43091317909 at b1e5b40 PASS on all3 platforms; downloaded all3 receipts and independently validated. See rust/docs/native-runtime-milestone.md for exact scope/diagnostics. Basic runtime gate is not full packaged-app/media acceptance.
- Automatic continuation ran43093280146 at1367d40: all3 extracted payloads PASS. Owner verified downloaded package/executable hashes and native receipts, see rust/docs/extracted-package-milestone.md. No install/release/full app claim.

## Next active capability
- Portable durable download backend worker t_d5d44259 completed source-only; t_b8082b2d verified all24 frozen hashes, independently reran44 Linux tests+1doc/fmt/Clippy and3Python guard tests in an isolated exact-source copy. Sole download CI owner confirmed by live Desktop comment; current-only notify+wake binding read back. Existing workflow extended with a separate15minute three-OS native download matrix; native acceptance PENDING. See rust/docs/portable-downloads-verification.md. Existing profile model/provider preserved. Broader media acceptance and product UI still pending.
- Initial outage and Windows fixture failures preserved. Corrected7deb877/run43114513874 owner-verified all3 download artifacts: Linux44/macOS43/Windows45 named tests,1doc/fmt/strict Clippy each. Same verifier card t_b8082b2d closed bounded core milestone only; all6 download+GUI/package jobs now PASS at7deb877, downloaded package/binary/receipt checks verified; desktop product integration incomplete. Owner reader needed Windows path/universal-newline handling, raw evidence unchanged; details rust/docs/portable-downloads-native-milestone.md.
- Legacy nonsecret import t_f0939a14 and verifier t_d18d39a6 DONE:60e4c3b pushed, native run43119171777 all6jobs PASS; owner reran named-log audit78root/43app/34legacy-specific eachOS,15live/committed hashes and all3package/binary/native-receipt checks. Library-only, import UI not yet connected. Source packet rust/test-artifacts/legacy-verifier-t_d18d39a6/complete-updated-source.md.
- Media worker gateway owner failed twice; original worker+unused return archived/readback, source retained. Reynard took direct ownership, fixed missing modal focus wrap under existing4th DOM regression;12unit/4compiled DOM/4Rust tests, Svelte/Vite/native bothfeature Clippy PASS. Opt-in actual-frame host capture seam+extracted-package CI added, native result pending. See rust/docs/media-viewer-milestone.md. Full product still incomplete.

## Current native media checkpoint
- dc7d0c0/run43306961270: Linux/Windows native and all3download jobsPASS; owner independently validated both actual native runtime+36rawframe artifacts. macOS app runtime passes523msinitial/30–49msscroll/allvideo+cleanup; macOSjobFAIL on rawhostcapture cadence, not playback. Latest evidence in rust/docs/media-native-attempt-11.md.
- Hosted diagnosis43311871441/43312948998/43313601057 proves coarse long-sleep overshoot, narrow window enumeration not bottleneck, measuredclock/1msyield observed81–86ms for80ms target. Original failed probe43310755058 retained. Product unchanged; correct testhelper to pace from prior actualcapturecompletion, preserving all strict thresholds. New native qualification pending. No wholeapp acceptance.
- Full product UI/search/favorites/pools/download/settings/migration integration remains incomplete; platform architecture/Wayland/HiDPI/accessibility/audio acceptance not established. C# and main preserved.
