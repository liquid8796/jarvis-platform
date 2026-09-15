// Records the desktop reference's Android-emulator MCP server: its wire name,
// its single tool, that tool's description and input schema, the hardware
// buttons it accepts and the gate the reference puts in front of it.
//
// The server is feature-flagged off in the installed build, so no live session
// exposes it and its declaration has to come from the bundle. Unlike the other
// servers' chunks this one pulls in electron and node:child_process at load,
// so it is read rather than executed: the definition object is located by its
// export name and each symbol it names is resolved on demand — a declaration
// is evaluated in a context holding what it turned out to need, and the three
// values it takes from a neighbouring chunk come from that chunk's export map,
// because a minified export is a getter returning a private symbol.
//
//   npx @electron/asar extract "C:/Program Files/WindowsApps/Claude_<ver>_x64__pzs8sxrjxfjjc/app/resources/app.asar" <dir>
//   node gen-android-emulator.js <dir> <version>
//
// Writes android-emulator-<version>.json beside this script. Chunk file names
// change every release; the anchors here are the exported declaration names,
// which do not.

const fs = require('fs');
const vm = require('vm');
const path = require('path');

const [, , asarDir, version] = process.argv;
if (!asarDir || !version) {
  console.error('usage: node gen-android-emulator.js <asar-dir> <version>');
  process.exit(2);
}

const buildDir = path.join(asarDir, '.vite', 'build');
const chunks = fs
  .readdirSync(buildDir)
  .filter(name => name.endsWith('.js'))
  .map(name => path.join(buildDir, name));
const read = (file) => fs.readFileSync(file, 'utf8');

const definitionChunk = chunks.find(file => read(file).includes('exports.androidEmulatorServerDefinition'));
if (!definitionChunk) {
  console.error('no chunk exports androidEmulatorServerDefinition');
  process.exit(3);
}

const source = read(definitionChunk);

/// Walks to the end of an expression: the first comma or semicolon outside any
/// bracket or string. Minified code puts many declarations on one line.
function expressionEnd(text, at) {
  let depth = 0;
  for (; at < text.length; at++) {
    const c = text[at];
    if (c === '`' || c === '"' || c === "'") {
      const quote = c;
      at++;
      while (at < text.length) {
        if (text.charCodeAt(at) === 92) { at += 2; continue; }
        if (text[at] === quote) break;
        at++;
      }

      continue;
    }

    if (c === '[' || c === '{' || c === '(') depth++;
    else if (c === ']' || c === '}' || c === ')') { if (depth === 0) return at; depth--; }
    else if ((c === ',' || c === ';') && depth === 0) return at;
  }

  return at;
}

const SEPARATORS = ',;{(=) \n\t[!&|?:\'';

/// Every `name=<expression>` declaration in the chunk, in source order: a
/// minified name is reused across scopes, so the caller tries them in turn.
function declarations(text, name) {
  const found = [];
  for (let from = 0; ;) {
    const at = text.indexOf(name + '=', from);
    if (at < 0) return found;
    from = at + 1;
    if (at > 0 && SEPARATORS.indexOf(text[at - 1]) < 0) continue;
    const start = at + name.length + 1;
    if (text[start] === '=' || text[start] === '>') continue;
    found.push(text.slice(start, expressionEnd(text, start)));
  }
}

/// The value a minified chunk exports under `name`: the export map names a
/// private symbol, and that symbol is declared elsewhere as a literal.
function exportedValue(text, name) {
  const map = new RegExp(
    'Object\\.defineProperty\\(exports,"' + name + '",\\{enumerable:!0,get:function\\(\\)\\{return (\\w+)\\}\\}\\)')
    .exec(text);
  if (!map) throw new Error(`chunk exports no ${name}`);
  for (const expression of declarations(text, map[1])) {
    try {
      return vm.runInNewContext('(' + expression + ')');
    } catch {
      // Not the literal declaration — a later assignment of the same name.
    }
  }

  throw new Error(`no literal declaration of ${map[1]} (exported as ${name})`);
}

/// A minified name may be `$e`; escaping keeps it a literal inside a RegExp.
const escapeName = (name) => name.replace(/[$]/g, '\\$&');

// The definition object's own binding, read from the export rather than
// remembered: minified names change every release, the exported name does not.
const definitionName = /exports\.androidEmulatorServerDefinition\s*=\s*([\w$]+)/.exec(source)?.[1]
  ?? (() => { console.error('chunk does not assign androidEmulatorServerDefinition'); process.exit(4); })();

