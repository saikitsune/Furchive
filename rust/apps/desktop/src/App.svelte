<script lang="ts">
 import {onMount,tick} from 'svelte';
 import {runBrowseCampaign} from './lib/browse-campaign';
 import {invoke} from '@tauri-apps/api/core';
 import Qualification from './Qualification.svelte';
 import Engineering from './Engineering.svelte';
 import BrowsePanel from './lib/BrowsePanel.svelte';
 import {BrowserBridge} from './lib/browser-bridge';
 const bridge=new BrowserBridge(invoke);
 let mode=$state<'browse'|'qualification'|'engineering'>('browse'),nativeTest=$state(false);
 onMount(async()=>{
  // Parent may enable the bounded media driver only in a self-test build.
  try{nativeTest=await invoke<boolean>('media_test_mode');if(nativeTest){mode='engineering';return;}}catch{/* Older native shell has no media driver seam. */}
  try{
   const phase=await invoke<number>('browse_test_phase');
   if(phase){await tick();try{await invoke('qualification_result',{result:await runBrowseCampaign(phase)});}catch(e){await invoke('qualification_result',{result:{schema:1,ok:false,phase,error:String(e)}});}return;}
  }catch{/* Production build has no active test phase. */}
  try{if(await invoke<boolean>('qualification_mode'))mode='qualification';}catch{/* Ordinary browser engineering harness. */}
 });
</script>
<svelte:head><title>Furchive · Rust Alpha</title></svelte:head>
{#if mode==='qualification'}<Qualification/>{:else if mode==='engineering'}<Engineering {nativeTest}/>{:else}
 <BrowsePanel {bridge}/>
 <footer><button onclick={()=>mode='engineering'}>Harmless media test gallery</button><small>Alpha: durable download controls, full gallery, account settings and migration UI still pending integration.</small></footer>
{/if}
<style>footer{padding:16px;display:flex;align-items:center;gap:12px}button{background:#293a49;color:#f0e5da;border:1px solid #677888;border-radius:5px;padding:12px;margin:8px 8px 8px 0}</style>
