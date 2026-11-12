"""Additive harmless viewer fixtures. Never overwrite the original qualification corpus."""
from pathlib import Path
import hashlib
import json
from PIL import Image, ImageDraw

COLORS = [(224, 48, 64), (32, 192, 96), (48, 96, 224)]

def generate(root: Path) -> dict:
    root.mkdir(parents=True, exist_ok=True)
    frames = []
    for color in COLORS:
        image = Image.new('RGB', (320, 200), color)
        # Fixed black corners and a large uniform center permit robust host screenshot sampling.
        draw = ImageDraw.Draw(image)
        for box in [(0,0,19,19),(300,0,319,19),(0,180,19,199),(300,180,319,199)]:
            draw.rectangle(box, fill=(0,0,0))
        frames.append(image)
    frames[0].save(root/'viewer-animation.gif', save_all=True, append_images=frames[1:], duration=240, loop=0, optimize=False, disposal=2)
    frames[0].save(root/'viewer-animation.apng', format='PNG', save_all=True, append_images=frames[1:], duration=240, loop=0, disposal=0, blend=0)
    frames[0].save(root/'viewer-animation.webp', save_all=True, append_images=frames[1:], duration=240, loop=0, lossless=True)
    large = Image.new('RGB', (4096,3072), (55,88,121))
    ImageDraw.Draw(large).rectangle((1024,768,3072,2304), fill=(210,175,95))
    large.save(root/'viewer-large.png')
    # Tall but modest allocation; exceeds the edge budget without a decompression bomb.
    Image.new('RGB', (16385,2048), (96,64,112)).save(root/'viewer-oversized.png')
    (root/'viewer-corrupt.png').write_bytes(b'Furchive owned intentionally invalid media\x00')
    # Deliberately missing IHDR body, not a decoder-dependent partial raster.
    (root/'viewer-truncated.png').write_bytes(b'\x89PNG\r\n\x1a\n\x00\x00\x00\rIHDR\x00\x00')
    names = ['viewer-animation.gif','viewer-animation.apng','viewer-animation.webp','viewer-large.png','viewer-oversized.png','viewer-corrupt.png','viewer-truncated.png']
    manifest = {'schema':1,'ownership':'original generated geometric pixels; no user media',
        'animation':{'width':320,'height':200,'frame_ms':240,'frames':3,'colors':COLORS,'sample_normalized':[0.5,0.5]},
        'files':[{'name':name,'bytes':(root/name).stat().st_size,'sha256':hashlib.sha256((root/name).read_bytes()).hexdigest()} for name in names]}
    (root/'viewer-manifest.json').write_text(json.dumps(manifest,indent=2)+'\n',encoding='utf8')
    return manifest

if __name__ == '__main__':
    root = Path(__file__).resolve().parents[1]/'apps/desktop/public/fixtures'
    print(json.dumps(generate(root),indent=2))
