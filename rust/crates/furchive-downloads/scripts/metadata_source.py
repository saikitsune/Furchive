"""Freeze metadata milestone core source and explicit platform inventories.
Run from the isolated checkout after metadata verification; does not discover
expected test IDs or certify non-Linux/native-WebView execution.
"""
from pathlib import Path
import hashlib,json,subprocess,sys
BASE=Path(__file__).resolve().parents[1]
REPO=BASE.parents[2]
BASE_HEAD='05d1bf378b8b73d63110aa588c73d62a7e5bece6'
REPAIR_BASE='372d9b6cb7dd9a1816cf393a39e141d9df3abfd1'

def freeze():
    evidence=REPO/'rust/test-artifacts/metadata-repair'
    receipts=json.loads((evidence/'verification.json').read_text())
    assert len(receipts)==26 and all(r['exit']==0 for r in receipts)
    for receipt in receipts:
        assert hashlib.sha256((evidence/receipt['log']).read_bytes()).hexdigest()==receipt['sha256'],receipt['label']
    sys.path.insert(0,str(BASE/'scripts'))
    from native_check import validate_tests
    inventory=json.loads((BASE/'contracts/test-inventory.json').read_text())
    validate_tests((evidence/'final-core.log').read_text(),inventory['linux'])
    paths=[BASE/'.gitignore',BASE/'Cargo.toml',BASE/'Cargo.lock',BASE/'README.md']
    paths+=sorted((BASE/'src').rglob('*.rs'))
    paths+=sorted((BASE/'scripts').glob('*.py'))
    paths+=sorted(p for p in (BASE/'contracts').glob('*') if p.name!='source-manifest.json')
    hashes={str(p.relative_to(BASE)):{'sha256':hashlib.sha256(p.read_bytes()).hexdigest(),'bytes':p.stat().st_size} for p in paths if p.is_file()}
    result={'base_head':BASE_HEAD,'repair_base':REPAIR_BASE,'scope':'rust/crates/furchive-downloads/','milestone':'normalized-furchive-v1 sidecars; REY-META-01 correction; implementation handoff only','files':hashes,'inventory':{k:{'count':len(v),'sha256':hashlib.sha256(('\n'.join(v)+'\n').encode()).hexdigest()} for k,v in inventory.items()},'verification':{'linux':'native core tests/fixture/doc/fmt/strict Clippy; see isolated verification.json','windows':'UNRUN for this candidate; reviewed closed inventory updated intentionally','macos':'UNRUN for this candidate; reviewed closed inventory updated intentionally','packaged_webview':'UNRUN on all OS; campaign now requires 27+11 checks; no CI dispatched'},'verification_receipts':receipts}
    path=BASE/'contracts/source-manifest.json';path.write_text(json.dumps(result,indent=2)+'\n');print(json.dumps(result['inventory']))
if __name__=='__main__':freeze()
