// Records the desktop reference's `visualize` server: the sections read_me
// composes, the map from module to sections, the widget runtime its ui://
// resource serves, and a hash of each so the port's copies can be checked.
//
// The chunk holds every section as a string literal and composes them in a
// pure function, so the faithful way to record it is to run the reference's
// own code. The module is evaluated in a vm with its two imports stubbed —
// they are read only by isEnabled, which this script never calls — and the
// sections are then read straight off the sandbox, because the chunk declares
// them with `var` at the top level of a CommonJS file.
//
// Three sections are functions of the widget's pixel width, not of the
// platform name: the handler maps the platform through `n` first (desktop and
// unknown both give 680, mobile 380). They do not merely interpolate that
// number — a narrow widget is told different things, and the arithmetic around
// the width changes with it — so each is recorded once per width the reference
// can ask for rather than as one parameterised template.
//
//   npx @electron/asar extract "C:/Program Files/WindowsApps/Claude_<ver>_x64__pzs8sxrjxfjjc/app/resources/app.asar" <dir>
//   node gen-visualize-corpus.js <dir> <version>
//
// Writes ../../../../src/JarvisCode.App/Assets/Visualize/* and
// visualize-corpus-<version>.json beside this script. The chunk file name
// changes every release; the anchor here is the exported declaration name,
// which does not.

const fs = require('fs');
const vm = require('vm');
const path = require('path');
const crypto = require('crypto');

const [, , asarDir, version] = process.argv;
if (!asarDir || !version) {
  console.error('usage: node gen-visualize-corpus.js <asar-dir> <version>');
  process.exit(2);
}

const here = __dirname;
const assetsDir = path.resolve(here, '../../../../src/JarvisCode.App/Assets/Visualize');
const outPath = path.join(here, `visualize-corpus-${version}.json`);

const buildDir = path.join(asarDir, '.vite', 'build');
const chunkPath = fs
  .readdirSync(buildDir)
  .filter(name => name.endsWith('.js'))
  .map(name => path.join(buildDir, name))
  .find(file => fs.readFileSync(file, 'utf8').includes('exports.getImagineServerDef'));
if (!chunkPath) {
  console.error('no chunk exports getImagineServerDef');
  process.exit(3);
}

const exported = {};
const sandbox = {
  exports: exported,
  module: { exports: exported },
  require: () => new Proxy({}, { get: () => () => undefined }),
  console,
  process: { platform: process.platform, env: {} },
};
sandbox.globalThis = sandbox;
sandbox.global = sandbox;
vm.createContext(sandbox);
new vm.Script(fs.readFileSync(chunkPath, 'utf8'), { filename: path.basename(chunkPath) })
  .runInContext(sandbox);

const definition = sandbox.exports.getImagineServerDef();
const readMeSchema = definition.tools.find(tool => tool.name === 'read_me').inputSchema;
const modules = readMeSchema.properties.modules.items.enum;
const platforms = readMeSchema.properties.platform.enum;
const widths = Object.fromEntries(platforms.map(platform => [platform, sandbox.n(platform)]));

// The sections, under names taken from what each one is about; the chunk's own
// are single letters. `svg`, `layout` and `html` are the width-parameterised
// three, recorded once per width under `w{width}/`.
const sectionOf = { svg: sandbox.s, layout: sandbox.c, html: sandbox.i };
const files = {
  'base.md': sandbox.r,
  'footer.md': sandbox.f,
  'cds-tokens.md': sandbox.t,
  'elicitation.md': sandbox.a,
  'palette.md': sandbox.o,
  'charts.md': sandbox.l,
  'data-viz.md': sandbox.u,
  'art.md': sandbox.d,
  'widget-runtime.html': sandbox.p(),
};
for (const width of new Set(Object.values(widths))) {
  for (const [name, render] of Object.entries(sectionOf)) {
    files[`w${width}/${name}.md`] = render(width);
  }
}

// Which sections each module contributes, as the chunk's `g` returns them.
const named = new Map();
for (const [file, text] of Object.entries(files)) {
  named.set(text, path.basename(file, path.extname(file)));
}
for (const width of new Set(Object.values(widths))) {
  for (const [name, render] of Object.entries(sectionOf)) {
    named.set(render(width), `w${width}/${name}`);
  }
}
const composition = {};
for (const width of new Set(Object.values(widths))) {
  const map = sandbox.g(width);
  composition[width] = {};
  for (const module of modules) {
    composition[width][module] = map[module].map(section => {
      const name = named.get(section);
      if (!name) {
        throw new Error(`unidentified section in ${width}/${module}`);
      }

      return name;
    });
  }
}

const sha = (text) => crypto.createHash('sha256').update(text, 'utf8').digest('hex');
const text = async (result) => (await result).content.map(part => part.text).join('');

main();

async function main() {
  // Recorded as hashes: the port composes read_me from the same sections, and
  // a hash proves the composition without a second copy of the corpus.
  const samples = {};
  for (const platform of platforms) {
    const all = await text(definition.handleToolCall('read_me', { modules, platform }));
    samples[platform] = { chars: all.length, sha256: sha(all) };
    for (const module of modules) {
      const one = await text(definition.handleToolCall('read_me', { modules: [module], platform }));
      samples[`${platform}/${module}`] = { chars: one.length, sha256: sha(one) };
    }
  }

  fs.mkdirSync(assetsDir, { recursive: true });
  const assets = {};
  for (const [file, content] of Object.entries(files)) {
    // The reference's own bytes; the repo keeps LF everywhere and these
    // sections carry none of the CRLF a Windows editor would add.
    fs.mkdirSync(path.dirname(path.join(assetsDir, file)), { recursive: true });
    fs.writeFileSync(path.join(assetsDir, file), content, 'utf8');
    assets[file] = { chars: content.length, sha256: sha(content) };
  }

  const resources = definition.handleListResources();
  const resource = definition.handleReadResource(resources[0].uri);

  fs.writeFileSync(outPath, JSON.stringify({
    build: version,
    chunk: path.basename(chunkPath),
    serverName: definition.serverName,
    tools: definition.tools,
    modules,
    platforms,
    widths,
    separator: '\n\n',
    showWidgetResult: await text(definition.handleToolCall('show_widget', {})),
    readMeEmpty: { chars: (await text(definition.handleToolCall('read_me', {}))).length,
      sha256: sha(await text(definition.handleToolCall('read_me', {}))) },
    composition,
    assets,
    samples,
    resources,
    runtime: { uri: resources[0].uri, mimeType: resource.contents[0].mimeType,
      meta: resource.contents[0]._meta, sha256: sha(resource.contents[0].text) },
  }, null, 1) + '\n', 'utf8');

  console.log('chunk    :', path.basename(chunkPath));
  console.log('server   :', definition.serverName, '| tools:',
    definition.tools.map(tool => tool.name).join(', '));
  console.log('widths   :', JSON.stringify(widths));
  console.log('assets   :', Object.entries(assets)
    .map(([file, meta]) => `${file}=${meta.chars}`).join(' '));
  for (const platform of platforms) {
    console.log(`read_me ${platform.padEnd(8)}: ${samples[platform].chars} chars`);
  }
}
