"""Prepare and exercise qualification packages, never install or publish them.

Windows is a portable ZIP (requires system WebView2), Linux a Debian payload,
macOS an unsigned .app archive. These are self-test engineering builds, not releases.
"""
from pathlib import Path, PurePosixPath
import argparse
import hashlib
import json
import sys
import platform as host_platform
import plistlib
import shutil
import subprocess
import tarfile
import zipfile

DESKTOP=Path(__file__).resolve().parents[1]/'apps/desktop'
TARGET=DESKTOP/'src-tauri/target/debug'

def locate_binary(root:Path,platform:str)->Path:
    if platform=='Linux':
        binary=root/'usr/bin/furchive-desktop'
    elif platform=='Windows':
        binary=root/'furchive-desktop.exe'
    elif platform=='macOS':
        apps=list(root.glob('*.app'))
        if len(apps)!=1:raise ValueError('expected exactly one macOS app')
        with (apps[0]/'Contents/Info.plist').open('rb') as f:info=plistlib.load(f)
        name=info.get('CFBundleExecutable')
        if not isinstance(name,str) or not name or name in ('.','..') or '/' in name or '\\' in name:
            raise ValueError('invalid bundle executable name')
        binary=apps[0]/'Contents/MacOS'/name
    else:raise ValueError('unknown platform')
    if not binary.is_file() or binary.is_symlink() or not binary.resolve().is_relative_to(root.resolve()):
        raise ValueError('package executable is missing or escapes payload')
    return binary

def digest(path:Path)->str:
    h=hashlib.sha256()
    with path.open('rb') as f:
        for chunk in iter(lambda:f.read(1024*1024),b''):h.update(chunk)
    return h.hexdigest()

def safe_name(name:str)->bool:
    p=PurePosixPath(name)
    return not p.is_absolute() and '..' not in p.parts and '\\' not in name

def prepare(output:Path,platform:str)->tuple[Path,Path]:
    payload=output/'extracted';payload.mkdir()
    if platform=='Linux':
        bundles=list((TARGET/'bundle/deb').glob('*.deb'))
        if len(bundles)!=1:raise ValueError('expected one fresh Debian package')
        artifact=output/bundles[0].name;shutil.copy2(bundles[0],artifact)
        # Validate the actual Debian filesystem archive before extracting it.
        with subprocess.Popen(['dpkg-deb','--fsys-tarfile',str(artifact)],stdout=subprocess.PIPE) as child:
            if child.stdout is None:raise RuntimeError('missing Debian payload stream')
            with tarfile.open(fileobj=child.stdout,mode='r|') as archive:
                names=set()
                for entry in archive:
                    if not safe_name(entry.name) or entry.name in names or not(entry.isfile() or entry.isdir()):
                        raise ValueError('unsafe/duplicate Debian payload member')
                    names.add(entry.name)
            if child.wait(timeout=30)!=0:raise RuntimeError('Debian archive enumeration failed')
        subprocess.run(['dpkg-deb','--extract',str(artifact),str(payload)],check=True,timeout=60)
    elif platform=='macOS':
        apps=list((TARGET/'bundle/macos').glob('*.app'))
        if len(apps)!=1:raise ValueError('expected one fresh .app bundle')
        # Preserve the complete bundle, not a copied executable pretending to be an app.
        artifact=output/'Furchive-qualification-macOS.tar.gz'
        with tarfile.open(artifact,'w:gz') as archive:archive.add(apps[0],arcname=apps[0].name)
        with tarfile.open(artifact,'r:gz') as archive:
            names=set()
            for entry in archive.getmembers():
                if not safe_name(entry.name) or entry.name in names or not(entry.isfile() or entry.isdir()):
                    raise ValueError('unsafe/duplicate macOS archive member')
                names.add(entry.name)
            archive.extractall(payload,filter='data')
    elif platform=='Windows':
        # The portable ZIP is a distinct acceptance target, not an NSIS/MSI installer.
        artifact=output/'Furchive-qualification-Windows.zip'
        with zipfile.ZipFile(artifact,'w',compression=zipfile.ZIP_DEFLATED) as archive:
            archive.write(TARGET/'furchive-desktop.exe','furchive-desktop.exe')
            archive.writestr('QUALIFICATION.txt','Self-test engineering build. Requires Windows WebView2. Not a finished application or installer.\n')
        with zipfile.ZipFile(artifact) as archive:
            if archive.namelist()!=['furchive-desktop.exe','QUALIFICATION.txt']:raise ValueError('unexpected portable ZIP inventory')
            archive.extractall(payload)
    else:raise ValueError('unknown platform')
    return artifact,locate_binary(payload,platform)

def main()->None:
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--prepare-only',action='store_true',help='prepare bytes only; explicitly NOT a runtime pass')
    args=parser.parse_args();output=args.output.resolve()
    if output.exists():raise SystemExit('Refusing stale package evidence directory')
    output.mkdir(parents=True)
    platform=host_platform.system();platform='macOS' if platform=='Darwin' else platform
    artifact,binary=prepare(output,platform)
    record={'platform':platform,'artifact':artifact.name,'artifact_sha256':digest(artifact),
            'extracted_binary':str(binary.relative_to(output)),'binary_sha256':digest(binary),
            'native_runtime':'NOT RUN','release_ready':False}
    receipt=output/'package.json';receipt.write_text(json.dumps(record,indent=2))
    if args.prepare_only:
        print(json.dumps(record,indent=2));print('PACKAGE PREPARED ONLY — NOT native runtime acceptance');return
    subprocess.run([sys.executable,str(Path(__file__).with_name('run-native-qualification.py')),
                    str(binary),'--output',str(output/'native.json')],check=True,timeout=120)
    record['native_runtime']='PASS: extracted payload image decode/video playback+seek/virtualized DOM only'
    receipt.write_text(json.dumps(record,indent=2));print(json.dumps(record,indent=2))

if __name__=='__main__':main()
