// Records the desktop reference's in-process MCP servers, their tools, tool
// descriptions and input schemas into Captures/Mcp/internal-servers-<build>.json,
// which InternalMcpSurfaceParityTests compares this port against.
//
// The desktop declares nine of the ten servers as plain JSON Schema literals in
// its Electron main bundle, and scheduled-tasks as zod builders passed straight
// to the Agent SDK's tool(); this script reads both forms out of the extracted
// app.asar rather than restating them.
//
//   npx @electron/asar extract "C:/Program Files/WindowsApps/Claude_<ver>_x64__pzs8sxrjxfjjc/app/resources/app.asar" <dir>
//   node gen-internal-servers.js <dir> internal-servers-<ver>.json <ver>
//
// The chunk file names change every release; the anchors here are string
// literals and declaration names, which do not.

const fs=require('fs'),vm=require('vm'),path=require('path'),os=require('os');
const BS=92;
function findExprEnd(d,p){
  let depth=0;
  for(;p<d.length;p++){
    const c=d[p];
    if(c==='`'||c==='"'||c==="'"){const q=c;p++;while(p<d.length){if(d.charCodeAt(p)===BS){p+=2;continue;}if(d[p]===q)break;p++;}continue;}
    if(c==='['||c==='{'||c==='(') depth++;
    else if(c===']'||c==='}'||c===')'){ if(depth===0) return p; depth--; }
    else if((c===','||c===';')&&depth===0) return p;
  }
  return p;
}
function lookup(d,name){
  const SEP=',;{(=) ' + String.fromCharCode(10,9,91,33,38,124,63,58,39);
  const cands=[];
  let from=0;
  for(;;){
    const at=d.indexOf(name+'=',from);
    if(at<0) break;
    from=at+1;
    if(at>0 && SEP.indexOf(d[at-1])<0) continue;
    const start=at+name.length+1;
    if(d[start]==='=') continue;
    const end=findExprEnd(d,start);
    const expr=d.slice(start,end);
    if(expr.length>0) cands.push([at,expr]);
  }
  if(cands.length===0){
    const fk='function '+name+'(';
    let fi=-1, best2=-1;
    for(;;){ fi=d.indexOf(fk,fi+1); if(fi<0) break; if(fi<(global.__ANCHOR__||1e18)) best2=fi; else if(best2<0){best2=fi;break;} }
    if(best2>=0){
      // find body end
      const ob=d.indexOf('{', d.indexOf(')', best2));
      const end=matchForward(d, ob);
      return '('+d.slice(best2, end+1)+')';
    }
    return null;
  }
  const anchor=global.__ANCHOR__||0;
  let best=null;
  for(const c of cands){ if(c[0]<anchor && (best===null||c[0]>best[0])) best=c; }
  if(best) return best[1];
  return cands[0][1];
}
function scanBackArray(d,pos){
  let depth=0;
  for(let p=pos;p>=0;p--){const c=d[p];
    if(c===']'||c==='}'||c===')') depth++;
    else if(c==='['||c==='{'||c==='('){ if(depth===0){ if(c==='[') return p; } else depth--; }}
  return -1;
}
function matchForward(d,start){
  let depth=0;
  for(let p=start;p<d.length;p++){const c=d[p];
    if(c==='`'||c==='"'||c==="'"){const q=c;p++;while(p<d.length){if(d.charCodeAt(p)===BS){p+=2;continue;}if(d[p]===q)break;p++;}continue;}
    if(c==='['||c==='{'||c==='(') depth++;
    else if(c===']'||c==='}'||c===')'){depth--;if(depth===0)return p;}}
  return -1;
}
const TOLERANT=new Proxy(function(){},{get:(t,k)=>k===Symbol.toPrimitive?()=>'?':TOLERANT,apply:()=>TOLERANT,construct:()=>TOLERANT});
evalWith.cache = {};
function evalWith(src,d,label){
  const resolved=evalWith.cache||(evalWith.cache={});
  const seen=(global.__SEEN__=global.__SEEN__||new Set());
  const proxy=new Proxy(resolved,{
    has:(t,k)=>(k in t)||!(k in globalThis),
    get:(t,k)=>{ if(k===Symbol.unscopables) return undefined; if(k in t) return t[k]; seen.add(String(k)); return undefined; }
  });
  for(let round=0;round<60;round++){
    seen.clear();
    let out, threw=null;
    try{ out=vm.runInNewContext('with(scope){ ('+src+') }',{scope:proxy}); }
    catch(e){ threw=e; }
    if(seen.size===0){
      if(threw){ console.error(label,'EVAL FAIL',threw.message); return null; }
      return out;
    }
    let progress=false;
    for(const name of [...seen]){
      if(name in resolved) continue;
      const expr=lookup(d,name);
      if(expr===null){ console.error(label,'UNRESOLVED',name); resolved[name]=TOLERANT; progress=true; continue; }
      let v=evalWith(expr,d,label+'/'+name);
      if(v===null||v===undefined) v=TOLERANT;
      resolved[name]=v; progress=true;
    }
    if(!progress){ if(threw){ console.error(label,'EVAL FAIL(stuck)',threw.message); return null; } return out; }
  }
  console.error(label,'gave up');
  return null;
}

