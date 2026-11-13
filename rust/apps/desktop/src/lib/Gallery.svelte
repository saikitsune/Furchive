<script lang="ts">
 import {onMount,tick,untrack} from 'svelte';
 import {createCatalog,type MediaItem} from './media';
 import {gridWindow,navigateId} from './presentation';
 let {items,grants,selectedId=null,onselect=()=>{},onvisible=()=>{},scale=1}:{items:readonly MediaItem[];grants:readonly string[];selectedId?:string|null;onselect?:(id:string)=>void;onvisible?:(ids:string[])=>void;scale?:number}=$props();
 let catalog=$derived(createCatalog(items,grants));
 $effect(()=>{const id=selectedId;if(id&&catalog.some(item=>item.id===id))focused=id;});
 let width=$state(800),height=$state(600),top=$state(0),focused=$state<string|null>(null);
 let viewport:HTMLDivElement;
 let range=$derived(gridWindow(catalog.length,width,top,height,scale));
 let rows=$derived(catalog.slice(range.start,range.end));
 let visibleKey=$derived(rows.map(item=>item.id).join('\n'));
 $effect(()=>{const ids=visibleKey;untrack(()=>onvisible(ids?ids.split('\n'):[]));});
 let active=$derived(focused && catalog.some(i=>i.id===focused)?focused:selectedId && catalog.some(i=>i.id===selectedId)?selectedId:catalog[0]?.id??null);
 let visibleActive=$derived(rows.some(i=>i.id===active));
 onMount(()=>{
  const observer=new ResizeObserver(()=>{width=viewport.clientWidth||800;height=Math.min(900,viewport.clientHeight||600);});
  width=viewport.clientWidth||800;height=Math.min(900,viewport.clientHeight||600);observer.observe(viewport);return ()=>observer.disconnect();
 });
 async function key(event:KeyboardEvent){
  if(['ArrowLeft','ArrowRight','ArrowUp','ArrowDown','Home','End'].includes(event.key)){
   event.preventDefault();focused=navigateId(catalog.map(i=>i.id),active,event.key,range.columns);
   const index=catalog.findIndex(i=>i.id===focused),row=Math.floor(index/range.columns)*range.rowHeight;
   if(row<viewport.scrollTop)viewport.scrollTop=row;
   else if(row+range.rowHeight>viewport.scrollTop+height)viewport.scrollTop=row+range.rowHeight-height;
   top=viewport.scrollTop;await tick();
  }else if((event.key==='Enter'||event.key===' ')&&active){event.preventDefault();onselect(active);}
 }
 export function focus(){viewport?.focus();}
</script>
<div class="gallery" data-gallery data-selected={active} data-start={range.start} data-end={range.end} data-columns={range.columns} data-row-height={range.rowHeight} bind:this={viewport} role="listbox" aria-label="Media gallery" tabindex="0" aria-activedescendant={visibleActive&&active?'tile-'+active:undefined} onkeydown={key} onscroll={()=>top=viewport.scrollTop}>
 <div style:height={range.height+'px'} class="spacer">
  <div class="tiles" style:top={range.top+'px'} style:grid-template-columns={'repeat('+range.columns+',minmax(0,1fr))'}>
   {#each rows as item(item.id)}
    <div id={'tile-'+item.id} role="option" aria-selected={active===item.id} data-gallery-tile data-id={item.id} class:chosen={active===item.id} style:height={range.rowHeight-8+'px'}>
     <button tabindex="-1" onclick={()=>{focused=item.id;onselect(item.id)}} aria-label={'Open '+item.title}>
      {#if item.thumbnail || item.kind==='image'}<img src={item.thumbnail??item.src} alt="" style:height={116*scale+'px'} loading="lazy" decoding="async">{:else}<span class="video-badge">VIDEO</span>{/if}
      <b>{item.title}</b><small>{item.kind}</small>
     </button>
    </div>
   {/each}
  </div>
 </div>
 {#if !catalog.length}<p>No media supplied.</p>{/if}
</div>
<style>
 .gallery{height:min(65vh,600px);min-height:200px;overflow:auto;position:relative;border:1px solid #394857;border-radius:8px;outline-offset:3px;overflow-anchor:none}
 .spacer{position:relative}.tiles{position:absolute;display:grid;width:100%;gap:8px;padding:4px}
 [role=option]{height:176px;min-width:0;border:2px solid transparent;border-radius:6px;overflow:hidden}.chosen{border-color:#edbc87}
 button{width:100%;height:100%;padding:7px;display:flex;flex-direction:column;gap:5px;text-align:left;background:#202b36;color:#e5e9ed;border:0;cursor:pointer}
 img{width:100%;height:116px;object-fit:contain;background:#12181e}b{overflow:hidden;text-overflow:ellipsis;white-space:nowrap;max-width:100%}.video-badge{height:116px;display:grid;place-items:center}
</style>
