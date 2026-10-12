// Consommation du forfait (données de /usage) sous la saisie : session de 5 h et semaine, avec leur réinitialisation.
// Plafonds par modèle et coût de la session dans l'infobulle. Clé API (pas de plafonds) : coût seul.
import { el, usageEl } from './dom.js';

const MAIN = [['five_hour', 'Session'], ['seven_day', 'Semaine']];
const BY_MODEL = [['seven_day_opus', 'Semaine Opus'], ['seven_day_sonnet', 'Semaine Sonnet']];

// Moins de 24 h : l'heure suffit ; au-delà, le jour aussi (« lun. 09:00 »)
function resetLabel(iso) {
  const d = iso ? new Date(iso) : null;
  if (!d || isNaN(d)) return '';
  const opts = d - Date.now() < 864e5 ? { hour: '2-digit', minute: '2-digit' } : { weekday: 'short', hour: '2-digit', minute: '2-digit' };
  return d.toLocaleString('fr-FR', opts);
}

function gauge(label, w) {
  const pct = Math.round(w.utilization);
  const g = el('span', 'gauge');
  g.classList.toggle('warn', pct >= 70 && pct < 90);
  g.classList.toggle('danger', pct >= 90);
  g.append(el('span', 'label', label));
  g.appendChild(el('span', 'bar')).appendChild(el('span', 'fill')).style.width = Math.min(pct, 100) + '%';
  g.append(el('span', 'pct', `${pct} %`));
  const reset = resetLabel(w.resets_at);
  if (reset) g.append(el('span', 'reset', reset));
  return g;
}

export function onUsage(m) {
  const limits = m.limits || {};
  const has = w => typeof w?.utilization === 'number';
  const main = MAIN.filter(([k]) => has(limits[k])).map(([k, label]) => ({ label, ...limits[k] }));
  const byModel = BY_MODEL.filter(([k]) => has(limits[k])).map(([k, label]) => ({ label, ...limits[k] }))
    .concat((limits.model_scoped || []).filter(has).map(s => ({ label: 'Semaine ' + s.display_name, ...s })));
  const cost = typeof m.cost === 'number' && m.cost > 0 ? m.cost.toFixed(2) + ' $' : null;

  usageEl.replaceChildren(...main.map(w => gauge(w.label, w)));
  if (!main.length && cost) usageEl.append(el('span', 'cost', `Conversation : ${cost}`));
  usageEl.hidden = !usageEl.childElementCount;
  const line = w => `${w.label} : ${Math.round(w.utilization)} %` + (w.resets_at ? ` · réinitialisation ${resetLabel(w.resets_at)}` : '');
  usageEl.title = [...main, ...byModel].map(line).concat(cost ? [`Coût de la conversation : ${cost}`] : []).join('\n');
}
