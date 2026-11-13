'use strict';
const status = document.getElementById('status');
const browserStatus = document.getElementById('browser');
const sessionSelect = document.getElementById('session');
let busy = false;
async function call(action, extra = {}) {
  const reply = await chrome.runtime.sendMessage({ type: 'JARVIS_IMAGEGEN_CONFIG', action, ...extra });
  if (!reply || reply.error) throw new Error(reply?.error || 'The extension did not respond. Reload Jarvis Agent Browser.');
  return reply;
}
async function run(action, extra) {
  const buttons = [...document.querySelectorAll('button')];
  busy = true;
  buttons.forEach(b => b.disabled = true);
  try { const reply = await call(action, extra); status.textContent = reply.message || 'Selection saved. Refresh Jarvis Agent ImageGen settings.'; }
  catch (error) { status.textContent = error.message; }
  finally { busy = false; buttons.forEach(b => b.disabled = false); await refreshState().catch(() => {}); }
}
document.getElementById('bind').addEventListener('click', () => run('bind'));
document.getElementById('disconnect').addEventListener('click', () => run('unbind'));
document.getElementById('adopt').addEventListener('click', () => run('adopt', {
  sessionId: document.getElementById('session').value,
  confirmImageOnly: document.getElementById('confirmed').checked
}));
async function refreshState() {
  const state = await call('state');
  browserStatus.textContent = `${state.browser} · ${state.connected ? 'Agent connected' : 'Agent disconnected'}\n${state.instanceId}`;
  const selected = sessionSelect.value;
  sessionSelect.replaceChildren();
  for (const session of state.sessions) {
    const option = document.createElement('option'); option.value = session.id;
    option.textContent = `${session.id}${session.imageTab ? ' · image tab assigned' : ''}`; sessionSelect.append(option);
  }
  if (selected && state.sessions.some(session => session.id === selected)) sessionSelect.value = selected;
  if (!state.sessions.length && !status.textContent) {
    status.textContent = 'Call image_gen__get_state in your Jarvis session to make it available for optional tab assignment.';
  }
  if (!busy) document.getElementById('adopt').disabled = !state.sessions.length;
}
refreshState().catch(error => { browserStatus.textContent = 'Jarvis Agent unavailable'; status.textContent = error.message; });
// A native reconnect can complete while the popup remains open. Never leave stale status on screen.
setInterval(() => { refreshState().catch(() => { browserStatus.textContent = 'Jarvis Agent unavailable'; }); }, 2000);
