const fs = require('fs');
const vm = require('vm');
const path = require('path');
const file = process.argv[2];
const output = process.argv[3] || path.resolve(__dirname, '../../tests/JarvisCode.App.Tests/Fixtures/reference-highlight-1.46388.3.0.json');
const modules = process.env.JARVIS_RENDERER_BUILD_MODULES || path.join(__dirname, 'node_modules');
const pending = new Map();
let listener;
let ready;
const started = new Promise(resolve => ready = resolve);
const context = vm.createContext({
  TextDecoder, TextEncoder, performance, console, setTimeout, clearTimeout, atob, btoa,
  self: { addEventListener: (_, fn) => listener = fn, postMessage: message => {
    if (message.kind === 'ready') ready(message);
    else if (pending.has(message.id ?? message.streamId)) { pending.get(message.id ?? message.streamId)(message); pending.delete(message.id ?? message.streamId); }
  } },
});
vm.runInContext(fs.readFileSync(file, 'utf8'), context, { timeout: 10000 });
(async () => {
  await started;
  const loaded = vm.runInContext('Object.keys(sa)', context);
  const referenceThemes = vm.runInContext('JSON.stringify(ca)', context);
  fs.writeFileSync(path.join(__dirname, 'reference-themes.json'), referenceThemes);
  process.stdout.write(JSON.stringify({ loaded }) + '\n');
  const shiki = await import(require('url').pathToFileURL(path.resolve(modules, 'shiki/dist/index.mjs')).href);
  const theme = (await shiki.bundledThemes['github-dark']()).default;
  const engine = await shiki.createHighlighter({ themes: [theme, ...JSON.parse(referenceThemes)], langs: [] });
  const samples = {
    javascript: 'const result = /abc+/g.test("text"); // comment',
    typescript: 'interface User { name: string; age?: number }',
    tsx: 'const element = <button disabled={false}>Run</button>;',
    python: 'def greet(name: str):\n    return f"Hello {name}"',
    sql: 'SELECT count(*) AS total FROM users WHERE active = true;',
    rust: 'fn main() { let value: Option<i32> = Some(42); println!("{:?}", value); }',
    csharp: 'public record User(string Name) { public int Age { get; init; } }',
    yaml: 'service:\n  port: 8080\n  enabled: true',
    html: '<style>.x { color: red }</style><button title="Go">Go</button>',
    markdown: '# Heading\n\n**strong** and [link](https://example.com)',
  };
  const fixtures = [];
  const flatten = lines => (Array.isArray(lines[0]) ? lines.flat() : lines).flatMap(token => [...token.content].filter(text => text !== '\n' && text !== '\r').map(text => [text, token.color, token.fontStyle || 0]));
  for (const themeName of ['github-dark', 'claude-dark', 'claude-light']) for (const [lang, text] of Object.entries(samples)) {
    const grammar = (await shiki.bundledLanguages[lang]()).default;
    const id = themeName + ':' + lang;
    const answer = new Promise(resolve => pending.set(id, resolve));
    listener({ data: { kind: 'stream', streamId: id, text, lang, langRegistration: grammar, themes: { theme: themeName }, themeRegistrations: [theme] } });
    const reference = await answer;
    await engine.loadLanguage(lang);
    const candidate = engine.codeToTokensBase(text, { lang, theme: themeName });
    const equal = JSON.stringify(flatten(reference.tokens || [])) === JSON.stringify(flatten(candidate));
    fixtures.push({ lang, theme: themeName, text, tokens: reference.tokens, equal, error: reference.error, loaded: loaded.includes(lang) });
  }
  fs.writeFileSync(output, JSON.stringify(fixtures, null, 2));
  process.stdout.write(JSON.stringify(fixtures.map(({ lang, equal, loaded, error }) => ({ lang, equal, loaded, error })), null, 2));
})().catch(error => { process.stderr.write(error.stack); process.exitCode = 1; });
