// Session-owned ChatGPT Web jobs in the user's existing browser. No browser launch or credential export.
// Native commands never accept a URL to download, an arbitrary tab to adopt, or account selection.
globalThis.JarvisImageGen = (() => {
  const JOBS = 'jarvis.imagegen.jobs.v1', SESSIONS = 'jarvis.imagegen.sessions.v1';
  const jobs = new Map(), scopes = new Map();
  let writeTail = Promise.resolve(), pendingSave = null;
  const loaded = (async () => {
    const stored = (await chrome.storage.local.get(JOBS))[JOBS] || [];
    const sessions = (await chrome.storage.session.get(SESSIONS))[SESSIONS] || [];
    if (!Array.isArray(stored) || stored.length > 1000 || !Array.isArray(sessions) || sessions.length > 512) throw Error('IMAGEGEN_STORAGE_INVALID');
    for (const job of stored) {
      if (!/^ig_[a-f0-9]{32}$/.test(job.jobId) || !SESSION_ID.test(job.sessionId)) throw Error('IMAGEGEN_STORAGE_INVALID');
      jobs.set(job.jobId, job);
    }
    for (const scope of sessions) if (SESSION_ID.test(scope.sessionId)) scopes.set(scope.sessionId, scope);
  })();
  function persist() {
    writeTail = writeTail.catch(() => {}).then(async () => {
      await chrome.storage.local.set({ [JOBS]: [...jobs.values()] });
      await chrome.storage.session.set({ [SESSIONS]: [...scopes.values()] });
    });
    return writeTail;
  }
  const error = (errorCode, message) => ({ errorCode, message });
  function publicState(job, extra = {}) {
    return { jobId: job.jobId, tabId: job.tabId, conversationId: job.conversationId || null,
      submissionAttempted: !!job.sendIntent, ...extra };
  }
  function getJob(jobId, session) {
    if (!/^ig_[a-f0-9]{32}$/.test(jobId || '')) throw Error('IMAGEGEN_JOB_INVALID');
    const job = jobs.get(jobId);
    if (!job || job.sessionId !== session.id) throw Error('IMAGEGEN_JOB_NOT_OWNED');
    return job;
  }
  async function page(job, session, action, data = {}) {
    const tab = await targetTab({ tabId: job.tabId }, session);
    if (new URL(tab.url).origin !== 'https://chatgpt.com') return error('ORIGIN_CHANGED', 'The selected ImageGen tab changed origin.');
    const actualId = (new URL(tab.url).pathname.match(/\/c\/([a-zA-Z0-9-]+)/) || [])[1] || null;
    if (job.conversationId && actualId !== job.conversationId) return error('CONVERSATION_CHANGED', 'The selected ImageGen conversation changed.');
    showIndicator(tab.id, null);
    return await runInPage(tab.id, jarvisImageGenPage, [action, data]) || error('PAGE_UNSUPPORTED', 'ChatGPT returned no verifiable page state.');
  }
  async function account(job, session) {
    const state = await page(job, session, 'account');
    if (state.errorCode) return state;
    if (!/^[a-f0-9]{64}$/.test(state.accountHash || '')) return error('ACCOUNT_UNVERIFIED', 'ChatGPT account identity could not be verified.');
    const scope = scopes.get(session.id);
    if ((job.accountHash && job.accountHash !== state.accountHash) || (scope?.accountHash && scope.accountHash !== state.accountHash))
      return error('ACCOUNT_CHANGED', 'The ChatGPT account changed. Restore the original account or explicitly assign a new image session.');
    job.accountHash = state.accountHash;
    if (scope) scope.accountHash = state.accountHash;
    return null;
  }
  function allowedAsset(url) {
    try {
      const u = new URL(url);
      if (u.protocol === 'blob:') return url.startsWith('blob:https://chatgpt.com/');
      return u.protocol === 'https:' && !u.username && !u.password &&
        (u.hostname === 'chatgpt.com' && u.pathname.startsWith('/backend-api/') || u.hostname.endsWith('.oaiusercontent.com'));
    } catch { return false; }
  }
  function sameConversation(url, job) {
    try { const u = new URL(url); return u.origin === 'https://chatgpt.com' && u.pathname.endsWith('/c/' + job.conversationId); }
    catch { return false; }
  }
  function isCandidate(item, active) {
    return allowedAsset(item.url) && sameConversation(item.referrer, active.job) &&
      (!item.mime || item.mime.startsWith('image/')) && Date.parse(item.startTime) >= active.started - 1000;
  }
  // The Save-button route is serialized across this extension. Ambiguous download attribution fails closed.
  chrome.downloads.onCreated.addListener(item => {
    if (pendingSave && isCandidate(item, pendingSave)) pendingSave.items.set(item.id, item);
  });
  chrome.downloads.onDeterminingFilename.addListener((item, suggest) => {
    if (pendingSave && isCandidate(item, pendingSave)) {
      pendingSave.items.set(item.id, item);
      pendingSave.named.add(item.id);
      suggest({ filename: pendingSave.filename, conflictAction: 'uniquify' });
    } else suggest();
  });
  async function saveControl(job, session, index) {
    const data = { marker: job.marker, userIds: job.userIds, userMessageId: job.userMessageId, index, key: job.keys[index] };
    const plan = await page(job, session, 'download_plan', data);
    if (plan.errorCode) return plan;
    const filename = `JarvisImageGen/${job.jobId}/result-${index}.bin`;
    if (plan.href) {
      if (!allowedAsset(plan.href) || plan.href.startsWith('blob:')) return error('RESULT_UNVERIFIED', 'The image Save link is not an allowed image download.');
      const downloadId = await chrome.downloads.download({ url: plan.href, filename, conflictAction: 'uniquify', saveAs: false });
      return { downloadId };
    }
    if (pendingSave) return error('DOWNLOAD_BUSY', 'Another image save is currently being attributed. Read this job again later.');
    const active = { job, filename, started: Date.now(), items: new Map(), named: new Set() };
    pendingSave = active;
    try {
      const click = await page(job, session, 'click_save', data);
      if (click.errorCode) return click;
      let first = 0;
      for (let i = 0; i < 30; i++) {
        await new Promise(resolve => setTimeout(resolve, 150));
        if (active.items.size > 1) return error('DOWNLOAD_AMBIGUOUS', 'More than one download followed Save. No file was accepted.');
        if (active.items.size === 1) {
          first ||= Date.now();
          const id = [...active.items.keys()][0];
          if (Date.now() - first >= 600 && active.named.has(id)) return { downloadId: id };
        }
      }
      return error('DOWNLOAD_UNVERIFIED', 'The Save action did not produce an attributable image download. No screenshot or thumbnail was substituted.');
    } finally { pendingSave = null; }
  }
  async function handle(cmd, args, session) {
    const result = await handleCore(cmd, args, session);
    const job = jobs.get(args.jobId);
    if (result?.errorCode && job?.sessionId === session.id) {
      job.lastErrorCode = result.errorCode;
      job.failed = !job.sendIntent || ['GENERATION_REJECTED', 'QUOTA_EXCEEDED'].includes(result.errorCode);
      await persist();
    }
    return result;
  }
  async function handleCore(cmd, args, session) {
    await loaded;
    if (cmd === 'imagegen_state') {
      const scope = scopes.get(session.id);
      if (!scope) return { status: 'no_image_tab', ready: false };
      try {
        const tab = await targetTab({ tabId: scope.tabId }, session);
        return { status: new URL(tab.url).origin === 'https://chatgpt.com' ? 'tab_bound' : 'tab_changed', tabId: tab.id,
          conversationId: scope.conversationId || null, adopted: !!scope.adopted };
      } catch { return { status: 'tab_lost', ready: false }; }
    }
    if (cmd === 'imagegen_prepare') {
      if (!/^ig_[a-f0-9]{32}$/.test(args.jobId || '')) throw Error('IMAGEGEN_JOB_INVALID');
      if (jobs.has(args.jobId)) return publicState(getJob(args.jobId, session));
      if (jobs.size >= 1000) return error('BROWSER_CAPACITY', 'Browser ImageGen history reached its bounded capacity. Review retained job metadata.');
      let scope = scopes.get(session.id);
      if (scope) {
        try { await targetTab({ tabId: scope.tabId }, session); }
        catch { return error('TAB_LOST', 'The bound ImageGen tab was closed or moved. Explicitly assign a new image-only tab in the extension.'); }
      } else {
        // A tab in an existing normal window; never chrome.windows.create or a browser executable/profile.
        const window = await chrome.windows.getLastFocused({ windowTypes: ['normal'] });
        if (!window?.id) return error('BROWSER_NOT_CONNECTED', 'Open a normal window in your selected browser profile.');
        const tab = await chrome.tabs.create({ url: 'https://chatgpt.com/', windowId: window.id, active: false });
        await ensureGroup(tab.id, session);
        scope = { sessionId: session.id, tabId: tab.id, adopted: false, conversationId: null };
        scopes.set(session.id, scope);
      }
      const job = { jobId: args.jobId, sessionId: session.id, tabId: scope.tabId, conversationId: scope.conversationId,
        names: [], sendIntent: false, submitted: false, downloads: [], keys: [], stable: 0, createdAt: Date.now() };
      jobs.set(job.jobId, job); await persist(); return publicState(job);
    }
    const job = getJob(args.jobId, session);
    if (job.cancelled && !['imagegen_cancel', 'imagegen_poll', 'imagegen_download', 'imagegen_download_status'].includes(cmd))
      return error('CANCELLED', 'This browser image job was cancelled before submission.');
    if (cmd === 'imagegen_inspect') {
      const tab = await targetTab({ tabId: job.tabId }, session);
      if (tab.status === 'loading' || !tab.url || tab.url === 'about:blank') return publicState(job, { ready: false });
      const loading = await page(job, session, 'inspect');
      if (loading.errorCode) return loading;
      if (!loading.ready) return publicState(job, { ready: false });
    }
    if (cmd !== 'imagegen_cancel') {
      const changed = await account(job, session); if (changed) return changed;
    }
    if (cmd === 'imagegen_inspect') {
      const state = await page(job, session, 'inspect');
      if (state.ready) {
        job.userIds = state.userIds; job.conversationId = state.conversationId;
        const scope = scopes.get(session.id); scope.conversationId = state.conversationId;
        if (state.conversationId && [...scopes.values()].some(s => s.sessionId !== session.id && s.conversationId === state.conversationId))
          return error('CONVERSATION_OWNED', 'This ChatGPT conversation belongs to another ImageGen session.');
        await persist();
      }
      return publicState(job, state.errorCode ? state : { ready: !!state.ready });
    }
    if (cmd === 'imagegen_upload') {
      if (job.sendIntent) return error('ALREADY_SUBMITTED', 'This image request has already attempted submission.');
      if (!Array.isArray(args.paths) || args.paths.length < 1 || args.paths.length > 5 || args.paths.some(p => typeof p !== 'string'))
        return error('UPLOAD_INCOMPLETE', 'Supply all local reference images together.');
      if (job.uploadIntent) return error('UPLOAD_INCOMPLETE', 'Upload was already attempted. It will not be repeated automatically.');
      const marked = await page(job, session, 'mark_upload', { jobId: job.jobId, count: args.paths.length });
      if (marked.errorCode) return marked;
      job.names = args.paths.map(p => p.split(/[\\/]/).at(-1)); job.uploadIntent = true; await persist();
      await ensureAttached(job.tabId);
      const doc = await debuggerSend(job.tabId, 'DOM.getDocument', { depth: 0 });
      const found = await debuggerSend(job.tabId, 'DOM.querySelector', { nodeId: doc.root.nodeId, selector: `input[data-jarvis-imagegen-input="${job.jobId}"]` });
      if (!found.nodeId) return error('UPLOAD_INCOMPLETE', 'The verified file input disappeared.');
      await debuggerSend(job.tabId, 'DOM.setFileInputFiles', { nodeId: found.nodeId, files: args.paths });
      return publicState(job, { uploaded: job.names.length });
    }
    if (cmd === 'imagegen_upload_status') {
      const state = await page(job, session, 'upload_status', { names: job.names });
      return publicState(job, state);
    }
    if (cmd === 'imagegen_submit') {
      if (job.sendIntent) return publicState(job, { submitted: !!job.submitted, reconciliationRequired: true });
      if (typeof args.prompt !== 'string' || !args.prompt.trim() || args.prompt.length > 16000 || !Array.isArray(job.userIds))
        return error('PROMPT_INVALID', 'The image request is not ready for submission.');
      const ready = await page(job, session, 'focus_composer', { names: job.names });
      if (ready.errorCode) return ready;
      job.marker = `[jarvis-imagegen:${job.jobId}]`;
      // Correlation is visible, not a hidden instruction or a command for the model to execute.
      job.text = args.prompt + '\n\nRequest tracking reference (do not draw this text): ' + job.marker;
      job.sendIntent = true; await persist();
      await ensureAttached(job.tabId);
      await debuggerSend(job.tabId, 'Input.insertText', { text: job.text });
      if (job.cancelled) return error('CANCELLED', 'Image submission was stopped locally.');
      const result = await page(job, session, 'submit', { text: job.text, names: job.names, userIds: job.userIds });
      if (result.errorCode) return result;
      job.submitted = true; await persist(); return publicState(job, { submitted: true });
    }
    if (cmd === 'imagegen_poll') {
      if (!job.sendIntent || !job.marker) return error('SUBMISSION_UNVERIFIED', 'No verifiable prompt submission exists for this browser job.');
      const state = await page(job, session, 'poll', { marker: job.marker, userIds: job.userIds, userMessageId: job.userMessageId });
      if (state.errorCode) return state;
      if (state.conversationId) {
        job.conversationId = state.conversationId;
        const scope = scopes.get(session.id); if (scope) scope.conversationId = state.conversationId;
      }
      if (state.userMessageId) job.userMessageId = state.userMessageId;
      if (state.ready) {
        const same = JSON.stringify(job.keys) === JSON.stringify(state.keys);
        job.stable = same ? job.stable + 1 : 1; job.keys = state.keys;
      } else job.stable = 0;
      await persist();
      return publicState(job, { ready: job.stable >= 3, count: job.keys.length });
    }
    if (cmd === 'imagegen_download') {
      if (job.stable < 3 || !job.keys.length || !job.conversationId) return error('RESULT_UNVERIFIED', 'The generated image is not ready to save.');
      // Do one Save per call so all native messages remain below the bridge deadline.
      if (job.downloads.length < job.keys.length) {
        const result = await saveControl(job, session, job.downloads.length);
        if (result.errorCode) return result;
        job.downloads.push(result.downloadId); await persist();
      }
      return publicState(job, { started: job.downloads.length, count: job.keys.length });
    }
    if (cmd === 'imagegen_download_status') {
      if (job.downloads.length < job.keys.length) {
        const result = await handle('imagegen_download', { jobId: job.jobId }, session);
        if (result.errorCode) return result;
      }
      const output = [];
      for (const downloadId of job.downloads) {
        const [item] = await chrome.downloads.search({ id: downloadId });
        if (!item || item.state === 'interrupted') return error('DOWNLOAD_FAILED', 'An owned image download did not finish.');
        if (Math.max(item.totalBytes || 0, item.bytesReceived || 0, item.fileSize || 0) > 24 * 1024 * 1024) {
          if (item.state !== 'complete') await chrome.downloads.cancel(downloadId);
          return error('DOWNLOAD_TOO_LARGE', 'The owned image download exceeds the 24 MiB result limit.');
        }
        if (item.state !== 'complete') return publicState(job, { ready: false });
        const segments = item.filename.split(/[\\/]/);
        if (segments.at(-3) !== 'JarvisImageGen' || segments.at(-2) !== job.jobId || !segments.at(-1).startsWith('result-') ||
          !item.mime?.startsWith('image/') || !['safe', 'accepted', 'deepScannedSafe', 'allowlistedByPolicy'].includes(item.danger))
          return error('DOWNLOAD_UNVERIFIED', 'The browser download is not a verified image in this job destination.');
        output.push({ downloadId, filename: item.filename, fileSize: item.fileSize, mimeType: item.mime, state: item.state });
      }
      job.completed = output.length === job.keys.length && output.length > 0;
      await persist();
      return publicState(job, { ready: job.completed, downloads: output });
    }
    if (cmd === 'imagegen_cancel') {
      job.cancelled = true; await persist();
      for (const id of job.downloads) await chrome.downloads.cancel(id).catch(() => {});
      try { return await page(job, session, 'cancel', { marker: job.marker }); }
      catch { return { stopRequested: false, serverCancellationConfirmed: false }; }
    }
    throw Error('IMAGEGEN_OPERATION_UNSUPPORTED');
  }
  async function stopSession(sessionId) {
    await loaded;
    for (const job of jobs.values()) if (job.sessionId === sessionId) job.cancelled = true;
    await persist();
  }
  async function closeSession(session) {
    await stopSession(session.id);
    const scope = scopes.get(session.id);
    if (scope?.adopted && tabOwners.get(scope.tabId) === session.id) {
      // A user-adopted tab is never closed by session teardown.
      tabOwners.delete(scope.tabId);
      await chrome.debugger.detach({ tabId: scope.tabId }).catch(() => {});
      if (chrome.tabs.ungroup) await chrome.tabs.ungroup(scope.tabId).catch(() => {});
    }
    scopes.delete(session.id); await persist();
  }
  // Only the packaged popup can request local binding/adoption. Web pages cannot grant themselves access.
  chrome.runtime.onMessage.addListener((message, sender, respond) => {
    if (message?.type !== 'JARVIS_IMAGEGEN_CONFIG') return;
    if (sender.id !== chrome.runtime.id || sender.url !== chrome.runtime.getURL('imagegen-popup.html')) {
      respond({ error: 'Configuration is restricted to the Jarvis extension popup.' }); return;
    }
    (async () => {
      await loaded; await sessionReady;
      if (message.action === 'state') return { connected: !!port, browser: browserName(),
        instanceId: await extensionInstanceId(), sessions: [...sessionStates.values()].filter(s => !s.closed).map(s => ({ id: s.id, imageTab: scopes.get(s.id)?.tabId || null })) };
      if (!port) throw Error('Connect Jarvis Agent before choosing a browser.');
      if (!['Chrome', 'Edge'].includes(browserName())) throw Error('ImageGen supports Chrome or Edge.');
      if (message.action === 'adopt') {
        const session = sessionStates.get(message.sessionId);
        if (!session || session.closed || message.confirmImageOnly !== true) throw Error('Select a live session and confirm this is an image-only chat.');
        if ([...jobs.values()].some(j => j.sessionId === session.id && !j.cancelled && !j.completed && !j.failed)) throw Error('Finish the active job before changing its tab.');
        const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
        if (!tab || new URL(tab.url).origin !== 'https://chatgpt.com') throw Error('Open the image-only ChatGPT tab first.');
        if (tabOwners.has(tab.id) && tabOwners.get(tab.id) !== session.id) throw Error('That tab belongs to another session.');
        const conv = (new URL(tab.url).pathname.match(/\/c\/([a-zA-Z0-9-]+)/) || [])[1] || null;
        if (conv && [...scopes.values()].some(s => s.sessionId !== session.id && s.conversationId === conv)) throw Error('That conversation is already assigned to another session.');
        await ensureGroup(tab.id, session);
        scopes.set(session.id, { sessionId: session.id, tabId: tab.id, conversationId: conv, adopted: true }); await persist();
      } else if (!['bind', 'unbind'].includes(message.action)) throw Error('Unknown configuration action.');
      post({ event: 'imagegen_bind_browser', enabled: message.action !== 'unbind' });
      return { requested: true, message: 'Selection sent to the local Agent. Refresh ImageGen settings to confirm.' };
    })().then(respond, e => respond({ error: e.message || 'Configuration failed.' }));
    return true;
  });
  return { handle, stopSession, closeSession, allowedAsset, sameConversation };
})();
