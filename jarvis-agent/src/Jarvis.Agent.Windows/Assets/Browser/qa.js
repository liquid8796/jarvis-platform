// Structured QA uses the same ownership, debugger connection and native bridge as normal tools.
// No model-supplied JavaScript is evaluated. Every locator/assertion is interpreted below.
function qaValidate(spec) {
  if (!spec || typeof spec !== 'object' || Array.isArray(spec) || JSON.stringify(spec).length > 65536) throw Error('Invalid QA spec.');
  const allowed = new Set(['url','expectedUrl','ready','viewports','steps','timeoutMs','fullPage','requireVisualReview','referenceId']);
  for (const key of Object.keys(spec)) if (!allowed.has(key)) throw Error('Unknown QA field: ' + key);
  for (const key of ['fullPage','requireVisualReview']) if(spec[key]!=null && typeof spec[key]!=='boolean')throw Error('Invalid '+key+'.');
  if(spec.referenceId!=null && (typeof spec.referenceId!=='string'||spec.referenceId.length>2048))throw Error('Invalid referenceId.');
  const url = new URL(spec.url); if (!['http:','https:'].includes(url.protocol)) throw Error('QA url must be HTTP(S).');
  if (spec.expectedUrl != null) { const expected = new URL(spec.expectedUrl, spec.url); if (!['http:','https:'].includes(expected.protocol)) throw Error('Invalid expectedUrl.'); }
  if (!Array.isArray(spec.viewports) || spec.viewports.length < 1 || spec.viewports.length > 4) throw Error('QA requires 1-4 viewports.');
  const names = new Set();
  for (const v of spec.viewports) {
    if (!v || typeof v.name !== 'string' || !v.name.trim() || v.name.length > 80 || names.has(v.name)) throw Error('Viewport names must be unique.');
    names.add(v.name);
    if (!Number.isInteger(v.width) || !Number.isInteger(v.height) || v.width < 240 || v.width > 3840 || v.height < 240 || v.height > 2160) throw Error('Invalid viewport dimensions.');
    if (v.mobile != null && typeof v.mobile !== 'boolean') throw Error('Invalid mobile flag.');
  }
  if (!Array.isArray(spec.steps) || spec.steps.length < 1 || spec.steps.length > 24) throw Error('QA requires 1-24 steps.');
  const locator = l => {
    if (!l || typeof l !== 'object' || Array.isArray(l)) throw Error('Locator is required.');
    for (const [k,v] of Object.entries(l)) if (!['role','name','text','testId','css','frame'].includes(k) || (v != null && (typeof v !== 'string' || v.length > 500))) throw Error('Invalid locator field: ' + k);
    if (!['role','text','testId','css'].some(k => typeof l[k] === 'string' && l[k].trim())) throw Error('Locator needs role, text, testId or css.');
  };
  if (spec.ready != null) locator(spec.ready);
  let action = false, assertion = false;
  for (const step of spec.steps) {
    if (!step || !['click','fill','select','check','press','assert'].includes(step.action)) throw Error('Unsupported QA action.');
    for(const key of Object.keys(step))if(!['action','locator','value','key','expect'].includes(key))throw Error('Unknown QA step field: '+key);
    if (step.action !== 'assert') { action = true; locator(step.locator); }
    if (step.action === 'press' && (typeof step.key !== 'string' || step.key.length > 80)) throw Error('press requires a key.');
    if (['fill','select'].includes(step.action) && (typeof step.value !== 'string' || step.value.length > 10000)) throw Error('fill/select require a string value.');
    if (step.action === 'check' && typeof step.value !== 'boolean') throw Error('check requires a boolean value.');
    if (step.expect) {
      for(const key of Object.keys(step.expect))if(!['kind','locator','value'].includes(key))throw Error('Unknown assertion field: '+key);
      assertion = true;
      if (!['visible','hidden','text','value','checked','count','url'].includes(step.expect.kind)) throw Error('Unsupported QA assertion.');
      if (step.expect.kind !== 'url') locator(step.expect.locator || step.locator);
      if (['text','value','url'].includes(step.expect.kind) && typeof step.expect.value !== 'string') throw Error('Assertion requires a string value.');
      if (step.expect.kind === 'checked' && typeof step.expect.value !== 'boolean') throw Error('checked assertion requires boolean.');
      if (step.expect.kind === 'count' && (!Number.isInteger(step.expect.value) || step.expect.value < 0 || step.expect.value > 1000)) throw Error('Invalid count assertion.');
    } else throw Error('Every QA step requires an explicit expected postcondition.');
  }
  if (!action || !assertion) throw Error('QA requires a real interaction and an expected postcondition.');
  if (spec.timeoutMs != null && (!Number.isInteger(spec.timeoutMs) || spec.timeoutMs < 1000 || spec.timeoutMs > 30000)) throw Error('timeoutMs must be 1000-30000.');
  return spec;
}

