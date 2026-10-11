#!/usr/bin/env node
import { createRequire } from 'node:module';
import { readFile, mkdir } from 'node:fs/promises';
const require = createRequire(new URL('../web/package.json', import.meta.url));
const { chromium, expect } = require('@playwright/test');
const base = process.env.PUBLIC_URL ?? `http://localhost:${process.env.PORT ?? '4104'}`;
const token = process.env.DEMO_CONTROL_TOKEN;
if (!token) throw new Error('DEMO_CONTROL_TOKEN is required in environment; never pass it in argv');
async function control(phase,duration=120) {
 const response=await fetch(base+'/api/demo/scenario',{method:'POST',headers:{Authorization:'Bearer '+token,'Content-Type':'application/json'},body:JSON.stringify({phase,duration_seconds:duration}),signal:AbortSignal.timeout(15000)});
 if(!response.ok)throw new Error('Control request failed '+response.status);
 return response.json();
}
const browser=await chromium.launch({headless:true,executablePath:process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH||undefined});
const page=await browser.newPage({viewport:{width:1360,height:1000}});
const errors=[];page.on('pageerror',error=>errors.push(error.message));
if(process.env.DISABLE_RUM_FOR_JOURNEY==='true'){
 const url=process.env.CORRIDORA_RUM_SCRIPT_URL??'https://rum.corridora.com/corridora-rum.js';
 await page.route(url.split('?')[0]+'*',route=>route.abort());
}
if(process.env.RUM_BUNDLE_PATH){
 const bundle=await readFile(process.env.RUM_BUNDLE_PATH);
 const url=process.env.CORRIDORA_RUM_SCRIPT_URL??'https://rum.corridora.com/corridora-rum.js';
 await page.route(url.split('?')[0]+'*',route=>route.fulfill({status:200,contentType:'application/javascript',body:bundle}));
}
async function ready(){await page.goto(base,{waitUntil:'networkidle'});await expect(page.getByTestId('summary-demo-workout')).toBeVisible();if(process.env.CORRIDORA_RUM_KEY)await page.waitForFunction(()=>window.CorridoraRum?.getInitializationStatus()==='ready');}
async function summary(id,status){await page.getByTestId('summary-'+id).click();await expect(page.getByTestId('job-status')).toHaveAttribute('data-status',status,{timeout:65000});}
let complete=false;
try {
 await control('healthy');await ready();await summary('demo-workout','complete');
 await page.getByTestId('trace-check').click();await expect(page.getByTestId('trace-id')).toHaveText(/^[0-9a-f]{32}$/);const healthyTrace=await page.getByTestId('trace-id').innerText();
 await expect(page.locator('.content')).toHaveCSS('display','grid');
 await expect(page.locator('h1')).toHaveCSS('font-size','68px');
 await expect(page.locator('aside')).toHaveCSS('background-color','rgb(23, 63, 58)');
 const artifacts=new URL('../.local/validation/',import.meta.url);await mkdir(artifacts,{recursive:true});
 await page.screenshot({path:new URL('stride-desktop.png',artifacts).pathname,fullPage:true});
 await page.setViewportSize({width:390,height:844});await expect(page.locator('h1')).toHaveCSS('font-size','49px');
 if(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth))throw new Error('Mobile layout overflows horizontally');
 await page.screenshot({path:new URL('stride-mobile.png',artifacts).pathname,fullPage:true});
 await page.setViewportSize({width:1360,height:1000});
 const rollout=await control('regression');await ready();await summary('demo-workout','poison');await summary('healthy-workout','complete');
 await control('recover');await ready();await summary('demo-workout','complete');
 const expiring=await control('regression',1);await new Promise(r=>setTimeout(r,1500));
 const effective=await (await fetch(base+'/api/demo/status',{signal:AbortSignal.timeout(15000)})).json();
 if(effective.release===expiring.release)throw new Error('Finite override did not expire');
 if(effective.phase==='regression'&&!effective.scheduled)throw new Error('Expired override still drives regression');
 // An expired override during the natural UTC window correctly returns to its daily regression.
 await control('recover');await ready();await summary('demo-workout','complete');
 if(errors.length)throw new Error('Browser errors: '+errors.join('; '));
 if(process.env.CORRIDORA_RUM_KEY)await page.evaluate(async()=>{await window.CorridoraRum.init({}).forceFlush();});
 const heartbeat={ok:true,healthy_trace_id:healthyTrace,release:rollout.release,phases:['healthy','poison','healthy-control','recover','expired'],at:new Date().toISOString()};
 const response=await fetch(base+'/api/demo/heartbeat',{method:'POST',headers:{Authorization:'Bearer '+token,'Content-Type':'application/json'},body:JSON.stringify(heartbeat),signal:AbortSignal.timeout(15000)});if(!response.ok)throw new Error('Heartbeat failed '+response.status);
 complete=true;console.log(JSON.stringify({url:base,...heartbeat,browser_rum_enabled:!!process.env.CORRIDORA_RUM_KEY,stored_corridora_correlation:'requires scoped query or UI verification'}));
} finally {
 if(!complete)await control('recover').catch(()=>{});
 await browser.close();
}
