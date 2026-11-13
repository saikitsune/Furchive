<script lang="ts">
 import {onMount,tick} from 'svelte';
 import Viewer from './Viewer.svelte';
 import Gallery from './Gallery.svelte';
 import {type MediaItem,createCatalog} from './media';
 import {type BrowserBridge,type Query,type SearchPage,type PoolPage,type SavedSearch,type Suggestion,type Preferences,type LegacyPreview,type DownloadJob,type Identity} from './browser-bridge';
 let {bridge}:{bridge:BrowserBridge}=$props();
 let include=$state<string[]>([]),exclude=$state<string[]>([]),includeInput=$state(''),excludeInput=$state('');
 let safe=$state(true),questionable=$state(false),explicit=$state(false),sort=$state<Query['sort']>('newest');
 let username=$state(''),poolId=$state(''),name=$state(''),saved=$state<SavedSearch[]>([]),pins=$state<{source:string;id:string}[]>([]),suggestions=$state<Suggestion[]>([]);
 let result=$state<SearchPage|null>(null),pool=$state<PoolPage|null>(null),error=$state(''),busy=$state(false),page=$state(1),mode=$state<'search'|'favorites'|'pool'>('search');
 let preferences=$state<Preferences|null>(null),applied=$state<Preferences|null>(null),preferenceNotice=$state('');
 let legacy=$state<LegacyPreview|null>(null),importSearches=$state<string[]>([]),importPools=$state<string[]>([]),importNotice=$state('');
 let prepared=$state<readonly MediaItem[]>([]),grants=$state<readonly string[]>([]),selectedId=$state<string|null>(null);
 let thumbnails=$state<Record<string,MediaItem>>({});
 let jobs=$state<DownloadJob[]>([]),downloadGranted=$state(false);
 let accountUser=$state(''),accountKey=$state(''),identity=$state<Identity|null>(null);
 let generation=0,suggestionGeneration=0,mediaGeneration=0,preferenceGeneration=0;
 const requestedPreviews=new Set<string>();
 const placeholder='/placeholder.svg';
 let visiblePosts=$derived(pool?pool.entries.flatMap(e=>e.post?[e.post]:[]):result?.items??[]);
 let galleryItems=$derived<readonly MediaItem[]>(visiblePosts.map(p=>({id:p.key.id,kind:'image',title:p.title,src:thumbnails[p.key.id]?.src??placeholder})));
 let galleryGrants=$derived([placeholder,...Object.values(thumbnails).map(p=>p.src)]);
 function visible(ids:string[]){const current=generation;void (async()=>{for(const id of ids){if(current!==generation)return;if(requestedPreviews.has(id)||!visiblePosts.find(p=>p.key.id===id)?.preview_url)continue;requestedPreviews.add(id);try{const media=await bridge.prepare(id,'preview');if(current===generation)thumbnails={...thumbnails,[id]:media};}catch{/* Thumbnail failure leaves a neutral bundled placeholder, never a direct URL. */}}})();}
 let poolOffset=$state(0);
 let downloadGroups=$derived([...new Set(jobs.flatMap(j=>j.group_id?[j.group_id]:[]))].map(id=>({id,label:jobs.find(j=>j.group_id===id)?.group_label??id,members:jobs.filter(j=>j.group_id===id)})));
 let navigation=$derived({index:visiblePosts.findIndex(p=>p.key.id===selectedId),total:visiblePosts.length});
 let submitted:{query:Query;username:string;poolId:string}|null=null;
 const query=():Query=>({include:[...include],exclude:[...exclude],ratings:{only:[...(safe?['safe' as const]:[]),...(questionable?['questionable' as const]:[]),...(explicit?['explicit' as const]:[])]},artist:null,sort});
 onMount(()=>{void refresh();void readAppliedPreferences();return ()=>{generation++;suggestionGeneration++;mediaGeneration++;preferenceGeneration++;};});
 async function refresh(){try{[saved,pins]=await Promise.all([bridge.searches(),bridge.pins()]);}catch(e){error=String(e);}}
 function chip(which:'include'|'exclude',value:string){
  const token=value.trim();if(!token||/\s/.test(token)||token.startsWith('-')){error='Enter one unsigned tag at a time.';return;}
  if(which==='include'){if(!include.includes(token))include=[...include,token];includeInput='';}else{if(!exclude.includes(token))exclude=[...exclude,token];excludeInput='';}suggestions=[];suggestionGeneration++;
 }
 async function complete(){const id=++suggestionGeneration;const prefix=includeInput.trim();if(prefix.length<2){suggestions=[];return;}try{const rows=await bridge.suggestions(prefix);if(id===suggestionGeneration)suggestions=rows;}catch(e){if(id===suggestionGeneration)error=String(e);}}
 async function view(id:string){const request=++mediaGeneration;error='';try{const item=await bridge.prepare(id,'full');if(request!==mediaGeneration)return;grants=[item.src];prepared=createCatalog([item],grants);selectedId=item.id;}catch(e){if(request===mediaGeneration)error=String(e);}}
 async function run(nextMode:typeof mode,next=1,offset=0,paginate=false){const id=++generation;mediaGeneration++;selectedId=null;prepared=[];grants=[];thumbnails={};requestedPreviews.clear();result=null;pool=null;busy=true;error='';try{
  const intent=paginate&&submitted?submitted:{query:query(),username,poolId};
  const q=intent.query;if(q.ratings!=='any'&&!q.ratings.only.length)throw Error('Select at least one rating.');
  if(nextMode==='pool'){const value=Number(intent.poolId);if(!Number.isSafeInteger(value)||value<1)throw Error('Enter a positive pool ID.');const p=await bridge.pool(value,offset,q.ratings);if(id!==generation)return;pool=p;poolOffset=offset;result=null;}
  else{const p=nextMode==='favorites'?await bridge.favorites(q,intent.username,next):await bridge.search(q,next);if(id!==generation)return;result=p;pool=null;}
  submitted=intent;mode=nextMode;page=next;
 }catch(e){if(id===generation)error=String(e);}finally{if(id===generation)busy=false;}}
 function clearAccountView(){generation++;mediaGeneration++;busy=false;result=null;pool=null;selectedId=null;prepared=[];grants=[];thumbnails={};requestedPreviews.clear();}
 async function login(){clearAccountView();error='';const key=accountKey;accountKey='';await tick();try{identity=await bridge.login(accountUser,key);}catch(e){error=String(e);}}
 async function restoreAccount(){clearAccountView();error='';try{identity=await bridge.restoreAccount();}catch(e){error=String(e);}}
 async function logout(){clearAccountView();identity=null;accountKey='';try{await bridge.logout();}catch(e){error=String(e);}}
 async function chooseDownloads(){try{downloadGranted=await bridge.chooseDownloads();if(downloadGranted)jobs=await bridge.jobs();}catch(e){error=String(e);}}
 $effect(()=>{if(!downloadGranted)return;let active=true,inflight=false;const timer=setInterval(async()=>{if(inflight)return;inflight=true;try{const rows=await bridge.jobs();if(active)jobs=rows;}catch(e){if(active)error=String(e);}finally{inflight=false;}},500);return ()=>{active=false;clearInterval(timer);};});
 async function refreshJobs(){try{jobs=await bridge.jobs();}catch(e){error=String(e);}}
 async function queueWholePool(){if(!pool||!submitted||busy)return;const id=Number(pool.pool.key.id),ratings=submitted.query.ratings;busy=true;try{await bridge.queueWholePool(id,ratings);await refreshJobs();}catch(e){error=String(e);}finally{busy=false;}}
 async function queuePoolPage(){try{await bridge.queuePoolPage();await refreshJobs();}catch(e){error=String(e);}}
 async function controlGroup(id:string,verb:'pause'|'resume'|'cancel'){try{await bridge.controlGroup(id,verb);await refreshJobs();}catch(e){error=String(e);}}
 async function queueDownload(id:string){try{await bridge.queue(id);await refreshJobs();}catch(e){error=String(e);}}
 async function control(id:number,verb:'pause'|'resume'|'cancel'){try{await bridge.control(id,verb);await refreshJobs();}catch(e){error=String(e);}}
 async function previewLegacy(){error='';importNotice='';legacy=null;importSearches=[];importPools=[];try{legacy=await bridge.legacyPreview();}catch(e){error=String(e);}}
 async function confirmLegacy(){if(!legacy)return;const snapshot=legacy;legacy=null;try{const report=await bridge.legacyConfirm(snapshot.digest,[...importSearches],[...importPools]);importNotice=`Imported ${report.imported_searches} searches and ${report.imported_pins} pins; skipped ${report.skipped_searches+report.skipped_pins} existing records.`;await refresh();}catch(e){error=String(e);}}
 async function readAppliedPreferences(){const request=++preferenceGeneration;try{const value=await bridge.preferences();if(request===preferenceGeneration)applied=value;}catch(e){if(request===preferenceGeneration)error=String(e);}}
 async function loadPreferences(){const request=++preferenceGeneration;try{const value=await bridge.preferences();if(request!==preferenceGeneration)return;preferences=structuredClone(value);applied=structuredClone(value);preferenceNotice='';}catch(e){if(request===preferenceGeneration)error=String(e);}}
 async function savePreferences(){if(!preferences)return;const request=++preferenceGeneration;try{const value=await bridge.savePreferences($state.snapshot(preferences));if(request!==preferenceGeneration)return;preferences=structuredClone(value);applied=structuredClone(value);preferenceNotice='Preferences saved';}catch(e){if(request===preferenceGeneration)error=String(e);}}
 async function save(){error='';try{const value=name.trim();if(!value)throw Error('Name this search first.');await bridge.save({id:crypto.randomUUID(),name:value,query:query()});name='';await refresh();}catch(e){error=String(e);}}
 async function remove(id:string){try{await bridge.delete(id);await refresh();}catch(e){error=String(e);}}
 async function pin(){if(!pool)return;const id=Number(pool.pool.key.id);try{await bridge.pin(id,!pins.some(p=>p.id===String(id)));await refresh();}catch(e){error=String(e);}}
 function restore(s:SavedSearch){include=[...s.query.include];exclude=[...s.query.exclude];sort=s.query.sort;safe=s.query.ratings==='any'||s.query.ratings.only.includes('safe');questionable=s.query.ratings==='any'||s.query.ratings.only.includes('questionable');explicit=s.query.ratings==='any'||s.query.ratings.only.includes('explicit');}
