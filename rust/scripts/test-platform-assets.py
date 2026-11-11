"""Preflight real desktop resources before paying for a native runner."""
from pathlib import Path
import struct
import unittest

ROOT=Path(__file__).resolve().parents[1]/'apps/desktop/src-tauri'
class PlatformAssets(unittest.TestCase):
    def test_windows_resource_contains_a_256px_png(self):
        raw=(ROOT/'icons/icon.ico').read_bytes()
        reserved,kind,count=struct.unpack_from('<HHH',raw)
        self.assertEqual((reserved,kind),(0,1))
        self.assertGreater(count,0)
        entries=[struct.unpack_from('<BBBBHHII',raw,6+i*16) for i in range(count)]
        full=[e for e in entries if e[0]==0 and e[1]==0]
        self.assertEqual(len(full),1)
        size,offset=full[0][-2:]
        self.assertLessEqual(offset+size,len(raw))
        self.assertEqual(raw[offset:offset+8],b'\x89PNG\r\n\x1a\n')
        self.assertEqual(struct.unpack_from('>II',raw,offset+16),(256,256))

if __name__=='__main__':unittest.main()
