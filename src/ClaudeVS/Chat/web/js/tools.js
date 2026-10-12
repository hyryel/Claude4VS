// Appels d'outils : résumé, détails, élément repliable et résultat.
import { tools } from './state.js';
import { el, clickable, scroll, rel, short, fileLink } from './dom.js';
import { renderMd } from './markdown.js';
import { toolLabel, updateAgents } from './conversation.js';

const isAgent = name => name === 'Task' || name === 'Agent';

export function toolSummary(name, input) {
  if (!input) return '';
  switch (name) {
    case 'Bash': case 'PowerShell': return input.description || input.command;
    case 'Read': case 'Edit': case 'MultiEdit': case 'Write': case 'NotebookEdit': return rel(input.file_path || input.notebook_path);
    case 'Grep': return input.pattern + (input.path ? '  ' + rel(input.path) : '');
    case 'Glob': return input.pattern;
    case 'WebFetch': return input.url;
    case 'WebSearch': return input.query;
    case 'Task': case 'Agent': return input.description;
    case 'TodoWrite': return (input.todos || []).length + ' tâches';
    default: return Object.keys(input).length ? short(input) : '';
  }
}

export function diffPre(oldStr, newStr) {
  const pre = el('pre', 'block diff');
  for (const l of (oldStr || '').split('\n')) pre.appendChild(el('span', 'del', '- ' + l));
  for (const l of (newStr || '').split('\n')) pre.appendChild(el('span', 'add', '+ ' + l));
  return pre;
}

export function toolDetails(name, input) {
  const frag = document.createDocumentFragment();
  if (!input) return frag;
  if (input.file_path) frag.appendChild(fileLink(input.file_path));
  if (name === 'Edit') frag.appendChild(diffPre(input.old_string, input.new_string));
  else if (name === 'MultiEdit') for (const e of input.edits || []) frag.appendChild(diffPre(e.old_string, e.new_string));
  else if (name === 'Write') frag.appendChild(el('pre', 'block', input.content));
  else if (name === 'Bash' || name === 'PowerShell') frag.appendChild(el('pre', 'block', input.command));
  else if (isAgent(name) && input.prompt)
    frag.appendChild(el('pre', 'block wrap', (input.subagent_type ? `[${input.subagent_type}] ` : '') + input.prompt));
  else if (name === 'TodoWrite') {
    const ul = el('ul', 'todos');
    for (const t of input.todos || [])
      ul.appendChild(el('li', t.status, (t.status === 'completed' ? '☑ ' : t.status === 'in_progress' ? '▶ ' : '☐ ') + t.content));
    frag.appendChild(ul);
  } else if (!input.file_path && Object.keys(input).length) frag.appendChild(el('pre', 'block', JSON.stringify(input, null, 2)));
  return frag;
}

// Déplie / replie le corps d'un outil (et tient l'état ARIA de son en-tête à jour)
export function setOpen(t, open) {
  t.classList.toggle('open', open);
  t.querySelector('.tool-head').setAttribute('aria-expanded', open);
}

// Carte de sous-agent : titre = description de l'appel, type d'agent en résumé ; corps = « Règles » (le prompt) et
// actions, deux boîtes repliables. Les actions repliées ne montrent que la dernière (voir onSubagentAssistant).
function agentDetails(t, input) {
  t.querySelector('.name').textContent = input.description || toolLabel(t.dataset.tool);
  t.querySelector('.summary').textContent = input.subagent_type || '';
  const frag = document.createDocumentFragment();
  if (input.prompt) {
    const rules = frag.appendChild(el('details', 'agent-rules'));
    rules.appendChild(el('summary', null, 'Règles'));
    rules.appendChild(el('pre', 'block wrap', input.prompt));
  }
  const actions = frag.appendChild(el('div', 'agent-actions'));
  actions.hidden = true; // affichée à la première action
  const head = actions.appendChild(el('div', 'agent-actions-head', 'Actions'));
  head.setAttribute('aria-expanded', 'false');
  clickable(head, () => {
    const open = !actions.classList.contains('open');
    actions.classList.toggle('open', open);
    head.setAttribute('aria-expanded', open);
    const list = actions.querySelector('.sub');
    if (open) list.scrollTop = list.scrollHeight;
  });
  actions.appendChild(el('div', 'sub'));
  t.querySelector('.tool-body').prepend(frag);
  setOpen(t, true);
}

