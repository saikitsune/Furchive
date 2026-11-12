"""Test the host-frame evidence validator using LABELED synthetic parser doubles only."""
from pathlib import Path
import importlib.util
import hashlib
import json
import tempfile
import unittest
from PIL import Image

spec=importlib.util.spec_from_file_location('evidence',Path(__file__).with_name('media-validate-capture.py'))
assert spec and spec.loader
module=importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)

class EvidenceTests(unittest.TestCase):
    def test_synthetic_parser_double_accepts_owned_states_rejects_frozen_frames_and_wrong_identity(self):
        with tempfile.TemporaryDirectory() as directory:
            root=Path(directory)
            manifest={'schema':1,'platform':'linux','engine':'WebKitGTK','display':'X11','pid':123,'windowId':'owned-test-double','candidateSha256':'a'*64,'executableSha256':'b'*64,'sourceSha256':'c'*64,'captures':[]}
            colors=[(224,48,64),(32,192,96),(48,96,224)]
            fixture_manifest=json.loads((Path(__file__).resolve().parents[1]/'apps/desktop/public/fixtures/viewer-manifest.json').read_text())
            for suffix in ['gif','apng','webp']:
                capture={'checkpoint':suffix,'fixtureSha256':next(x['sha256'] for x in fixture_manifest['files'] if x['name']=='viewer-animation.'+suffix),'viewport':{'width':640,'height':400,'dpr':1},'rect':{'x':160,'y':100,'width':320,'height':200},'clientPhysical':{'width':640,'height':400},'foreground':True,'frames':[]}
                for i in range(12):
                    file=root/f'{suffix}-{i}.png';Image.new('RGB',(640,400),colors[i%3]).save(file)
                    capture['frames'].append({'file':file.name,'sha256':hashlib.sha256(file.read_bytes()).hexdigest(),'monotonicMs':i*80})
                manifest['captures'].append(capture)
            self.assertTrue(module.validate(manifest,root,fixture_manifest,'a'*64,'b'*64,'c'*64))
            bad=json.loads(json.dumps(manifest));bad['captures'][0]['foreground']=False
            with self.assertRaises(ValueError):module.validate(bad,root,fixture_manifest,'a'*64,'b'*64,'c'*64)
            with self.assertRaises(ValueError):module.validate(manifest,root,fixture_manifest,'d'*64,'b'*64,'c'*64)
            bad=json.loads(json.dumps(manifest));bad['captures'][0]['frames']=[dict(bad['captures'][0]['frames'][0],monotonicMs=i*80) for i in range(12)]
            with self.assertRaises(ValueError):module.validate(bad,root,fixture_manifest,'a'*64,'b'*64,'c'*64)
            bad=json.loads(json.dumps(manifest));bad['captures'][0]['frames'][0]['file']='../elsewhere.png'
            with self.assertRaises(ValueError):module.validate(bad,root,fixture_manifest,'a'*64,'b'*64,'c'*64)

if __name__=='__main__':unittest.main()
