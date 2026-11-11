"""Bounded core checks, isolated from the parent-owned root Cargo.lock.

Usage: python rust/crates/furchive-engine/scripts/check.py LABEL [cargo args...]
Generated workspace, lockfile, compiler outputs and logs are ignored artifacts.
Set FURCHIVE_CHECK_ROOT=1 to run against the parent workspace (use --locked and
select only the two owned packages). This runner is not concurrency-safe.
"""
import os
from pathlib import Path
import shutil
import subprocess
import sys

engine = Path(__file__).resolve().parents[1]
artifacts = engine / "test-artifacts"
workspace = artifacts / "workspace"
workspace.mkdir(parents=True, exist_ok=True)
for name in ("furchive-domain", "furchive-engine"):
    source = engine.parent / name
    dest = workspace / "crates" / name
    if dest.exists():
        shutil.rmtree(dest)
    shutil.copytree(source, dest, ignore=shutil.ignore_patterns("target", "test-artifacts", "__pycache__"))
(workspace / "Cargo.toml").write_text('[workspace]\nresolver = "2"\nmembers = ["crates/furchive-domain", "crates/furchive-engine"]\n')
env = dict(os.environ, HOME="/opt/data/home", RUSTUP_AUTO_INSTALL="0")
env["PATH"] = "/opt/data/home/.cargo/bin:" + env["PATH"]
env["CARGO_TARGET_DIR"] = str(artifacts / "target")
if os.environ.get("FURCHIVE_CHECK_ROOT") == "1":
    workspace = engine.parents[1]
command = ["cargo", *sys.argv[2:]]
with (artifacts / (sys.argv[1] + ".log")).open("w") as log:
    log.write("COMMAND: " + " ".join(command) + "\n")
    log.flush()
    result = subprocess.run(command, cwd=workspace, env=env, stdout=log, stderr=subprocess.STDOUT, timeout=240)
    log.write("\nEXIT: " + str(result.returncode) + "\n")
print((artifacts / (sys.argv[1] + ".log")).read_text())
raise SystemExit(result.returncode)
