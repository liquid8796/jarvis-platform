// Jarvis Browser — connects this browser to the Jarvis Code desktop app over
// native messaging. The app sends {id, cmd, args}; we reply {id, ok, data|error}.
//
// Beyond tab control and DOM scripting, the inspection surface (console,
// network, screenshots, trusted input, viewport emulation, JS eval) rides the
// Chrome DevTools Protocol via chrome.debugger, attached lazily per tab.

const HOST = "com.jarvis.agent.browser";
let port = null;
let reconnectDelay = 1000;

function connect() {
  try {
    port = chrome.runtime.connectNative(HOST);
  } catch (e) {
    schedule();
    return;
  }
  reconnectDelay = 1000;
  port.onMessage.addListener(onRequest);
  port.onDisconnect.addListener(() => {
    port = null;
    // Nothing is driving these pages any more; leaving a glow up would lie.
    clearIndicators("off");
    schedule();
  });
  post({ event: "ready", version: chrome.runtime.getManifest().version, browser: browserName() });
}

// Which Chromium this is, so the app can tell several connected browsers apart.
// Order matters: every Chromium fork also says "Chrome/…" in its UA.
function browserName() {
  const ua = navigator.userAgent;
  if (ua.includes("Edg/")) return "Edge";
  if (ua.includes("OPR/")) return "Opera";
  if (ua.includes("Vivaldi/")) return "Vivaldi";
  if (navigator.brave) return "Brave";
  if (ua.includes("Chrome/")) return "Chrome";
  return "Chromium";
}

function schedule() {
  setTimeout(connect, reconnectDelay);
  reconnectDelay = Math.min(reconnectDelay * 2, 30000);
}

function post(message) {
  if (port) {
    try { port.postMessage(message); } catch (e) { /* port died; reconnect fires */ }
  }
}

async function onRequest(message) {
  if (!message || !message.id) return;
  try {
    const data = await handle(message.cmd, message.args || {});
    post({ id: message.id, ok: true, data });
  } catch (e) {
    post({ id: message.id, ok: false, error: String(e && e.message ? e.message : e) });
  }
}

// ---- the Jarvis tab group ---------------------------------------------------
//
// Every tab the agent drives lives in one Chrome tab group, so the user can see
// at a glance which tabs are being controlled and the agent cannot wander into
// the rest of the window. Tabs join the group by being created here or by an
// explicit browser_tab_select; nothing else is drivable.

const GROUP_TITLE = "Jarvis";
let groupId = null;

/** The group's id, creating it around a tab when there isn't one yet. */
async function ensureGroup(tabId) {
  // An extension loaded before tab groups were asked for has no API to group
  // with, and silently driving ungrouped tabs is exactly what this prevents.
  if (!chrome.tabGroups) {
    throw new Error(
      "This browser's Jarvis Browser extension predates the tab group and cannot be driven safely. " +
      "Reload it at chrome://extensions (its manifest now needs the tabGroups permission).");
  }

  if (groupId !== null) {
    try {
      await chrome.tabGroups.get(groupId);
    } catch (e) {
      groupId = null;
    }
  }

  if (groupId === null) {
    groupId = await chrome.tabs.group({ tabIds: [tabId] });
    await chrome.tabGroups.update(groupId, { title: GROUP_TITLE, color: "orange" });
    return groupId;
  }

  const tab = await chrome.tabs.get(tabId);
  if (tab.groupId !== groupId) {
    await chrome.tabs.group({ tabIds: [tabId], groupId });
  }
  return groupId;
}

async function inGroup(tab) {
  if (!chrome.tabGroups || groupId === null || tab.groupId !== groupId) return false;
  try {
    await chrome.tabGroups.get(groupId);
    return true;
  } catch (e) {
    groupId = null;
    return false;
  }
}

/**
 * The tab a page-facing command acts on. Only tabs in the Jarvis group qualify:
 * the model has to open one (browser_tab_new / browser_navigate) or the user's
 * own tab has to be adopted deliberately with browser_tab_select.
 */
async function targetTab(args) {
  if (args.tabId) {
    const tab = await chrome.tabs.get(args.tabId);
    if (!await inGroup(tab)) {
      throw new Error(
        `Tab ${args.tabId} is not in the ${GROUP_TITLE} tab group. Open a tab with browser_tab_new, ` +
        `or adopt this one with browser_tab_select — only grouped tabs can be driven.`);
    }
    return tab;
  }

  if (groupId !== null) {
    const grouped = await chrome.tabs.query({ groupId });
    const active = grouped.find(t => t.active) || grouped[0];
    if (active) return active;
  }

  throw new Error(
    `No tab is in the ${GROUP_TITLE} tab group yet. Call browser_tab_new to open one, or ` +
    "browser_tab_select with a tab id from browser_tabs to adopt an existing tab.");
}

// ---- CDP session management ------------------------------------------------

const CONSOLE_CAP = 1000;
const NETWORK_CAP = 500;

/** tabId -> { console: [], network: Map(requestId -> entry), netOrder: [] } */
const attached = new Map();

function debuggerSend(tabId, method, params) {
  return chrome.debugger.sendCommand({ tabId }, method, params || {});
}

async function ensureAttached(tabId) {
  const existing = attached.get(tabId);
  if (existing) return existing;
  await chrome.debugger.attach({ tabId }, "1.3");
  const state = { console: [], network: new Map(), netOrder: [], startedAt: Date.now() };
  attached.set(tabId, state);
  await debuggerSend(tabId, "Runtime.enable");
  await debuggerSend(tabId, "Log.enable");
  await debuggerSend(tabId, "Network.enable");
  await debuggerSend(tabId, "Page.enable");
  return state;
}

/** Attach quietly for capture; pages the debugger cannot reach (chrome://) just skip it. */
async function tryAttach(tabId) {
  try { await ensureAttached(tabId); } catch (e) { /* capture unavailable there */ }
}

function pushConsole(state, entry) {
  state.console.push(entry);
  if (state.console.length > CONSOLE_CAP) state.console.shift();
}

function remoteToText(o) {
  if (!o) return "";
  if (o.type === "string") return o.value;
  if (o.unserializableValue) return o.unserializableValue;
  if ("value" in o) {
    try { return typeof o.value === "object" ? JSON.stringify(o.value) : String(o.value); }
    catch (e) { return String(o.value); }
  }
  if (o.preview && o.preview.properties) {
    const inner = o.preview.properties.map(p => `${p.name}: ${p.value}`).join(", ");
    return `${o.preview.description || ""}{${inner}}`;
  }
  return o.description || o.type;
}

