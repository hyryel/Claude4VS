// Interface du panneau Claude : rend le flux stream-json du CLI relayé par l'extension (ChatController).
(() => {
  'use strict';

  // Références DOM résolues une seule fois au démarrage : un élément injecté dans le fil (id en double dans du HTML
  // rendu) ne peut pas se substituer à un élément de l'application, et $() n'est plus appelé après l'initialisation.
  const $ = id => document.getElementById(id);
  const messagesEl = $('messages'), inputEl = $('input'), statusEl = $('status'), activityEl = $('activity'), stopEl = $('stop');
  const welcomeEl = $('welcome'), selectionEl = $('selection'), contextEl = $('context'), slashEl = $('slash');
  const sessionsEl = $('sessions'), sessionListEl = $('sessionList'), attachmentsEl = $('attachments'), fileInputEl = $('fileInput');
  const modeEl = $('mode'), modelEl = $('model'), effortEl = $('effort'), cwdEl = $('cwd');
  const autoFormatEl = $('autoFormat'), autoAnalyzeEl = $('autoAnalyze');
  const host = window.chrome && window.chrome.webview;
  const post = msg => host ? host.postMessage(msg) : console.log('→ host', msg);

  marked.setOptions({ gfm: true, breaks: false });
  // Le HTML brut que marked laisse passer est cloisonné : ni id/name (clobbering), ni style (recouvrement du panneau),
  // ni éléments de formulaire, ni data-* ; « class » n'est gardé que sur <code class="language-…"> (blocs mermaid).
  const PURIFY_CFG = {
    ADD_ATTR: ['target'], ALLOW_DATA_ATTR: false,
    FORBID_ATTR: ['id', 'name', 'style'],
    FORBID_TAGS: ['form', 'input', 'button', 'select', 'textarea', 'label', 'style'],
  };
  let keepClasses = false; // vrai le temps de nettoyer un SVG mermaid, dont le style repose sur ses classes
  DOMPurify.addHook('uponSanitizeAttribute', (node, data) => {
    if (keepClasses || data.attrName !== 'class') return;
    if (node.tagName !== 'CODE' || !/^language-[\w-]+$/.test(data.attrValue)) data.keepAttr = false;
  });
  function renderMd(text) {
    try { return DOMPurify.sanitize(marked.parse(text || ''), PURIFY_CFG); }
    catch (e) { console.error(e); return el('pre', 'block wrap', text || '').outerHTML; } // repli : texte brut échappé
  }

  // ---------------------------------------------------------------- diagrammes Mermaid
  let darkTheme = true;
  function initMermaid() {
    if (!window.mermaid) return;
    mermaid.initialize({ startOnLoad: false, theme: darkTheme ? 'dark' : 'default', securityLevel: 'strict', suppressErrorRendering: true });
  }
  initMermaid();

  // Profil html + svg : mermaid place ses libellés dans des <foreignObject> contenant du HTML, que DOMPurify retire par
  // défaut (HTML_INTEGRATION_POINTS) ; ce HTML reste soumis aux mêmes règles (ni script, ni on*, ni formulaire).
  const SVG_CFG = {
    USE_PROFILES: { html: true, svg: true, svgFilters: true },
    ADD_TAGS: ['foreignObject'], HTML_INTEGRATION_POINTS: { foreignobject: true },
    FORBID_TAGS: PURIFY_CFG.FORBID_TAGS,
  };
  function sanitizeSvg(svg) {
    keepClasses = true;
    try { return DOMPurify.sanitize(svg, SVG_CFG); } finally { keepClasses = false; }
  }

  // Remplace les blocs ```mermaid (une fois le texte complet) par le diagramme rendu
  async function renderDiagrams(container) {
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
  function rerenderDiagrams() {
    initMermaid();
    for (const div of messagesEl.querySelectorAll('.mermaid[data-src]')) { delete div.dataset.done; div.classList.remove('mermaid-error'); }
    renderDiagrams(messagesEl);
  }

  let cwd = '';
  let commands = [];          // commandes / renvoyées par initialize
  let busy = false;
  let interrupted = false;     // le prochain « result » vient d'une interruption volontaire
  let currentAssistant = null; // conteneur du tour en cours
  let currentMessageId = null; // id du message API en cours de streaming
  const messages = new Map();  // id message API → { el, blocks: Map(index → bloc) }
  const streamed = new Set();  // messages dont le texte est arrivé en streaming
  const tools = new Map();     // tool_use_id → élément
  const perms = new Map();     // request_id → carte de permission

  // ---------------------------------------------------------------- utilitaires

  function el(tag, cls, text) {
    const e = document.createElement(tag);
    if (cls) e.className = cls;
    if (text != null) e.textContent = text;
    return e;
  }

  // Élément non natif rendu actionnable au clavier (Entrée / Espace) comme un bouton
  function clickable(node, fn) {
    node.setAttribute('role', 'button');
    node.tabIndex = 0;
    node.addEventListener('click', fn);
    node.addEventListener('keydown', e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); fn(e); } });
  }

  function nearBottom() {
    return messagesEl.scrollHeight - messagesEl.scrollTop - messagesEl.clientHeight < 80;
  }
  let stick = true;
  messagesEl.addEventListener('scroll', () => { stick = nearBottom(); });
  function scroll() { if (stick) messagesEl.scrollTop = messagesEl.scrollHeight; }

  function append(node) {
    welcomeEl.remove(); // détaché, pas détruit : clear() le remet en place
    messagesEl.appendChild(node);
    scroll();
    return node;
  }

  function showRenderError(e) {
    console.error(e);
    append(el('div', 'error', 'Erreur d\'affichage : ' + ((e && e.message) || e)));
  }

  // Chemin relatif au dossier de travail, quel que soit le séparateur (le CLI renvoie parfois des « / »)
  function rel(path) {
    if (!path) return '';
    const norm = s => s.replace(/\//g, '\\').toLowerCase();
    if (cwd && norm(path).startsWith(norm(cwd) + '\\')) return path.slice(cwd.length + 1);
    return path;
  }

  function fileLink(path, line) {
    const a = el('a', 'file', rel(path));
    a.title = path;
    a.addEventListener('click', e => { e.stopPropagation(); post({ type: 'openFile', path, line }); });
    return a;
  }

  function short(v, n = 140) {
    const s = typeof v === 'string' ? v : JSON.stringify(v);
    return s.length > n ? s.slice(0, n) + '…' : s;
  }

  function setStatus(state) {
    const wasBusy = busy;
    busy = state === 'busy';
    // Plus de tour en cours : l'interruption n'attend plus de « result ». Sur « ready » juste après un tour, c'est le
    // « result » (posté après le status par ChatController) qui affiche « — interrompu — » et réinitialise lui-même.
    if (state === 'idle' || (state === 'ready' && !wasBusy)) interrupted = false;
    // Processus arrêté : plus aucun rejeu n'attribuera d'uuid aux bulles encore en attente
    if (state === 'idle') for (const b of messagesEl.querySelectorAll('.msg.user[data-pending]')) delete b.dataset.pending;
    statusEl.className = busy ? 'busy' : '';
    statusEl.textContent = { busy: 'Claude travaille…', starting: 'Démarrage de claude…', ready: '', idle: '' }[state] ?? '';
    stopEl.hidden = !busy;
    activityEl.classList.toggle('busy', busy);
  }

  function interrupt() {
    if (!busy) return;
    interrupted = true;
    post({ type: 'interrupt' });
  }
  const interruptedDivider = () => append(el('div', 'divider', '— interrompu —'));
  const toolLabel = name => name.replace(/^mcp__/, '');

  // ---------------------------------------------------------------- messages utilisateur

  function addUserMessage(text, contextLabel, imgs, uuid) {
    const m = el('div', 'msg user');
    if (contextLabel) m.appendChild(el('span', 'ctx', '📎 ' + contextLabel));
    if (imgs?.length) {
      const row = m.appendChild(el('div', 'msg-images'));
      for (const i of imgs) { const im = row.appendChild(el('img')); im.src = i.url; im.alt = 'Image jointe'; }
    }
    if (text) m.appendChild(document.createTextNode(text));
    if (uuid) setUserUuid(m, uuid);
    append(m);
    currentAssistant = null;
    return m;
  }

  // Message créé localement (send, consigne après refus, analyse d'exception) : il attend l'uuid que le CLI rejouera
  function addOwnMessage(text, contextLabel, imgs) {
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
    b.onclick = e => { e.stopPropagation(); post({ type: 'rewind', uuid, dryRun: true }); };
  }

  function onUserReplay(m) {
    const content = m.message?.content;
    const blocks = Array.isArray(content) ? content : [{ type: 'text', text: content }];
    // On ignore le contexte IDE (<ide_selection>…), les sorties de commandes (<local-command…>) et le résumé de compactage
    const own = blocks.find(c => (c.type === 'text' && typeof c.text === 'string' && !c.text.startsWith('<')) || c.type === 'image');
    if (!own) return;
    if (own.type === 'text' && (own.text.startsWith('This session is being continued') || own.text.startsWith('[Request interrupted'))) return;
    // Le CLI rejoue les messages dans l'ordre d'envoi : la plus ancienne bulle encore en attente reçoit l'uuid
    // (jamais une vieille bulle de l'historique, qui n'est pas marquée en attente)
    const bubble = messagesEl.querySelector('.msg.user[data-pending]');
    if (bubble && m.uuid) { delete bubble.dataset.pending; setUserUuid(bubble, m.uuid); }
  }

  let rewindCard = null;
  function onRewindPreview(m) {
    rewindCard?.remove();
    const bubble = messagesEl.querySelector(`.msg.user[data-uuid="${CSS.escape(m.uuid || '')}"]`);
    const card = el('div', 'perm rewind-card');
    rewindCard = card;
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
      ok.onclick = () => { ok.disabled = true; post({ type: 'rewind', uuid: m.uuid }); };
    }
    const actions = card.querySelector('.actions') || card.appendChild(el('div', 'actions'));
    const close = actions.appendChild(el('button', null, 'Fermer'));
    close.onclick = () => { card.remove(); rewindCard = null; };
    if (bubble) bubble.after(card); else append(card);
    card.scrollIntoView({ block: 'nearest' });
  }

  function onRewindDone(m) {
    if (!rewindCard) return;
    rewindCard.classList.add('resolved');
    rewindCard.appendChild(el('div', 'outcome', m.ok ? '✓ Fichiers restaurés' : '✗ Échec : ' + (m.error || 'inconnu')));
    rewindCard = null;
  }

  // ---------------------------------------------------------------- messages assistant

  function assistantContainer(id) {
    let entry = messages.get(id);
    if (!entry) {
      if (!currentAssistant) currentAssistant = append(el('div', 'msg assistant'));
      entry = { el: currentAssistant, blocks: new Map() };
      messages.set(id, entry);
    }
    return entry;
  }

  function textBlock(container) {
    const div = container.appendChild(el('div', 'md'));
    const block = { type: 'text', el: div, text: '', raf: 0 };
    // Les deltas sont coalescés par frame ; finalize annule le rendu différé pour qu'il n'écrase pas les diagrammes
    block.render = () => {
      if (block.raf) return;
      block.raf = requestAnimationFrame(() => { block.raf = 0; div.innerHTML = renderMd(block.text); scroll(); });
    };
    // Texte complet : rendu immédiat puis diagrammes
    block.finalize = () => {
      cancelAnimationFrame(block.raf); block.raf = 0;
      div.innerHTML = renderMd(block.text);
      renderDiagrams(div);
    };
    return block;
  }

  function thinkingBlock(container) {
    const d = container.appendChild(el('details', 'thinking'));
    d.appendChild(el('summary', null, 'Réflexion'));
    const content = d.appendChild(el('div', 'content'));
    const block = { type: 'thinking', el: d, text: '' };
    block.render = () => { content.textContent = block.text; };
    return block;
  }

  function onStreamEvent(m) {
    if (m.parent_tool_use_id) return; // flux interne d'un sous-agent
    const ev = m.event;
    switch (ev.type) {
      case 'message_start':
        assistantContainer(ev.message.id);
        currentMessageId = ev.message.id;
        break;
      case 'content_block_start': {
        const entry = assistantContainer(currentMessageId);
        const cb = ev.content_block;
        if (cb.type === 'text') { entry.blocks.set(ev.index, textBlock(entry.el)); streamed.add(currentMessageId); }
        else if (cb.type === 'thinking') { entry.blocks.set(ev.index, thinkingBlock(entry.el)); streamed.add(currentMessageId); }
        else if (cb.type === 'tool_use') toolElement(cb.id, cb.name, null, entry.el);
        break;
      }
      case 'content_block_delta': {
        const block = messages.get(currentMessageId)?.blocks.get(ev.index);
        if (!block) break;
        if (ev.delta.type === 'text_delta') { block.text += ev.delta.text; block.render(); }
        else if (ev.delta.type === 'thinking_delta') { block.text += ev.delta.thinking; block.render(); }
        break;
      }
      case 'content_block_stop': {
        const block = messages.get(currentMessageId)?.blocks.get(ev.index);
        if (block?.type === 'thinking' && !block.text.trim()) block.el.remove();
        else if (block?.type === 'text') block.finalize();
        break;
      }
    }
  }

  // Activité d'un sous-agent : liste de ses outils sous l'outil Task/Agent parent
  const subTools = new Map(); // tool_use_id du sous-agent → ligne
  function onSubagentAssistant(m) {
    const parent = tools.get(m.parent_tool_use_id);
    if (!parent) return;
    let list = parent.querySelector('.sub');
    if (!list) {
      list = el('div', 'sub');
      parent.querySelector('.tool-body').prepend(list);
      setOpen(parent, true);
    }
    for (const c of m.message.content || []) {
      if (c.type !== 'tool_use' || subTools.has(c.id)) continue;
      const line = list.appendChild(el('div', 'sub-line running'));
      line.appendChild(el('span', 'name', toolLabel(c.name)));
      line.appendChild(el('span', 'summary', ' ' + toolSummary(c.name, c.input)));
      subTools.set(c.id, line);
      parent.querySelector('.state').textContent = list.children.length + ' outils ';
    }
    scroll();
  }

  function onSubagentResults(content) {
    for (const c of content) {
      const line = c.type === 'tool_result' && subTools.get(c.tool_use_id);
      if (!line) continue;
      line.classList.remove('running');
      line.classList.add(c.is_error ? 'error' : 'ok');
    }
  }

  function onAssistant(m) {
    if (m.parent_tool_use_id) return onSubagentAssistant(m);
    const msg = m.message;
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

  // ---------------------------------------------------------------- outils

  function toolSummary(name, input) {
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

  function diffPre(oldStr, newStr) {
    const pre = el('pre', 'block diff');
    for (const l of (oldStr || '').split('\n')) pre.appendChild(el('span', 'del', '- ' + l));
    for (const l of (newStr || '').split('\n')) pre.appendChild(el('span', 'add', '+ ' + l));
    return pre;
  }

  function toolDetails(name, input) {
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
  function setOpen(t, open) {
    t.classList.toggle('open', open);
    t.querySelector('.tool-head').setAttribute('aria-expanded', open);
  }

  function toolElement(id, name, input, container) {
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

  function onToolResult(r) {
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
    if (r.is_error) setOpen(t, true);
  }

  // ---------------------------------------------------------------- permissions

  // La carte se place juste sous l'outil concerné (sinon en fin de fil)
  function placeCard(card, toolUseId) {
    const tool = tools.get(toolUseId);
    if (!tool) return append(card);
    tool.after(card);
    scroll();
    return card;
  }

  function permissionCard(m) {
    const req = m.request, id = m.request_id, input = req.input || {};
    const card = el('div', 'perm');
    perms.set(id, card);
    const place = node => placeCard(node, req.tool_use_id);
    const respond = response => post({ type: 'permission', requestId: id, response });
    // Refus avec consigne : on interrompt le tour puis la consigne part comme un vrai message utilisateur
    // (un motif glissé dans le résultat d'outil est traité par le modèle comme une donnée, pas une instruction).
    const denyWith = (reasonText, fallback) => {
      if (!reasonText) return respond({ behavior: 'deny', message: fallback });
      interrupted = true;
      respond({ behavior: 'deny', message: 'L\'utilisateur a refusé cette action et va préciser ce qu\'il veut.', interrupt: true });
      addOwnMessage(reasonText);
      post({ type: 'send', text: reasonText });
    };

    if (req.tool_name === 'AskUserQuestion') return place(questionCard(card, input, respond));

    if (req.tool_name === 'ExitPlanMode') {
      card.appendChild(el('div', 'title', 'Claude propose un plan'));
      const md = card.appendChild(el('div', 'md'));
      md.innerHTML = renderMd(input.plan || '');
      const actions = card.appendChild(el('div', 'actions'));
      const ok = actions.appendChild(el('button', 'primary', 'Approuver le plan'));
      ok.onclick = () => { respond({ behavior: 'allow' }); setMode('default'); };
      const reason = actions.appendChild(el('input'));
      reason.type = 'text'; reason.placeholder = 'Ou expliquer ce qu\'il faut changer…';
      const no = actions.appendChild(el('button', null, 'Continuer à planifier'));
      no.onclick = () => denyWith(reason.value.trim(), 'L\'utilisateur veut continuer à affiner le plan.');
      reason.addEventListener('keydown', e => { if (e.key === 'Enter') no.click(); });
      return place(card);
    }

    card.appendChild(el('div', 'title', `Autoriser ${req.display_name || req.tool_name} ?`));
    if (req.description && req.description !== toolSummary(req.tool_name, input)) card.appendChild(el('div', null, req.description));
    card.appendChild(toolDetails(req.tool_name, input));
    if (req.decision_reason) card.appendChild(el('div', 'outcome', req.decision_reason));
    const isEdit = ['Edit', 'MultiEdit', 'Write'].includes(req.tool_name);
    if (isEdit) card.appendChild(el('div', 'outcome', 'Le diff est ouvert dans Visual Studio : vous pouvez aussi décider (et retoucher) là-bas.'));

    const actions = card.appendChild(el('div', 'actions'));
    const allow = actions.appendChild(el('button', 'primary', 'Autoriser'));
    allow.onclick = () => respond({ behavior: 'allow' });
    if ((req.permission_suggestions || []).length) {
      const always = actions.appendChild(el('button', null, alwaysLabel(req.permission_suggestions)));
      always.onclick = () => {
        respond({ behavior: 'allow', updatedPermissions: req.permission_suggestions });
        const mode = req.permission_suggestions.find(s => s.type === 'setMode');
        if (mode) modeEl.value = mode.mode;
      };
    }
    if (isEdit) {
      const show = actions.appendChild(el('button', null, 'Afficher le diff'));
      show.onclick = () => post({ type: 'showDiff', requestId: id });
    }
    const reason = actions.appendChild(el('input'));
    reason.type = 'text'; reason.placeholder = 'Refuser et dire à Claude…';
    const deny = actions.appendChild(el('button', null, 'Refuser'));
    deny.onclick = () => denyWith(reason.value.trim(), 'L\'utilisateur a refusé.');
    reason.addEventListener('keydown', e => { if (e.key === 'Enter') deny.click(); });
    return place(card);
  }

  function alwaysLabel(suggestions) {
    const mode = suggestions.find(s => s.type === 'setMode');
    if (mode?.mode === 'acceptEdits') return 'Autoriser toutes les modifications (session)';
    return 'Toujours autoriser (session)';
  }

  function questionCard(card, input, respond) {
    card.appendChild(el('div', 'title', 'Claude a une question'));
    const questions = input.questions || [];
    // Un état par question, indexé par position (deux questions ou options de même libellé ne se confondent pas)
    const state = questions.map(() => ({ chosen: new Set(), other: '' }));
    const answerOf = (q, s) => s.other || (q.options || []).filter(o => s.chosen.has(o)).map(o => o.label).join(', ');
    const actions = el('div', 'actions');
    const ok = actions.appendChild(el('button', 'primary', 'Répondre'));
    const refresh = () => { ok.disabled = !questions.every((q, i) => answerOf(q, state[i])); };
    questions.forEach((q, i) => {
      const s = state[i];
      const box = card.appendChild(el('div', 'question'));
      box.appendChild(el('div', 'q', q.question));
      const opts = box.appendChild(el('div', 'opts'));
      const buttons = [];
      const other = el('input');
      const sync = () => buttons.forEach(([b, o]) => { const on = s.chosen.has(o); b.classList.toggle('selected', on); b.setAttribute('aria-pressed', on); });
      for (const o of q.options || []) {
        const b = opts.appendChild(el('button', 'opt', o.label));
        b.setAttribute('aria-pressed', 'false');
        if (o.description) b.appendChild(el('small', null, o.description));
        buttons.push([b, o]);
        b.onclick = () => {
          const on = s.chosen.has(o);
          if (!q.multiSelect) s.chosen.clear();
          on ? s.chosen.delete(o) : s.chosen.add(o);
          s.other = other.value = ''; // une option choisie remplace la réponse libre
          sync(); refresh();
        };
      }
      opts.appendChild(other);
      other.type = 'text'; other.placeholder = 'Autre réponse…';
      other.oninput = () => { s.other = other.value.trim(); if (s.other) { s.chosen.clear(); sync(); } refresh(); };
      other.addEventListener('keydown', e => { if (e.key === 'Enter' && !ok.disabled) ok.click(); });
    });
    card.appendChild(actions);
    // Le CLI attend les réponses indexées par le texte de la question ; on les reconstruit depuis l'état par position
    ok.onclick = () => {
      const answers = {};
      questions.forEach((q, i) => { answers[q.question] = answerOf(q, state[i]); });
      respond({ behavior: 'allow', updatedInput: { ...input, answers } });
    };
    const no = actions.appendChild(el('button', null, 'Ignorer'));
    no.onclick = () => respond({ behavior: 'deny', message: 'L\'utilisateur n\'a pas répondu à la question.' });
    refresh();
    return card;
  }

  // behavior : 'allow' | 'deny' | 'cancelled' (demande abandonnée par le C# : processus arrêté, tour terminé, control_cancel_request)
  function onPermissionResolved(id, behavior) {
    const card = perms.get(id);
    if (!card) return;
    card.classList.add('resolved');
    card.querySelectorAll('button, input').forEach(x => { x.disabled = true; });
    card.appendChild(el('div', 'outcome', behavior === 'allow' ? '✓ Autorisé' : behavior === 'cancelled' ? 'Demande expirée' : '✗ Refusé'));
    perms.delete(id);
  }

  // ---------------------------------------------------------------- flux du CLI

  function onCli(m) {
    try {
      switch (m.type) {
        case 'stream_event': return onStreamEvent(m);
        case 'assistant': return onAssistant(m);
        case 'user': {
          const content = m.message?.content;
          if (m.parent_tool_use_id) return Array.isArray(content) && onSubagentResults(content);
          if (Array.isArray(content) && content.some(c => c.type === 'tool_result')) {
            for (const c of content) if (c.type === 'tool_result') onToolResult(c);
            return;
          }
          return onUserReplay(m);
        }
        case 'control_request':
          if (m.request?.subtype === 'can_use_tool') permissionCard(m);
          return;
        case 'control_response': {
          const r = m.response;
          // Refus d'un set_model / set_permission_mode… : sans ce message, le sélecteur afficherait une valeur non appliquée
          if (r?.subtype === 'error') append(el('div', 'error', 'Commande refusée par le CLI : ' + (r.error || 'erreur inconnue')));
          if (r?.response?.commands) commands = r.response.commands;
          return;
        }
        case 'system':
          if (m.subtype === 'init') {
            if (m.slash_commands && !commands.length) commands = m.slash_commands.map(name => ({ name, description: '' }));
          } else if (m.subtype === 'compact_boundary') append(el('div', 'divider', '— contexte compacté —'));
          else if (m.subtype === 'status') {
            if (m.status === 'compacting') { statusEl.className = 'busy'; statusEl.textContent = 'Compactage du contexte…'; }
            else if (m.status == null) setStatus(busy ? 'busy' : 'ready'); // fin du compactage : retour à l'état réel du tour
          }
          return;
        case 'result': {
          currentAssistant = null;
          // Erreur et interruption sont affichées indépendamment : une interruption ne doit pas masquer une vraie erreur.
          // Le « result » d'un tour interrompu est lui-même en erreur (« Request interrupted… ») : celui-là n'est pas une erreur.
          const errText = m.result || m.errors?.join('\n') || ('Erreur : ' + m.subtype);
          const isInterruptResult = interrupted && /interrupt/i.test(errText + ' ' + (m.subtype || ''));
          if ((m.is_error || (m.subtype && m.subtype !== 'success')) && !isInterruptResult) append(el('div', 'error', errText));
          if (interrupted) interruptedDivider();
          interrupted = false;
          const parts = [];
          if (m.duration_ms) parts.push((m.duration_ms / 1000).toFixed(1) + ' s');
          if (m.total_cost_usd) parts.push(m.total_cost_usd.toFixed(3) + ' $');
          if (parts.length) append(el('div', 'result', parts.join(' · ')));
          return;
        }
      }
    } catch (e) { showRenderError(e); }
  }

  // L'historique peut arriver en plusieurs lots : le séparateur n'est posé qu'avec le dernier
  function renderHistory(list, last) {
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

  function clear() {
    messagesEl.replaceChildren(welcomeEl);
    messages.clear(); streamed.clear(); tools.clear(); perms.clear(); subTools.clear();
    currentAssistant = null; currentMessageId = null; rewindCard = null; interrupted = false;
    contextEl.hidden = true;
  }

  // ---------------------------------------------------------------- messages de l'extension

  host?.addEventListener('message', e => {
    const m = e.data;
    try {
      switch (m.type) {
        case 'cli': return onCli(m.msg);
        case 'status': return setStatus(m.state);
        case 'error': return append(el('div', 'error', m.text));
        case 'permissionResolved': return onPermissionResolved(m.requestId, m.behavior);
        case 'cleared': return clear();
        case 'history': return renderHistory(m.messages || [], m.last ?? true);
        case 'insert': {
          const pos = inputEl.selectionStart ?? inputEl.value.length;
          inputEl.value = inputEl.value.slice(0, pos) + m.text + inputEl.value.slice(pos);
          inputEl.focus();
          inputEl.selectionStart = inputEl.selectionEnd = pos + m.text.length;
          resizeInput();
          return;
        }
        case 'selection':
          selectionEl.hidden = !m.label;
          selectionEl.textContent = '📎 ' + (m.label || '');
          return;
        case 'focus': inputEl.focus(); return;
        case 'notice': return append(el('div', 'notice', m.text));
        case 'debugEvent': return onDebugEvent(m);
        case 'context': return onContext(m);
        case 'rewindPreview': return onRewindPreview(m);
        case 'rewindDone': return onRewindDone(m);
        case 'settings':
          modelEl.value = m.model || '';
          effortEl.value = m.effort || '';
          modeEl.value = m.mode;
          selectionEl.classList.toggle('off', !m.attachSelection);
          autoFormatEl.classList.toggle('off', !m.autoFormat);
          autoAnalyzeEl.classList.toggle('off', !m.autoAnalyze);
          cwd = m.cwd || '';
          cwdEl.textContent = cwd;
          return;
        case 'sessions': return showSessions(m.sessions || []);
        case 'files': return onFiles(m);
        case 'theme':
          for (const [k, v] of Object.entries(m.vars || {})) document.documentElement.style.setProperty(k, v);
          document.documentElement.style.colorScheme = m.dark ? 'dark' : 'light';
          document.documentElement.classList.toggle('light', !m.dark); // seconde palette sémantique (style.css)
          if (darkTheme !== !!m.dark) { darkTheme = !!m.dark; rerenderDiagrams(); }
          return;
      }
    } catch (err) { showRenderError(err); }
  });

  // ---------------------------------------------------------------- saisie

  function resizeInput() {
    inputEl.style.height = 'auto';
    inputEl.style.height = Math.min(inputEl.scrollHeight + 2, window.innerHeight * 0.4) + 'px';
  }

  function send() {
    const text = inputEl.value.trim();
    if (!text && !images.length) return;
    const ctx = !selectionEl.hidden && !selectionEl.classList.contains('off') ? selectionEl.textContent.replace(/^📎 /, '') : null;
    addOwnMessage(text, ctx, images);
    stick = true; scroll();
    post({ type: 'send', text, images: images.map(i => ({ mediaType: i.mediaType, data: i.data })) });
    inputEl.value = '';
    images = [];
    renderAttachments();
    hidePopup();
    resizeInput();
  }

  // ---------------------------------------------------------------- images jointes

  const MAX_SIDE = 1568; // au-delà, l'API redimensionne de toute façon : on allège l'envoi
  let images = [];        // { mediaType, data (base64), url (aperçu) }

  function addImageFile(file) {
    if (!file || !file.type.startsWith('image/')) return;
    const fail = () => append(el('div', 'notice', 'Image illisible, non jointe : ' + (file.name || file.type)));
    const reader = new FileReader();
    reader.onerror = fail;
    reader.onload = () => {
      const img = new Image();
      img.onerror = fail;
      img.onload = () => {
        const scale = Math.min(1, MAX_SIDE / Math.max(img.width, img.height));
        let url = reader.result;
        let mediaType = file.type;
        if (scale < 1 || !['image/png', 'image/jpeg', 'image/gif', 'image/webp'].includes(mediaType)) {
          const canvas = document.createElement('canvas');
          canvas.width = Math.round(img.width * scale);
          canvas.height = Math.round(img.height * scale);
          canvas.getContext('2d').drawImage(img, 0, 0, canvas.width, canvas.height);
          mediaType = 'image/png';
          url = canvas.toDataURL(mediaType);
          if (url.length > 4_000_000) { mediaType = 'image/jpeg'; url = canvas.toDataURL(mediaType, 0.85); }
        }
        images.push({ mediaType, data: url.slice(url.indexOf(',') + 1), url });
        renderAttachments();
      };
      img.src = reader.result;
    };
    reader.readAsDataURL(file);
  }

  function renderAttachments() {
    attachmentsEl.innerHTML = '';
    attachmentsEl.hidden = !images.length;
    images.forEach((img, i) => {
      const thumb = attachmentsEl.appendChild(el('div', 'thumb'));
      const im = thumb.appendChild(el('img'));
      im.src = img.url; im.alt = 'Image jointe ' + (i + 1);
      const x = thumb.appendChild(el('button', 'remove', '✕'));
      x.title = 'Retirer';
      x.setAttribute('aria-label', 'Retirer l\'image ' + (i + 1));
      x.onclick = () => { images.splice(i, 1); renderAttachments(); };
    });
  }

  inputEl.addEventListener('paste', e => {
    const files = [...(e.clipboardData?.items || [])].filter(it => it.kind === 'file').map(it => it.getAsFile());
    if (!files.some(f => f?.type.startsWith('image/'))) return;
    e.preventDefault();
    files.forEach(addImageFile);
  });
  document.addEventListener('dragover', e => e.preventDefault());
  document.addEventListener('drop', e => {
    e.preventDefault();
    [...(e.dataTransfer?.files || [])].forEach(addImageFile);
  });
  $('attach').onclick = () => fileInputEl.click();
  fileInputEl.onchange = e => { [...e.target.files].forEach(addImageFile); e.target.value = ''; };

  function setMode(mode) {
    modeEl.value = mode;
    post({ type: 'setMode', mode });
  }

  $('send').onclick = send;
  // Toute la pastille « Claude travaille… | ■ Arrêter » est cliquable, pas seulement le bouton.
  activityEl.onclick = interrupt;
  modeEl.onchange = e => post({ type: 'setMode', mode: e.target.value });
  modelEl.onchange = e => post({ type: 'setModel', model: e.target.value });
  effortEl.onchange = e => post({ type: 'setEffort', effort: e.target.value });
  $('compact').onclick = () => {
    if (busy) return;
    append(el('div', 'divider', '— compactage demandé —'));
    post({ type: 'send', text: '/compact' });
  };

  function onContext(m) {
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
  $('newChat').onclick = () => { clear(); post({ type: 'newChat' }); };
  $('history').onclick = () => post({ type: 'listSessions' });
  $('closeSessions').onclick = () => { sessionsEl.hidden = true; };
  selectionEl.onclick = () => post({ type: 'setAttachSelection', value: selectionEl.classList.contains('off') });
  autoFormatEl.onclick = () => post({ type: 'setAutoFormat', value: autoFormatEl.classList.contains('off') });
  autoAnalyzeEl.onclick = () => post({ type: 'setAutoAnalyze', value: autoAnalyzeEl.classList.contains('off') });

  // Le débogueur de VS s'est arrêté sur une exception en dehors d'un appel d'outil
  function onDebugEvent(m) {
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
      b.onclick = () => { b.disabled = true; addOwnMessage('⚡ Analyse de l\'exception'); post({ type: 'analyzeException' }); };
    }
    append(card);
    if (m.auto) stick = true;
  }

  function showSessions(list) {
    sessionListEl.innerHTML = '';
    if (!list.length) sessionListEl.appendChild(el('div', 'session', 'Aucune conversation pour ce dossier.'));
    for (const s of list) {
      const row = sessionListEl.appendChild(el('div', 'session'));
      row.appendChild(el('div', null, s.title));
      row.appendChild(el('div', 'when', s.updated));
      clickable(row, () => { sessionsEl.hidden = true; post({ type: 'resume', sessionId: s.id }); });
    }
    sessionsEl.hidden = false;
  }

  // ---------------------------------------------------------------- suggestions « / » et « @ »
  // Une suggestion = { label, detail, apply() } ; la liste s'affiche au-dessus de la zone de saisie.

  let popupItems = [], popupIndex = 0;
  let fileSeq = 0, fileQuery = null, fileTimer = null, pendingFileSearch = null;

  function showPopup(items) {
    popupItems = items;
    if (!items.length) return hidePopup();
    popupIndex = Math.min(popupIndex, items.length - 1);
    slashEl.innerHTML = '';
    items.forEach((it, i) => {
      const row = slashEl.appendChild(el('div', 'item' + (i === popupIndex ? ' active' : ''), it.label));
      row.setAttribute('role', 'option');
      row.setAttribute('aria-selected', i === popupIndex);
      if (it.detail) row.appendChild(el('small', null, it.detail));
      row.onmousedown = e => { e.preventDefault(); pick(i); };
    });
    slashEl.hidden = false;
    slashEl.querySelector('.active')?.scrollIntoView({ block: 'nearest' });
  }
  function hidePopup() {
    slashEl.hidden = true; popupItems = []; popupIndex = 0;
    clearTimeout(fileTimer); fileQuery = null; pendingFileSearch = null; // une recherche « @ » en vol ne doit pas rouvrir la liste
  }
  function pick(i) { const it = popupItems[i]; hidePopup(); it.apply(); inputEl.focus(); }

  // Remplace le jeton en cours de frappe (de start au curseur) par du texte
  function replaceToken(start, text) {
    const end = inputEl.selectionStart;
    inputEl.value = inputEl.value.slice(0, start) + text + inputEl.value.slice(end);
    inputEl.selectionStart = inputEl.selectionEnd = start + text.length;
    resizeInput();
  }

  function updateSuggestions() {
    const before = inputEl.value.slice(0, inputEl.selectionStart);

    const slash = /^\/(\S*)$/.exec(before);
    if (slash && commands.length) {
      const q = slash[1].toLowerCase();
      return showPopup(commands.filter(c => c.name.toLowerCase().includes(q)).slice(0, 30).map(c => ({
        label: '/' + c.name,
        detail: c.description ? short(c.description, 90) : '',
        apply: () => replaceToken(0, '/' + c.name + ' '),
      })));
    }

    const at = /(^|\s)@([^\s@]*)$/.exec(before);
    if (at) {
      const query = at[2], start = before.length - query.length - 1;
      if (query === fileQuery) return;
      fileQuery = query;
      clearTimeout(fileTimer);
      fileTimer = setTimeout(() => {
        const seq = ++fileSeq;
        pendingFileSearch = { seq, start };
        post({ type: 'searchFiles', query, seq });
      }, 120);
      return;
    }
    hidePopup();
  }

  function onFiles(m) {
    if (!pendingFileSearch || m.seq !== pendingFileSearch.seq) return; // réponse périmée
    const { start } = pendingFileSearch;
    showPopup((m.files || []).map(f => {
      const slash = f.lastIndexOf('/');
      return {
        label: f.slice(slash + 1),
        detail: slash > 0 ? f.slice(0, slash) : '',
        apply: () => replaceToken(start, '@' + f + ' '),
      };
    }));
  }

  inputEl.addEventListener('input', () => { updateSuggestions(); resizeInput(); });
  inputEl.addEventListener('click', updateSuggestions);
  inputEl.addEventListener('blur', () => setTimeout(hidePopup, 150));
  inputEl.addEventListener('keydown', e => {
    if (!slashEl.hidden && popupItems.length) {
      if (e.key === 'ArrowDown') { popupIndex = (popupIndex + 1) % popupItems.length; showPopup(popupItems); e.preventDefault(); return; }
      if (e.key === 'ArrowUp') { popupIndex = (popupIndex - 1 + popupItems.length) % popupItems.length; showPopup(popupItems); e.preventDefault(); return; }
      if (e.key === 'Tab' || (e.key === 'Enter' && !e.shiftKey)) { pick(popupIndex); e.preventDefault(); return; }
      if (e.key === 'Escape') { hidePopup(); e.preventDefault(); return; }
    }
    if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); send(); }
    else if (e.key === 'Escape') interrupt();
  });

  post({ type: 'ready' });
  inputEl.focus();
})();
