"""Run an already-built native Tauri qualification executable and validate its receipt.
No browser substitute, sandbox bypass, live API or user credentials.
"""
import argparse
import json
import os
from pathlib import Path
import subprocess
import sys

parser=argparse.ArgumentParser()
parser.add_argument("binary",type=Path)
parser.add_argument("--output",type=Path,default=Path("test-artifacts/native-qualification.json"))
args=parser.parse_args()
binary=args.binary.resolve()
output=args.output.resolve()
output.parent.mkdir(parents=True,exist_ok=True)
if output.exists():
    raise SystemExit("Refusing stale native receipt; choose a new output path")
env=os.environ.copy()
env["FURCHIVE_QUALIFICATION_OUTPUT"]=str(output)
env["FURCHIVE_TEST_DATA_ROOT"]=str(output.parent/"profile")
result=subprocess.run([str(binary)],env=env,capture_output=True,text=True,timeout=100)
output.with_suffix(".log").write_text(result.stdout+result.stderr)
print(result.stdout+result.stderr)
if result.returncode or not output.is_file():
    raise SystemExit(f"Native executable failed or no receipt: exit={result.returncode}")
node="""import fs from 'node:fs';
import {validateQualification} from './src/lib/qualification.js';
const receipt=JSON.parse(fs.readFileSync(process.argv[1],'utf8'));
validateQualification(receipt);
console.log(JSON.stringify(receipt,null,2));
"""
desktop=Path(__file__).resolve().parents[1]/"apps/desktop"
subprocess.run(["node","--input-type=module","-e",node,str(output)],cwd=desktop,check=True,timeout=15)
print("NATIVE QUALIFICATION PASSED (decode/play/seek + virtualized DOM); animation frames, accessibility, GPU/performance and full app parity remain separate gates")
