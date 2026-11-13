# Native download source milestone — exact acceptance contract

## Verdict and authority

This is a **source implementation milestone, not cross-platform certification**.
Starting HEAD: `1367d403efe94799a4ae44ca643cfdd1fb6cb6f1`, branch `rewrite/rust`.
During this shared-dir run the owner advanced HEAD to
`10c9e330a676541f32b2e7714053469d3f846aa6` (docs/progress only);
`git diff --name-only <starting-head> HEAD -- rust/crates/furchive-downloads`
was empty. Worker did not commit or change those sibling paths.
Only `rust/crates/furchive-downloads/**` changed. No commits, staging, push,
workflow, desktop, root workspace, production/user media or live e621 operation.
Configured Bram route remained `nous/openai/gpt-6.1-sol`, service tier `normal`,
fallback list empty; no model/config changes and no model-independent review.

Historical original DL-01 BLOCK, original31-test receipts and owner repair receipts
remain untouched. Starting repaired baseline33 tests passed in `xp-00-baseline.log`.

## Native mechanism map

| Contract | Linux | macOS | Windows |
|---|---|---|---|
| Grant/ancestors | root-to-leaf openat NOFOLLOW DIRECTORY; child openat2 BENEATH/NO_SYMLINKS/NO_MAGICLINKS | root-to-leaf and child component openat NOFOLLOW DIRECTORY | local drive root, single component NtCreateFile RootDirectory, OPEN_REPARSE_POINT, query+reject every reparse tag |
| Identity | device+inode | device+inode | FILE_ID_INFO:64-bit volume serial + full128-bit file ID |
| Private new storage | stage0700, files0600, regular/nlink1 | same | protected owner-rights+SYSTEM DACL for new stage/part/marker/lock; regular, nlink1, not delete-pending/reparse |
| Exclusive owner | std File::try_lock | same | same native Windows file lock |
| No clobber publish | renameat2 NOREPLACE via rustix | renameatx_np RENAME_EXCL via rustix | NtSetInformationFile FileRenameInformationEx, no replace flag |
| Overwrite | atomic rename, never truncate original | same | same-volume handle-relative rename REPLACE_IF_EXISTS+POSIX_SEMANTICS; no delete+rename/copy fallback |
| Namespace durability | real File::sync_all directory fsync | real directory fsync | query NTFS by pinned handle, reopen same directory by empty relative NT name with directory write rights, check full identity, NtFlushBuffersFile; every error propagated |

Media traversal never canonicalizes then opens. Existing canonicalize calls in
model.rs only reserve Linux's trusted application DB path; native Windows/macOS
DB separation instead uses opened ancestor identities. Test-owned physical temp
base resolution is fixture preparation, not a production confinement check.

Worker has no Unix metadata/rename calls left. HTTP/controls remain the same shared
implementation: harmless loopback fixtures exercise exact If-Range, offset/range/
validator/size/hash checks, cancel/pause writer-ending acknowledgement, FIFO,
exclusive worker, actual child death and publication crash reconciliation.

Receipts read old schema1 Unix tuples and new tagged native identities. New receipts
cannot be read by old binaries; no backward-binary downgrade or cross-OS DB move is
promised. Root identity mismatch continues to fail closed.

## Explicit platform limits / architectural tradeoffs

1. **Windows and macOS DB must be outside the media grant.** Native case folding,
   Unicode normalization and NTFS8.3 aliases cannot safely reserve an in-root DB by
   portable spelling. Before opening/creating DB, compare granted root identity
   with every DB-parent ancestor opened nofollow; reject if inside. Use app state
   for DB and a separate owned media directory. Linux keeps the previous in-root
   DB behavior with companion reservations and conservative ASCII case rejection.
   This is a deliberate platform constraint, not an API signature change.
2. Windows supports **local NTFS drive-letter roots only** (including verbatim disk
   form); UNC/device/remote roots, ReFS/FAT/exFAT, reparse points/cloud placeholders,
   symlinks/junctions/mount points are rejected, not followed. Standard-user access
   must permit writable directory reopen/flush. If denied, Engine::open fails
   before opening DB; it does not fall back to pretend fsync. A drive-letter alias
   controlled by privileged actors remains outside the same-user/admin boundary.
3. Windows10/11 builds/filesystems must support FileRenameInformationEx and POSIX
   replacement of a still-open original; otherwise error. No fallback. Native
   runner must establish actual API availability; cfg check cannot prove this.
