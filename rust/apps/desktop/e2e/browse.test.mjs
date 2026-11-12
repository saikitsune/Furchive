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
const {BrowserBridge}=await import('../src/lib/browser-bridge.ts');
async function settle(){await new Promise(r=>setTimeout(r,0));flushSync();}
function input(label,value){const e=document.querySelector(`[aria-label="${label}"]`);assert.ok(e,label);e.value=value;e.dispatchEvent(new Event('input',{bubbles:true}));flushSync();return e;}
function click(text){const b=[...document.querySelectorAll('button')].find(e=>e.textContent.trim()===text);assert.ok(b,text);b.click();flushSync();}
test('rendered product search preserves chips ratings favorites and pool holes through the real thin bridge',async()=>{
 const calls=[];let poolCalls=0;
  const bridge=new BrowserBridge(async(command,args)=>{
  if(command==='preferences')return {filename_template:'{source}/{id}.{ext}',pool_filename_template:'{source}/{pool_name}/{id}.{ext}',concurrent_downloads:3,duplicates:'skip',gallery_scale:1,cache_limit_bytes:536870912,restore_session:false,save_metadata:false,start_muted:true};
  calls.push({command,args});if(command==='saved_searches'||command==='pinned_pools'||command==='suggestions')return [];
  if(command==='search'||command==='favorites')return {items:[],raw_count:50,next_page:2};
  if(command==='pool_page'){poolCalls++;return {pool:{key:{source:'e621',id:'3'},name:'Fixture pool',post_ids:['10','2','30'],description:null},entries:[{id:'10',position:0,post:null},{id:'2',position:1,post:{key:{source:'e621',id:'2'},title:'Fixture'}},{id:'30',position:2,post:null}],total:3,next_offset:null};}
 });
 const app=mount(Component,{target:document.body,props:{bridge}});flushSync();await settle();
 try{
  input('Include tags','fox');click('Add include');input('Exclude tags','comic');click('Add exclude');click('Search');await settle();
  const first=calls.find(c=>c.command==='search');assert.deepEqual(first.args.query.include,['fox']);assert.deepEqual(first.args.query.exclude,['comic']);assert.deepEqual(first.args.query.ratings,{only:['safe']});
  input('Include tags','wolf');click('Add include');click('Next page');await settle();assert.equal(calls.filter(c=>c.command==='search').at(-1).args.page,2);assert.deepEqual(calls.filter(c=>c.command==='search').at(-1).args.query.include,['fox'],'pagination must retain submitted query rather than unsent edits');
  input('Favorites username','fixture_user');click('Browse favorites');await settle();assert.equal(calls.find(c=>c.command==='favorites').args.username,'fixture_user');
  input('Pool ID','3');click('Open pool');await settle();assert.equal(poolCalls,1);assert.match(document.body.textContent,/Unavailable or filtered/);assert.match(document.body.textContent,/#3/);assert.match(document.body.textContent,/Fixture pool/);
  click('Pin pool');await settle();assert.deepEqual(calls.find(c=>c.command==='set_pin').args,{id:3,pinned:true});
  input('Saved search name','Fox collection');click('Save search');await settle();assert.equal(calls.find(c=>c.command==='save_search').args.search.name,'Fox collection');
 }finally{await unmount(app);window.happyDOM.abort();}
});

test('product gallery prepares only mounted preview keys and never binds remote metadata URLs directly',async()=>{
 const calls=[];const bridge=new BrowserBridge(async(command,args)=>{calls.push({command,args});if(command==='saved_searches'||command==='pinned_pools')return [];if(command==='search')return {items:Array.from({length:50},(_,i)=>({key:{source:'e621',id:String(100+i)},title:'Local '+i,artists:[],tags:{},description:'fixture',rating:'safe',score:1,preview_url:'https://untrusted.invalid/pixel'})),raw_count:50,next_page:2};if(command==='prepare_media')return {id:args.id,kind:'image',title:'Post',src:'http://127.0.0.1:43123/'+'a'.repeat(64)+'/media/'+'b'.repeat(64)};throw Error(command);});
 const app=mount(Component,{target:document.body,props:{bridge}});flushSync();await settle();try{click('Search');await settle();await settle();assert.ok(document.querySelector('[data-gallery]'));const tiles=document.querySelectorAll('[data-gallery-tile]');assert.ok(tiles.length>0&&tiles.length<=108);assert.ok(calls.some(c=>c.command==='prepare_media'&&c.args.variant==='preview'));assert.ok(![...document.querySelectorAll('[data-gallery] img')].some(n=>n.getAttribute('src')?.includes('untrusted.invalid')));}finally{await unmount(app);window.happyDOM.abort();}
});

test('product selection fetches only the current post key and exposes granted media to the reusable viewer',async()=>{
 const calls=[];const bridge=new BrowserBridge(async(command,args)=>{calls.push({command,args});if(command==='saved_searches'||command==='pinned_pools')return [];if(command==='search')return {items:[{key:{source:'e621',id:'100'},title:'Local image',artists:[],tags:{},description:'fixture',rating:'safe',score:1}],raw_count:1,next_page:null};if(command==='prepare_media')return {id:'100',kind:'image',title:'Post 100',src:'http://127.0.0.1:43123/'+'a'.repeat(64)+'/media/'+'b'.repeat(64)};throw Error(command);});
 const app=mount(Component,{target:document.body,props:{bridge}});flushSync();await settle();try{
  click('Search');await settle();click('View artwork');await settle();assert.deepEqual(calls.find(c=>c.command==='prepare_media').args,{id:'100',variant:'full'});assert.equal(document.querySelector('[data-viewer]')?.dataset.id,'100');assert.match(document.querySelector('[data-viewer] [data-media]').getAttribute('src'),/\/media\/[a-f0-9]{64}$/);
 }finally{await unmount(app);window.happyDOM.abort();}
});

test('download controls require directory grant and acknowledge pause resume cancel through thin intents',async()=>{
 const calls=[];let jobs=[];const bridge=new BrowserBridge(async(command,args)=>{calls.push({command,args});if(command==='saved_searches'||command==='pinned_pools')return [];if(command==='search')return {items:[{key:{source:'e621',id:'100'},title:'Fixture',artists:[],tags:{},description:'',rating:'safe',score:1}],raw_count:1,next_page:null};if(command==='choose_download_directory')return true;if(command==='download_jobs')return structuredClone(jobs);if(command==='queue_download'){jobs=[{id:1,post_id:'100',state:'Paused',bytes:0,relative_path:'e621/100.png',error:null}];return 1;}if(command==='download_control'){jobs[0].state={pause:'Paused',resume:'Running',cancel:'Cancelled'}[args.verb];return;}throw Error(command);});
 const app=mount(Component,{target:document.body,props:{bridge}});flushSync();await settle();try{click('Choose download directory');await settle();click('Search');await settle();click('Queue download');await settle();assert.ok(document.querySelector('[data-job-id="1"]'));click('Resume');await settle();click('Pause');await settle();click('Cancel');await settle();assert.deepEqual(calls.filter(c=>c.command==='download_control').map(c=>c.args.verb),['resume','pause','cancel']);assert.match(document.querySelector('[data-job-id="1"]').textContent,/Cancelled/);}finally{await unmount(app);window.happyDOM.abort();}
});

test('legacy preview requires explicit selected records before confirmation and never sends a source path',async()=>{
 let searches=[],calls=[];const query={include:['fox'],exclude:[],ratings:{only:['safe']},artist:null,sort:'newest'};
 const bridge=new BrowserBridge(async(command,args)=>{calls.push({command,args});if(command==='saved_searches')return searches;if(command==='pinned_pools')return [];if(command==='legacy_preview')return {digest:'a'.repeat(64),searches:[{id:'legacy-1',name:'Recovered foxes',query}],pins:[],warnings:['Credentials excluded'],queue:'Absent'};if(command==='legacy_confirm'){searches=[{id:'legacy-1',name:'Recovered foxes',query}];return {imported_searches:1,imported_pins:0,skipped_searches:0,skipped_pins:0};}throw Error(command);});
 const app=mount(Component,{target:document.body,props:{bridge}});flushSync();await settle();try{click('Preview legacy data');await settle();assert.ok(!calls.some(c=>c.command==='legacy_confirm'));const check=document.querySelector('[aria-label="Import search Recovered foxes"]');assert.ok(check);assert.equal(check.checked,false);check.click();flushSync();click('Confirm selected import');await settle();assert.deepEqual(calls.find(c=>c.command==='legacy_confirm').args,{digest:'a'.repeat(64),searchIds:['legacy-1'],poolIds:[]});assert.match(document.body.textContent,/Imported 1 searches/);}finally{await unmount(app);window.happyDOM.abort();}
});

test('preferences controls send only non-secret non-directory preferences and show acknowledged persistence',async()=>{
 let value={filename_template:'{source}/{id}.{ext}',pool_filename_template:'{source}/{pool_name}/{id}.{ext}',concurrent_downloads:3,duplicates:'skip',gallery_scale:1,cache_limit_bytes:536870912,restore_session:false,save_metadata:false,start_muted:true};let saved;
 const bridge=new BrowserBridge(async(command,args)=>{if(command==='saved_searches'||command==='pinned_pools')return [];if(command==='preferences')return structuredClone(value);if(command==='save_preferences'){saved=structuredClone(args.preferences);value=saved;return structuredClone(value);}throw Error(command);});
 const app=mount(Component,{target:document.body,props:{bridge}});flushSync();await settle();try{
  click('Load preferences');await settle();input('Filename template','{source}/{artist}/{id}.{ext}');click('Save preferences');await settle();
  assert.equal(saved.filename_template,'{source}/{artist}/{id}.{ext}');assert.equal('download_directory'in saved,false);assert.equal('api_key'in saved,false);assert.match(document.body.textContent,/Preferences saved/);
 }finally{await unmount(app);window.happyDOM.abort();}
});

test('native campaign driver reaches both rendered phases with labeled in-memory provider double',async()=>{
 const oldSetAttribute=window.Element.prototype.setAttribute;window.Element.prototype.setAttribute=function(name,value){oldSetAttribute.call(this,name,value);if(this instanceof window.HTMLImageElement&&name==='src'){Object.defineProperties(this,{naturalWidth:{value:320,configurable:true},naturalHeight:{value:200,configurable:true},currentSrc:{value,configurable:true},complete:{value:true,configurable:true}});queueMicrotask(()=>this.dispatchEvent(new Event('load')));}};
 const imageSrc=Object.getOwnPropertyDescriptor(window.HTMLImageElement.prototype,'src');const videoSrc=Object.getOwnPropertyDescriptor(window.HTMLMediaElement.prototype,'src');const oldPlay=window.HTMLMediaElement.prototype.play;const oldPause=window.HTMLMediaElement.prototype.pause;const oldLoad=window.HTMLMediaElement.prototype.load;
 Object.defineProperty(window.HTMLImageElement.prototype,'src',{...imageSrc,set(value){imageSrc.set.call(this,value);Object.defineProperties(this,{naturalWidth:{value:320,configurable:true},naturalHeight:{value:200,configurable:true},currentSrc:{value,configurable:true},complete:{value:true,configurable:true}});queueMicrotask(()=>this.dispatchEvent(new Event('load')));}});
 Object.defineProperty(window.HTMLMediaElement.prototype,'src',{...videoSrc,set(value){videoSrc.set.call(this,value);Object.defineProperties(this,{videoWidth:{value:320,configurable:true},videoHeight:{value:200,configurable:true},duration:{value:2,configurable:true},paused:{value:true,writable:true,configurable:true},currentTime:{value:0,writable:true,configurable:true}});queueMicrotask(()=>this.dispatchEvent(new Event('loadeddata')));}});
 window.HTMLMediaElement.prototype.play=async function(){this.paused=false;this.currentTime=.5;this.dispatchEvent(new Event('play'));};window.HTMLMediaElement.prototype.pause=function(){this.paused=true;};window.HTMLMediaElement.prototype.load=function(){};
 const {runBrowseCampaign}=await import('../src/lib/browse-campaign.ts');let searches=[],pins=[],jobs=[];let resumes=0;let preferences={filename_template:'{source}/{artist}/{id}.{ext}',pool_filename_template:'{source}/{pool_name}/{id}.{ext}',concurrent_downloads:3,duplicates:'skip',gallery_scale:1,cache_limit_bytes:536870912,restore_session:false,save_metadata:false,start_muted:true};
 const bridge=new BrowserBridge(async(command,args)=>{
  if(command==='choose_download_directory')return true;if(command==='download_jobs')return structuredClone(jobs);if(command==='queue_download'){const id=jobs.length+1;jobs.push({id,post_id:args.id,state:'Paused',bytes:0,relative_path:'e621/local_fixture/100.png',error:null});return id;}if(command==='download_control'){jobs.find(j=>j.id===args.id).bytes=100;jobs.find(j=>j.id===args.id).state=args.verb==='resume'?(++resumes===1?'Running':'Completed'):args.verb==='pause'?'Paused':'Cancelled';return;}
  if(command==='legacy_preview')return {digest:'a'.repeat(64),searches:[{id:'legacy-fixture',name:'Migrated fixture search',query:{include:['fox'],exclude:[],ratings:{only:['safe']},artist:null,sort:'newest'}}],pins:[],warnings:[],queue:'Absent'};if(command==='legacy_confirm'){searches=[...searches,{id:'legacy-fixture',name:'Migrated fixture search',query:{include:['fox'],exclude:[],ratings:{only:['safe']},artist:null,sort:'newest'}}];return {imported_searches:1,imported_pins:0,skipped_searches:0,skipped_pins:0};}
  if(command==='prepare_media')return {id:args.id,kind:Number(args.id)>=104?'video':'image',title:'Local fixture',src:'http://127.0.0.1:43123/'+'a'.repeat(64)+'/media/'+('b'.repeat(62)+args.id.slice(-2))};
  if(command==='preferences')return structuredClone(preferences);if(command==='save_preferences'){preferences=structuredClone(args.preferences);return preferences;}
  if(command==='saved_searches')return structuredClone(searches);if(command==='pinned_pools')return structuredClone(pins);
  if(command==='save_search'){searches=[args.search];return;}if(command==='delete_search'){searches=searches.filter(s=>s.id!==args.id);return;}
  if(command==='set_pin'){pins=args.pinned?[{source:'e621',id:String(args.id)}]:[];return;}
  if(command==='suggestions')return [{name:'fox',post_count:12,category:'general'}];
  if(command==='search'||command==='favorites')return {items:args.page===2?[]:Array.from({length:50},(_,i)=>({key:{source:'e621',id:String(100+i)},title:'Post '+i,artists:[],tags:{},description:'fixture',rating:'safe',score:1,preview_url:'https://static1.e621.net/data/aa/bb/aabb0000000000000000000000000000.png'})),raw_count:args.page===2?0:50,next_page:args.page===2?null:2};
  if(command==='pool_page')return {pool:{key:{source:'e621',id:'3'},name:'Native fixture pool',post_ids:['10','2','30']},entries:[{id:'10',position:0,post:null},{id:'2',position:1,post:{title:'Post2'}},{id:'30',position:2,post:null}],total:3,next_offset:null};
  throw Error('unexpected command '+command);
 });
 for(const phase of [1,2]){const app=mount(Component,{target:document.body,props:{bridge}});flushSync();await settle();try{const receipt=await runBrowseCampaign(phase);assert.equal(receipt.ok,true);assert.equal(receipt.checks.length,phase===1?19:6);}finally{await unmount(app);}}
 assert.deepEqual(searches,[]);assert.deepEqual(pins,[]);window.Element.prototype.setAttribute=oldSetAttribute;Object.defineProperty(window.HTMLImageElement.prototype,'src',imageSrc);Object.defineProperty(window.HTMLMediaElement.prototype,'src',videoSrc);window.HTMLMediaElement.prototype.play=oldPlay;window.HTMLMediaElement.prototype.pause=oldPause;window.HTMLMediaElement.prototype.load=oldLoad;window.happyDOM.abort();
});