chrome.debugger.onEvent.addListener((source, method, params) => {
  const state = source.tabId != null ? attached.get(source.tabId) : null;
  if (!state) return;

  switch (method) {
    case "Runtime.consoleAPICalled":
      pushConsole(state, {
        level: params.type === "warning" ? "warn" : params.type,
        text: (params.args || []).map(remoteToText).join(" "),
        ts: params.timestamp,
      });
      break;

    case "Runtime.exceptionThrown": {
      const d = params.exceptionDetails || {};
      const desc = (d.exception && (d.exception.description || d.exception.value)) || d.text || "Uncaught exception";
      pushConsole(state, { level: "error", text: String(desc), url: d.url, ts: params.timestamp });
      break;
    }

    case "Log.entryAdded": {
      const e = params.entry || {};
      pushConsole(state, {
        level: e.level === "warning" ? "warn" : (e.level || "log"),
        text: e.text || "",
        url: e.url,
        source: e.source,
        ts: e.timestamp,
      });
      break;
    }

    case "Network.requestWillBeSent": {
      const r = params.request || {};
      const entry = {
        requestId: params.requestId,
        url: r.url,
        method: r.method,
        type: params.type,
        status: 0,
        finished: false,
        ts: params.timestamp,
      };
      if (!state.network.has(params.requestId)) {
        state.netOrder.push(params.requestId);
        if (state.netOrder.length > NETWORK_CAP) {
          state.network.delete(state.netOrder.shift());
        }
      }
      state.network.set(params.requestId, entry);
      break;
    }

    case "Network.responseReceived": {
      const entry = state.network.get(params.requestId);
      if (entry) {
        const resp = params.response || {};
        entry.status = resp.status;
        entry.statusText = resp.statusText;
        entry.mimeType = resp.mimeType;
      }
      break;
    }

    case "Network.loadingFinished": {
      const entry = state.network.get(params.requestId);
      if (entry) {
        entry.finished = true;
        entry.size = params.encodedDataLength;
      }
      break;
    }

    case "Network.loadingFailed": {
      const entry = state.network.get(params.requestId);
      if (entry) {
        entry.finished = true;
        entry.failed = true;
        entry.errorText = params.errorText;
      }
      break;
    }

    case "Page.frameNavigated":
      // Main-frame navigation: fresh page, fresh logs (DevTools default).
      if (params.frame && !params.frame.parentId) {
        state.console = [];
        state.network.clear();
        state.netOrder = [];
      }
      break;

    case "Page.screencastFrame": {
      debuggerSend(source.tabId, "Page.screencastFrameAck", { sessionId: params.sessionId }).catch(() => {});
      const gif = state.gif;
      if (gif && gif.recording) {
        gif.frames.push({ ts: (params.metadata && params.metadata.timestamp) || Date.now() / 1000, data: params.data });
        if (gif.frames.length > 240) {
          gif.frames.shift();
          gif.dropped = (gif.dropped || 0) + 1;
        }
      }
      break;
    }
  }
});

chrome.debugger.onDetach.addListener((source) => {
  if (source.tabId != null) attached.delete(source.tabId);
});

chrome.tabs.onRemoved.addListener((tabId) => attached.delete(tabId));

// ---- in-page helpers (serialized into the tab; no closures) ----------------

/** Builds the accessibility-tree node list and assigns persistent ref ids. */
function pageA11y(filter, rootRef, maxNodes) {
  const g = globalThis.__jarvisA11y || (globalThis.__jarvisA11y = { n: 0, byRef: new Map(), byEl: new WeakMap() });

  const interactiveRoles = new Set(["button", "link", "checkbox", "radio", "tab", "menuitem",
    "combobox", "option", "switch", "slider", "textbox", "searchbox", "menuitemcheckbox", "menuitemradio"]);
  const landmark = { NAV: "navigation", MAIN: "main", HEADER: "banner", FOOTER: "contentinfo",
    ASIDE: "complementary", FORM: "form", SECTION: "region", ARTICLE: "article", DIALOG: "dialog",
    UL: "list", OL: "list", LI: "listitem", TABLE: "table", TR: "row", TD: "cell", TH: "columnheader",
    IMG: "image", SELECT: "combobox", TEXTAREA: "textbox", BUTTON: "button", SUMMARY: "button", OPTION: "option" };
  const textish = new Set(["P", "BLOCKQUOTE", "PRE", "FIGCAPTION", "LABEL", "LEGEND", "CODE"]);

  function role(el) {
    const explicit = el.getAttribute("role");
    if (explicit) return explicit;
    const tag = el.tagName;
    if (tag === "A") return el.hasAttribute("href") ? "link" : "generic";
    if (tag === "INPUT") {
      const t = (el.type || "text").toLowerCase();
      if (t === "checkbox" || t === "radio") return t;
      // A file input reads as a button, as it does in the reference tree.
      if (t === "button" || t === "submit" || t === "reset" || t === "image" || t === "file") return "button";
      if (t === "range") return "slider";
      if (t === "search") return "searchbox";
      if (t === "hidden") return "";
      return "textbox";
    }
    if (/^H[1-6]$/.test(tag)) return "heading";
    return landmark[tag] || (textish.has(tag) ? "text" : "generic");
  }

  // Fields whose contents must never reach the model, matching the reference
  // extension's own set: password and hidden inputs, plus anything the page
  // labels as a credential, one-time code or card number.
  const SENSITIVE_AUTOCOMPLETE = ["current-password", "new-password", "one-time-code",
    "cc-number", "cc-csc", "cc-exp", "cc-exp-month", "cc-exp-year"];

  function sensitive(el) {
    const type = (el.getAttribute("type") || "").toLowerCase();
    if (type === "password" || type === "hidden") return true;
    const autocomplete = (el.getAttribute("autocomplete") || "").toLowerCase();
    return SENSITIVE_AUTOCOMPLETE.some(token => autocomplete.includes(token));
  }

  function name(el) {
    const aria = el.getAttribute("aria-label");
    if (aria) return aria;
    const labelledBy = el.getAttribute("aria-labelledby");
    if (labelledBy) {
      const parts = labelledBy.split(/\s+/)
        .map(id => { const t = document.getElementById(id); return t ? t.innerText.trim() : ""; })
        .filter(Boolean);
      if (parts.length) return parts.join(" ");
    }
    if (el.id) {
      const label = document.querySelector(`label[for="${CSS.escape(el.id)}"]`);
      if (label) return label.innerText.trim();
    }
    // A sensitive field still has to read as "filled" without saying what with.
    if (sensitive(el)) return el.value ? "[value redacted]" : "";
    return el.getAttribute("alt") || el.getAttribute("placeholder") || el.getAttribute("title") ||
      (el.closest("label") ? el.closest("label").innerText.trim().slice(0, 120) : "") ||
      ((el.tagName === "A" || el.tagName === "BUTTON" || /^H[1-6]$/.test(el.tagName) ||
        el.tagName === "SUMMARY" || el.tagName === "OPTION" || el.tagName === "LEGEND")
        ? el.innerText.trim().replace(/\s+/g, " ").slice(0, 120) : "");
  }

  function interactive(el, r) {
    if (el.disabled) return false;
    const tag = el.tagName;
    if (tag === "A" && el.hasAttribute("href")) return true;
    if (tag === "BUTTON" || tag === "SELECT" || tag === "TEXTAREA" || tag === "SUMMARY") return true;
    if (tag === "INPUT") return (el.type || "").toLowerCase() !== "hidden";
    if (el.isContentEditable) return true;
    if (el.hasAttribute("onclick")) return true;
    if (interactiveRoles.has(r)) return true;
    const tabIndex = el.getAttribute("tabindex");
    return tabIndex !== null && Number(tabIndex) >= 0;
  }

  function visible(el) {
    try { return el.checkVisibility({ visibilityProperty: true, opacityProperty: false }); }
    catch (e) {
      const rect = el.getBoundingClientRect();
      return rect.width > 0 && rect.height > 0;
    }
  }

  function refFor(el) {
    let ref = g.byEl.get(el);
    if (!ref) {
      ref = ++g.n;
      g.byEl.set(el, ref);
      g.byRef.set(ref, new WeakRef(el));
    }
    return ref;
  }

  let root = document.body;
  if (rootRef) {
    const held = g.byRef.get(rootRef);
    const el = held && held.deref();
    if (!el || !el.isConnected) throw new Error(`ref_${rootRef} is stale — call browser_read_page again.`);
    root = el;
  }
  if (!root) return { url: location.href, title: document.title, nodes: [], truncated: false };

  const nodes = [];
  let truncated = false;

  function walk(el, depth) {
    if (nodes.length >= maxNodes) { truncated = true; return; }
    const tag = el.tagName;
    if (tag === "SCRIPT" || tag === "STYLE" || tag === "NOSCRIPT" || tag === "TEMPLATE") return;
    // filter "all" is the reference's default and deliberately keeps elements
    // that are off-screen or hidden; anything narrower is what is on show now.
    if (filter !== "all" && (el.getAttribute("aria-hidden") === "true" || !visible(el))) return;

    const r = role(el);
    const isInteractive = interactive(el, r);
    const structural = r && r !== "generic" && r !== "text";
    const isText = r === "text" || r === "heading";
    let emitted = false;

    if (isInteractive || (filter === "all" && (structural || isText))) {
      const node = { role: isInteractive && r === "generic" ? "generic" : (r || "generic"), depth, tag: tag.toLowerCase() };
      const label = name(el);
      if (label) node.name = label;
      if (isText && !node.name) {
        const text = el.innerText.trim().replace(/\s+/g, " ").slice(0, 200);
        if (!text) return;
        node.name = text;
      }
      if (isInteractive) {
        node.ref = refFor(el);
        if (el.tagName === "INPUT" && ((el.type || "").toLowerCase() === "checkbox" || (el.type || "").toLowerCase() === "radio")) {
          node.checked = !!el.checked;
        } else if (el.tagName === "SELECT" || el.tagName === "TEXTAREA" ||
                   (el.tagName === "INPUT" && el.value)) {
          if (el.value) node.value = sensitive(el) ? "[value redacted]" : String(el.value).slice(0, 80);
        }
      }
      nodes.push(node);
      emitted = true;
      // Interactive leaves rarely have useful children (their text is the name).
      if (isInteractive && tag !== "FORM" && tag !== "TABLE") return;
      if (isText) return;
    }

    for (const child of el.children) {
      walk(child, depth + (emitted ? 1 : 0));
    }
  }

  walk(root, 0);
  return { url: location.href, title: document.title, nodes, truncated };
}

