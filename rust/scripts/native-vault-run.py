"""Four separate real native vault processes; synthetic data in unique test entry.
Run only in disposable CI/explicit isolated environment, never the user's keyring.
"""
from pathlib import Path
import argparse,json,platform,secrets,subprocess

def validate(text,phase,scope,system):
 try:r=json.loads(text)
 except (ValueError,TypeError) as e:raise ValueError('missing native vault receipt') from e
 if r!={'schema':1,'phase':phase,'namespace':scope,'native_store':system,'ok':True,'synthetic_data':True}:raise ValueError('native vault phase identity/nonpass')
 return True

def main():
 p=argparse.ArgumentParser(description=__doc__);p.add_argument('binary',type=Path);p.add_argument('--output',type=Path,required=True);args=p.parse_args()
 root=args.output.resolve();root.mkdir(parents=True,exist_ok=False);scope='qualification-'+secrets.token_hex(16);binary=args.binary.resolve();system={'Linux':'linux','Darwin':'macos','Windows':'windows'}[platform.system()];completed=[]
 try:
  for phase in ('write','read','delete','absent'):
   r=subprocess.run([str(binary),phase,scope],capture_output=True,text=True,timeout=20)
   (root/(phase+'.log')).write_text(r.stdout+r.stderr)
   if r.returncode:raise RuntimeError('native vault phase failed: '+phase)
   validate(r.stdout,phase,scope,system);completed.append(phase)
   (root/'progress.json').write_text(json.dumps({'namespace':scope,'completed':completed,'native_store':system},indent=2))
  (root/'accepted.json').write_text(json.dumps({'schema':1,'native_store':system,'phases':completed,'synthetic_data':True,'native_protected_store':True,'full_app_complete':False},indent=2))
  print('NATIVE VAULT PASS: isolated real protected entry persisted/read/deleted across processes; prompts/locked-user-store remain separate')
 finally:
  if completed and 'delete' not in completed:
   cleanup=subprocess.run([str(binary),'delete',scope],capture_output=True,text=True,timeout=20);(root/'cleanup.log').write_text(cleanup.stdout+cleanup.stderr)
if __name__=='__main__':main()
