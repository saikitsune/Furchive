"""Parser doubles only; no native keyring success inferred."""
import importlib.util,unittest
from pathlib import Path
s=importlib.util.spec_from_file_location('vault',Path(__file__).with_name('native-vault-run.py'))
if s is None or s.loader is None:raise RuntimeError('runner missing')
m=importlib.util.module_from_spec(s);s.loader.exec_module(m)
class Receipt(unittest.TestCase):
 def test_native_phase_requires_matching_namespace_platform_and_explicit_receipt(self):
  import json
  scope='qualification-'+'a'*32;value={'schema':1,'phase':'read','namespace':scope,'native_store':'linux','ok':True,'synthetic_data':True}
  self.assertTrue(m.validate(json.dumps(value),'read',scope,'linux'))
  for bad in ['', '{}', json.dumps(dict(value,ok=False)),json.dumps(dict(value,namespace='active-account')),json.dumps(dict(value,phase='write')),json.dumps(dict(value,native_store='windows'))]:
   with self.assertRaises(ValueError):m.validate(bad,'read',scope,'linux')
if __name__=='__main__':unittest.main()
