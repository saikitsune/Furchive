"""Named host contracts; fake adapters are NOT native application proof."""
import importlib.util
import json
import pathlib
import tempfile
import unittest

HERE = pathlib.Path(__file__).resolve().parent

def load():
    spec = importlib.util.spec_from_file_location('default_smoke', HERE / 'default-smoke.py')
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module

class PackageContracts(unittest.TestCase):
    def test_inner_cleanup_preserves_real_direct_child_sigterm(self):
        import os, signal, subprocess, sys
        from unittest.mock import patch
        import psutil
        if sys.platform != 'linux':
            return  # Linux-native adapter only; no cross-platform runtime claim.
        spec = importlib.util.spec_from_file_location('native_cleanup', HERE / 'default-linux.py')
        if spec is None or spec.loader is None:
            raise RuntimeError('missing Linux cleanup adapter')
        module = importlib.util.module_from_spec(spec); spec.loader.exec_module(module)
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory); log = (root / 'raw.log').open('wb')
            proc = subprocess.Popen([sys.executable, '-c', 'import time;time.sleep(20)'], start_new_session=True, stdout=log, stderr=subprocess.STDOUT)
            identity = psutil.Process(proc.pid)
            adapter = module.LinuxNative.__new__(module.LinuxNative)
            adapter.psutil, adapter.output = psutil, root
            child = {'pid': proc.pid, 'created': identity.create_time(), 'proc': proc, 'log': log, 'phase': 1}
            try:
                with patch.object(psutil, 'process_iter', return_value=iter([identity])):
                    adapter.cleanup(child)
                receipt = json.loads((root / 'phase-1-exit.json').read_text())
                self.assertEqual(receipt['raw_exit'], -signal.SIGTERM, 'Popen alone owns the direct wait status')
                self.assertEqual(proc.returncode, -signal.SIGTERM)
                self.assertEqual(receipt['survivors'], [])
                self.assertFalse(psutil.pid_exists(proc.pid))
            finally:
                if proc.poll() is None:
                    proc.kill(); proc.wait(timeout=3)
                if not log.closed:
                    log.close()

    def test_real_host_parent_child_grandchild_timeout_cleanup(self):
        import sys, time
        import psutil
        if sys.platform != 'linux':
            from unittest.mock import patch
            smoke = load()
            with patch.object(smoke.subprocess, 'Popen') as spawn:
                with self.assertRaises(RuntimeError):
                    smoke.supervise(['not-launched'], None, 1)
                spawn.assert_not_called()
            return
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            script = root / 'host-double.py'
            script.write_text('import subprocess,sys,time,os\nfrom pathlib import Path\n'
                              'Path(sys.argv[2]+sys.argv[1]).write_text(str(os.getpid()))\n'
                              'if int(sys.argv[1])<2: subprocess.Popen([sys.executable,__file__,str(int(sys.argv[1])+1),sys.argv[2]],start_new_session=True)\n'
                              'time.sleep(60)\n')
            with (root/'raw.log').open('wb') as log:
                with self.assertRaises(TimeoutError):
                    load().supervise([sys.executable,str(script),'0',str(root/'pid-')], log, 1)
            for name in ('pid-0','pid-1','pid-2'):
                self.assertTrue((root/name).exists(), 'actual host descendant not started')
                pid = int((root/name).read_text())
                self.assertFalse(psutil.pid_exists(pid) and psutil.Process(pid).status() != psutil.STATUS_ZOMBIE)
    def test_missing_default_implementation_is_not_success(self):
        self.assertTrue((HERE / 'default-smoke.py').is_file(), 'default smoke implementation missing')

    def test_safe_inventory_rejects_aliases_duplicates_links(self):
        smoke = load()
        self.assertEqual(smoke.inventory([('usr/bin/app', 'file')]), ['usr/bin/app'])
        for entries in [[('../app','file')], [('/app','file')], [('a','link')], [('a','file'),('a','file')], [('a/./b','file')], [('a\\b','file')]]:
            with self.subTest(entries=entries), self.assertRaises(ValueError):
                smoke.inventory(entries)

    def test_unsupported_isolation_refuses_before_launch(self):
        for host in ('Windows', 'Darwin', 'unknown'):
            with self.subTest(host=host), self.assertRaises(ValueError):
                load().isolation(host, pathlib.Path('/owned'))

    def test_linux_isolation_contains_all_user_paths_and_removes_test_authority(self):
        env, root = load().isolation('Linux', pathlib.Path('/owned'), {
            'HOME':'/foreign', 'FURCHIVE_TEST_DATA_ROOT':'/foreign', 'HTTP_PROXY':'secret',
            'WEBKIT_DISABLE_SANDBOX_THIS_IS_DANGEROUS':'1', 'DISPLAY':':99'})
        self.assertEqual(root, pathlib.Path('/owned/data/dev.furchive.desktop.alpha'))
        self.assertEqual(env['HOME'], '/owned/home')
        self.assertEqual(env['XDG_DATA_HOME'], '/owned/data')
        self.assertNotIn('HTTP_PROXY', env)
        self.assertFalse(any(k.startswith('FURCHIVE_') or k.startswith('WEBKIT_DISABLE_') for k in env))

    def test_stale_output_refused(self):
        with tempfile.TemporaryDirectory() as directory:
            with self.assertRaises(ValueError): load().fresh(pathlib.Path(directory))

    def test_default_feature_receipt_rejects_self_test_and_missing(self):
        smoke = load()
        self.assertEqual(smoke.default_features(['custom-protocol']), ['custom-protocol'])
        for features in ([], ['self-test','custom-protocol'], ['fixture-api'], None):
            with self.subTest(features=features), self.assertRaises(ValueError): smoke.default_features(features)

    def test_actual_portable_archive_roundtrip_has_distinct_default_name(self):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            target = root / 'debug'; target.mkdir()
            (target / 'furchive-desktop.exe').write_bytes(b'labeled PE payload double')
            output = root / 'output'; output.mkdir()
            row = load().prepare(target, output, 'Windows')
            self.assertEqual(row['artifact'], 'Furchive-default-Windows.zip')
            self.assertEqual(row['native_runtime'], 'UNRUN')
            self.assertEqual(row['binary_sha256'], load().digest(output / row['executable']))
            self.assertEqual((output / row['executable']).read_bytes(), b'labeled PE payload double')

    def test_complete_macos_bundle_roundtrip_not_just_executable(self):
        import plistlib
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory); target = root / 'debug'
            app = target / 'bundle/macos/Furchive.app/Contents'
            (app / 'MacOS').mkdir(parents=True); (app / 'Resources').mkdir()
            (app / 'MacOS/app').write_bytes(b'labeled Mach-O double')
            (app / 'Resources/owned.txt').write_bytes(b'full bundle sentinel')
            (app / 'Info.plist').write_bytes(plistlib.dumps({'CFBundleExecutable':'app'}))
            output = root / 'output'; output.mkdir()
            row = load().prepare(target, output, 'Darwin')
            self.assertEqual((output / 'payload/Furchive.app/Contents/Resources/owned.txt').read_bytes(), b'full bundle sentinel')
            self.assertEqual(row['native_runtime'], 'UNRUN')

    def test_native_adapter_and_command_entrypoint_are_shipped(self):
        smoke = load()
        self.assertTrue(callable(getattr(smoke, 'main', None)), 'missing real command entrypoint')
        self.assertTrue((HERE / 'default-linux.py').is_file(), 'missing native adapter')

    def test_named_receipt_binds_exact_actual_inventory(self):
        smoke = load()
        actual = {'ids': ['A.test_one', 'B.test_two'], 'passed': ['A.test_one', 'B.test_two'], 'errors': [], 'skipped': []}
        self.assertTrue(smoke.named_receipt(actual, ['A.test_one', 'B.test_two']))
        for bad in ({}, dict(actual, ids=[]), dict(actual, passed=['A.test_one']),
                    dict(actual, ids=['A.test_one','A.test_one']), dict(actual, skipped=['A.test_one']),
                    dict(actual, errors=['diagnostic'])):
            with self.subTest(bad=bad), self.assertRaises(ValueError):
                smoke.named_receipt(bad, ['A.test_one', 'B.test_two'])

    def test_build_receipt_rejects_missing_or_test_features(self):
        smoke = load()
        row = {'reason':'compiler-artifact', 'target':{'name':'furchive-desktop', 'kind':['bin']},
               'features':['custom-protocol'], 'executable':'/owned/furchive-desktop'}
        self.assertEqual(smoke.build_receipt(json.dumps(row), pathlib.Path('/owned/furchive-desktop'))['features'], ['custom-protocol'])
        for text in ('', json.dumps(dict(row, features=['self-test'])),
                     json.dumps(dict(row, executable='/foreign/app')), json.dumps(row)+'\n'+json.dumps(row)):
            with self.subTest(text=text), self.assertRaises(ValueError):
                smoke.build_receipt(text, pathlib.Path('/owned/furchive-desktop'))

    def test_cosmetic_restore_refuses_semantic_manifest_change(self):
        smoke = load()
        original = b'self-test = ["a","b"]\nother=1\n'
        self.assertEqual(smoke.cosmetic_restore(original, b'self-test = ["a", "b"]\nother=1\n'), original)
        with self.assertRaises(ValueError): smoke.cosmetic_restore(original, b'self-test = ["a", "c"]\nother=1\n')

    def test_native_close_revalidates_window_before_any_os_action(self):
        from unittest.mock import patch
        spec = importlib.util.spec_from_file_location('linux_adapter', HERE / 'default-linux.py')
        assert spec is not None and spec.loader is not None
        module = importlib.util.module_from_spec(spec); spec.loader.exec_module(module)
        adapter = module.LinuxNative.__new__(module.LinuxNative)
        adapter.identity = lambda child: None
        adapter.window = lambda child: type('Window', (), {'id': 42})()
        with patch.object(module.subprocess, 'run') as run:
            with self.assertRaises(ValueError): adapter.close({'pid':123}, {'window':'99'})
            run.assert_not_called()

    def test_positive_native_close_uses_ewmh_not_window_destroy(self):
        from unittest.mock import patch, Mock
        spec = importlib.util.spec_from_file_location('linux_adapter', HERE / 'default-linux.py')
        assert spec is not None and spec.loader is not None
        module = importlib.util.module_from_spec(spec); spec.loader.exec_module(module)
        adapter = module.LinuxNative.__new__(module.LinuxNative)
        adapter.identity = lambda child: None
        adapter.window = lambda child: type('Window', (), {'id': 42})()
        proc = Mock(); proc.wait.return_value = 0
        with patch.object(module.subprocess, 'run') as run:
            self.assertEqual(adapter.close({'pid':123,'proc':proc}, {'window':'42'}), 0)
            run.assert_called_once_with(['wmctrl','-ic','0x2a'], check=True, timeout=5)

    def test_actual_main_refuses_unsupported_profile_without_launch(self):
        import sys
        from unittest.mock import patch
        for host in ('Windows', 'Darwin'):
            with tempfile.TemporaryDirectory() as directory:
                root = pathlib.Path(directory)
                binary = root / 'app'; binary.write_bytes(b'labeled binary double')
                artifact = root / 'package.zip'; artifact.write_bytes(b'labeled package double')
                smoke = load()
                package = {'executable':'app','artifact':'package.zip', 'compiler_features':['custom-protocol'],
                           'binary_sha256':smoke.digest(binary),'artifact_sha256':smoke.digest(artifact)}
                receipt = root / 'package.json'; receipt.write_text(json.dumps(package))
                out = root / 'result'
                with patch.object(sys, 'argv', ['default-smoke.py','--runtime-package',str(receipt),'--output',str(out)]), patch('platform.system', return_value=host), patch.object(smoke.subprocess, 'Popen') as spawn:
                    self.assertEqual(smoke.main(), 2)
                    spawn.assert_not_called()
                self.assertEqual(json.loads((out/'smoke.json').read_text())['status'], 'UNRUN')

