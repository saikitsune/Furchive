"""Exercise real packaged UI -> Tauri IPC -> Rust -> harmless loopback HTTP/SQLite.
No live artwork, accounts, browser surrogate, installs or sandbox changes.
"""
from pathlib import Path
from http.server import BaseHTTPRequestHandler,ThreadingHTTPServer
import threading,urllib.parse,json,subprocess,argparse,os,hashlib,sqlite3
CHECKS={1:['include-exclude-ratings','autocomplete','pagination-submitted-query','favorites','ordered-pool-holes','pin-persist','saved-search-persist','preferences-save','product-gallery-thumbnails','legacy-preview-confirm',*[f'product-{id}-{kind}' for id,kind in [(100,'image'),(101,'animation'),(102,'animation'),(103,'animation'),(104,'video'),(105,'video')]]],2:['restart-state','saved-search-delete','unpin','preferences-restart','legacy-restart-delete']}
def validate(receipt,phase):
 if receipt.get('schema')!=1 or receipt.get('ok') is not True or receipt.get('phase')!=phase or sorted(receipt.get('checks',[]))!=sorted(CHECKS[phase]):raise ValueError('missing or invalid native browsing receipt')
 return True

def main():
 parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('binary',type=Path);parser.add_argument('--output',type=Path,required=True);args=parser.parse_args();out=args.output.resolve();binary=args.binary.resolve()
 if out.exists():raise SystemExit('refusing stale browsing evidence')
 out.mkdir(parents=True);requests=[]
 legacy=out/'legacy-fixture';legacy.mkdir();(legacy/'artwork.bin').write_bytes(b'owned harmless preservation sentinel')
 (legacy/'settings.json').write_text(json.dumps({'SavedSearches':json.dumps([{'Name':'Migrated fixture search','IncludeTags':['fox'],'ExcludeTags':[],'RatingFilterIndex':3}]),'E621ApiKey':'synthetic-key-must-not-import'}))
 legacy_hashes={p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in legacy.iterdir()}
 class Handler(BaseHTTPRequestHandler):
  def log_message(self,format,*args):pass
  def do_GET(self):
   url=urllib.parse.urlparse(self.path);pairs=urllib.parse.parse_qs(url.query);requests.append({'path':url.path,'query':pairs,'authorization':self.headers.get('Authorization') is not None,'userAgent':self.headers.get('User-Agent','')})
   if 'filter_id' in pairs or self.headers.get('Authorization'):self.send_error(403);return
   if url.path.startswith('/data/aa/bb/aabb0000000000000000000000000000.'):
    ext=url.path.rsplit('.',1)[-1];fixture={'png':'still.png','gif':'viewer-animation.gif','apng':'viewer-animation.apng','webp':'viewer-animation.webp','webm':'vp8.webm','mp4':'h264.mp4'}.get(ext)
    if fixture is None:self.send_error(404);return
    data=(Path(__file__).resolve().parents[1]/'apps/desktop/public/fixtures'/fixture).read_bytes();self.send_response(200);self.send_header('Content-Length',str(len(data)));self.end_headers();self.wfile.write(data);return
   def post(id):
    ext={100:'png',101:'gif',102:'apng',103:'webp',104:'webm',105:'mp4'}.get(id,'png')
    return {'id':id,'rating':'s','tags':{'artist':['local_fixture']},'description':'Harmless metadata only','preview':{'url':'https://static1.e621.net/data/aa/bb/aabb0000000000000000000000000000.png'},'file':{'url':f'https://static1.e621.net/data/aa/bb/aabb0000000000000000000000000000.{ext}','ext':ext},'score':{'total':1}}
   if url.path=='/tags.json':value=[{'name':'fox','category':0,'post_count':12}]
   elif url.path=='/pools/3.json':value={'id':3,'name':'Native fixture pool','post_ids':[10,2,30]}
   elif url.path=='/posts.json':
    if pairs.get('tags')==['id:10,2,30']:value={'posts':[post(2)]}
    elif pairs.get('page',['1'])==['2']:value={'posts':[]}
    else:value={'posts':[post(i+100) for i in range(50)]}
   else:self.send_error(404);return
   data=json.dumps(value).encode();self.send_response(200);self.send_header('Content-Type','application/json');self.send_header('Content-Length',str(len(data)));self.end_headers();self.wfile.write(data)
 server=ThreadingHTTPServer(('127.0.0.1',0),Handler);thread=threading.Thread(target=server.serve_forever,daemon=True);thread.start()
 try:
  for phase in (1,2):
   receipt=out/f'phase-{phase}.json';env=dict(os.environ,FURCHIVE_BROWSE_TEST_PHASE=str(phase),FURCHIVE_BROWSE_TEST_OUTPUT=str(receipt),FURCHIVE_QUALIFICATION_OUTPUT=str(receipt),FURCHIVE_BROWSE_FIXTURE_ORIGIN=f'http://127.0.0.1:{server.server_port}/',FURCHIVE_TEST_DATA_ROOT=str(out/'profile'))
   with (out/f'phase-{phase}.log').open('w') as log:
    subprocess.run([str(binary)],env=env,stdout=log,stderr=log,check=True,timeout=60)
   validate(json.loads(receipt.read_text()),phase)
  with sqlite3.connect(out/'profile/state.sqlite') as db:
   if db.execute('SELECT count(*) FROM saved_searches').fetchone()[0]!=0 or db.execute('SELECT count(*) FROM pinned_pools').fetchone()[0]!=0:raise ValueError('actual restart cleanup not persisted')
  if {p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in legacy.iterdir()}!=legacy_hashes:raise ValueError('legacy source or owned artwork changed')
  tags=[q['query'].get('tags',[''])[0] for q in requests if q['path']=='/posts.json'];required=['fox -comic rating:s order:id_desc','fox fav:fixture_user -comic rating:s order:id_desc','id:10,2,30']
  if not all(t in tags for t in required) or any(q['authorization'] or 'filter_id' in q['query'] or not q['userAgent'].startswith('Furchive/') for q in requests):raise ValueError('native HTTP/query policy mismatch')
  (out/'accepted.json').write_text(json.dumps({'sourceCommit':subprocess.check_output(['git','rev-parse','HEAD'],text=True).strip(),'binarySha256':hashlib.sha256(binary.read_bytes()).hexdigest(),'phases':[1,2],'requests':requests,'scope':'native metadata browsing and restart only','full_app_complete':False},indent=2))
  print('NATIVE BROWSING PASS: actual UI/IPC/Rust/HTTP/restart contracts; media/download/settings product integration incomplete')
 finally:
  (out/'requests.json').write_text(json.dumps(requests,indent=2));server.shutdown();server.server_close();thread.join(timeout=3)
if __name__=='__main__':main()
