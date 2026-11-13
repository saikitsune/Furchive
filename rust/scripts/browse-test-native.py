"""Parser negatives are labeled doubles, not native product acceptance."""
import importlib.util,unittest
from pathlib import Path
spec=importlib.util.spec_from_file_location('browse',Path(__file__).with_name('browse-run-native.py'))
if spec is None or spec.loader is None:raise RuntimeError('missing browse driver')
m=importlib.util.module_from_spec(spec);spec.loader.exec_module(m)
class Receipt(unittest.TestCase):
 def test_remembered_session_checks_are_mandatory_in_both_phases(self):
  for phase,name in [(1,'remember-browsing-opt-in'),(2,'remembered-browsing-explicit-open')]:
   self.assertIn(name,m.CHECKS[phase])
   good={'schema':1,'ok':True,'phase':phase,'checks':list(m.CHECKS[phase])}
   if phase==1:good['template_preview']={'filename':'preview/12345.png','pool_filename':'e621/pools/sample_artist/Sample_pool/7_12345.png','invalid_rejected':True,'unsaved':True}
   self.assertTrue(m.validate(good,phase))
   with self.assertRaises(ValueError):m.validate(dict(good,checks=[x for x in good['checks'] if x!=name]),phase)

 def test_cache_clear_native_restart_check_is_required(self):
  self.assertIn('cache-clear-preservation',m.CHECKS[2])
  good={'schema':1,'ok':True,'phase':2,'checks':list(m.CHECKS[2])}
  self.assertTrue(m.validate(good,2))
  for checks in [[n for n in good['checks'] if n!='cache-clear-preservation'],good['checks']+['cache-clear-preservation']]:
   with self.assertRaises(ValueError):m.validate(dict(good,checks=checks),2)

 def test_template_preview_is_required_with_real_rendered_output_and_unsaved_state(self):
  self.assertIn('filename-template-preview',m.CHECKS[1])
  good={'schema':1,'ok':True,'phase':1,'checks':list(m.CHECKS[1]),'template_preview':{'filename':'preview/12345.png','pool_filename':'e621/pools/sample_artist/Sample_pool/7_12345.png','invalid_rejected':True,'unsaved':True}}
  self.assertTrue(m.validate(good,1))
  for payload in [None,{},dict(good['template_preview'],filename='wrong'),dict(good['template_preview'],unsaved=False),dict(good['template_preview'],invalid_rejected=False)]:
   with self.assertRaises(ValueError):m.validate(dict(good,template_preview=payload),1)
  with self.assertRaises(ValueError):m.validate({k:v for k,v in good.items() if k!='template_preview'},1)

 def test_requires_every_named_ui_path_not_process_exit_alone(self):
  good={'schema':1,'ok':True,'phase':1,'checks':['include-exclude-ratings','autocomplete','pagination-submitted-query','favorites','ordered-pool-holes','pin-persist','saved-search-persist','preferences-save','remember-browsing-opt-in','filename-template-preview','presentation-preferences','product-gallery-thumbnails','legacy-preview-confirm','account-verify-logout','product-adjacent-navigation','pool-gallery','pool-download-group','bounded-parallel-downloads','whole-pool-queue','viewer-cross-page-search','viewer-cross-page-pool','download-pause-ack','download-complete','download-cancel','normalized-metadata-rename',*[f'product-{id}-{kind}' for id,kind in [(100,'image'),(101,'animation'),(102,'animation'),(103,'animation'),(104,'video'),(105,'video')]]]}
  good['template_preview']={'filename':'preview/12345.png','pool_filename':'e621/pools/sample_artist/Sample_pool/7_12345.png','invalid_rejected':True,'unsaved':True}
  self.assertTrue(m.validate(good,1))
  for bad in [{},dict(good,ok=False),dict(good,phase=2),dict(good,checks=good['checks'][:-1]),dict(good,checks=good['checks']+good['checks'][:1])]:
   with self.assertRaises(ValueError):m.validate(bad,1)
  self.assertTrue(m.validate({'schema':1,'ok':True,'phase':2,'checks':['restart-state','saved-search-delete','unpin','preferences-restart','legacy-restart-delete','download-history-restart','pool-group-restart','whole-pool-restart','presentation-preferences-restart','local-completed-media-restart','normalized-metadata-restart','remembered-browsing-explicit-open','cache-clear-preservation']},2))
 def test_old_restart_receipt_cannot_pass_without_local_decode(self):
  old={'schema':1,'ok':True,'phase':2,'checks':['restart-state','saved-search-delete','unpin','preferences-restart','legacy-restart-delete','download-history-restart','pool-group-restart','whole-pool-restart','presentation-preferences-restart']}
  with self.assertRaises(ValueError):m.validate(old,2)
  current=dict(old,checks=old['checks']+['local-completed-media-restart','normalized-metadata-restart','remembered-browsing-explicit-open','cache-clear-preservation'])
  self.assertTrue(m.validate(current,2))
  for bad in [dict(current,checks=current['checks']+['local-completed-media-restart']),dict(current,ok=False)]:
   with self.assertRaises(ValueError):m.validate(bad,2)
 def test_metadata_campaign_requires_both_phases_and_exact_closed_inventory(self):
  for phase,name in [(1,'normalized-metadata-rename'),(2,'normalized-metadata-restart')]:
   self.assertIn(name,m.CHECKS[phase])
   good={'schema':1,'ok':True,'phase':phase,'checks':list(m.CHECKS[phase])}
   if phase==1:good['template_preview']={'filename':'preview/12345.png','pool_filename':'e621/pools/sample_artist/Sample_pool/7_12345.png','invalid_rejected':True,'unsaved':True}
   self.assertTrue(m.validate(good,phase))
   for checks in [good['checks'][:-1],good['checks']+[name],[n for n in good['checks'] if n!=name]]:
    with self.assertRaises(ValueError):m.validate(dict(good,checks=checks),phase)
 def test_metadata_bytes_receipts_preserve_collision_and_bind_exact_rename(self):
  import tempfile,json
  with tempfile.TemporaryDirectory() as tmp:
   root=Path(tmp);(root/'e621').mkdir();(root/'e621/100.png').write_bytes(b'art');(root/'e621/100 (1).png').write_bytes(b'art');(root/'e621/100.png.metadata.json').write_bytes(b'owned unrelated metadata must survive')
   payload={'representation':'normalized-furchive-v1','key':{'source':'e621','id':'100'},'url':'https://static1.e621.net/data/aa/bb/aabb0000000000000000000000000000.png'}
   side=root/'e621/100 (1).png.metadata.json';side.write_text(json.dumps(payload))
   jobs=[{'id':1,'metadata_status':'NotRequested'},{'id':8,'state':'Completed','relative_path':'e621/100.png','metadata_status':'Failed','metadata_error':'preserved'},{'id':9,'state':'Completed','relative_path':'e621/100 (1).png','metadata_status':'Written','metadata_error':None}]
   self.assertEqual(m.validate_metadata(root,jobs,b'art'),side.read_bytes())
   side.write_text(json.dumps(dict(payload,representation='raw')))
   with self.assertRaises(ValueError):m.validate_metadata(root,jobs,b'art')
if __name__=='__main__':unittest.main()