/**
 * Resolves a ref to its page-absolute viewport coordinate, scrolling it into
 * view. Inside an iframe the offsets of the owning frame elements are added
 * (possible for same-origin chains; a cross-origin boundary throws with
 * guidance to click by coordinate from a screenshot instead).
 */
function pageRefPoint(ref) {
  const g = globalThis.__jarvisA11y;
  const held = g && g.byRef.get(ref);
  const el = held && held.deref();
  if (!el || !el.isConnected) throw new Error(`ref_${ref} is stale — call browser_read_page again.`);
  el.scrollIntoView({ block: "center", inline: "nearest" });
  const rect = el.getBoundingClientRect();
  let x = rect.left + rect.width / 2;
  let y = rect.top + rect.height / 2;
  let w = window;
  while (w !== w.parent) {
    // frameElement is null across origins (and throws in some engines); either
    // way the offset is unknowable from here, and guessing would put the click
    // somewhere else on the page.
    let owner = null;
    try {
      owner = w.frameElement;
    } catch (e) {
      owner = null;
    }
    if (!owner) {
      throw new Error(
        "ref_" + ref + " sits in a cross-origin iframe, so its position on the page cannot be resolved — " +
        "take a browser_computer screenshot and click it by coordinate instead.");
    }
    owner.scrollIntoView({ block: "center", inline: "nearest" });
    const frameRect = owner.getBoundingClientRect();
    x += frameRect.left;
    y += frameRect.top;
    w = w.parent;
  }
  return { x: Math.round(x), y: Math.round(y) };
}

/** Marks the file input a ref points at so the DevTools protocol can find it. */
function pageMarkForUpload(ref, token) {
  const g = globalThis.__jarvisA11y;
  const held = g && g.byRef.get(ref);
  const el = held && held.deref();
  if (!el || !el.isConnected) throw new Error(`ref_${ref} is stale — call browser_read_page again.`);
  const input = el.tagName === "INPUT" && (el.type || "").toLowerCase() === "file"
    ? el
    : el.querySelector('input[type="file"]');
  if (!input) throw new Error(`ref_${ref} is not a file input (and contains none) — find the <input type=file> with browser_read_page or browser_find.`);
  input.setAttribute("data-jarvis-upload", token);
  return { marked: true };
}

function pageUnmarkUpload(token) {
  for (const el of document.querySelectorAll(`[data-jarvis-upload="${token}"]`)) {
    el.removeAttribute("data-jarvis-upload");
  }
  return true;
}

/** Drops a file onto whatever sits at (x, y), for drag-and-drop upload targets. */
function pageDropFile(x, y, name, mime, base64) {
  const bytes = Uint8Array.from(atob(base64), c => c.charCodeAt(0));
  const file = new File([bytes], name, { type: mime });
  const dt = new DataTransfer();
  dt.items.add(file);
  const el = document.elementFromPoint(x, y) || document.body;
  for (const type of ["dragenter", "dragover", "drop"]) {
    el.dispatchEvent(new DragEvent(type, { bubbles: true, cancelable: true, dataTransfer: dt, clientX: x, clientY: y }));
  }
  return { dropped: true, target: el.tagName.toLowerCase() };
}

