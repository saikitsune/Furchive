# Native downloads attempt 1 — Windows fixture correction

Candidate `2992d62b819652210973cf1e5a2da13b3f5e90c3`.
Recovered run https://github.com/saikitsune/Furchive/actions/runs/43113611486 (one workflow_dispatch after original outage run43098258533 remained scheduler-stuck; normal/force cancel both409 despite GET queued). Same candidate/workflow/gates as original.

## Actual evidence, not green-badge inference

Downloaded artifacts, verified GitHub ZIP digest, exact committed manifest/inventory bytes, complete platform success IDs, source hashes and six zero-exit logs using the retained owner artifact verifier:

- Linux artifact13896848527: PASS44 named tests+1doctest+fmt+Clippy.
- macOS artifact13897472500: PASS43 named tests+1doctest+fmt+Clippy.
- Windows artifact13896329572: FAILED,44 passed/1failed. No terminal native PASS receipt. Failure `tests::native::invalid_root_with_parent_component_is_rejected_before_staging_creation`, src/tests/native.rs:121. Other Windows tests including real directory flush, junction rejection, partial resume, atomic replacement and crash reconciliation passed within that failed suite; that is not complete Windows acceptance and doctest/Clippy later gates did not execute.

Original failed logs/ZIPs remain under rust/test-artifacts/ci-run-43113611486. Full application and portable download milestone remain incomplete.

## Root cause

The fixture calls `OwnedTemp::new`, which resolves the host temp directory using `canonicalize`. On Windows this yields a verbatim prefix. Rust `PathBuf::join` normalizes away `.`/`..` for such prefixes, so `temp.path().join("other/../")` passes a valid normalized root into Grant, not the invalid input the test claims. Grant correctly accepts that normal root. Native test failure is a fixture-construction defect, not demonstrated traversal acceptance by the media capability backend.

Authoritative behavior: https://doc.rust-lang.org/std/path/struct.PathBuf.html#method.push explicitly says that when self has a verbatim prefix and path is nonempty, the new path is normalized with all `.`/`..` removed. Prefix separator differences: https://doc.rust-lang.org/std/path/enum.Prefix.html.

## Bounded correction, no relaxed boundary

Changed only the existing native test and its source-manifest entry: append literal native separators/`..` to an OsString, convert to PathBuf without join, assert a ParentDir component is really present, require Grant rejection before any staging directory, and retain a separate valid-root positive control. Test ID/count unchanged, no skip or ignored test, no production backend change, no weakening of directory flush/rename/reparse rules. Original failing run is preserved. Exact corrected Windows test/runtime remains pending a new candidate run; local Linux targeted regression and full44tests+1doc+fmt/Clippy+3Python guards PASS. Native clean gate and new Windows CI must still verify the correction.
