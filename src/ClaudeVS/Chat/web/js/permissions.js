// Demandes de permission du CLI : cartes d'autorisation, de plan et de question.
import { state, tools, perms, subTools } from './state.js';
import { el, append, scroll, modeEl, short } from './dom.js';
import { post } from './host.js';
import { ToHost } from './protocol.js';
import { renderMd } from './markdown.js';
import { toolSummary, toolDetails } from './tools.js';
import { addOwnMessage, takeRewindNotice } from './conversation.js';
import { setMode } from './composer.js';

// La carte se place juste sous l'outil concerné (sinon en fin de fil). Outil d'un sous-agent : sous la carte de
// l'agent, avec son nom, pour savoir qui demande quoi (un agent en arrière-plan demande après la fin du tour).
// Outil rangé dans la zone « Actions » du tour (repliée) : sous la zone, pour rester visible.
export function placeCard(card, toolUseId) {
  const agent = subTools.get(toolUseId)?.closest('.tool.agent');
  const tool = agent || tools.get(toolUseId);
  if (!tool) return append(card);
  if (agent) card.prepend(el('div', 'perm-agent', 'Agent « ' + agent.querySelector('.tool-head .name').textContent + ' »'));
  // Plusieurs demandes : dans l'ordre d'arrivée, à la suite des précédentes
  let anchor = tool.closest('.turn-actions') || tool;
  while (anchor.nextElementSibling?.classList.contains('perm') && anchor.nextElementSibling.dataset.toolUseId) anchor = anchor.nextElementSibling;
  anchor.after(card);
  scroll();
  return card;
}

export function permissionCard(m) {
  const req = m.request, id = m.request_id, input = req.input || {};
  const card = el('div', 'perm');
  perms.set(id, card);
  // Mémorisés pour résorber la carte dans celle de l'outil une fois la décision prise (onPermissionResolved)
  card.dataset.toolUseId = req.tool_use_id || '';
  card.dataset.kind = req.tool_name === 'AskUserQuestion' || req.tool_name === 'ExitPlanMode' ? 'dialog' : 'tool';
  const place = node => placeCard(node, req.tool_use_id);
  const respond = response => post({ type: ToHost.permission, requestId: id, response });
  // Refus avec consigne : on interrompt le tour puis la consigne part comme un vrai message utilisateur
  // (un motif glissé dans le résultat d'outil est traité par le modèle comme une donnée, pas une instruction).
  const denyWith = (reasonText, fallback) => {
    if (!reasonText) return respond({ behavior: 'deny', message: fallback });
    state.interrupted = true;
    respond({ behavior: 'deny', message: 'L\'utilisateur a refusé cette action et va préciser ce qu\'il veut.', interrupt: true });
    addOwnMessage(reasonText);
    post({ type: ToHost.send, text: reasonText, notice: takeRewindNotice() });
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
  // Bash / PowerShell : une ligne qui dit ce que fait la commande (phrase écrite par le modèle dans input.description),
  // la commande complète dessous, repliée si elle est longue
  const isShell = req.tool_name === 'Bash' || req.tool_name === 'PowerShell';
  const command = isShell ? input.command || '' : '';
  const lines = command.split('\n').length;
  const longCommand = lines > 5 || command.length > 400;
  const gist = isShell ? input.description || req.description || (longCommand ? command : '') : req.description;
  if (gist && (isShell || gist !== toolSummary(req.tool_name, input))) {
    const line = card.appendChild(el('div', 'gist', short(gist.trim().split('\n')[0], 80)));
    line.title = gist;
  }
  const details = toolDetails(req.tool_name, input);
  if (longCommand) {
    const box = card.appendChild(el('details', 'cmd'));
    box.appendChild(el('summary', null, `Commande complète (${lines} ligne${lines > 1 ? 's' : ''})`));
    box.appendChild(details);
  } else card.appendChild(details);
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
    show.onclick = () => post({ type: ToHost.showDiff, requestId: id });
  }
  const reason = actions.appendChild(el('input'));
  reason.type = 'text'; reason.placeholder = 'Refuser et dire à Claude…';
  const deny = actions.appendChild(el('button', null, 'Refuser'));
  deny.onclick = () => denyWith(reason.value.trim(), 'L\'utilisateur a refusé.');
  reason.addEventListener('keydown', e => { if (e.key === 'Enter') deny.click(); });
  return place(card);
}

