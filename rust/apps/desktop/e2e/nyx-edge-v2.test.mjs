import test from 'node:test';
import assert from 'node:assert/strict';
import {readFile,writeFile,mkdir} from 'node:fs/promises';
import {fileURLToPath,pathToFileURL} from 'node:url';
import {Window} from 'happy-dom';
const window=new Window({url:'http://localhost'});
for(const key of ['window','document','navigator','HTMLElement','Element','Node','Text','Comment','Event','MouseEvent','KeyboardEvent','CustomEvent','MutationObserver','HTMLMediaElement','HTMLVideoElement','HTMLImageElement','getComputedStyle'])Object.defineProperty(globalThis,key,{value:key==='getComputedStyle'?window.getComputedStyle.bind(window):window[key],configurable:true});
globalThis.ResizeObserver=class{observe(){}disconnect(){}};
const {compile}=await import('svelte/compiler');const {mount,unmount,flushSync}=await import('svelte');
const output=new URL('../../../test-artifacts/browser-components/',import.meta.url);await mkdir(output,{recursive:true});
let viewerCode=compile(await readFile(new URL('../src/lib/Viewer.svelte',import.meta.url),'utf8'),{filename:'Viewer.svelte',generate:'client',dev:true}).js.code;
viewerCode=viewerCode.replace(/(?:from|import) (['"])(svelte[^'"]*)\1/g,(all,q,spec)=>all.replace(spec,import.meta.resolve(spec))).replace(/from (['"])(\.\/[^'"]*)\1/g,(_,q,spec)=>`from '${new URL('../src/lib/'+spec.slice(2)+'.ts',import.meta.url)}'`);
await writeFile(new URL('Viewer.mjs',output),viewerCode);
let galleryCode=compile(await readFile(new URL('../src/lib/Gallery.svelte',import.meta.url),'utf8'),{filename:'Gallery.svelte',generate:'client',dev:true}).js.code;galleryCode=galleryCode.replace(/(?:from|import) (['"])(svelte[^'"]*)\1/g,(all,q,spec)=>all.replace(spec,import.meta.resolve(spec))).replace(/from (['"])(\.\/[^'"]*)\1/g,(_,q,spec)=>`from '${new URL('../src/lib/'+spec.slice(2)+'.ts',import.meta.url)}'`);await writeFile(new URL('Gallery.mjs',output),galleryCode);
let source=compile(await readFile(new URL('../src/lib/BrowsePanel.svelte',import.meta.url),'utf8'),{filename:'BrowsePanel.svelte',generate:'client',dev:true}).js.code;
source=source.replace(/(?:from|import) (['"])(svelte[^'"]*)\1/g,(all,q,spec)=>all.replace(spec,import.meta.resolve(spec))).replace(/from (['"])(\.\/[^'"]*)\1/g,(_,q,spec)=>`from '${(spec.endsWith('.svelte')?new URL(spec.slice(2).replace('.svelte','.mjs'),output):new URL('../src/lib/'+spec.slice(2)+'.ts',import.meta.url))}'`);
const file=new URL('BrowsePanel.mjs',output);await writeFile(file,source);const Component=(await import(file.href)).default;
const {BrowserBridge:ThinBrowserBridge}=await import('../src/lib/browser-bridge.ts');
const {viewableFixture}=await import('./viewable-fixture.mjs');
class BrowserBridge extends ThinBrowserBridge{constructor(invoke){super(async(...args)=>viewableFixture(await invoke(...args)));}}
async function settle(){await new Promise(r=>setTimeout(r,0));flushSync();}
function input(label,value){const e=document.querySelector(`[aria-label="${label}"]`);assert.ok(e,label);e.value=value;e.dispatchEvent(new Event('input',{bubbles:true}));flushSync();return e;}
function click(text){const b=[...document.querySelectorAll('button')].find(e=>e.textContent.trim()===text);assert.ok(b,text);b.click();flushSync();}

const prefs={filename_template:'{id}',pool_filename_template:'{id}',concurrent_downloads:1,duplicates:'skip',gallery_scale:1,cache_limit_bytes:16777216,restore_session:false,save_metadata:false,start_muted:true};
const mediaURL='https://static1.e621.net/data/aa/bb/aabb0000000000000000000000000000.png';
const post=(id,viewable=true)=>({key:{source:'e621',id},title:'Post '+id,artists:[],tags:{},description:'',rating:'safe',score:1,full_url:viewable?mediaURL:null});
const prepared=id=>({id,kind:'image',title:'Post '+id,src:'http://127.0.0.1:43123/'+'a'.repeat(64)+'/media/'+'b'.repeat(64)});
const receipt=()=>({selected_id:'200',page:2,offset:0,search:{items:[post('200')],raw_count:1,next_page:null},pool:null});
function bridgeFor(rows,handleStep,calls){return new BrowserBridge(async(command,args)=>{calls.push({command,args});if(command==='saved_searches'||command==='pinned_pools')return [];if(command==='preferences')return prefs;if(command==='search')return {items:rows,raw_count:50,next_page:2};if(command==='prepare_media'){if(rows.find(p=>p.key.id===args.id)?.full_url===null)throw Error('media unavailable or filtered');return prepared(args.id);}if(command==='viewer_step')return handleStep(args);throw Error(command);});}
async function cleanup(app){document.querySelector('[aria-label="Close viewer"]')?.click();flushSync();await settle();await unmount(app);window.happyDOM.abort();}
test('NYX-XPV-01 trailing unviewable record blocks cross-page next',async()=>{
 const calls=[],bridge=bridgeFor([post('100'),post('101',false)],()=>receipt(),calls);
 const app=mount(Component,{target:document.body,props:{bridge}});flushSync();await settle();let observed;
 try{click('Search');await settle();click('View artwork');await settle();document.querySelector('[aria-label="Next media"]').click();await settle();await settle();observed={selected:document.querySelector('[data-viewer]')?.dataset.id,calls:calls.filter(x=>['viewer_step','prepare_media'].includes(x.command)),error:document.querySelector('[role="alert"]')?.textContent};console.log('OBSERVED',JSON.stringify(observed));}finally{await cleanup(app);}
 assert.equal(observed.selected,'200','Next must use the viewable edge rather than preparing missing media forever');
});
test('NYX-XPV-02 explicit close supersedes delayed edge receipt',async()=>{
 const calls=[];let finish;const bridge=bridgeFor([post('100')],()=>new Promise(r=>finish=r),calls);
 const app=mount(Component,{target:document.body,props:{bridge}});flushSync();await settle();let observed;
 try{click('Search');await settle();click('View artwork');await settle();document.querySelector('[aria-label="Next media"]').click();await settle();assert.equal(document.querySelector('[data-viewer]'),null);click('View artwork');await settle();assert.equal(document.querySelector('[data-viewer]')?.dataset.id,'100');document.querySelector('[aria-label="Close viewer"]').click();flushSync();assert.equal(document.querySelector('[data-viewer]'),null);finish(receipt());await settle();await settle();observed={selected:document.querySelector('[data-viewer]')?.dataset.id??null,calls:calls.filter(x=>['viewer_step','prepare_media'].includes(x.command))};console.log('OBSERVED',JSON.stringify(observed));}finally{await cleanup(app);}
 assert.equal(observed.selected,null,'Explicit close must not be undone by an older edge receipt');
});
test('NYX-XPV-03 newer gallery selection supersedes delayed edge receipt',async()=>{
 const calls=[];let finish;const bridge=bridgeFor([post('99'),post('100')],()=>new Promise(r=>finish=r),calls);
 const app=mount(Component,{target:document.body,props:{bridge}});flushSync();await settle();let observed;
 try{click('Search');await settle();document.querySelector('[data-post-id="100"] button').click();await settle();document.querySelector('[aria-label="Next media"]').click();await settle();document.querySelector('[data-post-id="99"] button').click();await settle();assert.equal(document.querySelector('[data-viewer]')?.dataset.id,'99');finish(receipt());await settle();await settle();observed={selected:document.querySelector('[data-viewer]')?.dataset.id??null,calls:calls.filter(x=>['viewer_step','prepare_media'].includes(x.command))};console.log('OBSERVED',JSON.stringify(observed));}finally{await cleanup(app);}
 assert.equal(observed.selected,'99','New gallery selection must supersede old page turn');
});
test('close and newer selection remain final while backend page reconciliation is delayed',async()=>{
 const calls=[];let finish,restore;let searches=0;
 const rows=[post('99'),post('100')];const bridge=new BrowserBridge(async(command,args)=>{calls.push({command,args});if(command==='saved_searches'||command==='pinned_pools')return [];if(command==='preferences')return prefs;if(command==='search'){if(++searches===1)return {items:rows,raw_count:50,next_page:2};return new Promise(r=>restore=r);}if(command==='prepare_media')return prepared(args.id);if(command==='viewer_step')return new Promise(r=>finish=r);throw Error(command);});
 const app=mount(Component,{target:document.body,props:{bridge}});flushSync();await settle();let observed;
 try{click('Search');await settle();document.querySelector('[data-post-id="100"] button').click();await settle();document.querySelector('[aria-label="Next media"]').click();await settle();document.querySelector('[data-post-id="99"] button').click();await settle();finish(receipt());await settle();assert.equal(typeof restore,'function');document.querySelector('[data-post-id="100"] button').click();await settle();const before=calls.filter(c=>c.command==='prepare_media').length;restore({items:rows,raw_count:50,next_page:2});await settle();await settle();observed={selected:document.querySelector('[data-viewer]')?.dataset.id,after:calls.filter(c=>c.command==='prepare_media').length,before};}finally{await cleanup(app);}
 assert.equal(observed.selected,'100');assert.equal(observed.after,observed.before+1,'latest selection must be re-prepared under restored backend page authority');
});
test('NYX-XPV-control ordinary valid edge opens returned page',async()=>{
 const calls=[],bridge=bridgeFor([post('100')],()=>receipt(),calls);const app=mount(Component,{target:document.body,props:{bridge}});flushSync();await settle();let id;
 try{click('Search');await settle();click('View artwork');await settle();document.querySelector('[aria-label="Next media"]').click();await settle();await settle();id=document.querySelector('[data-viewer]')?.dataset.id;}finally{await cleanup(app);}
 assert.equal(id,'200');assert.equal(calls.filter(x=>x.command==='viewer_step').length,1);
});
