import {test} from 'node:test';
import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
// Source inventory guard for native driver, not behavior or native proof.
test('legacy qualification screen remains a separate unchanged mode',async()=>{
 const old=await readFile(new URL('../src/Qualification.svelte',import.meta.url),'utf8');
 assert.match(old,/async function qualify/);assert.match(old,/qualification_result/);
});