/**
 * The on-page indicator, in the reference extension's four pieces: a glow border
 * and a phantom cursor while the agent is acting, a "Stop Jarvis" button that
 * reaches the app, and a quiet pill the rest of the time saying the tab group is
 * under control. State is `{mode: "active"|"idle"|"off", label, x, y}`.
 */
function pageIndicator(state) {
  const ACCENT = "204,120,92";
  const ids = { glow: "__jarvis_glow__", cursor: "__jarvis_cursor__", stop: "__jarvis_stop__", pill: "__jarvis_pill__" };
  const drop = (id) => { const el = document.getElementById(id); if (el) el.remove(); };
  const host = document.body || document.documentElement;
  if (!host) return false;

  if (!document.getElementById("__jarvis_indicator_style__")) {
    const style = document.createElement("style");
    style.id = "__jarvis_indicator_style__";
    style.textContent =
      `#${ids.glow}{position:fixed;inset:0;pointer-events:none;z-index:2147483646;opacity:0;` +
      `transition:opacity .3s ease-in-out}` +
      `#${ids.glow} > div{position:absolute;inset:0;box-shadow:` +
      `inset 0 0 15px rgba(${ACCENT},.7),inset 0 0 25px rgba(${ACCENT},.5),inset 0 0 35px rgba(${ACCENT},.2)}` +
      `#${ids.cursor}{position:fixed;top:0;left:0;pointer-events:none;z-index:2147483646;` +
      `transition:transform 180ms cubic-bezier(0.2,0,0,1);will-change:transform}` +
      `#${ids.stop},#${ids.pill}{position:fixed;bottom:16px;left:50%;z-index:2147483647;` +
      `font:600 14px/1 system-ui,-apple-system,"Segoe UI",sans-serif;color:#141413;background:#FAF9F5;` +
      `border:0.5px solid rgba(31,30,29,.4);border-radius:12px;padding:12px 16px;` +
      `display:inline-flex;align-items:center;gap:10px;white-space:nowrap;` +
      `box-shadow:0 40px 80px rgba(${ACCENT},.24),0 4px 14px rgba(${ACCENT},.24);` +
      `transition:transform .3s cubic-bezier(.4,0,.2,1),opacity .3s cubic-bezier(.4,0,.2,1)}` +
      `#${ids.stop}{cursor:pointer;transform:translate(-50%,100px);opacity:0}` +
      `#${ids.stop}.__on{transform:translate(-50%,0);opacity:1}` +
      `#${ids.stop}:hover{background:#F5F4F0}` +
      `#${ids.pill}{pointer-events:none;font-weight:500;transform:translate(-50%,0)}` +
      `@media (prefers-reduced-motion: reduce){` +
      `#${ids.glow},#${ids.cursor},#${ids.stop},#${ids.pill}{transition:none}}`;
    (document.head || host).appendChild(style);
  }

  if (state.mode === "off" || state.mode === "idle") {
    clearTimeout(globalThis.__jarvisIndicatorWatchdog);
  }

  if (state.mode === "off") {
    Object.values(ids).forEach(drop);
    return true;
  }

  if (state.mode === "idle") {
    drop(ids.glow); drop(ids.cursor); drop(ids.stop);
    let pill = document.getElementById(ids.pill);
    if (!pill) {
      pill = document.createElement("div");
      pill.id = ids.pill;
      pill.setAttribute("aria-hidden", "true");
      host.appendChild(pill);
    }
    pill.textContent = "Jarvis is active in this tab group";
    return true;
  }

  drop(ids.pill);

  let glow = document.getElementById(ids.glow);
  if (!glow) {
    glow = document.createElement("div");
    glow.id = ids.glow;
    glow.setAttribute("aria-hidden", "true");
    glow.appendChild(document.createElement("div"));
    host.appendChild(glow);
  }
  requestAnimationFrame(() => { glow.style.opacity = "1"; });

  let stop = document.getElementById(ids.stop);
  if (!stop) {
    stop = document.createElement("button");
    stop.id = ids.stop;
    stop.type = "button";
    stop.innerHTML =
      '<svg width="16" height="16" viewBox="0 0 256 256" fill="currentColor" aria-hidden="true">' +
      '<path d="M128,20A108,108,0,1,0,236,128,108.12,108.12,0,0,0,128,20Zm0,192a84,84,0,1,1,84-84A84.09,' +
      '84.09,0,0,1,128,212Zm40-112v56a12,12,0,0,1-12,12H100a12,12,0,0,1-12-12V100a12,12,0,0,1,12-12h56A12,' +
      '12,0,0,1,168,100Z"></path></svg><span>Stop Jarvis</span>';
    // isTrusted keeps a page's own synthetic click from stopping the agent.
    stop.addEventListener("click", (e) => {
      if (!e.isTrusted) return;
      stop.querySelector("span").textContent = "Stopping…";
      try { chrome.runtime.sendMessage({ type: "JARVIS_STOP" }); } catch (err) { /* port gone */ }
    });
    host.appendChild(stop);
    requestAnimationFrame(() => stop.classList.add("__on"));
  }

  // The page keeps its own watchdog: Chrome can stop the service worker between
  // commands, and a glow left burning would claim the agent is still working.
  clearTimeout(globalThis.__jarvisIndicatorWatchdog);
  globalThis.__jarvisIndicatorWatchdog = setTimeout(() => pageIndicator({ mode: "idle" }), 60000);

  if (typeof state.x === "number" && typeof state.y === "number") {
    let cursor = document.getElementById(ids.cursor);
    if (!cursor) {
      cursor = document.createElement("div");
      cursor.id = ids.cursor;
      cursor.setAttribute("aria-hidden", "true");
      const arrow = (stroke, fill, extra) =>
        `<svg width="20" height="26" viewBox="0 0 20 26" style="position:absolute;top:0;left:0;overflow:visible;${extra}">` +
        `<path d="M0 0 L0 18 L4.5 14 L7.5 21.5 L11 20 L8 13 L14 13 Z" stroke="${stroke}" stroke-width="3" ` +
        `stroke-linejoin="round" fill="${stroke}"></path>` +
        `<path d="M0 0 L0 18 L4.5 14 L7.5 21.5 L11 20 L8 13 L14 13 Z" fill="${fill}"></path></svg>`;
      cursor.innerHTML =
        arrow("white", "#111", "") +
        arrow(`rgb(${ACCENT})`, "#FAF9F5",
          `filter:drop-shadow(0 0 4px rgba(${ACCENT},.9)) drop-shadow(0 0 10px rgba(${ACCENT},.45));`);
      host.appendChild(cursor);
    }
    cursor.style.transform = `translate3d(${state.x}px, ${state.y}px, 0)`;
  }

  return true;
}

