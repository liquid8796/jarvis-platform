import { createHighlighter, bundledLanguages, bundledThemes } from 'shiki';
import katex from 'katex';
import referenceThemes from './reference-themes.json';

const highlighter = createHighlighter({ themes: [], langs: [] });
const registered = new Set();

window.jarvisRichText = {
  versions: { shiki: '4.4.3', katex: katex.version },
  async highlight({ code, language, theme }) {
    const engine = await highlighter;
    const lang = String(language || 'text').toLowerCase();
    if (!bundledLanguages[lang]) return [{ text: code }];
    await engine.loadLanguage(lang);
    if (!registered.has(theme.Id)) {
      if (bundledThemes[theme.Id]) await engine.loadTheme(theme.Id);
      else if (referenceThemes.some(entry => entry.name === theme.Id)) await engine.loadTheme(referenceThemes.find(entry => entry.name === theme.Id));
      else {
        engine.loadTheme({
          name: theme.Id, type: theme.Mode === 1 ? 'dark' : 'light',
          colors: { 'editor.foreground': theme.Foreground || '#888888', 'editor.background': theme.Background || '#000000' },
          tokenColors: [
            { scope: 'comment', settings: { foreground: theme.Comment } },
            { scope: 'string', settings: { foreground: theme.String } },
            { scope: 'keyword', settings: { foreground: theme.Keyword } },
            { scope: 'constant.numeric', settings: { foreground: theme.Number } },
            { scope: 'variable', settings: { foreground: theme.Variable || theme.Foreground } },
          ].filter(token => token.settings.foreground),
        });
      }
      registered.add(theme.Id);
    }
    const result = engine.codeToTokens(code, { lang, theme: theme.Id });
    const breaks = code.match(/\r\n|\n|\r/g) || [];
    return result.tokens.flatMap((line, index) => [
      ...line.map(token => ({ text: token.content, color: token.color, fontStyle: token.fontStyle || 0 })),
      ...(index < result.tokens.length - 1 ? [{ text: breaks[index] || '\n' }] : []),
    ]);
  },
  async math({ latex, display, size, color }) {
    const element = document.getElementById('math');
    element.style.fontSize = size + 'px';
    element.style.color = color;
    katex.render(latex, element, { displayMode: display, errorColor: 'inherit', output: 'htmlAndMathml', strict: false, throwOnError: false });
    await document.fonts.ready;
    const bounds = element.getBoundingClientRect();
    return { width: Math.ceil(bounds.width), height: Math.ceil(bounds.height), html: element.innerHTML, error: !!element.querySelector('.katex-error') };
  },
};