class FakeNative:
    """Labeled protocol double; no pixels here are product evidence."""
    def __init__(self, defect=None):
        self.defect = defect
        self.closed = []
        self.cleaned = []

    def launch(self, phase):
        return {'pid': 400 + phase, 'path': '/owned/app', 'phase': phase, 'alive': True}

    def observe(self, child):
        if self.defect == 'early-exit':
            child['alive'] = False
        row = dict(child, window='window-' + str(child['pid']), nonce='current',
                   data_root='/owned/data', product=True, settings='sentinel', nonblank=True)
        if self.defect == 'wrong-pid': row['pid'] += 1
        if self.defect == 'wrong-path': row['path'] = '/foreign/app'
        if self.defect == 'stale': row['nonce'] = 'old'
        if self.defect == 'no-proof': row['product'] = False
        if self.defect == 'blank': row['nonblank'] = False
        if self.defect == 'timeout': raise TimeoutError('deliberate double timeout')
        return row

    def close(self, child, row):
        self.closed.append(child['pid'])
        child['alive'] = False
        return 0

    def alive(self, child): return child['alive']
    def cleanup(self, child): self.cleaned.append(child['pid'])

class LifecycleContracts(unittest.TestCase):
    def test_positive_fake_normal_close_restart(self):
        fake = FakeNative()
        result = load().lifecycle(fake, '/owned/app', '/owned/data', 'current', 'sentinel')
        self.assertEqual(result['status'], 'PASS')
        self.assertEqual(fake.closed, [401, 402])
        self.assertEqual(fake.cleaned, [401, 402])

    def test_wrong_pid_never_closes_foreign_window(self): self.reject('wrong-pid')
    def test_wrong_path_never_closes_foreign_window(self): self.reject('wrong-path')
    def test_stale_evidence_rejected(self): self.reject('stale')
    def test_early_exit_rejected(self): self.reject('early-exit')
    def test_timeout_rejected_even_if_cleanup_zero(self): self.reject('timeout')
    def test_exit_zero_without_proof_rejected(self): self.reject('no-proof')
    def test_blank_capture_rejected(self): self.reject('blank')

    def reject(self, defect):
        fake = FakeNative(defect)
        with self.assertRaises((ValueError, TimeoutError)):
            load().lifecycle(fake, '/owned/app', '/owned/data', 'current', 'sentinel')
        self.assertEqual(fake.closed, [])
        self.assertEqual(fake.cleaned, [401])

