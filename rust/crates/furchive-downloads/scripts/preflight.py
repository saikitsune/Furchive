"""Read only this worker's selected route; do not expose credentials."""
import json
from pathlib import Path
import os
import subprocess
# Avoid importing optional profile dependencies or printing any secret fields.
lines = Path('/opt/data/profiles/bram/config.yaml').read_text().splitlines()
allowed = {'default', 'provider', 'service_tier', 'fallback_providers'}
in_model = False
for line in lines:
    key = line.strip().split(':', 1)[0]
    if line and not line.startswith(' '):
        in_model = key == 'model'
    if key in allowed and (in_model or not line.startswith(' ')):
        print(line)
env = dict(os.environ, HOME='/opt/data/home', RUSTUP_HOME='/opt/data/home/.rustup', RUSTUP_AUTO_INSTALL='0')
for args in [['/opt/data/home/.cargo/bin/rustc', '--version'],
             ['/opt/data/home/.cargo/bin/rustup', 'target', 'list', '--installed']]:
    subprocess.run(args, env=env, check=True)