/** Sets a form element's value by ref (input/textarea/select/checkbox/contenteditable). */
function pageFormInput(ref, value) {
  const g = globalThis.__jarvisA11y;
  const held = g && g.byRef.get(ref);
  const el = held && held.deref();
  if (!el || !el.isConnected) throw new Error(`ref_${ref} is stale — call browser_read_page again.`);
  el.scrollIntoView({ block: "center", inline: "nearest" });
  el.focus();

  const fire = (type) => el.dispatchEvent(new Event(type, { bubbles: true }));

  if (el.tagName === "SELECT") {
    const wanted = String(value);
    const option = [...el.options].find(o => o.value === wanted) ||
                   [...el.options].find(o => o.text.trim() === wanted.trim());
    if (!option) throw new Error(`No option matches "${wanted}".`);
    el.value = option.value;
    fire("input"); fire("change");
    return { set: true, value: option.value };
  }

  const type = (el.type || "").toLowerCase();
  if (el.tagName === "INPUT" && type === "file") {
    // Assigning to a file input's value is a silent no-op by design.
    throw new Error("A file input cannot be typed into — upload with browser_file_upload instead.");
  }

  if (el.tagName === "INPUT" && (type === "checkbox" || type === "radio")) {
    const wanted = value === true || value === "true";
    if (el.checked !== wanted) el.click();
    return { set: true, checked: el.checked };
  }

  if (el.tagName === "INPUT" || el.tagName === "TEXTAREA") {
    const setter = Object.getOwnPropertyDescriptor(el.constructor.prototype, "value");
    if (setter && setter.set) setter.set.call(el, String(value)); else el.value = String(value);
    fire("input"); fire("change");
    return { set: true };
  }

  if (el.isContentEditable) {
    el.textContent = String(value);
    fire("input");
    return { set: true };
  }

  throw new Error("Element is not an input, textarea, select, checkbox or contenteditable.");
}

/** Visible text, article/main first, falling back to the whole body. */
function pageText(maxChars) {
  const pick = document.querySelector("article") || document.querySelector("main") || document.body;
  const text = pick ? pick.innerText : "";
  return {
    title: document.title,
    url: location.href,
    text: text.slice(0, maxChars),
    truncated: text.length > maxChars,
  };
}

async function runInPage(tabId, func, args, frameId) {
  const target = { tabId };
  if (frameId) target.frameIds = [frameId];
  const [result] = await chrome.scripting.executeScript({ target, func, args });
  if (result && result.error) throw new Error(result.error);
  return result ? result.result : null;
}

// The glow and the Stop button stay up while commands keep arriving; a lull
// drops the tab back to the quiet pill. The app also ends a session explicitly
// when its turn finishes, which is the path that survives a sleeping worker.
const QUIET_AFTER_MS = 4000;
const indicated = new Map();

/** Fire-and-forget agent-activity indicator on the tab's main frame. */
function showIndicator(tabId, point) {
  const state = { mode: "active" };
  if (point && typeof point.x === "number" && typeof point.y === "number") {
    state.x = point.x;
    state.y = point.y;
  }

  runInPage(tabId, pageIndicator, [state]).catch(() => {});
  clearTimeout(indicated.get(tabId));
  indicated.set(tabId, setTimeout(() => {
    indicated.delete(tabId);
    runInPage(tabId, pageIndicator, [{ mode: "idle" }]).catch(() => {});
  }, QUIET_AFTER_MS));
}

/** Takes every trace of the agent off the pages it was driving. */
async function clearIndicators(mode) {
  for (const timer of indicated.values()) clearTimeout(timer);
  indicated.clear();
  if (groupId === null) return;
  let tabs = [];
  try { tabs = await chrome.tabs.query({ groupId }); } catch (e) { return; }
  for (const tab of tabs) {
    runInPage(tab.id, pageIndicator, [{ mode }]).catch(() => {});
  }
}

// The page's own Stop button: the only thing it can do is ask the app to stop.
chrome.runtime.onMessage.addListener((message, sender) => {
  if (message && message.type === "JARVIS_STOP") {
    post({ event: "stop_requested", tabId: sender.tab ? sender.tab.id : null });
  }
});

/** Logs an input event into the tab's GIF recording, when one is running. */
function logGifEvent(tabId, action, point, label) {
  const state = attached.get(tabId);
  const gif = state && state.gif;
  if (gif && gif.recording) {
    gif.events.push({
      ts: Date.now() / 1000, action,
      x: point ? point.x : undefined, y: point ? point.y : undefined,
      label: label || undefined,
    });
  }
}

// ---- keyboard --------------------------------------------------------------

const KEYS = {
  enter: { code: "Enter", key: "Enter", vk: 13, text: "\r" },
  return: { code: "Enter", key: "Enter", vk: 13, text: "\r" },
  tab: { code: "Tab", key: "Tab", vk: 9 },
  escape: { code: "Escape", key: "Escape", vk: 27 },
  esc: { code: "Escape", key: "Escape", vk: 27 },
  backspace: { code: "Backspace", key: "Backspace", vk: 8 },
  delete: { code: "Delete", key: "Delete", vk: 46 },
  space: { code: "Space", key: " ", vk: 32, text: " " },
  up: { code: "ArrowUp", key: "ArrowUp", vk: 38 },
  down: { code: "ArrowDown", key: "ArrowDown", vk: 40 },
  left: { code: "ArrowLeft", key: "ArrowLeft", vk: 37 },
  right: { code: "ArrowRight", key: "ArrowRight", vk: 39 },
  arrowup: { code: "ArrowUp", key: "ArrowUp", vk: 38 },
  arrowdown: { code: "ArrowDown", key: "ArrowDown", vk: 40 },
  arrowleft: { code: "ArrowLeft", key: "ArrowLeft", vk: 37 },
  arrowright: { code: "ArrowRight", key: "ArrowRight", vk: 39 },
  home: { code: "Home", key: "Home", vk: 36 },
  end: { code: "End", key: "End", vk: 35 },
  pageup: { code: "PageUp", key: "PageUp", vk: 33 },
  pagedown: { code: "PageDown", key: "PageDown", vk: 34 },
  insert: { code: "Insert", key: "Insert", vk: 45 },
};
for (let i = 1; i <= 12; i++) KEYS[`f${i}`] = { code: `F${i}`, key: `F${i}`, vk: 111 + i };

const MODIFIER_BITS = { alt: 1, ctrl: 2, control: 2, meta: 4, cmd: 4, command: 4, shift: 8 };

