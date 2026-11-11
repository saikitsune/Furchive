# Portable durable downloads — native core milestone verified

Candidate: `7deb877701bdc0f13c7cf3567648c4858017df98`
Run: https://github.com/saikitsune/Furchive/actions/runs/43114513874
Scope: all three **Download contracts** jobs and their actual native receipts. The separate GUI/package jobs were still running when this milestone was verified; their status is not inferred here. Full Furchive app is incomplete.

| Platform | Named unit tests | README doctest | fmt / strict all-target Clippy | Artifact ID |
|---|---:|---:|---|---|
| Linux (Ubuntu24.04) |44|1|PASS|13897882065|
| macOS (macos15) |43|1|PASS|13898345652|
| Windows (hosted MSVC/NTFS) |45|1|PASS|13898320747|

Owner fetched the real logs/ZIPs, checked GitHub's ZIP digest, byte-identical committed manifest/inventory, exact platform named tests without skips/duplicates, all23 source hashes and all six log exit receipts. Windows's source/log paths use native backslashes and its logs CRLF: the original owner Linux-only reader refused those. A fresh owner reader interprets only the win32 paths via PureWindowsPath (collision checked) and applies universal-newline decoding before the unchanged strict test parser. Raw ZIPs/receipts remain untouched. This is a verifier portability repair, not modified product evidence or a relaxed test inventory.

ZIP SHA256:
- Linux: `21a695e1e3dc920e072c8697859fed980337845d3d66a73b2352556e569b494a`
- macOS: `3c864d4d48db02d2f0314456ef8e745db670a9f4ca6d3db315eeb050f467443b`
- Windows: `ae963b3d99f66de57d209752c5394d11ecd33e76be3e72b7633440b3f915a87d`

Durable evidence: `rust/test-artifacts/ci-run-43114513874/download-verification.json`, three `download-contracts-*.zip`, job logs, corrected owner-only `verify-native-artifacts.py`. The originally failing Windows fixture run43113611486 and original scheduler-stuck43098258533 are preserved. DL-01 historical independent BLOCK and owner repairs remain historical, not retroactively changed verdicts.

## What the core acceptance establishes

Real per-platform synthetic HTTP and filesystem behavior covers persisted FIFO, validated Range/If-Range resume, control acknowledgement after writer stop, integrity before publication, duplicate preservation, pinned native object identity, nonfollowing symlink/junction/reparse confinement, exclusive ownership, directory flush/atomic rename and process-crash reconciliation. The Windows literal-parent fixture correction now passes in actual native MSVC execution with no production backend change.

## Limits retained

Windows local NTFS drive-letter roots only; reject UNC/device/remote roots and all reparse points. macOS physical nofollow roots; DB outside grant on Windows/macOS. Real directory flush is mandatory; no pretend durability fallback. No hardware/power-loss or hostile same-user/admin relocation sandbox proof. No integration claim for desktop Downloads UI, groups, quotas, metadata sidecars, parallel workers or application-wide pacing/retry. Those remain integration work. A core milestone is not a release approval, finished product, or test on every OS/CPU/filesystem combination.

## Continuation

The existing same-card native verification can close for this bounded core milestone. Continue already-authorized work: legacy non-secret-data import and broader packaged media lifecycle/performance acceptance before committing the full product UI. No main merge, release publication, license/model/provider/production change is authorized by these results.
