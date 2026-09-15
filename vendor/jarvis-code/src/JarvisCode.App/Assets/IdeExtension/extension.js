'use strict';
const vscode = require('vscode');
const net = require('net');
const fs = require('fs');
const path = require('path');
const os = require('os');
const crypto = require('crypto');
const MAX = 8 * 1024 * 1024;
let stop;

function activate(context) {
  const id = crypto.randomBytes(16).toString('hex');
  const token = crypto.randomBytes(32).toString('hex');
  const name = `jarvis-ide-${process.pid}-${id}`;
  const registry = process.env.JARVIS_IDE_REGISTRY || path.join(os.homedir(), '.jarvis', 'ide');
  const lock = path.join(registry, `${id}.lock`);
  const pipe = process.platform === 'win32' ? `\\\\.\\pipe\\${name}` : path.join(os.tmpdir(), name);
  const diffs = new Map();
  const documents = new Map();
  const connections = new Set();
  let clientName = '';
  const status = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Right, 20);
  status.text = '$(plug) Jarvis'; status.tooltip = 'Jarvis Code IDE bridge is available for this workspace';
  status.command = 'jarvisCode.connectionStatus'; status.show(); context.subscriptions.push(status);
  const folders = () => (vscode.workspace.workspaceFolders || []).filter(f => f.uri.scheme === 'file').map(f => f.uri.fsPath);
  const publish = () => {
    fs.mkdirSync(registry, { recursive: true, mode: 0o700 });
    const value = { id, pid: process.pid, startedAt: Math.round(Date.now() - process.uptime() * 1000),
      ideName: vscode.env.appName, workspaceFolders: folders(), transport: 'jarvis-pipe', pipeName: name, authToken: token };
    const temp = `${lock}.tmp`; fs.writeFileSync(temp, JSON.stringify(value), { mode: 0o600 }); fs.renameSync(temp, lock);
  };
  const allowed = filename => {
    const full = path.resolve(filename);
    const real = fs.existsSync(full) ? fs.realpathSync.native(full) : path.join(fs.realpathSync.native(path.dirname(full)), path.basename(full));
    const contains = (root, target) => { const relative = path.relative(root, target); return relative === '' || (!relative.startsWith(`..${path.sep}`) && relative !== '..' && !path.isAbsolute(relative)); };
    if (!folders().some(folder => contains(fs.realpathSync.native(folder), real))) throw new Error('The file is outside this editor workspace.');
    return full;
  };
  const fileUri = value => {
    if (typeof value !== 'string' || !value) throw new Error('A file path is required.');
    const candidate = value.startsWith('file:') ? vscode.Uri.parse(value) : vscode.Uri.file(value);
    if (candidate.scheme !== 'file') throw new Error('Only local workspace files are supported.');
    return vscode.Uri.file(allowed(candidate.fsPath));
  };
  context.subscriptions.push(vscode.workspace.registerTextDocumentContentProvider('jarvis-diff', {
    provideTextDocumentContent: uri => documents.get(uri.toString()) || ''
  }));
  function currentDiff() {
    const input = vscode.window.tabGroups.activeTabGroup.activeTab?.input;
    return input instanceof vscode.TabInputTextDiff ? diffs.get(input.modified.authority) : undefined;
  }
  async function finish(diff, accepted) {
    if (!diff) return false;
    if (accepted) {
      if (diff.readOnly) throw new Error('This comparison is read-only. Edit the file to change it.');
      const exists = fs.existsSync(diff.target.fsPath);
      const doc = exists ? await vscode.workspace.openTextDocument(diff.target) : null;
      if (exists !== diff.existed || (doc && doc.getText() !== diff.current)) throw new Error('The file changed after this review opened. Open a fresh diff before accepting.');
      const edit = new vscode.WorkspaceEdit();
      if (!exists) edit.createFile(diff.target, { overwrite: false });
      edit.replace(diff.target, doc ? new vscode.Range(doc.positionAt(0), doc.positionAt(doc.getText().length)) : new vscode.Range(0, 0, 0, 0), diff.proposed);
      if (!await vscode.workspace.applyEdit(edit)) throw new Error('The editor could not apply the change.');
      if (!await (await vscode.workspace.openTextDocument(diff.target)).save()) throw new Error('The editor could not save the change.');
    }
    diff.resolve?.([{ type: 'text', text: accepted ? 'DIFF_ACCEPTED' : 'DIFF_REJECTED' }, { type: 'text', text: accepted ? diff.proposed : diff.current }]);
    diffs.delete(diff.id);
    const tabs = vscode.window.tabGroups.all.flatMap(g => g.tabs).filter(t => t.input instanceof vscode.TabInputTextDiff && t.input.modified.authority === diff.id);
    await vscode.window.tabGroups.close(tabs);
    documents.delete(diff.oldUri.toString()); documents.delete(diff.newUri.toString());
    return true;
  }
  context.subscriptions.push(vscode.commands.registerCommand('jarvisCode.acceptDiff', async () => {
    try { await finish(currentDiff(), true); } catch (e) { vscode.window.showErrorMessage(e.message); }
  }));
  context.subscriptions.push(vscode.commands.registerCommand('jarvisCode.rejectDiff', () => finish(currentDiff(), false)));
  context.subscriptions.push(vscode.commands.registerCommand('jarvisCode.connectionStatus', () =>
    vscode.window.showInformationMessage(clientName ? `Connected client: ${clientName}` : 'Jarvis Code IDE bridge is ready.')));
  context.subscriptions.push(vscode.window.onDidChangeActiveTextEditor(() =>
    vscode.commands.executeCommand('setContext', 'jarvisCode.diffCanApply', !!currentDiff() && !currentDiff().readOnly)));
  context.subscriptions.push(vscode.window.tabGroups.onDidChangeTabs(event => {
    for (const tab of event.closed) if (tab.input instanceof vscode.TabInputTextDiff) {
      const diff = diffs.get(tab.input.modified.authority);
      if (diff) { diff.resolve?.([{ type: 'text', text: 'DIFF_REJECTED' }]); diffs.delete(diff.id); documents.delete(diff.oldUri.toString()); documents.delete(diff.newUri.toString()); }
    }
  }));

  async function call(method, args = {}) {
    if (!vscode.workspace.isTrusted) throw new Error('Trust this workspace before connecting Jarvis Code.');
    switch (method) {
      case 'getDiagnostics': {
        const entries = args.uri ? [[fileUri(args.uri), vscode.languages.getDiagnostics(fileUri(args.uri))]] : vscode.languages.getDiagnostics();
        return entries.filter(([uri]) => { try { fileUri(uri.toString()); return true; } catch { return false; } }).map(([uri, diagnostics]) => ({
          uri: uri.toString(), diagnostics: diagnostics.map(d => ({ message: d.message, source: d.source, code: typeof d.code === 'object' ? d.code.value : d.code,
            severity: ['Error', 'Warning', 'Information', 'Hint'][d.severity], range: { start: d.range.start, end: d.range.end } }))
        }));
      }
      case 'getSelection':
      case 'getCurrentSelection': {
        const editor = vscode.window.activeTextEditor;
        if (!editor || editor.document.uri.scheme !== 'file') return { text: '', filePath: null };
        fileUri(editor.document.uri.toString());
        return { text: editor.document.getText(editor.selection).slice(0, 64000), filePath: editor.document.uri.fsPath,
          fileUrl: editor.document.uri.toString(), selection: { start: editor.selection.start, end: editor.selection.end }, version: editor.document.version };
      }
      case 'getOpenEditors': return vscode.window.visibleTextEditors.filter(e => { try { fileUri(e.document.uri.toString()); return true; } catch { return false; } }).map(e => ({ uri: e.document.uri.toString(), isActive: e === vscode.window.activeTextEditor }));
      case 'openFile': {
        const uri = fileUri(args.file_path || args.uri);
        const editor = await vscode.window.showTextDocument(await vscode.workspace.openTextDocument(uri), { preview: false });
        if (Number.isInteger(args.line)) { const pos = new vscode.Position(Math.max(0, args.line - 1), 0); editor.selection = new vscode.Selection(pos, pos); editor.revealRange(editor.selection); }
        return { opened: uri.toString() };
      }
      case 'openDiff': {
        const target = fileUri(args.new_file_path);
        const existed = fs.existsSync(target.fsPath);
        const current = existed ? (await vscode.workspace.openTextDocument(target)).getText() : '';
        const old = typeof args.old_file_contents === 'string' ? args.old_file_contents : args.old_file_path && fs.existsSync(fileUri(args.old_file_path).fsPath) ? (await vscode.workspace.openTextDocument(fileUri(args.old_file_path))).getText() : '';
        if (typeof args.new_file_contents !== 'string') throw new Error('new_file_contents is required.');
        const key = crypto.randomBytes(16).toString('hex');
        const oldUri = vscode.Uri.parse(`jarvis-diff://${key}/original/${encodeURIComponent(path.basename(target.fsPath))}`);
        const newUri = vscode.Uri.parse(`jarvis-diff://${key}/proposed/${encodeURIComponent(path.basename(target.fsPath))}`);
        documents.set(oldUri.toString(), old); documents.set(newUri.toString(), args.new_file_contents);
        const diff = { id: key, target, current, existed, readOnly: args.read_only === true, proposed: args.new_file_contents, oldUri, newUri, name: args.tab_name || 'Jarvis Code change' };
        diffs.set(key, diff);
        await vscode.commands.executeCommand('vscode.diff', oldUri, newUri, diff.name, { preview: false });
        await vscode.commands.executeCommand('setContext', 'jarvisCode.diffCanApply', !diff.readOnly);
        if (args.wait_for_decision === false) return { opened: key, tab_name: diff.name };
        return new Promise(resolve => { diff.resolve = resolve; });
      }
      case 'closeDiff':
      case 'close_tab': {
        const diff = [...diffs.values()].find(d => d.id === args.id || d.name === args.tab_name);
        return { closed: await finish(diff, false) };
      }
      case 'closeAllDiffTabs': {
        const items = [...diffs.values()]; for (const diff of items) await finish(diff, false); return { closed: items.length };
      }
      default: throw new Error(`Unknown editor operation: ${method}`);
    }
  }
  const server = net.createServer(socket => {
    connections.add(socket); socket.on('close', () => connections.delete(socket));
    let buffer = Buffer.alloc(0); let handled = false;
    socket.setTimeout(30 * 60 * 1000, () => socket.destroy());
    socket.on('error', () => {});
    socket.on('data', async chunk => {
      if (handled) return;
      buffer = Buffer.concat([buffer, chunk]);
      if (buffer.length < 4) return;
      const length = buffer.readInt32LE(0);
      if (length < 1 || length > MAX) return socket.destroy();
      if (buffer.length < length + 4) return;
      handled = true;
      let request; let response;
      try {
        request = JSON.parse(buffer.subarray(4, length + 4).toString('utf8'));
        const supplied = Buffer.from(typeof request.authToken === 'string' ? request.authToken : '');
        const expected = Buffer.from(token);
        if (supplied.length !== expected.length || !crypto.timingSafeEqual(supplied, expected)) throw new Error('Editor client authentication failed.');
        if (request.clientInfo?.name !== 'Jarvis Code' || !Number.isInteger(request.clientInfo?.pid)) throw new Error('Editor client identity is required.');
        try { process.kill(request.clientInfo.pid, 0); } catch { throw new Error('The editor client process is no longer running.'); }
        if (request.method !== 'tools/call') throw new Error('Only editor tool calls are supported.');
        clientName = `${request.clientInfo.name} (process ${request.clientInfo.pid})`;
        const result = await call(request.params?.name, request.params?.arguments);
        response = { jsonrpc: '2.0', id: request.id, result };
      } catch (e) { response = { jsonrpc: '2.0', id: request?.id ?? null, error: { code: -32000, message: e.message } }; }
      const bytes = Buffer.from(JSON.stringify(response));
      if (bytes.length > MAX) return socket.destroy();
      const prefix = Buffer.alloc(4); prefix.writeInt32LE(bytes.length); socket.end(Buffer.concat([prefix, bytes]));
    });
  });
  server.on('error', error => { status.tooltip = `Jarvis Code bridge unavailable: ${error.message}`; });
  server.listen(pipe, publish);
  context.subscriptions.push(vscode.workspace.onDidChangeWorkspaceFolders(publish));
  stop = () => { server.close(); for (const socket of connections) socket.destroy(); connections.clear(); for (const diff of diffs.values()) diff.resolve?.([{ type: 'text', text: 'DIFF_REJECTED' }]); try { fs.unlinkSync(lock); } catch {} };
  context.subscriptions.push({ dispose: stop });
  return { call, get lockFile() { return lock; } };
}
function deactivate() { stop?.(); }
module.exports = { activate, deactivate };
