// Fil de la conversation : état du tour, messages utilisateur, retour arrière, flux du CLI, historique.
import { state, messages, streamed, tools, perms, subTools } from './state.js';
import {
  el, append, clickable, rel, showRenderError,
  messagesEl, statusEl, stopEl, activityEl, welcomeEl, contextEl, sessionsEl, sessionListEl,
} from './dom.js';
import { post } from './host.js';
import { ToHost } from './protocol.js';
import { onStreamEvent, onAssistant, onSubagentResults } from './stream.js';
import { onToolResult } from './tools.js';
import { permissionCard } from './permissions.js';

export function setStatus(st) {
  const wasBusy = state.busy;
  state.busy = st === 'busy';
  // Plus de tour en cours : l'interruption n'attend plus de « result ». Sur « ready » juste après un tour, c'est le
  // « result » (posté après le status par ChatController) qui affiche « — interrompu — » et réinitialise lui-même.
  if (st === 'idle' || (st === 'ready' && !wasBusy)) state.interrupted = false;
  // Processus arrêté : plus aucun rejeu n'attribuera d'uuid aux bulles encore en attente
  if (st === 'idle') for (const b of messagesEl.querySelectorAll('.msg.user[data-pending]')) delete b.dataset.pending;
  statusEl.className = state.busy ? 'busy' : '';
  statusEl.textContent = { busy: 'Claude travaille…', starting: 'Démarrage de claude…', ready: '', idle: '' }[st] ?? '';
  stopEl.hidden = !state.busy;
  activityEl.classList.toggle('busy', state.busy);
}

export function interrupt() {
  if (!state.busy) return;
  state.interrupted = true;
  post({ type: ToHost.interrupt });
}
export function interruptedDivider() { return append(el('div', 'divider', '— interrompu —')); }
export function toolLabel(name) { return name.replace(/^mcp__/, ''); }

// ---------------------------------------------------------------- messages utilisateur

export function addUserMessage(text, contextLabel, imgs, uuid) {
  const m = el('div', 'msg user');
  if (contextLabel) m.appendChild(el('span', 'ctx', '📎 ' + contextLabel));
  if (imgs?.length) {
    const row = m.appendChild(el('div', 'msg-images'));
    for (const i of imgs) { const im = row.appendChild(el('img')); im.src = i.url; im.alt = 'Image jointe'; }
  }
  if (text) m.appendChild(document.createTextNode(text));
  if (uuid) setUserUuid(m, uuid);
  append(m);
  state.currentAssistant = null;
  return m;
}

// Message créé localement (send, consigne après refus, analyse d'exception) : il attend l'uuid que le CLI rejouera
export function addOwnMessage(text, contextLabel, imgs) {
  const m = addUserMessage(text, contextLabel, imgs);
  m.dataset.pending = '1';
  return m;
}

// ---------------------------------------------------------------- retour arrière (fichiers)

// Le CLI renvoie chaque message utilisateur avec son uuid (--replay-user-messages) :
// c'est la clé du retour arrière des fichiers à l'état précédant ce message.
function setUserUuid(bubble, uuid) {
  bubble.dataset.uuid = uuid;
  if (bubble.querySelector('.rewind')) return;
  const b = bubble.appendChild(el('button', 'rewind icon', '↶'));
  b.title = 'Remettre les fichiers dans leur état d\'avant ce message';
  b.setAttribute('aria-label', b.title);
  b.onclick = e => { e.stopPropagation(); post({ type: ToHost.rewind, uuid, dryRun: true }); };
}

