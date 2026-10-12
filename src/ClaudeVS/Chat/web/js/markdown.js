// Rendu markdown (marked + DOMPurify) et diagrammes Mermaid. marked, DOMPurify et mermaid sont des globales
// chargées par index.html avant les modules.
import { state } from './state.js';
import { el, scroll, messagesEl } from './dom.js';

// Le HTML brut que marked laisse passer est cloisonné : ni id/name (clobbering), ni style (recouvrement du panneau),
// ni éléments de formulaire, ni data-* ; « class » n'est gardé que sur <code class="language-…"> (blocs mermaid).
const PURIFY_CFG = {
  ADD_ATTR: ['target'], ALLOW_DATA_ATTR: false,
  FORBID_ATTR: ['id', 'name', 'style'],
  FORBID_TAGS: ['form', 'input', 'button', 'select', 'textarea', 'label', 'style'],
};

// Appelé une fois par main.js, avant tout rendu
export function setupMarkdown() {
  marked.setOptions({ gfm: true, breaks: false });
  DOMPurify.addHook('uponSanitizeAttribute', (node, data) => {
    if (state.keepClasses || data.attrName !== 'class') return;
    if (node.tagName !== 'CODE' || !/^language-[\w-]+$/.test(data.attrValue)) data.keepAttr = false;
  });
  initMermaid();
}

export function renderMd(text) {
  try { return DOMPurify.sanitize(marked.parse(text || ''), PURIFY_CFG); }
  catch (e) { console.error(e); return el('pre', 'block wrap', text || '').outerHTML; } // repli : texte brut échappé
}

// ---------------------------------------------------------------- diagrammes Mermaid

export function initMermaid() {
  if (!window.mermaid) return;
  mermaid.initialize({ startOnLoad: false, theme: state.darkTheme ? 'dark' : 'default', securityLevel: 'strict', suppressErrorRendering: true });
}

// Profil html + svg : mermaid place ses libellés dans des <foreignObject> contenant du HTML, que DOMPurify retire par
// défaut (HTML_INTEGRATION_POINTS) ; ce HTML reste soumis aux mêmes règles (ni script, ni on*, ni formulaire).
const SVG_CFG = {
  USE_PROFILES: { html: true, svg: true, svgFilters: true },
  ADD_TAGS: ['foreignObject'], HTML_INTEGRATION_POINTS: { foreignobject: true },
  FORBID_TAGS: PURIFY_CFG.FORBID_TAGS,
};
function sanitizeSvg(svg) {
  state.keepClasses = true;
  try { return DOMPurify.sanitize(svg, SVG_CFG); } finally { state.keepClasses = false; }
}

// Remplace les blocs ```mermaid (une fois le texte complet) par le diagramme rendu
export async function renderDiagrams(container) {
  if (!window.mermaid) return;
  for (const code of container.querySelectorAll('pre > code.language-mermaid')) {
    const div = el('div', 'mermaid');
    div.dataset.src = code.textContent;
    code.parentElement.replaceWith(div);
  }
  for (const div of container.querySelectorAll('.mermaid[data-src]:not([data-done])')) {
    div.dataset.done = '1';
    try {
      const { svg } = await mermaid.render('mmd' + Math.random().toString(36).slice(2), div.dataset.src);
      div.innerHTML = sanitizeSvg(svg);
    } catch (e) {
      div.classList.add('mermaid-error');
      div.innerHTML = '';
      div.appendChild(el('pre', 'block wrap', div.dataset.src));
      div.appendChild(el('div', 'notice', 'Diagramme non rendu : ' + (e.message || e).toString().split('\n')[0]));
    }
  }
  scroll();
}

export function rerenderDiagrams() {
  initMermaid();
  for (const div of messagesEl.querySelectorAll('.mermaid[data-src]')) { delete div.dataset.done; div.classList.remove('mermaid-error'); }
  renderDiagrams(messagesEl);
}