function parseCombo(combo) {
  const parts = combo.split("+").map(p => p.trim()).filter(Boolean);
  if (!parts.length) throw new Error(`No key in "${combo}".`);
  const keyPart = parts[parts.length - 1];
  let modifiers = 0;
  for (const part of parts.slice(0, -1)) {
    const bit = MODIFIER_BITS[part.toLowerCase()];
    if (!bit) throw new Error(`Unknown modifier "${part}".`);
    modifiers |= bit;
  }

  const known = KEYS[keyPart.toLowerCase()];
  if (known) return { modifiers, ...known };
  if (keyPart.length === 1) {
    const ch = modifiers & 8 ? keyPart.toUpperCase() : keyPart;
    const upper = keyPart.toUpperCase();
    return {
      modifiers,
      code: /[a-z]/i.test(keyPart) ? `Key${upper}` : (/[0-9]/.test(keyPart) ? `Digit${keyPart}` : ""),
      key: ch,
      vk: upper.charCodeAt(0),
      // Shortcut chords (ctrl/alt/meta held) must not also insert the character.
      text: modifiers & ~8 ? undefined : ch,
    };
  }
  throw new Error(`Unknown key "${keyPart}".`);
}

async function pressKey(tabId, combo) {
  const k = parseCombo(combo);
  const base = {
    modifiers: k.modifiers,
    key: k.key,
    code: k.code,
    windowsVirtualKeyCode: k.vk,
    nativeVirtualKeyCode: k.vk,
  };
  await debuggerSend(tabId, "Input.dispatchKeyEvent",
    { type: k.text ? "keyDown" : "rawKeyDown", text: k.text, ...base });
  await debuggerSend(tabId, "Input.dispatchKeyEvent", { type: "keyUp", ...base });
}

// ---- mouse -----------------------------------------------------------------

async function mouseClick(tabId, x, y, button, clickCount, modifiers) {
  await debuggerSend(tabId, "Input.dispatchMouseEvent",
    { type: "mouseMoved", x, y, button: "none", modifiers });
  for (let i = 1; i <= clickCount; i++) {
    await debuggerSend(tabId, "Input.dispatchMouseEvent",
      { type: "mousePressed", x, y, button, clickCount: i, modifiers });
    await debuggerSend(tabId, "Input.dispatchMouseEvent",
      { type: "mouseReleased", x, y, button, clickCount: i, modifiers });
  }
}

async function resolvePoint(tabId, args) {
  if (args.ref) return await runInPage(tabId, pageRefPoint, [args.ref], args.frameId);
  if (typeof args.x === "number" && typeof args.y === "number") return { x: args.x, y: args.y };
  throw new Error("Pass coordinate [x, y] or a ref from browser_read_page.");
}

function comboModifiers(args) {
  let bits = 0;
  for (const part of String(args.modifiers || "").split("+")) {
    const bit = MODIFIER_BITS[part.trim().toLowerCase()];
    if (bit) bits |= bit;
  }
  return bits;
}

// ---- computer actions ------------------------------------------------------

async function screenshot(tabId, region, scale) {
  const metrics = await debuggerSend(tabId, "Page.getLayoutMetrics");
  const viewport = metrics.cssVisualViewport || metrics.visualViewport || {};
  const params = { format: "png" };
  if (region) {
    const [x, y, w, h] = region;
    const zoom = scale || Math.min(4, Math.max(1, Math.round((1200 / Math.max(1, w)) * 10) / 10));
    params.clip = { x, y, width: w, height: h, scale: zoom };
  }
  const shot = await debuggerSend(tabId, "Page.captureScreenshot", params);
  return {
    image: shot.data,
    width: Math.round(viewport.clientWidth || 0),
    height: Math.round(viewport.clientHeight || 0),
  };
}

async function computer(args) {
  const tab = await targetTab(args);
  await ensureAttached(tab.id);
  const action = args.action;
  const modifiers = comboModifiers(args);

  switch (action) {
    case "screenshot":
      return await screenshot(tab.id, null);

    case "zoom": {
      if (!Array.isArray(args.region) || args.region.length !== 4) {
        throw new Error("zoom needs region [x, y, width, height].");
      }
      return await screenshot(tab.id, args.region, args.scale);
    }

    case "left_click": case "right_click": case "double_click": case "triple_click": {
      const point = await resolvePoint(tab.id, args);
      const button = action === "right_click" ? "right" : "left";
      const count = action === "double_click" ? 2 : action === "triple_click" ? 3 : 1;
      showIndicator(tab.id, point);
      logGifEvent(tab.id, action, point);
      await mouseClick(tab.id, point.x, point.y, button, count, modifiers);
      return { clicked: true, x: point.x, y: point.y };
    }

    case "hover": {
      const point = await resolvePoint(tab.id, args);
      await debuggerSend(tab.id, "Input.dispatchMouseEvent",
        { type: "mouseMoved", x: point.x, y: point.y, button: "none", modifiers });
      return { hovered: true, x: point.x, y: point.y };
    }

    case "left_click_drag": {
      if (typeof args.startX !== "number" || typeof args.startY !== "number") {
        throw new Error("left_click_drag needs start_coordinate.");
      }
      const end = await resolvePoint(tab.id, args);
      showIndicator(tab.id, end);
      logGifEvent(tab.id, "left_click_drag", { x: args.startX, y: args.startY }, "drag");
      await debuggerSend(tab.id, "Input.dispatchMouseEvent",
        { type: "mousePressed", x: args.startX, y: args.startY, button: "left", clickCount: 1, modifiers });
      const steps = 8;
      for (let i = 1; i <= steps; i++) {
        await debuggerSend(tab.id, "Input.dispatchMouseEvent", {
          type: "mouseMoved",
          x: Math.round(args.startX + ((end.x - args.startX) * i) / steps),
          y: Math.round(args.startY + ((end.y - args.startY) * i) / steps),
          button: "left", modifiers,
        });
      }
      await debuggerSend(tab.id, "Input.dispatchMouseEvent",
        { type: "mouseReleased", x: end.x, y: end.y, button: "left", clickCount: 1, modifiers });
      return { dragged: true };
    }

    case "scroll": {
      const point = typeof args.x === "number" ? { x: args.x, y: args.y }
        : args.ref ? await resolvePoint(tab.id, args) : { x: 400, y: 300 };
      const amount = (args.scrollAmount || 3) * 120;
      const direction = args.scrollDirection || "down";
      const deltaX = direction === "left" ? -amount : direction === "right" ? amount : 0;
      const deltaY = direction === "up" ? -amount : direction === "down" ? amount : 0;
      await debuggerSend(tab.id, "Input.dispatchMouseEvent",
        { type: "mouseWheel", x: point.x, y: point.y, deltaX, deltaY, modifiers });
      return { scrolled: direction };
    }

    case "scroll_to": {
      if (!args.ref) throw new Error("scroll_to needs a ref.");
      const point = await runInPage(tab.id, pageRefPoint, [args.ref], args.frameId);
      return { scrolledTo: true, ...point };
    }

    case "type": {
      if (typeof args.text !== "string") throw new Error("type needs text.");
      showIndicator(tab.id, null);
      logGifEvent(tab.id, "type", null, "typing");
      await debuggerSend(tab.id, "Input.insertText", { text: args.text });
      return { typed: true };
    }

    case "key": {
      if (!args.text) throw new Error("key needs the key combo in text (e.g. \"ctrl+a\", \"Return\").");
      const repeat = Math.min(Math.max(args.repeat || 1, 1), 50);
      showIndicator(tab.id, null);
      logGifEvent(tab.id, "key", null, args.text);
      for (let i = 0; i < repeat; i++) await pressKey(tab.id, args.text);
      return { pressed: args.text, repeat };
    }

    case "wait": {
      const seconds = Math.min(Math.max(args.duration || 1, 0), 15);
      await new Promise(resolve => setTimeout(resolve, seconds * 1000));
      return { waited: seconds };
    }

    default:
      throw new Error(`Unknown computer action: ${action}`);
  }
}