</script>
<section class="browser" aria-label="Furchive artwork browser">
 <header><strong>Furchive</strong><small>RUST ALPHA · e621</small><span>Artwork browser · scoped media cache · durable download queue</span></header>
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
   <h2>Account</h2><p>Anonymous until explicitly verified. API keys stay in your OS credential store, never settings or logs. Saved credentials are not restored automatically.</p>
   {#if identity}<p>Verified account: {identity.name}</p><button onclick={logout}>Log out and remove key</button>{:else}
    <label>Username<input aria-label="Account username" bind:value={accountUser} autocomplete="username"></label><label>API key<input type="password" aria-label="Account API key" bind:value={accountKey} autocomplete="off"></label><button onclick={login}>Verify and save account</button><button onclick={restoreAccount}>Restore saved account</button>
   {/if}
   <h2>Preferences</h2><button onclick={loadPreferences}>Load preferences</button>
   {#if preferences}
    <label>Filename template<input aria-label="Filename template" bind:value={preferences.filename_template}></label>
    <label>Pool filename template<input aria-label="Pool filename template" bind:value={preferences.pool_filename_template}></label>
    <label>Gallery scale<input aria-label="Gallery scale" type="number" min="0.5" max="2" step="0.25" bind:value={preferences.gallery_scale}></label>
    <label>Resident media cache (MiB)<input aria-label="Resident media cache MiB" type="number" min="16" max="512" step="16" value={Math.min(512,preferences.cache_limit_bytes/1048576)} onchange={event=>{if(preferences)preferences.cache_limit_bytes=Number(event.currentTarget.value)*1048576;}}></label>
    <label class="import-row"><input aria-label="Start videos muted" type="checkbox" bind:checked={preferences.start_muted}>Start videos muted</label>
    <label>Duplicate downloads<select aria-label="Duplicate downloads" value={preferences.duplicates} onchange={event=>{const value=event.currentTarget.value;if(preferences&&(value==='skip'||value==='rename'||value==='overwrite'))preferences.duplicates=value;}}><option value="skip">Skip existing</option><option value="rename">Keep both (rename)</option><option value="overwrite">Replace after verified completion</option></select></label>
    <p>Media cache is compressed resident bytes, capped at 512 MiB. Decoded webview surfaces and in-flight response buffers have separate lifetimes. Gallery and mute changes apply after acknowledged save; mute policy applies on next media open.</p>
    <label>Concurrent downloads<select aria-label="Concurrent downloads" value={preferences.concurrent_downloads} onchange={event=>{if(preferences)preferences.concurrent_downloads=Number(event.currentTarget.value);}}>{#each [1,2,3,4] as n}<option value={n}>{n}</option>{/each}</select></label>
    <p>Concurrency is applied when granting a download directory. Restart before changing an active worker. Jobs sharing the same native destination directory are conservatively serialized to protect aliases and Rename collisions. Account keys/directory grants/session restoration are not edited here.</p>
    <button onclick={savePreferences}>Save preferences</button>{#if preferenceNotice}<p role="status">{preferenceNotice}</p>{/if}
   {/if}
   <h2>Downloads</h2><button onclick={chooseDownloads}>Choose download directory</button><button onclick={refreshJobs}>Refresh downloads</button><p>New jobs start paused. Resume explicitly starts transfer. Reselect the same directory after restart to recover history.</p>
   {#each downloadGroups as group}<div data-download-group={group.id}><b>{group.label}</b><p>{group.members.filter(j=>j.state==='Completed').length} / {group.members.length} complete</p><button onclick={()=>controlGroup(group.id,'resume')}>Resume group</button><button onclick={()=>controlGroup(group.id,'pause')}>Pause group</button><button onclick={()=>controlGroup(group.id,'cancel')}>Cancel group</button></div>{/each}
   {#each jobs as job}<div data-job-id={job.id} data-job-bytes={job.bytes} data-group-position={job.group_position??undefined}><b>Post {job.post_id} · {job.state}</b><p>{job.bytes}{job.total_bytes!=null?' / '+job.total_bytes:''} bytes · {job.relative_path}</p>
    {#if job.total_bytes!=null&&job.total_bytes>0}<progress aria-label={'Download progress '+job.id} max={job.total_bytes} value={job.bytes}></progress>{/if}
    <p>{job.bytes_per_second!=null?Math.round(job.bytes_per_second)+' B/s':'Rate unavailable'} · {job.eta_seconds!=null?'ETA '+Math.ceil(job.eta_seconds)+'s':'ETA unavailable'}</p>{#if job.error}<p>{job.error}</p>{/if}
    <button disabled={!['Paused','Failed'].includes(job.state)} onclick={()=>control(job.id,'resume')}>Resume</button><button disabled={!['Running','Queued'].includes(job.state)} onclick={()=>control(job.id,'pause')}>Pause</button><button disabled={!['Paused','Failed','Running','Queued'].includes(job.state)} onclick={()=>control(job.id,'cancel')}>Cancel</button>
   </div>{/each}
   <h2>Recover old data</h2><p>Choose the old Furchive data folder in the system picker. Preview first; artwork, keys, settings and queued downloads are never imported automatically.</p><button onclick={previewLegacy}>Preview legacy data</button>
   {#if legacy}<div aria-label="Legacy import preview"><p>Preview digest: <code>{legacy.digest}</code></p>{#each legacy.warnings as warning}<p>{warning}</p>{/each}
    {#each legacy.searches as search}<label class="import-row"><input type="checkbox" aria-label={'Import search '+search.name} bind:group={importSearches} value={search.id}>{search.name}</label>{/each}
    {#each legacy.pins as pin}<label class="import-row"><input type="checkbox" aria-label={'Import pool '+pin.key.id} bind:group={importPools} value={pin.key.id}>Pool {pin.key.id}: {pin.name}</label>{/each}
    <button disabled={!importSearches.length&&!importPools.length} onclick={confirmLegacy}>Confirm selected import</button>
   </div>{/if}{#if importNotice}<p role="status">{importNotice}</p>{/if}
   <h2>Pinned pools</h2>{#each pins as p}<button onclick={()=>{poolId=p.id;void run('pool');}}>Pool {p.id}</button>{/each}
  </aside>
  <div class="results" aria-busy={busy}>
   {#if error}<p role="alert">{error}</p>{/if}
   {#if busy}<p role="status">Loading…</p>{/if}
   {#if pool}<Gallery scale={applied?.gallery_scale??1} items={galleryItems} grants={galleryGrants} selectedId={selectedId} onselect={id=>void view(id)} onvisible={visible}/><h1>{pool.pool.name}</h1><p>{pool.total} ordered members · hidden/unavailable members retain their original position.</p><button onclick={pin}>{pins.some(p=>p.id===pool?.pool.key.id)?'Unpin pool':'Pin pool'}</button><button disabled={!downloadGranted||busy} onclick={queuePoolPage}>Queue visible pool page</button><button disabled={!downloadGranted||busy||pool.total>10000} onclick={queueWholePool}>Queue whole pool ({pool.total} members)</button><p>Whole-pool metadata uses bounded batches, current account filters and submitted ratings. Hidden members retain positions; jobs start paused. Maximum 10,000 members.</p>
    <ol class="posts">{#each pool.entries as e}<li value={e.position+1} data-post-id={e.id}><b>#{e.position+1} · {e.id}</b><p>{e.post?.title??'Unavailable or filtered'}</p>{#if e.post}<button onclick={()=>view(e.id)}>View artwork</button><button disabled={!downloadGranted} onclick={()=>queueDownload(e.id)}>Queue download</button>{/if}</li>{/each}</ol>
    <button disabled={busy||poolOffset===0} onclick={()=>run('pool',1,Math.max(0,poolOffset-50),true)}>Previous pool page</button>
    {#if pool.next_offset!==null}<button disabled={busy} onclick={()=>run('pool',1,pool?.next_offset??0,true)}>Next pool page</button>{/if}
   {:else if result}<Gallery scale={applied?.gallery_scale??1} items={galleryItems} grants={galleryGrants} selectedId={selectedId} onselect={id=>void view(id)} onvisible={visible}/><h1>{mode==='favorites'?'Favorites':'Search results'} · page {page}</h1><p>{result.items.length} visible posts from {result.raw_count} upstream records. Account filters remain respected.</p>
    <ol class="posts">{#each result.items as p}<li data-post-id={p.key.id}><b>{p.title}</b><button onclick={()=>view(p.key.id)}>View artwork</button><button disabled={!downloadGranted} onclick={()=>queueDownload(p.key.id)}>Queue download</button><p>{p.artists.join(', ')||'No artist supplied'} · {p.rating} · score {p.score}</p><details><summary>Tags &amp; description</summary><p>{p.description}</p>{#each Object.entries(p.tags) as [category,tags]}<p><b>{category}:</b> {tags.join(' ')}</p>{/each}</details></li>{/each}</ol>
    <nav><button disabled={busy||page===1} onclick={()=>run(mode,page-1,0,true)}>Previous page</button><button disabled={busy||result.next_page===null} onclick={()=>run(mode,result?.next_page??1,0,true)}>Next page</button></nav>
   {:else}<h1>Your collection starts with a search.</h1><p>No network request is made until you search, browse favorites, request suggestions, or open a pool. Safe-only is the initial rating selection. Open a returned post to view validated media through the scoped Rust cache. Downloads and full gallery integration are still in progress.</p>{/if}
  </div>
 </div>
 <Viewer startMuted={applied?.start_muted??true} items={prepared} {grants} {selectedId} {navigation} onstep={delta=>{const post=visiblePosts[navigation.index+delta];if(post)void view(post.key.id);}} onclose={()=>{selectedId=null;mediaGeneration++;}}/>
</section>
<style>
 .browser{color:#e5e9ed}header{gap:16px;flex-wrap:wrap;height:auto;min-height:70px}header span{color:#98a7b6;font-size:12px}.layout{display:grid;grid-template-columns:280px minmax(0,1fr)}aside{padding:20px;display:flex;flex-direction:column;gap:9px}h2{margin:15px 0 3px;font-size:15px}input,button,select{border-radius:5px;background:#202b36;color:#e5e9ed;border:1px solid #526271;padding:8px;font:inherit}input{min-width:0;width:100%}button{cursor:pointer}button:disabled{opacity:.5;cursor:wait}.primary{background:#b76b31;border-color:#ebad6b}.chips{display:flex;flex-wrap:wrap;gap:4px}.chips button{background:#254934}.excludes button{background:#57303b}fieldset{border:1px solid #394857;margin:0;padding:8px;display:flex;flex-wrap:wrap;gap:8px}fieldset label{display:flex;flex-direction:row;gap:4px;align-items:center;font-size:12px}fieldset input{width:auto}.import-row{flex-direction:row;align-items:center}.import-row input{width:auto}code{overflow-wrap:anywhere}.results{padding:24px;min-width:0}.posts{list-style:none;padding:0;display:grid;grid-template-columns:repeat(auto-fill,minmax(230px,1fr));gap:12px}.posts li{background:#1c252e;border:1px solid #354656;border-radius:6px;padding:16px;overflow-wrap:anywhere}.posts p{margin:8px 0}.saved{display:flex;gap:4px}.saved button:first-child{flex:1}nav{display:flex;gap:12px}[role=alert]{color:#ffbdc9;border:1px solid #a86470;padding:12px}.suggestions{display:flex;flex-wrap:wrap;gap:4px}@media(max-width:800px){.layout{grid-template-columns:230px minmax(0,1fr)}.results{padding:12px}}
</style>
