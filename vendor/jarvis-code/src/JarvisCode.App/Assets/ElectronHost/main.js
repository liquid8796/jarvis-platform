// The Browser pane's engine process.
//
// The pane is one frameless BrowserWindow that the WPF app reparents into
// itself (SetParent + WS_CHILD), holding one WebContentsView per tab — the
// architecture the reference desktop uses for the same pane, where its own
// bundle builds tabs out of WebContentsView rather than a window each. The
// host window never positions or sizes itself: WPF owns its rectangle through
// MoveWindow on the reparented HWND, and this process only lays the active
// view out to fill whatever content bounds it ends up with.
//
// The protocol is newline-delimited JSON over a Windows named pipe whose name
// the host passes as --pipe=. It is deliberately not stdio: in Electron's main
// process on Windows process.stdin reaches EOF immediately, so a sidecar that
// took its commands there emits 'end' and exits before app.whenReady() ever
// fires - measured on Electron 42.10.0, not assumed. The pipe doubles as the
// shutdown signal, since losing it means the app that owns this engine is gone.
//
// Requests carry an id and are answered exactly once; events carry no id and
// are never answered.

const net = require('net');
const nodePath = require('path');
const { app, BrowserWindow, WebContentsView, shell, protocol, session, net: electronNet } = require('electron');

// A folder the host wants served to a page needs a real origin, not file://:
// the diagram renderer's page runs mermaid in a sandboxed iframe, and a
// file:// document is opaque-origin enough to break it. A privileged scheme
// has to be declared before the app is ready, so it is declared always and
// only gains handlers when a surface asks for one.
const ASSET_SCHEME = 'jarvis-asset';
protocol.registerSchemesAsPrivileged([{
  scheme: ASSET_SCHEME,
  privileges: { standard: true, secure: true, supportFetchAPI: true, corsEnabled: true },
}]);

/** host segment -> directory, for ASSET_SCHEME. */
const assetRoots = new Map();
const assetSessions = new WeakSet();
const storageSessions = new Set();

function storageSession(partition) {
  const value = partition ? session.fromPartition(partition) : session.defaultSession;
  storageSessions.add(value);
  installAssetHandler(value);
  return value;
}

function installAssetHandler(value) {
  if (assetSessions.has(value)) return;
  assetSessions.add(value);
  value.protocol.handle(ASSET_SCHEME, (request) => {
    const url = new URL(request.url);
    const root = assetRoots.get(url.hostname);
    if (!root) return new Response('no such asset host', { status: 404 });
    const file = nodePath.resolve(nodePath.join(root, decodeURIComponent(url.pathname)));
    if (file !== root && !file.startsWith(root + nodePath.sep)) {
      return new Response('outside the asset root', { status: 403 });
    }
    return electronNet.fetch('file:///' + file.split(nodePath.sep).join('/'));
  });
}

function requestedSession(windowId) {
  return windowId ? requireWindow(windowId).storage : storageSession('');
}

/** The host's end of the protocol, once connected. */
let socket = null;
let shuttingDown = false;

async function shutdown(code) {
  if (shuttingDown) return;
  shuttingDown = true;
  // Persistent jars are shared by windows, so closing a window is not their
  // lifetime boundary. Flush at the engine's boundary before exiting instead.
  await Promise.all([...storageSessions].map(async (value) => {
    value.flushStorageData();
    try { await value.cookies.flushStore(); } catch { /* best effort on shutdown */ }
  }));
  app.exit(code);
}

// ---- wire ------------------------------------------------------------------

function write(payload) {
  if (socket && !socket.destroyed) {
    socket.write(JSON.stringify(payload) + '\n');
  }
}

function emit(event, body) {
  write(Object.assign({ event }, body));
}

function fail(id, error) {
  write({ id, ok: false, error: String(error && error.message ? error.message : error) });
}

// ---- state -----------------------------------------------------------------

// The app reparents more than one engine window: the Browser pane is one, and
// every other surface that used to be its own WebView2 - an artifact tile, a
// question preview, the diagram renderer - is another. Each is a window here,
// with its own tabs, so they share one Chromium rather than a process each.
/** windowId -> { id, win, activeTabId } */
const windows = new Map();
let nextWindowId = 1;

/** tabId -> { id, windowId, view, attached, url, title, deniedMedia:Set } */
const tabs = new Map();
let nextTabId = 1;

function requireWindow(windowId) {
  const entry = windows.get(windowId ?? defaultWindowId());
  if (!entry || entry.win.isDestroyed()) {
    throw new Error('no engine window ' + (windowId ?? '(default)'));
  }
  return entry;
}

