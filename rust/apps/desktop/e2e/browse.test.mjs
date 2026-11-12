import test from 'node:test';
import assert from 'node:assert/strict';
import {readFile,writeFile,mkdir} from 'node:fs/promises';
import {fileURLToPath,pathToFileURL} from 'node:url';
import {Window} from 'happy-dom';
const window=new Window({url:'http://localhost'});
for(const key of ['window','document','navigator','HTMLElement','Element','Node','Text','Comment','Event','MouseEvent','KeyboardEvent','CustomEvent','MutationObserver','getComputedStyle'])Object.defineProperty(globalThis,key,{value:key==='getComputedStyle'?window.getComputedStyle.bind(window):window[key],configurable:true});
const {compile}=await import('svelte/compiler');const {mount,unmount,flushSync}=await import('svelte');
const output=new URL('../../../test-artifacts/browser-components/',import.meta.url);await mkdir(output,{recursive:true});
let source=compile(await readFile(new URL('../src/lib/BrowsePanel.svelte',import.meta.url),'utf8'),{filename:'BrowsePanel.svelte',generate:'client',dev:true}).js.code;
source=source.replace(/(?:from|import) (['"])(svelte[^'"]*)\1/g,(all,q,spec)=>all.replace(spec,import.meta.resolve(spec))).replace(/from (['"])(\.\/[^'"]*)\1/g,(_,q,spec)=>`from '${new URL('../src/lib/'+spec.slice(2)+'.ts',import.meta.url)}'`);
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

test('preferences controls send only non-secret non-directory preferences and show acknowledged persistence',async()=>{
 let value={filename_template:'{source}/{id}.{ext}',pool_filename_template:'{source}/{pool_name}/{id}.{ext}',concurrent_downloads:3,duplicates:'skip',gallery_scale:1,cache_limit_bytes:536870912,restore_session:false,save_metadata:false,start_muted:true};let saved;
 const bridge=new BrowserBridge(async(command,args)=>{if(command==='saved_searches'||command==='pinned_pools')return [];if(command==='preferences')return structuredClone(value);if(command==='save_preferences'){saved=structuredClone(args.preferences);value=saved;return structuredClone(value);}throw Error(command);});
 const app=mount(Component,{target:document.body,props:{bridge}});flushSync();await settle();try{
  click('Load preferences');await settle();input('Filename template','{source}/{artist}/{id}.{ext}');click('Save preferences');await settle();
  assert.equal(saved.filename_template,'{source}/{artist}/{id}.{ext}');assert.equal('download_directory'in saved,false);assert.equal('api_key'in saved,false);assert.match(document.body.textContent,/Preferences saved/);
 }finally{await unmount(app);window.happyDOM.abort();}
});

test('native campaign driver reaches both rendered phases with labeled in-memory provider double',async()=>{
 const {runBrowseCampaign}=await import('../src/lib/browse-campaign.ts');let searches=[],pins=[];let preferences={filename_template:'{source}/{artist}/{id}.{ext}',pool_filename_template:'{source}/{pool_name}/{id}.{ext}',concurrent_downloads:3,duplicates:'skip',gallery_scale:1,cache_limit_bytes:536870912,restore_session:false,save_metadata:false,start_muted:true};
 const bridge=new BrowserBridge(async(command,args)=>{
  if(command==='preferences')return structuredClone(preferences);if(command==='save_preferences'){preferences=structuredClone(args.preferences);return preferences;}
  if(command==='saved_searches')return structuredClone(searches);if(command==='pinned_pools')return structuredClone(pins);
  if(command==='save_search'){searches=[args.search];return;}if(command==='delete_search'){searches=[];return;}
  if(command==='set_pin'){pins=args.pinned?[{source:'e621',id:String(args.id)}]:[];return;}
  if(command==='suggestions')return [{name:'fox',post_count:12,category:'general'}];
  if(command==='search'||command==='favorites')return {items:args.page===2?[]:Array.from({length:50},(_,i)=>({key:{source:'e621',id:String(100+i)},title:'Post '+i,artists:[],tags:{},description:'fixture',rating:'safe',score:1})),raw_count:args.page===2?0:50,next_page:args.page===2?null:2};
  if(command==='pool_page')return {pool:{key:{source:'e621',id:'3'},name:'Native fixture pool',post_ids:['10','2','30']},entries:[{id:'10',position:0,post:null},{id:'2',position:1,post:{title:'Post2'}},{id:'30',position:2,post:null}],total:3,next_offset:null};
  throw Error('unexpected command '+command);
 });
 for(const phase of [1,2]){const app=mount(Component,{target:document.body,props:{bridge}});flushSync();await settle();try{const receipt=await runBrowseCampaign(phase);assert.equal(receipt.ok,true);assert.equal(receipt.checks.length,phase===1?8:4);}finally{await unmount(app);}}
 assert.deepEqual(searches,[]);assert.deepEqual(pins,[]);window.happyDOM.abort();
});