export function alwaysLabel(suggestions) {
  const mode = suggestions.find(s => s.type === 'setMode');
  if (mode?.mode === 'acceptEdits') return 'Autoriser toutes les modifications (session)';
  return 'Toujours autoriser (session)';
}

export function questionCard(card, input, respond) {
  card.appendChild(el('div', 'title', 'Claude a une question'));
  const questions = input.questions || [];
  // Un état par question, indexé par position (deux questions ou options de même libellé ne se confondent pas)
  const picks = questions.map(() => ({ chosen: new Set(), other: '' }));
  const answerOf = (q, s) => s.other || (q.options || []).filter(o => s.chosen.has(o)).map(o => o.label).join(', ');
  const actions = el('div', 'actions');
  const ok = actions.appendChild(el('button', 'primary', 'Répondre'));
  const refresh = () => { ok.disabled = !questions.every((q, i) => answerOf(q, picks[i])); };
  questions.forEach((q, i) => {
    const s = picks[i];
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
    questions.forEach((q, i) => { answers[q.question] = answerOf(q, picks[i]); });
    respond({ behavior: 'allow', updatedInput: { ...input, answers } });
  };
  const no = actions.appendChild(el('button', null, 'Ignorer'));
  no.onclick = () => respond({ behavior: 'deny', message: 'L\'utilisateur n\'a pas répondu à la question.' });
  refresh();
  return card;
}

// Demande abandonnée sans réponse : 'stopped' = processus arrêté par l'extension, 'cli' = retirée par le CLI
// (interruption, ou décidée par un autre moyen dont on ne sait rien : formulation prudente).
function cancelledLabels(reason) {
  if (reason === 'stopped') return ['Abandonnée', 'Abandonnée (session arrêtée)'];
  if (state.interrupted) return ['Interrompue', 'Interrompue'];
  return ['Retirée', 'Retirée par Claude Code'];
}

// behavior : 'allow' | 'deny' | 'cancelled' (demande abandonnée, reason : 'stopped' | 'cli')
export function onPermissionResolved(id, behavior, reason) {
  const card = perms.get(id);
  if (!card) return;
  const [cancelledTag, cancelledOutcome] = cancelledLabels(reason);
  const label = behavior === 'allow' ? 'Autorisé' : behavior === 'cancelled' ? cancelledTag : 'Refusé';
  // Demande d'autorisation d'un outil : une fois décidée, elle n'apporte plus rien (les paramètres sont dans la carte
  // de l'outil). On la retire et la décision reste tracée par une étiquette dans l'en-tête de l'outil.
  // Les cartes de question et de plan, elles, portent les réponses de l'utilisateur : elles restent.
  const toolUseId = card.dataset.toolUseId;
  const tool = card.dataset.kind === 'tool' && tools.get(toolUseId);
  const subLine = card.dataset.kind === 'tool' && subTools.get(toolUseId);
  if (tool || subLine) {
    card.remove();
    const tag = el('span', 'perm-tag ' + behavior, label);
    if (tool) tool.querySelector('.tool-head .state').before(tag);
    else subLine.querySelector('.name').after(' ', tag);
    perms.delete(id);
    return;
  }
  card.classList.add('resolved');
  card.querySelectorAll('button, input').forEach(x => { x.disabled = true; });
  card.appendChild(el('div', 'outcome', behavior === 'allow' ? '✓ Autorisé' : behavior === 'cancelled' ? cancelledOutcome : '✗ Refusé'));
  perms.delete(id);
}
