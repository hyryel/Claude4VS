// Références DOM et utilitaires d'affichage.
import { state } from './state.js';
import { post } from './host.js';
import { ToHost } from './protocol.js';

// Références DOM résolues une seule fois au démarrage : un élément injecté dans le fil (id en double dans du HTML
// rendu) ne peut pas se substituer à un élément de l'application, et $() n'est plus appelé après l'initialisation.
const $ = id => document.getElementById(id);
export const messagesEl = $('messages'), inputEl = $('input'), statusEl = $('status'), activityEl = $('activity'), stopEl = $('stop');
export const welcomeEl = $('welcome'), selectionEl = $('selection'), contextEl = $('context'), usageEl = $('usage'), slashEl = $('slash');
export const sessionsEl = $('sessions'), sessionListEl = $('sessionList'), attachmentsEl = $('attachments'), fileInputEl = $('fileInput');
export const modeEl = $('mode'), modelEl = $('model'), effortEl = $('effort'), cwdEl = $('cwd');
export const autoFormatEl = $('autoFormat'), autoAnalyzeEl = $('autoAnalyze');
export const attachEl = $('attach'), sendEl = $('send'), compactEl = $('compact');
export const newChatEl = $('newChat'), historyEl = $('history'), closeSessionsEl = $('closeSessions');
export const bannerEl = $('banner'), bannerTextEl = $('bannerText'), closeBannerEl = $('closeBanner');

export function el(tag, cls, text) {
  const e = document.createElement(tag);
  if (cls) e.className = cls;
  if (text != null) e.textContent = text;
  return e;
}

// Élément non natif rendu actionnable au clavier (Entrée / Espace) comme un bouton
export function clickable(node, fn) {
  node.setAttribute('role', 'button');
  node.tabIndex = 0;
  node.addEventListener('click', fn);
  node.addEventListener('keydown', e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); fn(e); } });
}

export function nearBottom() {
  return messagesEl.scrollHeight - messagesEl.scrollTop - messagesEl.clientHeight < 80;
}
export function scroll() { if (state.stick) messagesEl.scrollTop = messagesEl.scrollHeight; }

export function append(node) {
  welcomeEl.remove(); // détaché, pas détruit : clear() le remet en place
  messagesEl.appendChild(node);
  scroll();
  return node;
}

export function showRenderError(e) {
  console.error(e);
  append(el('div', 'error', 'Erreur d\'affichage : ' + ((e && e.message) || e)));
}

// Chemin relatif au dossier de travail, quel que soit le séparateur (le CLI renvoie parfois des « / »)
export function rel(path) {
  if (!path) return '';
  const norm = s => s.replace(/\//g, '\\').toLowerCase();
  const cwd = state.cwd;
  if (cwd && norm(path).startsWith(norm(cwd) + '\\')) return path.slice(cwd.length + 1);
  return path;
}

export function fileLink(path, line) {
  const a = el('a', 'file', rel(path));
  a.title = path;
  a.addEventListener('click', e => { e.stopPropagation(); post({ type: ToHost.openFile, path, line }); });
  return a;
}

export function short(v, n = 140) {
  const s = typeof v === 'string' ? v : JSON.stringify(v);
  return s.length > n ? s.slice(0, n) + '…' : s;
}
