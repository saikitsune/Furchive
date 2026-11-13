# Whole-pool paused admission and progress reset

Owner implementation builds on exact4c7290a native/review checkpoint. New candidate native qualification pending; local canonical116workspace/54default+55fixturecore+1doctest/7nativeRust/23DOM+14unit,fmt/strictClippy/check/build PASS. Whole application NOT COMPLETE.

## Behavior
Explicit Queue whole pool intent requests one authoritative e621 pool snapshot and batches at most100 member IDs. AccountSession holds the existing account lock through metadata acquisition and paused queue commit, so a login/logout cannot mix identity across batches. Missing/account-filtered or rating-excluded posts remain holes at original ordinals. No filter_id override, fallback endpoint, implicit grant, or auto Resume. Limit10,000 members; larger pool fails before member requests. Any batch error aborts before queue mutation. Empty/no-available pool yields a refused empty group, not fake download success.

The core offers separate bounded100-entry page and10,000-entry whole group entrypoints sharing one validate-all/SQLite transaction implementation. Service validates complete contiguous snapshot coverage and authoritative membership before whole admission. Jobs start Paused, share durable random identity/label, retain positions and use original one-based filename ordinals. Existing per-child acknowledged controls/restart semantics remain; group controls are not one atomic multi-writer effect.

UI sends pool ID and submitted ratings only, not renderer metadata/paths/URLs. It labels the10,000 maximum and paused behavior. New native campaign queues105-member fixture with only positions1/102 available (post2/post103), downloads off-page post103 to authoritative filename103_103.webp, cancels the remaining group child, and independently checks final bytes plus SQLite history after restart.

## Regression repair NYX-CONC-01
Nyx's original exact4c7290a review remains PASS with non-blocking MINOR: Pause/Resume between jobs polls reused old sampler history. Original failing probe/report preserved at ../test-artifacts/nyx-concurrency-4c7290a-t_ae0e483c. New service regression observes SQLite independently (does not accidentally insert a paused jobs poll), tests single/group/shutdown paths at1/2/4 workers, and first resumed rate/ETA must be unknown. RED reproduced limit1 single path; fix invalidates progress on controls and clears on shutdown under existing service lock. Pure sampler tests remain.

## Verification
New tests: whole-pool snapshot205IDs/100+100+5batches, hidden/rating holes, oversize-before-request, interrupted metadata no partial return;203pausedjobs with ordinal holes/reopen; invalid final member no insertion;105-member actual loopback AccountSession to SQLite with original positions1/102; core whole-group atomic invalid/oversize/page-bound/reopen; compiled Svelte explicit command/no browser authority; sampler control regression across9mode/limit combinations.

Failures retained: missing new APIs/UI RED; session request assertion initially assumed no trailing question mark (fixture parsing corrected); UI mistakenly used Rust-style id() instead of property (caught compiled DOM); updated native restart receipt fixture initially omitted new named check (corrected); nested worktree desktop Cargo ancestor-workspace admission refused before formatting/build (do not weaken candidate; exercise after integration in canonical root); strict Clippy caught reviewer-derived redundant format! (removed). npm audit reports2low development dependency findings, deepmerge GHSA-gcqx-w48m-w2h4 via vite Svelte plugin with no advertised fix; no automatic upgrades or suppression. No runtime exploit/release clearance inferred.

Remaining metadata sidecars, wider filesystem/OS/architecture/display/accessibility/audio, paired real native vault/UI prompts, cross-page viewer and final release review remain open. Original C# preserved; no main merge/release/license/provider changes.