// The definition imports several chunks; the one that matters is whichever
// exports the members the definition object names. Its binding in this chunk is
// the prefix those references carry, and the members are resolved on demand —
// which member names the reference uses is itself a per-release detail.
const definitionSource = declarations(source, definitionName)
  .find(text => text.includes('serverName:') && text.includes('handleToolCall:'))
  ?? (() => { console.error('no definition object literal'); process.exit(4); })();
const importBinding = /serverName:(\w+)\./.exec(definitionSource)?.[1]
  ?? (() => { console.error('serverName is not a member of an imported chunk'); process.exit(4); })();
const importedFile = new RegExp('(?:^|[^\\w$])' + escapeName(importBinding) + '=require\\(\\".\\/([\\w.-]+\\.js)\\"\\)').exec(source)?.[1]
  ?? (() => { console.error('no require for binding ' + importBinding); process.exit(4); })();
const neighbourPath = chunks.find(file => path.basename(file) === importedFile);
if (!neighbourPath) {
  console.error('imported chunk not found: ' + importedFile);
  process.exit(4);
}

const neighbour = { binding: importBinding, path: neighbourPath };
const neighbourText = read(neighbourPath);
const resolvedMembers = {};
const imported = new Proxy({}, {
  has: () => true,
  get: (_, key) => (resolvedMembers[key] ??= exportedValue(neighbourText, String(key))),
});

/// Evaluates a declaration, resolving whatever local names it turns out to
/// need from the same chunk. The imported binding is in scope throughout.
function evaluate(name, seen = new Set()) {
  if (seen.has(name)) throw new Error(`cycle resolving ${name}`);
  seen.add(name);
  const context = { [neighbour.binding]: imported };
  for (const expression of declarations(source, name)) {
    for (let attempt = 0; attempt < 12; attempt++) {
      try {
        return vm.runInNewContext('(' + expression + ')', { ...context });
      } catch (error) {
        const missing = /^(\w+) is not defined$/.exec(error.message);
        if (!missing) break;
        context[missing[1]] = evaluate(missing[1], new Set(seen));
      }
    }
  }

  throw new Error(`cannot resolve ${name}`);
}

const definitionText = definitionSource;
const shape = {
  serverName: /serverName:([\w.]+)/.exec(definitionText),
  toolName: /tools:\[\{name:([\w.]+)/.exec(definitionText),
  description: /description:(\w+)/.exec(definitionText),
  inputSchema: /inputSchema:(\w+)/.exec(definitionText),
  alwaysLoad: /alwaysLoad:!0/.test(definitionText),
  isEnabled: /isEnabled:(.*?),handleToolCall:/.exec(definitionText),
};
for (const [key, value] of Object.entries(shape)) {
  if (!value) { console.error(`definition object has no ${key}`); process.exit(5); }
}

const member = (reference) => (reference.includes('.')
  ? imported[reference.split('.')[1]]
  : evaluate(reference));
const serverName = member(shape.serverName[1]);
const toolName = member(shape.toolName[1]);
const description = evaluate(shape.description[1]);
const inputSchema = evaluate(shape.inputSchema[1]);

// The wire prefix the reference builds for a server whose name has spaces.
const clean = (value) => value.replace(/[^a-zA-Z0-9_-]/g, '_');
const wireName = `mcp__${clean(serverName)}__${clean(toolName)}`;

fs.writeFileSync(path.join(__dirname, `android-emulator-${version}.json`), JSON.stringify({
  build: version,
  chunk: path.basename(definitionChunk),
  serverName,
  wireName,
  actions: inputSchema.properties.action.enum,
  // The hardware buttons the `button` action accepts, taken from the schema the
  // reference declares rather than from the neighbour's export directly.
  buttons: inputSchema.properties.name.enum,
  isEnabledSource: shape.isEnabled[1],
  tools: [{ name: toolName, description, inputSchema, alwaysLoad: shape.alwaysLoad }],
}, null, 1) + '\n', 'utf8');

console.log('chunk    :', path.basename(definitionChunk));
console.log('server   :', serverName);
console.log('tool     :', toolName, '| wire:', wireName);
console.log('actions  :', inputSchema.properties.action.enum.join(', '));
console.log('buttons  :', inputSchema.properties.name.enum.join(', '));
console.log('schema   :', Object.keys(inputSchema.properties).join(', '));
console.log('gate     :', shape.isEnabled[1]);