function onUserReplay(m) {
  const content = m.message?.content;
  const blocks = Array.isArray(content) ? content : [{ type: 'text', text: content }];
  // On ignore le contexte IDE (<ide_selection>…), les sorties de commandes (<local-command…>) et le résumé de compactage
  const own = blocks.find(c => (c.type === 'text' && typeof c.text === 'string' && !c.text.startsWith('<')) || c.type === 'image');
  if (!own) return;
  if (own.type === 'text' && (own.text.startsWith('This session is being continued') || own.text.startsWith('[Request interrupted'))) return;
  // Le CLI rejoue les messages dans l'ordre d'envoi : les plus anciennes bulles encore en attente reçoivent l'uuid
  // (jamais une vieille bulle de l'historique, qui n'est pas marquée en attente). Les messages envoyés pendant une
  // réponse sont FUSIONNÉS par le CLI en un seul tour (un texte saisi par message, un seul uuid) : chacune des bulles
  // concernées reçoit cet uuid, même point de retour arrière, puisque c'est un seul tour pour le CLI.
  if (!m.uuid) return;
  const typed = blocks.filter(c => c.type === 'text' && typeof c.text === 'string' && !c.text.startsWith('<')).length;
  const count = Math.max(1, typed);
  const pending = [...messagesEl.querySelectorAll('.msg.user[data-pending]')].slice(0, count);
  for (const bubble of pending) { delete bubble.dataset.pending; setUserUuid(bubble, m.uuid); }
}

export function onRewindPreview(m) {
  state.rewindCard?.remove();
  const bubble = messagesEl.querySelector(`.msg.user[data-uuid="${CSS.escape(m.uuid || '')}"]`);
  const card = el('div', 'perm rewind-card');
  state.rewindCard = card;
  // Mémorisé pour la confirmation affichée en fin de conversation (rewindDone ne renvoie pas toujours la liste)
  // Texte saisi seulement : les nœuds texte directs de la bulle (ni la puce 📎 de contexte, ni le bouton ↶)
  const own = bubble ? [...bubble.childNodes].filter(n => n.nodeType === Node.TEXT_NODE).map(n => n.textContent).join('') : '';
  card.rewindInfo = { files: m.files || [], message: own.trim() };
  if (!m.ok || !m.canRewind) {
    card.appendChild(el('div', 'title', 'Retour arrière impossible'));
    card.appendChild(el('div', null, m.error || 'Aucun point de sauvegarde pour ce message (fichiers non modifiés, ou conversation reprise sans sauvegardes).'));
  } else if (!m.files?.length) {
    card.appendChild(el('div', 'title', 'Rien à annuler'));
    card.appendChild(el('div', null, 'Aucun fichier n\'a été modifié depuis ce message.'));
  } else {
    card.appendChild(el('div', 'title', `Remettre ${m.files.length} fichier(s) dans leur état d'avant ce message ?`));
    const ul = card.appendChild(el('ul'));
    for (const f of m.files) ul.appendChild(el('li', null, rel(f)));
    card.appendChild(el('div', 'outcome', `+${m.insertions ?? 0} / −${m.deletions ?? 0} lignes seront annulées. La conversation, elle, est conservée.`));
    const actions = card.appendChild(el('div', 'actions'));
    const ok = actions.appendChild(el('button', 'primary', 'Restaurer les fichiers'));
    ok.onclick = () => { ok.disabled = true; post({ type: ToHost.rewind, uuid: m.uuid }); };
  }
  const actions = card.querySelector('.actions') || card.appendChild(el('div', 'actions'));
  const close = actions.appendChild(el('button', null, 'Fermer'));
  close.onclick = () => { card.remove(); state.rewindCard = null; };
  if (bubble) bubble.after(card); else append(card);
  card.scrollIntoView({ block: 'nearest' });
}

export function onRewindDone(m) {
  const card = state.rewindCard;
  if (!card) return;
  card.classList.add('resolved');
  card.appendChild(el('div', 'outcome', m.ok ? '✓ Fichiers restaurés' : '✗ Échec : ' + (m.error || 'inconnu')));
  state.rewindCard = null;
  // Confirmation explicite en fin de conversation : la carte, elle, est plus haut dans le fil, contre le message visé
  const info = card.rewindInfo || { files: [], message: '' };
  const files = (m.files?.length ? m.files : info.files).map(rel);
  const quoted = info.message.length > 80 ? info.message.slice(0, 80) + '…' : info.message;
  const done = el('div', m.ok ? 'rewind-done' : 'error');
  if (m.ok) {
    done.appendChild(el('div', 'title', `↶ Retour arrière effectué : ${files.length} fichier(s) restauré(s)`));
    if (files.length) { const ul = done.appendChild(el('ul')); for (const f of files) ul.appendChild(el('li', null, f)); }
    if (quoted) done.appendChild(el('div', 'outcome', `État rétabli : celui d'avant le message « ${quoted} ». La conversation est conservée.`));
  } else done.textContent = '↶ Retour arrière échoué : ' + (m.error || 'erreur inconnue');
  append(done);
  // L'utilisateur a remonté le fil pour cliquer sur ↶ : le défilement automatique est suspendu, on l'amène à la confirmation
  done.scrollIntoView({ block: 'nearest', behavior: 'smooth' });
}

