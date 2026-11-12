"""Host driver schedule tests; fake pixels/time are NOT native media proof."""
import importlib.util
from pathlib import Path
import unittest

spec=importlib.util.spec_from_file_location('driver',Path(__file__).with_name('media-run-native.py'))
if spec is None or spec.loader is None:raise RuntimeError('driver missing')
module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)

class CaptureSchedule(unittest.TestCase):
    def test_native_macos_capture_targets_only_the_owned_window(self):
        calls=[]
        class QuartzDouble:
            kCGWindowListOptionIncludingWindow=8
            kCGWindowImageNominalResolution=16
            def CGRectMake(self,*args):return args
            def CGWindowListCreateImage(self,rect,options,wid,flags):calls.append((rect,options,wid,flags));return 'owned-image'
            def CGImageGetWidth(self,image):return 1
            def CGImageGetHeight(self,image):return 1
            def CGImageGetBytesPerRow(self,image):return 4
            def CGImageGetDataProvider(self,image):return 'owned-provider'
            def CGDataProviderCopyData(self,provider):return bytes([3,2,1,255])
        shot=module.QuartzWindowScreen(QuartzDouble(),42).grab({'left':10,'top':20,'width':1,'height':1})
        self.assertEqual(calls,[((10,20,1,1),8,42,16)])
        self.assertEqual(shot.size,(1,1));self.assertEqual(shot.rgb,b'\x01\x02\x03')

    def test_native_stream_receipt_refuses_missing_or_reordered_frames(self):
        import tempfile,json,hashlib
        with tempfile.TemporaryDirectory() as directory:
            root=Path(directory);rows=[]
            for i in range(12):
                path=root/f'gif-{i}.png';path.write_bytes(b'owned parser double');rows.append({'file':path.name,'monotonicMs':1000+i*80})
            receipt={'pid':123,'windowId':'42','checkpoint':'gif','captureAPI':'ScreenCaptureKit.SCScreenshotManager.desktopIndependentWindow','frames':rows}
            path=root/'gif.native-frames.json';path.write_text(json.dumps(receipt))
            frames=module.read_native_frames(path,root,123,'42','gif')
            self.assertEqual(len(frames),12);self.assertEqual(frames[0]['sha256'],hashlib.sha256(b'owned parser double').hexdigest())
            receipt['frames']=list(reversed(rows));path.write_text(json.dumps(receipt))
            with self.assertRaises(ValueError):module.read_native_frames(path,root,123,'42','gif')
            receipt['frames']=rows;receipt['pid']=999;path.write_text(json.dumps(receipt))
            with self.assertRaises(ValueError):module.read_native_frames(path,root,123,'42','gif')

    def test_png_encoding_is_outside_the_frame_sampling_schedule(self):
        clock=[0.0];events=[]
        class Shot:
            size=(1,1)
            rgb=b'\x01\x02\x03'
        class Screen:
            def grab(self,region):
                events.append('capture');clock[0]+=.005;return Shot()
        def verify():events.append('identity')
        def sleep(seconds):clock[0]+=seconds
        samples=module.capture_pixels(Screen(),{'x':0,'y':0,'width':1,'height':1},verify,lambda:clock[0],sleep)
        self.assertEqual(len(samples),12)
        self.assertEqual(events.count('capture'),12)
        self.assertEqual(events.count('identity'),12)
        self.assertTrue(all(79<=b[0]-a[0]<=81 for a,b in zip(samples,samples[1:])))
        self.assertTrue(all(s[1:]==((1,1),b'\x01\x02\x03') for s in samples))

if __name__=='__main__':unittest.main()
