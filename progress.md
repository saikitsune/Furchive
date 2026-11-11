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
