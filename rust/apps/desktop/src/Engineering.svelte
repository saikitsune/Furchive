<script lang="ts">
 import {onMount,tick} from 'svelte';
 import {invoke} from '@tauri-apps/api/core';
 import Gallery from './lib/Gallery.svelte';
 import Viewer from './lib/Viewer.svelte';
 import {createCatalog,type MediaItem} from './lib/media';
 import {runMediaCampaign,campaignDiagnostics} from './lib/media-campaign';
 const initialStart=performance.now();
 let {nativeTest=false}:{nativeTest?:boolean}=$props();
 const imageNames=['still.png','still.jpg','animation.gif','animation.apng','still.webp','viewer-animation.gif','viewer-animation.apng','viewer-animation.webp','viewer-large.png','viewer-oversized.png','viewer-corrupt.png','viewer-truncated.png'];
 const videoNames=['vp8.webm','vp9.webm','h264.mp4'];
 let mediaBase=$state(''),note=$state('Only generated fixtures. No accounts, remote art, restoration or product integration.');
 let selected=$state<string|null>(null),gallery:Gallery;
 const imageGrants=imageNames.map(name=>'/fixtures/'+name);
 let grants=$derived([...imageGrants,...(mediaBase?videoNames.map(name=>mediaBase+name):[])]);
 let fixtures=$derived<readonly MediaItem[]>(createCatalog([
  ...imageNames.map((name,i)=>({id:'media-'+i,kind:'image' as const,src:'/fixtures/'+name,title:name})),
  ...(mediaBase?videoNames.map((name,i)=>({id:'video-'+i,kind:'video' as const,src:mediaBase+name,title:name,thumbnail:'/fixtures/still.webp'})):[])
 ],grants));
 const collectionGrants=['/fixtures/still.webp'];
 const items:readonly MediaItem[]=createCatalog(Array.from({length:10000},(_,i)=>({id:'fixture-'+i,kind:'image' as const,src:'/fixtures/still.webp',title:`Generated collection ${i+1}`})),collectionGrants);
 let viewerItems=$derived(selected?.startsWith('fixture-')?items:fixtures);
 async function close(){selected=null;await tick();gallery?.focus();}
 function choose(id:string){selected=id;}
 onMount(async()=>{
  try{mediaBase=await invoke<string>('qualification_media_base');}catch{note+=' Browser mode: video uses bundled fixtures, not the native capability transport.';mediaBase='/fixtures/';}
  if(nativeTest){
   try{await tick();const receipt=await runMediaCampaign(async(checkpoint,payload)=>invoke('media_test_checkpoint',{checkpoint,receipt:payload}),initialStart);await invoke('media_test_result',{result:receipt});}
   catch(error){note=String(error);await invoke('media_test_result',{result:{schema:1,ok:false,error:String(error),engine:navigator.userAgent,diagnostics:campaignDiagnostics}});}
  }
 });
</script>
<header><div><span class="mark">F</span><strong>Furchive</strong><small>ENGINEERING MODE</small></div><span>Reusable media components · generated fixtures only</span></header>
<div class="engineering">
 <h1>Gallery & media workbench</h1><p>{note}</p>
 <nav aria-label="Generated media fixtures">{#each fixtures as fixture(fixture.id)}<button data-fixture={fixture.title} onclick={()=>choose(fixture.id)}>{fixture.title}</button>{/each}</nav>
 <p>10,000 unique opaque IDs · arrow keys move selection · Home / End · Enter opens. Viewer: ← / → and Escape.</p>
 <Gallery {items} grants={collectionGrants} selectedId={selected} onselect={choose} bind:this={gallery}/>
 <Viewer items={viewerItems} {grants} selectedId={selected} onselect={choose} onclose={close}/>
</div>
<style>
 .engineering{max-width:1400px;margin:auto;padding:20px}nav{display:flex;flex-wrap:wrap;gap:7px;margin-bottom:16px}button{background:#253442;color:#f0e6da;border:1px solid #5a6a78;border-radius:5px;padding:7px 10px;cursor:pointer}h1{font-size:24px}
</style>
