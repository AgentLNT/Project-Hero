const {chromium}=require('C:/Users/1/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright');
const {pathToFileURL}=require('node:url');
const path=require('node:path');
const fs=require('node:fs');
(async()=>{
  const browser=await chromium.launch({channel:'msedge',headless:true});
  const report=[];
  for(const state of ['normal','intervention']) {
    const stem=`hero-hud-${state}-vector-v3`;
    const page=await browser.newPage({viewport:{width:1920,height:1080},deviceScaleFactor:1});
    await page.goto(pathToFileURL(path.join(__dirname,stem+'.svg')).href);
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
    }));
    report.push({state,...check});
    if(check.parserError||check.outsideCanvas.length)throw Error(JSON.stringify(report));
    await page.screenshot({path:path.join(__dirname,stem+'.png')});
    await page.setViewportSize({width:1366,height:768});
    await page.evaluate(()=>{const svg=document.querySelector('svg');svg.style.width='100vw';svg.style.height='100vh';svg.style.display='block';});
    await page.screenshot({path:path.join(__dirname,stem+'-1366-check.png')});
    await page.close();
  }
  fs.writeFileSync(path.join(__dirname,'svg-layout-check.json'),JSON.stringify(report,null,2));
  console.log(JSON.stringify(report));
  await browser.close();
})().catch(e=>{console.error(e);process.exit(1)});
