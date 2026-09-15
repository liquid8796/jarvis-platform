import DOMPurify from 'dompurify';

DOMPurify.addHook('uponSanitizeAttribute', (_node, data) => {
  if ((data.attrName === 'href' || data.attrName === 'xlink:href') && !data.attrValue.startsWith('#')) data.keepAttr = false;
  if (data.attrName === 'style') data.attrValue = data.attrValue.split(';').filter(property =>
    !/(?:url\s*\(|expression\s*\(|position\s*:|z-index\s*:|behavior\s*:)/i.test(property)).join(';');
});
const host = document.getElementById('diagram');
const shadow = host.attachShadow({ mode: 'open' });
window.jarvisDiagram = {
  render(svg) {
    const sanitized = DOMPurify.sanitize(svg, { USE_PROFILES: { svg: true, svgFilters: true }, ADD_TAGS: ['style'] });
    shadow.innerHTML = '<style>:host{display:block}svg{display:block;width:100%;height:auto;max-width:100%}text,tspan{user-select:text}</style>' + sanitized;
    return !!shadow.querySelector('svg');
  },
};
window.addEventListener('wheel', event => {
  if (event.ctrlKey || typeof window.__jarvisDiagramPost !== 'function') return;
  event.preventDefault();
  window.__jarvisDiagramPost(JSON.stringify({ kind: 'wheel', delta: event.deltaY }));
}, { passive: false });