if __name__ == '__main__':
    import sys
    # Closed, reviewed inventory: discovery alone cannot silently shrink this gate.
    expected = [
        'LifecycleContracts.test_blank_capture_rejected',
        'LifecycleContracts.test_early_exit_rejected',
        'LifecycleContracts.test_exit_zero_without_proof_rejected',
        'LifecycleContracts.test_positive_fake_normal_close_restart',
        'LifecycleContracts.test_stale_evidence_rejected',
        'LifecycleContracts.test_timeout_rejected_even_if_cleanup_zero',
        'LifecycleContracts.test_wrong_path_never_closes_foreign_window',
        'LifecycleContracts.test_wrong_pid_never_closes_foreign_window',
        'PackageContracts.test_actual_main_refuses_unsupported_profile_without_launch',
        'PackageContracts.test_actual_portable_archive_roundtrip_has_distinct_default_name',
        'PackageContracts.test_build_receipt_rejects_missing_or_test_features',
        'PackageContracts.test_complete_macos_bundle_roundtrip_not_just_executable',
        'PackageContracts.test_cosmetic_restore_refuses_semantic_manifest_change',
        'PackageContracts.test_default_feature_receipt_rejects_self_test_and_missing',
        'PackageContracts.test_inner_cleanup_preserves_real_direct_child_sigterm',
        'PackageContracts.test_linux_isolation_contains_all_user_paths_and_removes_test_authority',
        'PackageContracts.test_missing_default_implementation_is_not_success',
        'PackageContracts.test_named_receipt_binds_exact_actual_inventory',
        'PackageContracts.test_native_adapter_and_command_entrypoint_are_shipped',
        'PackageContracts.test_native_close_revalidates_window_before_any_os_action',
        'PackageContracts.test_positive_native_close_uses_ewmh_not_window_destroy',
        'PackageContracts.test_real_host_parent_child_grandchild_timeout_cleanup',
        'PackageContracts.test_safe_inventory_rejects_aliases_duplicates_links',
        'PackageContracts.test_stale_output_refused',
        'PackageContracts.test_unsupported_isolation_refuses_before_launch',
    ]
    suite = unittest.defaultTestLoader.loadTestsFromModule(sys.modules[__name__])
    def ids(suite):
        result = []
        for test in suite:
            result.extend(ids(test) if isinstance(test, unittest.TestSuite) else [test.id().removeprefix('__main__.')])
        return result
    inventory_ids = ids(suite)
    class NamedResult(unittest.TextTestResult):
        def __init__(self, *args, **kwargs):
            super().__init__(*args, **kwargs)
            self.passed_ids = []
        def addSuccess(self, test):
            super().addSuccess(test)
            self.passed_ids.append(test.id().removeprefix('__main__.'))
    result = unittest.TextTestRunner(verbosity=2, resultclass=NamedResult).run(suite)
    receipt = {'ids': inventory_ids, 'passed': getattr(result, 'passed_ids'),
               'errors': [str(t) for t, _ in result.failures + result.errors],
               'skipped': [str(t) for t, _ in result.skipped]}
    load().named_receipt(receipt, expected)
    print(json.dumps(receipt, sort_keys=True))
    raise SystemExit(0 if result.wasSuccessful() else 1)
