'use strict';
const status = document.getElementById('status');
async function call(action, extra = {}) {
  const reply = await chrome.runtime.sendMessage({ type: 'JARVIS_IMAGEGEN_CONFIG', action, ...extra });
  if (!reply || reply.error) throw new Error(reply?.error || 'The extension did not respond. Reload Jarvis Agent Browser.');
  return reply;
}
async function run(action, extra) {
  const buttons = [...document.querySelectorAll('button')];
  buttons.forEach(b => b.disabled = true);
  try { const reply = await call(action, extra); status.textContent = reply.message || 'Selection saved. Refresh Jarvis Agent ImageGen settings.'; }
  catch (error) { status.textContent = error.message; }
  finally { buttons.forEach(b => b.disabled = false); }
}
document.getElementById('bind').addEventListener('click', () => run('bind'));
document.getElementById('disconnect').addEventListener('click', () => run('unbind'));
document.getElementById('adopt').addEventListener('click', () => run('adopt', {
  sessionId: document.getElementById('session').value,
  confirmImageOnly: document.getElementById('confirmed').checked
}));
call('state').then(state => {
  document.getElementById('browser').textContent = `${state.browser} · ${state.connected ? 'Agent connected' : 'Agent disconnected'}\n${state.instanceId}`;
  const select = document.getElementById('session');
  for (const session of state.sessions) {
    const option = document.createElement('option'); option.value = session.id;
    option.textContent = `${session.id}${session.imageTab ? ' · image tab assigned' : ''}`; select.append(option);
  }
  if (!state.sessions.length) {
    status.textContent = 'Call image_gen__get_state in your Jarvis session to make it available for optional tab assignment.';
    document.getElementById('adopt').disabled = true;
  }
}).catch(error => { status.textContent = error.message; });
