import {tick} from 'svelte';
/** Rendered UI driver. Native proof comes from host readback, never this function alone. */
export async function runAccountCampaign(phase:number){
 const checks:string[]=[];
 const wait=async(predicate:()=>boolean)=>{const end=performance.now()+8000;while(!predicate()){const alert=document.querySelector('[role="alert"]');if(alert)throw Error('native account UI: '+alert.textContent);if(performance.now()>end)throw Error('native account UI condition timed out');await new Promise(r=>setTimeout(r,20));await tick();}};
 const button=(name:string)=>{const b=[...document.querySelectorAll<HTMLButtonElement>('button')].find(b=>b.textContent?.trim()===name);if(!b||b.disabled)throw Error('missing account control '+name);b.click();};
 await wait(()=>document.querySelector('[aria-label="Account API key"]')!==null);
 if(document.body.textContent?.includes('Verified account:'))throw Error('account restored without explicit intent');
 if(phase===3){
  checks.push('anonymous-start');
  const user=document.querySelector<HTMLInputElement>('[aria-label="Account username"]')!,key=document.querySelector<HTMLInputElement>('[aria-label="Account API key"]')!;
  if(key.type!=='password')throw Error('account key input is not masked');
  user.value='fixture_user';user.dispatchEvent(new Event('input',{bubbles:true}));key.value='synthetic-native-vault-qualification-only';key.dispatchEvent(new Event('input',{bubbles:true}));await tick();button('Verify and save account');
  await wait(()=>document.body.textContent?.includes('Verified account: fixture_user')===true);checks.push('verified-login-native-save');if(key.value!=='')throw Error('account input retained key');checks.push('masked-key-cleared');
 }else if(phase===4){
  checks.push('anonymous-restart');button('Restore saved account');await wait(()=>document.body.textContent?.includes('Verified account: fixture_user')===true);checks.push('explicit-native-restore');button('Log out and remove key');await wait(()=>document.querySelector('[aria-label="Account API key"]')!==null&&!document.body.textContent?.includes('Verified account:'));checks.push('logout-native-delete');
 }else throw Error('invalid account UI phase');
 if(document.querySelector('[role="alert"]'))throw Error('native account UI displayed error: '+document.querySelector('[role="alert"]')?.textContent);
 return {schema:1,ok:true,phase,checks};
}
