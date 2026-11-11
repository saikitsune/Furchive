"""Bounded checks, owned target/cache/logs; never writes the parent lockfile."""
import os
from pathlib import Path
import subprocess
import sys

crate = Path(__file__).resolve().parents[1]
artifacts = crate / "test-artifacts"
artifacts.mkdir(exist_ok=True)
env = dict(os.environ, HOME="/opt/data/home", RUSTUP_AUTO_INSTALL="0")
env["PATH"] = "/opt/data/home/.cargo/bin:" + env["PATH"]
env["CARGO_TARGET_DIR"] = str(artifacts / "target")
env["CARGO_HOME"] = str(artifacts / "cargo-home")
env["RUSTUP_HOME"] = "/opt/data/home/.rustup"
# Registry/index downloads and compiler outputs stay in this owned crate.
command = ["cargo", *sys.argv[2:]]
log = artifacts / (sys.argv[1] + ".log")
with log.open("w") as out:
    out.write("COMMAND: " + " ".join(command) + "\n")
    out.flush()
    try:
        result = subprocess.run(command, cwd=crate, env=env, stdout=out, stderr=subprocess.STDOUT, timeout=240)
        code = result.returncode
    except subprocess.TimeoutExpired:
        code = 124
        out.write("\nTIMEOUT after 240 seconds\n")
    out.write("\nEXIT: " + str(code) + "\n")
print(log.read_text())
raise SystemExit(code)
