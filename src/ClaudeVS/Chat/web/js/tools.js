// Appels d'outils : résumé, détails, élément repliable et résultat.
import { tools } from './state.js';
import { el, clickable, scroll, rel, short, fileLink } from './dom.js';
import { toolLabel } from './conversation.js';

export function toolSummary(name, input) {
  if (!input) return '';
  switch (name) {
    case 'Bash': case 'PowerShell': return input.command;
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
  else if ((name === 'Agent' || name === 'Task') && input.prompt)
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

export function toolElement(id, name, input, container) {
  let t = tools.get(id);
  if (!t) {
    t = el('div', 'tool running');
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
  }
  if (input && !t.dataset.input) {
    t.dataset.input = '1';
    t.querySelector('.summary').textContent = toolSummary(name, input);
    t.querySelector('.tool-body').prepend(toolDetails(name, input));
    if (name === 'TodoWrite') setOpen(t, true);
  }
  return t;
}

export function onToolResult(r) {
  const t = tools.get(r.tool_use_id);
  if (!t) return;
  t.classList.remove('running');
  t.classList.add(r.is_error ? 'error' : 'ok');
  const sub = t.querySelector('.sub');
  t.querySelector('.state').textContent = (sub ? sub.children.length + ' outils ' : '') + (r.is_error ? '✗' : '✓');
  if (sub && !r.is_error) setOpen(t, false); // sous-agent terminé : on replie
  let text = r.content;
  if (Array.isArray(text)) text = text.map(c => c.type === 'text' ? c.text : `[${c.type}]`).join('\n');
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