// Serialized into the requested frame. Includes open shadow roots and strict matching.
function qaDom(operation, locator, payload) {
  const norm = s => String(s ?? '').replace(/\s+/g, ' ').trim();
  const roots = [document];
  const elements = [];
  for (let i=0; i<roots.length; i++) for (const el of roots[i].querySelectorAll('*')) { elements.push(el); if (el.shadowRoot) roots.push(el.shadowRoot); }
  const visible = el => {
    const r = el.getBoundingClientRect(), s = getComputedStyle(el);
    return r.width > 0 && r.height > 0 && s.display !== 'none' && s.visibility !== 'hidden' && s.visibility !== 'collapse' && Number(s.opacity) > 0 && !el.closest('[hidden],[inert]');
  };
  const role = el => el.getAttribute('role') || ({BUTTON:'button',SELECT:'combobox',TEXTAREA:'textbox',IMG:'img',NAV:'navigation',MAIN:'main',DIALOG:'dialog',FORM:'form',H1:'heading',H2:'heading',H3:'heading',H4:'heading',H5:'heading',H6:'heading'}[el.tagName]) ||
    (el.tagName==='A' && el.hasAttribute('href') ? 'link' : el.tagName==='INPUT' ? ({checkbox:'checkbox',radio:'radio',button:'button',submit:'button',range:'slider',search:'searchbox'}[el.type] || 'textbox') : '');
  const name = el => {
    const labelled = el.getAttribute('aria-labelledby');
    if (labelled) return norm(labelled.split(/\s+/).map(id => el.getRootNode().getElementById?.(id)?.textContent || document.getElementById(id)?.textContent || '').join(' '));
    return norm(el.getAttribute('aria-label') || (el.labels && [...el.labels].map(l=>l.textContent).join(' ')) || el.getAttribute('alt') || el.innerText || el.getAttribute('title') || (['button','submit'].includes(el.type) ? el.value : ''));
  };
  let frameVisible=true,visibilityKnown=true,frameWindow=window;
  while(frameWindow!==frameWindow.parent){
    let owner;try{owner=frameWindow.frameElement;}catch{}
    if(!owner){visibilityKnown=false;break;}
    const box=owner.getBoundingClientRect(),style=owner.ownerDocument.defaultView.getComputedStyle(owner);
    if(box.width<=0||box.height<=0||style.display==='none'||style.visibility==='hidden'||Number(style.opacity)===0)frameVisible=false;
    frameWindow=frameWindow.parent;
  }
  if (operation === 'health') {
    const overlaySelectors = ['vite-error-overlay','[data-nextjs-dialog-overlay]','[data-nextjs-error-overlay]','#webpack-dev-server-client-overlay','#react-error-overlay'];
    const overlays = elements.filter(el => overlaySelectors.some(selector => el.matches(selector)) &&
      (visible(el) || (el.shadowRoot && [...el.shadowRoot.querySelectorAll('*')].some(visible))));
    const width = innerWidth, height = innerHeight;
    const clipping = el => {
      const r=el.getBoundingClientRect();
      if(r.left < -1 || r.right > width + 1)return 'viewport';
      let parent=el.parentElement || el.getRootNode().host;
      while(parent){
        const style=getComputedStyle(parent),box=parent.getBoundingClientRect();
        if((['hidden','clip'].includes(style.overflowX)&&(r.left<box.left-1||r.right>box.right+1))||
           (['hidden','clip'].includes(style.overflowY)&&(r.top<box.top-1||r.bottom>box.bottom+1)))return parent.tagName.toLowerCase()+(parent.id?'#'+parent.id:'');
        parent=parent.parentElement || parent.getRootNode().host;
      }
      return null;
    };
    const clipped = elements.filter(el => visible(el) && !['HTML','BODY'].includes(el.tagName) &&
      !el.closest('[data-jarvis-indicator]') && clipping(el))
      .slice(0,40).map(el=>({tag:el.tagName.toLowerCase(),text:norm(el.innerText).slice(0,100),left:el.getBoundingClientRect().left,right:el.getBoundingClientRect().right,clippedBy:clipping(el)}));
    const renderedElementCount=elements.filter(el=>!['HTML','BODY','SCRIPT','STYLE','NOSCRIPT','TEMPLATE'].includes(el.tagName)&&visible(el)).length;
    return { url:location.href,title:document.title,readyState:document.readyState,observedWidth:width,observedHeight:height,
      domPresent:!!document.body && renderedElementCount>0,renderedElementCount,frameworkOverlay:overlays.length>0,
      overlayDetails:overlays.map(el=>norm(el.innerText || el.shadowRoot?.textContent).slice(0,1000)),
      overflow:{horizontal:Math.max(document.documentElement.scrollWidth,document.body?.scrollWidth||0)>width+1,vertical:document.documentElement.scrollHeight>height+1,clipped},
      userAgent:navigator.userAgent,touchPoints:navigator.maxTouchPoints,deviceScaleFactor:devicePixelRatio };
  }
  if (locator.frame && locator.frame !== window.name && locator.frame !== location.href) return {matches:[],frameSkipped:true};
  let candidates = elements;
  if (locator.css) candidates = roots.flatMap(root=>[...root.querySelectorAll(locator.css)]);
  candidates = candidates.filter(el => (!locator.role || (role(el)===locator.role && !el.closest('[aria-hidden="true"]'))) && (!locator.name || name(el)===norm(locator.name)) &&
    (!locator.text || norm(el.innerText || el.textContent)===norm(locator.text)) && (!locator.testId || el.getAttribute('data-testid')===locator.testId));
  // Text locators select the most specific matching node rather than all its ancestors.
  if (locator.text) candidates = candidates.filter(el=>!candidates.some(other=>other!==el && el.contains(other)));
  const describe = el => {
    const r=el.getBoundingClientRect();
    return {text:norm(el.innerText||el.textContent),value:'value' in el?String(el.value):null,checked:'checked' in el?!!el.checked:null,
      visible:visible(el)&&frameVisible,visibilityKnown,enabled:!el.disabled && el.getAttribute('aria-disabled')!=='true' && !el.closest('[inert]'),
      rect:{x:r.x,y:r.y,width:r.width,height:r.height},role:role(el),name:name(el)};
  };
  if (operation === 'inspect') return {matches:candidates.slice(0,1001).map(describe)};
  if (candidates.length!==1) throw Error('Strict locator resolved '+candidates.length+' elements.');
  const el=candidates[0];el.scrollIntoView({block:'center',inline:'center'});
  const details=describe(el);
  if (!details.visible || !details.enabled) throw Error('Element is hidden or disabled.');
  const r=el.getBoundingClientRect(), x=r.left+r.width/2,y=r.top+r.height/2;
  let hit=document.elementFromPoint(x,y);while(hit?.shadowRoot){const next=hit.shadowRoot.elementFromPoint(x,y);if(!next||next===hit)break;hit=next;}
  if (hit!==el && !el.contains(hit)) throw Error('Element is covered by '+(hit?.tagName||'viewport edge')+'.');
  if (operation==='prepare') {
    let topX=x,topY=y,w=window,trustedPoint=true;
    while(w!==w.parent){let owner;try{owner=w.frameElement;}catch{}if(!owner){trustedPoint=false;break;}
      const f=owner.getBoundingClientRect();topX+=f.left;topY+=f.top;
      let parentHit=w.parent.document.elementFromPoint(topX,topY);
      while(parentHit?.shadowRoot){const next=parentHit.shadowRoot.elementFromPoint(topX,topY);if(!next||next===parentHit)break;parentHit=next;}
      if(parentHit!==owner&&!owner.contains(parentHit))throw Error('Parent iframe is covered or clipped.');
      w=w.parent;}
    el.focus();return {matches:[details],x:topX,y:topY,trustedPoint};
  }
  const fire=type=>el.dispatchEvent(new Event(type,{bubbles:true,composed:true}));
  if(operation==='click'){el.click();}
  else if(operation==='fill'){
    if(!['INPUT','TEXTAREA'].includes(el.tagName)&&!el.isContentEditable)throw Error('Element is not editable.');
    if(el.readOnly)throw Error('Element is readonly.');
    if(el.isContentEditable)el.textContent=payload;else{const proto=el.tagName==='TEXTAREA'?HTMLTextAreaElement.prototype:HTMLInputElement.prototype;Object.getOwnPropertyDescriptor(proto,'value').set.call(el,payload);}fire('input');fire('change');
  }else if(operation==='select'){
    if(el.tagName!=='SELECT')throw Error('Element is not a select.');const choices=[...el.options].filter(o=>o.value===payload||norm(o.text)===norm(payload));
    if(choices.length!==1)throw Error('Select option is missing or ambiguous.');el.value=choices[0].value;fire('input');fire('change');
  }else if(operation==='check'){
    if(!['checkbox','radio'].includes(el.type))throw Error('Element is not checkable.');if(el.checked!==payload)el.click();if(el.checked!==payload)throw Error('Checked state did not change.');
  }else throw Error('Unsupported DOM action.');
  return {matches:[describe(el)]};
}