// Generates the reference fixture of the desktop's in-process MCP servers.
const ASAR=process.argv[2], OUT=process.argv[3], BUILD=process.argv[4];
if(!ASAR||!OUT||!BUILD){ console.error('usage: node gen-internal-servers.js <extracted-asar-dir> <out.json> <build>'); process.exit(2); }
const B=path.join(ASAR,'.vite','build');

// Chunk file names change every release, so nothing here names one. A chunk is
// located by a literal it contains; a needle that matches more than one file is
// an error rather than a coin toss.
const CHUNKS=fs.readdirSync(B).filter(f=>f.endsWith('.js')).sort();
const CHUNK_TEXT={};
function chunkText(file){ return CHUNK_TEXT[file] ||= fs.readFileSync(path.join(B,file),'utf8'); }
function chunkFor(needle){
  const hits=CHUNKS.filter(f=>chunkText(f).indexOf(needle)>=0);
  if(hits.length===0) throw new Error('no chunk contains: '+needle);
  if(hits.length>1) throw new Error('needle is ambiguous across '+hits.join(', ')+': '+needle);
  return hits[0];
}
// `var X="ccd_session"` -> the identifier the server object's `serverName:` uses.
function serverIdent(file,name){
  const d=chunkText(file);
  for(const q of ['"',"'",'`']){
    const at=d.indexOf('='+q+name+q);
    if(at<0) continue;
    let p2=at-1;
    while(p2>=0 && /[\w$]/.test(d[p2])) p2--;
    return d.slice(p2+1,at);
  }
  throw new Error('no identifier is assigned '+name+' in '+file);
}
// The tools array of the in-process server whose serverName literal is `name`.
function serverTools(name,preset){
  const file=chunkFor('="'+name+'"');
  const ident=serverIdent(file,name);
  const at=chunkText(file).indexOf('serverName:'+ident+',tools:');
  if(at<0) throw new Error('no serverName:'+ident+',tools: in '+file);
  return run(file, null, null, null, null, preset, chunkText(file).indexOf('[', at));
}

function scanBackArray(d,pos){
  let depth=0;
  for(let p=pos;p>=0;p--){const c=d[p];
    if(c===']'||c==='}'||c===')') depth++;
    else if(c==='['||c==='{'||c==='('){ if(depth===0){ if(c==='[') return p; } else depth--; }}
  return -1;
}

// One reference read: locate the declaration around `needle` (or evaluate `expr`
// against the module's scope) and resolve every identifier it reaches by looking
// the definition up in the same chunk.
function run(file,needle,expr,anchor,subst,preset,arrayAt){
  const d=chunkText(file);
  let s;
  if(arrayAt!==undefined&&arrayAt>=0){ s=arrayAt; }
  else if(needle===null||needle===undefined){ s=-1; }
  else{
    const i=d.indexOf(needle);
    if(i<0) throw new Error('needle not found in '+file+': '+needle);
    s=scanBackArray(d,i);
  }
  const e=s>=0?matchForward(d,s):-1;
  global.__ANCHOR__ = anchor||(s>=0?s:d.length);
  global.__SEEN__ = new Set();
  // Shallow-copied, not JSON round-tripped: a preset may carry a function that
  // stands in for a feature gate whose value cannot be read out of the bundle.
  evalWith.cache = preset ? Object.assign({}, preset) : {};
  let src = expr || d.slice(s,e+1);
  if(subst) for(const pair of subst.split('@@')){ const [a,b]=pair.split('>>'); src=src.split(a).join(b); }
  const out=evalWith(src,d,file+':'+needle);
  if(out===null||out===undefined) throw new Error('could not evaluate '+file+':'+needle);
  // A gate the reference declares as a function survives only by reference:
  // JSON.stringify turns it into undefined, and 1.46388.1.0 made two of the
  // chrome chunk's exports functions that the computer-use factory calls.
  if(typeof out==='function') return out;
  return JSON.parse(JSON.stringify(out));
}

