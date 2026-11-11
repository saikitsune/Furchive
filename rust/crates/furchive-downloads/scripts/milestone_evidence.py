"""Verify current implementation receipts and freeze source hashes, not history."""
from pathlib import Path
import hashlib
import json
import subprocess
from native_check import validate_tests

BASE = Path(__file__).resolve().parents[1]
REPO = BASE.parents[2]

def main():
    inventory = json.loads((BASE/'contracts/test-inventory.json').read_text())
    receipt = json.loads((BASE/'test-artifacts/native-linux/receipt.json').read_text())
    assert receipt['tests'] == len(inventory['linux']) == 44
    assert receipt['test_ids'] == inventory['linux']
    for name, relative in receipt['receipts'].items():
        text = (BASE/relative).read_text()
        assert text.endswith('EXIT: 0\n'), name
    validate_tests((BASE/receipt['receipts']['tests']).read_text(), inventory['linux'])
    for name in ['xp-09-full-windows-cfg.log', 'xp-21-windows-clippy.log']:
        assert (BASE/'test-artifacts'/name).read_text().endswith('EXIT: 0\n'), name
    # Bind evidence to exact bytes, not just the latest successful command name.
    for relative, digest in receipt['sha256'].items():
        assert hashlib.sha256((BASE/relative).read_bytes()).hexdigest() == digest, relative
    files = [BASE/'.gitignore', BASE/'Cargo.toml', BASE/'Cargo.lock', BASE/'README.md']
    files += sorted((BASE/'src').rglob('*.rs'))
    files += sorted((BASE/'scripts').glob('*.py'))
    files += sorted(p for p in (BASE/'contracts').glob('*') if p.name != 'source-manifest.json')
    hashes = {str(p.relative_to(BASE)): {'sha256':hashlib.sha256(p.read_bytes()).hexdigest(), 'bytes':p.stat().st_size} for p in files}
    changed = subprocess.check_output(['git','diff','--name-only'], cwd=REPO, text=True).splitlines()
    untracked = subprocess.check_output(['git','ls-files','--others','--exclude-standard'], cwd=REPO, text=True).splitlines()
    owned = 'rust/crates/furchive-downloads/'
    assert all(p.startswith(owned) for p in changed+untracked), changed+untracked
    changed = sorted(set(changed+untracked) | {owned+'contracts/source-manifest.json'})
    result = {'base_head':subprocess.check_output(['git','rev-parse','HEAD'],cwd=REPO,text=True).strip(),
              'scope':owned, 'changed_paths':changed, 'files':hashes,
              'inventory':{k:{'count':len(v),'sha256':hashlib.sha256(('\n'.join(v)+'\n').encode()).hexdigest()} for k,v in inventory.items()},
              'verification':{'linux':'native 44 tests +1 doctest, fmt, Clippy -Dwarnings PASS',
                              'windows':'GNU cfg check +Clippy all-targets PASS; runtime pending',
                              'macos':'missing Apple std/SDK; native build/runtime pending'},
              'native_receipt':'test-artifacts/native-linux/receipt.json'}
    path = BASE/'contracts/source-manifest.json'
    path.write_text(json.dumps(result,indent=2)+'\n')
    print(json.dumps(result,indent=2))

if __name__ == '__main__':
    main()
