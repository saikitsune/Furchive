"""Contract tests for locating the executable in the actual extracted app payload."""
from pathlib import Path
import importlib.util
import plistlib
import tempfile
import unittest

spec=importlib.util.spec_from_file_location('package_qualification',Path(__file__).with_name('qualify-package.py'))
if spec is None or spec.loader is None:raise RuntimeError('package checker module missing')
module=importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)

class PackageLayout(unittest.TestCase):
    def test_independent_browsing_runs_after_media_failure_but_combined_acceptance_stays_failed(self):
        import subprocess
        calls=[]
        def runner(argv,**kwargs):
            calls.append(argv)
            if 'media-run-native.py' in argv[1]:raise subprocess.CalledProcessError(1,argv)
        with tempfile.TemporaryDirectory() as tmp:
            with self.assertRaises(RuntimeError):module.exercise_campaigns(Path('owned-binary'),Path(tmp),runner)
        self.assertEqual([Path(c[1]).name for c in calls],['media-run-native.py','browse-run-native.py'])

    def test_real_account_campaign_is_required_separately_and_runs_after_peer_failure(self):
        import subprocess
        calls=[]
        def runner(argv,**kwargs):
            calls.append(argv)
            if 'media-run-native.py' in argv[1]:raise subprocess.CalledProcessError(1,argv)
        with tempfile.TemporaryDirectory() as tmp:
            with self.assertRaises(RuntimeError):module.exercise_campaigns(Path('owned-binary'),Path(tmp),runner,account_probe=Path('owned-probe'))
            receipt=__import__('json').loads((Path(tmp)/'campaigns.json').read_text())
            self.assertEqual(receipt['account']['status'],'PASS')
            self.assertEqual(receipt['media']['status'],'FAIL')
        self.assertEqual(len(calls),3)
        self.assertTrue(any('account-ui-run-native.py' in str(v) or 'native-vault-linux.py' in str(v) for v in calls[2]))

    def test_bundle_executable_must_exist_in_exact_platform_layout(self):
        with tempfile.TemporaryDirectory() as tmp:
            root=Path(tmp)
            linux=root/'linux/usr/bin/furchive-desktop';linux.parent.mkdir(parents=True);linux.write_bytes(b'owned test executable')
            self.assertEqual(module.locate_binary(root/'linux','Linux'),linux)
            win=root/'windows/furchive-desktop.exe';win.parent.mkdir(parents=True);win.write_bytes(b'owned test executable')
            self.assertEqual(module.locate_binary(root/'windows','Windows'),win)
            app=root/'mac/Furchive Rust Alpha.app';(app/'Contents/MacOS').mkdir(parents=True)
            (app/'Contents/Info.plist').write_bytes(plistlib.dumps({'CFBundleExecutable':'furchive-desktop'}))
            mac=app/'Contents/MacOS/furchive-desktop';mac.write_bytes(b'owned test executable')
            self.assertEqual(module.locate_binary(root/'mac','macOS'),mac)
            (app/'Contents/Info.plist').write_bytes(plistlib.dumps({'CFBundleExecutable':'../../outside'}))
            with self.assertRaises(ValueError):module.locate_binary(root/'mac','macOS')
            linux.unlink()
            with self.assertRaises(ValueError):module.locate_binary(root/'linux','Linux')

if __name__=='__main__':unittest.main()
