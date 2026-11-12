<script lang="ts">
 import {onMount,untrack} from 'svelte';
 import {createCatalog,LoadScope,imageAllowed,releaseVideo,type MediaItem} from './media';
 import {clampTransform,fitScale,navigateId} from './presentation';
 let {items,grants,selectedId,onselect=()=>{},onclose=()=>{}}:{items:readonly MediaItem[];grants:readonly string[];selectedId:string|null;onselect?:(id:string)=>void;onclose?:()=>void}=$props();
 let catalog=$derived(createCatalog(items,grants));
 let item=$derived(catalog.find(i=>i.id===selectedId));
 let index=$derived(catalog.findIndex(i=>i.id===selectedId));
 let stage=$state<HTMLDivElement>(),dialog=$state<HTMLDivElement>();
 let element=$state<HTMLImageElement|HTMLVideoElement|null>(null);
 let status=$state<'loading'|'ready'|'error'>('loading'),message=$state('');
 let w=$state(1),h=$state(1),vw=$state(640),vh=$state(400);
 let fit=$state(true),scale=$state(1),x=$state(0),y=$state(0);
 let muted=$state(true),speed=$state(1),playing=$state(false),time=$state(0),duration=$state(0);
 let drag:{id:number;x:number;y:number;px:number;py:number}|null=null;
 const scope=new LoadScope();
 let transform=$derived(clampTransform(w,h,vw,vh,fit?fitScale(w,h,vw,vh):scale,x,y));
 $effect(()=>{
  const selected=item,container=stage;
  if(!selected||!container)return;
  return untrack(()=>{
   status='loading';message='';w=1;h=1;fit=true;scale=1;x=0;y=0;playing=false;time=0;duration=0;muted=true;speed=1;drag=null;
   const media=selected.kind==='video'?document.createElement('video'):document.createElement('img');
   let timer:ReturnType<typeof setTimeout>;
   const events: Array<[string,EventListener]>=[];
   const token=scope.begin(()=>{
    clearTimeout(timer);for(const [name,handler]of events)media.removeEventListener(name,handler);
    if(media instanceof HTMLVideoElement)releaseVideo(media);else media.removeAttribute('src');
    media.remove();if(element===media)element=null;
   });
   function on(name:string,handler:()=>void){events.push([name,handler]);media.addEventListener(name,handler);}
   let failed=false;
   function fail(reason:string){token.commit(()=>{failed=true;clearTimeout(timer);status='error';message=reason;media.style.visibility='hidden';if(media instanceof HTMLVideoElement)releaseVideo(media);else media.removeAttribute('src');});}
   function ready(){if(failed)return;token.commit(()=>{
    const width=media instanceof HTMLVideoElement?media.videoWidth:media.naturalWidth,height=media instanceof HTMLVideoElement?media.videoHeight:media.naturalHeight;
    if(!imageAllowed(width,height)){fail('Media refused: invalid dimensions or exceeds 32 megapixels / 16384 pixels per edge.');return;}
    clearTimeout(timer);w=width;h=height;status='ready';media.style.visibility='visible';
    if(media instanceof HTMLVideoElement){if(!Number.isFinite(media.duration)||media.duration<=0){fail('Media refused: invalid video duration.');return;}duration=media.duration;}
   });}
   media.style.cssText='position:absolute;left:50%;top:50%;max-width:none;max-height:none;visibility:hidden;transform-origin:center;user-select:none;pointer-events:none;';
   media.setAttribute('data-media',selected.kind);
   if(media instanceof HTMLVideoElement){media.preload='metadata';media.muted=true;media.playsInline=true;on('loadedmetadata',ready);on('timeupdate',()=>token.commit(()=>time=media.currentTime));on('play',()=>token.commit(()=>playing=true));on('pause',()=>token.commit(()=>playing=false));on('ended',()=>token.commit(()=>playing=false));}
   else{media.alt=selected.title;media.draggable=false;on('load',ready);}
   on('error',()=>fail('Media could not be decoded (corrupt, truncated or unsupported).'));
   element=media;container.append(media);timer=setTimeout(()=>fail('Media load timed out.'),8000);media.src=selected.src;dialog?.focus();
   return ()=>scope.close();
  });
 });
 $effect(()=>{const media=element,t=transform;if(media){media.style.width=w+'px';media.style.height=h+'px';media.style.transform=`translate(-50%,-50%) translate(${t.x}px,${t.y}px) scale(${t.scale})`;}});
 onMount(()=>{const observer=new ResizeObserver(()=>{if(stage){vw=stage.clientWidth||640;vh=stage.clientHeight||400;}});if(stage)observer.observe(stage);return ()=>{observer.disconnect();scope.close();};});
 // Stage is conditional: observe each new open as well as initial mount.
 $effect(()=>{if(!stage)return;const node=stage;vw=node.clientWidth||640;vh=node.clientHeight||400;const observer=new ResizeObserver(()=>{vw=node.clientWidth||640;vh=node.clientHeight||400;});observer.observe(node);return ()=>observer.disconnect();});
 function close(){scope.close();onclose();}
 function navigate(key:string){const id=navigateId(catalog.map(i=>i.id),selectedId,key,1);if(id&&id!==selectedId)onselect(id);}
 function key(event:KeyboardEvent){
  if(event.key==='Tab'&&dialog){
   const controls=[...dialog.querySelectorAll<HTMLElement>('button:not(:disabled),input:not(:disabled),select:not(:disabled),[tabindex="0"]')];
   const first=controls[0],last=controls.at(-1);
   if(first&&event.shiftKey&&(document.activeElement===first||document.activeElement===dialog)){event.preventDefault();last?.focus();}
   else if(first&&!event.shiftKey&&(document.activeElement===last||document.activeElement===dialog)){event.preventDefault();first.focus();}
   return;
  }
  if((event.target as HTMLElement).matches('input,select'))return;
  if(event.key==='Escape'){event.preventDefault();close();}
  if(event.key==='ArrowLeft'||event.key==='ArrowRight'){event.preventDefault();navigate(event.key);}
 }
 function zoom(factor:number){const current=transform;fit=false;scale=current.scale*factor;x=current.x;y=current.y;}
 function reset(){fit=true;x=0;y=0;}
 function pointerDown(event:PointerEvent){if(status!=='ready'||event.button!==0||!stage)return;stage.setPointerCapture(event.pointerId);drag={id:event.pointerId,x:event.clientX,y:event.clientY,px:transform.x,py:transform.y};}
 function pointerMove(event:PointerEvent){if(drag?.id!==event.pointerId)return;x=drag.px+event.clientX-drag.x;y=drag.py+event.clientY-drag.y;}
 function pointerEnd(){drag=null;}
 async function play(){const video=element;if(!(video instanceof HTMLVideoElement))return;if(!video.paused){video.pause();return;}try{await video.play();}catch{if(element===video){message='Playback was refused. Use Play again with a direct gesture.';}}}
 function changeMute(){muted=!muted;if(element instanceof HTMLVideoElement)element.muted=muted;}
 function changeSpeed(event:Event){speed=Number((event.target as HTMLSelectElement).value);if(element instanceof HTMLVideoElement)element.playbackRate=speed;}
 function seek(event:Event){const value=Number((event.target as HTMLInputElement).value);if(element instanceof HTMLVideoElement&&Number.isFinite(value))element.currentTime=Math.max(0,Math.min(duration,value));}
