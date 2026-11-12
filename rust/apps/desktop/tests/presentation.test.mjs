import {test} from 'node:test';
import assert from 'node:assert/strict';
import * as view from '../src/lib/presentation.ts';

test('responsive window is bounded for 10,000 IDs at every scroll position',()=>{
 assert.equal(typeof view.gridWindow,'function','responsive virtual grid missing');
 for(const width of [1,320,800,1920,2400]) for(const top of [0,20000,1e9]) for(const height of [600,900,20000]) {
  const r=view.gridWindow(10000,width,top,height);
  assert.ok(r.columns>=1 && r.columns<=12); assert.ok(r.end-r.start<=108);
  assert.ok(r.start>=0 && r.end<=10000 && r.start<r.end);
 }
 assert.deepEqual(view.gridWindow(0,800,0,600),{columns:4,start:0,end:0,height:0,top:0,scrollTop:0});
});

test('keyboard selection stays clamped and uses stable ID through dataset reorder',()=>{
 assert.equal(typeof view.navigateId,'function','stable selection missing');
 const ids=['a','b','c','d','e'];
 assert.equal(view.navigateId(ids,'b','ArrowDown',3),'e');
 assert.equal(view.navigateId(ids,'e','ArrowRight',3),'e');
 assert.equal(view.navigateId(ids,'a','ArrowLeft',3),'a');
 assert.equal(view.navigateId(ids,'b','Home',3),'a');
 assert.equal(view.navigateId(ids,'b','End',3),'e');
 assert.equal(view.navigateId(['e','b','a'],'b','ArrowRight',2),'a');
 assert.equal(view.navigateId([],null,'Home',1),null);
});

test('fit, 100 percent, zoom and pan never expose unbounded offsets',()=>{
 assert.equal(typeof view.clampTransform,'function','bounded viewer transform missing');
 const fit=view.clampTransform(320,200,640,400,1,999,-999);
 assert.deepEqual(fit,{scale:1,x:0,y:0});
 const zoom=view.clampTransform(320,200,200,100,99,99999,-99999);
 assert.deepEqual(zoom,{scale:8,x:1180,y:-750});
 assert.equal(view.fitScale(320,200,160,100),0.5);
 assert.equal(view.fitScale(320,200,640,400),1);
});
