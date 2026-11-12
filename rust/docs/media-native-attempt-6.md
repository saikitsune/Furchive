# Native media attempt6 — readiness boundary and host-capture schedule

Candidatef74dadb/run43290864158. All3download jobs PASS; Linux native campaign PASS. macOS campaign FAIL; Windows rendered runtime passes all application predicates but raw-frame cadence validator correctly refuses one375ms gap (limit200ms). Earlier Windows passes remain historical, not transferred.

## macOS transport evidence

Self-test transport sees exactly one GET/HTTP1.1, Range absent, Origin absent,200response complete65471byteVP8 body; no refusal/duplicate-header/I/O diagnostic and no second request. Original qualification same package passes. Reusable viewer signals ready at loadedmetadata/readyState1 before it has decoded current-frame data; original path uses preloadauto/loadeddata. This isolates the next falsifiable hypothesis: metadata-only readiness allows playback against a resource whose current-frame/pipeline is not ready. Native cause still unproven until correction-control execution; no claim network200 alone certifies decoder readiness.

Added component regression requiring metadata alone keeps Play disabled and loadeddata enables it. Observed RED (`metadata`preload), then GREEN after enabling auto buffering ONLY for currently selected media and marking ready on loadeddata. Existing8second load budget/error/cleanup/dimension limits unchanged, no auto play, no codec substitute or webview-policy change. Updated existing controls tests to emit actual loadeddata readiness, kept explicit metadata-negative assertion. Real macOS native outcome remains mandatory.

## Windows capture infrastructure

Runtime all predicates pass, raw GIF capture timing has375ms gap while other intervals78–94ms. Host was PNG-compressing/writing every full client frame in the sampling loop. Remove that irrelevant variable without weakening validator: copy bounded12raw RGB samples per checkpoint with foreground verification and monotonic timestamps, encode PNG only after sampling; use modest compression level1 for owned evidence. At most12*4096*4096*3bytes under existing native geometry limit, ordinary1280x860client far smaller. New schedule test uses labeled clock/screen doubles (not native proof), observed missing seam RED then GREEN; asserts exactly12real grab calls/identity checks and80msfake cadence. No retry of failed native result, no cadence/deadline inflation; next actual host frames must pass original30–200ms cadence and3pixel-state progression.

Local9compiled component tests/12unit/check0errors0warnings/Vite PASS; Python driver/capture-parser/corpus3tests PASS. Full app/product UI and native media acceptance remain incomplete. All original failure ZIPs/logs/receipts remain rust/test-artifacts/ci-run-43290864158.
