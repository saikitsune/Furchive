<script lang="ts">
  import {onMount} from 'svelte';
  import {invoke} from '@tauri-apps/api/core';
  import {visibleRange} from './lib/gallery.js';
  let qualifying=$state(false);
  let report=$state('Preparing native engine…');
  let top=$state(0);
  let viewport:HTMLDivElement;
  const items=Array.from({length:10000},(_,i)=>({id:i+1,title:`Qualification image ${i+1}`}));
  let range=$derived(visibleRange(items.length,5,top,600,220,2));
  let rows=$derived(items.slice(range.start,range.end));
  let selected=$state('/fixtures/still.png');
  let mediaBase=$state('');
  function mediaUrl(value:string){return /\.(webm|mp4)$/.test(value)?mediaBase+value.split('/').pop():value;}
  const media=['still.jpg','still.png','animation.gif','animation.apng','still.webp','vp8.webm','vp9.webm','h264.mp4'];
  async function qualify() {
    const results:Record<string,unknown>={engine:navigator.userAgent,images:[],videos:[],gallery:{}};
    const imageResults:unknown[]=[];
    for(const name of media.slice(0,5)) {
      const image=new Image(); image.src='/fixtures/'+name;
      const result=await new Promise(resolve=>{
        const timer=setTimeout(()=>resolve({name,ok:false,error:'timeout'}),8000);
        image.onload=()=>{clearTimeout(timer);resolve({name,ok:image.naturalWidth>0,width:image.naturalWidth,height:image.naturalHeight});};
        image.onerror=()=>{clearTimeout(timer);resolve({name,ok:false,error:'decode'});};
      }); imageResults.push(result);
    }
    results.images=imageResults;
    const videos:unknown[]=[];
    for(const name of media.slice(5)) {
      const video=document.createElement('video'); video.defaultMuted=true;video.setAttribute('muted','');video.muted=true; video.preload='auto'; document.body.append(video);
      const result=await new Promise(resolve=>{
        const timer=setTimeout(()=>resolve({name,ok:false,error:'timeout'}),12000);
        video.onerror=()=>{clearTimeout(timer);resolve({name,ok:false,error:video.error?.message,code:video.error?.code,ready:video.readyState,network:video.networkState,time:video.currentTime,muted:video.muted,defaultMuted:video.defaultMuted,src:video.currentSrc});};
        video.onloadeddata=async()=>{
          video.onloadeddata=null;
          try {
            await video.play();
            await new Promise<void>((resolve,reject)=>{
              const timer=setTimeout(()=>reject(new Error('seek timeout')),4000);
              video.onseeked=()=>{clearTimeout(timer);resolve();};video.currentTime=0.5;
            });
            await new Promise(r=>setTimeout(r,250));
            clearTimeout(timer);resolve({name,ok:video.videoWidth>0&&!video.paused,time:video.currentTime,duration:video.duration,width:video.videoWidth,ready:video.readyState,played:true,seeked:true});
          }catch(e){clearTimeout(timer);resolve({name,ok:false,error:String(e)});}
        }; video.src=mediaBase+name;
      }); videos.push(result); video.pause();video.removeAttribute('src');video.load();video.remove();
    }
    results.videos=videos;
    viewport.scrollTop=20000;
    await new Promise(r=>setTimeout(r,120));
    results.gallery={total:items.length,mounted:document.querySelectorAll('[data-tile]').length,scrollTop:viewport.scrollTop,start:range.start,end:range.end};
    report=JSON.stringify(results,null,2);
    await invoke('qualification_result',{result:results});
  }
  onMount(async()=>{
    try {mediaBase=await invoke<string>('qualification_media_base');
      qualifying=await invoke<boolean>('qualification_mode');
      report=qualifying?'Running native WebKit media qualification…':'Native shell ready. Product integration in progress.';
      if(qualifying) await qualify();
    }catch(e){report=String(e);}
  });
</script>
<svelte:head><title>Furchive · Rust Alpha</title></svelte:head>
<header><div><span class="mark">F</span><strong>Furchive</strong><small>RUST ALPHA</small></div><span>Local-first · e621 archive</span></header>
<main>
<aside><h2>Your collection, organized.</h2><p>Search, pools and downloads are being connected to the Rust engine.</p><label>Media qualification<select bind:value={selected}>{#each media as name}<option value={'/fixtures/'+name}>{name}</option>{/each}</select></label><p class="muted">This is an engineering qualification screen, not a finished product.</p><pre>{report}</pre></aside>
<section><div class="toolbar"><h1>Native media & gallery qualification</h1><span>{qualifying?'AUTOMATED TEST':'DEVELOPMENT'}</span></div><div class="viewport" bind:this={viewport} onscroll={()=>top=viewport.scrollTop}>
<div style="height:440000px;position:relative"><div class="grid" style:top={(range.start/5)*220+'px'}>
{#each rows as item(item.id)}<button data-tile><img src="/fixtures/still.webp" alt="Generated abstract test fixture"><b>{item.title}</b><small>Owned test fixture · no remote artwork</small></button>{/each}
</div></div></div></section>
<aside class="preview"><h2>Viewer</h2>{#if /\.(webm|mp4)$/.test(selected)}<video src={mediaUrl(selected)} controls muted loop playsinline></video>{:else}<img src={selected} alt="Generated media fixture">{/if}<p>JPEG · PNG · GIF · APNG · WebP · WebM · MP4</p></aside>
</main>
