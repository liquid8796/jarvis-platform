const fs = require('node:fs');
const vm = require('node:vm');
const assert = require('node:assert/strict');

(async () => {
  const input = JSON.parse(fs.readFileSync(0, 'utf8'));
  let clicks = 0;
  let pastes = 0;
  let cancelled = false;
  const chunks = [];
  let context;
  const cancel = () => {
    if (!cancelled) { cancelled = true; vm.runInContext(input.cancel, context); }
  };
  const box = {
    innerText: '', isConnected: true, isContentEditable: true,
    focus() { document.activeElement = this; },
    getClientRects() { return [{}]; },
    getAttribute() { return null; },
    closest() { return null; },
    dispatchEvent(event) {
      const text = event.clipboardData.text;
      chunks.push(text);
      this.innerText += text;
      pastes++;
      if (input.scenario === 'paste') cancel();
    },
  };
  const send = { disabled: false, click() { clicks++; } };
  const turn = {
    innerText: 'Working\nExact final answer',
    getAttribute() { return 'conversation-turn-1'; },
    querySelector(selector) {
      if (selector.includes('copy-turn-action-button')) return {};
      if (selector === '.agent-turn') return {};
      if (selector.includes('data-message-author-role')) return answer;
      return null;
    },
    querySelectorAll() { return []; },
    closest() { return this; },
  };
  const answer = {
    innerText: 'Exact final answer',
    getAttribute() { return 'answer-1'; },
    closest() { return turn; },
  };
  const document = {
    activeElement: null,
    createRange() { return { selectNodeContents(node) { assert.equal(node, box); }, collapse() {} }; },
    body: { contains() { return true; } },
    querySelector(selector) {
      if (selector.includes('prompt-textarea') || selector.includes('contenteditable')) {
        if (input.scenario === 'composer') { cancel(); return null; }
        return box;
      }
      if (selector.includes('send-button') || selector.includes('Send')) {
        if (input.scenario === 'send') cancel();
        return clicks ? null : send;
      }
      return null;
    },
    querySelectorAll(selector) {
      if (selector.includes('prompt-textarea')) {
        if (input.scenario === 'composer') { cancel(); return []; }
        return [box];
      }
      if (selector.includes('data-message-author-role')) return clicks ? [answer] : [];
      if (selector.includes('conversation-turn')) return clicks ? [turn] : [];
      return [];
    },
    execCommand(command) {
      assert.notEqual(command, 'insertText', 'prompt must never use the escaping fallback');
      if (command === 'delete') box.innerText = '';
    },
  };
  context = vm.createContext({
    window: { getSelection() { return { removeAllRanges() {}, addRange() {} }; } },
    document, location: { pathname: '/c/12345678-abcd-abcd-abcd-123456789012' },
    setTimeout: (fn, ms) => setTimeout(fn, Math.min(ms, 1)), clearTimeout,
    DataTransfer: class { setData(type, text) { assert.equal(type, 'text/plain'); this.text = text; } },
    ClipboardEvent: class { constructor(type, options) { Object.assign(this, options); } },
    PointerEvent: class {},
  });
  vm.runInContext(input.kickoff, context);
  const deadline = Date.now() + 10000;
  while (!context.window.__jarvisChatGpt.test.done && Date.now() < deadline)
    await new Promise(resolve => setTimeout(resolve, 2));
  const operation = context.window.__jarvisChatGpt.test;
  assert.equal(operation.done, true, 'the page promise must settle');
  const result = JSON.parse(operation.result);
  if (input.scenario === 'complete') {
    assert.equal(clicks, 1);
    assert.equal(chunks.join(''), input.prompt);
    assert(pastes > 10);
    for (const chunk of chunks) {
      assert(chunk.length <= 8000);
      assert(!/[\uD800-\uDBFF]$/.test(chunk), 'must not split a surrogate pair');
    }
    assert.equal(result.text, 'Exact final answer');
    assert.equal(context.window.__jarvisChatGpt.answerPreview, result.text);
  } else {
    assert.equal(clicks, 0, 'cancelled preparation must not send');
    assert.match(result.error, /CANCELLED/);
    const afterCancel = pastes;
    await new Promise(resolve => setTimeout(resolve, 10));
    assert.equal(pastes, afterCancel, 'no abandoned paste after completion');
  }
  process.stdout.write(`PASS ${input.scenario}\n`);
})().catch(error => { console.error(error); process.exitCode = 1; });
