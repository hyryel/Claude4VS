// Canal avec l'extension (WebView2) : envoi des messages et aiguillage de ceux qu'elle poste.
import { state } from './state.js';
import { FromHost } from './protocol.js';
import { el, append, showRenderError, inputEl } from './dom.js';
import { rerenderDiagrams } from './markdown.js';
import {
  onCli, setStatus, clear, renderHistory, onDebugEvent, onContext, onRewindPreview, onRewindDone, showSessions,
} from './conversation.js';
import { onPermissionResolved } from './permissions.js';
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

// Les fonctions importées ne sont appelées qu'à la réception d'un message (jamais pendant l'évaluation du module)
const handlers = {
  [FromHost.cli]: m => onCli(m.msg),
  [FromHost.status]: m => setStatus(m.state),
  [FromHost.error]: m => append(el('div', 'error', m.text)),
  [FromHost.permissionResolved]: m => onPermissionResolved(m.requestId, m.behavior),
  [FromHost.cleared]: () => clear(),
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
};

// Appelé une fois par main.js, avant l'envoi de « ready »
export function listen() {
  host?.addEventListener('message', e => {
    const m = e.data;
    try {
      if (Object.hasOwn(handlers, m.type)) handlers[m.type](m);
    } catch (err) { showRenderError(err); }
  });
}
