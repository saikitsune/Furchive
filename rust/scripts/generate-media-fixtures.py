"""Generate tiny original media fixtures for native decoder qualification."""
from pathlib import Path
import subprocess
from PIL import Image, ImageDraw
root=Path(__file__).resolve().parents[1]/"apps/desktop/public/fixtures"
root.mkdir(parents=True,exist_ok=True)
frames=[]
for n,color in enumerate(("#da934c","#71c4a8","#6b8acb")):
    im=Image.new("RGB",(320,200),"#18222e")
    draw=ImageDraw.Draw(im)
    draw.rounded_rectangle((20+n*35,25,190+n*35,165),radius=35,fill=color)
    draw.text((20,180),"Furchive generated decoder fixture",fill="white")
    frames.append(im)
frames[0].save(root/"still.png")
frames[0].save(root/"still.jpg")
frames[0].save(root/"still.webp")
frames[0].save(root/"animation.gif",save_all=True,append_images=frames[1:],duration=[100,200,300],loop=0)
frames[0].save(root/"animation.apng",format="PNG",save_all=True,append_images=frames[1:],duration=[100,200,300],loop=0)
for name,codec in (("vp8.webm","libvpx"),("vp9.webm","libvpx-vp9"),("h264.mp4","libx264")):
    subprocess.run(["ffmpeg","-hide_banner","-loglevel","error","-y","-f","lavfi","-i","testsrc2=size=320x200:rate=24","-t","2","-an","-c:v",codec,"-pix_fmt","yuv420p",str(root/name)],check=True,timeout=45)
print("Generated fixtures:",*[p.name for p in sorted(root.iterdir())])