/** The only window, when a caller names none - which is how the pane calls. */
function defaultWindowId() {
  return windows.size === 1 ? [...windows.keys()][0] : null;
}

function requireTab(tabId) {
  const tab = tabs.get(tabId);
  if (!tab || tab.view.webContents.isDestroyed()) {
    throw new Error('no tab ' + tabId);
  }
  return tab;
}

// ---- layout ----------------------------------------------------------------

// Only the active view is given the window's full content rectangle; the others
// keep running with an empty rectangle, which is what lets a background tab
// hold its page (and its timers) exactly as a WebView2 per tab used to.
function layout(windowId) {
  const entry = windows.get(windowId);
  if (!entry || entry.win.isDestroyed()) {
    return;
  }
  const [width, height] = entry.win.getContentSize();
  for (const tab of tabs.values()) {
    if (tab.windowId !== windowId || tab.view.webContents.isDestroyed()) {
      continue;
    }
    tab.view.setBounds(tab.id === entry.activeTabId
      ? { x: 0, y: 0, width, height }
      : { x: 0, y: 0, width: 0, height: 0 });
  }
}

function layoutAll() {
  for (const id of windows.keys()) {
    layout(id);
  }
}

// ---- tab wiring ------------------------------------------------------------

function attachDebugger(tab) {
  if (tab.attached) {
    return;
  }
  const dbg = tab.view.webContents.debugger;
  dbg.attach('1.3');
  dbg.on('message', (_event, method, params) => {
    emit('cdp', { tabId: tab.id, method, params });
  });
  dbg.on('detach', (_event, reason) => {
    tab.attached = false;
    emit('cdp-detached', { tabId: tab.id, reason: String(reason) });
  });
  tab.attached = true;
}

function wire(tab) {
  const wc = tab.view.webContents;

  wc.on('page-title-updated', (_e, title) => {
    tab.title = title;
    emit('tab-title', { tabId: tab.id, title });
  });

  wc.on('did-navigate', (_e, url) => {
    tab.url = url;
    tab.deniedMedia.clear();
    emit('tab-navigated', { tabId: tab.id, url });
  });

  wc.on('did-navigate-in-page', (_e, url, isMainFrame) => {
    if (isMainFrame) {
      tab.url = url;
      emit('tab-navigated', { tabId: tab.id, url, inPage: true });
    }
  });

  wc.on('did-fail-load', (_e, errorCode, errorDescription, validatedURL, isMainFrame) => {
    if (isMainFrame) {
      emit('tab-load-failed', { tabId: tab.id, errorCode, errorDescription, url: validatedURL });
    }
  });

  // The engine draws no menu of its own; the host builds one from these.
  wc.on('context-menu', (_e, params) => {
    emit('tab-context-menu', {
      tabId: tab.id,
      params: {
        x: params.x,
        y: params.y,
        linkURL: params.linkURL || '',
        srcURL: params.srcURL || '',
        mediaType: params.mediaType || 'none',
        selectionText: params.selectionText || '',
        isEditable: params.isEditable === true,
      },
    });
  });

  wc.on('render-process-gone', (_e, details) => {
    emit('tab-gone', { tabId: tab.id, reason: details && details.reason });
  });

  // target=_blank and window.open: the pane decides between a new tab here and
  // the system browser, so the decision is reported rather than taken.
  wc.setWindowOpenHandler(({ url, disposition }) => {
    // A page opening a window for itself - a sign-in popup - is not the same as
    // a link asking for a new tab, and the host guards the two differently.
    emit('tab-open-request', {
      tabId: tab.id,
      url,
      disposition,
      popup: disposition === 'new-window',
    });
    return { action: 'deny' };
  });

  // A navigation that turns out to be a file rejects loadURL with ERR_ABORTED,
  // which on its own reads like any other failed load. The download is what
  // says otherwise, so the tab remembers that one just started.
  wc.session.on('will-download', (_e, _item, contents) => {
    if (contents === wc) {
      tab.downloadedAt = Date.now();
    }
  });

  // Camera and microphone are refused in the pane, as they are in the
  // reference; the refused kinds accumulate for the result trailer's Note.
  wc.session.setPermissionRequestHandler((_contents, permission, callback) => {
    if (permission === 'media' || permission === 'camera' || permission === 'microphone') {
      tab.deniedMedia.add(permission === 'media' ? 'camera' : permission);
      emit('tab-permission-denied', { tabId: tab.id, permission });
      callback(false);
      return;
    }
    callback(false);
  });
}

