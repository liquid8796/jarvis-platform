// Execute the shipping extension against a deterministic browser stub; no live browser is touched.
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const source = fs.readFileSync(path.join(__dirname, '../jarvis-agent/src/Jarvis.Agent.Windows/Assets/Browser/background.js'), 'utf8');
const A = 'js_' + 'a'.repeat(32), B = 'js_' + 'b'.repeat(32);
function browser() {
  const tabs = new Map(), groups = new Map(), storage = {}, replies = new Map();
  let tabId = 0, groupId = 0, requestId = 0;
  const event = () => ({ listeners: [], addListener(fn) { this.listeners.push(fn); } });
  const chrome = {
    runtime: { onMessage: event(), getManifest: () => ({ version: 'test' }),
      connectNative: () => ({ onMessage: event(), onDisconnect: event(), postMessage(m) { if (m.id) replies.set(m.id, m); } }) },
    storage: { session: {
      async get(key) { return { [key]: structuredClone(storage[key]) }; },
      async set(value) { Object.assign(storage, structuredClone(value)); }
    } },
    tabs: { onRemoved: event(),
      async create(options) { const tab = { id: ++tabId, groupId: -1, windowId: 1, title: 'Test', url: options.url, active: true }; tabs.set(tab.id, tab); return {...tab}; },
      async get(id) { if (!tabs.has(id)) throw Error('Missing tab'); return {...tabs.get(id)}; },
      async query(filter) { return [...tabs.values()].filter(t => filter.groupId === undefined || t.groupId === filter.groupId).map(t => ({...t})); },
      async group(options) { const id = options.groupId ?? ++groupId; groups.set(id, {}); for (const tid of options.tabIds) tabs.get(tid).groupId = id; return id; },
      async update(id, options) { Object.assign(tabs.get(id), options); return {...tabs.get(id)}; },
      async remove(ids) { for (const id of Array.isArray(ids) ? ids : [ids]) { tabs.delete(id); for (const fn of chrome.tabs.onRemoved.listeners) fn(id); } }
    },
    tabGroups: { async get(id) { if (!groups.has(id)) throw Error('Missing group'); return groups.get(id); }, async update(id, o) { Object.assign(groups.get(id), o); } },
    windows: { async update() {} },
    debugger: { onEvent: event(), onDetach: event(), async attach() {}, async detach() {}, async sendCommand() { return {}; } },
    scripting: { async executeScript() { return [{result: {text:'synthetic'}}]; } }
  };
  let context;
  function reload() {
    context = vm.createContext({ chrome, navigator: {userAgent:'Chrome/test'}, console, URL,
      setTimeout: () => 0, clearTimeout() {}, btoa: s => Buffer.from(s).toString('base64') });
    vm.runInContext(source, context);
  }
  reload();
  async function call(sessionId, cmd, args={}) {
    const id = String(++requestId);
    context.input = {id, sessionId, cmd, args};
    await vm.runInContext('onRequest(input)', context);
    const reply = replies.get(id); assert.ok(reply, 'Every command must reply');
    if (!reply.ok) throw Error(reply.error);
    return JSON.parse(JSON.stringify(reply.data));
  }
  return {call, tabs, groups, reload, chrome};
}
test('different sessions get different groups and metadata only for their own tabs', async () => {
  const b = browser();
  const [a, c] = await Promise.all([b.call(A,'create_tab'), b.call(B,'create_tab')]);
  assert.notEqual(b.tabs.get(a.tabId).groupId, b.tabs.get(c.tabId).groupId);
  assert.deepEqual((await b.call(A,'tabs')).map(t=>t.id), [a.tabId]);
  assert.deepEqual((await b.call(B,'tabs')).map(t=>t.id), [c.tabId]);
});
test('foreign tab cannot be closed or inspected, including the origin preflight', async () => {
  const b=browser(), a=await b.call(A,'create_tab'); await b.call(B,'create_tab');
  for (const cmd of ['close_tab','tab_origin','read_page','js_exec','select_tab'])
    await assert.rejects(b.call(B,cmd,{tabId:a.tabId,code:'1'}), /session|owned|group/i);
  assert.ok(b.tabs.has(a.tabId));
});
test('native requests without an application session fail explicitly', async () => {
  await assert.rejects(browser().call(undefined,'create_tab'), /session/i);
});
test('closing A removes only A tabs and B continues', async () => {
  const b=browser(), a=await b.call(A,'create_tab'), c=await b.call(B,'create_tab');
  await b.call(A,'session',{active:false,close:true});
  assert.equal(b.tabs.has(a.tabId),false); assert.ok(b.tabs.has(c.tabId));
  assert.equal((await b.call(B,'tabs')).length,1);
});
test('service worker restart restores ownership without adopting another sessions tabs', async () => {
  const b=browser(), a=await b.call(A,'create_tab'), c=await b.call(B,'create_tab');
  b.reload();
  assert.deepEqual((await b.call(A,'tabs')).map(t=>t.id),[a.tabId]);
  await assert.rejects(b.call(A,'close_tab',{tabId:c.tabId}), /session|owned|group/i);
});
test('concurrent requests for one session create one group', async () => {
  const b=browser(); await Promise.all(Array.from({length:8},()=>b.call(A,'create_tab')));
  assert.equal(new Set([...b.tabs.values()].map(t=>t.groupId)).size,1);
});
test('manually moving an owned tab into another group does not transfer its ownership', async () => {
  const b=browser(), a=await b.call(A,'create_tab'), c=await b.call(B,'create_tab');
  b.tabs.get(a.tabId).groupId=b.tabs.get(c.tabId).groupId;
  await assert.rejects(b.call(B,'tab_origin',{tabId:a.tabId}), /session|owned|group/i);
});

test('closed sessions do not consume active capacity and cannot resume', async () => {
  const b=browser();
  for(let i=1;i<=520;i++) {
    const id='js_'+i.toString(16).padStart(32,'0');
    await b.call(id,'create_tab'); await b.call(id,'session',{active:false,close:true});
  }
  const live=await b.call(A,'create_tab'); assert.ok(b.tabs.has(live.tabId));
  await assert.rejects(b.call('js_'+(1).toString(16).padStart(32,'0'),'create_tab'),/closed/i);
});