// ---------------------------------------------------------------- flux du CLI

// Types du protocole stream-json du CLI (pas les nôtres : voir protocol.js pour ceux de l'extension)
const cliHandlers = {
  stream_event: m => onStreamEvent(m),
  assistant: m => onAssistant(m),
  user: m => {
    const content = m.message?.content;
    if (m.parent_tool_use_id) return Array.isArray(content) && onSubagentResults(content);
    if (Array.isArray(content) && content.some(c => c.type === 'tool_result')) {
      for (const c of content) if (c.type === 'tool_result') onToolResult(c);
      return;
    }
    return onUserReplay(m);
  },
  control_request: m => {
    if (m.request?.subtype === 'can_use_tool') permissionCard(m);
  },
  control_response: m => {
    const r = m.response;
    // Refus d'un set_model / set_permission_mode… : sans ce message, le sélecteur afficherait une valeur non appliquée
    if (r?.subtype === 'error') append(el('div', 'error', 'Commande refusée par le CLI : ' + (r.error || 'erreur inconnue')));
    if (r?.response?.commands) state.commands = r.response.commands;
  },
  system: m => {
    if (m.subtype === 'init') {
      if (m.slash_commands && !state.commands.length) state.commands = m.slash_commands.map(name => ({ name, description: '' }));
    } else if (m.subtype === 'compact_boundary') append(el('div', 'divider', '— contexte compacté —'));
    else if (m.subtype === 'status') {
      if (m.status === 'compacting') { statusEl.className = 'busy'; statusEl.textContent = 'Compactage du contexte…'; }
      else if (m.status == null) setStatus(state.busy ? 'busy' : 'ready'); // fin du compactage : retour à l'état réel du tour
    }
  },
  result: m => {
    state.currentAssistant = null;
    // Le « result » d'un tour que l'utilisateur a interrompu est toujours marqué en erreur, avec un texte interne au CLI
    // (« Request interrupted… », « [ede_diagnostic] result_type=user … ») : ce n'est pas une erreur pour l'utilisateur.
    // On n'affiche que le séparateur « interrompu » ; le texte brut part dans la console pour le diagnostic.
    const isError = m.is_error || (m.subtype && m.subtype !== 'success');
    const errText = m.result || m.errors?.join('\n') || ('Erreur : ' + m.subtype);
    if (isError && state.interrupted) console.info('Résultat du tour interrompu :', errText);
    else if (isError) append(el('div', 'error', errText));
    if (state.interrupted) interruptedDivider();
    state.interrupted = false;
    const parts = [];
    if (m.duration_ms) parts.push((m.duration_ms / 1000).toFixed(1) + ' s');
    if (m.total_cost_usd) parts.push(m.total_cost_usd.toFixed(3) + ' $');
    if (parts.length) append(el('div', 'result', parts.join(' · ')));
  },
};

export function onCli(m) {
  try {
    if (Object.hasOwn(cliHandlers, m.type)) cliHandlers[m.type](m);
  } catch (e) { showRenderError(e); }
}