// A view that has never navigated has no document, and CDP on it does not
// answer at all - it hangs rather than erroring (measured on Electron 42.10.0:
// DOM.getDocument on a fresh view never returns, while the same view answers in
// milliseconds once it holds about:blank, background and zero-sized or not). So
// a tab is given a blank document before it is handed out. tab.url stays empty,
// which is what keeps hasPage false for a tab the user has not sent anywhere.
async function createTab(windowId, url, popup) {
  const entry = requireWindow(windowId);
  // A window the page opened for itself carries its kind in its id, which is
  // what the guard on the host side reads.
  const id = (popup ? 'popup-' : 'tab-') + nextTabId++;
  const view = new WebContentsView({
    // Native dialogs are disabled, as they are in the reference. With Page
    // enabled a dialog is routed to the debugger instead of shown, and nothing
    // answers it: alert() then wedges the tab for good, not just that call.
    webPreferences: {
      session: entry.storage,
      sandbox: true,
      contextIsolation: true,
      nodeIntegration: false,
      disableDialogs: true,
    },
  });
  const tab = {
    id,
    windowId: entry.id,
    view,
    attached: false,
    url: url || '',
    title: 'New tab',
    deniedMedia: new Set(),
    popup: popup === true,
    downloadedAt: 0,
  };
  tabs.set(id, tab);
  entry.win.contentView.addChildView(view);
  wire(tab);
  attachDebugger(tab);
  await view.webContents.loadURL('about:blank');
  return tab;
}

function closeTab(tabId) {
  const tab = tabs.get(tabId);
  if (!tab) {
    return { found: false, wasLast: false };
  }
  const entry = windows.get(tab.windowId);
  const siblings = [...tabs.values()].filter(t => t.windowId === tab.windowId);
  const wasLast = siblings.length === 1;
  try {
    if (tab.attached && tab.view.webContents.debugger.isAttached()) {
      tab.view.webContents.debugger.detach();
    }
  } catch {
    // A debugger that is already gone is not an error worth failing the close over.
  }
  if (entry && !entry.win.isDestroyed()) {
    try {
      entry.win.contentView.removeChildView(tab.view);
    } catch {
      // Removing a view the window already dropped is a no-op.
    }
  }
  try {
    tab.view.webContents.close();
  } catch {
    // Same: a destroyed webContents needs no closing.
  }
  tabs.delete(tabId);
  if (entry && entry.activeTabId === tabId) {
    const left = [...tabs.values()].filter(t => t.windowId === entry.id);
    entry.activeTabId = left.length > 0 ? left[left.length - 1].id : null;
  }
  layout(tab.windowId);
  return { found: true, wasLast };
}

function tabRow(tab) {
  const entry = windows.get(tab.windowId);
  return {
    tabId: tab.id,
    windowId: tab.windowId,
    url: tab.url,
    title: tab.title,
    active: entry ? tab.id === entry.activeTabId : false,
    hasPage: Boolean(tab.url),
    popup: tab.popup === true,
    deniedMedia: [...tab.deniedMedia],
  };
}

// ---- commands --------------------------------------------------------------

