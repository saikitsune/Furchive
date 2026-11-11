"""Windows cfg Clippy check using existing stable target; does not run Windows."""
from pathlib import Path
import os
import subprocess
base = Path(__file__).resolve().parents[1]
env = dict(os.environ, HOME='/opt/data/home', RUSTUP_HOME='/opt/data/home/.rustup', RUSTUP_AUTO_INSTALL='0', CARGO_HOME=str(base/'test-artifacts/cargo-home'), CARGO_TARGET_DIR=str(base/'test-artifacts/target'))
env['PATH'] = '/opt/data/home/.cargo/bin:' + env['PATH']
command = ['cargo', '+stable', 'clippy', '--all-targets', '--target', 'x86_64-pc-windows-gnu', '--locked', '--', '-D', 'warnings']
result = subprocess.run(command, cwd=base, env=env, capture_output=True, text=True, timeout=200)
text = 'COMMAND: ' + ' '.join(command) + '\n' + result.stdout + result.stderr + '\nEXIT: ' + str(result.returncode) + '\n'
(base/'test-artifacts/xp-21-windows-clippy.log').write_text(text)
print(text)
raise SystemExit(result.returncode)
