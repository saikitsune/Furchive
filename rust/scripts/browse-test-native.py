"""Parser negatives are labeled doubles, not native product acceptance."""
import importlib.util,unittest
from pathlib import Path
spec=importlib.util.spec_from_file_location('browse',Path(__file__).with_name('browse-run-native.py'))
if spec is None or spec.loader is None:raise RuntimeError('missing browse driver')
m=importlib.util.module_from_spec(spec);spec.loader.exec_module(m)
class Receipt(unittest.TestCase):
 def test_requires_every_named_ui_path_not_process_exit_alone(self):
  good={'schema':1,'ok':True,'phase':1,'checks':['include-exclude-ratings','autocomplete','pagination-submitted-query','favorites','ordered-pool-holes','pin-persist','saved-search-persist','preferences-save','presentation-preferences','product-gallery-thumbnails','legacy-preview-confirm','account-verify-logout','product-adjacent-navigation','pool-gallery','pool-download-group','bounded-parallel-downloads','whole-pool-queue','viewer-cross-page-search','viewer-cross-page-pool','download-pause-ack','download-complete','download-cancel',*[f'product-{id}-{kind}' for id,kind in [(100,'image'),(101,'animation'),(102,'animation'),(103,'animation'),(104,'video'),(105,'video')]]]}
  self.assertTrue(m.validate(good,1))
  for bad in [{},dict(good,ok=False),dict(good,phase=2),dict(good,checks=good['checks'][:-1]),dict(good,checks=good['checks']+good['checks'][:1])]:
   with self.assertRaises(ValueError):m.validate(bad,1)
  self.assertTrue(m.validate({'schema':1,'ok':True,'phase':2,'checks':['restart-state','saved-search-delete','unpin','preferences-restart','legacy-restart-delete','download-history-restart','pool-group-restart','whole-pool-restart','presentation-preferences-restart','local-completed-media-restart']},2))
 def test_old_restart_receipt_cannot_pass_without_local_decode(self):
  old={'schema':1,'ok':True,'phase':2,'checks':['restart-state','saved-search-delete','unpin','preferences-restart','legacy-restart-delete','download-history-restart','pool-group-restart','whole-pool-restart','presentation-preferences-restart']}
  with self.assertRaises(ValueError):m.validate(old,2)
  current=dict(old,checks=old['checks']+['local-completed-media-restart'])
  self.assertTrue(m.validate(current,2))
  for bad in [dict(current,checks=current['checks']+['local-completed-media-restart']),dict(current,ok=False)]:
   with self.assertRaises(ValueError):m.validate(bad,2)
if __name__=='__main__':unittest.main()
