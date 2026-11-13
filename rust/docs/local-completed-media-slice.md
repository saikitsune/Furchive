# Local completed-media slice

## Contract

Full viewer requests remain current-query/account-authorized e621 post IDs. Rust obtains the current canonical URL, looks up the latest Completed download for that exact source/post/URL and snapshots up to min(resident budget,64MiB) from the explicitly regranted native directory. Preview thumbnails retain their existing network route. No pathname or new origin enters from the renderer.

Only a durable native publication receipt grants local-resolution authority: opened file identity, actual size, SHA256 and optional admitted MD5/SHA256 must match. Descriptor-relative no-follow traversal, reserved database/staging exclusions, native root identity and single owner remain enforced. Native Rename publication names are used verbatim rather than reconstructed from current templates. Local file reads/hashing run on spawn_blocking behind the existing two-item media semaphore; publication rechecks the cache generation and current budget. Missing/modified/misbound publications produce explicit errors, not a silent redownload. No matching completed record falls back to the existing bounded canonical network path.

After restart, queue history alone does not grant a filesystem root, restore an account or restore explicit content. Select the directory and perform a normal filtered metadata request before local rendering. This is not a recursive offline library browser, automatic legacy-art adoption or persistent media grant.

## Independent finding and correction — NYX-LM-01

Original staged tree fc96441d5c8317cf41c989de366a4d46222877b9 and archive09bb8622aa5187910424cbb9be48d7bc6af4185faf9236f37766b187b77f5a33 remain preserved. Nyx demonstrated a MAJOR captured-generation/URL continuity defect: after awaited local miss, calling public prepare(id,variant) minted a newer grant. An old request could therefore fetch a new query's URL and bypass the newer local corruption check. No unauthorized filesystem or cross-account disclosure was demonstrated; shipped UI also has an independent generation fence.

Owner reproduced with real single-slot blocking-pool occupancy, manual poll-to-Pending and authority replacement: two negative tests failed with Ok handles (exit101), two controls passed. Repair shares private prepare_granted(Grant) between normal network prepare and local miss while retaining the same semaphore permit; original generation checks before HTTP/cache use and on publish remain. No check-then-regrant seam. Four regressions pass, including local-hit revoke, current-miss network positive, stale miss and corrupted-current-publication variant. Original failing receipts are not overwritten. Fresh exact-hash review and native packaged proof are mandatory; original default Linux package is superseded static-only evidence.

## Executed development evidence

- Core test first failed to compile on missing completed_bytes (exit101), then passed.
- App fixture test first failed on missing prepare_with_downloads (exit101), then passed with HTTP fixture shut down before reopen/read.
- Current-upstream-version regression failed (exit101: new upstream version cannot read old post bytes as current), then passed after exact URL binding.
- Core tests cover renamed destination, unchanged original, symlink leaf, replaced native object with identical bytes, wrong source, missing leaf, bounds and same-size tamper.
- Host receipt test initially refused the newly required local-completed-media-restart contract; updated closed inventory now rejects old/duplicate receipts.
- DOM campaign ran the new restart path but correctly failed stale expected count10!=9; explicit inventory updated to26+10 and retained positive local-decode assertion.
- Full local Rust/frontend/fmt/Clippy and native Linux core gates recorded by owner. Fresh actual three-OS packaged campaign remains required: phase2 denies ALL fixture /data/ HTTP responses, then uses rendered saved-search+Search+viewer controls to require decoded completed post100 after root regrant. Other media/gallery/account gates remain unchanged.

No C# source, downloaded collection, production credentials, model/provider/profile settings, license, main branch or release modified.

Remaining: metadata sidecars, cross-page viewer, broader architecture/display/filesystem/install/upgrade/accessibility/locked-store qualification and final whole-product independent review. Whole application is NOT COMPLETE.