// ---- viewport emulation ----------------------------------------------------

const MOBILE_UA = "Mozilla/5.0 (Linux; Android 12; Pixel 6) AppleWebKit/537.36 " +
  "(KHTML, like Gecko) Chrome/126.0.0.0 Mobile Safari/537.36";

async function resize(args) {
  const tab = await targetTab(args);
  await ensureAttached(tab.id);
  const applied = [];

  if (args.reset) {
    await debuggerSend(tab.id, "Emulation.clearDeviceMetricsOverride");
    await debuggerSend(tab.id, "Emulation.setTouchEmulationEnabled", { enabled: false });
    await debuggerSend(tab.id, "Emulation.setUserAgentOverride", { userAgent: "" });
    applied.push("viewport reset");
  } else if (args.width && args.height) {
    const mobile = !!args.mobile;
    await debuggerSend(tab.id, "Emulation.setDeviceMetricsOverride",
      { width: args.width, height: args.height, deviceScaleFactor: 0, mobile });
    await debuggerSend(tab.id, "Emulation.setTouchEmulationEnabled",
      { enabled: mobile, maxTouchPoints: mobile ? 5 : 1 });
    if (mobile) {
      await debuggerSend(tab.id, "Emulation.setUserAgentOverride", { userAgent: MOBILE_UA });
    }
    applied.push(`${args.width}x${args.height}${mobile ? " (mobile)" : ""}`);
  }

  if (args.colorScheme) {
    await debuggerSend(tab.id, "Emulation.setEmulatedMedia",
      { features: [{ name: "prefers-color-scheme", value: args.colorScheme }] });
    applied.push(`prefers-color-scheme: ${args.colorScheme}`);
  }

  return { applied };
}

// ---- command dispatch ------------------------------------------------------

