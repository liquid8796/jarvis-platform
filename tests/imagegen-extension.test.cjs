'use strict';
// Execute the SHIPPING extension with deterministic browser/ChatGPT fixtures; no account, cookies or network.
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { webcrypto } = require('node:crypto');
const base = path.join(__dirname, '../jarvis-agent/src/Jarvis.Agent.Windows/Assets/Browser');
const A = 'js_' + 'a'.repeat(32), B = 'js_' + 'b'.repeat(32);
const J = 'ig_' + 'c'.repeat(32), K = 'ig_' + 'd'.repeat(32);
function browser() {
  const event = () => ({ listeners: [], addListener(fn) { this.listeners.push(fn); } });
  const local = {}, sessionStorage = {}, tabs = new Map(), groups = new Map(), replies = new Map(), downloads = new Map();
  const timerHandles = new Set();
  let context, requestId = 0, nextTab = 0, nextGroup = 0, nextDownload = 0;
  const state = { accountHash: 'a'.repeat(64), sendClicks: 0, uploads: 0, downloaded: 0, stopped: 0,
    namesReady: true, ready: true, userIds: [], imageKeys: ['assistant:https://chatgpt.com/backend-api/files/image'],
    href: 'https://chatgpt.com/backend-api/files/image/download', commands: [], boundRequests: [] };
  const store = target => ({ async get(key) { return { [key]: structuredClone(target[key]) }; },
    async set(value) { Object.assign(target, structuredClone(value)); } });
  const chrome = {
    runtime: { id: 'test-extension', getURL: name => 'chrome-extension://test-extension/' + name,
      onMessage: event(), getManifest: () => ({ version: '1.4.0' }),
      connectNative: () => ({ onMessage: event(), onDisconnect: event(), disconnect() {}, postMessage(m) {
        if (m.id) replies.set(m.id, m); if (m.event === 'imagegen_bind_browser') state.boundRequests.push(m);
      } }) },
    storage: { local: store(local), session: store(sessionStorage) },
    tabs: { onRemoved: event(),
      async create(o) { const t = { id: ++nextTab, groupId: -1, status: 'complete', windowId: o.windowId || 7, url: o.url, title: 'Fixture', active: o.active !== false }; tabs.set(t.id, t); return { ...t }; },
      async get(id) { if (!tabs.has(id)) throw Error('Missing tab'); return { ...tabs.get(id) }; },
      async query(q) { return [...tabs.values()].filter(t => (q.groupId === undefined || t.groupId === q.groupId) && (!q.active || t.active)).map(t => ({ ...t })); },
      async group(o) { const id = o.groupId ?? ++nextGroup; groups.set(id, {}); for (const tid of o.tabIds) tabs.get(tid).groupId = id; return id; },
      async update(id, o) { Object.assign(tabs.get(id), o); return { ...tabs.get(id) }; },
      async ungroup(id) { tabs.get(id).groupId = -1; },
      async remove(id) { tabs.delete(id); for (const fn of chrome.tabs.onRemoved.listeners) fn(id); } },
    tabGroups: { async get(id) { if (!groups.has(id)) throw Error('Missing group'); return groups.get(id); }, async update(id, o) { Object.assign(groups.get(id), o); } },
    windows: { async getLastFocused() { return { id: 7, type: 'normal' }; }, async update() {} },
    debugger: { onEvent: event(), onDetach: event(), async attach() {}, async detach() {},
      async sendCommand(target, method, parameters) {
        state.commands.push(method);
        if (method === 'DOM.getDocument') return { root: { nodeId: 1 } };
        if (method === 'DOM.querySelector') return { nodeId: 2 };
        if (method === 'DOM.setFileInputFiles') { state.uploads++; state.uploadPaths = [...parameters.files]; }
        if (method === 'Input.insertText') state.text = parameters.text;
        return {};
      } },
    scripting: { async executeScript({ target, func, args }) {
      if (func !== context.jarvisImageGenPage) return [{ result: {} }];
      const [action, data] = args; state.lastAction = action;
      const tab = tabs.get(target.tabId);
      const conv = (new URL(tab.url).pathname.match(/\/c\/([^/]+)/) || [])[1] || null;
      let result;
      switch (action) {
        case 'account': result = state.accountError ? { errorCode: state.accountError, message: 'Check account' } : { accountHash: state.accountHash }; break;
        case 'inspect': result = state.draft ? { errorCode: 'DRAFT_PRESENT', message: 'Draft preserved' } : { ready: state.ready, conversationId: conv, userIds: [...state.userIds] }; break;
        case 'mark_upload': result = { ready: true }; break;
        case 'upload_status': result = { ready: state.namesReady }; break;
        case 'focus_composer': result = state.namesReady ? { ready: true } : { errorCode: 'UPLOAD_INCOMPLETE', message: 'Not all images uploaded' }; break;
        case 'submit':
          state.sendClicks++; assert.equal(data.text, state.text); state.userIds.push('user-' + state.sendClicks);
          tab.url = 'https://chatgpt.com/c/conversation-' + target.tabId; result = { submitted: true }; break;
        case 'poll': result = state.pollError ? { errorCode: state.pollError, message: 'Synthetic ChatGPT refusal' }
          : { ready: state.imageKeys.length > 0, conversationId: conv, userMessageId: state.userIds.at(-1), keys: [...state.imageKeys] }; break;
        case 'download_plan': result = { href: state.href, conversationId: conv }; break;
        case 'click_save': result = { clicked: true }; break;
        case 'cancel': state.stopped++; result = { stopRequested: true, serverCancellationConfirmed: false }; break;
        default: throw Error('Unmocked page action ' + action);
      }
      return [{ result }];
    } },
    downloads: { onCreated: event(), onDeterminingFilename: event(),
      async download(o) { state.downloaded++; const id = ++nextDownload;
        downloads.set(id, { id, filename: 'C:\\Users\\Fixture\\Downloads\\' + o.filename.replaceAll('/', '\\'), url: o.url,
          referrer: '', mime: 'image/png', state: 'complete', danger: 'safe', fileSize: 123, startTime: new Date().toISOString() }); return id; },
      async search(q) { return downloads.has(q.id) ? [{ ...downloads.get(q.id) }] : []; },
      async cancel(id) { const file = downloads.get(id); if (file && file.state !== 'complete') file.state = 'interrupted'; } }
  };
  function reload() {
    chrome.runtime.onMessage.listeners.length = 0;
    chrome.downloads.onCreated.listeners.length = 0;
    chrome.downloads.onDeterminingFilename.listeners.length = 0;
    context = vm.createContext({ chrome, navigator: { userAgent: 'Chrome/150' }, console, URL, crypto: webcrypto,
      structuredClone, TextEncoder, AbortController,
      setTimeout(fn, duration) { const id = setTimeout(fn, duration); id.unref(); timerHandles.add(id); return id; },
      clearTimeout(id) { clearTimeout(id); timerHandles.delete(id); }, btoa: s => Buffer.from(s).toString('base64') });
    context.importScripts = (...names) => names.forEach(name => vm.runInContext(fs.readFileSync(path.join(base, name), 'utf8'), context, { filename: name }));
    vm.runInContext(fs.readFileSync(path.join(base, 'background.js'), 'utf8'), context, { filename: 'background.js' });
  }
  reload();
  async function call(sessionId, cmd, args = {}) {
    const id = String(++requestId); context.request = { id, sessionId, cmd, args };
    await vm.runInContext('onRequest(request)', context);
    const result = replies.get(id); assert.ok(result, 'Native call must return');
    if (!result.ok) throw Error(result.error);
    return JSON.parse(JSON.stringify(result.data));
  }
  async function config(action, args = {}, fromPage = false) {
    const message = { type: 'JARVIS_IMAGEGEN_CONFIG', action, ...args };
    const sender = { id: chrome.runtime.id, url: fromPage ? 'https://chatgpt.com/' : chrome.runtime.getURL('imagegen-popup.html') };
    return new Promise((resolve, reject) => {
      const listener = chrome.runtime.onMessage.listeners.at(-1);
      try { listener(message, sender, resolve); } catch (error) { reject(error); }
    });
  }
  async function prepare(sessionId = A, jobId = J) { await call(sessionId, 'imagegen_prepare', { jobId }); return call(sessionId, 'imagegen_inspect', { jobId }); }
  async function generate(sessionId = A, jobId = J) {
    await prepare(sessionId, jobId); await call(sessionId, 'imagegen_submit', { jobId, prompt: 'Create a small icon' });
    let result; for (let i = 0; i < 3; i++) result = await call(sessionId, 'imagegen_poll', { jobId });
    assert.equal(result.ready, true); return result;
  }
  return { call, config, prepare, generate, state, tabs, downloads, reload, chrome, local,
    context: () => context, dispose: () => timerHandles.forEach(clearTimeout) };
}

