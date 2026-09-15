const $ = (s, root = document) => root.querySelector(s);
const esc = value => String(value ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const icons = {
 grid:'<rect x="3" y="3" width="7" height="7" rx="1.5"/><rect x="14" y="3" width="7" height="7" rx="1.5"/><rect x="3" y="14" width="7" height="7" rx="1.5"/><rect x="14" y="14" width="7" height="7" rx="1.5"/>',
 device:'<rect x="3" y="4" width="18" height="13" rx="2"/><path d="M8 21h8M12 17v4"/>',
 tools:'<path d="m8 4-5 8 5 8m8-16 5 8-5 8m-5-3 2-10"/>',
 users:'<circle cx="9" cy="7" r="3"/><path d="M3 21v-3a6 6 0 0 1 12 0v3m2-15a3 3 0 0 1 0 6m1 3a5 5 0 0 1 3 5"/>',
 pulse:'<path d="M2 12h4l3-8 6 16 3-8h4"/>', shield:'<path d="m12 3 8 3v6c0 5-8 9-8 9s-8-4-8-9V6z"/><path d="m8 12 3 3 5-6"/>',
 link:'<path d="m9 15 6-6m-8 4-2 2a4 4 0 0 0 6 6l3-3M10 6l3-3a4 4 0 0 1 6 6l-2 2"/>', search:'<circle cx="10" cy="10" r="6"/><path d="m15 15 6 6"/>',
 logout:'<path d="M9 3H4v18h5m5-14 5 5-5 5M8 12h12"/>', arrow:'<path d="M5 12h14m-5-5 5 5-5 5"/>', menu:'<path d="M3 6h18M3 12h18M3 18h18"/>'
};
const icon = name => `<svg class="ico" viewBox="0 0 24 24" aria-hidden="true">${icons[name] ?? icons.tools}</svg>`;
const pill = (value, label=value) => `<span class="pill ${esc(value)}"><span class="dot"></span>${esc(label)}</span>`;
const state = {user:null, registrationEnabled:false, page:'overview', csrf:'', overview:{}, devices:[], tools:[], capabilities:[], users:[], activity:[], query:'', busy:false, selectedTools:new Set(), bulkBusy:false};
let pendingToolAvailability = null;
let toastTimer;
function toast(message){$('#toast').textContent=message;$('#toast').classList.add('visible');clearTimeout(toastTimer);toastTimer=setTimeout(()=>$('#toast').classList.remove('visible'),4500);}
async function api(path, method='GET', body){
 const headers = {'Accept':'application/json'};
 if(method!=='GET'){headers['X-CSRF-TOKEN']=state.csrf;headers['Content-Type']='application/json';}
 const response=await fetch(path,{method,credentials:'same-origin',headers,body:body===undefined?undefined:JSON.stringify(body)});
 const data=response.status===204?null:await response.json().catch(()=>null);
 if(!response.ok) throw new Error(data?.error || Object.values(data?.errors||{}).flat().join(' ') || `Request failed (${response.status}).`);
 return data;
}
async function csrf(){state.csrf=(await api('/api/auth/csrf')).token;}
async function boot(){try{await csrf();Object.assign(state,await api('/api/auth/session'));if(state.user)await workspace();else auth();}catch(e){$('#app').innerHTML=`<main class="auth-shell"><section class="auth-card"><h1>Connection unavailable</h1><p>${esc(e.message)}</p><button data-action="reload">Try again</button></section></main>`;}}
function auth(register=false){
 $('#app').innerHTML=`<main class="auth-shell" id="main"><div class="auth-layout page-enter"><section class="auth-story"><div class="brand"><div class="brand-orb">J</div><div>Jarvis <small>CONTROL PLATFORM</small></div></div><h1>Your tools.<br>Your computer.<br><em>One connection.</em></h1><p>A private bridge between AI and your development workspace. Build, debug and create — with you in control.</p><div class="orbit"><div class="brand-orb">J</div><span class="orbit-node">${icon('tools')}</span><span class="orbit-node bottom">${icon('device')}</span></div><div class="story-footer"><span>MCP + WEBSOCKET TLS</span><span>v1.0.22</span></div></section><section class="auth-card"><p class="eyebrow">YOUR WORKSPACE, CONNECTED</p><h1>${register?'Start your workspace.':'Welcome back.'}</h1><p>${register?'Create an account to connect your first device.':'Sign in to manage your devices and tools.'}</p><form id="auth-form" data-register="${register}">${register?'<label>Your name<input name="displayName" autocomplete="name" maxlength="100" required placeholder="How should we call you?"></label>':''}<label>Email address<input name="email" type="email" autocomplete="username" maxlength="254" required placeholder="you@company.com"></label><label>Password<input name="password" type="password" autocomplete="${register?'new-password':'current-password'}" ${register?'minlength="14"':''} maxlength="256" required placeholder="${register?'At least 14 characters':'Your password'}"></label><div class="form-error" role="alert"></div><button class="primary" type="submit">${register?'Create account':'Sign in to workspace'} &nbsp; →</button></form>${state.registrationEnabled?`<p class="auth-switch">${register?'Already have an account?':'New to Jarvis?'} <button data-action="auth-switch" data-register="${!register}">${register?'Sign in':'Create an account'}</button></p>`:''}<div class="auth-notice">${icon('shield')}<span>${register?'New accounts need administrator approval by default. No device access is granted automatically.':'Computer control requires an enrolled agent and explicit approval on your device.'}</span></div><p class="small muted">Passwords require uppercase, lowercase, a number and a symbol. Contact your administrator for account recovery.</p></section></div></main>`;
}
async function workspace(){
 const returnUrl=new URLSearchParams(location.search).get('returnUrl');
 if(returnUrl?.startsWith('/connect/authorize?')){location.assign(returnUrl);return;}
 const names={overview:'Overview',devices:'Devices',tools:'Tool catalog',users:'Users',activity:'Activity'};
 $('#app').innerHTML=`<div class="layout"><aside class="sidebar"><div class="brand"><div class="brand-orb">J</div><div>Jarvis <small>CONTROL PLATFORM</small></div></div><div class="nav-title">WORKSPACE</div><nav class="nav">${['overview','devices','tools',...(state.user.role==='admin'?['users']:[]),'activity'].map(p=>`<button data-page="${p}" class="${p===state.page?'selected':''}">${icon(({overview:'grid',devices:'device',tools:'tools',users:'users',activity:'pulse'})[p])}${names[p]}</button>`).join('')}</nav><div class="nav-title">CONNECTION</div><nav class="nav"><button data-action="connection-help">${icon('link')}Connect ChatGPT</button><button data-action="security">${icon('shield')}Security & access</button></nav><div class="sidebar-bottom"><div class="connection-note"><b>● &nbsp; Local approval protected</b>Every sensitive action stays under your control.</div><div class="profile"><div class="avatar">${esc(state.user.displayName.slice(0,1).toUpperCase())}</div><div><div class="profile-name">${esc(state.user.displayName)}</div><div class="profile-sub">${state.user.role==='admin'?'Administrator':'Personal workspace'}</div></div><button data-action="logout" title="Sign out" aria-label="Sign out">${icon('logout')}</button></div></div></aside><div><header class="topbar"><button class="menu-toggle icon-btn" data-action="menu" aria-label="Open navigation">${icon('menu')}</button><div class="breadcrumb">Workspace &nbsp; / <strong id="breadcrumb">${names[state.page]}</strong></div><div class="top-right"><span class="pill active"><span class="dot"></span>Authenticated session</span><span class="avatar">${esc(state.user.displayName.slice(0,1).toUpperCase())}</span></div></header><main id="main" class="content"></main></div></div>`;
 await loadPage();
}
async function loadPage(silent=false){
 if(state.busy)return;state.busy=true;const requestedPage=state.page;
 try{
  if(state.page==='overview'){[state.overview,state.devices,state.activity]=await Promise.all([api('/api/overview'),api('/api/devices'),api('/api/activity')]);}
  if(state.page==='devices')state.devices=await api('/api/devices');
  if(state.page==='tools'){
   if(state.user.role==='admin')[state.tools,state.capabilities]=await Promise.all([api('/api/admin/tools'),api('/api/admin/capabilities')]);
   else{state.devices=await api('/api/devices');state.tools=(await Promise.all(state.devices.map(d=>api(`/api/devices/${d.id}/tools`)))).flat().filter((t,i,a)=>a.findIndex(x=>x.id===t.id)===i);}
  }
  if(state.page==='users')state.users=await api('/api/admin/users');
  if(state.page==='activity')state.activity=await api('/api/activity');
  if(requestedPage!==state.page){state.busy=false;await loadPage();return;}
  pruneToolSelection();
  render();
 }catch(e){if(!silent)toast(e.message);}finally{state.busy=false;}
}
function pageHeader(title,description,actions=''){return `<div class="page-header"><div><h1>${title}</h1><p>${description}</p></div><div class="button-row">${actions}</div></div>`;}
function empty(title,description,action=''){return `<div class="empty">${icon('device')}<h3>${title}</h3><p>${description}</p>${action}</div>`;}
function relative(timestamp){if(!timestamp)return 'Not connected yet';const d=new Date(timestamp*1000);return d.toLocaleString(undefined,{month:'short',day:'numeric',hour:'2-digit',minute:'2-digit'});}
function deviceTable(devices,compact=false){return !devices.length?empty('Your workspace starts here.','Enroll a device, open Jarvis Agent and connect it to this server.','<button class="primary" data-action="add-device">+ Enroll your first device</button>'):`<div class="table-wrap"><table><thead><tr><th>Device</th><th>Status</th><th>Tools</th><th>Version</th>${compact?'':'<th>Last connected</th>'}<th></th></tr></thead><tbody>${devices.map(d=>`<tr><td><div class="cell-title"><div class="device-icon">${icon('device')}</div><div><strong>${esc(d.name)}</strong><small>${esc(d.platform||'Waiting for first connection')}</small></div></div></td><td>${pill(!d.enabled?'disabled':d.online?'online':'offline')}</td><td>${d.toolCount}</td><td class="muted">${esc(d.agentVersion||'—')}</td>${compact?'':`<td class="small muted">${relative(d.lastSeenAt)}</td>`}<td><div class="row-actions"><button data-action="edit-device" data-id="${d.id}">Manage</button></div></td></tr>`).join('')}</tbody></table></div>`;}
function activityFeed(limit=6){return !state.activity.length?empty('No activity yet.','Your first enrollment and tool calls will appear here.') : state.activity.slice(0,limit).map(a=>`<div class="feed-row"><span class="dot"></span><div><strong>${esc(a.action)}</strong><small>${relative(a.time)} ${a.durationMs?`· ${a.durationMs} ms`:''}</small></div>${pill(a.outcome)}</div>`).join('');}
function render(){
 const main=$('#main');if(!main)return;
 const titles={overview:'Overview',devices:'Devices',tools:'Tool catalog',users:'Users',activity:'Activity'};
 $('#breadcrumb').textContent=titles[state.page];document.title=`${titles[state.page]} · Jarvis Control`;
 document.querySelectorAll('[data-page]').forEach(b=>{b.classList.toggle('selected',b.dataset.page===state.page);b.setAttribute('aria-current',b.dataset.page===state.page?'page':'false');});
 const addDevice='<button class="primary" data-action="add-device">+ &nbsp; Enroll device</button>';
 let html='';
 if(state.page==='overview'){
  const m=state.overview;
  html=pageHeader(`Welcome back, ${esc(state.user.displayName.split(' ')[0])}.`,'Here’s what’s happening across your connected workspace.',addDevice)+
  `<section class="hero"><div class="hero-copy"><p class="eyebrow">YOUR NEXT WORKFLOW STARTS HERE</p><h2>Bring AI closer<br>to the work that matters.</h2><p>Connect your computer once. Access your tools through MCP — with secure routing and local consent.</p><div class="button-row"><button class="primary" data-action="connection-help">Connect ChatGPT &nbsp; ↗</button><button class="ghost" data-page="tools">Explore your tools</button></div></div><div class="orbit"><div class="brand-orb">J</div><div class="orbit-node">${icon('tools')}</div><div class="orbit-node bottom">${icon('device')}</div></div></section>`+
  `<div class="metrics">${[[m.devices,'Enrolled devices','device','Connected to your account'],[m.online,'Online right now','pulse','Ready for local authorization'],[m.tools,'Published tools','tools','Enabled in the server catalog'],[m.callsToday,'Tool calls today','grid','Started calls · UTC day']].map(([n,l,i,c])=>`<div class="metric"><div class="metric-head">${l}${icon(i)}</div><div class="metric-value">${n??0}</div><div class="metric-caption">${c}</div></div>`).join('')}</div>`+
  `<section class="panel"><header class="panel-header"><div><h2>Your devices</h2><p>The computers powering your workspace</p></div><button data-page="devices">View all &nbsp; →</button></header>${deviceTable(state.devices.slice(0,4),true)}</section>`+
  `<div class="split"><section class="panel"><header class="panel-header"><h2>Recent activity</h2><button data-page="activity">View log &nbsp; →</button></header>${activityFeed(5)}</section><section class="panel"><header class="panel-header"><div><h2>From connected to productive</h2><p>Three steps to your first tool call</p></div></header>${[['Enroll a device','Create its identity and copy the one-time token.'],['Connect Jarvis Agent','Choose a workspace, connect, and arm local control.'],['Authorize your MCP client','Select the device in OAuth consent and call an enabled tool.']].map(([h,p],i)=>`<div class="step"><span class="step-number">0${i+1}</span><div><h3>${h}</h3><p>${p}</p></div></div>`).join('')}</section></div>`;
 }
 if(state.page==='devices')html=pageHeader('Your devices.','Manage enrolled computers. Disabling a device immediately closes its connection.',addDevice)+`<section class="panel">${deviceTable(state.devices)}</section>`;
 if(state.page==='tools'){
  const admin=state.user.role==='admin';
  html=pageHeader(admin?'Tools, under your control.':'Your agent capabilities.',admin?'Publish installed capabilities, edit their metadata and control availability.':'Capabilities installed on your devices. The administrator decides which are exposed over MCP.',admin?'<button data-action="import-tools">Import installed</button><button class="primary" data-action="add-tool">+ &nbsp; Add tool</button>':'')+
  `<div class="toolbar"><div class="search">${icon('search')}<input id="tool-search" aria-label="Search tools" placeholder="Search tools, category or description…" value="${esc(state.query)}"></div><span class="muted small">${state.tools.length} ${admin?'registered tools':'installed capabilities'}</span></div>${admin?toolSelectionToolbar():''}<div id="tool-cards">${toolCards()}</div>`;
 }
 if(state.page==='users')html=pageHeader('People & permissions.','Approve registrations and manage access to the control plane.','<button class="primary" data-action="add-user">+ &nbsp; Add user</button>')+
  `<section class="panel"><div class="table-wrap"><table><thead><tr><th>User</th><th>Role</th><th>Status</th><th></th></tr></thead><tbody>${state.users.map(u=>`<tr><td><div class="cell-title"><div class="avatar">${esc(u.displayName.slice(0,1))}</div><div><strong>${esc(u.displayName)}</strong><small>${esc(u.email)}</small></div></div></td><td>${pill(u.role)}</td><td>${pill(u.status)}</td><td><button class="ghost" data-action="edit-user" data-id="${u.id}">Manage</button></td></tr>`).join('')}</tbody></table></div></section>`;
 if(state.page==='activity')html=pageHeader('An accountable workspace.','Your latest 100 audit events. Arguments, tokens and screenshots are not recorded.')+`<section class="panel">${activityFeed(100)}</section>`;
 main.innerHTML=`<div class="page-enter">${html}<footer class="footer-note"><span>Jarvis Control · v1.0.22</span><span>Device-bound authorization &nbsp; / &nbsp; Local approval required</span></footer></div>`;
 if(state.page==='tools')syncToolSelection();
}
function visibleTools() {
 return state.tools.filter(t => (t.name+' '+t.category+' '+t.description).toLowerCase().includes(state.query.toLowerCase()));
}
function pruneToolSelection() {
 const visible = new Set(visibleTools().map(t => t.id));
 for(const id of state.selectedTools) if(!visible.has(id)) state.selectedTools.delete(id);
 if(state.user?.role!=='admin')state.selectedTools.clear();
}
function toolSelectionToolbar() {
 return `<section class="bulk-toolbar" aria-label="Bulk tool actions">
  <div class="bulk-selection"><label class="tool-select-all"><input type="checkbox" id="select-all-tools"><span id="select-all-label">Select all</span></label>
   <span id="tool-selection-count" class="small muted" role="status" aria-live="polite"></span></div>
  <div class="button-row"><button data-action="clear-tool-selection">Clear selection</button>
   <button data-action="bulk-disable-tools">Disable selected</button>
   <button class="primary" data-action="bulk-publish-tools">Publish selected</button></div>
  <p class="bulk-hint small muted">Selection applies to the current search. Publishing exposes tools to authorized MCP clients; local approval still applies.</p>
 </section>`;
}
function syncToolSelection() {
 const master=$('#select-all-tools'); if(!master)return;
 const visible=visibleTools(), count=visible.filter(t=>state.selectedTools.has(t.id)).length;
 master.checked=visible.length>0 && count===visible.length;
 master.indeterminate=count>0 && count<visible.length;
 master.disabled=state.bulkBusy || !visible.length;
 $('#select-all-label').textContent=state.query?'Select all filtered':'Select all';
 $('#tool-selection-count').textContent=`${count} selected / ${visible.length} shown`;
 for(const action of ['clear-tool-selection','bulk-disable-tools','bulk-publish-tools'])
  $(`[data-action="${action}"]`).disabled=state.bulkBusy || count===0;
 document.querySelectorAll('input[data-tool-select]').forEach(input=>{
  input.checked=state.selectedTools.has(input.dataset.toolSelect);
  input.disabled=state.bulkBusy;
  input.closest('.tool-card').classList.toggle('is-selected',input.checked);
 });
}
function toolCards() {
 const admin=state.user.role==='admin', list=visibleTools();
 return list.length?`<div class="cards">${list.map(t=>`<article class="tool-card ${admin&&state.selectedTools.has(t.id)?'is-selected':''}">
  <div class="tool-top"><span class="device-icon">${icon(t.category==='computer'?'device':'tools')}</span>
   <div class="tool-card-actions">${pill(admin?(t.enabled?'active':'disabled'):'installed',admin?(t.enabled?'Published':'Disabled'):'Installed')}
   ${admin?`<input type="checkbox" class="tool-checkbox" data-tool-select="${esc(t.id)}" aria-label="Select ${esc(t.name)}" ${state.selectedTools.has(t.id)?'checked':''}>`:''}</div></div>
  <h3>${esc(t.name)}</h3><p>${esc(t.description)}</p><footer><span>${esc(t.category)}</span><button data-action="tool-detail" data-id="${esc(t.id)}">View details &nbsp; ↗</button></footer>
 </article>`).join('')}</div>`:empty('No tools to show.','Connect an agent, then import its capabilities. Imported tools start disabled.');
}
function reviewToolAvailability(enabled) {
 if(state.user?.role!=='admin' || state.bulkBusy)return;
 const tools=visibleTools().filter(t=>state.selectedTools.has(t.id));
 if(!tools.length)return;
 pendingToolAvailability={enabled,tools:tools.map(({id,revision})=>({id,revision}))};
 const verb=enabled?'Publish':'Disable';
 modal(`${verb} ${tools.length} selected tools?`, `<div class="modal-body">
  <p>${enabled?'Authorized MCP clients will be able to discover and request these tools.':'These tools will be hidden from new tool lists and future calls will be rejected.'}</p>
  <div class="notice">${enabled?'This selection may include file writes, terminal commands, browser and desktop actions. Agent approval rules are unchanged.':'This does not undo actions already completed. Use Pause in the Agent to cancel its active jobs.'}</div>
  <p class="small muted">All selected changes are applied together. A stale or missing tool rejects the whole batch.</p>
  <ul class="bulk-preview">${tools.slice(0,6).map(t=>`<li>${esc(t.name)}</li>`).join('')}</ul>
  ${tools.length>6?`<p class="small muted">And ${tools.length-6} more selected tools.</p>`:''}
  <div id="bulk-tool-error" class="form-error" role="alert"></div></div>`,
  `<button data-action="close-modal">Cancel</button><button class="${enabled?'primary':'danger'}" data-action="confirm-tool-availability">${verb} ${tools.length} tools</button>`);
}
async function applyToolAvailability() {
 if(!pendingToolAvailability || state.bulkBusy || state.user?.role!=='admin')return;
 const request=pendingToolAvailability;
 state.bulkBusy=true; syncToolSelection();
 const button=$('[data-action="confirm-tool-availability"]'); button.disabled=true; button.textContent='Applying…';
 document.querySelectorAll('#modal-content [data-action="close-modal"]').forEach(b=>b.disabled=true);
 try {
  const result=await api('/api/admin/tools/bulk-availability','POST',request);
  state.selectedTools.clear(); pendingToolAvailability=null;
  $('#modal').close(); $('#modal-content').replaceChildren();
  toast(`${result.updated} of ${result.selected} tools ${request.enabled?'published':'disabled'}. Refresh your MCP client’s tool list.`);
  await loadPage();
 } catch(e) {
  $('#bulk-tool-error').textContent=e.message;
  button.disabled=false; button.textContent=request.enabled?'Retry publish':'Retry disable';
 } finally {
  state.bulkBusy=false; syncToolSelection();
  document.querySelectorAll('#modal-content [data-action="close-modal"]').forEach(b=>b.disabled=false);
 }
}
function modal(title,body,actions='<button data-action="close-modal">Close</button>'){
 $('#modal-content').innerHTML=`<div class="modal-header"><h2 id="modal-title">${title}</h2><button data-action="close-modal" class="icon-btn" aria-label="Close dialog">×</button></div>${body}<div class="modal-actions">${actions}</div>`;
 $('#modal').showModal();
}
function formModal(title,fields,onSubmit,extra=''){
 modal(title,`<form id="modal-form"><div class="modal-body">${fields}<div class="form-error" role="alert"></div></div></form>`,`${extra}<button data-action="close-modal">Cancel</button><button class="primary" type="submit" form="modal-form">Save changes</button>`);
 $('#modal-form').addEventListener('submit',async event=>{event.preventDefault();const button=$('button[form="modal-form"]');button.disabled=true;try{await onSubmit(Object.fromEntries(new FormData(event.currentTarget)));$('#modal').close();await loadPage();}catch(e){$('#modal-form .form-error').textContent=e.message;}finally{button.disabled=false;}});
}
function textField(name,label,value='',type='text',required=true){return `<label>${label}<input name="${name}" type="${type}" value="${esc(value)}" ${required?'required':''} ${type==='password'?'autocomplete="new-password" minlength="14" maxlength="256"':''}></label>`;}
function selectField(name,label,values,value){return `<label>${label}<select name="${name}">${values.map(([v,l])=>`<option value="${esc(v)}" ${v===value?'selected':''}>${esc(l)}</option>`).join('')}</select></label>`;}
function enrollment(result){modal('Your device is ready to pair.',`<div class="modal-body"><p>Copy these values into Jarvis Agent. The token is displayed only once and expires after 30 days.</p><label>Server URL<input readonly value="${esc(result.serverUrl)}"></label><label>Device ID<input readonly value="${esc(result.deviceId)}"></label><label>Enrollment token<input id="enrollment-token" readonly value="${esc(result.token)}"></label><div class="notice">Treat this token as a password. Do not paste it into a chat, commit it to Git or share it with another user.</div></div>`, '<button data-action="copy-enrollment">Copy token</button><button class="primary" data-action="close-modal">I’ve saved it</button>');}
function deviceEditor(device){
 formModal(device?'Manage device':'Enroll a new device',textField('name','Device name',device?.name??'')+(device?selectField('enabled','Remote connection',[['true','Enabled'],['false','Disabled']],String(device.enabled))+'<p class="small muted">Rotation disconnects the agent. Update its saved token before reconnecting.</p>':''),async values=>{
  if(device){await api(`/api/devices/${device.id}`,'PUT',{name:values.name,enabled:values.enabled==='true',revision:device.revision});toast('Device updated.');}
  else{const result=await api('/api/devices','POST',{name:values.name});setTimeout(()=>enrollment(result),0);}
 },device?`<button class="danger" data-action="delete-device" data-id="${device.id}">Delete</button><button data-action="rotate-device" data-id="${device.id}">Rotate token</button>`:'');
}
function toolEditor(tool){
 if(!state.capabilities.length){toast('Connect an agent first so its installed capabilities can be registered.');return;}
 formModal(tool?'Edit published tool':'Add a tool',textField('name','MCP tool name',tool?.name??'')+selectField('agentToolId','Installed capability',state.capabilities.map(t=>[t.id,t.name]),tool?.agentToolId??state.capabilities[0].id)+`<label>Description<textarea name="description" required maxlength="8000">${esc(tool?.description??'')}</textarea></label>`+selectField('enabled','Availability',[['false','Disabled'],['true','Published']],String(tool?.enabled??false))+'<p class="small muted">The argument schema and local consent rules are inherited from the installed agent tool and cannot be weakened here.</p>',async v=>{
 await api('/api/admin/tools'+(tool?'/'+tool.id:''),tool?'PUT':'POST',{...v,enabled:v.enabled==='true',revision:tool?.revision});toast('Tool saved. Reconnect or refresh the client’s tool list.');
 },tool?`<button class="danger" data-action="delete-tool" data-id="${tool.id}">Delete</button>`:'');
}
function userEditor(user){
 formModal(user?'Manage user':'Create user',textField('displayName','Display name',user?.displayName??'')+textField('email','Email',user?.email??'','email')+`<div class="field-row">${selectField('role','Role',[['user','User'],['admin','Administrator']],user?.role??'user')}${selectField('status','Status',[['pending','Pending approval'],['active','Active'],['disabled','Disabled']],user?.status??'active')}</div>`+textField('password',user?'New password (leave blank to keep current)':'Initial password','','password',!user)+'<p class="small muted">Updating an account revokes its existing MCP grants and disconnects its agents.</p>',async v=>{
 await api('/api/admin/users'+(user?'/'+user.id:''),user?'PUT':'POST',{...v,password:v.password||null});toast('Account saved.');
 },user?`<button class="danger" data-action="delete-user" data-id="${user.id}">Delete</button>`:'');
}
async function confirmDelete(kind,id){
 if(!confirm(`Delete this ${kind}? This cannot be undone.`))return;
 await api(kind==='device'?`/api/devices/${id}`:`/api/admin/${kind}s/${id}`,'DELETE');$('#modal').close();toast(`${kind} deleted.`);await loadPage();
}
document.addEventListener('submit',async event=>{
 if(event.target.id!=='auth-form')return;event.preventDefault();const form=event.target,button=$('button',form);button.disabled=true;
 try{const data=Object.fromEntries(new FormData(form));if(form.dataset.register==='true'){const result=await api('/api/auth/register','POST',data);auth();toast(result.message);}else{await api('/api/auth/login','POST',data);await csrf();Object.assign(state,await api('/api/auth/session'));await workspace();}}catch(e){$('.form-error',form).textContent=e.message;}finally{button.disabled=false;}
});
document.addEventListener('input',event=>{if(event.target.id==='tool-search'){
 state.query=event.target.value;pruneToolSelection();$('#tool-cards').innerHTML=toolCards();syncToolSelection();
}});
document.addEventListener('change',event=>{
 if(state.user?.role!=='admin' || state.bulkBusy)return;
 const input=event.target;
 if(input.id==='select-all-tools'){
  for(const tool of visibleTools())if(input.checked)state.selectedTools.add(tool.id);else state.selectedTools.delete(tool.id);
 }else if(input.matches('input[data-tool-select]')){
  if(input.checked)state.selectedTools.add(input.dataset.toolSelect);else state.selectedTools.delete(input.dataset.toolSelect);
 }else return;
 syncToolSelection();
});
$('#modal').addEventListener('cancel',event=>{if(state.bulkBusy)event.preventDefault();else pendingToolAvailability=null;});
document.addEventListener('click',async event=>{
 const button=event.target.closest('button');if(!button || state.bulkBusy)return;
 try{
  if(button.dataset.page){state.page=button.dataset.page;state.query='';state.selectedTools.clear();$('.sidebar')?.classList.remove('open');await loadPage();return;}
  const {action,id}=button.dataset;
  switch(action){
   case 'reload':location.reload();break;
   case 'menu':$('.sidebar').classList.toggle('open');break;
   case 'auth-switch':auth(button.dataset.register==='true');break;
   case 'close-modal':pendingToolAvailability=null;$('#modal').close();$('#modal-content').replaceChildren();break;
   case 'logout':await api('/api/auth/logout','POST',{});state.user=null;state.selectedTools.clear();pendingToolAvailability=null;state.page='overview';await csrf();auth();break;
   case 'add-device':deviceEditor();break;
   case 'edit-device':deviceEditor(state.devices.find(d=>d.id===id));break;
   case 'delete-device':await confirmDelete('device',id);break;
   case 'rotate-device':if(confirm('Rotate this device token and disconnect it now?')){const result=await api(`/api/devices/${id}/rotate`,'POST',{});enrollment(result);await loadPage();}break;
   case 'copy-enrollment':await navigator.clipboard.writeText($('#enrollment-token').value);toast('Copied. Keep the token private.');break;
   case 'clear-tool-selection':state.selectedTools.clear();syncToolSelection();break;
   case 'bulk-publish-tools':reviewToolAvailability(true);break;
   case 'bulk-disable-tools':reviewToolAvailability(false);break;
   case 'confirm-tool-availability':await applyToolAvailability();break;
   case 'add-tool':toolEditor();break;
   case 'import-tools':{const result=await api('/api/admin/tools/import','POST',{});toast(`${result.imported} capabilities imported. Review and enable the tools you need.`);await loadPage();break;}
   case 'tool-detail':{const tool=state.tools.find(t=>t.id===id),admin=state.user.role==='admin';const details=admin?await api(`/api/admin/tools/${id}`):{tool,capability:tool};modal(esc(tool.name),`<div class="modal-body"><p>${esc(tool.description)}</p><p>${pill(tool.category)} ${pill(details.capability?.readOnly?'read-only':'mutating')}</p><h3>Input schema</h3><pre>${esc(JSON.stringify(details.capability?.inputSchema??{},null,2))}</pre></div>`,`${admin?`<button class="primary" data-action="edit-tool" data-id="${id}">Edit tool</button>`:''}<button data-action="close-modal">Close</button>`);break;}
   case 'edit-tool':toolEditor(state.tools.find(t=>t.id===id));break;
   case 'delete-tool':await confirmDelete('tool',id);break;
   case 'add-user':userEditor();break;
   case 'edit-user':userEditor(state.users.find(u=>u.id===id));break;
   case 'delete-user':await confirmDelete('user',id);break;
   case 'connection-help':modal('Connect your MCP client',`<div class="modal-body"><p>Register this HTTPS endpoint in your MCP client. Choose OAuth, sign in, and authorize one enrolled device.</p><pre>${esc(state.mcpEndpoint??location.origin+'/mcp')}</pre><div class="notice">Your administrator must allowlist the exact OAuth callback shown by the client. HTTPS and valid server certificates are required outside development.</div><p>Before the first call: connect the agent, import and publish its tools, then locally arm control. Each sensitive action still asks for your approval.</p></div>`);break;
   case 'security':modal('You remain in control.',`<div class="modal-body"><h3>Three independent gates</h3><p>Your active account, a device-bound OAuth grant, and a locally armed agent. Published tools cannot bypass the agent’s consent prompts.</p><h3>Pause from your computer</h3><p>Use the tray menu or Ctrl + Alt + Pause. Only Jarvis-owned command processes are terminated.</p><div class="notice">Terminal, browser and desktop tools can access resources beyond a project folder. Use a dedicated Windows account for sensitive work. Never authorize an unknown client.</div><p>Revoke all MCP grants to force authorization again and disconnect your agents.</p></div>`,'<button class="danger" data-action="revoke">Revoke my grants</button><button data-action="close-modal">Close</button>');break;
   case 'revoke':if(confirm('Revoke your current grants, disconnect agents and sign out?')){await api('/api/auth/revoke','POST',{});$('#modal').close();state.user=null;state.selectedTools.clear();pendingToolAvailability=null;await csrf();auth();}break;
  }
 }catch(e){toast(e.message);}
});
// Only refresh a stable visible overview; never destroy a form or the user's search cursor.
setInterval(()=>{if(state.user&&document.visibilityState==='visible'&&!$('#modal').open&&state.page==='overview')loadPage(true);},15000);
boot();
