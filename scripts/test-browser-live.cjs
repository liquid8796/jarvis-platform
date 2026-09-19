#!/usr/bin/env node
'use strict';
// Real production BrowserService RPC -> vendor tools -> shipping MV3 extension
// -> chrome.debugger/scripting -> isolated Chromium. Only native-Port transport
// is adapted in a temporary copied extension; no Playwright actions or stubs.
const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const net = require('node:net');
const crypto = require('node:crypto');
const assert = require('node:assert/strict');
const {spawn} = require('node:child_process');

const repo = path.resolve(__dirname, '..');
const args = process.argv.slice(2);
const option = name => { const at=args.indexOf(name); return at<0 ? undefined : args[at+1]; };
const smokeOnly = args.includes('--smoke');
const runId = new Date().toISOString().replace(/[:.]/g,'-') + '-' + crypto.randomBytes(4).toString('hex');
const outputRoot = path.resolve(option('--output') || path.join(repo,'artifacts','browser-live',runId));
fs.mkdirSync(outputRoot,{recursive:true});
const tempRoot = fs.mkdtempSync(path.join(outputRoot,'.runtime-'));
const profile = path.join(tempRoot,'profile');
const extension = path.join(tempRoot,'extension');
const servicePipe = 'JarvisLiveService-' + crypto.randomUUID();
const extensionPipe = 'JarvisLiveExtension-' + crypto.randomUUID();
const session = 'js_' + crypto.randomBytes(16).toString('hex');
const secondSession = 'js_' + crypto.randomBytes(16).toString('hex');
const token = crypto.randomBytes(24).toString('hex');
const report = {schemaVersion:1,runId,startedAt:new Date().toISOString(),status:'running',
  coverage:{productionSessionBrowserToolSet:true,productionBrowserRuntimeClient:true,productionBrowserServiceRpc:true,productionVendorTools:true,shippingExtensionCode:true,
    realChromium:true,playwrightPageAutomation:false,nativeMessagingRegistryAndExecutable:false,
    fullAgentMcpAndTaskCompletion:false,nativePortTransport:'test-only loopback adapter'},
  cases:[],artifacts:[],versions:{},cleanup:{temporaryRuntime:tempRoot}};
