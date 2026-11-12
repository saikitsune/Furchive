<script lang="ts">
 import {onMount} from 'svelte';
 import {type BrowserBridge,type Query,type SearchPage,type PoolPage,type SavedSearch,type Suggestion} from './browser-bridge';
 let {bridge}:{bridge:BrowserBridge}=$props();
 let include=$state<string[]>([]),exclude=$state<string[]>([]),includeInput=$state(''),excludeInput=$state('');
 let safe=$state(true),questionable=$state(false),explicit=$state(false),sort=$state<Query['sort']>('newest');
 let username=$state(''),poolId=$state(''),name=$state(''),saved=$state<SavedSearch[]>([]),pins=$state<{source:string;id:string}[]>([]),suggestions=$state<Suggestion[]>([]);
 let result=$state<SearchPage|null>(null),pool=$state<PoolPage|null>(null),error=$state(''),busy=$state(false),page=$state(1),mode=$state<'search'|'favorites'|'pool'>('search');
 let generation=0,suggestionGeneration=0;
 let submitted:{query:Query;username:string;poolId:string}|null=null;
 const query=():Query=>({include:[...include],exclude:[...exclude],ratings:{only:[...(safe?['safe' as const]:[]),...(questionable?['questionable' as const]:[]),...(explicit?['explicit' as const]:[])]},artist:null,sort});
 onMount(()=>{void refresh();return ()=>{generation++;suggestionGeneration++;};});
 async function refresh(){try{[saved,pins]=await Promise.all([bridge.searches(),bridge.pins()]);}catch(e){error=String(e);}}
 function chip(which:'include'|'exclude',value:string){
  const token=value.trim();if(!token||/\s/.test(token)||token.startsWith('-')){error='Enter one unsigned tag at a time.';return;}
  if(which==='include'){if(!include.includes(token))include=[...include,token];includeInput='';}else{if(!exclude.includes(token))exclude=[...exclude,token];excludeInput='';}suggestions=[];suggestionGeneration++;
 }
 async function complete(){const id=++suggestionGeneration;const prefix=includeInput.trim();if(prefix.length<2){suggestions=[];return;}try{const rows=await bridge.suggestions(prefix);if(id===suggestionGeneration)suggestions=rows;}catch(e){if(id===suggestionGeneration)error=String(e);}}
 async function run(nextMode:typeof mode,next=1,offset=0,paginate=false){const id=++generation;busy=true;error='';try{
  const intent=paginate&&submitted?submitted:{query:query(),username,poolId};
  const q=intent.query;if(q.ratings!=='any'&&!q.ratings.only.length)throw Error('Select at least one rating.');
  if(nextMode==='pool'){const value=Number(intent.poolId);if(!Number.isSafeInteger(value)||value<1)throw Error('Enter a positive pool ID.');const p=await bridge.pool(value,offset,q.ratings);if(id!==generation)return;pool=p;result=null;}
  else{const p=nextMode==='favorites'?await bridge.favorites(q,intent.username,next):await bridge.search(q,next);if(id!==generation)return;result=p;pool=null;}
  submitted=intent;mode=nextMode;page=next;
 }catch(e){if(id===generation)error=String(e);}finally{if(id===generation)busy=false;}}
 async function save(){error='';try{const value=name.trim();if(!value)throw Error('Name this search first.');await bridge.save({id:crypto.randomUUID(),name:value,query:query()});name='';await refresh();}catch(e){error=String(e);}}
 async function remove(id:string){try{await bridge.delete(id);await refresh();}catch(e){error=String(e);}}
 async function pin(){if(!pool)return;const id=Number(pool.pool.key.id);try{await bridge.pin(id,!pins.some(p=>p.id===String(id)));await refresh();}catch(e){error=String(e);}}
 function restore(s:SavedSearch){include=[...s.query.include];exclude=[...s.query.exclude];sort=s.query.sort;safe=s.query.ratings==='any'||s.query.ratings.only.includes('safe');questionable=s.query.ratings==='any'||s.query.ratings.only.includes('questionable');explicit=s.query.ratings==='any'||s.query.ratings.only.includes('explicit');}
