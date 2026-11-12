"""Run the real native fixture campaign; capture ONLY its verified client rectangle.
No browser surrogate, sandbox disabling, user media, general screenshot IPC or install.
"""
from pathlib import Path
import argparse
import ctypes
import hashlib
import importlib.util
import json
import os
import platform
import subprocess
import sys
import time

ROOT=Path(__file__).resolve().parents[1]

def window_identity(pid):
    if sys.platform=='win32':
        user=ctypes.windll.user32
        user.GetForegroundWindow.restype=ctypes.c_void_p
        handle=user.GetForegroundWindow();owner=ctypes.c_ulong()
        user.GetWindowThreadProcessId(ctypes.c_void_p(handle),ctypes.byref(owner))
        if owner.value!=pid:raise ValueError('owned Windows client is not foreground')
        return str(handle)
    if sys.platform=='darwin':
        import Quartz
        windows=Quartz.CGWindowListCopyWindowInfo(Quartz.kCGWindowListOptionOnScreenOnly,Quartz.kCGNullWindowID)
        visible=[w for w in windows if w.get(Quartz.kCGWindowLayer)==0 and w.get(Quartz.kCGWindowAlpha,0)>0]
        if not visible or visible[0].get(Quartz.kCGWindowOwnerPID)!=pid:raise ValueError('owned macOS window is not foreground')
        return str(visible[0][Quartz.kCGWindowNumber])
    window=subprocess.check_output(['xdotool','getactivewindow'],text=True,timeout=3).strip()
    owner=subprocess.check_output(['xdotool','getwindowpid',window],text=True,timeout=3).strip()
    if int(owner)!=pid:raise ValueError('owned X11 client is not foreground')
    return window

class QuartzWindowScreen:
    """Capture one verified owned window, not a composition of the whole desktop."""
    def __init__(self,quartz,window_id):self.quartz=quartz;self.window_id=int(window_id)
    def __enter__(self):return self
    def __exit__(self,*args):return False
    def grab(self,region):
        from PIL import Image
        from types import SimpleNamespace
        q=self.quartz
        rect=q.CGRectMake(region['left'],region['top'],region['width'],region['height'])
        image=q.CGWindowListCreateImage(rect,q.kCGWindowListOptionIncludingWindow,self.window_id,q.kCGWindowImageNominalResolution)
        if image is None:raise RuntimeError('owned-window Quartz capture failed')
        width=q.CGImageGetWidth(image);height=q.CGImageGetHeight(image);stride=q.CGImageGetBytesPerRow(image)
        data=bytes(q.CGDataProviderCopyData(q.CGImageGetDataProvider(image)))
        pixels=Image.frombytes('RGBA',(width,height),data,'raw','BGRA',stride,1).convert('RGB')
        return SimpleNamespace(size=(width,height),rgb=pixels.tobytes())

def capture_pixels(screen,rect,verify,now=time.monotonic,sleep=time.sleep):
    """Sample owned raw pixels first; expensive PNG compression happens later."""
    samples=[]
    for _ in range(12):
        before=now();verify()
        shot=screen.grab({'left':rect['x'],'top':rect['y'],'width':rect['width'],'height':rect['height']})
        samples.append((before*1000,shot.size,bytes(shot.rgb)))
        sleep(max(0,.08-(now()-before)))
    return samples