const children=[];
let webServer, extensionSocket, rpcSocket, pendingPoll;
const requestQueue=[], pendingRpc=new Map();
let sequence=0;
let interrupted=false;
function interrupt(reason) {
  interrupted=true;
  for(const pending of pendingRpc.values())pending.reject(Error(reason));
  pendingRpc.clear();
}
process.once('SIGINT',()=>interrupt('Live harness interrupted'));
const watchdog=setTimeout(()=>interrupt('Live harness exceeded 10 minute deadline'),600000);
watchdog.unref();
fs.mkdirSync(outputRoot,{recursive:true});
const sha = bytes=>crypto.createHash('sha256').update(bytes).digest('hex');
const delay = ms=>new Promise(resolve=>setTimeout(resolve,ms));
function ownedChild(command,argv,name) {
  const child=spawn(command,argv,{cwd:repo,windowsHide:true,stdio:['ignore','pipe','pipe']});
  children.push({child,name});
  const log=fs.createWriteStream(path.join(outputRoot,name+'.log'));
  child.stdout.pipe(log,{end:false});child.stderr.pipe(log,{end:false});
  child.once('close',()=>log.end());
  child.once('error',error=>log.write(String(error)));
  return child;
}
async function waitChild(child,timeoutMs=180000) {
  return await new Promise((resolve,reject)=>{
    const timer=setTimeout(()=>reject(Error('Child process timeout')),timeoutMs);
    child.once('error',error=>{clearTimeout(timer);reject(error);});
    child.once('exit',code=>{clearTimeout(timer);code===0?resolve():reject(Error('Child process exited '+code));});
  });
}
function findBrowser() {
  if(option('--browser')) return path.resolve(option('--browser'));
  const base=path.join(process.env.LOCALAPPDATA || '', 'ms-playwright');
  const candidates=fs.existsSync(base)?fs.readdirSync(base).filter(x=>/^chromium-\d+$/.test(x))
    .sort((a,b)=>Number(b.split('-')[1])-Number(a.split('-')[1]))
    .map(x=>path.join(base,x,'chrome-win64','chrome.exe')):[];
  return candidates.find(fs.existsSync);
}
async function connectPipe(name,timeoutMs=20000) {
  const until=Date.now()+timeoutMs;
  while(Date.now()<until) {
    try { return await new Promise((resolve,reject)=>{
      const socket=net.connect('\\\\.\\pipe\\'+name);
      socket.once('connect',()=>{socket.removeListener('error',reject);socket.on('error',()=>{});resolve(socket);});
      socket.once('error',reject);
    }); } catch { await delay(100); }
  }
  throw Error('Named pipe did not become ready: '+name);
}
function lines(socket,receive) {
  let buffer='';socket.setEncoding('utf8');
  socket.on('data',chunk=>{buffer+=chunk;for(;;){const at=buffer.indexOf('\n');if(at<0)break;
    const line=buffer.slice(0,at).replace(/^\uFEFF/,'');buffer=buffer.slice(at+1);if(line.trim())receive(JSON.parse(line));}});
}
function json(response,status,value) {
  response.writeHead(status,{'Content-Type':'application/json','Access-Control-Allow-Origin':'*','Cache-Control':'no-store'});
  response.end(JSON.stringify(value));
}
async function startRelay() {
  webServer=http.createServer(async(request,response)=>{
    const url=new URL(request.url,'http://127.0.0.1');
    if(url.pathname==='/relay/'+token+'/trace' && request.method==='POST') {
      let body='';for await(const chunk of request){body+=chunk;if(body.length>20000){response.destroy();return;}}
      fs.appendFileSync(path.join(outputRoot,'extension-trace.jsonl'),body+'\n');json(response,200,{ok:true});return;
    }
    if(url.pathname==='/relay/'+token+'/post' && request.method==='POST') {
      let body='';for await(const chunk of request){body+=chunk;if(body.length>64000000){response.destroy();return;}}
      const message=JSON.parse(body);
      if(message.event==='ready')report.versions.extensionReady=message;
      extensionSocket.write(JSON.stringify(message)+'\n');json(response,200,{ok:true});return;
    }
    if(url.pathname==='/relay/'+token+'/next') {
      if(requestQueue.length){json(response,200,requestQueue.shift());return;}
      if(pendingPoll)json(pendingPoll,200,null);
      pendingPoll=response;
      const timer=setTimeout(()=>{if(pendingPoll===response){pendingPoll=undefined;json(response,200,null);}},1000);
      response.once('close',()=>{clearTimeout(timer);if(pendingPoll===response)pendingPoll=undefined;});return;
    }
    if(url.pathname==='/api/failure'){response.writeHead(500,{'Content-Type':'text/plain'});response.end('intentional fixture failure');return;}
    if(url.pathname==='/favicon.ico'){response.writeHead(204);response.end();return;}
    if(url.pathname==='/frame'){response.writeHead(200,{'Content-Type':'text/html'});response.end('<button id="frame-action" onclick="this.textContent=\'Frame done\'">Frame action</button>');return;}
    if(url.pathname==='/fixture'){response.writeHead(200,{'Content-Type':'text/html','Cache-Control':'no-store'});response.end(fs.readFileSync(path.join(repo,'tests/browser-live/fixture.html')));return;}
    response.writeHead(404);response.end('Not found');
  });
  await new Promise(resolve=>webServer.listen(0,'127.0.0.1',resolve));
  const origin='http://127.0.0.1:'+webServer.address().port;
  report.fixtureOrigin=origin;
  lines(extensionSocket,message=>{if(pendingPoll){const response=pendingPoll;pendingPoll=undefined;json(response,200,message);}else requestQueue.push(message);});
  return origin;
}
function rpc(message,timeoutMs=120000) {
  const id=String(++sequence);
  return new Promise((resolve,reject)=>{
    const timer=setTimeout(()=>{pendingRpc.delete(id);reject(Error('Browser RPC timeout: '+message.kind+' '+(message.request?.toolId||'')));},timeoutMs);
    pendingRpc.set(id,{resolve:result=>{clearTimeout(timer);resolve(result);},reject:error=>{clearTimeout(timer);reject(error);}});
    rpcSocket.write(JSON.stringify({...message,id})+'\n');
  });
}
async function tool(toolId,arguments_,identity=session,browserFamily='extension') {
  if(toolId==='browser.qa')arguments_={spec:arguments_};
  const result=await rpc({kind:'execute',request:{toolId,arguments:arguments_,context:{callId:'live-'+sequence,
    applicationSessionId:identity,isolationScopeId:identity,workingDirectory:outputRoot,
    additionalDirectories:[],fullPermission:true,browserFamily}}});
  if(!result.ok)throw Error(result.error);
  if(result.handshake)report.versions.productionHandshake=result.handshake;
  return result.reply;
}
async function runCase(name,body) {
  const started=Date.now();process.stdout.write('RUN '+name+'\n');
  try {const detail=await body();report.cases.push({name,passed:true,elapsedMs:Date.now()-started,...detail});process.stdout.write('PASS '+name+'\n');}
  catch(error){report.cases.push({name,passed:false,elapsedMs:Date.now()-started,error:error.stack||String(error)});process.stdout.write('FAIL '+name+': '+error.message+'\n');
    if(interrupted||/Browser RPC timeout|browser did not answer in time/i.test(error.message))throw error;}
  fs.writeFileSync(path.join(outputRoot,'results.json'),JSON.stringify(report,null,2));
}
function readQa(reply) {
  let data;try{data=JSON.parse(reply.text);}catch{throw Error('browser.qa did not return structured JSON: '+reply.text.slice(0,500));}
  assert.equal(data.schemaVersion,1);
  const file=path.join(outputRoot,'qa-'+String(sequence).padStart(3,'0')+'.json');
  fs.writeFileSync(file,JSON.stringify(data,null,2));(report.qaReports??=[]).push(file);
  for(const snapshot of data.snapshots||[]){
    if(snapshot.userAgent)report.versions.browserUserAgent=snapshot.userAgent;
    if(snapshot.artifactPath)inspectScreenshot(snapshot);
  }
  return data;
}
function inspectScreenshot(snapshot) {
  assert.ok(snapshot.artifactPath,'snapshot must carry an artifactPath');
  const target=path.resolve(snapshot.artifactPath);
  assert.ok(target.startsWith(outputRoot+path.sep),'Screenshot must be inside this run artifact root');
  const bytes=fs.readFileSync(target);assert.deepEqual(bytes.subarray(0,8),Buffer.from([137,80,78,71,13,10,26,10]));
  assert.ok(bytes.length>1000,'Screenshot must contain encoded image data');
  assert.equal(sha(bytes).toLowerCase(),snapshot.screenshotSha256.toLowerCase());
  const width=bytes.readUInt32BE(16),height=bytes.readUInt32BE(20);assert.ok(width>0&&height>0);
  const record={path:target,sha256:sha(bytes),width,height,bytes:bytes.length};
  if(!report.artifacts.some(item=>item.path===target))report.artifacts.push(record);return record;
}
async function acceptance(origin) {
  const viewports=[{name:'desktop',width:1280,height:800},{name:'mobile',width:390,height:844}];
  const config=(fixtureCase,steps,extra={})=>({url:origin+'/fixture?case='+fixtureCase,
    expectedUrl:origin+'/fixture?case='+fixtureCase,ready:{testId:'fixture-title'},
    viewports:[viewports[0]],steps,timeoutMs:2500,fullPage:true,...extra});
  const click=(css,expect={kind:'visible',locator:{css}})=>({action:'click',locator:{css},expect});
  const text=(css,value)=>({kind:'text',locator:{css},value});
  const readFixture=async(tabId,identity=session,family='extension')=>{
    const until=Date.now()+5000;let reply;
    do {reply=await tool('browser.read_page',{tabId},identity,family);
      if(!reply.isError&&reply.text.includes('Jarvis browser QA fixture'))return reply;
      await delay(100);
    } while(Date.now()<until);
    throw Error('Fixture DOM did not become ready: '+reply?.text);
  };
  await runCase('healthy CTA + benign Vite style + real desktop/mobile PNGs',async()=>{
    const qa=readQa(await tool('browser.qa',config('healthy',[click('#save',text('#saved','Saved'))],{viewports})));
    assert.equal(qa.passed,true,JSON.stringify(qa));assert.equal(qa.snapshots.length,2);
    for(const snapshot of qa.snapshots){assert.equal(snapshot.frameworkOverlay,false);inspectScreenshot(snapshot);}
    return {qa};
  });
  await runCase('broken CTA fails expected outcome',async()=>{
    const qa=readQa(await tool('browser.qa',config('broken',[click('#broken',text('#broken-status','Saved'))])));
    assert.equal(qa.passed,false);assert.ok(qa.steps.some(step=>!step.passed));return {qa};
  });
  await runCase('console error boom after click is captured without keywords',async()=>{
    const qa=readQa(await tool('browser.qa',config('console',[click('#console-boom')])));
    assert.equal(qa.passed,false);assert.ok(qa.snapshots.some(s=>s.consoleErrors.some(e=>JSON.stringify(e).includes('boom'))));return {qa};
  });
  await runCase('HTTP 500 after click fails scenario health',async()=>{
    const qa=readQa(await tool('browser.qa',config('network',[click('#request-failure')])));
    assert.equal(qa.passed,false);assert.ok(qa.snapshots.some(s=>s.networkFailures.some(e=>JSON.stringify(e).includes('500'))));return {qa};
  });
  await runCase('delayed hydration waits then interacts',async()=>{
    const qa=readQa(await tool('browser.qa',config('hydration',[click('#late',text('#late','Hydrated'))],{ready:{css:'#late'}})));
    assert.equal(qa.passed,true,JSON.stringify(qa));return {qa};
  });
  await runCase('mobile clipped control fails despite no document overflow',async()=>{
    const qa=readQa(await tool('browser.qa',config('clipping',[click('#save',text('#saved','Saved')),{action:'assert',expect:{kind:'visible',locator:{css:'#clipped'}}}],{viewports:[viewports[1]]})));
    assert.equal(qa.passed,false);assert.ok(qa.snapshots.some(s=>!s.overflow.horizontal&&s.overflow.clipped.length>0));return {qa};
  });
  await runCase('obstructed control produces failed interaction diagnostics',async()=>{
    const qa=readQa(await tool('browser.qa',config('obstructed',[click('#obstructed')])));
    assert.equal(qa.passed,false);assert.ok(qa.steps.some(s=>!s.passed));return {qa};
  });
  await runCase('disabled control cannot pass a click',async()=>{
    const qa=readQa(await tool('browser.qa',config('disabled',[click('#disabled')])));
    assert.equal(qa.passed,false);assert.ok(qa.steps.some(s=>!s.passed));return {qa};
  });
  await runCase('open shadow root semantic action',async()=>{
    const qa=readQa(await tool('browser.qa',config('shadow',[click('[data-testid="shadow-action"]',text('#shadow-result','Shadow done'))])));
    assert.equal(qa.passed,true,JSON.stringify(qa));return {qa};
  });
  await runCase('same-origin iframe trusted input and postcondition',async()=>{
    const locator={css:'#frame-action',frame:'qa-frame'};
    const qa=readQa(await tool('browser.qa',config('iframe',[{action:'click',locator,expect:{kind:'text',locator,value:'Frame done'}}],{ready:locator})));
    assert.equal(qa.passed,true,JSON.stringify(qa));assert.equal(qa.steps[0].inputMethod,'cdp');return {qa};
  });
  await runCase('hidden same-origin parent makes inner element visible assertion fail',async()=>{
    const locator={css:'#frame-action',frame:'qa-frame'};
    const qa=readQa(await tool('browser.qa',config('hidden-frame',[click('#save',text('#saved','Saved')),{action:'assert',expect:{kind:'visible',locator}}])));
    assert.equal(qa.passed,false);assert.equal(qa.steps[0].passed,true);assert.equal(qa.steps[1].passed,false);return {qa};
  });
  await runCase('covered cross-origin frame cannot bypass parent using DOM click',async()=>{
    const locator={css:'#frame-action',frame:'qa-frame'};
    const qa=readQa(await tool('browser.qa',config('cross-frame',[{action:'click',locator,expect:{kind:'text',locator,value:'Frame done'}}])));
    assert.equal(qa.passed,false);assert.ok(qa.steps.some(step=>!step.passed));return {qa};
  });
  await runCase('visual reference requires external review',async()=>{
    const qa=readQa(await tool('browser.qa',config('healthy',[click('#save',text('#saved','Saved'))],{requireVisualReview:true,referenceId:'fixture-design'})));
    assert.equal(qa.visualReview.status,'external_review_required');return {qa};
  });
  await runCase('application sessions cannot read each others real browser tabs',async()=>{
    const created=await tool('browser.tabs_create_mcp',{});assert.equal(created.isError,false,created.text);
    const match=created.text.match(/tab\s+(\d+)/i);assert.ok(match,created.text);const tabId=Number(match[1]);
    const navigation=await tool('browser.navigate',{tabId,url:origin+'/fixture?case=healthy'});assert.equal(navigation.isError,false,navigation.text);
    await readFixture(tabId);
    const foreign=await tool('browser.read_page',{tabId},secondSession);
    assert.equal(foreign.isError,true,'Foreign session must be rejected');
    const owner=await readFixture(tabId);assert.equal(owner.isError,false,owner.text);
    return {tabId,foreignError:foreign.text};
  });
  await runCase('tab references cannot cross real tabs and remain independently usable',async()=>{
    const create=async()=>{
      const reply=await tool('browser.tabs_create_mcp',{});assert.equal(reply.isError,false,reply.text);
      const match=reply.text.match(/tab\s+(\d+)/i);assert.ok(match,reply.text);return Number(match[1]);
    };
    const [a,b]=[await create(),await create()];
    for(const tabId of [a,b]){
      const reply=await tool('browser.navigate',{tabId,url:origin+'/fixture?case=healthy'});assert.equal(reply.isError,false,reply.text);
    }
    let observation;
    for(let n=0;n<20;n++){
      observation=await tool('browser.read_page',{tabId:a});
      if(/\bref_[A-Za-z0-9_-]+\b/.test(observation.text))break;await delay(100);
    }
    const ref=observation.text.match(/\bref_[A-Za-z0-9_-]+\b/)?.[0];assert.ok(ref,observation.text);
    const other=await tool('browser.read_page',{tabId:b});assert.equal(other.isError,false,other.text);
    await assert.rejects(()=>tool('browser.computer',{tabId:b,action:'hover',ref}),/stale|reference/i);
    const same=await tool('browser.computer',{tabId:a,action:'hover',ref});assert.equal(same.isError,false,same.text);
    return {tabA:a,tabB:b,reference:ref};
  });
  await runCase('URL-less auto call remembers browser family bound to tab',async()=>{
    const created=await tool('browser.tabs_create_mcp',{});const tabId=Number(created.text.match(/tab\s+(\d+)/i)?.[1]);assert.ok(tabId);
    const navigation=await tool('browser.navigate',{tabId,url:origin+'/fixture?case=healthy'});assert.equal(navigation.isError,false,navigation.text);
    const reading=await readFixture(tabId,session,'auto');assert.equal(reading.isError,false,reading.text);
    return {tabId};
  });
}
async function cleanup() {
  if(rpcSocket&&!rpcSocket.destroyed)for(const id of [session,secondSession]){
    try{await rpc({kind:'endSession',sessionId:id,close:true},5000);}catch{}
  }
  rpcSocket?.destroy();extensionSocket?.destroy();
  if(pendingPoll){json(pendingPoll,503,{stopped:true});pendingPoll=undefined;}
  if(webServer){webServer.closeAllConnections();await new Promise(resolve=>webServer.close(resolve));}
  for(const {child,name} of children.slice().reverse())if(child.exitCode===null&&child.pid){
    // The PID is only ever taken from a child spawned by this harness.
    const exitCode=await new Promise(resolve=>{
      const killer=spawn('taskkill.exe',['/PID',String(child.pid),'/T','/F'],{windowsHide:true,stdio:'ignore'});
      killer.once('error',resolve);killer.once('exit',resolve);
    });report.cleanup[name+'Pid']=child.pid;report.cleanup[name+'StopExitCode']=exitCode;
  }
  await delay(500);
  // Deletion target is exactly the test-owned mkdtemp directory inside this run.
  const resolved=fs.realpathSync(tempRoot),base=fs.realpathSync(outputRoot);
  assert.ok(resolved.startsWith(base+path.sep)&&path.basename(resolved).startsWith('.runtime-'));
  try{fs.rmSync(resolved,{recursive:true,force:true,maxRetries:10,retryDelay:200});report.cleanup.temporaryProfileRemoved=true;}
  catch(error){report.cleanup.temporaryProfileRemoved=false;report.cleanup.error=String(error);}
}
(async()=>{
  try {
    if(process.platform!=='win32')throw Error('This production BrowserService harness requires Windows');
    const browser=findBrowser();if(!browser||!fs.existsSync(browser))throw Error('No extension-capable Chromium found; pass --browser to Chrome for Testing/Chromium. No download or user-browser mutation was attempted.');
    report.versions.browserExecutable=browser;
    report.versions.node=process.version;
    const shipping=path.join(repo,'jarvis-agent/src/Jarvis.Agent.Windows/Assets/Browser');
    const background=fs.readFileSync(path.join(shipping,'background.js'));
    report.versions.shippingBackgroundSha256=sha(background);
    report.versions.shippingQaSha256=fs.existsSync(path.join(shipping,'qa.js'))?sha(fs.readFileSync(path.join(shipping,'qa.js'))):null;
    report.versions.sourceVersion=fs.readFileSync(path.join(repo,'VERSION'),'utf8').trim();
    const project=path.join(repo,'tests/Jarvis.Browser.LiveHarness/Jarvis.Browser.LiveHarness.csproj');
    const dll=path.join(repo,'tests/Jarvis.Browser.LiveHarness/bin/Debug/net10.0-windows/Jarvis.Browser.LiveHarness.dll');
    if(!args.includes('--no-build'))await waitChild(ownedChild('dotnet',['build',project,'-c','Debug','--nologo','-v','quiet'],'build'));
    if(!fs.existsSync(dll))throw Error('Harness executable missing; run without --no-build');
    ownedChild('dotnet',[dll,servicePipe,extensionPipe,outputRoot,String(process.pid)],'service');
    [extensionSocket,rpcSocket]=await Promise.all([connectPipe(extensionPipe),connectPipe(servicePipe+'-front')]);
    lines(rpcSocket,message=>{const pending=pendingRpc.get(message.id);if(pending){pendingRpc.delete(message.id);pending.resolve(message);}});
    report.versions.handshake=await rpc({kind:'hello',protocolVersion:1});assert.equal(report.versions.handshake.ok,true);
    const origin=await startRelay();
    fs.cpSync(shipping,extension,{recursive:true});
    const manifest=JSON.parse(fs.readFileSync(path.join(extension,'manifest.json'),'utf8'));
    report.versions.extensionManifest=manifest.version;manifest.background.service_worker='test-native-port-bootstrap.js';
    fs.writeFileSync(path.join(extension,'manifest.json'),JSON.stringify(manifest,null,2));
    const adapter=fs.readFileSync(path.join(repo,'tests/browser-live/native-port-bootstrap.js'),'utf8')
      .replace('__JARVIS_TEST_RELAY__',JSON.stringify(origin+'/relay/'+token));
    fs.writeFileSync(path.join(extension,'test-native-port-bootstrap.js'),adapter);
    assert.equal(sha(fs.readFileSync(path.join(extension,'background.js'))),report.versions.shippingBackgroundSha256);
    ownedChild(browser,['--headless=new','--user-data-dir='+profile,'--disable-extensions-except='+extension,
      '--load-extension='+extension,'--no-first-run','--no-default-browser-check','--disable-background-networking',
      '--disable-component-update','--disable-sync','--window-size=1280,800','about:blank'],'chromium');
    const until=Date.now()+20000;
    while(!report.versions.extensionReady&&Date.now()<until)await delay(100);
    assert.ok(report.versions.extensionReady,'Real shipping extension did not connect; see chromium.log. No stub fallback is used.');
    await runCase('real extension bootstrap through production BrowserService RPC',async()=>{
      const reply=await tool('browser.list_connected_browsers',{});assert.equal(reply.isError,false,reply.text);return {reply:reply.text};
    });
    if(!smokeOnly)await acceptance(origin);
    report.status=report.cases.every(item=>item.passed)?'passed':'failed';
  } catch(error){report.status='failed';report.error=error.stack||String(error);process.stderr.write(String(error)+'\n');}
  finally {
    try{await cleanup();}catch(error){report.cleanup.error=String(error);report.status='failed';}
    report.finishedAt=new Date().toISOString();
    clearTimeout(watchdog);
    fs.writeFileSync(path.join(outputRoot,'results.json'),JSON.stringify(report,null,2));
    process.stdout.write('Results: '+path.join(outputRoot,'results.json')+'\n');
    process.exitCode=report.status==='passed'?0:1;
  }
})();