async function qaFrames(tabId, operation, locator, payload) {
  const results=await chrome.scripting.executeScript({target:{tabId,allFrames:true},func:qaDom,args:[operation,locator||{},payload??null]});
  return results.filter(r=>r.result && !r.result.frameSkipped).map(r=>({frameId:r.frameId,...r.result}));
}
async function qaWait(check,timeoutMs,deadline,label){
  const until=Math.min(Date.now()+timeoutMs,deadline);let last='condition not met';
  while(Date.now()<until){try{const value=await check();if(value)return value;}catch(e){last=e.message||String(e);}await new Promise(r=>setTimeout(r,75));}
  throw Error(label+': '+last);
}
async function qaResolve(tabId,locator,timeout,deadline){
  let previous=null;
  return qaWait(async()=>{
    const frames=await qaFrames(tabId,'inspect',locator);
    const hits=frames.flatMap(f=>f.matches.map(m=>({frameId:f.frameId,...m})));
    if(hits.length!==1)throw Error('Strict locator resolved '+hits.length+' elements.');
    const hit=hits[0];if(!hit.visible||!hit.enabled)throw Error('Element is hidden or disabled.');
    const rect=JSON.stringify(hit.rect);if(previous!==rect){previous=rect;return false;}
    const prepared=await runInPage(tabId,qaDom,['prepare',locator,null],hit.frameId);
    if(!prepared.trustedPoint)throw Error('Cross-origin frame coordinates cannot be verified; interaction is unsupported instead of bypassing parent-frame actionability.');
    return {...hit,prepared};
  },timeout,deadline,'Locator/actionability timeout');
}
async function qaExpect(tabId,expect,fallback,timeout,deadline){
  return qaWait(async()=>{
    if(expect.kind==='url'){const health=await runInPage(tabId,qaDom,['health',{},null]);if(health.url!==expect.value)throw Error('URL was '+health.url);return health.readyState==='complete';}
    const hits=(await qaFrames(tabId,'inspect',expect.locator||fallback)).flatMap(f=>f.matches);
    if(expect.kind==='count'){if(hits.length!==expect.value)throw Error('Count was '+hits.length);return true;}
    if(hits.some(hit=>hit.visibilityKnown===false))throw Error('Cross-origin parent-frame visibility cannot be verified for this assertion.');
    if(expect.kind==='hidden'){if(hits.some(h=>h.visible))throw Error('Element is still visible');return true;}
    if(hits.length!==1)throw Error('Assertion locator resolved '+hits.length+' elements.');
    const hit=hits[0];const actual=expect.kind==='visible'?hit.visible:hit[expect.kind];
    if(expect.kind==='text'&&!hit.visible)throw Error('Expected text is not visible.');
    const wanted=expect.kind==='visible'?true:expect.value;
    if(actual!==wanted)throw Error('Expected '+JSON.stringify(wanted)+'; observed '+JSON.stringify(actual));return true;
  },timeout,deadline,'Postcondition timeout');
}
async function qaScreenshot(tabId,fullPage){
  await chrome.tabs.update(tabId,{active:true});
  await debuggerSend(tabId,'Page.bringToFront');
  const metrics=await debuggerSend(tabId,'Page.getLayoutMetrics');
  const size=fullPage?(metrics.cssContentSize||metrics.contentSize):(metrics.cssVisualViewport||metrics.visualViewport);
  const params={format:'png',captureBeyondViewport:!!fullPage};
  if(fullPage){const width=Math.ceil(size.width),height=Math.ceil(size.height);if(width*height>32000000||height>16384)throw Error('Full-page screenshot exceeds bounded capture size.');params.clip={x:0,y:0,width,height,scale:1};}
  let timer;
  const data=(await Promise.race([debuggerSend(tabId,'Page.captureScreenshot',params),new Promise((_,reject)=>{
    timer=setTimeout(()=>reject(Error('Screenshot capture timed out after 10 seconds.')),10000);
  })]).finally(()=>clearTimeout(timer))).data;
  if(typeof data!=='string'||Math.ceil(data.length*3/4)>4*1024*1024)throw Error('Screenshot exceeds the 4 MiB artifact limit; choose a smaller viewport or disable fullPage.');
  return data;
}
async function runQa(raw,session){
  const spec=qaValidate(raw),timeout=spec.timeoutMs||15000,deadline=Date.now()+90000;
  const result={schemaVersion:1,passed:false,url:spec.url,steps:[],snapshots:[],errors:[],visualReview:{required:spec.requireVisualReview!==false,status:spec.requireVisualReview===false?'not_required':'external_review_required',referenceId:spec.referenceId||null}};
  let tab,capturedBytes=0;
  try{
    tab=await chrome.tabs.create({url:'about:blank',active:true});result.tabId=tab.id;
    await ensureGroup(tab.id,session);const state=await ensureAttached(tab.id);state.preserveLogs=true;
    for(const viewport of spec.viewports){
      const snapshot={name:viewport.name,width:viewport.width,height:viewport.height,passed:false,identityPassed:false,steps:[]};let scenarioPassed=true;
      state.console=[];state.network.clear();state.netOrder=[];state.consoleDropped=0;state.networkDropped=0;
      try{
        await resize({tabId:tab.id,width:viewport.width,height:viewport.height,mobile:viewport.mobile===true,deviceScaleFactor:1},session);
        await chrome.tabs.update(tab.id,{url:spec.url});
        const expected=new URL(spec.expectedUrl||spec.url,spec.url).href;
        await qaWait(async()=>{const h=await runInPage(tab.id,qaDom,['health',{},null]);if(h.url!==expected)throw Error('Unexpected URL '+h.url);if(h.readyState!=='complete'||!h.domPresent)return false;return h;},timeout,deadline,'Page readiness timeout');
        if(spec.ready)await qaExpect(tab.id,{kind:'visible',locator:spec.ready},null,timeout,deadline);
        snapshot.identityPassed=true;
        for(let index=0;index<spec.steps.length;index++){
          const step=spec.steps[index],record={viewport:viewport.name,index,action:step.action,passed:false};result.steps.push(record);snapshot.steps.push(record);
          try{
            if(step.action!=='assert'){
              const hit=await qaResolve(tab.id,step.locator,timeout,deadline);
              const prep=hit.prepared;
              if(step.action==='press')await pressKey(tab.id,step.key);
              else if(step.action==='click'&&prep.trustedPoint){await mouseClick(tab.id,prep.x,prep.y,'left',1,0);record.inputMethod='cdp';}
              else{await runInPage(tab.id,qaDom,[step.action,step.locator,step.value??null],hit.frameId);record.inputMethod='dom';}
            }
            if(step.expect)await qaExpect(tab.id,step.expect,step.locator,timeout,deadline);
            record.passed=true;
          }catch(e){record.error=String(e.message||e);throw Error('Step '+index+': '+record.error);}
        }
        // Two animation frames plus a bounded quiet window capture deferred action errors/requests.
        let quietSince=Date.now(),lastCount=-1;
        await qaWait(async()=>{
          const requests=[...state.network.values()];
          if(requests.length!==lastCount||requests.some(r=>!r.finished&&!['WebSocket','EventSource'].includes(r.type))){lastCount=requests.length;quietSince=Date.now();return false;}
          return Date.now()-quietSince>=300;
        },Math.min(timeout,5000),deadline,'Network did not settle after interaction');
      }catch(e){scenarioPassed=false;result.errors.push(viewport.name+': '+String(e.message||e));}
      try{
        Object.assign(snapshot,await runInPage(tab.id,qaDom,['health',{},null]));
        const image=await qaScreenshot(tab.id,spec.fullPage!==false);
        const imageBytes=Math.floor(image.length*3/4)-(image.endsWith('==')?2:image.endsWith('=')?1:0);
        if(capturedBytes+imageBytes>4*1024*1024)throw Error('QA screenshots exceed the aggregate 4 MiB transport budget. Reduce viewport count/size or disable fullPage.');
        capturedBytes+=imageBytes;snapshot.image=image;
        snapshot.capturedAt=new Date().toISOString();
      }catch(e){scenarioPassed=false;result.errors.push(viewport.name+': observation failed: '+String(e.message||e));}
      snapshot.consoleErrors=state.console.filter(e=>e.level==='error'||e.level==='assert').map(e=>({...e,text:String(e.text).slice(0,3000)}));
      snapshot.consoleWarnings=state.console.filter(e=>e.level==='warn'||e.level==='warning').map(e=>({...e,text:String(e.text).slice(0,3000)}));
      snapshot.networkFailures=[...state.network.values()].filter(e=>e.failed||e.status>=400).map(e=>({...e,url:String(e.url).slice(0,2000)}));
      snapshot.captureComplete=!state.consoleDropped&&!state.networkDropped;
      if(!snapshot.captureComplete){scenarioPassed=false;result.errors.push(viewport.name+': health capture exceeded bounded buffers ('+state.consoleDropped+' console, '+state.networkDropped+' network records dropped).');}
      snapshot.passed=scenarioPassed&&snapshot.domPresent===true&&snapshot.observedWidth===viewport.width&&snapshot.observedHeight===viewport.height&&
        !snapshot.frameworkOverlay&&!snapshot.overflow?.horizontal&&!snapshot.overflow?.clipped?.length&&!snapshot.consoleErrors.length&&!snapshot.consoleWarnings.length&&!snapshot.networkFailures.length&&!!snapshot.image;
      result.snapshots.push(snapshot);
      if(!snapshot.passed&&!result.errors.some(e=>e.startsWith(viewport.name+':')))result.errors.push(viewport.name+': rendered health, viewport or screenshot checks failed.');
      if(Date.now()>=deadline)break;
    }
    result.passed=result.snapshots.length===spec.viewports.length&&result.snapshots.every(s=>s.passed)&&result.errors.length===0;
  }catch(e){result.errors.push(String(e.message||e));}
  finally{if(tab){try{await chrome.tabs.remove(tab.id);tabOwners.delete(tab.id);await persistSessions();result.cleanedUp=true;}catch(e){result.cleanedUp=false;result.passed=false;result.errors.push('QA cleanup failed: '+String(e.message||e));}}}
  return result;
}
