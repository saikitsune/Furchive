import test from 'node:test';
import assert from 'node:assert/strict';
import {readFile,writeFile,mkdir,mkdtemp} from 'node:fs/promises';
import {fileURLToPath,pathToFileURL} from 'node:url';
import {Window} from 'happy-dom';
const window=new Window({url:'http://localhost'});
for(const key of ['window','document','navigator','HTMLElement','Element','Node','Text','Comment','Event','MouseEvent','KeyboardEvent','CustomEvent','MutationObserver','HTMLMediaElement','HTMLVideoElement','HTMLImageElement','getComputedStyle'])Object.defineProperty(globalThis,key,{value:key==='getComputedStyle'?window.getComputedStyle.bind(window):window[key],configurable:true});
globalThis.ResizeObserver=class{observe(){}disconnect(){}};
const {compile}=await import('svelte/compiler');const {mount,unmount,flushSync}=await import('svelte');
const outputRoot=new URL('../../../test-artifacts/',import.meta.url);await mkdir(outputRoot,{recursive:true});const output=pathToFileURL((await mkdtemp(fileURLToPath(new URL('browser-components-',outputRoot))))+'/');
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
function bridgeFor(rows,handleStep,calls){return new BrowserBridge(async(command,args)=>{calls.push({command,args});if(command==='saved_searches'||command==='pinned_pools')return [];if(command==='preferences')return prefs;if(command==='search'||command==='favorites')return {items:rows,raw_count:50,next_page:2};if(command==='prepare_media'){if(rows.find(p=>p.key.id===args.id)?.full_url===null)throw Error('media unavailable or filtered');return prepared(args.id);}if(command==='viewer_step')return handleStep(args);throw Error(command);});}
async function cleanup(app){document.querySelector('[aria-label="Close viewer"]')?.click();flushSync();await settle();await unmount(app);window.happyDOM.abort();}

for(const mode of ['search','favorites','pool'])for(const delta of [-1,1])for(const missing of [null,'https://evil.invalid/unsupported.svg'])test(`NYX-XPV-01 ${mode} ${delta<0?'previous':'next'} skips ${missing===null?'null':'noncanonical'} full_url at viewable edge`,async()=>{
 const calls=[];const rows=delta===1?[post('100'),post('101',false)]:[post('101',false),post('100')];
 rows.find(p=>p.key.id==='101').full_url=missing;
 const poolPage={pool:{key:{source:'e621',id:'3'},name:'Pool',post_ids:['101','100']},entries:rows.map((p,i)=>({id:p.key.id,position:50+i,post:p})),total:150,next_offset:100};
 const bridge=new BrowserBridge(async(command,args)=>{calls.push({command,args});if(command==='saved_searches'||command==='pinned_pools')return [];if(command==='preferences')return prefs;if(command==='search'||command==='favorites')return {items:rows,raw_count:50,next_page:2};if(command==='pool_page')return poolPage;if(command==='prepare_media'){if(args.id==='101')throw Error('media unavailable or filtered');return prepared(args.id);}if(command==='viewer_step')return {...receipt(),page:delta===1?3:1};throw Error(command);});
 const app=mount(Component,{target:document.body,props:{bridge}});flushSync();await settle();let observed;
 try{
 if(mode!=='pool'){if(mode==='favorites'){input('Favorites username','fixture_user');click('Browse favorites');}else click('Search');await settle();if(delta===-1){click('Next page');await settle();}}
 else{input('Pool ID','3');click('Open pool');await settle();click('Next pool page');await settle();}
 document.querySelector('[data-post-id="100"] button').click();await settle();document.querySelector(`[aria-label="${delta===1?'Next':'Previous'} media"]`).click();await settle();await settle();observed={mode,delta,id:document.querySelector('[data-viewer]')?.dataset.id,steps:calls.filter(x=>x.command==='viewer_step'),prepared:calls.filter(x=>x.command==='prepare_media')};console.log('OBSERVED',JSON.stringify(observed));
 }finally{await cleanup(app);}
 assert.equal(observed.id,'200');
});
