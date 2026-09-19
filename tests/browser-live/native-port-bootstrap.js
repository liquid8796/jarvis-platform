// Test-only transport adapter. All browser actions remain in unchanged production
// background.js and use real chrome.* APIs. This deliberately does not exercise
// OS native-messaging registration or the native-host stdio executable.
const relay = __JARVIS_TEST_RELAY__;
// Diagnostics report operation names/timings only; implementations still delegate
// to the original real extension APIs and never substitute return values.
const trace = event => fetch(relay + '/trace', {method:'POST',body:JSON.stringify(event)}).catch(()=>{});
for (const [api,method] of [['tabs','create'],['tabs','update'],['tabs','remove'],['tabs','group'],['tabGroups','update'],['scripting','executeScript'],['debugger','attach'],['debugger','sendCommand']]) {
  const original = chrome[api][method].bind(chrome[api]);
  chrome[api][method] = async (...args) => {
    const id=crypto.randomUUID(),detail=api==='debugger'?args[1]:api==='scripting'?args[0]?.func?.name:undefined;
    trace({id,api,method,detail,phase:'start',at:Date.now()});
    try{const result=await original(...args);trace({id,api,method,detail,phase:'done',at:Date.now()});return result;}
    catch(error){trace({id,api,method,detail,phase:'error',error:String(error),at:Date.now()});throw error;}
  };
}
chrome.runtime.connectNative = function () {
  const listeners = [], disconnected = [];
  let closed = false;
  const port = {
    onMessage: { addListener: fn => listeners.push(fn) },
    onDisconnect: { addListener: fn => disconnected.push(fn) },
    postMessage(message) {
      fetch(relay + '/post', { method:'POST', body:JSON.stringify(message) }).catch(close);
    },
    disconnect: close
  };
  function close() { if (!closed) { closed=true; for (const fn of disconnected) fn(); } }
  (async () => {
    try {
      while (!closed) {
        const response = await fetch(relay + '/next');
        if (!response.ok) throw Error('Test relay stopped');
        const message = await response.json();
        if (message !== null) for (const fn of listeners) fn(message, port);
      }
    } catch { close(); }
  })();
  return port;
};
importScripts('background.js');
