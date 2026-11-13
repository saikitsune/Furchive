"""Exercise rendered account UI paired with real isolated OS credential storage.
Disposable hosted desktop only; synthetic entry, harmless loopback identity endpoint.
"""
from pathlib import Path
from http.server import BaseHTTPRequestHandler,ThreadingHTTPServer
import argparse,base64,hashlib,hmac,json,os,platform,secrets,subprocess,threading
CHECKS={3:['anonymous-start','verified-login-native-save','masked-key-cleared'],4:['anonymous-restart','explicit-native-restore','logout-native-delete']}
def validate(receipt,phase,scope,system):
 if (receipt.get('schema')!=1 or receipt.get('ok') is not True or receipt.get('phase')!=phase or receipt.get('credential_store')!='native' or receipt.get('native_store')!=system or receipt.get('namespace')!=scope or sorted(receipt.get('checks',[]))!=sorted(CHECKS[phase])):raise ValueError('native account UI receipt invalid')
 return True
def allowed_request(path,authorized):return path in ('/users/me.json','/users/me.json?') and authorized is True
def main():
 p=argparse.ArgumentParser(description=__doc__);p.add_argument('binary',type=Path);p.add_argument('--output',type=Path,required=True);p.add_argument('--probe',type=Path,required=True);a=p.parse_args();root=a.output.resolve()
 if root.exists():raise SystemExit('refusing stale account UI evidence')
 root.mkdir(parents=True);scope='qualification-'+secrets.token_hex(16);system={'Linux':'linux','Darwin':'macos','Windows':'windows'}[platform.system()];binary=a.binary.resolve();probe=a.probe.resolve();requests=[];completed=[]
 synthetic='synthetic-native-vault-qualification-only';expected='Basic '+base64.b64encode(('fixture_user:'+synthetic).encode()).decode()
 class Handler(BaseHTTPRequestHandler):
  def log_message(self,format,*args):pass
  def do_GET(self):
   authorized=hmac.compare_digest(self.headers.get('Authorization',''),expected);requests.append({'path':self.path,'authorized':authorized,'user_agent':self.headers.get('User-Agent','')})
   if not allowed_request(self.path,authorized):self.send_error(403);return
   body=b'{"id":123,"name":"fixture_user"}';self.send_response(200);self.send_header('Content-Length',str(len(body)));self.end_headers();self.wfile.write(body)
 server=ThreadingHTTPServer(('127.0.0.1',0),Handler);thread=threading.Thread(target=server.serve_forever,daemon=True);thread.start()
 def vault(phase,label=None):
  result=subprocess.run([str(probe),phase,scope],capture_output=True,text=True,timeout=20);(root/f'vault-{label or phase}.log').write_text(result.stdout+result.stderr)
  if result.returncode:raise RuntimeError('native vault readback failed: '+phase)
  row=json.loads(result.stdout)
  if row!={'schema':1,'phase':phase,'namespace':scope,'native_store':system,'ok':True,'synthetic_data':True}:raise ValueError('native vault readback receipt')
 owned=False
 try:
  vault('absent','initial-absent');owned=True
  for phase in (3,4):
   output=root/f'phase-{phase}.json';env=dict(os.environ,FURCHIVE_BROWSE_TEST_PHASE=str(phase),FURCHIVE_BROWSE_TEST_OUTPUT=str(output),FURCHIVE_QUALIFICATION_OUTPUT=str(output),FURCHIVE_BROWSE_FIXTURE_ORIGIN=f'http://127.0.0.1:{server.server_port}/',FURCHIVE_TEST_DATA_ROOT=str(root/'profile'),FURCHIVE_ACCOUNT_UI_NAMESPACE=scope)
   with (root/f'phase-{phase}.log').open('w') as log:subprocess.run([str(binary)],env=env,stdout=log,stderr=log,check=True,timeout=45)
   validate(json.loads(output.read_text()),phase,scope,system);completed.append(phase)
   if phase==3:vault('read')
  vault('absent','final-absent')
  if len(requests)!=2 or not all(r['authorized'] and allowed_request(r['path'],True) and r['user_agent'].startswith('Furchive/') for r in requests):raise ValueError('actual account UI request inventory')
  for path in (root/'profile').glob('*.json'):
   if synthetic in path.read_text():raise ValueError('synthetic credential persisted as plaintext settings')
  accepted={'schema':1,'sourceCommit':subprocess.check_output(['git','rev-parse','HEAD'],text=True).strip(),'binarySha256':hashlib.sha256(binary.read_bytes()).hexdigest(),'phases':completed,'namespace':scope,'native_store':system,'credential_store':'native','synthetic_data':True,'entry_deleted':True,'requests':requests,'full_app_complete':False}
 finally:
  try:
   if owned:
    cleanup=subprocess.run([str(probe),'delete',scope],capture_output=True,text=True,timeout=20);(root/'cleanup.log').write_text(cleanup.stdout+cleanup.stderr)
    if cleanup.returncode:raise RuntimeError('qualification entry cleanup failed')
  finally:
   (root/'requests.json').write_text(json.dumps(requests,indent=2));server.shutdown();server.server_close();thread.join(timeout=3)
 (root/'accepted.json').write_text(json.dumps(accepted,indent=2));print('ACCOUNT UI NATIVE PASS: rendered login/save/restart/explicit restore/logout with real protected store and separate-process readback')
if __name__=='__main__':main()
