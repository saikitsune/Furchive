"""Exercise real packaged UI -> Tauri IPC -> Rust -> harmless loopback HTTP/SQLite.
No live artwork, accounts, browser surrogate, installs or sandbox changes.
"""
from pathlib import Path
from http.server import BaseHTTPRequestHandler,ThreadingHTTPServer
import threading,urllib.parse,json,subprocess,argparse,os,hashlib,sqlite3,time
CHECKS={1:['include-exclude-ratings','autocomplete','pagination-submitted-query','favorites','ordered-pool-holes','pin-persist','saved-search-persist','preferences-save','filename-template-preview','presentation-preferences','product-gallery-thumbnails','legacy-preview-confirm','account-verify-logout','product-adjacent-navigation','pool-gallery','pool-download-group','bounded-parallel-downloads','whole-pool-queue','viewer-cross-page-search','viewer-cross-page-pool','download-pause-ack','download-complete','download-cancel','normalized-metadata-rename',*[f'product-{id}-{kind}' for id,kind in [(100,'image'),(101,'animation'),(102,'animation'),(103,'animation'),(104,'video'),(105,'video')]]],2:['restart-state','saved-search-delete','unpin','preferences-restart','legacy-restart-delete','download-history-restart','pool-group-restart','whole-pool-restart','presentation-preferences-restart','local-completed-media-restart','normalized-metadata-restart','cache-clear-preservation']}
def validate(receipt,phase):
 if receipt.get('schema')!=1 or receipt.get('ok') is not True or receipt.get('phase')!=phase or sorted(receipt.get('checks',[]))!=sorted(CHECKS[phase]):raise ValueError('missing or invalid native browsing receipt')
 if phase==1 and receipt.get('template_preview')!={'filename':'preview/12345.png','pool_filename':'e621/pools/sample_artist/Sample_pool/7_12345.png','invalid_rejected':True,'unsaved':True}:raise ValueError('invalid native filename preview evidence')
 return True

def validate_metadata(downloads,jobs,media):
 """Fixed harmless campaign contract; never a production renderer path API."""
 by_id={j['id']:j for j in jobs};failed=by_id[8];written=by_id[9]
 if any(by_id[i].get('metadata_status','NotRequested')!='NotRequested' for i in by_id if i<8):raise ValueError('old jobs adopted metadata')
 if failed['state']!='Completed' or failed['metadata_status']!='Failed' or not failed['metadata_error'] or written['state']!='Completed' or written['metadata_status']!='Written' or written['metadata_error'] is not None:raise ValueError('metadata acknowledgement mismatch')
 if failed['relative_path']!='e621/100.png' or written['relative_path']!='e621/100 (1).png':raise ValueError('metadata Rename target mismatch')
 if (downloads/'e621/100.png').read_bytes()!=media or (downloads/'e621/100 (1).png').read_bytes()!=media or (downloads/'e621/100.png.metadata.json').read_bytes()!=b'owned unrelated metadata must survive':raise ValueError('metadata collision/art preservation mismatch')
 data=(downloads/'e621/100 (1).png.metadata.json').read_bytes();value=json.loads(data)
 if len(data)>1048576 or value['representation']!='normalized-furchive-v1' or value['key']!={'source':'e621','id':'100'} or value['url']!='https://static1.e621.net/data/aa/bb/aabb0000000000000000000000000000.png':raise ValueError('normalized metadata shape/URL mismatch')
 if set(downloads.rglob('*.metadata.json'))!={downloads/'e621/100.png.metadata.json',downloads/'e621/100 (1).png.metadata.json'}:raise ValueError('unexpected metadata export')
 return data

