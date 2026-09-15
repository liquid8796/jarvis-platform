'use strict';
const vscode = require('vscode');
const fs = require('fs');
const path = require('path');
const { spawn } = require('child_process');
exports.run = async function () {
  const folder = vscode.workspace.workspaceFolders[0].uri.fsPath;
  const filename = path.join(folder, 'probe.js');
  fs.writeFileSync(filename, 'const value = ;\n');
  const doc = await vscode.workspace.openTextDocument(filename);
  const editor = await vscode.window.showTextDocument(doc);
  editor.selection = new vscode.Selection(0, 6, 0, 11);
  const extension = vscode.extensions.getExtension('jarvis-code.jarvis-code-ide');
  if (!extension) throw new Error('Development extension not found.');
  const api = await extension.activate();
  await until(() => fs.existsSync(api.lockFile), 'bridge lock');
  await until(() => vscode.languages.getDiagnostics(doc.uri).length > 0, 'JavaScript language diagnostics');
  const outside = path.join(path.dirname(folder), 'outside.txt'); fs.writeFileSync(outside, 'outside fixture');
  const child = spawn(process.env.JARVIS_IDE_SMOKE_EXE,
    [process.env.JARVIS_IDE_REGISTRY, folder, outside], { windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
  let output = ''; child.stdout.on('data', data => output += data); child.stderr.on('data', data => output += data);
  const finished = new Promise((resolve, reject) => { child.once('error', reject); child.once('exit', code => code === 0 ? resolve() : reject(new Error(output))); });
  try {
    await until(() => vscode.window.tabGroups.all.some(g => g.tabs.some(t => t.label === 'Acceptance probe')), 'approval diff');
    await vscode.commands.executeCommand('jarvisCode.acceptDiff');
    await finished;
    if (!output.includes('IDE_SMOKE_PASS')) throw new Error('Client produced no success evidence.\n' + output);
    if (doc.getText() !== 'const value = 42;\n') throw new Error('Editor buffer did not receive the accepted edit.');
    fs.writeFileSync(process.env.JARVIS_IDE_SMOKE_RESULT, output);
  } finally { if (child.exitCode === null) child.kill(); }
};
async function until(predicate, description) {
  const deadline = Date.now() + 30000;
  while (!predicate()) { if (Date.now() > deadline) throw new Error('Timed out waiting for ' + description); await new Promise(resolve => setTimeout(resolve, 100)); }
}
