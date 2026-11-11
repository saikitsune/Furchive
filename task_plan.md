# Furchive Rust rewrite execution

## Goal
Working e621-focused Windows/Linux/macOS desktop app with Rust engine, Tauri 2 and Svelte/TypeScript. Approved by Sai in this conversation. Reference review: /opt/data/.hermes/plans/2026-11-11_100735-furchive-rust-migration.md. Branch rewrite/rust, baseline f395735d2e0441613a21205a99913ab29e1c7685.

## Constraints
Preserve old C# source, user data, art and history. No production/server changes, license choice, model/provider changes, main merge or release publication without approval. Test-first behavior slices; real artifacts/tests required. Read scoped repository AGENTS.md if present. Keep milestone-scale progress and honest platform gaps.

## Phases
- [complete] 0: Durable execution records and scoped tooling; unprivileged native SDK recovered (runtime sandbox remains blocked).
- [in_progress] 1: Basic native image/video play+seek and10,000-item/40DOM gallery proof PASS on three hosted OS runners at b1e5b40. Extracted Debian/macOS.app/Windows portable payload proof also PASS at1367d40; animation/lifecycle/performance/platform breadth gates remain before full UI commitment.
- [in_progress] 2: Domain/storage foundation 23 tests passes; e621 mapping/request foundation 3 tests passes. Full API behavior pending.
- [in_progress] 3: Portable download source t_d5d44259 complete; t_b8082b2d independently reproduced44 Linux tests+1doc/fmt/Clippy and owns bounded three-OS native CI. Native Windows/macOS acceptance and desktop integration remain pending. Full migration pending.
- [pending] 4: Product UI and IPC integration.
- [pending] 5: Independent milestone review, native build/package matrix, release readiness (human release approval).

## Current ownership
Reynard owns environment/native UI proof and integration. Specialists receive bounded file ownership and an automatic return task, never overlapping modifications. Whole project remains incomplete until full acceptance.

## Errors / unresolved boundaries
- Unprivileged GTK/WebKit SDK recovered and native package builds; container denies WebKit sandbox namespaces. Sai authorized rewrite branch push/native Actions to recover runtime proof.
- Native hosted macOS/Windows/Linux basic runtime proof established by run43091317909. Extracted package basic runtime proof established by run43093280146. Signing/notarization, installers and broader media acceptance remain pending; no release authority inferred.
- Project LICENSE absent: leave license choice pending, not an implementation blocker.
- Download native CI43098258533 at2992d62 remains externally queued, zero jobs/artifacts after GitHub Actions incident dvy0t3mvh1lg/recovery backlog. Source/Linux gate verified; native Windows/macOS actual contracts still mandatory. Same-card owner continuation, not completed portable support.
