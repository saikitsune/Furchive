import {tick} from 'svelte';
import {validateMediaEvidence,type MediaEvidence} from './media-evidence';
import manifest from '../../public/fixtures/viewer-manifest.json';

export type Checkpoint='gif'|'apng'|'webp';
export const campaignDiagnostics:Record<string,unknown>={stage:'starting',videos:[]};
function videoState(v:HTMLVideoElement){return {src:v.currentSrc,paused:v.paused,ended:v.ended,ready:v.readyState,network:v.networkState,muted:v.muted,defaultMuted:v.defaultMuted,speed:v.playbackRate,time:v.currentTime,duration:v.duration,width:v.videoWidth,error:v.error?.code,errorMessage:v.error?.message,status:viewer()?.dataset.status,message:document.querySelector('[data-viewer] .status')?.textContent,playbackMessage:document.querySelector('[data-viewer] [data-playback-message]')?.textContent};}
export type CaptureRequest={fixture:string;fixtureSha256:string;rect:{x:number;y:number;width:number;height:number};viewport:{width:number;height:number;dpr:number};frames:number;intervalMs:number};
const frame=()=>new Promise<void>(resolve=>requestAnimationFrame(()=>resolve()));
const painted=async()=>{await tick();await frame();await frame();};
function demand<T>(value:T|null|undefined,message:string):T{if(value===null||value===undefined||value===false)throw new Error(message);return value;}
async function until(predicate:()=>boolean,message:string,ms=8000){const start=performance.now();while(!predicate()){if(performance.now()-start>ms)throw new Error(message);await new Promise(r=>setTimeout(r,20));}}
const viewer=()=>document.querySelector<HTMLElement>('[data-viewer]');
const currentMedia=()=>demand(document.querySelector<HTMLImageElement|HTMLVideoElement>('[data-media]'),'missing current media');
const button=(label:string)=>demand(document.querySelector<HTMLButtonElement>(`button[aria-label="${label}"]`),'missing '+label);
const fixture=(name:string)=>demand([...document.querySelectorAll<HTMLButtonElement>('[data-fixture]')].find(x=>x.dataset.fixture===name),'missing fixture '+name);
const released=(media:HTMLImageElement|HTMLVideoElement)=>!media.hasAttribute('src')&&!media.isConnected&&(!(media instanceof HTMLVideoElement)||media.paused);
async function open(name:string,status='ready'){fixture(name).click();await until(()=>viewer()?.dataset.status===status,'media state '+name+' expected '+status);await painted();}
async function close(){button('Close viewer').click();await painted();demand(!viewer(),'viewer still open');}

/** Opt-in, fixed fixture-only campaign. No canvas snapshots and no host paths supplied by UI. */
export async function runMediaCampaign(capture:(checkpoint:Checkpoint,request:CaptureRequest)=>Promise<unknown>,initialStart:number):Promise<MediaEvidence> {
 await painted();
 const grid=demand(document.querySelector<HTMLElement>('[data-gallery]'),'missing reusable gallery');
 const initialMs=performance.now()-initialStart;
 const samples:MediaEvidence['gallery']['samples']=[];
 for(const top of [0,2000,20000,100000,200000,0]){
  const started=performance.now();grid.scrollTop=top;grid.dispatchEvent(new Event('scroll'));await painted();
  const start=Number(grid.dataset.start),end=Number(grid.dataset.end);
  samples.push({elapsedMs:performance.now()-started,mounted:grid.querySelectorAll('[data-gallery-tile]').length,start,end,scrollTop:grid.scrollTop});
 }
 grid.focus();grid.dispatchEvent(new KeyboardEvent('keydown',{key:'End',bubbles:true}));await painted();
 const selected=grid.dataset.selected??'';
 const focused=document.activeElement===grid;
 const images:MediaEvidence['images']=[];
 for(const [i,name]of ['viewer-large.png','viewer-corrupt.png','viewer-truncated.png','viewer-oversized.png'].entries()){
  await open(name,i?'error':'ready');const media=currentMedia(),status=viewer()?.dataset.status??'';await close();images.push({name,status,released:released(media)});
 }
 const videos:MediaEvidence['videos']=[];
 for(const name of ['vp8.webm','vp9.webm','h264.mp4']){
  campaignDiagnostics.stage='video '+name;
  await open(name);const video=currentMedia();demand(video instanceof HTMLVideoElement,'video element required');const v=video as HTMLVideoElement;
  campaignDiagnostics.beforePlay=videoState(v);
  button('Play video').click();
  try{await until(()=>!v.paused&&v.currentTime>0.1,'video did not play');}catch(e){campaignDiagnostics.failedVideo=videoState(v);throw e;}
  const played=!v.paused;
  const speed=demand(document.querySelector<HTMLSelectElement>('[aria-label="Playback speed"]'),'speed');speed.value='2';speed.dispatchEvent(new Event('change',{bubbles:true}));
  let seeked=false;v.addEventListener('seeked',()=>seeked=true,{once:true});
  const seek=demand(document.querySelector<HTMLInputElement>('[aria-label="Seek video"]'),'seek');seek.value='0.5';seek.dispatchEvent(new Event('input',{bubbles:true}));
  await until(()=>seeked&&v.currentTime>=0.45,'video seek did not complete');
  const row={name,played,seeked,speed:v.playbackRate,muted:v.muted,time:v.currentTime,released:false};
  campaignDiagnostics.afterSeek=videoState(v);await close();row.released=released(v);videos.push(row);campaignDiagnostics.videos=[...videos];
 }
 const lifecycle:MediaEvidence['lifecycle']=[];
 for(let cycle=0;cycle<20;cycle++){
  await open('h264.mp4');const video=currentMedia();
  fixture('viewer-large.png').click();await painted();const old=currentMedia();
  fixture('still.png').click();await until(()=>viewer()?.dataset.status==='ready','rapid navigation did not settle');
  const now=currentMedia();
  old.dispatchEvent(new Event('load'));old.dispatchEvent(new Event('error'));video.dispatchEvent(new Event('error'));await painted();
  const staleIgnored=viewer()?.dataset.id==='media-0'&&viewer()?.dataset.status==='ready'&&currentMedia()===now;
  await close();lifecycle.push({cycle,released:released(video)&&released(old)&&released(now),staleIgnored,closed:!viewer(),remaining:document.querySelectorAll('[data-media]').length});
 }
 const animationCheckpoints:string[]=[];
 for(const checkpoint of ['gif','apng','webp'] as const){
  const name='viewer-animation.'+checkpoint;await open(name);const image=currentMedia();const rect=image.getBoundingClientRect();
  const request:CaptureRequest={fixture:name,fixtureSha256:demand(manifest.files.find(x=>x.name===name),'fixture hash').sha256,rect:{x:rect.x,y:rect.y,width:rect.width,height:rect.height},viewport:{width:innerWidth,height:innerHeight,dpr:devicePixelRatio},frames:12,intervalMs:80};
  await capture(checkpoint,request);animationCheckpoints.push(checkpoint);await close();
 }
 const result:MediaEvidence={schema:1,ok:true,engine:navigator.userAgent,dataset:{total:10000,unique:10000,rowHeight:184},gallery:{initialMs,samples,selected,focused},images,videos,lifecycle,animationCheckpoints};
 campaignDiagnostics.runtime=result;
 validateMediaEvidence(result);return result;
}