export function toolElement(id, name, input, container) {
  let t = tools.get(id);
  if (!t) {
    t = el('div', isAgent(name) ? 'tool agent running' : 'tool running');
    t.dataset.tool = name;
    const head = t.appendChild(el('div', 'tool-head'));
    head.appendChild(el('span', 'name', toolLabel(name)));
    head.appendChild(el('span', 'summary'));
    head.appendChild(el('span', 'state'));
    head.setAttribute('aria-expanded', 'false');
    clickable(head, () => setOpen(t, !t.classList.contains('open')));
    t.appendChild(el('div', 'tool-body'));
    container.appendChild(t);
    tools.set(id, t);
    scroll();
    if (isAgent(name)) updateAgents();
  }
  if (input && !t.dataset.input) {
    t.dataset.input = '1';
    if (isAgent(name)) agentDetails(t, input);
    else {
      t.querySelector('.summary').textContent = toolSummary(name, input);
      t.querySelector('.tool-body').prepend(toolDetails(name, input));
    }
    if (name === 'TodoWrite') setOpen(t, true);
  }
  return t;
}

// Fin d'un sous-agent (résultat de l'outil, ou notification d'un agent lancé en arrière-plan) : la carte se replie sur
// son titre, la réponse de l'agent va dans une boîte « Résultat » repliée. Idempotent : la notification peut arriver
// deux fois (message system puis rejeu utilisateur), la seconde seulement avec le résultat.
export function onAgentDone(toolUseId, failed, result) {
  const t = tools.get(toolUseId);
  if (!t) return;
  if (t.classList.contains('running')) {
    t.classList.remove('running', 'background');
    t.classList.add(failed ? 'error' : 'ok');
    const count = t.querySelector('.sub')?.children.length;
    t.querySelector('.state').textContent = (count ? count + ' outils ' : '') + (failed ? '✗' : '✓');
    if (!failed) setOpen(t, false);
    updateAgents();
  }
  if (result && !t.querySelector('.agent-result')) {
    const box = t.querySelector('.tool-body').appendChild(el('details', 'agent-result'));
    box.appendChild(el('summary', null, 'Résultat'));
    box.appendChild(el('div', 'md')).innerHTML = renderMd(result);
  }
}

// Agents qui ne finiront jamais : processus du CLI arrêté, ou conversation reprise (ses agents tournaient dans l'ancien)
export function stopAgents() {
  for (const t of document.querySelectorAll('.tool.agent.running')) {
    t.classList.remove('running', 'background');
    t.classList.add('error');
    t.querySelector('.state').textContent = 'arrêté';
  }
  updateAgents();
}

export function onToolResult(r) {
  const t = tools.get(r.tool_use_id);
  if (!t) return;
  let text = r.content;
  if (Array.isArray(text)) text = text.map(c => c.type === 'text' ? c.text : `[${c.type}]`).join('\n');
  if (t.classList.contains('agent')) {
    // Agent lancé en arrière-plan : ce résultat n'est qu'un accusé de lancement destiné au modèle (identifiant interne,
    // consignes). La carte reste « en cours » jusqu'à la notification de fin (onTaskNotification).
    if (!r.is_error && typeof text === 'string' && text.startsWith('Async agent launched')) {
      t.classList.add('background');
      return;
    }
    return onAgentDone(r.tool_use_id, r.is_error, text);
  }
  t.classList.remove('running');
  t.classList.add(r.is_error ? 'error' : 'ok');
  t.querySelector('.state').textContent = r.is_error ? '✗' : '✓';
  if (text && t.dataset.tool !== 'TodoWrite') {
    const pre = el('pre', 'block wrap', text.length > 4000 ? text.slice(0, 4000) + '\n…' : text);
    t.querySelector('.tool-body').appendChild(pre);
  }
  // Erreur : la carte reste repliée ; une ligne de résumé (1re ligne utile du message) reste visible sous l'en-tête,
  // le détail complet est dans le corps, à déplier au clic.
  if (r.is_error && text) {
    const first = text.split('\n').map(s => s.trim()).find(Boolean) || '';
    const summary = first.replace(/^(Error|Erreur)\s*:\s*/i, '').replace(/\s*:\s*$/, ''); // « …applied: » annonçait la liste repliée
    t.querySelector('.tool-head').after(el('div', 'tool-error-summary', summary.length > 160 ? summary.slice(0, 160) + '…' : summary));
  }
}
