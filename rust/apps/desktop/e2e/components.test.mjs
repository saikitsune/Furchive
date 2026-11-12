// Real compiled Svelte component tests. Browser evidence only, never native qualification.
import {test,before,after} from 'node:test';
import assert from 'node:assert/strict';
import {spawn} from 'node:child_process';
import {setTimeout as delay} from 'node:timers/promises';
const {chromium}=await import(process.env.FURCHIVE_PLAYWRIGHT_MODULE || 'playwright');
let browser,server,page;
before(async()=>{
 server=spawn(process.execPath,['node_modules/vite/bin/vite.js','--host','127.0.0.1','--port','1427','--strictPort'],{cwd:new URL('..',import.meta.url),stdio:'pipe'});
 let log='';server.stdout.on('data',x=>log+=x);server.stderr.on('data',x=>log+=x);
 for(let i=0;i<100;i++){try {if((await fetch('http://127.0.0.1:1427')).ok)break;}catch{} await delay(100);if(i===99)throw new Error(log);}
 browser=await chromium.launch({headless:true,chromiumSandbox:true});
 page=await browser.newPage({viewport:{width:1280,height:900}});
});
after(async()=>{await browser?.close();server?.kill();});

test('rendered gallery selects and navigates stable IDs with bounded responsive DOM',async()=>{
 await page.goto('http://127.0.0.1:1427');
 await page.getByRole('button',{name:'Open engineering gallery'}).click();
 await page.waitForSelector('[data-gallery]');
 const tiles=page.locator('[data-gallery-tile]');
 assert.ok(await tiles.count()>0 && await tiles.count()<=108);
 await tiles.first().click();
 assert.equal(await page.locator('[data-viewer]').getAttribute('data-id'),'fixture-0');
 await page.locator('[data-viewer]').press('ArrowRight');
 assert.equal(await page.locator('[data-viewer]').getAttribute('data-id'),'fixture-1');
 await page.getByRole('button',{name:'Close viewer'}).click();
 assert.equal(await page.locator('[data-gallery]').evaluate(e=>e===document.activeElement),true);
 await page.locator('[data-gallery]').press('End');
 assert.equal(await page.locator('[data-gallery]').getAttribute('data-selected'),'fixture-9999');
 assert.ok(await tiles.count()<=108);
 await page.locator('[data-gallery]').press('Enter');
 assert.equal(await page.locator('[data-viewer]').getAttribute('data-id'),'fixture-9999');
 await page.getByRole('button',{name:'Close viewer'}).click();
 await page.setViewportSize({width:540,height:800});
 assert.ok(await tiles.count()<=108);
});