// L'historique peut arriver en plusieurs lots : le séparateur n'est posé qu'avec le dernier
export function renderHistory(list, last) {
  for (const m of list) {
    if (m.type === 'user') {
      const content = m.message?.content;
      if (typeof content === 'string') {
        if (content.startsWith('[Request interrupted')) interruptedDivider();
        else if (!content.startsWith('<')) addUserMessage(content, null, null, m.uuid);
        continue;
      }
      let text = [], ctx = null, imgs = [];
      for (const c of content || []) {
        if (c.type === 'tool_result') onToolResult(c);
        else if (c.type === 'image' && c.source?.type === 'base64')
          imgs.push({ url: `data:${c.source.media_type};base64,${c.source.data}` });
        else if (c.type === 'text') {
          const sel = /^<ide_selection>The user selected the lines (\d+) to (\d+) from (.+?):/.exec(c.text);
          const opened = /^<ide_opened_file>The user opened the file (.+?) in the IDE/.exec(c.text);
          if (sel) ctx = `${rel(sel[3])}:${sel[1]}-${sel[2]}`;
          else if (opened) ctx = rel(opened[1]);
          else if (!c.text.startsWith('<')) text.push(c.text);
        }
      }
      if (text.length === 1 && text[0].startsWith('[Request interrupted')) interruptedDivider();
      else if (text.length || imgs.length) addUserMessage(text.join('\n'), ctx, imgs, m.uuid);
    } else if (m.type === 'assistant') onAssistant(m);
  }
  if (last) append(el('div', 'divider', '— conversation reprise —'));
}

export function clear() {
  messagesEl.replaceChildren(welcomeEl);
  messages.clear(); streamed.clear(); tools.clear(); perms.clear(); subTools.clear();
  state.currentAssistant = null; state.currentMessageId = null; state.rewindCard = null; state.interrupted = false;
  contextEl.hidden = true;
}

// ---------------------------------------------------------------- jauge de contexte, débogueur, sessions

export function onContext(m) {
  if (!m.total) { contextEl.hidden = true; return; }
  const pct = Math.round(100 * m.used / m.total);
  contextEl.hidden = false;
  contextEl.querySelector('.fill').style.width = pct + '%';
  contextEl.querySelector('.pct').textContent = `${pct} %`;
  contextEl.classList.toggle('warn', pct >= 70 && pct < 90);
  contextEl.classList.toggle('danger', pct >= 90);
  const k = n => (n / 1000).toFixed(n < 10000 ? 1 : 0) + 'k';
  contextEl.title = `Contexte : ${k(m.used)} / ${k(m.total)} tokens\n` +
    (m.details || []).map(d => `${d.name} : ${k(d.tokens)}`).join('\n');
}

// Le débogueur de VS s'est arrêté sur une exception en dehors d'un appel d'outil
export function onDebugEvent(m) {
  const s = m.state || {}, ex = s.exception;
  // Première trame avec un fichier source : la trame de tête est souvent dans le framework
  const loc = (s.callStack || []).find(f => f.file) || s.location;
  const card = el('div', 'perm event');
  card.appendChild(el('div', 'title', '⚡ Le débogueur s\'est arrêté sur une exception'));
  const detail = card.appendChild(el('div', 'detail'));
  detail.textContent = (ex ? `${ex.type}\n${ex.value}\n` : '') + (loc ? `${rel(loc.file || '')}:${loc.line ?? '?'}  ${loc.function || ''}` : '');
  if (m.auto) {
    card.appendChild(el('div', 'outcome', 'Analyse demandée à Claude.'));
    addOwnMessage('⚡ Analyse de l\'exception');
  } else {
    const actions = card.appendChild(el('div', 'actions'));
    const b = actions.appendChild(el('button', 'primary', 'Analyser avec Claude'));
    b.onclick = () => { b.disabled = true; addOwnMessage('⚡ Analyse de l\'exception'); post({ type: ToHost.analyzeException }); };
  }
  append(card);
  if (m.auto) state.stick = true;
}

export function showSessions(list) {
  sessionListEl.innerHTML = '';
  if (!list.length) sessionListEl.appendChild(el('div', 'session', 'Aucune conversation pour ce dossier.'));
  for (const s of list) {
    const row = sessionListEl.appendChild(el('div', 'session'));
    row.appendChild(el('div', null, s.title));
    row.appendChild(el('div', 'when', s.updated));
    clickable(row, () => { sessionsEl.hidden = true; post({ type: ToHost.resume, sessionId: s.id }); });
  }
  sessionsEl.hidden = false;
}
