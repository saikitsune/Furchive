# Native media attempt11 — isolate host sleep overshoot before another application change

## Actual dc7d0c0/run43306961270

macOS runtime receipt passes: initial523ms,scroll30–49ms,25–40mountedrecords, all3videos and20cleanupcycles. macOS helper compiled/executed and captures all3animations. Overall macOS job FAILS unchanged30–200ms interval requirement: actual deltas include20–22ms firstwarmup intervals and209–241ms subsequent intervals. Actual capture API calls usually11–31ms; noncapture gaps197–214ms dominate. All3download jobs and Linux/Windows nativejobsPASS. Owner independently downloaded Linux/WindowsZIPs, checked providerSHA256 and reran actual runtime+36hostframe validators for each. No overall app completion.

## Bounded native diagnosis, not speculative backend churn

Added CI-only5minute read-only-clock/window-info probe; no screenshots/media/userdata/permissionchange. Probe first failed because host has no ordinary foreground window without the app. Fixed probe creates/closes its own blank window. Retained original failure43310755058 (exit133), no acceptance claim.

Actual probe43311871441 proves WindowServer enum<0.8ms while requested80ms Swift sleeps take82–155ms. Followup43312948998 independently reproduces Swift zero-tolerance/activity and Mach absolute waits jitter (up to155ms); no real-time scheduler/hostpolicy changes. Latest43313601057 compares relative waits and clock-rechecked1ms sleeps. Thread/POSIX80ms requests overshoot up to145ms. Measured-clock-short-sleep-deadline actually81.1–86.2ms across8samples. These are limited current runner observations, not universal scheduler guarantees. ArtifactZIP digests verified and rawtiming.json retained with exacthead/run.

Kept existing actual-compiled SCScreenshotManager backend. An uncommitted SCStream idea was discarded before push after authoritative Apple docs showed unchanged display emits idle/noimage; accepting duplicated stream images or changing fixture cadence would undermine raw-frame proof. No product source changes or new backend introduced.

Correction is narrow pacing: before each image request after first, recheck actual monotonic elapsed since preceding capturedimage and yield with1ms sleeps until80ms. Never busyspin, change timestamps, request-start pace, skip/retry images, or modify30–200ms bound. Warmup delay cannot create too-short secondimage interval. Captures retain actual request/completion clocks, strict PID/window perimage, safe-client dimensions,12realimages/format and hash. New actual platform run must establish final cadence; diagnosis alone is not acceptance. Existing Python3driver+1parser+1corpus contracts pass unchanged. Swift/platform qualification pending existing sixjobCI.

Complete app still not ready; product search/download/settings integration remains outstanding and broaderX11/Wayland/macOSarch/HiDPI/accessibility/audio gates remain unverified. No mainmerge/release/licensing decision/security/model/provider change.