const commands = {
  'host.create'({ show, width, height, partition, x, y }) {
    const id = 'win-' + nextWindowId++;
    const storage = storageSession(partition);
    const win = new BrowserWindow({
      width: width || 1024,
      height: height || 768,
      ...(Number.isFinite(x) && Number.isFinite(y) ? { x, y } : {}),
      show: false,
      frame: false,
      // These are child surfaces, never independent applications. On Windows
      // skipTaskbar only removes the current shell button; toolbar supplies
      // WS_EX_TOOLWINDOW before WPF shows/reparents the HWND, so later native
      // show/hide cycles cannot register a second Electron taskbar button.
      ...(process.platform === 'win32' ? { type: 'toolbar' } : {}),
      skipTaskbar: true,
      backgroundColor: '#00000000',
      webPreferences: { sandbox: true, session: storage },
    });
    const entry = { id, win, activeTabId: null, storage };
    windows.set(id, entry);
    win.on('resize', () => layout(id));
    win.on('closed', () => {
      windows.delete(id);
      for (const [tabId, tab] of [...tabs]) {
        if (tab.windowId === id) {
          tabs.delete(tabId);
        }
      }
      emit('host-closed', { windowId: id });
    });
    if (show) {
      win.showInactive();
    }
    return { windowId: id, hwnd: handleOf(win) };
  },

  'host.show'({ windowId }) {
    const entry = requireWindow(windowId);
    entry.win.showInactive();
    layout(entry.id);
    return {};
  },

  'host.hide'({ windowId }) {
    requireWindow(windowId).win.hide();
    return {};
  },

  'host.close'({ windowId }) {
    const entry = windows.get(windowId ?? defaultWindowId());
    if (entry && !entry.win.isDestroyed()) {
      entry.win.destroy();
    }
    return {};
  },

  'host.layout'({ windowId }) {
    if (windowId) {
      layout(windowId);
    } else {
      layoutAll();
    }
    return {};
  },

  async 'tab.create'({ windowId, url, foreground, popup }) {
    const entry = requireWindow(windowId);
    const tab = await createTab(entry.id, url, popup === true);
    if (foreground !== false || entry.activeTabId === null) {
      entry.activeTabId = tab.id;
    }
    layout(entry.id);
    return { tabId: tab.id, windowId: entry.id };
  },

  'tab.select'({ tabId }) {
    const tab = tabs.get(tabId);
    if (!tab) {
      return { found: false };
    }
    const entry = windows.get(tab.windowId);
    if (entry) {
      entry.activeTabId = tabId;
      layout(entry.id);
    }
    return { found: true };
  },

  'tab.close'({ tabId }) {
    return closeTab(tabId);
  },

  'tab.list'({ windowId }) {
    const id = windowId ?? defaultWindowId();
    const entry = windows.get(id);
    return {
      tabs: [...tabs.values()].filter(t => !id || t.windowId === id).map(tabRow),
      activeTabId: entry ? entry.activeTabId : null,
      windowId: id,
    };
  },

  async 'tab.navigate'({ tabId, url }) {
    const tab = requireTab(tabId);
    const before = tab.downloadedAt;
    try {
      await tab.view.webContents.loadURL(url);
    } catch (error) {
      // A load the browser abandoned because the address was a file, rather
      // than one that failed: the caller is told which it was.
      if (tab.downloadedAt > before) {
        return { url: tab.url, downloaded: true };
      }
      throw error;
    }
    tab.url = tab.view.webContents.getURL();
    return { url: tab.url, downloaded: tab.downloadedAt > before };
  },

  // Where a back/forward move would land, without moving. The reference reads
  // the adjacent entry the same way before it decides whether the move is
  // allowed at all.
  'tab.historyTarget'({ tabId, direction }) {
    const tab = requireTab(tabId);
    const nav = tab.view.webContents.navigationHistory;
    const can = direction === 'back' ? nav.canGoBack() : nav.canGoForward();
    if (!can) {
      return { url: null };
    }
    const index = nav.getActiveIndex() + (direction === 'back' ? -1 : 1);
    return { url: nav.getEntryAtIndex(index)?.url ?? null };
  },

  'tab.history'({ tabId, direction }) {
    const tab = requireTab(tabId);
    const nav = tab.view.webContents.navigationHistory;
    const can = direction === 'back' ? nav.canGoBack() : nav.canGoForward();
    if (!can) {
      return { moved: false };
    }
    if (direction === 'back') {
      nav.goBack();
    } else {
      nav.goForward();
    }
    return { moved: true };
  },

  'tab.notes'({ tabId }) {
    const tab = requireTab(tabId);
    return {
      title: tab.view.webContents.getTitle(),
      url: tab.view.webContents.getURL(),
      deniedMedia: [...tab.deniedMedia],
    };
  },

  async 'cdp.send'({ tabId, method, params }) {
    const tab = requireTab(tabId);
    attachDebugger(tab);
    const result = await tab.view.webContents.debugger.sendCommand(method, params || {});
    return { result };
  },

  // Chromium's own find, which the engine exposes directly. The pane used to
  // run window.find in the page because WebView2 1.0.2903 has no find API; this
  // is the real one, so the highlight and match count are the browser's.
  'tab.find'({ tabId, query, forward, findNext }) {
    const tab = requireTab(tabId);
    if (!query) {
      tab.view.webContents.stopFindInPage('clearSelection');
      return { requestId: 0 };
    }
    return {
      requestId: tab.view.webContents.findInPage(query, {
        forward: forward !== false,
        findNext: findNext === true,
      }),
    };
  },

  // Explicit user action only; transient surfaces use in-memory partitions.
  async 'session.clear'({ windowId }) {
    const storage = requestedSession(windowId);
    await storage.clearStorageData();
    await storage.clearCache();
    return {};
  },

  // Serves one folder at jarvis-asset://{host}/, so a page can be loaded from
  // an origin the browser treats as secure. Paths are resolved inside the root
  // and anything that climbs out of it is refused.
  'assets.serve'({ host, directory }) {
    if (!host || !directory) {
      throw new Error('assets.serve needs a host and a directory');
    }
    assetRoots.set(host, nodePath.resolve(directory));
    installAssetHandler(storageSession(''));
    return { scheme: ASSET_SCHEME };
  },

  // The cookies a signed-in session is made of. Set before the first
  // navigation, or the page loads signed out and redirects.
  async 'session.setCookies'({ cookies, windowId }) {
    if (!Array.isArray(cookies)) {
      throw new Error('session.setCookies needs an array');
    }
    let set = 0;
    for (const cookie of cookies) {
      try {
        await requestedSession(windowId).cookies.set(cookie);
        set++;
      } catch {
        // One cookie the browser will not take is not worth failing the
        // sign-in over; the caller counts what landed.
      }
    }
    return { set, of: cookies.length };
  },

  // Chromium's Page.printToPDF is a headless-only DevTools method and is not
  // there over this debugger; printing is the engine's own call.
  async 'tab.printToPDF'({ tabId, printBackground }) {
    const tab = requireTab(tabId);
    const pdf = await tab.view.webContents.printToPDF({
      printBackground: printBackground !== false,
    });
    return { data: pdf.toString('base64') };
  },

  // The user agent the page sees. Setting it on the session only reaches
  // contents made afterwards, so the tabs that already exist are set too and
  // the call does not depend on being made before them.
  'session.setUserAgent'({ userAgent }) {
    if (!userAgent) {
      throw new Error('session.setUserAgent needs a userAgent');
    }
    session.defaultSession.setUserAgent(userAgent);
    for (const tab of tabs.values()) {
      if (!tab.view.webContents.isDestroyed()) {
        tab.view.webContents.setUserAgent(userAgent);
      }
    }
    return {};
  },

  'shell.open'({ url }) {
    shell.openExternal(url);
    return {};
  },

  'app.versions'() {
    return { electron: process.versions.electron, chrome: process.versions.chrome };
  },
};