test('new image uses inactive tab in existing window and returns only verified download metadata', async t => {
  const b = browser(); t.after(b.dispose); await b.generate();
  const tab = [...b.tabs.values()][0]; assert.equal(tab.active, false); assert.equal(tab.windowId, 7);
  assert.equal(b.state.sendClicks, 1); assert.equal(b.state.uploads, 0);
  await b.call(A, 'imagegen_download', { jobId: J });
  const done = await b.call(A, 'imagegen_download_status', { jobId: J });
  assert.equal(done.ready, true); assert.equal(done.downloads.length, 1); assert.equal(b.state.downloaded, 1);
  assert.equal(done.downloads[0].downloadId, 1);
  assert.match(done.downloads[0].filename, /JarvisImageGen/);
  assert.doesNotMatch(JSON.stringify(done), /accountHash|accessToken|cookie|https:/);
});
test('local edits upload exact staged images and incomplete upload prevents Send', async t => {
  const b = browser(); t.after(b.dispose); await b.prepare();
  const paths = ['C:\\stage\\source-0.png', 'C:\\stage\\source-1.png'];
  await b.call(A, 'imagegen_upload', { jobId: J, paths });
  assert.deepEqual(b.state.uploadPaths, paths); assert.equal(b.state.uploads, 1);
  b.state.namesReady = false;
  assert.equal((await b.call(A, 'imagegen_upload_status', { jobId: J })).ready, false);
  const failed = await b.call(A, 'imagegen_submit', { jobId: J, prompt: 'Edit the two references' });
  assert.equal(failed.errorCode, 'UPLOAD_INCOMPLETE'); assert.equal(b.state.sendClicks, 0);
});
test('submit intent survives service worker restart and is never replayed', async t => {
  const b = browser(); t.after(b.dispose); await b.generate(); b.reload();
  const result = await b.call(A, 'imagegen_submit', { jobId: J, prompt: 'Create a small icon' });
  assert.equal(result.reconciliationRequired, true); assert.equal(b.state.sendClicks, 1);
  assert.equal((await b.call(A, 'imagegen_poll', { jobId: J })).ready, true);
});
test('foreign session cannot poll, cancel, download or adopt another sessions job/tab', async t => {
  const b = browser(); t.after(b.dispose); await b.generate(); await b.call(B, 'imagegen_state');
  for (const command of ['poll', 'cancel', 'download', 'download_status', 'submit'])
    await assert.rejects(b.call(B, 'imagegen_' + command, { jobId: J, prompt: 'x' }), /OWNED/);
  [...b.tabs.values()][0].active = true;
  assert.match((await b.config('adopt', { sessionId: B, confirmImageOnly: true })).error, /another session/i);
});
test('account switch blocks follow-up operations and the next job in the same session', async t => {
  const b = browser(); t.after(b.dispose); await b.generate(); b.state.accountHash = 'b'.repeat(64);
  assert.equal((await b.call(A, 'imagegen_download', { jobId: J })).errorCode, 'ACCOUNT_CHANGED');
  await b.call(A, 'imagegen_prepare', { jobId: K });
  assert.equal((await b.call(A, 'imagegen_inspect', { jobId: K })).errorCode, 'ACCOUNT_CHANGED');
  assert.equal(b.state.downloaded, 0); assert.equal(b.state.sendClicks, 1);
});
test('lost or navigated tab is not replaced by another browser/tab', async t => {
  const b = browser(); t.after(b.dispose); await b.generate();
  [...b.tabs.values()][0].url = 'https://example.com/';
  assert.equal((await b.call(A, 'imagegen_poll', { jobId: J })).errorCode, 'ORIGIN_CHANGED');
  await b.chrome.tabs.remove([...b.tabs.keys()][0]);
  const result = await b.call(A, 'imagegen_prepare', { jobId: K });
  assert.equal(result.errorCode, 'TAB_LOST'); assert.equal(b.tabs.size, 0);
});
test('draft is preserved and login/challenge failures never send', async t => {
  const b = browser(); t.after(b.dispose); b.state.draft = true;
  assert.equal((await b.prepare()).errorCode, 'DRAFT_PRESENT'); assert.equal(b.state.sendClicks, 0);
  b.state.draft = false; b.state.accountError = 'NOT_SIGNED_IN';
  assert.equal((await b.call(A, 'imagegen_inspect', { jobId: J })).errorCode, 'NOT_SIGNED_IN');
  b.state.accountError = 'CHALLENGE';
  assert.equal((await b.call(A, 'imagegen_inspect', { jobId: J })).errorCode, 'CHALLENGE');
  assert.equal(b.state.sendClicks, 0);
});
test('popup configuration is local-only and cannot be requested by a web page', async t => {
  const b = browser(); t.after(b.dispose);
  assert.match((await b.config('bind', {}, true)).error, /restricted/); assert.equal(b.state.boundRequests.length, 0);
  assert.equal((await b.config('bind')).requested, true); assert.equal(b.state.boundRequests.length, 1);
});
test('explicitly adopted image-only tab survives session close', async t => {
  const b = browser(); t.after(b.dispose); await b.call(A, 'imagegen_state');
  const tab = await b.chrome.tabs.create({ url: 'https://chatgpt.com/c/own-image-conversation', active: true });
  const adopted = await b.config('adopt', { sessionId: A, confirmImageOnly: true }); assert.equal(adopted.requested, true);
  await b.call(A, 'session', { active: false, close: true }); assert.ok(b.tabs.has(tab.id));
});
test('page-returned foreign URL and unrelated Downloads file are not accepted', async t => {
  const b = browser(); t.after(b.dispose); await b.generate();
  b.state.href = 'https://attacker.example/image.png';
  assert.equal((await b.call(A, 'imagegen_download', { jobId: J })).errorCode, 'RESULT_UNVERIFIED'); assert.equal(b.state.downloaded, 0);
  b.state.href = 'https://chatgpt.com/backend-api/files/image/download'; await b.call(A, 'imagegen_download', { jobId: J });
  b.downloads.get(1).filename = 'C:\\Users\\Fixture\\Downloads\\other.png';
  assert.equal((await b.call(A, 'imagegen_download_status', { jobId: J })).errorCode, 'DOWNLOAD_UNVERIFIED');
});
test('safe MIME and browser safety status are required before accepting a download', async t => {
  const b = browser(); t.after(b.dispose); await b.generate(); await b.call(A, 'imagegen_download', { jobId: J });
  b.downloads.get(1).mime = 'text/html'; assert.equal((await b.call(A, 'imagegen_download_status', { jobId: J })).errorCode, 'DOWNLOAD_UNVERIFIED');
  b.downloads.get(1).mime = 'image/png'; b.downloads.get(1).danger = 'dangerous';
  assert.equal((await b.call(A, 'imagegen_download_status', { jobId: J })).errorCode, 'DOWNLOAD_UNVERIFIED');
});
test('ChatGPT refusal/limit propagates without downloads or additional sends', async t => {
  const b = browser(); t.after(b.dispose); await b.generate();
  for (const code of ['GENERATION_REJECTED', 'QUOTA_EXCEEDED']) {
    b.state.pollError = code; assert.equal((await b.call(A, 'imagegen_poll', { jobId: J })).errorCode, code);
  }
  assert.equal(b.state.sendClicks, 1); assert.equal(b.state.downloaded, 0);
});
test('originals are downloaded individually and all results are required', async t => {
  const b = browser(); t.after(b.dispose); b.state.imageKeys = ['one', 'two', 'three']; await b.generate();
  await b.call(A, 'imagegen_download', { jobId: J });
  assert.equal(b.state.downloaded, 1);
  assert.equal((await b.call(A, 'imagegen_download_status', { jobId: J })).ready, false);
  const result = await b.call(A, 'imagegen_download_status', { jobId: J }); assert.equal(result.ready, true);
  assert.equal(result.downloads.length, 3); assert.equal(b.state.downloaded, 3);
});
test('asset allowlist rejects paid API, localhost, credentials and deceptive hostnames', t => {
  const b = browser(); t.after(b.dispose); const allowed = b.context().JarvisImageGen.allowedAsset;
  for (const u of ['https://api.openai.com/v1/images/generations', 'http://localhost/image', 'https://chatgpt.com.attacker.example/backend-api/x', 'https://user:pass@chatgpt.com/backend-api/x', 'file:///C:/secret.png']) assert.equal(allowed(u), false, u);
  assert.equal(allowed('https://chatgpt.com/backend-api/files/x/download'), true);
  assert.equal(allowed('https://files.oaiusercontent.com/generated/image.png'), true);
});
test('page adapter fails closed on wrong origin without performing network requests', async () => {
  const context = vm.createContext({ location: { origin: 'https://evil.example' } });
  vm.runInContext(fs.readFileSync(path.join(base, 'imagegen-page.js'), 'utf8'), context);
  const result = await context.jarvisImageGenPage('account'); assert.equal(result.errorCode, 'ORIGIN_CHANGED');
});

