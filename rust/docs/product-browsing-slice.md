# First product browsing slice

This is real product UI/engine integration, not finished application acceptance. `BrowsePanel.svelte` now drives typed thin `BrowserBridge` IPC; reusable Rust `browse::Browser` owns searches/favorites/pool projection/pins and the bounded DB actor owns durable state. Default launch opens metadata browsing. No auto-restored network query, no remote thumbnails/direct-media URL scope yet. The existing separately qualified Gallery/Viewer are still fixture-only until safe production media/cache integration lands.

## Implemented

- Include/exclude chips, known-rating choices, sort, upstream suggestion choices, explicit searches/favorites, pagination and saved-search save/restore/delete.
- Pool ID browsing and pins. Rust fetches authoritative pool membership, retrieves bounded exact-ID batches, refuses unexpected/duplicate/noncanonical IDs, and reorders returned posts into original pool positions. Missing/account-filtered/local-rating-excluded members remain holes rather than compaction or cached-account revival.
- Exact-ID requests share the existing paced authenticated-capable E621Client; no filter_id bypass or arbitrary endpoint origins. Username favorites constraints are validated Rust-side; favorite mutation/auth integration not claimed.
- Pagination captures submitted query, username and pool identity, not unsent form edits. Actual compiled component regression observed RED `[fox,wolf]` then GREEN retained `[fox]` on page2.
- Native self-test-only loopback metadata fixture adapter (`fixture-api` feature activated by desktop `self-test` only). Production ignores test origin environment. No credentials, redirects or live API in campaign. Runner exercises actual product DOM handlers -> Tauri IPC -> Rust -> local HTTP + SQLite; restarts exact executable then verifies saved search/pin restoration, delete/unpin and durable DB emptiness.

## Local executed verification

- Rust all-features workspace:86passed,0failed (85without fixture-only origin test), formatting, strict all-target Clippy.
- Native Rust6tests + default and self-test/custom-protocol strict Clippy PASS.
- Frontend13unit+11compiled component tests PASS, Svelte0errors0warnings, ViteproductionbuildPASS. Native campaign itself executed against a labeled in-memory provider double in compiled DOM; this proves driver/control flow only, not native acceptance.
- Python browser receipt negative test and package-layout test PASS. Missing/duplicate named UI checks refuse process-exit-only admission.
- Playwright browser path initially absent; installed exact browser in project test-artifacts. Real sandboxed launch then refused by container usernamespace policy; preserved failure, NO --no-sandbox or host policy changes. Native CI remains actual application/platform execution route.
- Previous temporary native sysroot was pruned. Rebuilt authenticated Debian SDK under project-owned test-artifacts,293package hashes checked against signed repository metadata; existing host sonames linked only within test-owned SDK. Initial metadata-reader epoch-name errors and missing runtime symlinks were corrected; no host install/privilege/sandbox change. Native linker/tests now execute successfully.

## Next actual acceptance

Existing six-jobCI now also launches extracted package metadata browsing campaign twice against one private test profile. Native product browsing is PENDING until those actual receipts/HTTP records and packaged bytes are verified. No model/provider/main/release/license changes; original C# unchanged. Production media/cache/download integration, settings/credentials/migration UI and broader platform gates remain incomplete.
