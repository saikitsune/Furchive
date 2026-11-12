"""Validate host-captured native frames. No canvas or generated frames are proof.
The caller supplies expected artifact/source/executable hashes independently.
This verifies the contract, not the honesty of a hostile host or provenance by itself.
"""
from pathlib import Path
import argparse
import hashlib
import json
import math
import re
from statistics import median
from typing import cast
from PIL import Image

def validate(receipt, root: Path, fixtures, candidate: str, executable: str, source: str):
    def require(ok, message):
        if not ok:
            raise ValueError('native capture: '+message)
    require(receipt.get('schema')==1,'schema')
    for name,expected in [('candidateSha256',candidate),('executableSha256',executable),('sourceSha256',source)]:
        require(bool(re.fullmatch('[0-9a-f]{64}',expected)) and receipt.get(name)==expected,name)
    require((receipt.get('platform'),receipt.get('engine')) in [('linux','WebKitGTK'),('windows','WebView2'),('macos','WKWebView')],'actual native engine')
    require(isinstance(receipt.get('pid'),int) and receipt['pid']>0 and bool(receipt.get('windowId')),'owned process/window identity')
    require(receipt.get('display') in ['X11','Windows desktop','macOS WindowServer'],'declared supported capture display')
    captures=receipt.get('captures',[])
    require([c.get('checkpoint') for c in captures]==['gif','apng','webp'],'exact capture inventory')
    for capture in captures:
        name='viewer-animation.'+capture['checkpoint']
        expected=next(row['sha256'] for row in fixtures['files'] if row['name']==name)
        require(capture.get('fixtureSha256')==expected,'owned fixture hash')
        require(capture.get('foreground') is True,'known harmless foreground window')
        viewport=capture['viewport'];physical=capture['clientPhysical'];rect=capture['rect']
        require(all(isinstance(n,(int,float)) and math.isfinite(n) for n in [viewport['width'],viewport['height'],viewport['dpr'],*rect.values()]),'finite geometry')
        dpr=viewport['dpr'];require(0.5<=dpr<=4,'scale')
        require(abs(physical['width']-viewport['width']*dpr)<=2 and abs(physical['height']-viewport['height']*dpr)<=2,'client geometry/scale identity')
        require(rect['width']>0 and rect['height']>0 and rect['x']>=0 and rect['y']>=0 and rect['x']+rect['width']<=viewport['width'] and rect['y']+rect['height']<=viewport['height'],'fixture in client area')
        frames=capture.get('frames',[]);require(len(frames)==12,'12 raw host frames')
        states=[];times=[]
        for frame in frames:
            leaf=frame['file'];require(isinstance(leaf,str) and bool(re.fullmatch(r'[A-Za-z0-9_-]+\.png',leaf)),'frame filename')
            file=root/leaf;require(file.is_file() and not file.is_symlink(),'raw frame file')
            require(hashlib.sha256(file.read_bytes()).hexdigest()==frame['sha256'],'raw frame hash')
            timestamp=frame['monotonicMs'];require(isinstance(timestamp,(int,float)) and math.isfinite(timestamp),'capture timestamp');times.append(timestamp)
            with Image.open(file) as image:
                require(image.size==(physical['width'],physical['height']),'unscaled client capture dimensions')
                center_x=round((rect['x']+rect['width']/2)*dpr);center_y=round((rect['y']+rect['height']/2)*dpr)
                crop=image.convert('RGB').crop((center_x-3,center_y-3,center_x+4,center_y+4))
                pixels=[cast(tuple[int,int,int],crop.getpixel((x,y))) for y in range(7) for x in range(7)];sample=[median(p[channel] for p in pixels) for channel in range(3)]
                distances=[max(abs(sample[i]-color[i]) for i in range(3)) for color in fixtures['animation']['colors']]
                state=min(range(3),key=lambda i:distances[i]);require(distances[state]<=24,'owned fixture pixel state');states.append(state)
        require(all(30<=b-a<=200 for a,b in zip(times,times[1:])) and times[-1]-times[0]>=700,'bounded monotonic frame cadence')
        require(len(set(states))==3 and sum(a!=b for a,b in zip(states,states[1:]))>=2,'actual animated frame progression (frozen image refused)')
    return True

if __name__=='__main__':
    parser=argparse.ArgumentParser()
    parser.add_argument('receipt',type=Path)
    parser.add_argument('--candidate-sha256',required=True)
    parser.add_argument('--executable-sha256',required=True)
    parser.add_argument('--source-sha256',required=True)
    args=parser.parse_args()
    fixtures=json.loads((Path(__file__).resolve().parents[1]/'apps/desktop/public/fixtures/viewer-manifest.json').read_text())
    validate(json.loads(args.receipt.read_text()),args.receipt.parent,fixtures,args.candidate_sha256,args.executable_sha256,args.source_sha256)
    print('HOST FRAME CONTRACT PASS: GIF/APNG/WebP pixel progression; not audio/GPU/fullscreen/Wayland proof')