</script>
<section class="browser" aria-label="Furchive artwork browser">
 <header><strong>Furchive</strong><small>RUST ALPHA · e621</small><span>Metadata browser — media/download integration in progress</span></header>
 <div class="layout">
  <aside>
   <h2>Search</h2>
   <label>Include tags<input aria-label="Include tags" bind:value={includeInput} oninput={complete} onkeydown={e=>{if(e.key==='Enter')chip('include',includeInput)}}></label><button onclick={()=>chip('include',includeInput)}>Add include</button>
   <div class="suggestions">{#each suggestions as s}<button onclick={()=>chip('include',s.name)}>{s.name} <small>{s.post_count}</small></button>{/each}</div>
   <div class="chips">{#each include as tag}<button aria-label={'Remove include '+tag} onclick={()=>include=include.filter(t=>t!==tag)}>{tag} ×</button>{/each}</div>
   <label>Exclude tags<input aria-label="Exclude tags" bind:value={excludeInput} onkeydown={e=>{if(e.key==='Enter')chip('exclude',excludeInput)}}></label><button onclick={()=>chip('exclude',excludeInput)}>Add exclude</button>
   <div class="chips excludes">{#each exclude as tag}<button aria-label={'Remove exclude '+tag} onclick={()=>exclude=exclude.filter(t=>t!==tag)}>{tag} ×</button>{/each}</div>
   <fieldset><legend>Ratings</legend><label><input type="checkbox" bind:checked={safe}>Safe</label><label><input type="checkbox" bind:checked={questionable}>Questionable</label><label><input type="checkbox" bind:checked={explicit}>Explicit</label></fieldset>
   <label>Order<select aria-label="Sort order" bind:value={sort}><option value="newest">Newest</option><option value="oldest">Oldest</option><option value="score">Score</option><option value="favorites">Favorites count</option><option value="random">Random</option></select></label>
   <button class="primary" disabled={busy} onclick={()=>run('search')}>Search</button>
   <label>Favorites username<input aria-label="Favorites username" bind:value={username}></label><button disabled={busy} onclick={()=>run('favorites')}>Browse favorites</button>
   <label>Pool ID<input aria-label="Pool ID" inputmode="numeric" bind:value={poolId}></label><button disabled={busy} onclick={()=>run('pool')}>Open pool</button>
   <h2>Saved searches</h2><label>Search name<input aria-label="Saved search name" bind:value={name}></label><button onclick={save}>Save search</button>
   {#each saved as s}<div class="saved"><button onclick={()=>restore(s)}>{s.name}</button><button aria-label={'Delete '+s.name} onclick={()=>remove(s.id)}>×</button></div>{/each}
   <h2>Pinned pools</h2>{#each pins as p}<button onclick={()=>{poolId=p.id;void run('pool');}}>Pool {p.id}</button>{/each}
  </aside>
  <div class="results" aria-busy={busy}>
   {#if error}<p role="alert">{error}</p>{/if}
   {#if busy}<p role="status">Loading…</p>{/if}
   {#if pool}<h1>{pool.pool.name}</h1><p>{pool.total} ordered members · hidden/unavailable members retain their original position.</p><button onclick={pin}>{pins.some(p=>p.id===pool?.pool.key.id)?'Unpin pool':'Pin pool'}</button>
    <ol class="posts">{#each pool.entries as e}<li value={e.position+1} data-post-id={e.id}><b>#{e.position+1} · {e.id}</b><p>{e.post?.title??'Unavailable or filtered'}</p></li>{/each}</ol>
    {#if pool.next_offset!==null}<button disabled={busy} onclick={()=>run('pool',1,pool?.next_offset??0,true)}>Next pool page</button>{/if}
   {:else if result}<h1>{mode==='favorites'?'Favorites':'Search results'} · page {page}</h1><p>{result.items.length} visible posts from {result.raw_count} upstream records. Account filters remain respected.</p>
    <ol class="posts">{#each result.items as p}<li data-post-id={p.key.id}><b>{p.title}</b><p>{p.artists.join(', ')||'No artist supplied'} · {p.rating} · score {p.score}</p><details><summary>Tags &amp; description</summary><p>{p.description}</p>{#each Object.entries(p.tags) as [category,tags]}<p><b>{category}:</b> {tags.join(' ')}</p>{/each}</details></li>{/each}</ol>
    <nav><button disabled={busy||page===1} onclick={()=>run(mode,page-1,0,true)}>Previous page</button><button disabled={busy||result.next_page===null} onclick={()=>run(mode,result?.next_page??1,0,true)}>Next page</button></nav>
   {:else}<h1>Your collection starts with a search.</h1><p>No network request is made until you search, browse favorites, request suggestions, or open a pool. Safe-only is the initial rating selection. This alpha currently shows metadata; it is not a finished media browser.</p>{/if}
  </div>
 </div>
</section>
<style>
 .browser{color:#e5e9ed}header{gap:16px;flex-wrap:wrap;height:auto;min-height:70px}header span{color:#98a7b6;font-size:12px}.layout{display:grid;grid-template-columns:280px minmax(0,1fr)}aside{padding:20px;display:flex;flex-direction:column;gap:9px}h2{margin:15px 0 3px;font-size:15px}input,button,select{border-radius:5px;background:#202b36;color:#e5e9ed;border:1px solid #526271;padding:8px;font:inherit}input{min-width:0;width:100%}button{cursor:pointer}button:disabled{opacity:.5;cursor:wait}.primary{background:#b76b31;border-color:#ebad6b}.chips{display:flex;flex-wrap:wrap;gap:4px}.chips button{background:#254934}.excludes button{background:#57303b}fieldset{border:1px solid #394857;margin:0;padding:8px;display:flex;flex-wrap:wrap;gap:8px}fieldset label{display:flex;flex-direction:row;gap:4px;align-items:center;font-size:12px}fieldset input{width:auto}.results{padding:24px;min-width:0}.posts{list-style:none;padding:0;display:grid;grid-template-columns:repeat(auto-fill,minmax(230px,1fr));gap:12px}.posts li{background:#1c252e;border:1px solid #354656;border-radius:6px;padding:16px;overflow-wrap:anywhere}.posts p{margin:8px 0}.saved{display:flex;gap:4px}.saved button:first-child{flex:1}nav{display:flex;gap:12px}[role=alert]{color:#ffbdc9;border:1px solid #a86470;padding:12px}.suggestions{display:flex;flex-wrap:wrap;gap:4px}@media(max-width:800px){.layout{grid-template-columns:230px minmax(0,1fr)}.results{padding:12px}}
</style>