4. macOS requires native NOREPLACE (10.12+ API) and real directory fsync. Root paths
   must be physical, no symlink ancestors (e.g. use physical `/private/...`, not
   `/var/...` aliases). No rename or fsync emulation. No macOS SDK/stdlib installed
   here; native compile+runtime remains an open mandatory gate.
5. Existing grant/stage/DB/ancestors must not be maliciously relocated/replaced by
   same-user/admin actors; no sandbox against concurrent hardlink insertion,
   mount replacement or changing pre-existing permissions. New Windows storage
   receives protected DACLs; pre-existing storage remains trusted owned app state.
   No hardware/power-loss fault injection proof; API success assumes compliant
   filesystem/filter/storage stack. Unsupported filesystem/flush is fatal.

Staging is fenced both by reserved spelling and opened directory identity, so
NTFS short-name or normalization aliases cannot route destination operations into
private staging. Duplicate suffix paths retain `/` separators on Windows too.

## Authoritative native references inspected

- [NtCreateFile user mode](https://learn.microsoft.com/en-us/windows/win32/api/winternl/nf-winternl-ntcreatefile): relative RootDirectory, create/open dispositions and directory restrictions.
- [FILE_RENAME_INFORMATION](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntifs/ns-ntifs-_file_rename_information): atomic same-volume rename, replace/POSIX flags, relative target name.
- [MS-FSA2.1.5.7](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-fsa/0de7dc40-9627-437e-a4df-c4696cdc3d02): directory structure flush to stable storage.
- [MS-FSA Appendix A footnote80](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-fsa/4e3695bd-7574-4f24-a223-b4679c065b63#Appendix_A_80): **NTFS only**; other filesystems can return success without doing it. Hence mandatory NTFS query, never trusting flush success alone.
- [NtFlushBuffersFileEx](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntifs/nf-ntifs-ntflushbuffersfileex), [ZwFlushBuffersFile](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntifs/nf-ntifs-zwflushbuffersfile): native flush/write access and user-mode Nt entry point.
- [GetVolumeInformationByHandleW](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getvolumeinformationbyhandlew).
- Locked rustix1.1.6 source: Darwin `renameat2` implementation invokes `renameatx_np`; `RenameFlags::NOREPLACE` is `RENAME_EXCL`; missing API returns NOSYS, no emulation for dirfds/flags.

Public Q&A/third-party discussions were discovery aids, not certification evidence.
The native gate must validate writable NT directory reopen in the actual runtime.
If this fails, retain error, investigate rights/API against native evidence; do not
remove flush or accept failed calls. An alternative needing owner decision is a
separate explicitly recovery-only durability contract, not this strict API.

## TDD and actual local evidence

All logs below are retained under ignored `test-artifacts/`; historical logs were
not overwritten. Missing API RED is distinguished from behavioral RED:

- `xp-01-adapter-red.log`: missing identity/sync_directory/publish compile RED;
  `xp-02-adapter-green.log`: real no-clobber/overwrite/open-original identity GREEN.
- `xp-07-collision-recovery-red.log`: assertion **suffix lost publication identity/digest**;
  `xp-08-collision-recovery-green.log`: persist receipt for each suffix candidate.
- `xp-13-case-alias-red.log`: assertion **database alias admitted: NESTED/JOBS.DB**;
  `xp-14-case-alias-green.log`: native-safe DB policy/conservative Linux reservation.
- `xp-16-stage-identity-red.log`: missing ensure_media_directory compile RED;
  `xp-17-stage-identity-green.log`: opened-stage identity fence GREEN.
- Added native and serialization edge checks immediately passed generalized
  implementation where exercised; not claimed as separate observed RED cycles.
- `xp-04-windows-check.log`: initial1.99.1 missing target std failure retained;
  installed `+stable` has Windows GNU std, so `xp-09-full-windows-cfg.log` and
  `xp-21-windows-clippy.log` are actual **full crate/all-targets cfg compile/Clippy**
  passes. `xp-05-windows-cfg.log` is an additional backend-only metadata check.
  None links or executes Windows, and no Windows runner receipt exists here.
- `xp-22-macos-check.log`: missing Apple std and cross C compiler/SDK failure;
  no install attempted and no macOS build/runtime pass claimed.
- `xp-28-native-gate-interleaved-tests.log`: first real gate rejected a complete
  44-test PASS because child harness stdout spliced its parent's named line.
  Child stdout is now suppressed only at the parent spawn (stderr preserved);
  exit77 and durable resume assertions unchanged. Re-running the real native
  gate passed the reviewed inventory. The strict parser was not weakened.
- Final current native Linux receipts: `test-artifacts/native-linux/receipt.json`
  from the gate below (44 named unit tests, one README doctest). Python gate guard
  tests:3 unittest cases. The Rust harness child entry is a no-op standalone;
  its parent actually spawns it, requires exit77, and checks durable resume.

## Exact native runner commands / required acceptance

Parent owns workflow edits and native Actions execution. No worker release claim.
On each **native** Ubuntu/Linux, macOS and Windows MSVC runner, with Python3.10+
plus Rust>=1.99, cargo, rustfmt, Clippy and native C build tools installed, run from
repository root:

```sh
python rust/crates/furchive-downloads/scripts/test_native_check.py
python rust/crates/furchive-downloads/scripts/native_check.py
```

Windows runner must allow symlink fixture creation (Developer Mode or token
privilege). Symlink/junction setup failure is a test failure, **not a skip**.
Use local NTFS temp space; no administrator volume flush or production data.
macOS fixture creates an owned temp base using its physical path. All media is
synthetic bytes served only on127.0.0.1. No live e621 request is permitted.

The native gate bounds each command to240s, logs exact command/exit, and runs:
`rustc --version --verbose`; `cargo test --lib --locked -- --list`;
`cargo test --all-targets --locked -- --test-threads=1`;
`cargo test --doc --locked`; `cargo fmt -- --check`;
`cargo clippy --all-targets --locked -- -D warnings`.
It compares discovery **and actual success names** against the checked-in inventory,
rejecting duplicates, missing/ignored/filtered tests and warning/error transcripts.
No inventory regeneration occurs in the runner. Expected counts: Linux54,
macOS53, Windows55 (plus one explicit fixture-http test on each OS). Differences are explicit cfg contracts, never ignored tests:
Linux2 in-grant DB cases vs native outside-grant DB rejection; Windows adds2
junction/flush/UNC checks. All common HTTP/control/recovery contracts run natively.
Retain `test-artifacts/native-<sys.platform>/*` including receipt.json and bind it to
exact committed head/source manifest. A successful no-op or cfg build is not pass.

Local host equivalent: `python .../scripts/native_check.py --local-toolchain`
uses only the existing explicit `/opt/data/home` Rust toolchain/cache (no install).
The final gate is actually exercised with this option on Linux. Cross cfg checks:
`python .../scripts/windows_crate_check.py`, `windows_clippy_check.py`,
`windows_cfg_probe.py`; these explicitly use existing `+stable`, no native claims.

## Normalized metadata milestone inventory (current source)

The earlier counts above are historical portable-core evidence, not the current
sidecar candidate. Current reviewed `test-inventory.json` counts are Linux **85**,
macOS **84**, Windows **86**, plus one explicit fixture-http test per platform.
The sidecar milestone adds 21 named tests; the REY-META-01 correction adds the
unchanged two reviewer regressions and six retry boundary tests, including its
real-process-kill harness. Linux current receipts exercise these tests;
macOS/Windows current build/runtime and all packaged-WebView sidecar campaigns
remain **UNRUN**.
`metadata_inventory.py` enumerates intentional additions explicitly rather than
learning expectations from discovery. `metadata_source.py` freezes the current
source-manifest against the isolated `metadata-repair/verification.json` logs;
original `metadata-evidence` receipts and BLOCK verdict are historical, unchanged;
`milestone_evidence.py` is retained only for its earlier historical milestone.
See [sidecar lifecycle and limits](../../../docs/normalized-metadata-sidecars.md).

## Changed-file / hash inventory

`contracts/source-manifest.json` contains exact changed deliverables and SHA256
for all current crate source/tests/scripts/contracts/README/Cargo files; excludes
itself to avoid self-hash recursion. `contracts/test-inventory.json` enumerates
every unique platform test ID. `scripts/milestone_evidence.py` verifies receipts,
checks crate-only Git delta and generates the manifest without altering historical
evidence. Nested Cargo.lock is unchanged; no new dependency was needed.
One-time authoring scripts were removed after use; only runnable check/evidence
scripts remain. Important commands and failed alternatives are recorded above.
