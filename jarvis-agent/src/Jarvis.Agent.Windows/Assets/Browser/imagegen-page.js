// Fixed ChatGPT page adapter. Executed in an isolated extension world, not supplied by a model.
// No auth token/cookie is returned or used to call the generation API. Unsupported DOM fails closed.
globalThis.jarvisImageGenPage = async function (action, data = {}) {
  const fail = (code, message) => ({ errorCode: code, message });
  if (location.origin !== 'https://chatgpt.com') return fail('ORIGIN_CHANGED', 'The ImageGen tab is no longer on ChatGPT.');
  const normal = s => String(s || '').replace(/\s+/g, ' ').trim();
  const visible = e => !!e && e.getClientRects().length > 0;
  const conversationId = (location.pathname.match(/\/c\/([a-zA-Z0-9-]+)/) || [])[1] || null;
  const composer = () => document.querySelector('#prompt-textarea[contenteditable="true"],textarea#prompt-textarea');
  const form = () => composer()?.closest('form') || document.querySelector('#composer-background');
  const users = () => [...document.querySelectorAll('[data-message-author-role="user"]')];
  const userText = e => normal((e.querySelector('.whitespace-pre-wrap') || e).innerText || e.textContent);
  const id = e => e.getAttribute('data-message-id') || e.closest('[data-message-id]')?.getAttribute('data-message-id') || null;
  const busy = () => !!document.querySelector('[data-testid="stop-button"],button[aria-label="Stop generating"],button[aria-label="Dừng tạo"]');
  function attachments(expected) {
    const root = form();
    if (!root) return { ready: false };
    const text = normal(root.innerText + ' ' + [...root.querySelectorAll('[title],[aria-label],img')]
      .map(e => (e.title || '') + ' ' + (e.getAttribute('aria-label') || '') + ' ' + (e.alt || '')).join(' '));
    const processing = !!root.querySelector('[role="progressbar"],progress,[data-state="uploading"],.animate-spin');
    return { ready: !processing && expected.every(name => text.includes(name)), processing };
  }
  function currentTurn(marker) {
    const all = users(), matches = all.filter(e => userText(e).includes(marker));
    if (matches.length !== 1 || all.at(-1) !== matches[0]) return null;
    const user = matches[0];
    const article = user.closest('article') || user.closest('[data-testid^="conversation-turn"]');
    if (!article || !id(user)) return null;
    const assistants = [...document.querySelectorAll('[data-message-author-role="assistant"]')]
      .filter(e => !!(article.compareDocumentPosition(e) & Node.DOCUMENT_POSITION_FOLLOWING));
    return { user, userId: id(user), assistants };
  }
  function generatedCards(turn) {
    const cards = [], seen = new Set();
    for (const assistant of turn.assistants) {
      for (const image of assistant.querySelectorAll('img')) {
        if (!image.complete || image.naturalWidth < 16 || image.naturalHeight < 16 || !image.currentSrc && !image.src) continue;
        let node = image.parentElement, card = null, control = null;
        for (let i = 0; node && node !== assistant.parentElement && i < 8; i++, node = node.parentElement) {
          const controls = [...node.querySelectorAll('button,a[download]')].filter(e => {
            const label = normal((e.getAttribute('aria-label') || '') + ' ' + (e.title || '') + ' ' + (e.innerText || ''));
            return e.hasAttribute('download') || /^(save image|download image|download|lưu ảnh|tải xuống|tải ảnh)(\s|$)/i.test(label);
          });
          if (controls.length === 1 && node.querySelectorAll('img').length === 1) { card = node; control = controls[0]; break; }
        }
        if (!card || !control) continue;
        const semantic = normal((image.alt || '') + ' ' + (card.innerText || '') + ' ' + (card.getAttribute('data-testid') || ''));
        if (!/generated|image created|created image|media.generation|ảnh.*tạo|đã tạo|tạo.*ảnh/i.test(semantic)) continue;
        const key = (id(assistant) || '') + ':' + (image.currentSrc || image.src);
        if (seen.has(key)) continue;
        seen.add(key); cards.push({ key, image, control });
      }
    }
    return cards;
  }
  if (action === 'account') {
    try {
      const controller = new AbortController();
      const timer = setTimeout(() => controller.abort(), 8000);
      let response;
      try { response = await fetch('/api/auth/session', { credentials: 'include', redirect: 'error', signal: controller.signal }); }
      finally { clearTimeout(timer); }
      if (response.status === 403) return fail('CHALLENGE', 'Complete the verification in your selected ChatGPT tab.');
      if (!response.ok) return fail('ACCOUNT_UNVERIFIED', 'ChatGPT account state could not be verified.');
      // Only user identity is inspected. Never export accessToken, cookies or the full response.
      const identity = (await response.json())?.user?.id;
      if (typeof identity !== 'string' || !identity) return fail('NOT_SIGNED_IN', 'Sign in to ChatGPT in this browser profile.');
      const digest = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(identity));
      return { accountHash: [...new Uint8Array(digest)].map(b => b.toString(16).padStart(2, '0')).join('') };
    } catch { return fail('ACCOUNT_UNVERIFIED', 'ChatGPT account state could not be verified.'); }
  }
  if (action === 'inspect') {
    if (document.querySelector('iframe[src*="challenges.cloudflare.com"],#challenge-running')) return fail('CHALLENGE', 'Complete the verification in ChatGPT.');
    const editor = composer();
    if (!editor) return { ready: false, conversationId };
    if (busy()) return fail('TAB_BUSY', 'This ChatGPT conversation is already generating a response.');
    if (normal(editor.value ?? editor.innerText)) return fail('DRAFT_PRESENT', 'The ImageGen composer contains a draft. It was not overwritten.');
    const root = form();
    if (root?.querySelector('img') || [...(root?.querySelectorAll('input[type="file"]') || [])].some(e => e.files?.length))
      return fail('ATTACHMENTS_PRESENT', 'The composer already contains attachments. Clear them explicitly before starting a new image job; they were not reused or removed.');
    const all = users();
    if (all.some(e => !id(e))) return fail('PAGE_UNSUPPORTED', 'ChatGPT message identities could not be verified.');
    return { ready: true, conversationId, userIds: all.map(id) };
  }
  if (action === 'mark_upload') {
    if (busy() || !composer() || normal(composer().value ?? composer().innerText)) return fail('TAB_BUSY', 'The ImageGen composer is not empty.');
    const inputs = [...(form()?.querySelectorAll('input[type="file"]') || [])].filter(e => !e.disabled && (!e.accept || /image|png|jpg/i.test(e.accept)));
    if (!inputs.length) return fail('PAGE_UNSUPPORTED', 'ChatGPT file input was not found.');
    const input = inputs.find(e => e.multiple) || inputs[0];
    if (data.count > 1 && !input.multiple) return fail('UPLOAD_INCOMPLETE', 'This composer cannot attach all reference images together.');
    input.setAttribute('data-jarvis-imagegen-input', data.jobId);
    return { ready: true };
  }
  if (action === 'upload_status') return attachments(data.names || []);
  if (action === 'focus_composer') {
    const editor = composer();
    if (!editor || busy() || normal(editor.value ?? editor.innerText)) return fail('DRAFT_PRESENT', 'The ChatGPT composer changed before submission.');
    if (!attachments(data.names || []).ready) return fail('UPLOAD_INCOMPLETE', 'Reference image uploads could not be verified. No prompt was sent.');
    editor.focus(); return { ready: true };
  }
  if (action === 'submit') {
    const editor = composer();
    if (!editor || normal(editor.value ?? editor.innerText) !== normal(data.text)) return fail('DRAFT_CHANGED', 'Composer text differs from the accepted image request.');
    if (busy() || !attachments(data.names || []).ready) return fail('UPLOAD_INCOMPLETE', 'References or composer state changed before send.');
    const all = users().map(id);
    if (JSON.stringify(all) !== JSON.stringify(data.userIds)) return fail('CONVERSATION_CHANGED', 'Another user message appeared before send.');
    const findSend = () => document.querySelector('[data-testid="send-button"],button[aria-label="Send prompt"],button[aria-label="Gửi lời nhắc"]');
    let button = findSend();
    // React may replace the voice control with Send after native insertText returns.
    for (let i = 0; i < 40 && (!button || button.disabled || !visible(button)); i++) {
      await new Promise(resolve => setTimeout(resolve, 50)); button = findSend();
    }
    if (!button || button.disabled || !visible(button)) return fail('PAGE_UNSUPPORTED', 'ChatGPT Send control is not available.');
    if (normal(editor.value ?? editor.innerText) !== normal(data.text) || busy() ||
        JSON.stringify(users().map(id)) !== JSON.stringify(data.userIds) || !attachments(data.names || []).ready)
      return fail('CONVERSATION_CHANGED', 'The composer or conversation changed before Send.');
    button.click(); return { submitted: true };
  }
  if (action === 'poll' || action === 'download_plan' || action === 'click_save') {
    const turn = currentTurn(data.marker);
    if (!turn) {
      const all = users();
      if (all.some(e => userText(e).includes(data.marker))) return fail('CONVERSATION_CHANGED', 'A different message followed this image request.');
      return { ready: false, conversationId };
    }
    if (data.userIds && users().length !== data.userIds.length + 1) return fail('CONVERSATION_CHANGED', 'The active conversation branch changed.');
    if (data.userMessageId && turn.userId !== data.userMessageId) return fail('CONVERSATION_CHANGED', 'The original request message identity changed.');
    const cards = generatedCards(turn);
    if (cards.length > 5) return fail('RESULT_UNVERIFIED', 'More than five images were returned; inspect the ChatGPT conversation.');
    const text = normal(turn.assistants.map(e => e.innerText || '').join(' '));
    if (!busy() && !cards.length) {
      if (/reached.{0,60}(image|creation).{0,30}limit|image (generation|creation) limit|giới hạn.{0,30}(tạo )?ảnh/i.test(text))
        return fail('QUOTA_EXCEEDED', 'ChatGPT reported its image creation limit. No paid API fallback was used.');
      if (/content policy|chính sách nội dung|(?:cannot|can't|unable to|không thể).{0,50}(?:generate|create|edit|tạo|chỉnh sửa).{0,30}(?:image|ảnh)/i.test(text))
        return fail('GENERATION_REJECTED', 'ChatGPT declined this image request.');
    }
    if (action === 'poll') return { ready: !busy() && cards.length > 0, conversationId, userMessageId: turn.userId,
      keys: cards.map(c => c.key), count: cards.length };
    if (busy() || !cards[data.index] || cards[data.index].key !== data.key) return fail('RESULT_UNVERIFIED', 'The selected generated image changed before download.');
    const control = cards[data.index].control;
    if (action === 'download_plan') return { href: control.tagName === 'A' && control.hasAttribute('download') ? control.href : null, conversationId };
    control.click(); return { clicked: true };
  }
  if (action === 'cancel') {
    if (!currentTurn(data.marker)) return { stopRequested: false };
    const stop = document.querySelector('[data-testid="stop-button"],button[aria-label="Stop generating"],button[aria-label="Dừng tạo"]');
    if (stop) stop.click();
    return { stopRequested: !!stop, serverCancellationConfirmed: false };
  }
  return fail('PAGE_UNSUPPORTED', 'Unsupported ImageGen page operation.');
};
