// Contract/orchestration tests; live rendering is covered separately by the opt-in browser harness.
const {test}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');
const vm=require('node:vm');
const source=fs.readFileSync(path.join(__dirname,'../jarvis-agent/src/Jarvis.Agent.Windows/Assets/Browser/qa.js'),'utf8');
function spec(){return {url:'http://localhost:4173/',viewports:[{name:'desktop',width:1440,height:900}],timeoutMs:1000,
  steps:[{action:'click',locator:{role:'button',name:'Save'},expect:{kind:'text',locator:{testId:'status'},value:'Saved'}}]};}
function runtime(options={}){
  const events=[],state={console:[],network:new Map(),netOrder:[]};let viewport;
  const context=vm.createContext({URL,Date,Map,Set,console,setTimeout,clearTimeout,
    chrome:{tabs:{async create(){events.push('create');return{id:7};},async update(){events.push('navigate');return{id:7};},async remove(){events.push('close');}},
      scripting:{async executeScript(){return[{frameId:0,result:{matches:[{visible:true,enabled:true,rect:{x:1,y:1,width:50,height:20},text:'Saved'}]}}];}}},
    ensureGroup:async()=>events.push('own'),ensureAttached:async()=>{events.push('attach');return state;},
    resize:async value=>{viewport=value;events.push('resize');},persistSessions:async()=>{},tabOwners:new Map([[7,'session']]),
    runInPage:async(_id,_fn,args)=>args[0]==='prepare'?{trustedPoint:true,x:26,y:11}:args[0]==='health'?{
      url:'http://localhost:4173/',title:'Fixture',readyState:'complete',domPresent:true,observedWidth:viewport.width,observedHeight:viewport.height,
      overflow:{horizontal:!!options.overflow,vertical:false,clipped:[]},frameworkOverlay:false}:null,
    mouseClick:async()=>{events.push('click');if(options.error)state.console.push({level:'error',text:'boom'});if(options.httpFailure)state.network.set('bad',{url:'/save',status:500,finished:true});},
    pressKey:async()=>{},debuggerSend:async(_id,method)=>{events.push(method);return method==='Page.getLayoutMetrics'?{cssContentSize:{width:1440,height:900}}:{data:options.largeImage?'A'.repeat(3*1024*1024):'iVBORw0KGgo='};}
  });
  vm.runInContext(source,context);return{context,events};
}
test('structured QA rejects unbounded or declarative-only scenarios and unknown executable fields',()=>{
  const {context}=runtime();
  for(const mutate of [s=>s.steps=[{action:'assert',expect:{kind:'url',value:s.url}}],s=>s.steps[0].action='javascript',
    s=>s.viewports.push({...s.viewports[0]}),s=>s.code='alert(1)',s=>s.steps[0].locator={},s=>s.timeoutMs=90000]){
    const input=spec();mutate(input);context.input=input;assert.throws(()=>vm.runInContext('qaValidate(input)',context));
  }
});
test('QA attaches before navigation, records real action/postcondition chain and closes only its owned tab',async()=>{
  const {context,events}=runtime();context.input=spec();
  const result=await vm.runInContext('runQa(input,{id:"session"})',context);
  assert.equal(result.passed,true);assert.ok(events.indexOf('attach')<events.indexOf('navigate'));
  assert.equal(result.snapshots[0].identityPassed,true);assert.equal(result.snapshots[0].steps[0].passed,true);
  assert.equal(result.cleanedUp,true);assert.equal(events.at(-1),'close');
  assert.equal(result.visualReview.status,'external_review_required');
});
for(const [name,options,field] of [['arbitrary console errors',{error:true},'consoleErrors'],['HTTP failure after action',{httpFailure:true},'networkFailures'],['responsive overflow',{overflow:true},null]]){
  test('QA fails closed for '+name,async()=>{
    const {context,events}=runtime(options);context.input=spec();const result=await vm.runInContext('runQa(input,{id:"session"})',context);
    assert.equal(result.passed,false);assert.equal(result.snapshots[0].passed,false);assert.equal(events.at(-1),'close');
    if(field)assert.equal(result.snapshots[0][field].length,1);
    if(options.error)assert.equal(result.snapshots[0].consoleErrors[0].text,'boom');
  });
}
test('aggregate screenshot transport limit fails explicitly before returning an oversized response',async()=>{
  const {context}=runtime({largeImage:true});context.input=spec();context.input.viewports.push({name:'mobile',width:390,height:844});
  const result=await vm.runInContext('runQa(input,{id:"session"})',context);
  assert.equal(result.passed,false);assert.ok(result.snapshots[0].image);assert.equal(result.snapshots[1].image,undefined);
  assert.ok(result.errors.some(e=>e.includes('aggregate 4 MiB')));assert.equal(result.cleanedUp,true);
});
