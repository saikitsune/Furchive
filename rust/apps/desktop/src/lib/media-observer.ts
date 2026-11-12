import {tick} from 'svelte';
/** A previous ready node is not evidence that a newly selected node is ready. */
export async function settledSelection(id:string,previous:HTMLImageElement|HTMLVideoElement):Promise<HTMLImageElement|HTMLVideoElement>{
 const deadline=performance.now()+8000;
 while(true){
  await tick();
  const viewer=document.querySelector<HTMLElement>('[data-viewer]');
  const current=document.querySelector<HTMLImageElement|HTMLVideoElement>('[data-media]');
  if(viewer?.dataset.id===id&&viewer.dataset.status==='ready'&&current&&current!==previous)return current;
  if(performance.now()>deadline)throw Error('selected replacement media did not settle');
  await new Promise(r=>setTimeout(r,20));
 }
}
