"""Owned real-process regressions; never launch native Furchive or touch peers."""
from pathlib import Path
import importlib.util,json,os,subprocess,sys,tempfile,time,unittest
HERE=Path(__file__).resolve().parent
class LostParent(unittest.TestCase):
 def run_case(self,mode):
  import psutil
  with tempfile.TemporaryDirectory(prefix='supervisor-regression-',dir=HERE.parents[0]/'test-artifacts')as directory:
   root=Path(directory);leaf_file=root/'leaf.json';fixture=root/'fixture.py';runner=root/'runner.py'
   fixture.write_text("import os,sys,time,subprocess,json\nfrom pathlib import Path\nimport psutil\np=Path(sys.argv[1]);mode=sys.argv[2]\nif mode=='leaf':\n p.write_text(json.dumps({'pid':os.getpid(),'birth':psutil.Process().create_time()}));time.sleep(20)\nelse:\n time.sleep(.1)\n if mode=='lost-middle':\n  pid=os.fork()\n  if pid==0:\n   os.setsid();subprocess.Popen([sys.executable,__file__,str(p),'leaf'],start_new_session=True);os._exit(0)\n  time.sleep(20)\n else:\n  subprocess.Popen([sys.executable,__file__,str(p),'leaf'],start_new_session=True,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)\n  while not p.exists():time.sleep(.001)\n  sys.exit(7 if mode=='crash' else 0)\n")
   runner.write_text("import importlib.util,json,sys\nfrom pathlib import Path\ns=importlib.util.spec_from_file_location('smoke',sys.argv[1]);m=importlib.util.module_from_spec(s);s.loader.exec_module(m)\nwith Path(sys.argv[2]).open('wb')as log:\n try: result={'exit':m.supervise([sys.executable,sys.argv[3],sys.argv[4],sys.argv[5]],log,3 if sys.argv[5]!='lost-middle' else .7)}\n except TimeoutError:result={'timed_out':True}\nPath(sys.argv[6]).write_text(json.dumps(result))\n")
   try:
    result=subprocess.run([sys.executable,str(runner),str(HERE/'default-smoke.py'),str(root/'raw.log'),str(fixture),str(leaf_file),mode,str(root/'result.json')],capture_output=True,text=True,timeout=20)
    self.assertEqual(result.returncode,0,result.stderr)
    deadline=time.monotonic()+1
    while not leaf_file.exists()and time.monotonic()<deadline:time.sleep(.01)
    self.assertTrue(leaf_file.exists(),'fixture must reach real descendant boundary')
    identity=json.loads(leaf_file.read_text())
    try:p=psutil.Process(identity['pid']);alive=p.create_time()==identity['birth']and p.status()!=psutil.STATUS_ZOMBIE
    except psutil.NoSuchProcess:alive=False
    self.assertFalse(alive,'lost-parent detached descendant survived authoritative cleanup')
    receipt=json.loads((root/'result.json').read_text())
    if mode=='lost-middle':self.assertTrue(receipt.get('timed_out'))
    else:self.assertEqual(receipt['exit'],7 if mode=='crash'else 0)
   finally:
    if leaf_file.exists():
     identity=json.loads(leaf_file.read_text())
     try:
      p=psutil.Process(identity['pid'])
      if p.create_time()==identity['birth']and p.status()!=psutil.STATUS_ZOMBIE:
       p.kill()
       try:p.wait(timeout=3)
       except psutil.TimeoutExpired:pass
     except psutil.NoSuchProcess:pass
 def test_zero_exit_lost_parent(self):self.run_case('zero')
 def test_crash_exit_lost_parent(self):self.run_case('crash')
 def test_live_root_lost_middle_timeout(self):self.run_case('lost-middle')
 def test_timeout_preserves_actual_sigterm_exit(self):
  import signal
  with tempfile.TemporaryDirectory(prefix='supervisor-signal-',dir=HERE.parents[0]/'test-artifacts')as directory:
   root=Path(directory);runner=root/'runner.py'
   runner.write_text("import importlib.util,sys,json\nfrom pathlib import Path\ns=importlib.util.spec_from_file_location('smoke',sys.argv[1]);m=importlib.util.module_from_spec(s);s.loader.exec_module(m)\nwith Path(sys.argv[2]).open('wb')as log:\n try:m.supervise([sys.executable,'-c','import time;time.sleep(20)'],log,.2)\n except TimeoutError:pass\n")
   r=subprocess.run([sys.executable,str(runner),str(HERE/'default-smoke.py'),str(root/'signal.log')],capture_output=True,text=True,timeout=10);self.assertEqual(r.returncode,0,r.stderr)
   receipt=json.loads((root/'signal.log').read_text().splitlines()[-1]);self.assertTrue(receipt['timed_out']);self.assertEqual(receipt['raw_exit'],-signal.SIGTERM,'Popen must own the direct child wait status');self.assertEqual(receipt['survivors'],[])

if __name__=='__main__':unittest.main(verbosity=2)
