// Messages assistant : conteneurs, blocs texte / réflexion, flux stream_event et activité des sous-agents.
import { state, messages, streamed, tools, subTools } from './state.js';
import { el, append, scroll } from './dom.js';
import { renderMd, renderDiagrams, addCopyButtons } from './markdown.js';
import { toolElement, toolSummary } from './tools.js';
import { toolLabel } from './conversation.js';
import { setActualModel } from './toolbar.js';

// Intervalle minimal entre deux rendus markdown d'un même bloc pendant le streaming : chaque rendu reparse tout le
// texte reçu, son coût croît avec la longueur de la réponse.
const RENDER_INTERVAL_MS = 100;

export function assistantContainer(id) {
  let entry = messages.get(id);
  if (!entry) {
    if (!state.currentAssistant) state.currentAssistant = append(el('div', 'msg assistant'));
    entry = { el: state.currentAssistant, blocks: new Map() };
    messages.set(id, entry);
  }
  return entry;
}

export function textBlock(container) {
  const div = container.appendChild(el('div', 'md'));
  const block = { type: 'text', el: div, text: '', raf: 0, timer: 0, last: 0 };
  const paint = () => { block.raf = 0; block.last = performance.now(); div.innerHTML = renderMd(block.text); scroll(); };
  // Les deltas sont coalescés : au plus un rendu par RENDER_INTERVAL_MS, calé sur une frame.
  // finalize annule le rendu différé pour qu'il n'écrase pas les diagrammes.
  block.render = () => {
    if (block.raf || block.timer) return;
    const wait = RENDER_INTERVAL_MS - (performance.now() - block.last);
    if (wait <= 0) block.raf = requestAnimationFrame(paint);
    else block.timer = setTimeout(() => { block.timer = 0; block.raf = requestAnimationFrame(paint); }, wait);
  };
  // Texte complet : rendu immédiat puis diagrammes
  block.finalize = () => {
    clearTimeout(block.timer); block.timer = 0;
    cancelAnimationFrame(block.raf); block.raf = 0;
    div.innerHTML = renderMd(block.text);
    renderDiagrams(div); // remplace les blocs mermaid avant son premier await
    addCopyButtons(div);
  };
  return block;
}

export function thinkingBlock(container) {
  const d = container.appendChild(el('details', 'thinking'));
  d.appendChild(el('summary', null, 'Réflexion'));
  const content = d.appendChild(el('div', 'content'));
  const block = { type: 'thinking', el: d, text: '' };
  block.render = () => { content.textContent = block.text; };
  return block;
}

export function onStreamEvent(m) {
  if (m.parent_tool_use_id) return; // flux interne d'un sous-agent
  const ev = m.event;
  switch (ev.type) {
    case 'message_start':
      assistantContainer(ev.message.id);
      state.currentMessageId = ev.message.id;
      break;
    case 'content_block_start': {
      const id = state.currentMessageId;
      const entry = assistantContainer(id);
      const cb = ev.content_block;
      if (cb.type === 'text') { entry.blocks.set(ev.index, textBlock(entry.el)); streamed.add(id); }
      else if (cb.type === 'thinking') { entry.blocks.set(ev.index, thinkingBlock(entry.el)); streamed.add(id); }
      else if (cb.type === 'tool_use') toolElement(cb.id, cb.name, null, entry.el);
      break;
    }
    case 'content_block_delta': {
      const block = messages.get(state.currentMessageId)?.blocks.get(ev.index);
      if (!block) break;
      if (ev.delta.type === 'text_delta') { block.text += ev.delta.text; block.render(); }
      else if (ev.delta.type === 'thinking_delta') { block.text += ev.delta.thinking; block.render(); }
      break;
    }
    case 'content_block_stop': {
      const block = messages.get(state.currentMessageId)?.blocks.get(ev.index);
      if (block?.type === 'thinking' && !block.text.trim()) block.el.remove();
      else if (block?.type === 'text') block.finalize();
      break;
    }
  }
}

// ---------------------------------------------------------------- sous-agents

// Activité d'un sous-agent : liste de ses outils dans la boîte « Actions » de la carte d'agent (créée par agentDetails).
// Repliée, la boîte ne montre que la dernière ligne (CSS) ; dépliée, la liste défile et suit la dernière action.
function onSubagentAssistant(m) {
  const actions = tools.get(m.parent_tool_use_id)?.querySelector('.agent-actions');
  if (!actions) return;
  const list = actions.querySelector('.sub');
  const added = (m.message.content || []).filter(c => c.type === 'tool_use' && !subTools.has(c.id));
  if (!added.length) return;
  for (const c of added) {
    const line = list.appendChild(el('div', 'sub-line running'));
    line.appendChild(el('span', 'name', toolLabel(c.name)));
    line.appendChild(el('span', 'summary', ' ' + toolSummary(c.name, c.input)));
    subTools.set(c.id, line);
  }
  actions.hidden = false;
  actions.querySelector('.agent-actions-head').textContent = `Actions (${list.children.length})`;
  actions.closest('.tool').querySelector('.state').textContent = list.children.length + ' outils ';
  if (actions.classList.contains('open')) list.scrollTop = list.scrollHeight;
  scroll();
}

export function onSubagentResults(content) {
  for (const c of content) {
    const line = c.type === 'tool_result' && subTools.get(c.tool_use_id);
    if (!line) continue;
    line.classList.remove('running');
    line.classList.add(c.is_error ? 'error' : 'ok');
    // Refus ou échec : la raison telle que l'a écrite celui qui a décidé (utilisateur, règle, hook de permission…),
    // première ligne utile sur la ligne d'action, texte complet en infobulle
    if (!c.is_error) continue;
    const text = Array.isArray(c.content) ? c.content.map(x => x.type === 'text' ? x.text : '').join('\n') : c.content || '';
    const first = text.split('\n').map(s => s.trim()).find(Boolean);
    if (!first) continue;
    line.appendChild(el('span', 'sub-error', ' — ' + first.replace(/^(Error|Erreur)\s*:\s*/i, '')));
    line.title = text;
  }
}

export function onAssistant(m) {
  if (m.parent_tool_use_id) return onSubagentAssistant(m);
  const msg = m.message;
  setActualModel(msg.model);
  const entry = assistantContainer(msg.id);
  for (const c of msg.content || []) {
    if (c.type === 'tool_use') toolElement(c.id, c.name, c.input, entry.el);
    else if (!streamed.has(msg.id)) {
      // Historique ou message reçu sans streaming
      if (c.type === 'text' && c.text) { const b = textBlock(entry.el); b.text = c.text; b.finalize(); }
      else if (c.type === 'thinking' && c.thinking) { const b = thinkingBlock(entry.el); b.text = c.thinking; b.render(); }
    }
  }
}
