# Furchive Rust rewrite execution

## Goal
Working e621-focused Windows/Linux/macOS desktop app with Rust engine, Tauri 2 and Svelte/TypeScript. Approved by Sai in this conversation. Reference review: /opt/data/.hermes/plans/2026-11-11_100735-furchive-rust-migration.md. Branch rewrite/rust, baseline f395735d2e0441613a21205a99913ab29e1c7685.

## Constraints
Preserve old C# source, user data, art and history. No production/server changes, license choice, model/provider changes, main merge or release publication without approval. Test-first behavior slices; real artifacts/tests required. Read scoped repository AGENTS.md if present. Keep milestone-scale progress and honest platform gaps.

## Phases
- [complete] 0: Durable execution records and scoped tooling; unprivileged native SDK recovered (runtime sandbox remains blocked).
- [in_progress] 1: Basic native image/video play+seek and10,000-item/40DOM gallery proof PASS on three hosted OS runners at b1e5b40. Extracted Debian/macOS.app/Windows portable payload proof also PASS at1367d40; animation/lifecycle/performance/platform breadth gates remain before full UI commitment.
- [in_progress] 2: Domain/storage foundation 23 tests passes; e621 mapping/request foundation 3 tests passes. Full API behavior pending.
- [in_progress] 3: Domain/API/download/legacy product integration verified in bounded native campaigns; downloads now durable1–4workers, page/wholepool pausedgroups and observed progress. Metadata sidecars and broader storage acceptance remain pending. Historical library proofs retained in progress.md.
- [in_progress] 4: Product UI/IPC integrated with packaged search/media/settings/legacy/account/download/group contracts; whole-pool native25+8/runtime proof atbc7cf3d/run43343132832 owner-verified; metadata and platform breadth acceptance open.
- [pending] 5: Independent milestone review, native build/package matrix, release readiness (human release approval).

## Current ownership
Latest owner-verified implementation candidatebc7cf3d/run43343132832: nine jobs/artifacts checked,25+8 native UI/restart perOS,108hostframes; whole-pool queue/ordinals/offpagebytes and sampler-reset regression pass. Earlier4c7290a independently reviewed PASS with retained presentation-only MINOR, now owner-repaired/tested. Current owner slice: gallery/mute/duplicate/residentcache preference behavior; local tests pass, fresh native26+9 campaign pending. Remaining metadata sidecars, paired real-vaultUI, crosspageviewer and platformbreadth/final review. Historical ownership details below are retained, not current proof.
Reynard directly owns media/gallery and native capture integration after two worker gateway-EOF failures. Old t_3b58f344 and t_7a9f8319 both archived/read back; no live duplicate worker.12unit+4compiled DOM+4Rust tests and strict frontend/native gates pass locally; extracted native campaign pending. Full application incomplete.

## Errors / unresolved boundaries
- Unprivileged GTK/WebKit SDK recovered and native package builds; container denies WebKit sandbox namespaces. Sai authorized rewrite branch push/native Actions to recover runtime proof.
- Native hosted macOS/Windows/Linux basic runtime proof established by run43091317909. Extracted package basic runtime proof established by run43093280146. Signing/notarization, installers and broader media acceptance remain pending; no release authority inferred.
- Project LICENSE absent: leave license choice pending, not an implementation blocker.
- Original outage run43098258533 and Windows fixture failure43113611486 preserved. Corrected run43114513874 all6download+GUI/package jobs PASS; owner verified artifacts/contained binaries/receipts on allthree platforms. No full-app or all-filesystem/CPU claim.