</script>
{#if item}
 <div class="veil">
 <div class="viewer" data-viewer data-id={item.id} data-status={status} data-scale={transform.scale} data-pan-x={transform.x} data-pan-y={transform.y} role="dialog" aria-modal="true" aria-label="Media viewer" tabindex="-1" bind:this={dialog} onkeydown={key}>
  <div class="bar"><h2>{item.title}</h2><button aria-label="Close viewer" onclick={close}>Close ×</button></div>
  <div class="bar controls"><button aria-label="Previous media" disabled={index<=0} onclick={()=>navigate('ArrowLeft')}>← Previous</button><span>{index+1} / {catalog.length}</span><button aria-label="Next media" disabled={index>=catalog.length-1} onclick={()=>navigate('ArrowRight')}>Next →</button></div>
  <div class="stage" data-stage bind:this={stage} role="img" aria-label="Media pan and zoom surface" onpointerdown={pointerDown} onpointermove={pointerMove} onpointerup={pointerEnd} onpointercancel={pointerEnd} onlostpointercapture={pointerEnd} onwheel={event=>{event.preventDefault();zoom(event.deltaY<0?1.2:1/1.2)}}></div>
  <p class="status" role="status">{status==='loading'?'Loading…':status==='error'?message:`${w} × ${h} · ${Math.round(transform.scale*100)}%${fit?' · fit':''}`}</p>
  <div class="bar controls"><button onclick={reset}>Fit / reset</button><button onclick={()=>{fit=false;scale=1;x=0;y=0}}>100%</button><button aria-label="Zoom out" onclick={()=>zoom(1/1.25)}>−</button><button aria-label="Zoom in" onclick={()=>zoom(1.25)}>+</button></div>
  {#if item.kind==='video'}
   <div class="bar controls"><button aria-label={playing?'Pause video':'Play video'} disabled={status!=='ready'} onclick={play}>{playing?'Pause':'Play'}</button><button aria-label={muted?'Unmute video':'Mute video'} onclick={changeMute}>{muted?'Muted':'Sound on'}</button><label>Speed<select aria-label="Playback speed" value={speed} onchange={changeSpeed}>{#each [0.25,0.5,1,1.5,2] as rate}<option value={rate}>{rate}×</option>{/each}</select></label><input aria-label="Seek video" type="range" min="0" max={duration||1} step="0.01" value={time} oninput={seek} disabled={status!=='ready'}><span>{time.toFixed(1)} / {duration.toFixed(1)}s</span></div>
   {#if message&&status!=='error'}<p role="status">{message}</p>{/if}
  {/if}
 </div>
 </div>
{/if}
<style>
 .veil{position:fixed;inset:0;z-index:20;background:#080c10e8;display:grid;place-items:center;padding:18px}.viewer{width:min(100%,1000px);max-height:96vh;overflow:auto;background:#17212b;border:1px solid #526474;border-radius:10px;padding:14px;outline-offset:3px}.bar{display:flex;align-items:center;gap:12px;justify-content:space-between}.controls{justify-content:center;flex-wrap:wrap;margin:8px 0}.stage{height:min(48vh,430px);min-height:160px;position:relative;overflow:hidden;background:#080d12;touch-action:none;cursor:grab}h2{font-size:17px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}button,select{background:#2b3b49;color:#f0ece6;border:1px solid #627583;border-radius:5px;padding:7px 11px;cursor:pointer}button:disabled{opacity:.45;cursor:default}.status{margin:8px 0;min-height:24px}label{flex-direction:row;align-items:center}input{max-width:260px;flex:1}
</style>
