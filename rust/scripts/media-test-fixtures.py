"""Owned synthetic corpus tests; these are NOT native decoder evidence."""
from pathlib import Path
import importlib.util
import tempfile
import unittest
import hashlib
from PIL import Image

SCRIPT = Path(__file__).with_name('media-generate-fixtures.py')
spec = importlib.util.spec_from_file_location('fixtures', SCRIPT)
assert spec and spec.loader
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)

class CorpusTests(unittest.TestCase):
    def test_additive_generation_and_manifest_frame_states(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            old = root / 'animation.gif'
            old.write_bytes(b'preserve original corpus')
            manifest = module.generate(root)
            self.assertEqual(old.read_bytes(), b'preserve original corpus')
            self.assertEqual(len(manifest['files']), 7)
            for row in manifest['files']:
                file = root / row['name']
                self.assertEqual(hashlib.sha256(file.read_bytes()).hexdigest(), row['sha256'])
            for suffix in ['gif', 'apng', 'webp']:
                with Image.open(root / ('viewer-animation.'+suffix)) as image:
                    self.assertEqual(getattr(image,'n_frames'), 3)
                    states = []
                    for n in range(3):
                        image.seek(n)
                        states.append(image.convert('RGB').getpixel((160, 100)))
                    self.assertEqual(states, [tuple(color) for color in manifest['animation']['colors']])
            with Image.open(root / 'viewer-large.png') as image:
                self.assertEqual(image.size, (4096,3072))
            with Image.open(root / 'viewer-oversized.png') as image:
                self.assertGreater(image.width*image.height,32_000_000)
            for name in ['viewer-corrupt.png','viewer-truncated.png']:
                with self.assertRaises(Exception):
                    with Image.open(root / name) as image:
                        image.load()

if __name__ == '__main__':
    unittest.main()