def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('binary',type=Path)
    parser.add_argument('--output',type=Path,required=True)
    args=parser.parse_args();binary=args.binary.resolve();output=args.output.resolve()
    if output.exists():raise SystemExit('refusing stale native campaign evidence')
    output.mkdir(parents=True)
    if sys.platform=='win32':ctypes.windll.user32.SetProcessDPIAware()
    binary_hash=hashlib.sha256(binary.read_bytes()).hexdigest()
    head=subprocess.check_output(['git','rev-parse','HEAD'],cwd=ROOT.parent,text=True).strip()
    candidate=hashlib.sha256(head.encode()).hexdigest()
    files=sorted((ROOT/'apps/desktop/src').rglob('*'))
    source_hash=hashlib.sha256(b''.join(str(f.relative_to(ROOT)).encode()+b'\0'+f.read_bytes()+b'\0' for f in files if f.is_file())).hexdigest()
    system=platform.system();native={'Linux':('linux','WebKitGTK','X11'),'Windows':('windows','WebView2','Windows desktop'),'Darwin':('macos','WKWebView','macOS WindowServer')}[system]
    report={'schema':1,'platform':native[0],'engine':native[1],'display':native[2],'candidateSha256':candidate,'executableSha256':binary_hash,'sourceSha256':source_hash,'sourceCommit':head,'captures':[]}
    env=dict(os.environ,FURCHIVE_MEDIA_TEST_ROOT=str(output),FURCHIVE_TEST_DATA_ROOT=str(output/'profile'))
    # Keep diagnostics on disk even if app/driver fails. The child is owned and reaped.
    with (output/'native.log').open('w') as log:
        child=subprocess.Popen([str(binary)],env=env,stdout=log,stderr=log)
        report['pid']=child.pid
        try:
            import mss
            from PIL import Image
            deadline=time.monotonic()+100
            for checkpoint in ('gif','apng','webp'):
                request=output/f'{checkpoint}.request.json'
                while not request.exists():
                    if child.poll() is not None:raise RuntimeError('native app exited before capture checkpoint')
                    if time.monotonic()>deadline:raise TimeoutError('native campaign checkpoint deadline')
                    time.sleep(.025)
                payload=json.loads(request.read_text())
                if payload['pid']!=child.pid or payload['checkpoint']!=checkpoint:raise ValueError('checkpoint child identity mismatch')
                rect=payload['client'];spec=payload['request']
                if spec['frames']!=12 or spec['intervalMs']!=80:raise ValueError('unexpected capture request')
                # set_focus is requested by the owned native app before request publication.
                wid=window_identity(child.pid);report['windowId']=wid
                capture={'checkpoint':checkpoint,'fixtureSha256':spec['fixtureSha256'],'viewport':spec['viewport'],'rect':spec['rect'],'clientPhysical':{'width':rect['width'],'height':rect['height']},'foreground':True,'frames':[]}
                def verify_foreground():
                    if window_identity(child.pid)!=wid:raise ValueError('foreground window changed during capture')
                if sys.platform=='darwin':
                    import Quartz
                    capture_context=QuartzWindowScreen(Quartz,wid)
                else:capture_context=mss.mss()
                with capture_context as screen:
                    samples=capture_pixels(screen,rect,verify_foreground)
                for i,(timestamp,size,pixels) in enumerate(samples):
                    path=output/f'{checkpoint}-{i}.png';Image.frombytes('RGB',size,pixels).save(path,compress_level=1)
                    capture['frames'].append({'file':path.name,'sha256':hashlib.sha256(path.read_bytes()).hexdigest(),'monotonicMs':timestamp})
                report['captures'].append(capture)
                (output/'capture.json').write_text(json.dumps(report,indent=2))
                (output/f'{checkpoint}.ack').write_bytes(b'OK')
            exit_code=child.wait(timeout=max(1,deadline-time.monotonic()))
            if exit_code!=0:raise RuntimeError(f'native campaign exit {exit_code}')
        finally:
            if child.poll() is None:child.terminate()
            try:child.wait(timeout=5)
            except subprocess.TimeoutExpired:child.kill();child.wait(timeout=5)
    subprocess.run(['node','--experimental-strip-types','--input-type=module','-e',"import fs from 'node:fs';import {validateMediaEvidence} from './src/lib/media-evidence.ts';validateMediaEvidence(JSON.parse(fs.readFileSync(process.argv[1],'utf8')));",str(output/'runtime.json')],cwd=ROOT/'apps/desktop',check=True,timeout=15)
    spec=importlib.util.spec_from_file_location('capture_validator',ROOT/'scripts/media-validate-capture.py')
    if spec is None or spec.loader is None:raise RuntimeError('capture validator missing')
    validator=importlib.util.module_from_spec(spec);spec.loader.exec_module(validator)
    fixtures=json.loads((ROOT/'apps/desktop/public/fixtures/viewer-manifest.json').read_text())
    validator.validate(report,output,fixtures,candidate,binary_hash,source_hash)
    (output/'accepted.json').write_text(json.dumps({'native_runtime':True,'sourceCommit':head,'executableSha256':binary_hash,'actual_host_frame_progression':['gif','apng','webp'],'full_app_complete':False},indent=2))
    print('NATIVE MEDIA CAMPAIGN PASS: real viewer lifecycle, rendered animation frames and measured gallery bounds; full app incomplete')

if __name__=='__main__':main()
