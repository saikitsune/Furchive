import {tick} from 'svelte';
/** Operates rendered product controls, not the bridge or domain directly. Self-test
 * caller enables this only through native compile-time guarded test commands. */
export async function runBrowseCampaign(phase:number){
 const checks:string[]=[];
 const wait=async(predicate:()=>boolean)=>{const end=performance.now()+7000;while(!predicate()){if(performance.now()>end)throw Error('native browse condition timed out');await new Promise(r=>setTimeout(r,20));await tick();}};
 const button=(name:string)=>{const b=[...document.querySelectorAll<HTMLButtonElement>('button')].find(b=>b.textContent?.trim()===name);if(!b||b.disabled)throw Error('missing or disabled '+name);b.click();};
 const input=(label:string,value:string)=>{const e=document.querySelector<HTMLInputElement>(`[aria-label="${label}"]`);if(!e)throw Error('missing '+label);e.value=value;e.dispatchEvent(new Event('input',{bubbles:true}));};
 const idle=()=>document.querySelector('[aria-label="Furchive artwork browser"] [aria-busy]')?.getAttribute('aria-busy')==='false';
 await wait(()=>document.querySelector('[aria-label="Include tags"]')!==null);await tick();
 if(phase===1){
  input('Include tags','fo');await wait(()=>[...document.querySelectorAll('button')].some(b=>b.textContent?.trim()==='fox 12'));button('fox 12');await tick();checks.push('autocomplete');
  input('Exclude tags','comic');await tick();button('Add exclude');await tick();button('Search');await wait(()=>idle()&&document.querySelectorAll('[data-post-id]').length===50);checks.push('include-exclude-ratings');
  input('Include tags','wolf');await tick();button('Add include');await tick();button('Next page');await wait(()=>idle()&&document.body.textContent?.includes('page 2')===true);checks.push('pagination-submitted-query');
  // Remove the unsubmitted chip before the next explicit search intent.
  document.querySelector<HTMLButtonElement>('[aria-label="Remove include wolf"]')?.click();await tick();
  input('Favorites username','fixture_user');await tick();button('Browse favorites');await wait(()=>idle()&&document.body.textContent?.includes('Favorites · page 1')===true);checks.push('favorites');
  input('Pool ID','3');await tick();button('Open pool');await wait(()=>idle()&&document.body.textContent?.includes('Native fixture pool')===true);
  const rows=[...document.querySelectorAll<HTMLElement>('[data-post-id]')];if(rows.map(r=>r.dataset.postId).join(',')!=='10,2,30'||rows.filter(r=>r.textContent?.includes('Unavailable or filtered')).length!==2)throw Error('native ordered pool projection mismatch');checks.push('ordered-pool-holes');
  button('Pin pool');await wait(()=>[...document.querySelectorAll('button')].some(b=>b.textContent?.trim()==='Unpin pool'));checks.push('pin-persist');
  input('Saved search name','Native fixture search');await tick();button('Save search');await wait(()=>document.querySelector('[aria-label="Delete Native fixture search"]')!==null);checks.push('saved-search-persist');
 }else if(phase===2){
  await wait(()=>document.querySelector('[aria-label="Delete Native fixture search"]')!==null&&[...document.querySelectorAll('button')].some(b=>b.textContent?.trim()==='Pool 3'));checks.push('restart-state');
  document.querySelector<HTMLButtonElement>('[aria-label="Delete Native fixture search"]')!.click();await wait(()=>document.querySelector('[aria-label="Delete Native fixture search"]')===null);checks.push('saved-search-delete');
  button('Pool 3');await wait(()=>idle()&&[...document.querySelectorAll('button')].some(b=>b.textContent?.trim()==='Unpin pool'));button('Unpin pool');await wait(()=>![...document.querySelectorAll('button')].some(b=>b.textContent?.trim()==='Pool 3'));checks.push('unpin');
 }else throw Error('unknown native browse phase');
 if(document.querySelector('[role="alert"]'))throw Error('native product displayed an error: '+document.querySelector('[role="alert"]')?.textContent);
 return {schema:1,ok:true,phase,checks};
}
