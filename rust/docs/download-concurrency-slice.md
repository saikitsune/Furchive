# Bounded download concurrency and observed progress

Status: local verification complete; fresh packaged three-platform qualification and independent review pending. Overall application NOT COMPLETE.

## Product behavior
- Rust download manager admits 1–4 workers selected from validated preferences at explicit directory grant. Existing `start()`/`select_root()` retain single-worker behavior for API compatibility. No new dependencies or schema migration.
- Parallel manager owns admission, durable transitions and task joins. Pause/cancel abort and JOIN the affected writer before persisting the control acknowledgement. Resume of an active job is idempotent. Shutdown joins every task before persisting remaining Running jobs as Paused.
- Reserve by native parent-directory object identity, not lowercased path spelling. Jobs sharing one directory serialize conservatively, protecting case/normalization aliases and Rename suffix collisions, including different leaf names. Separate directories may overlap. This deliberately limits concurrency with flat templates; it does not claim all distinct filenames can run concurrently. Paused jobs reserve no writer and do not starve queued work.
- Job IPC includes expected total, observed bytes/sec and ETA. First sample, stalled/unknown total, paused/terminal jobs and counter regressions do not fabricate an ETA. Rate history is nonpersistent and absent jobs are evicted. UI shows progress and explicit unavailable values.
- Concurrency edits apply on next directory grant/restart, not silently to an existing worker. Directory capabilities and credentials remain outside preference edits.

## Tests and evidence
- RED: missing `start_with_concurrency` and progress module; missing concurrency UI; paused predecessor prevented unrelated queued completion. GREEN: corrected implementation and regressions.
- Core tests prove two stalled real HTTP writers with bounded admission; four independent native directories publish exact bytes concurrently; identical-destination FIFO publication; invalid 0/5 limits; repeated Resume does not duplicate a request; cancel joins only its writer; pause/resume validates actual Range/If-Range at limits 1,2,4; shutdown leaves no Running/final partial files.
- App fixture test proves configured two-worker service through host grant/enqueue/control and exact files. Rate tests use controlled Instants, including pause/counter regression/missing totals/history removal.
- `rust/test-artifacts/parallel-native-linux.log`: actual strict native gate, 53 default +54 fixture tests, one doctest, fmt, strict Clippy. Feature tests include defaults, not disjoint coverage.
- `rust/test-artifacts/parallel-workspace.log`: workspace all-feature suite (named counts recorded separately).
- Desktop native seven Rust tests, strict default/all-feature Clippy, workspace formatting/Clippy, six catalog tests passed. Frontend 22 compiled component tests +14 units, svelte-check zero errors/warnings, Vite build passed. Python receipt/layout guard tests: 3+1+2 passed.
- Native campaign extended to 24 first-run +7 restart checks. New check exercises two rendered queued jobs, simultaneous Running states, observed rate/ETA, exact final bytes. Fixture records peak outstanding download responses. Await real extracted packages; do not infer native pass from component doubles.

## Limits retained
Same-directory serialization is deliberately conservative. Whole-pool queueing, metadata sidecars, cross-page viewer behavior, paired account UI/native-store locked prompt scenarios, architecture/display/accessibility/audio/filesystem breadth and final independent release review remain open. Existing Windows shutdown and Linux GPU/accessibility diagnostics remain unresolved. No C# removal, main merge, release publication, license or provider/profile change.
