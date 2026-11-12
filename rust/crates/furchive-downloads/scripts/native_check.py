"""Native Linux/macOS/Windows gate. No install, network media, or cfg-only pass.
Run with Python3.10+ and cargo/rustc/rustfmt/clippy already on PATH.
"""
from pathlib import Path
import hashlib
import json
import os
import re
import subprocess
import sys

BASE = Path(__file__).resolve().parents[1]

def validate_tests(text, expected):
    ids = re.findall(r'^test (tests::\S+) \.\.\. ok$', text, re.M)
    receipt = f'test result: ok. {len(expected)} passed; 0 failed; 0 ignored; 0 measured; 0 filtered out;'
    if (len(ids) != len(set(ids)) or sorted(ids) != sorted(expected) or receipt not in text
            or re.search(r'^(?:warning|error)(?:\[|:)', text, re.M)
            or re.search(r'^test \S+ \.\.\. (?!ok$)', text, re.M)):
        raise ValueError('missing/duplicate/ignored/unexpected test or bad success receipt')


def run():
    inventory = json.loads((BASE/'contracts/test-inventory.json').read_text())
    expected = inventory[sys.platform]
    artifacts = BASE/'test-artifacts'/('native-'+sys.platform)
    artifacts.mkdir(parents=True, exist_ok=True)
    env = dict(os.environ, CARGO_TARGET_DIR=str(artifacts/'target'))
    # This opt-in local override uses the previously installed toolchain only.
    if '--local-toolchain' in sys.argv:
        # Installed host toolchain only; native CI uses its own normal PATH.
        env.update(HOME='/opt/data/home', RUSTUP_HOME='/opt/data/home/.rustup',
                   RUSTUP_AUTO_INSTALL='0', CARGO_HOME=str(BASE/'test-artifacts/cargo-home'))
        env['PATH'] = '/opt/data/home/.cargo/bin:' + env['PATH']
    receipts = {}
    def command(label, args):
        result = subprocess.run(args, cwd=BASE, env=env, capture_output=True, text=True, timeout=240)
        output = result.stdout + result.stderr
        path = artifacts/(label+'.log')
        path.write_text('COMMAND: '+' '.join(args)+'\n'+output+'\nEXIT: '+str(result.returncode)+'\n')
        print(output, flush=True)
        if result.returncode or re.search(r'^(?:warning|error)(?:\[|:)', output, re.M):
            raise RuntimeError('native gate failed: '+label)
        receipts[label] = str(path.relative_to(BASE))
        return output
    command('versions', ['rustc', '--version', '--verbose'])
    listing = command('inventory', ['cargo', 'test', '--lib', '--locked', '--', '--list'])
    found = re.findall(r'^(tests::\S+): test$', listing, re.M)
    if len(found) != len(set(found)) or sorted(found) != expected:
        raise ValueError('native discovery differs from reviewed inventory')
    # Single harness thread makes transcripts unambiguous; async/concurrency tests
    # still run actual Tokio tasks/threads/processes internally.
    output = command('tests', ['cargo', 'test', '--all-targets', '--locked', '--', '--test-threads=1'])
    validate_tests(output, expected)
    feature_output=command('fixture-tests',['cargo','test','--all-targets','--features','fixture-http','--locked','--','--test-threads=1'])
    validate_tests(feature_output,expected+['tests::explicit_fixture_engine_downloads_real_bytes_and_refuses_nonloopback_origin'])
    doc = command('doc', ['cargo', 'test', '--doc', '--locked'])
    if '1 passed; 0 failed; 0 ignored' not in doc:
        raise ValueError('README doctest missing')
    command('fmt', ['cargo', 'fmt', '--', '--check'])
    command('clippy', ['cargo', 'clippy', '--all-targets', '--locked', '--', '-D', 'warnings'])
    files = [BASE/'Cargo.toml', BASE/'Cargo.lock', BASE/'README.md']
    files += sorted((BASE/'src').rglob('*.rs'))
    files += sorted((BASE/'scripts').glob('*.py'))
    files += sorted(p for p in (BASE/'contracts').glob('*') if p.name != 'source-manifest.json')
    hashes = {str(p.relative_to(BASE)): hashlib.sha256(p.read_bytes()).hexdigest() for p in files if p.is_file()}
    result = {'platform':sys.platform, 'native_runtime':True, 'tests':len(expected),
              'test_ids':expected, 'doctests':1, 'receipts':receipts, 'sha256':hashes}
    (artifacts/'receipt.json').write_text(json.dumps(result, indent=2)+'\n')
    print(json.dumps({'platform':sys.platform, 'tests':len(expected), 'receipt':str(artifacts/'receipt.json')}))

if __name__ == '__main__':
    run()
