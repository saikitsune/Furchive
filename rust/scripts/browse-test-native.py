"""Parser negatives are labeled doubles, not native product acceptance."""
import importlib.util,unittest
from pathlib import Path
spec=importlib.util.spec_from_file_location('browse',Path(__file__).with_name('browse-run-native.py'))
if spec is None or spec.loader is None:raise RuntimeError('missing browse driver')
m=importlib.util.module_from_spec(spec);spec.loader.exec_module(m)
class Receipt(unittest.TestCase):
 def test_requires_every_named_ui_path_not_process_exit_alone(self):
  good={'schema':1,'ok':True,'phase':1,'checks':['include-exclude-ratings','autocomplete','pagination-submitted-query','favorites','ordered-pool-holes','pin-persist','saved-search-persist','preferences-save','product-gallery-thumbnails','legacy-preview-confirm','account-verify-logout','product-adjacent-navigation','pool-gallery','pool-download-group','bounded-parallel-downloads','download-pause-ack','download-complete','download-cancel',*[f'product-{id}-{kind}' for id,kind in [(100,'image'),(101,'animation'),(102,'animation'),(103,'animation'),(104,'video'),(105,'video')]]]}
  self.assertTrue(m.validate(good,1))
  for bad in [{},dict(good,ok=False),dict(good,phase=2),dict(good,checks=good['checks'][:-1]),dict(good,checks=good['checks']+good['checks'][:1])]:
   with self.assertRaises(ValueError):m.validate(bad,1)
  self.assertTrue(m.validate({'schema':1,'ok':True,'phase':2,'checks':['restart-state','saved-search-delete','unpin','preferences-restart','legacy-restart-delete','download-history-restart','pool-group-restart']},2))
if __name__=='__main__':unittest.main()