async function handle(cmd, args) {
  switch (cmd) {
    case "ping":
      return { pong: true };

    // The app ends a session when its turn finishes: the glow and the Stop
    // button come down, and the pages go back to the quiet pill (or clean).
    case "session": {
      await clearIndicators(args.active === false ? "off" : "idle");
      return { session: args.active === false ? "ended" : "idle" };
    }

    case "tabs": {
      const tabs = await chrome.tabs.query({});
      return tabs.map(t => ({
        id: t.id, title: t.title, url: t.url, active: t.active,
        attached: attached.has(t.id),
        grouped: groupId !== null && t.groupId === groupId,
      }));
    }

    // The origin gate on the app side asks for this before the first action on a
    // tab, so a page change is noticed before anything is clicked on it.
    case "tab_origin": {
      const tab = args.tabId ? await chrome.tabs.get(args.tabId) : await targetTab(args);
      let origin = "";
      try {
        origin = new URL(tab.url || "").origin;
      } catch (e) {
        origin = tab.url || "";
      }
      return { tabId: tab.id, url: tab.url || "", origin, title: tab.title || "" };
    }

    case "navigate": {
      if (!args.url) throw new Error("url is required");
      if (args.url === "back" || args.url === "forward") {
        const tab = await targetTab(args);
        await (args.url === "back" ? chrome.tabs.goBack(tab.id) : chrome.tabs.goForward(tab.id));
        return { tabId: tab.id, went: args.url };
      }
      if (args.newTab || !args.tabId) {
        const tab = await chrome.tabs.create({ url: args.url, active: true });
        await ensureGroup(tab.id);
        await tryAttach(tab.id);
        return { tabId: tab.id, grouped: true };
      }
      const target = await targetTab(args);
      await tryAttach(target.id);
      await chrome.tabs.update(target.id, { url: args.url, active: true });
      return { tabId: target.id };
    }

    case "create_tab": {
      const tab = await chrome.tabs.create({ url: args.url || "about:blank", active: args.active !== false });
      await ensureGroup(tab.id);
      return { tabId: tab.id, grouped: true };
    }

    case "select_tab": {
      if (!args.tabId) throw new Error("tabId is required");
      const tab = await chrome.tabs.update(args.tabId, { active: true });
      await chrome.windows.update(tab.windowId, { focused: true });
      // Selecting is how a tab the user already had open joins the group.
      await ensureGroup(args.tabId);
      return { selected: args.tabId, grouped: true };
    }

    case "close_tab": {
      if (!args.tabId) throw new Error("tabId is required");
      await chrome.tabs.remove(args.tabId);
      return { closed: args.tabId };
    }

    case "read_page": {
      const tab = await targetTab(args);
      return await runInPage(tab.id, pageText, [args.maxChars || 60000]);
    }

    case "a11y": {
      const tab = await targetTab(args);
      const a11yArgs = [args.filter === "all" ? "all" : "interactive", args.rootRef || null, args.maxNodes || 1500];
      if (args.rootRef) {
        // A subtree focus targets the one frame its ref lives in.
        const result = await runInPage(tab.id, pageA11y, a11yArgs, args.frameId);
        return { frames: [{ frameId: args.frameId || 0, ...result }] };
      }

      const results = await chrome.scripting.executeScript({
        target: { tabId: tab.id, allFrames: true },
        func: pageA11y,
        args: a11yArgs,
      });
      const frames = results
        .filter(r => r && r.result && r.result.nodes && r.result.nodes.length)
        .sort((a, b) => a.frameId - b.frameId)
        .map(r => ({ frameId: r.frameId, ...r.result }));
      if (!frames.length && results[0] && results[0].result) {
        frames.push({ frameId: results[0].frameId, ...results[0].result });
      }
      return { frames };
    }

    case "form_input": {
      if (!args.ref) throw new Error("ref is required");
      const tab = await targetTab(args);
      showIndicator(tab.id, null);
      return await runInPage(tab.id, pageFormInput, [args.ref, args.value], args.frameId);
    }

    case "file_upload": {
      if (!args.ref) throw new Error("ref is required");
      if (!Array.isArray(args.paths) || !args.paths.length) throw new Error("paths is required");
      const tab = await targetTab(args);
      await ensureAttached(tab.id);
      const token = "jf" + Date.now().toString(36) + Math.floor(Math.random() * 1e6).toString(36);
      await runInPage(tab.id, pageMarkForUpload, [args.ref, token], args.frameId);
      try {
        showIndicator(tab.id, null);
        await debuggerSend(tab.id, "DOM.getDocument", { depth: 0 });
        const search = await debuggerSend(tab.id, "DOM.performSearch",
          { query: `[data-jarvis-upload="${token}"]` });
        try {
          if (!search.resultCount) {
            throw new Error("The file input could not be reached over the DevTools protocol " +
              "(inputs inside cross-origin iframes cannot).");
          }
          const found = await debuggerSend(tab.id, "DOM.getSearchResults",
            { searchId: search.searchId, fromIndex: 0, toIndex: 1 });
          await debuggerSend(tab.id, "DOM.setFileInputFiles",
            { files: args.paths, nodeId: found.nodeIds[0] });
        } finally {
          debuggerSend(tab.id, "DOM.discardSearchResults", { searchId: search.searchId }).catch(() => {});
        }
        return { uploaded: args.paths.length };
      } finally {
        runInPage(tab.id, pageUnmarkUpload, [token], args.frameId).catch(() => {});
      }
    }

    case "drop_image": {
      if (typeof args.x !== "number" || typeof args.y !== "number") throw new Error("x and y are required");
      const tab = await targetTab(args);
      showIndicator(tab.id, null);
      return await runInPage(tab.id, pageDropFile,
        [args.x, args.y, args.name || "image.png", args.mime || "image/png", args.data]);
    }

    case "gif": {
      const tab = await targetTab(args);
      const state = await ensureAttached(tab.id);
      switch (args.op) {
        case "start": {
          state.gif = { recording: true, frames: [], events: [], dropped: 0 };
          await debuggerSend(tab.id, "Page.startScreencast",
            { format: "jpeg", quality: 70, maxWidth: 800, maxHeight: 800, everyNthFrame: 2 });
          return { recording: true };
        }
        case "stop": {
          await debuggerSend(tab.id, "Page.stopScreencast");
          if (state.gif) state.gif.recording = false;
          return { frames: state.gif ? state.gif.frames.length : 0 };
        }
        case "fetch": {
          if (!state.gif || !state.gif.frames.length) {
            throw new Error("No recorded frames for this tab — start_recording first, then act on the page.");
          }
          if (state.gif.recording) {
            await debuggerSend(tab.id, "Page.stopScreencast");
            state.gif.recording = false;
          }
          return { frames: state.gif.frames, events: state.gif.events, dropped: state.gif.dropped };
        }
        case "clear": {
          if (state.gif && state.gif.recording) {
            await debuggerSend(tab.id, "Page.stopScreencast").catch(() => {});
          }
          delete state.gif;
          return { cleared: true };
        }
        default:
          throw new Error(`Unknown gif op: ${args.op}`);
      }
    }

    case "js_exec": {
      if (typeof args.code !== "string") throw new Error("code is required");
      const tab = await targetTab(args);
      await ensureAttached(tab.id);
      const result = await debuggerSend(tab.id, "Runtime.evaluate", {
        expression: args.code,
        replMode: true,
        awaitPromise: true,
        returnByValue: true,
        timeout: 10000,
      });
      if (result.exceptionDetails) {
        const d = result.exceptionDetails;
        const desc = (d.exception && (d.exception.description || d.exception.value)) || d.text;
        throw new Error(String(desc));
      }
      const value = result.result || {};
      return {
        value: "value" in value ? value.value : (value.unserializableValue || value.description || null),
        type: value.type,
      };
    }

    case "console_read": {
      const tab = await targetTab(args);
      const state = attached.get(tab.id);
      if (!state) {
        await ensureAttached(tab.id);
        return { entries: [], justAttached: true };
      }
      const entries = state.console.slice();
      // read_console_messages{clear:true}: hand back what is buffered and empty
      // it, so a later read does not repeat what was already reported.
      if (args.clear === true) state.console.length = 0;
      return { entries };
    }

    case "network_read": {
      const tab = await targetTab(args);
      const state = attached.get(tab.id);
      if (!state) {
        await ensureAttached(tab.id);
        return { requests: [], justAttached: true };
      }
      if (args.requestId) {
        const entry = state.network.get(args.requestId);
        if (!entry) throw new Error(`No request ${args.requestId} in the buffer.`);
        const body = await debuggerSend(tab.id, "Network.getResponseBody", { requestId: args.requestId });
        return { request: entry, body: body.body, base64Encoded: body.base64Encoded };
      }
      const requests = state.netOrder.map(id => state.network.get(id)).filter(Boolean);
      if (args.clear === true) {
        state.network.clear();
        state.netOrder.length = 0;
      }
      return { requests };
    }

    case "resize":
      return await resize(args);

    case "computer":
      return await computer(args);

    case "click": {
      if (!args.selector) throw new Error("selector is required");
      const tab = await targetTab(args);
      showIndicator(tab.id, null);
      const [result] = await chrome.scripting.executeScript({
        target: { tabId: tab.id },
        args: [args.selector],
        func: (selector) => {
          const el = document.querySelector(selector);
          if (!el) return { clicked: false, error: "No element matches " + selector };
          el.scrollIntoView({ block: "center" });
          el.click();
          return { clicked: true };
        },
      });
      return result.result;
    }

    case "type": {
      if (!args.selector) throw new Error("selector is required");
      const tab = await targetTab(args);
      showIndicator(tab.id, null);
      const [result] = await chrome.scripting.executeScript({
        target: { tabId: tab.id },
        args: [args.selector, args.text || "", !!args.submit],
        func: (selector, text, submit) => {
          const el = document.querySelector(selector);
          if (!el) return { typed: false, error: "No element matches " + selector };
          el.focus();
          if ("value" in el) {
            const setter = Object.getOwnPropertyDescriptor(el.constructor.prototype, "value");
            if (setter && setter.set) setter.set.call(el, text); else el.value = text;
            el.dispatchEvent(new Event("input", { bubbles: true }));
            el.dispatchEvent(new Event("change", { bubbles: true }));
          } else if (el.isContentEditable) {
            el.textContent = text;
            el.dispatchEvent(new Event("input", { bubbles: true }));
          } else {
            return { typed: false, error: "Element is not an input" };
          }
          if (submit && el.form) el.form.submit();
          return { typed: true };
        },
      });
      return result.result;
    }

    default:
      throw new Error("Unknown command: " + cmd);
  }
}

connect();
