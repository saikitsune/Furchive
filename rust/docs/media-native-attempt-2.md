# Native media attempt 2 — persistent mute defect isolated

Exact diagnostic source19aa506, run43281548193. Download jobs all PASS. All native jobs nonpass; failing boundaries distinguished below. Original6ba3f82Windows campaign PASS remains historical, not transferred to this run.

## Linux: precise actual observation

Runtime preserved full failed result. All3videos actually play, seek to~0.51–0.54seconds at2x and release correctly; all20cleanup/stale-load cycles pass; gallery384ms initial and56–72ms scroll,36–48mounted nodes. The failed video predicate is **muted:false after seeking**, while beforePlay muted:true/defaultMuted:false. All3video rows show this mismatch. Actual36host PNGs captured and strict owner frame validator independently PASS for GIF/APNG/WebP. Runtime muted contract remains FAIL and was not relaxed.

## macOS: still distinct

vp8 metadata ready1;width320;mutedtrue/defaultMutedfalse beforePlay. After failed play, ready0/network0,width0,durationNaN but no preserved MediaError; this is resource/pipeline reset rather than demonstrated NotAllowedError. No animation checkpoint reached. Do not assume Linux mute correction alone proves this fixed; retain state/status diagnostics and rerun real native gate. No policy disable, codec substitution or deadline inflation.

## Windows: earlier gate

Failed original qualification's extracted portable VP8 path with MEDIA_ERR_SRC_NOT_SUPPORTED4/Format error while VP9/H264 pass. No expanded campaign run in this attempt. This is distinct from the prior actually passing Windows artifact; keep failure and add safe transport I/O/error-state diagnostics instead of skipping VP8 or declaring flaky success. No root cause proven yet for this path.

## Bounded correction and feedback loop

Viewer dynamically sets current muted but not defaultMuted before source assignment. Added explicit muted/defaultMuted/attribute initialization before URL assignment, retaining real unmute control. Labeled component boundary regression simulates source initialization resetting current mute to default, observes RED defaultMutedfalse then GREEN true. This is a persistence-contract regression seam, not an emulation of WebKit internals. Current source now reflects user-safe muted defaults in both reusable Viewer and original Qualification, with exact native checks unchanged.6component/12unit/Svelte/Vite PASS. Native corrected behavior still pending. No generic autoplay/sandbox/CSP weakening or hidden retried success.

All original ZIPs, failure receipts and raw PNGs remain rust/test-artifacts/ci-run-43281548193. Source/report changes are not completed application acceptance; product integration remains downstream.