test('an unfinished pre-send job cannot be moved to another tab', async t => {
  const b = browser(); t.after(b.dispose); await b.prepare();
  await b.chrome.tabs.create({ url: 'https://chatgpt.com/c/another', active: true });
  const result = await b.config('adopt', { sessionId: A, confirmImageOnly: true });
  assert.match(result.error, /Finish the active job/);
});
test('oversized owned downloads are cancelled while unrelated downloads remain untouched', async t => {
  const b = browser(); t.after(b.dispose); await b.generate(); await b.call(A, 'imagegen_download', { jobId: J });
  const owned = b.downloads.get(1); owned.state = 'in_progress'; owned.totalBytes = 25 * 1024 * 1024;
  b.downloads.set(999, { state: 'in_progress' });
  const result = await b.call(A, 'imagegen_download_status', { jobId: J });
  assert.equal(result.errorCode, 'DOWNLOAD_TOO_LARGE'); assert.equal(owned.state, 'interrupted');
  assert.equal(b.downloads.get(999).state, 'in_progress');
});
test('actual page adapter refuses stale attachments before creating a new job', async () => {
  const form = { querySelector: selector => selector === 'img' ? {} : null, querySelectorAll: () => [] };
  const editor = { innerText: '', closest: () => form };
  const document = { querySelector: selector => selector.startsWith('#prompt-textarea') ? editor : null, querySelectorAll: () => [] };
  const context = vm.createContext({ location: { origin: 'https://chatgpt.com', pathname: '/' }, document });
  vm.runInContext(fs.readFileSync(path.join(base, 'imagegen-page.js'), 'utf8'), context);
  assert.equal((await context.jarvisImageGenPage('inspect')).errorCode, 'ATTACHMENTS_PRESENT');
});