def main():
 parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('binary',type=Path);parser.add_argument('--output',type=Path,required=True);args=parser.parse_args();out=args.output.resolve();binary=args.binary.resolve()
 if out.exists():raise SystemExit('refusing stale browsing evidence')
 out.mkdir(parents=True);requests=[];deny_media=False;transfer_lock=threading.Lock();active_transfers=0;peak_transfers=0
 legacy=out/'legacy-fixture';legacy.mkdir();(legacy/'artwork.bin').write_bytes(b'owned harmless preservation sentinel')
 (legacy/'settings.json').write_text(json.dumps({'SavedSearches':json.dumps([{'Name':'Migrated fixture search','IncludeTags':['fox'],'ExcludeTags':[],'RatingFilterIndex':3}]),'E621ApiKey':'synthetic-key-must-not-import'}))
 legacy_hashes={p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in legacy.iterdir()}
 downloads=out/'downloads-fixture';downloads.mkdir();(downloads/'existing-art.bin').write_bytes(b'owned existing download must survive')
 collision=downloads/'e621/100.png.metadata.json';collision.parent.mkdir();collision.write_bytes(b'owned unrelated metadata must survive')
 metadata_before_restart=None
 class Handler(BaseHTTPRequestHandler):
  def log_message(self,format,*args):pass
  def do_GET(self):
   nonlocal active_transfers,peak_transfers
   url=urllib.parse.urlparse(self.path);pairs=urllib.parse.parse_qs(url.query);requests.append({'path':url.path,'query':pairs,'authorization':self.headers.get('Authorization') is not None,'userAgent':self.headers.get('User-Agent',''),'range':self.headers.get('Range'),'ifRange':self.headers.get('If-Range'),'identity':self.headers.get('Accept-Encoding')=='identity'})
   if 'filter_id' in pairs or (self.headers.get('Authorization') and url.path!='/users/me.json'):self.send_error(403);return
   if url.path=='/users/me.json':
    data=b'{"id":123,"name":"fixture_user"}';self.send_response(200);self.send_header('Content-Length',str(len(data)));self.end_headers();self.wfile.write(data);return
   if deny_media and url.path.startswith('/data/'):
    self.send_error(403,'owned restart media transport disabled');return
   if url.path.startswith('/data/aa/bb/aabb0000000000000000000000000000.'):
    ext=url.path.rsplit('.',1)[-1];fixture={'png':'still.png','gif':'viewer-animation.gif','apng':'viewer-animation.apng','webp':'viewer-animation.webp','webm':'vp8.webm','mp4':'h264.mp4'}.get(ext)
    if fixture is None:self.send_error(404);return
    data=(Path(__file__).resolve().parents[1]/'apps/desktop/public/fixtures'/fixture).read_bytes()
    if self.headers.get('Accept-Encoding')=='identity':
     with transfer_lock:active_transfers+=1;peak_transfers=max(peak_transfers,active_transfers)
     start=int(self.headers.get('Range','bytes=0-').removeprefix('bytes=').split('-')[0]);part=data[start:];self.send_response(206 if start else 200);self.send_header('Content-Length',str(len(part)));self.send_header('ETag','"owned-v1"')
     if start:self.send_header('Content-Range',f'bytes {start}-{len(data)-1}/{len(data)}')
     self.end_headers()
     try:
      cuts=[0,max(1,len(part)//3),max(2,2*len(part)//3),len(part)]
      for a,b in zip(cuts,cuts[1:]):
       self.wfile.write(part[a:b]);self.wfile.flush();time.sleep(0.6)
     except (BrokenPipeError,ConnectionResetError):pass
     finally:
      with transfer_lock:active_transfers-=1
     return
    self.send_response(200);self.send_header('Content-Length',str(len(data)));self.end_headers();self.wfile.write(data);return
   def post(id):
    ext={100:'png',101:'gif',102:'apng',103:'webp',104:'webm',105:'mp4'}.get(id,'png')
    fixture={'png':'still.png','gif':'viewer-animation.gif','apng':'viewer-animation.apng','webp':'viewer-animation.webp','webm':'vp8.webm','mp4':'h264.mp4'}[ext];data=(Path(__file__).resolve().parents[1]/'apps/desktop/public/fixtures'/fixture).read_bytes()
    return {'id':id,'rating':'s','tags':{'artist':['local_fixture']},'description':'Harmless metadata only','preview':{'url':'https://static1.e621.net/data/aa/bb/aabb0000000000000000000000000000.png'},'file':{'url':None if id==149 else f'https://static1.e621.net/data/aa/bb/aabb0000000000000000000000000000.{ext}','ext':ext,'md5':hashlib.md5(data).hexdigest(),'size':len(data)},'score':{'total':1}}
   if url.path=='/tags.json':value=[{'name':'fox','category':0,'post_count':12}]
   elif url.path=='/pools/4.json':value={'id':4,'name':'Whole fixture pool','post_ids':list(range(1,106))}
   elif url.path=='/pools/3.json':value={'id':3,'name':'Native fixture pool','post_ids':[10,2,30]}
   elif url.path=='/posts.json':
    if pairs.get('tags')==['id:10,2,30']:value={'posts':[post(2)]}
    elif pairs.get('tags') in [['id:'+','.join(map(str,range(1,51)))],['id:'+','.join(map(str,range(1,101)))]]:value={'posts':[post(2)]}
    elif pairs.get('tags')==['id:101,102,103,104,105']:value={'posts':[post(103)]}
    elif pairs.get('tags')==['id:'+','.join(map(str,range(51,101)))]:value={'posts':[]}
    elif pairs.get('page',['1'])==['2']:value={'posts':[post(200)]}
    else:value={'posts':[post(i+100) for i in range(50)]}
   else:self.send_error(404);return
   data=json.dumps(value).encode();self.send_response(200);self.send_header('Content-Type','application/json');self.send_header('Content-Length',str(len(data)));self.end_headers();self.wfile.write(data)
 server=ThreadingHTTPServer(('127.0.0.1',0),Handler);thread=threading.Thread(target=server.serve_forever,daemon=True);thread.start()
 try:
  for phase in (1,2):
   deny_media=phase==2
   receipt=out/f'phase-{phase}.json';env=dict(os.environ,FURCHIVE_BROWSE_TEST_PHASE=str(phase),FURCHIVE_BROWSE_TEST_OUTPUT=str(receipt),FURCHIVE_QUALIFICATION_OUTPUT=str(receipt),FURCHIVE_BROWSE_FIXTURE_ORIGIN=f'http://127.0.0.1:{server.server_port}/',FURCHIVE_TEST_DATA_ROOT=str(out/'profile'))
   with (out/f'phase-{phase}.log').open('w') as log:
    subprocess.run([str(binary)],env=env,stdout=log,stderr=log,check=True,timeout=60)
   validate(json.loads(receipt.read_text()),phase)
   with sqlite3.connect(out/'profile/downloads.sqlite') as db:
    jobs=[json.loads(row[0]) for row in db.execute('SELECT payload FROM jobs ORDER BY id')]
   current_metadata=validate_metadata(downloads,jobs,(Path(__file__).resolve().parents[1]/'apps/desktop/public/fixtures/still.png').read_bytes())
   if phase==1:metadata_before_restart=current_metadata
   elif current_metadata!=metadata_before_restart:raise ValueError('metadata changed across restart')
  with sqlite3.connect(out/'profile/cache.sqlite') as cache:
   for table in ['posts','pools','pool_posts']:
    if cache.execute('SELECT count(*) FROM '+table).fetchone()[0]!=0:raise ValueError('acknowledged cache clear left persistent metadata')
  settings=json.loads((out/'profile/settings.json').read_text())
  if settings['gallery_scale']!=1.5 or settings['cache_limit_bytes']!=16777216 or settings['start_muted'] is not False or settings['duplicates']!='rename':raise ValueError('persisted presentation preference mismatch')
  with sqlite3.connect(out/'profile/state.sqlite') as db:
   if db.execute('SELECT count(*) FROM saved_searches').fetchone()[0]!=0 or db.execute('SELECT count(*) FROM pinned_pools').fetchone()[0]!=0:raise ValueError('actual restart cleanup not persisted')
  if {p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in legacy.iterdir()}!=legacy_hashes:raise ValueError('legacy source or owned artwork changed')
  if not any(q['identity'] and q['range'] and q['ifRange']=='"owned-v1"' for q in requests):raise ValueError('native pause/resume never issued validated Range request')
  expected=(Path(__file__).resolve().parents[1]/'apps/desktop/public/fixtures/still.png').read_bytes()
  if (downloads/'e621/local_fixture/100.png').read_bytes()!=expected or (downloads/'existing-art.bin').read_bytes()!=b'owned existing download must survive':raise ValueError('download artifact/preservation mismatch')
  if (downloads/'e621/pools/local_fixture/Native_fixture_pool/2_2.png').read_bytes()!=expected:raise ValueError('pool group ordinal/artifact mismatch')
  if peak_transfers<2 or peak_transfers>4:raise ValueError('actual native parallel-transfer bound not demonstrated')
  for id,fixture in [('100','still.png'),('101','viewer-animation.gif')]:
   ext='png' if id=='100' else 'gif';expected_parallel=(Path(__file__).resolve().parents[1]/'apps/desktop/public/fixtures'/fixture).read_bytes()
   if (downloads/f'e621/parallel/{id}/{id}.{ext}').read_bytes()!=expected_parallel:raise ValueError('parallel download bytes mismatch')
  with sqlite3.connect(out/'profile/downloads.sqlite') as db:
   whole=[json.loads(row[0]) for row in db.execute('SELECT payload FROM jobs ORDER BY id') if json.loads(row[0]).get('group_label')=='Whole fixture pool']
   if len(whole)!=2 or [j['group_position'] for j in whole]!=[1,102] or [j['state'] for j in whole]!=['Cancelled','Completed']:raise ValueError('whole-pool persistence/position mismatch')
  whole_bytes=(Path(__file__).resolve().parents[1]/'apps/desktop/public/fixtures/viewer-animation.webp').read_bytes()
  if (downloads/'e621/pools/local_fixture/Whole_fixture_pool/103_103.webp').read_bytes()!=whole_bytes:raise ValueError('whole-pool original ordinal artifact mismatch')
  tags=[q['query'].get('tags',[''])[0] for q in requests if q['path']=='/posts.json'];required=['fox -comic rating:s order:id_desc','fox fav:fixture_user -comic rating:s order:id_desc','id:10,2,30','id:'+','.join(map(str,range(1,101))),'id:101,102,103,104,105']
  if not all(t in tags for t in required) or any((q['authorization'] and q['path']!='/users/me.json') or 'filter_id' in q['query'] or not q['userAgent'].startswith('Furchive/') for q in requests):raise ValueError('native HTTP/query policy mismatch')
  (out/'accepted.json').write_text(json.dumps({'sourceCommit':subprocess.check_output(['git','rev-parse','HEAD'],text=True).strip(),'binarySha256':hashlib.sha256(binary.read_bytes()).hexdigest(),'phases':[1,2],'peak_parallel_transfers':peak_transfers,'requests':requests,'local_completed_media':{'restart_media_http':'refused','publication_read':'required'},'scope':'packaged browsing/download/media/account/settings bounded campaigns','full_app_complete':False},indent=2))
  print('NATIVE BROWSING PASS: actual UI/IPC/Rust/HTTP/restart contracts; media/download/settings product integration incomplete')
 finally:
  (out/'requests.json').write_text(json.dumps(requests,indent=2));server.shutdown();server.server_close();thread.join(timeout=3)
if __name__=='__main__':main()
