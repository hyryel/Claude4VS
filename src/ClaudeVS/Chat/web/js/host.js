// Canal avec l'extension (WebView2) : envoi des messages et aiguillage de ceux qu'elle poste.
import { state } from './state.js';
import { FromHost } from './protocol.js';
import { el, append, showRenderError, inputEl, bannerEl, bannerTextEl, closeBannerEl } from './dom.js';
import { rerenderDiagrams } from './markdown.js';
import {
  onCli, setStatus, clear, renderHistory, onDebugEvent, onContext, onRewindPreview, onRewindDone, showSessions,
} from './conversation.js';
import { onPermissionResolved } from './permissions.js';
import { setApplied } from './toolbar.js';
import { onInsert, onSelection, onSettings, onFiles } from './composer.js';

const host = window.chrome && window.chrome.webview;

export function post(msg) {
  return host ? host.postMessage(msg) : console.log('→ host', msg);
}

function onTheme(m) {
  for (const [k, v] of Object.entries(m.vars || {})) document.documentElement.style.setProperty(k, v);
  document.documentElement.style.colorScheme = m.dark ? 'dark' : 'light';
  document.documentElement.classList.toggle('light', !m.dark); // seconde palette sémantique (style.css)
  if (state.darkTheme !== !!m.dark) { state.darkTheme = !!m.dark; rerenderDiagrams(); }
}

// Bandeau d'annonce hors du fil : survit au vidage de la conversation, reste visible quel que soit le défilement.
// Affiché pendant que Claude travaille (« passera dans … à la fin de sa réponse »), il reste jusqu'à être remplacé ;
// sinon il se ferme seul au bout de 5 s : une barre décroît en bas du bandeau (animation CSS « auto »), et c'est la
// fin de l'animation qui le ferme. Souris dessus : l'animation est en pause (style.css), elle reprend où elle en était.
function hideBanner() { bannerEl.hidden = true; bannerEl.classList.remove('auto'); }
function onBanner(m) {
  bannerEl.classList.remove('auto');
  bannerTextEl.textContent = m.text || '';
  bannerEl.hidden = !m.text;
  if (m.text && !m.sticky && !state.busy) { // sticky : l'annonce demande une action, pas de fermeture automatique
    void bannerEl.offsetWidth; // relance l'animation si un bandeau à décompte en remplace un autre
    bannerEl.classList.add('auto');
  }
}

// Les fonctions importées ne sont appelées qu'à la réception d'un message (jamais pendant l'évaluation du module)
const handlers = {
  [FromHost.cli]: m => onCli(m.msg),
  [FromHost.status]: m => setStatus(m.state),
  [FromHost.error]: m => append(el('div', 'error', m.text)),
  [FromHost.permissionResolved]: m => onPermissionResolved(m.requestId, m.behavior),
  // Nouvelle conversation, reprise ou bascule de solution : l'annonce précédente n'a plus d'objet. Lors d'une bascule,
  // le C# poste « cleared » AVANT le nouveau bandeau, qui s'affiche donc bien ensuite.
  [FromHost.cleared]: () => { clear(); hideBanner(); },
  [FromHost.history]: m => renderHistory(m.messages || [], m.last ?? true),
  [FromHost.insert]: m => onInsert(m),
  [FromHost.selection]: m => onSelection(m),
  [FromHost.focus]: () => inputEl.focus(),
  [FromHost.notice]: m => append(el('div', 'notice', m.text)),
  [FromHost.debugEvent]: m => onDebugEvent(m),
  [FromHost.context]: m => onContext(m),
  [FromHost.rewindPreview]: m => onRewindPreview(m),
  [FromHost.rewindDone]: m => onRewindDone(m),
  [FromHost.settings]: m => onSettings(m),
  [FromHost.sessions]: m => showSessions(m.sessions || []),
  [FromHost.files]: m => onFiles(m),
  [FromHost.theme]: m => onTheme(m),
  [FromHost.banner]: m => onBanner(m),
  [FromHost.applied]: m => setApplied(m),
};

// Appelé une fois par main.js, avant l'envoi de « ready »
export function listen() {
  closeBannerEl.onclick = hideBanner;
  bannerEl.addEventListener('animationend', e => { if (e.animationName === 'banner-countdown') hideBanner(); });
  host?.addEventListener('message', e => {
    const m = e.data;
    try {
      if (Object.hasOwn(handlers, m.type)) handlers[m.type](m);
    } catch (err) { showRenderError(err); }
  });
}