function handleOf(win) {
  const buffer = win.getNativeWindowHandle();
  // The handle is the process's own pointer width; Windows x64 is 8 bytes.
  return buffer.length >= 8
    ? buffer.readBigUInt64LE(0).toString()
    : String(buffer.readUInt32LE(0));
}

// ---- dispatch --------------------------------------------------------------

async function dispatch(message) {
  const handler = commands[message.cmd];
  if (!handler) {
    fail(message.id, 'unknown command ' + message.cmd);
    return;
  }
  try {
    const result = await handler(message.args || {});
    write({ id: message.id, ok: true, result: result || {} });
  } catch (error) {
    fail(message.id, error);
  }
}

function pipePath() {
  for (const arg of process.argv) {
    if (arg.startsWith('--pipe=')) {
      return '\\\\.\\pipe\\' + arg.slice('--pipe='.length);
    }
  }
  return null;
}

function connect() {
  const path = pipePath();
  if (!path) {
    // Nothing can drive this process and nothing can hear it say so.
    app.exit(2);
    return;
  }

  socket = net.connect({ path });
  socket.setNoDelay(true);

  let pending = '';
  socket.on('data', (chunk) => {
    pending += chunk.toString('utf8');
    let cut;
    while ((cut = pending.indexOf('\n')) >= 0) {
      const line = pending.slice(0, cut).trim();
      pending = pending.slice(cut + 1);
      if (line.length === 0) {
        continue;
      }
      let message;
      try {
        message = JSON.parse(line);
      } catch {
        continue;
      }
      dispatch(message);
    }
  });

  // The host going away is the shutdown signal: there is nobody left to
  // render for, so the engine goes with it rather than outliving the app.
  socket.on('close', () => shutdown(0));
  socket.on('error', () => shutdown(3));

  emit('ready', { electron: process.versions.electron, chrome: process.versions.chrome });
}

app.commandLine.appendSwitch('disable-features', 'CalculateNativeWinOcclusion');

// A surface may need Chromium started differently - the ChatGPT session needs
// the automation flag suppressed, or the page is flagged before its cookies are
// even read - and a switch has to be set before the app is ready, so it comes
// in on argv rather than as a command.
for (const arg of process.argv) {
  if (arg.startsWith('--chromium-switch=')) {
    const value = arg.slice('--chromium-switch='.length);
    const split = value.indexOf('=');
    if (split > 0) {
      app.commandLine.appendSwitch(value.slice(0, split), value.slice(split + 1));
    } else {
      app.commandLine.appendSwitch(value);
    }
  }
}

app.whenReady().then(connect);

app.on('window-all-closed', () => {
  // The pane is closed and reopened many times in one session; the process
  // stays alive so the next open does not pay for a fresh Chromium start.
});
