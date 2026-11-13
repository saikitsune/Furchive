import {test} from 'node:test';
import assert from 'node:assert/strict';
import * as gate from '../src/lib/media-evidence.ts';

function receipt(){return {schema:1,ok:true,engine:'owned-native-test',dataset:{total:10000,unique:10000,rowHeight:184},gallery:{initialMs:20,samples:[0,1,2,3,4,5].map(i=>({elapsedMs:12,mounted:40,start:i*40,end:i*40+40,scrollTop:i*1800})),selected:'fixture-9999',focused:true},images:['viewer-large.png','viewer-corrupt.png','viewer-truncated.png','viewer-oversized.png'].map((name,i)=>({name,status:i?'error':'ready',released:true})),videos:['vp8.webm','vp9.webm','h264.mp4'].map(name=>({name,played:true,seeked:true,speed:2,muted:true,volume:.5,time:0.7,released:true})),lifecycle:Array.from({length:20},(_,cycle)=>({cycle,released:true,staleIgnored:true,closed:true,remaining:0})),animationCheckpoints:['gif','apng','webp']};}

test('media evidence requires exact corpus, measured timing, full cleanup and separate host animation proof',()=>{
 assert.equal(typeof gate.validateMediaEvidence,'function','expanded media gate validator missing');
 assert.equal(gate.validateMediaEvidence(receipt()),true);
 for(const mutate of [r=>r.gallery.samples[0].mounted=10000,r=>r.gallery.samples[0].elapsedMs=251,r=>r.gallery.samples[0].elapsedMs=NaN,r=>r.gallery.initialMs=1001,r=>r.images.pop(),r=>r.images[0].status='error',r=>r.images[1].status='ready',r=>r.videos[0].released=false,r=>r.videos[0].played=false,r=>r.lifecycle.pop(),r=>r.lifecycle[0].remaining=1,r=>r.lifecycle[0].staleIgnored=false,r=>r.animationCheckpoints=['gif'],r=>r.dataset.unique=9999]) {
  const bad=receipt();mutate(bad);assert.throws(()=>gate.validateMediaEvidence(bad));
 }
 // This validates runtime observations only. Host screenshot evidence is a separate mandatory gate.
});


test('native runtime volume proof refuses absent nonfinite or incorrect live video volume',()=>{
 const good=receipt();for(const row of good.videos)row.volume=.5;
 assert.equal(gate.validateMediaEvidence(good),true);
 for(const value of [undefined,NaN,Infinity,-1,0,.36,1,2]){
  const bad=receipt();for(const row of bad.videos)row.volume=.5;bad.videos[0].volume=value;
  assert.throws(()=>gate.validateMediaEvidence(bad),'native live video volume must be measured at .5, not default or absent');
 }
});


test('native volume midpoint is exact after the real float32 backend representation boundary',()=>{
 const exact=receipt();for(const row of exact.videos)row.volume=Math.fround(.5);
 assert.equal(gate.validateMediaEvidence(exact),true,'exact midpoint proves a nondefault volume without demanding an unrepresentable float32 decimal');
 for(const value of [undefined,NaN,Infinity,0,1,Math.fround(.35)]){const bad=receipt();for(const row of bad.videos)row.volume=.5;bad.videos[0].volume=value;assert.throws(()=>gate.validateMediaEvidence(bad));}
});
