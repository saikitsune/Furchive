# Cross-page viewer slice

Rust owns the submitted search/favorites/pool cursor, accepted current media generation, page/offset and selected edge IDs. The renderer sends only selected ID plus a signed one-step intent. No replacement renderer query, remote URL or path is authority. Normal within-page selection remains thin UI projection.

At a page edge the old Viewer is removed/released, Rust fetches the next/previous page under existing account filters and ratings. Up to four wholly filtered/unviewable pages may be traversed per explicit action; no unbounded pool scan/prefetch. Pool positions and holes remain authoritative. A found page is compare-and-revoked into new cache authority; stale metadata completions cannot replace a newer query/account. Rust cursor updates reject older generations. No persistent session/account/explicit-content auto-restore added.

Frontend applies acknowledged new page and selected ID; a new search/pin/account intent fences the pending edge completion. Native qualification adds search148->200->148 (metadata149hasnullfullURL) and wholepool2->103->2 across the empty50..99batch; actual decode required each time. Existing saved-query/page scope, whole-pool downloads/restart,localcompletedmedia and6realnativeaccount checks remain mandatory. Expected native product inventory28+10 (previous26+10). Metadata sidecars are separately implemented in an isolated worktree and not included here.

## Original independent findings and repair

Original tree134ae93fe0063499c9c8a95d77cb012da408cfde/sourcearchive753e2add1be37f186dad79716229b0063cb8139c06f8653b1241945174c755fa remains BLOCKED with original report/JSON/evidence preserved. NYX-XPV-01: renderer selected metadata without full-media authority before reaching the Rust edge. NYX-XPV-02: pending edge checked query generation only, allowing late completion after a new selection/explicit close.

Owner copied original8 reviewer DOM cases unchanged and reproduced7fail/1pass(exit1). Repair adds required Rust-derived viewable_ids to search/favorites/pool and navigation DTOs; renderer only projects this list for within-page selection/edges, preserving all original metadata rows and pool ordinal holes. No client-side URL authority fallback. Existing test providers receive a labeled DTO fixture upgrade, not production URL validation. Original assertion bodies retained, favorites+noncanonical matrix extended.

Page-turn receipts now fence BOTH query and viewer-intent generation. If the query is unchanged but close/new selection supersedes the edge, bounded submitted-old-page metadata reconciliation restores backend media authority; it never reopens after close. After that acknowledged restore it invalidates old handles and reprepares only the latest currently desired ID when still viewable. A delayed reconciliation/new-selection regression initially failedRED then passedGREEN. This correction is necessary because Rust may commit an edge before its IPC receipt arrives. New query/pin/account continues to take precedence without restoring the old page.

All17 adversarial UI cases (12mode/direction/null-or-noncanonical, four original-style cases and one delayed reconciliation test) pass within49 total component tests. Source gates131workspace,9desktopRust,15frontendunit,fmt/strictClippy/check/build pass; fresh exact-hash independent re-review and native28+10qualification remain required. No confidentiality exploit demonstrated in original review.

## Executed development evidence

- Cursor tests RED missing module/API exit101 thenGREEN.
- Real Browser/Database/MediaCache navigation test RED missinggeneration/navigate APIs thenGREEN.
- DOMedge regression RED lastpostNextdisabled thenGREEN; stalecompletion case first incorrectly clicked disabledSearch, corrected TEST to actual enabled pinnedPool intent, same sourcefence tested.
- Latecursorpublication test RED oldergeneration overwrote newer cursor(exit101) thenGREEN monotonic replacement.
- Pool/favorites/boundedempty/staleresult and successfulback/forward compiledDOM tests pass.
- Campaign test caught malformed63hex one-digitpost2 synthetic mediahandle; corrected only testhandle padding, production64hexgate unchanged.
- StrictClippy manualmodulo lint fixed is_multiple_of, no lint suppression for it.

Native packaged runtime for this candidate remains UNRUN until fresh exact-head hosted evidence. Source tests/DOMdoubles/builds are not native proof. Existing source/account/localmedia milestones retain their exact older candidate provenance.

Remaining whole-product work: metadata sidecars,cancellation/slowfilesystem and history performance scope,broaderOSarchitecture/display/accessibility/audio/filesystem/install/upgrade/lockedstoreprompt gates,finalwholeproductreview. OriginalC#80files/userart/data preserved; no mainmerge/release/license/model/provider/safetychanges.
