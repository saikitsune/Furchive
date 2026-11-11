"""Check Windows backend alone with the already installed stable target, no linker."""
from pathlib import Path
import os
import subprocess
base = Path(__file__).resolve().parents[1]
art = base / 'test-artifacts'
env = dict(os.environ, HOME='/opt/data/home', RUSTUP_HOME='/opt/data/home/.rustup', RUSTUP_AUTO_INSTALL='0')
source = art / 'windows-cfg-probe.rs'
source.write_text('''#![allow(dead_code)]
#[derive(Debug, PartialEq, Eq)]
enum FileIdentity { Unix { device: u64, inode: u64 }, Windows { volume: u64, file: [u8;16] } }
#[derive(Clone, Copy)]
enum Access { Read, Create, Exclusive }
#[path = "../src/files/windows.rs"]
mod windows;
''')
command = ['/opt/data/home/.cargo/bin/rustc', '+stable', '--edition=2021', '--crate-type=lib', '--target=x86_64-pc-windows-gnu', '--emit=metadata', '-Dwarnings', str(source), '-o', str(art/'windows-cfg-probe.rmeta')]
result = subprocess.run(command, env=env, capture_output=True, text=True, timeout=90)
text = 'COMMAND: ' + ' '.join(command) + '\n' + result.stdout + result.stderr + '\nEXIT: ' + str(result.returncode) + '\n'
(art/'xp-05-windows-cfg.log').write_text(text)
print(text)
raise SystemExit(result.returncode)
