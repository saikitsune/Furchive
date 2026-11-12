import {test as nodeTest,after} from 'node:test';
const test=(name,fn)=>nodeTest(name,{timeout:8000},fn);
import assert from 'node:assert/strict';
import {readFile,writeFile,mkdir} from 'node:fs/promises';
import {pathToFileURL,fileURLToPath} from 'node:url';
import path from 'node:path';

const {Window}=await import(process.env.FURCHIVE_DOM_MODULE || 'happy-dom');
const window=new Window({url:'http://localhost'});
for(const key of ['window','document','navigator','HTMLElement','Element','Node','Text','Comment','Event','MouseEvent','KeyboardEvent','CustomEvent','HTMLMediaElement','HTMLVideoElement','HTMLImageElement','MutationObserver','getComputedStyle']) {
 Object.defineProperty(globalThis,key,{value:key==='getComputedStyle'?window.getComputedStyle.bind(window):window[key],configurable:true});
}
globalThis.ResizeObserver=class {observe(){} disconnect(){}};
const {compile}=await import('svelte/compiler');
const {mount,unmount,flushSync}=await import('svelte');
const output=path.join(fileURLToPath(new URL('../../../test-artifacts/',import.meta.url)),'furchive-components'); await mkdir(output,{recursive:true});
async function component(name){
 let result=compile(await readFile(new URL(`../src/lib/${name}.svelte`,import.meta.url),'utf8'),{filename:name+'.svelte',generate:'client',dev:true}).js.code;
 result=result.replace(/import (['"])(svelte[^'"]*)\1/g,(_,q,spec)=>`import '${import.meta.resolve(spec)}'`).replace(/from (['"])(svelte[^'"]*)\1/g,(_,q,spec)=>`from '${import.meta.resolve(spec)}'`)
 .replace(/from (['"])(\.\/[^'"]*)\1/g,(_,q,spec)=>`from '${new URL('../src/lib/'+spec.slice(2)+(path.extname(spec)?'':'.ts'),import.meta.url)}'`);
 const file=path.join(output,name+'.mjs');await writeFile(file,result);return (await import(pathToFileURL(file).href+'?t='+Date.now())).default;
}
const {createCatalog}=await import('../src/lib/media.ts');
const items=createCatalog(Array.from({length:10000},(_,i)=>({id:`id-${i}`,kind:'image',src:'/fixtures/still.png',title:`Item ${i}`})),['/fixtures/still.png']);
let active;
async function viewerHost(){
 await component('Viewer');
 const file=path.join(output,'ViewerHost.mjs');
 const source=`<script>import Viewer from '${pathToFileURL(path.join(output,'Viewer.mjs')).href}';let {items,grants}=$props();let id=$state(items[0]?.id??null);export function select(value){id=value;}function close(){id=null;}</script><Viewer {items} {grants} selectedId={id} onselect={value=>id=value} onclose={close}/>`;
 let code=compile(source,{filename:'ViewerHost.svelte',generate:'client',dev:true}).js.code.replace(/(?:from|import) (['"])(svelte[^'"]*)\1/g,(all,q,spec)=>all.replace(spec,import.meta.resolve(spec)));
 await writeFile(file,code);return (await import(pathToFileURL(file).href)).default;
}
after(async()=>{if(active)await unmount(active);window.happyDOM.abort();});

test('rendered Gallery keeps virtual DOM bounded and keyboard selection survives scroll',async()=>{
 const Gallery=await component('Gallery'); let selected;
 active=mount(Gallery,{target:document.body,props:{items,grants:['/fixtures/still.png'],onselect:id=>selected=id}});flushSync();
 const gallery=document.querySelector('[data-gallery]');assert.ok(gallery);
 assert.ok(document.querySelectorAll('[data-gallery-tile]').length<=108);
 gallery.dispatchEvent(new KeyboardEvent('keydown',{key:'End',bubbles:true}));flushSync();
 assert.equal(gallery.getAttribute('data-selected'),'id-9999');
 gallery.dispatchEvent(new KeyboardEvent('keydown',{key:'Enter',bubbles:true}));flushSync();assert.equal(selected,'id-9999');
 gallery.scrollTop=0;gallery.dispatchEvent(new Event('scroll'));flushSync();
 assert.equal(gallery.getAttribute('data-selected'),'id-9999');assert.ok(document.querySelectorAll('[data-gallery-tile]').length<=108);
 gallery.dispatchEvent(new KeyboardEvent('keydown',{key:'Home',bubbles:true}));flushSync();assert.equal(gallery.getAttribute('data-selected'),'id-0');
 gallery.dispatchEvent(new KeyboardEvent('keydown',{key:'ArrowDown',bubbles:true}));flushSync();assert.equal(gallery.getAttribute('data-selected'),'id-4');
 assert.equal(gallery.getAttribute('aria-activedescendant'),'tile-id-4');
 await unmount(active);active=null;
});

test('rendered Viewer fences stale image events and refuses corrupt or oversized media',async()=>{
 const Host=await viewerHost();
 const sample=[{id:'a',kind:'image',src:'/fixtures/a.png',title:'<b>literal</b>'},{id:'b',kind:'image',src:'/fixtures/b.png',title:'B'}];
 active=mount(Host,{target:document.body,props:{items:sample,grants:sample.map(i=>i.src)}});flushSync();
 const old=document.querySelector('[data-viewer] img');assert.ok(old);
 active.select('b');flushSync();
 Object.defineProperties(old,{naturalWidth:{value:320},naturalHeight:{value:200}});old.dispatchEvent(new Event('load'));flushSync();
 assert.equal(document.querySelector('[data-viewer]').getAttribute('data-id'),'b');
 assert.equal(document.querySelector('[data-viewer]').getAttribute('data-status'),'loading');
 const current=document.querySelector('[data-viewer] img');
 Object.defineProperties(current,{naturalWidth:{value:9000},naturalHeight:{value:9000}});current.dispatchEvent(new Event('load'));flushSync();
 assert.equal(document.querySelector('[data-viewer]').getAttribute('data-status'),'error');
 assert.equal(current.hasAttribute('src'),false);
 current.dispatchEvent(new Event('load'));flushSync();assert.equal(document.querySelector('[data-viewer]').getAttribute('data-status'),'error','a refused generation cannot revive');
 active.select('a');flushSync();document.querySelector('[data-viewer] img').dispatchEvent(new Event('error'));flushSync();
 assert.equal(document.querySelector('[data-viewer]').getAttribute('data-status'),'error');
 const failed=document.querySelector('[data-viewer] img');Object.defineProperties(failed,{naturalWidth:{value:320},naturalHeight:{value:200}});failed.dispatchEvent(new Event('load'));flushSync();assert.equal(document.querySelector('[data-viewer]').getAttribute('data-status'),'error');
 await unmount(active);active=null;
});

test('rendered Viewer waits for decoded video data instead of enabling Play on metadata alone',async()=>{
 const Host=await viewerHost();active=mount(Host,{target:document.body,props:{items:[{id:'v',kind:'video',src:'/fixtures/h264.mp4',title:'Video'}],grants:['/fixtures/h264.mp4']}});flushSync();
 try{
  const video=document.querySelector('video');assert.equal(video.preload,'auto');
  Object.defineProperties(video,{videoWidth:{value:320},videoHeight:{value:200},duration:{value:2}});
  video.dispatchEvent(new Event('loadedmetadata'));flushSync();
  assert.equal(document.querySelector('[data-viewer]').getAttribute('data-status'),'loading');
  assert.equal(document.querySelector('[aria-label="Play video"]').disabled,true);
  video.dispatchEvent(new Event('loadeddata'));flushSync();
  assert.equal(document.querySelector('[data-viewer]').getAttribute('data-status'),'ready');
  assert.equal(document.querySelector('[aria-label="Play video"]').disabled,false);
 }finally{if(active)await unmount(active);active=null;}
});

test('rendered Viewer releases video on navigation, close and unmount; controls use live element',async()=>{
 const Host=await viewerHost();
 const sample=[{id:'v',kind:'video',src:'/fixtures/h264.mp4',title:'Video'},{id:'i',kind:'image',src:'/fixtures/still.png',title:'Image'}];
 let pauses=0,loads=0;window.HTMLMediaElement.prototype.pause=function(){pauses++};window.HTMLMediaElement.prototype.load=function(){loads++};
 active=mount(Host,{target:document.body,props:{items:sample,grants:sample.map(i=>i.src)}});flushSync();
 const video=document.querySelector('video');
 Object.defineProperties(video,{videoWidth:{value:320},videoHeight:{value:200},duration:{value:2}});
 video.dispatchEvent(new Event('loadeddata'));flushSync();
 assert.equal(document.querySelector('[data-viewer]').getAttribute('data-status'),'ready');
 const speed=document.querySelector('[aria-label="Playback speed"]');speed.value='2';speed.dispatchEvent(new Event('change',{bubbles:true}));flushSync();assert.equal(video.playbackRate,2);
 document.querySelector('[aria-label="Unmute video"]').click();flushSync();assert.equal(video.muted,false);
 const seek=document.querySelector('[aria-label="Seek video"]');seek.value='1';seek.dispatchEvent(new Event('input',{bubbles:true}));assert.equal(video.currentTime,1);
 active.select('i');flushSync();assert.equal(video.hasAttribute('src'),false);assert.ok(pauses>=1&&loads>=1);
 active.select('v');flushSync();const second=document.querySelector('video');document.querySelector('[aria-label="Close viewer"]').click();flushSync();assert.equal(second.hasAttribute('src'),false);assert.equal(document.querySelector('[data-viewer]'),null);
 for(let i=0;i<12;i++){active.select('v');flushSync();const repeated=document.querySelector('video');active.select('i');flushSync();assert.equal(repeated.hasAttribute('src'),false);}
 await unmount(active);active=null;
});

test('rendered Viewer establishes persistent mute defaults before assigning a source',async()=>{
 const Host=await viewerHost();const prototype=window.HTMLMediaElement.prototype;
 const descriptor=Object.getOwnPropertyDescriptor(prototype,'src');assert.ok(descriptor?.set&&descriptor.get);
 const observed=[];
 Object.defineProperty(prototype,'src',{...descriptor,set(value){observed.push({muted:this.muted,defaultMuted:this.defaultMuted,attribute:this.hasAttribute('muted')});descriptor.set.call(this,value);this.muted=this.defaultMuted;}});
 try{
  active=mount(Host,{target:document.body,props:{items:[{id:'v',kind:'video',src:'/fixtures/h264.mp4',title:'Video'}],grants:['/fixtures/h264.mp4']}});flushSync();
  assert.deepEqual(observed,[{muted:true,defaultMuted:true,attribute:true}]);
  assert.equal(document.querySelector('video').muted,true);
 }finally{Object.defineProperty(prototype,'src',descriptor);if(active)await unmount(active);active=null;}
});

test('rendered Viewer preserves explicit mute intent across native volume state changes',async()=>{
 const Host=await viewerHost();active=mount(Host,{target:document.body,props:{items:[{id:'v',kind:'video',src:'/fixtures/h264.mp4',title:'Video'}],grants:['/fixtures/h264.mp4']}});flushSync();
 try{
  const video=document.querySelector('video');
  video.muted=false;video.dispatchEvent(new Event('volumechange'));flushSync();assert.equal(video.muted,true,'native pipeline reset must not override initial mute intent');
  document.querySelector('[aria-label="Unmute video"]').click();flushSync();assert.equal(video.muted,false,'explicit user unmute remains allowed');
  video.muted=true;video.dispatchEvent(new Event('volumechange'));flushSync();assert.equal(video.muted,false,'native changes must not override explicit unmute either');
 }finally{if(active)await unmount(active);active=null;}
});

test('rendered Viewer surfaces the native play rejection rather than an unexplained timeout',async()=>{
 const Host=await viewerHost();
 const previous=window.HTMLMediaElement.prototype.play;
 window.HTMLMediaElement.prototype.play=async function(){throw new window.DOMException('Native gesture required','NotAllowedError')};
 try{
  active=mount(Host,{target:document.body,props:{items:[{id:'v',kind:'video',src:'/fixtures/h264.mp4',title:'Video'}],grants:['/fixtures/h264.mp4']}});flushSync();
  const video=document.querySelector('video');Object.defineProperties(video,{videoWidth:{value:320},videoHeight:{value:200},duration:{value:2},paused:{value:true}});video.dispatchEvent(new Event('loadeddata'));flushSync();
  document.querySelector('[aria-label="Play video"]').click();await Promise.resolve();await Promise.resolve();flushSync();
  assert.match(document.querySelector('[data-playback-message]').textContent,/NotAllowedError/);
 }finally{window.HTMLMediaElement.prototype.play=previous;if(active)await unmount(active);active=null;}
});

test('rendered Viewer preserves the original decode failure when pending play is then aborted',async()=>{
 const Host=await viewerHost();const previous=window.HTMLMediaElement.prototype.play;let rejectPlay;
 window.HTMLMediaElement.prototype.play=function(){return new Promise((resolve,reject)=>rejectPlay=reject)};
 try{
  active=mount(Host,{target:document.body,props:{items:[{id:'v',kind:'video',src:'/fixtures/h264.mp4',title:'Video'}],grants:['/fixtures/h264.mp4']}});flushSync();
  const video=document.querySelector('video');Object.defineProperties(video,{videoWidth:{value:320},videoHeight:{value:200},duration:{value:2},paused:{value:true},error:{value:{code:3,message:'native decoder refused frame'},configurable:true}});video.dispatchEvent(new Event('loadeddata'));flushSync();
  document.querySelector('[aria-label="Play video"]').click();video.dispatchEvent(new Event('error'));flushSync();
  rejectPlay(new window.DOMException('source removed','AbortError'));await Promise.resolve();await Promise.resolve();flushSync();
  assert.match(document.querySelector('[data-viewer] .status').textContent,/Media error 3/);
  assert.doesNotMatch(document.querySelector('[data-viewer] .status').textContent,/Playback was refused/);
  assert.equal(video.hasAttribute('src'),false);
  Object.defineProperty(video,'error',{value:{code:4,message:'secondary source reset'},configurable:true});video.dispatchEvent(new Event('error'));flushSync();
  assert.match(document.querySelector('[data-viewer] .status').textContent,/Media error 3/,'cleanup must preserve first failure');
 }finally{window.HTMLMediaElement.prototype.play=previous;if(active)await unmount(active);active=null;}
});

test('rendered Viewer fit, 100 percent and bounded zoom reset operate on media transform',async()=>{
 const Host=await viewerHost();active=mount(Host,{target:document.body,props:{items:[{id:'large',kind:'image',src:'/fixtures/large.png',title:'<b>literal</b>'}],grants:['/fixtures/large.png']}});flushSync();
 const img=document.querySelector('[data-viewer] img');Object.defineProperties(img,{naturalWidth:{value:1280},naturalHeight:{value:800}});img.dispatchEvent(new Event('load'));flushSync();
 const viewer=document.querySelector('[data-viewer]');assert.equal(viewer.getAttribute('data-scale'),'0.5');assert.equal(viewer.querySelector('h2 b'),null);
 [...viewer.querySelectorAll('button')].find(b=>b.textContent==='100%').click();flushSync();assert.equal(viewer.getAttribute('data-scale'),'1');
 for(let i=0;i<20;i++){viewer.querySelector('[aria-label="Zoom in"]').click();flushSync();}assert.equal(viewer.getAttribute('data-scale'),'8');
 [...viewer.querySelectorAll('button')].find(b=>b.textContent==='Fit / reset').click();flushSync();assert.equal(viewer.getAttribute('data-scale'),'0.5');
 viewer.querySelector('[aria-label="Zoom in"]').click();flushSync();assert.equal(viewer.getAttribute('data-scale'),'0.625');
 const stage=viewer.querySelector('[data-stage]');stage.setPointerCapture=()=>{};
 for(let i=0;i<20;i++){viewer.querySelector('[aria-label="Zoom in"]').click();flushSync();}
 stage.dispatchEvent(new window.PointerEvent('pointerdown',{button:0,pointerId:1,clientX:100,clientY:100,bubbles:true}));
 stage.dispatchEvent(new window.PointerEvent('pointermove',{pointerId:1,clientX:100000,clientY:-100000,bubbles:true}));flushSync();
 assert.equal(viewer.getAttribute('data-pan-x'),'4800');assert.equal(viewer.getAttribute('data-pan-y'),'-3000');
 stage.dispatchEvent(new window.PointerEvent('pointerup',{pointerId:1,bubbles:true}));
 const buttons=[...viewer.querySelectorAll('button:not(:disabled)')];buttons.at(-1).focus();
 buttons.at(-1).dispatchEvent(new KeyboardEvent('keydown',{key:'Tab',bubbles:true,cancelable:true}));flushSync();assert.equal(document.activeElement,buttons[0]);
 await unmount(active);active=null;
});
