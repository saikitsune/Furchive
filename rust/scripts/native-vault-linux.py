"""Disposable Linux Secret Service for the native vault probe; no user session/keyring.
Run inside a fresh dbus-run-session. Private HOME/data/runtime under project output.
"""
from pathlib import Path
import argparse,os,secrets,subprocess,time,sys

def main():
 p=argparse.ArgumentParser(description=__doc__);p.add_argument('binary',type=Path);p.add_argument('--output',type=Path,required=True);p.add_argument('--account-ui',action='store_true');p.add_argument('--probe',type=Path);a=p.parse_args();root=a.output.resolve()
 if root.exists():raise SystemExit('refusing existing isolated Secret Service directory')
 root.mkdir(parents=True,mode=0o700);env=dict(os.environ)
 for key,leaf in [('HOME','home'),('XDG_DATA_HOME','data'),('XDG_CONFIG_HOME','config'),('XDG_RUNTIME_DIR','runtime')]:
  path=root/leaf;path.mkdir(mode=0o700);env[key]=str(path)
 control=root/'control';control.mkdir(mode=0o700)
 with (root/'secret-service.log').open('w') as log:
  daemon=subprocess.Popen(['gnome-keyring-daemon','--foreground','--unlock','--components=secrets','--control-directory',str(control)],env=env,stdin=subprocess.PIPE,stdout=log,stderr=log)
  try:
   if daemon.stdin is None:raise RuntimeError('missing owned keyring initialization input')
   daemon.stdin.write(secrets.token_hex(32).encode());daemon.stdin.close()
   deadline=time.monotonic()+10
   while True:
    if daemon.poll() is not None:raise RuntimeError('isolated Secret Service exited')
    probe=subprocess.run(['gdbus','call','--session','--dest','org.freedesktop.DBus','--object-path','/org/freedesktop/DBus','--method','org.freedesktop.DBus.NameHasOwner','org.freedesktop.secrets'],capture_output=True,text=True,env=env,timeout=2)
    if probe.returncode==0 and 'true' in probe.stdout:break
    if time.monotonic()>deadline:raise TimeoutError('isolated Secret Service startup')
    time.sleep(.05)
   script='account-ui-run-native.py' if a.account_ui else 'native-vault-run.py'
   argv=[sys.executable,str(Path(__file__).with_name(script)),str(a.binary.resolve()),'--output',str(root/'receipts')]
   if a.account_ui:
    if a.probe is None:raise ValueError('native account UI requires separate readback probe')
    argv+=['--probe',str(a.probe.resolve())]
   subprocess.run(argv,env=env,check=True,timeout=200 if a.account_ui else 100)
  finally:
   if daemon.poll() is None:daemon.terminate()
   try:daemon.wait(timeout=5)
   except subprocess.TimeoutExpired:daemon.kill();daemon.wait(timeout=3)
if __name__=='__main__':main()
