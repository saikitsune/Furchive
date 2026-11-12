import {test} from 'node:test';
import assert from 'node:assert/strict';
import * as media from '../src/lib/media.ts';

test('catalog refuses authority-bearing URLs, duplicate IDs and ungranted resources',()=>{
 assert.equal(typeof media.createCatalog,'function','validated media catalog is missing');
 const one={id:'opaque-1',kind:'image',src:'/fixtures/still.png',title:'<b>literal</b>'};
 const catalog=media.createCatalog([one],['/fixtures/still.png']);
 assert.equal(catalog[0].title,'<b>literal</b>');
 assert.ok(Object.isFrozen(catalog[0]));
 for(const src of ['file:///secret','javascript:alert(1)','data:image/png;base64,AA','//evil.test/a','/fixtures/../secret','/fixtures/%2e%2e/secret','http://evil.test/a','/fixtures/a\n.png']) {
  assert.throws(()=>media.createCatalog([{...one,src}],[src]),/URL/);
 }
 assert.throws(()=>media.createCatalog([one],[]),/grant/);
 assert.throws(()=>media.createCatalog([one,one],[one.src]),/ID/);
 assert.throws(()=>media.createCatalog([{...one,id:''}],[one.src]),/ID/);
 assert.throws(()=>media.createCatalog([{...one,kind:'html'}],[one.src]),/kind/);
 assert.equal(media.createCatalog([{...one,src:'http://127.0.0.1:12345/'+ 'a'.repeat(64) +'/h264.mp4',kind:'video'}],['http://127.0.0.1:12345/'+ 'a'.repeat(64) +'/h264.mp4']).length,1);
});

test('validated immutable catalog is reused only while exact grants remain unchanged',()=>{
 const item={id:'one',kind:'image',src:'/fixtures/still.png',title:'One'};
 const grants=[item.src];const catalog=media.createCatalog([item],grants);
 assert.equal(media.createCatalog(catalog,grants),catalog,'already validated immutable catalog should not be rebuilt per component');
 assert.throws(()=>media.createCatalog(catalog,[]),/grant/,'revocation must never be cached away');
 const mutable=[{...item}];media.createCatalog(mutable,grants);mutable[0].src='/fixtures/other.png';
 assert.throws(()=>media.createCatalog(mutable,grants),/grant/,'untrusted mutable input must be revalidated');
});

test('load generations cancel pending work, release video and ignore late completions',()=>{
 assert.equal(typeof media.LoadScope,'function','load generation scope missing');
 const scope=new media.LoadScope(); let cancelled=0, released=0;
 const a=scope.begin(()=>cancelled++);
 const b=scope.begin(()=>released++);
 assert.equal(cancelled,1); assert.equal(a.current(),false); assert.equal(b.current(),true);
 assert.equal(a.commit(()=>assert.fail('stale load committed')),false);
 let committed=0; assert.equal(b.commit(()=>committed++),true); assert.equal(committed,1);
 scope.close(); scope.close(); assert.equal(released,1); assert.equal(b.current(),false);
 const video={pause(){this.paused=true},removeAttribute(name){assert.equal(name,'src');this.srcRemoved=true},load(){this.loaded=true}};
 media.releaseVideo(video);
 assert.ok(video.paused && video.srcRemoved && video.loaded);
});

test('image dimensions refuse corrupt zero, nonfinite and excessive decoded images',()=>{
 assert.equal(typeof media.imageAllowed,'function','decoded image budget missing');
 for(const [w,h] of [[0,2],[2,0],[NaN,2],[2,Infinity],[9000,9000],[16385,1]]) assert.equal(media.imageAllowed(w,h),false);
 assert.equal(media.imageAllowed(4096,3072),true);
});