// A JS template/quoted literal body, turned back into the string it denotes.
function unesc(buf){
  const B2=String.fromCharCode(92);
  let out='';
  for(let i=0;i<buf.length;i++){
    if(buf.charCodeAt(i)===92){
      const n=buf[i+1];
      if(n==='`'||n==='$'){ out+=n; i++; continue; }
      out+=buf[i]+n; i++; continue;
    }
    if(buf[i]==='"'){ out+=B2+'"'; continue; }
    out+=buf[i];
  }
  return JSON.parse('"'+out+'"');
}

// The string literal whose opening quote sits at `at`.
function literalAtQuote(d, at){
  const q=d[at];
  let p2=at+1, buf='';
  while(p2<d.length){ if(d.charCodeAt(p2)===92){ buf+=d[p2]+d[p2+1]; p2+=2; continue; } if(d[p2]===q) break; buf+=d[p2]; p2++; }
  return unesc(buf);
}

// The string literal that opens at `marker` (whose last character is the quote).
function literalAfter(file, marker){
  const d=chunkText(file);
  const i=d.indexOf(marker);
  if(i<0) throw new Error('marker not found: '+marker);
  return literalAtQuote(d, i+marker.length-1);
}

// The string literal that starts with `head`, whichever quote it is written in.
// Content-addressed, so it survives the minified name it is assigned to changing.
function literalStartingWith(file, head){
  const d=chunkText(file);
  for(const q of ['"',"'",'`']){
    const at=d.indexOf(q+head);
    if(at>=0) return literalAtQuote(d, at);
  }
  throw new Error('no literal starts with '+head+' in '+file);
}

// The value a chunk exports under `name`, resolved through its own scope. The
// re-export table names the binding, so the exported name is the stable anchor.
function evalExport(file, name){
  const d=chunkText(file);
  const marker='Object.defineProperty(exports,"'+name+'",{enumerable:!0,get:function(){return ';
  const at=d.indexOf(marker);
  if(at<0) throw new Error('no export '+name+' in '+file);
  let p2=at+marker.length, ident='';
  while(p2<d.length && /[\w$]/.test(d[p2])){ ident+=d[p2]; p2++; }
  return run(file, null, ident, d.length);
}

