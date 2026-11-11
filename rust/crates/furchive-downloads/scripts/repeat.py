"""Repeat the real entire suite; retain a unique log for every invocation."""
from pathlib import Path
import subprocess
import sys

script = Path(__file__).with_name("check.py")
for label in ["50-repeat-a", "51-repeat-b", "52-repeat-c"]:
    result = subprocess.run([sys.executable, str(script), label, "test", "--all-targets", "--locked"], timeout=250)
    if result.returncode:
        raise SystemExit(result.returncode)
