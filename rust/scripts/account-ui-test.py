"""Host runner negatives are labeled doubles, never native UI acceptance."""
from pathlib import Path
import importlib.util,json,unittest
spec=importlib.util.spec_from_file_location('account_ui',Path(__file__).with_name('account-ui-run-native.py'))
if spec is None or spec.loader is None:raise RuntimeError('account UI runner missing')
m=importlib.util.module_from_spec(spec);spec.loader.exec_module(m)
class AccountReceipt(unittest.TestCase):
 def test_requires_native_backend_namespace_phase_and_every_named_ui_check(self):
  scope='qualification-'+'a'*32
  for phase in [3,4]:
   receipt={'schema':1,'ok':True,'phase':phase,'checks':list(m.CHECKS[phase]),'credential_store':'native','native_store':'linux','namespace':scope}
   self.assertTrue(m.validate(receipt,phase,scope,'linux'))
   for bad in [{},dict(receipt,credential_store='memory'),dict(receipt,namespace='active-account'),dict(receipt,phase=5),dict(receipt,checks=receipt['checks'][:-1]),dict(receipt,checks=receipt['checks']+receipt['checks'][:1])]:
    with self.assertRaises(ValueError):m.validate(bad,phase,scope,'linux')
 def test_full_host_runner_requires_both_ui_phases_and_separate_native_readbacks(self):
  from unittest.mock import patch
  import tempfile,subprocess,sys,urllib.request,base64,platform,contextlib,io
  for fault in ['none','restore-fails','missing-receipt','cleanup-fails']:
   calls=[];system={'Linux':'linux','Darwin':'macos','Windows':'windows'}[platform.system()]
   def child(argv,**kwargs):
    if str(argv[0])=='fixture-probe':
     phase,scope=argv[1:];calls.append('probe:'+phase)
     if fault=='cleanup-fails' and phase=='delete':return subprocess.CompletedProcess(argv,1,'owned cleanup failure','')
     return subprocess.CompletedProcess(argv,0,json.dumps({'schema':1,'phase':phase,'namespace':scope,'native_store':system,'ok':True,'synthetic_data':True}),'')
    env=kwargs['env'];phase=int(env['FURCHIVE_BROWSE_TEST_PHASE']);calls.append('ui:'+str(phase));scope=env['FURCHIVE_ACCOUNT_UI_NAMESPACE'];key='synthetic-native-vault-qualification-only'
    req=urllib.request.Request(env['FURCHIVE_BROWSE_FIXTURE_ORIGIN']+'users/me.json?',headers={'Authorization':'Basic '+base64.b64encode(('fixture_user:'+key).encode()).decode(),'User-Agent':'Furchive/host-runner-test-double'})
    with urllib.request.urlopen(req,timeout=3) as response:self.assertEqual(response.status,200)
    if fault=='restore-fails' and phase==4:raise subprocess.CalledProcessError(1,argv)
    if fault=='missing-receipt' and phase==4:return subprocess.CompletedProcess(argv,0)
    Path(env['FURCHIVE_BROWSE_TEST_OUTPUT']).write_text(json.dumps({'schema':1,'ok':True,'phase':phase,'checks':m.CHECKS[phase],'credential_store':'native','native_store':system,'namespace':scope}));return subprocess.CompletedProcess(argv,0)
   with tempfile.TemporaryDirectory() as tmp:
    root=Path(tmp)/'evidence';binary=Path(tmp)/'binary';binary.write_bytes(b'labeled child double, never native acceptance');probe=Path(tmp)/'probe'
    def dispatch(argv,**kwargs):
     if argv[0]==str(probe.resolve()):argv=['fixture-probe',*argv[1:]]
     return child(argv,**kwargs)
    with patch.object(sys,'argv',['account-ui-run-native.py',str(binary),'--output',str(root),'--probe',str(probe)]),patch.object(m.subprocess,'run',side_effect=dispatch),patch.object(m.subprocess,'check_output',return_value='owned-test-source'),contextlib.redirect_stdout(io.StringIO()):
     if fault!='none':
      with self.assertRaises((subprocess.CalledProcessError,FileNotFoundError,RuntimeError)):m.main()
     else:m.main()
    self.assertEqual((root/'accepted.json').exists(),fault=='none')
    self.assertEqual(calls,['probe:absent','ui:3','probe:read','ui:4']+([] if fault in ['restore-fails','missing-receipt'] else ['probe:absent'])+['probe:delete'])
    self.assertTrue((root/'vault-initial-absent.log').exists());self.assertTrue((root/'cleanup.log').exists())

 def test_account_fixture_refuses_unexpected_authority_and_never_records_credentials(self):
  self.assertFalse(m.allowed_request('/users/me.json',False));self.assertFalse(m.allowed_request('/posts.json',True));self.assertFalse(m.allowed_request('/users/me.json?filter_id=0',True));self.assertTrue(m.allowed_request('/users/me.json',True));self.assertTrue(m.allowed_request('/users/me.json?',True));self.assertFalse(m.allowed_request('/users/me.json?filter_id=',True));self.assertFalse(m.allowed_request('/users/me.json#fragment',True))
if __name__=='__main__':unittest.main()