// The function whose body contains `needle`, as {name, at}.
function functionAround(file, needle){
  const d=chunkText(file);
  const i=d.indexOf(needle);
  if(i<0) throw new Error('needle not found in '+file+': '+needle);
  let best=null;
  const re=/function ([A-Za-z_$][\w$]*)\(/g;
  let m;
  while((m=re.exec(d))!==null){ if(m.index>i) break; best={name:m[1], at:m.index}; }
  if(!best) throw new Error('no enclosing function for '+needle);
  return best;
}

// Every chunk is found by a literal it contains rather than by its name, which
// changes every release. The two feature gates below are resolved false because
// that is what a live ccd session on this build shows: ccd_session offers four
// tools (no start_session / hand_off_to_session) and read_terminal advertises
// lines / tab_id / wait_for_output_ms and nothing else.
const CHROME=chunkFor('Start a dev server by name from .claude/launch.json');
const CU=chunkFor('This computer is running Windows. The file manager is "File Explorer"');
const SHELL=chunkFor('="ccd_session"');

// Several chunks reach the main chunk through a require() namespace and read
// tool names and doc fragments off it (`t.Nw`, `e.Wh`). Resolving those through
// the same export table keeps a renamed member loud, where a fixed preset would
// quietly leave an "undefined" in the recording - which is what 1.46388.1.0 did
// to three computer-use scale descriptions.
const mainChunkExports=new Proxy({},{get:(_,k)=>typeof k==='string'?evalExport(CHROME,k):undefined});

// --- Claude Browser: the server assembly is [previews.filter(previewNames).map(withPaneUrl), ...paneTools]
const previews=run(CHROME,'Start a dev server by name from .claude/launch.json');
const pane=run(CHROME,'Read the current page in the Browser pane as a YAML-style accessibility tree');
const J5=new Set(['preview_start','preview_stop','preview_list','preview_logs']);
const EER=literalStartingWith(CHROME, 'Open the Browser pane: pass `url`');
function tEr(t){
  if(t.name!=='preview_start') return t;
  const s=t.inputSchema;
  return {...t, description:EER+t.description,
    inputSchema:{...s, properties:{...(s.properties||{}), url:{type:'string',description:"URL to open in the Browser pane without a dev server (instead of starting one by `name`)."}}, required:[]}};
}
const claudeBrowser=[...previews.filter(t=>J5.has(t.name)).map(tEr), ...pane];
// --- claude-in-chrome: the bridge's own tool list, nothing excluded
const chrome=run(CHROME,'Natural language description of what to find');
// --- computer-use: factory(opts,'pixels',apps) minus the per-action tools, computer_batch + the batch-only note
const CU_FACTORY=functionAround(CU, 'This computer is running Windows. The file manager is "File Explorer"');
const cuAll=run(CU,null,
  CU_FACTORY.name+'({platform:`win32`,screenshotFiltering:`mask`,teachMode:true,adaptiveResolution:true,appScoped:undefined},`pixels`,undefined)',
  CU_FACTORY.at,null,
  // Named rather than proxied: the factory probes members of `e` that are
  // gates rather than text, and resolving those eagerly throws. Wh joined
  // the list in 1.46388.1.0, which moved the scale sentence behind it.
  {e:{Dd:evalExport(CHROME,'Dd'), Od:evalExport(CHROME,'Od'), Wh:evalExport(CHROME,'Wh')}});
const ACTIONS=new Set(cuAll.find(t=>t.name==='computer_batch').inputSchema.properties.actions.items.properties.action.enum);
const F=literalStartingWith(SHELL, ' IMPORTANT: in this session the individual interaction tools');
const computerUse=cuAll.filter(t=>!ACTIONS.has(t.name)).map(t=>t.name==='computer_batch'?{...t,description:t.description+F}:t);
// --- the rest, each located by its own serverName literal
const ccdSession=serverTools('ccd_session',{Ti:()=>false, t:mainChunkExports});
const ccdDirectory=serverTools('ccd_directory');
const ccdSessionMgmt=serverTools('ccd_session_mgmt');
const mcpRegistry=serverTools('mcp-registry');
const TERM=chunkFor("Read what is on screen in the user's Terminal panel");
// `o` and `r` are presets rather than lookups: the chunk reuses both names for
// locals inside its result-formatting helpers, and the nearest assignment before
// the tools array is one of those rather than the module-level description.
const terminal=run(TERM,'name:"read_terminal"',null,null,null,
  {r:false, o:literalStartingWith(TERM, "Read what is on screen in the user's Terminal panel")});
const visualize=run(chunkFor('Returns required context for show_widget'),'Returns required context for show_widget');

// `tool("list_scheduled_tasks", "…"` — the call marker, whichever local name the
// SDK's tool() helper was minified to and whichever quote the name is written in.
function toolCall(d, toolName){
  for(const q of ['"',"'",'`']){
    const at=d.indexOf('('+q+toolName+q+',');
    if(at<0) continue;
    let p2=at-1, name='';
    while(p2>=0 && /[\w$]/.test(d[p2])){ name=d[p2]+name; p2--; }
    return name+'('+q+toolName+q+',';
  }
  throw new Error('no tool() call for '+toolName);
}

// The zod builder members this chunk uses, mapped to the JSON Schema type each
// produces. Read from the declarations rather than remembered: the SDK's minified
// member names change per release, the shapes they describe do not.
function zodBuilders(d){
  const known={
    string:['taskId:', 'prompt:'],
    boolean:['notifyOnCompletion:', 'enabled:'],
  };
  const out={};
  for(const [type, props] of Object.entries(known)){
    for(const prop of props){
      const re=new RegExp(prop+'e\\.([A-Za-z]+)\\(\\)');
      const m=re.exec(d);
      if(m){ out[m[1]]=type; break; }
    }
  }
  if(Object.keys(out).length===0) throw new Error('no zod builders recognised');
  return out;
}

// scheduled-tasks declares zod shapes rather than JSON Schema: tool(name, description, shape, handler).
function scheduledTasks(){
  const file=chunkFor('List all scheduled tasks with their current state');
  const d=chunkText(file);
  // The zod builders are minified members of the SDK module; each is identified
  // by the JSON Schema type it produces, read off one declaration that uses it.
  const ZTYPE=zodBuilders(d);
  const tools=[];
  for(const toolName of ['list_scheduled_tasks','create_scheduled_task','update_scheduled_task','delete_scheduled_task']){
    const marker=toolCall(d, toolName);
    const at=d.indexOf(marker);
    if(at<0) throw new Error('scheduled tool not found: '+toolName);
    let i=at+marker.length;
    // description literal
    const q=d[i]; i++;
    let buf='';
    while(i<d.length){ if(d.charCodeAt(i)===92){ buf+=d[i]+d[i+1]; i+=2; continue; } if(d[i]===q) break; buf+=d[i]; i++; }
    const description=unesc(buf);
    i++; // closing quote
    if(d[i]!==',') throw new Error('expected comma after description of '+toolName);
    i++;
    // shape object literal
    const shapeStart=i;
    let depth=0, end=-1;
    for(let p2=i;p2<d.length;p2++){
      const c=d[p2];
      if(c==='`'||c==='"'||c==="'"){const qq=c;p2++;while(p2<d.length){if(d.charCodeAt(p2)===92){p2+=2;continue}if(d[p2]===qq)break;p2++;}continue;}
      if(c==='{'||c==='('||c==='[') depth++;
      else if(c==='}'||c===')'||c===']'){depth--; if(depth===0){end=p2;break;}}
    }
    const shape=d.slice(shapeStart,end+1);
    const properties={}; const required=[];
    const entryRe=/([A-Za-z_][A-Za-z0-9_]*):e\.([A-Za-z]+)\(\)((?:\.[A-Za-z]+\([^)]*\))*)/g;
    let m;
    while((m=entryRe.exec(shape))){
      const prop=m[1], zt=ZTYPE[m[2]];
      if(!zt) throw new Error('unknown zod builder e.'+m[2]);
      const chain=shape.slice(m.index+m[0].length-m[3].length);
      // read the modifier chain from the start of the entry to the next top-level comma
      let p2=m.index+m[1].length+1, dep=0, stop=-1;
      for(;p2<shape.length;p2++){
        const c=shape[p2];
        if(c==='`'||c==='"'||c==="'"){const qq=c;p2++;while(p2<shape.length){if(shape.charCodeAt(p2)===92){p2+=2;continue}if(shape[p2]===qq)break;p2++;}continue;}
        if(c==='('||c==='{'||c==='[') dep++;
        else if(c===')'||c==='}'||c===']'){ if(dep===0){stop=p2;break;} dep--; }
        else if(c===','&&dep===0){stop=p2;break;}
      }
      const expr=shape.slice(m.index+m[1].length+1, stop<0?shape.length:stop);
      const optional=/\.optional\(\)/.test(expr);
      const dm=/\.describe\((["'`])([\s\S]*?)\1\)\s*$/.exec(expr);
      const desc=dm?unesc(dm[2]):undefined;
      const p3={type:zt};
      if(desc!==undefined) p3.description=desc;
      properties[prop]=p3;
      if(!optional) required.push(prop);
      entryRe.lastIndex = stop<0?shape.length:stop;
    }
    const inputSchema={type:'object',properties};
    if(required.length) inputSchema.required=required;
    tools.push({name:toolName, description, inputSchema});
  }
  return tools;
}
const SCHEDULED=scheduledTasks();
const servers=[
  {name:'Claude Browser', wireName:'Claude_Browser', alwaysLoad:true, tools:claudeBrowser},
  {name:'claude-in-chrome', wireName:'claude-in-chrome', alwaysLoad:false, tools:chrome},
  {name:'computer-use', wireName:'computer-use', alwaysLoad:false, tools:computerUse},
  {name:'ccd_session', wireName:'ccd_session', alwaysLoad:true, tools:ccdSession},
  {name:'ccd_session_mgmt', wireName:'ccd_session_mgmt', alwaysLoad:false, tools:ccdSessionMgmt},
  {name:'ccd_directory', wireName:'ccd_directory', alwaysLoad:false, tools:ccdDirectory},
  // terminal's one tool declares alwaysLoad in the bundle from 1.44121.2.0 on, and
  // a live ccd session on that build shows read_terminal loaded rather than deferred.
  {name:'terminal', wireName:'terminal', alwaysLoad:true, tools:terminal},
  {name:'mcp-registry', wireName:'mcp-registry', alwaysLoad:false, tools:mcpRegistry},
  {name:'scheduled-tasks', wireName:'scheduled-tasks', alwaysLoad:false, tools:SCHEDULED},
  {name:'visualize', wireName:'visualize', alwaysLoad:true, tools:visualize},
];
for(const s of servers) for(const t of s.tools) if(t.inputSchema && t.inputSchema.required===undefined) delete t.inputSchema.required;
fs.writeFileSync(OUT, JSON.stringify({build:BUILD, servers}, null, 2).replace(/\r\n/g,'\n')+'\n');
console.error('servers', servers.map(s=>s.name+':'+s.tools.length).join(' '));
