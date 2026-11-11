"""Verify retained final receipts and hash this crate's deliverable inventory."""
from pathlib import Path
import hashlib
import json
import re

crate = Path(__file__).resolve().parents[1]
artifacts = crate / "test-artifacts"
receipts = {
    "fmt": "56-final-fmt.log",
    "clippy": "57-final-clippy.log",
    "tests": "58-final-test.log",
    "doctests": "59-final-doc.log",
}
for kind, filename in receipts.items():
    text = (artifacts / filename).read_text()
    assert text.endswith("EXIT: 0\n"), (kind, "missing success receipt")
    assert not re.search(r"^(error|warning):", text, re.MULTILINE), kind
unit = (artifacts / receipts["tests"]).read_text()
ids = re.findall(r"^test (tests::\S+) \.\.\. ok$", unit, re.MULTILINE)
assert len(ids) == len(set(ids)) == 31, ids
assert "31 passed; 0 failed; 0 ignored" in unit
assert "1 passed; 0 failed; 0 ignored" in (artifacts / receipts["doctests"]).read_text()
paths = [crate / ".gitignore", crate / "Cargo.toml", crate / "Cargo.lock", crate / "README.md"]
paths += sorted((crate / "src").glob("*.rs"))
paths += sorted((crate / "scripts").glob("*.py"))
files = {str(p.relative_to(crate)): {"sha256": hashlib.sha256(p.read_bytes()).hexdigest(), "bytes": p.stat().st_size} for p in paths}
result = {
    "crate": "furchive-downloads",
    "platform": "Linux only; non-Linux compile_error; openat2/renameat2/local fs required",
    "tests": len(ids),
    "test_ids": sorted(ids),
    "doctests": 1,
    "receipts": receipts,
    "files": files,
}
out = artifacts / "evidence.json"
out.write_text(json.dumps(result, indent=2) + "\n")
print(json.dumps(result, indent=2))
