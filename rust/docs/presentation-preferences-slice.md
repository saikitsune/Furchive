# Applied presentation and resident-cache preferences

Status: owner local source gates PASS; fresh exact packaged native campaign26+9 pending. Whole application NOT COMPLETE.

## Runtime contract
- Preferences loaded from local settings on mount; no automatic remote search/media/credential restore. A separate applied snapshot prevents unsaved form edits and failed saves changing gallery/mute behavior. A generation fence rejects late startup/load completions after newer saves or unmount.
- Gallery scale0.5–2 changes tile/preview/row geometry and keyboard scroll math, retaining <=108 mounted tiles across wide windows, tiny tiles and arbitrary scroll positions. Default1 remains engineering/media campaign default. New rowHeight field is explicit testable geometry, not inferred from CSS.
- Start videos muted is applied before source assignment, including defaultMuted and the muted attribute. Defaults stay muted; unchecking is explicit user preference. No autoplay added. Changing setting does not reset a currently open video; next open/navigation samples it. Live volume intent guards remain.
- Duplicate setting controls new durable intents only; existing queue jobs retain their admitted policy. Skip/keep-both Rename/verified Overwrite labels describe existing Rust transaction/publication behavior.
- Resident compressed cache budget is16–512MiB in UI. Legacy stored16GiB values remain readable, but effective resident media is explicitly capped512MiB. Active cloned Arc response buffers, two in-flight fetch buffers and webview decoded surfaces have separate lifetimes; not a total process-memory quota. Per-item64MiB/full and4MiB/preview caps still apply.
- SettingsStore::apply_with_cache holds settings then cache ownership, validates before effects, persists atomically before runtime budget/eviction acknowledgment. A failed persistence leaves previous settings memory and runtime cache budget unchanged. Shrink evicts FIFO handles; growth never resurrects. An old in-flight grant must pass current budget and generation before publication. Startup applies stored effective budget; self-test completion checks actual live budget/resident bytes, not only rendered text.

## Verification
Local119workspace named tests including3new cache preference tests;7desktop Rust;26compiledDOM+15unit;Svelte0errors/0warnings,Vitebuild,fmt,strictworkspace/default+allfeaturedesktopClippy passed. New tests observed RED missing cache API/UI, pure scale geometry, and delayed startup rollback then GREEN. Additional compiled Gallery tests cover End selection/mounted bounds across scales0.5/1.5/2. Existing zero-count geometry expectation extended with rowHeight, bounded assertions retained. Duplicate select binding issue surfaced by campaign; explicit validated change handler repaired it. Clippy default-field-reassignment in fixture corrected without suppression.

Native campaign26first-run+9restart adds real applied gallery1.5/row276,16MiB cache,rename,mute=false before assigning new video; independently reads persisted settings; Rust completion refuses mismatched actual cache budget. Media core qualification thresholds unchanged. Actual package/executable/frame bindings and logs must be reviewed before native acceptance.

## Remaining scope
Metadata sidecars, cross-page viewer workflow, paired account UI/native protected store and locked prompts, broader architectures/Wayland/HiDPI/accessibility/audio/GPU/filesystems, clean installers and final independent product review remain open. Original C# and downloaded artwork unchanged. No main merge/release/license/provider/profile changes. Historical Windows1412/LinuxAT-SPI/EGL/GLsink/ALSA diagnostics and2low development npm audit findings retained.
