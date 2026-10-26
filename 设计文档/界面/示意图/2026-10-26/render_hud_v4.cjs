const {chromium}=require('C:/Users/1/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright');
const {pathToFileURL}=require('node:url');
const path=require('node:path');
const fs=require('node:fs');
// Usage: node render_hud_v4.cjs [dir stem...]; defaults to the v4 states in this folder.
const DIR=process.argv[2]?path.resolve(process.argv[2]):__dirname;
const STEMS=process.argv.length>3?process.argv.slice(3):['1-normal-plan','2-normal-reaction','3-intervention-own','4-intervention-other','5-normal-timing'].map(s=>`hero-hud-v4-${s}`);
(async()=>{
  const browser=await chromium.launch({channel:'msedge',headless:true});
  const report=[];
  for(const stem of STEMS) {
    const page=await browser.newPage({viewport:{width:1920,height:1080},deviceScaleFactor:1});
    await page.goto(pathToFileURL(path.join(DIR,stem+'.svg')).href);
    await page.evaluate(()=>document.fonts.ready);
    const check=await page.evaluate(()=>({
      parserError:document.querySelector('parsererror')?.textContent??null,
      textCount:document.querySelectorAll('text').length,
      outsideCanvas:[...document.querySelectorAll('text')].flatMap(t=>{
        const r=t.getBoundingClientRect();
        return r.left<0||r.top<0||r.right>1920||r.bottom>1080?[{text:t.textContent,x:r.x,y:r.y,w:r.width,h:r.height}]:[];
      }),
      overlappingText:(()=>{
        const t=[...document.querySelectorAll('text')].map(e=>({s:e.textContent,b:e.getBoundingClientRect()}));
        const bad=[];
        for(let i=0;i<t.length;i++)for(let j=i+1;j<t.length;j++) {
          const a=t[i],b=t[j];
          const w=Math.min(a.b.right,b.b.right)-Math.max(a.b.left,b.b.left);
          const h=Math.min(a.b.bottom,b.b.bottom)-Math.max(a.b.top,b.b.top);
          if(w>2&&h>2)bad.push([a.s,b.s]);
        }
        return bad;
      })(),
      // Right-hand detail texts must stay inside the panel's inner margin.
      outsideDetailPanel:[...document.querySelectorAll('text')].flatMap(t=>{
        const r=t.getBoundingClientRect();
        return r.left>=1464&&r.right>1884?[{text:t.textContent,right:r.right}]:[];
      }),
    }));
    report.push({state:stem,...check});
    if(check.parserError||check.outsideCanvas.length)throw Error(JSON.stringify(report));
    await page.screenshot({path:path.join(DIR,stem+'.png')});
    await page.setViewportSize({width:1366,height:768});
    await page.evaluate(()=>{const svg=document.querySelector('svg');svg.style.width='100vw';svg.style.height='100vh';svg.style.display='block';});
    await page.screenshot({path:path.join(DIR,stem+'-1366-check.png')});
    await page.close();
  }
  fs.writeFileSync(path.join(DIR,'svg-layout-check.json'),JSON.stringify(report,null,2));
  console.log(JSON.stringify(report.map(r=>({state:r.state,texts:r.textCount,outside:r.outsideCanvas.length,overlaps:r.overlappingText,outsidePanel:r.outsideDetailPanel}))));
  await browser.close();
})().catch(e=>{console.error(e);process.exit(1)});
