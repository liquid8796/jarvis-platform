import fs from 'node:fs/promises';
import path from 'node:path';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const modules = process.env.JARVIS_RENDERER_BUILD_MODULES || path.join(here, 'node_modules');
const require = createRequire(path.join(modules, 'package.json'));
const esbuild = require('esbuild');
const output = path.resolve(here, '../../src/JarvisCode.App/Assets/RichText');
await fs.mkdir(output, { recursive: true });
await esbuild.build({
  entryPoints: { renderer: path.join(here, 'entry.js'), 'inline-diagram': path.join(here, 'inline-diagram.js') }, outdir: output,
  nodePaths: [modules], bundle: true, splitting: true, format: 'esm', platform: 'browser',
  minify: true, target: 'chrome142', legalComments: 'linked',
});
await fs.copyFile(path.join(modules, 'katex/dist/katex.min.css'), path.join(output, 'katex.min.css'));
await fs.cp(path.join(modules, 'katex/dist/fonts'), path.join(output, 'fonts'), { recursive: true });
await fs.mkdir(path.join(output, 'licenses'), { recursive: true });
await fs.copyFile(path.join(modules, 'katex/LICENSE'), path.join(output, 'licenses/katex.txt'));
await fs.copyFile(path.join(modules, 'shiki/LICENSE'), path.join(output, 'licenses/shiki.txt'));
await fs.copyFile(path.join(modules, 'dompurify/LICENSE'), path.join(output, 'licenses/dompurify.txt'));
await fs.copyFile(path.join(modules, '@shikijs/vscode-textmate/LICENSE.md'), path.join(output, 'licenses/vscode-textmate.txt'));
for (const name of await fs.readdir(path.join(modules, '@shikijs'))) {
  const directory = path.join(modules, '@shikijs', name);
  for (const file of await fs.readdir(directory)) {
    if (/^(license|notice)/i.test(file)) await fs.copyFile(path.join(directory, file), path.join(output, 'licenses', 'shikijs-' + name + '-' + file));
  }
}
await fs.writeFile(path.join(output, 'THIRD-PARTY-NOTICES.txt'),
  'KaTeX 0.16.25 (MIT), pinned to installed Claude Desktop 1.46388.3.0. https://github.com/KaTeX/KaTeX\n' +
  'Shiki 4.4.3 and bundled TextMate grammars/themes (MIT). https://github.com/shikijs/shiki\n' +
  'DOMPurify 3.4.0 (Apache-2.0 OR MPL-2.0). https://github.com/cure53/DOMPurify\n' +
  'Oniguruma WebAssembly is included by Shiki; licenses are retained with the generated bundles.\n' +
  'Build: npm ci --prefix scripts/richtext-assets; npm run build --prefix scripts/richtext-assets\n');
