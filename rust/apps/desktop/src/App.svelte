<script lang="ts">
 import {onMount} from 'svelte';
 import {invoke} from '@tauri-apps/api/core';
 import Qualification from './Qualification.svelte';
 import Engineering from './Engineering.svelte';
 let mode=$state<'landing'|'qualification'|'engineering'>('landing'),nativeTest=$state(false);
 onMount(async()=>{
  // Parent may enable the bounded media driver only in a self-test build.
  try{nativeTest=await invoke<boolean>('media_test_mode');if(nativeTest){mode='engineering';return;}}catch{/* Older native shell has no media driver seam. */}
  try{if(await invoke<boolean>('qualification_mode'))mode='qualification';}catch{/* Ordinary browser engineering harness. */}
 });
</script>
<svelte:head><title>Furchive · Engineering Alpha</title></svelte:head>
{#if mode==='qualification'}<Qualification/>{:else if mode==='engineering'}<Engineering {nativeTest}/>{:else}
 <header><div><span class="mark">F</span><strong>Furchive</strong><small>RUST ALPHA</small></div></header>
 <div class="landing"><h1>Local-first gallery components</h1><p>This is an explicit engineering harness, not the finished application. It uses generated harmless media only. No account, remote artwork, saved session or automatic user-data loading.</p><button onclick={()=>mode='engineering'}>Open engineering gallery</button><button onclick={()=>mode='qualification'}>Open original qualification screen</button></div>
{/if}
<style>.landing{max-width:700px;margin:10vh auto;padding:24px}button{background:#293a49;color:#f0e5da;border:1px solid #677888;border-radius:5px;padding:12px;margin:8px 8px 8px 0}</style>
