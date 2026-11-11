# DL-01 repair receipt

Original independent review BLOCK is preserved at downloads/test-artifacts/verify-t_e10269a2-verdict.md. A255byte admitted duplicate filename with Rename could append a suffix past255bytes, save an invalid path and poison listing/shutdown/reopen.

Owner changes (after original verifier finished):
- src/tests.rs: maximum_length_rename_preserves_queue_reopen_and_next_job observed RED in64-owner-DL01-red.log.
- src/worker.rs: build a candidate locally, UTF8-boundary truncate stem to leave room for suffix/extension, validate portable candidate/root reserved path before assigning or persisting.
- src/model.rs: validate intent/destination on every job save, not only read/admission.

Verification:
-65-owner-DL01-green.log: targeted regression PASS.
-66-owner-all.log:32 unit tests PASS (one documented child harness; parent process-death test executes child exit77).
-68-owner-clippy.log: all-targets -Dwarnings PASS.
-69-owner-original-probe-green.log: exact original independent probe unchanged, PASS; jobs_ok=true shutdown_ok=true reopen_ok=true, original bytes intact.

This is a repaired Linux-only download core, not cross-platform support or product acceptance. Original source manifest is superseded by these exact three edited files. A final corrected-candidate hash/test receipt is required before any integration/release claim. No original collection data was used or touched.
