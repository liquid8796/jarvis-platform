const fs = require('fs');
const path = require('path');
const vm = require('vm');
const source = fs.readFileSync(process.argv[2], 'utf8');
const start = source.indexOf('var Wo=');
const end = source.indexOf('function $s(', start);
if (start < 0 || end < 0) throw new Error('The installed stream-ceiling region changed');
const context = vm.createContext({});
vm.runInContext(source.slice(start, end), context);
const documents = [
  'Hello world. A final word', 'Prefix\nnewword', '# Heading\n\n## Next heading', '> quoted text\n> ',
  '- item\n- [ ] next', '1. first\n12) next', '**bold and _emphasis_** text', 'word_name and ~~strike~~',
  '[label](https://example.com/a) and ![image](file.png)', 'Use `code with spaces` now',
  '```typescript\nconst name = "hello";\n```\nAfter code', '> ```js\n> const value = 1\n> ```\nnormal',
  '````text\n``` inner\n````', '~~~python\nx = 1\n~~~', '\\*literal star and \\_escaped',
  'Math $x^2+y^2$ equals 1', 'Math \\(x^2+y^2\\) equals 1', '$$x^2$$\nNext', '\\[x+y\\]\nNext',
  'Price $20 and $30 per person', 'USD $1234.50, paid', 'Use $HOME/path and $A$1:$B$2 cells',
  'Values $1 + 2$ and $123 km$ continue', 'A :antArtifact[label]{id=one} directive',
  'Vietnamese\nTiếng Việt', 'Unicode\n中文文本', 'Emoji\n😀 hello', 'Greek\nΕλληνικά',
  'Arabic\nالعربية', 'A\n(unfinished', 'one\n###', 'one\n> - [x]',
];
const inputs = new Set();
for (const document of documents) for (let i = 0; i <= document.length; i++) inputs.add(document.slice(0, i));
for (const prefix of ['a ', '\n', '**', '[', '$']) {
  const long = prefix + 'x'.repeat(720) + ' 😀';
  for (const size of [24, 25, 80, 81, 120, 599, 600, 601, 719, long.length - 1, long.length]) inputs.add(long.slice(0, size));
}
const fixtures = [...inputs].map(text => {
  context.text = text;
  return { text, units: Array.from({ length: text.length }, (_, index) => text.charCodeAt(index)),
    chat: vm.runInContext('Xs(text,ys(text))', context), code: vm.runInContext('qs(text)', context) };
});
const output = process.argv[3] || path.resolve(__dirname, '../../tests/JarvisCode.App.Tests/Fixtures/stream-ceiling-1.46388.3.0.json');
fs.writeFileSync(output, JSON.stringify(fixtures));
process.stdout.write(fixtures.length + ' measured prefix fixtures\n');
