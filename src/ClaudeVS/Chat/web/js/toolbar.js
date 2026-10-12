// Barre d'outils : liste des modèles fournie par le CLI, modèle réellement utilisé, niveaux d'effort acceptés.
import { modelEl, effortEl } from './dom.js';

// Repli tant que le CLI n'a pas répondu à initialize (ou s'il ne fournit pas de liste)
const FALLBACK_MODELS = [
  { value: 'opus', displayName: 'Opus' },
  { value: 'sonnet', displayName: 'Sonnet' },
  { value: 'haiku', displayName: 'Haiku' },
  { value: 'fable', displayName: 'Fable' },
];

let models = FALLBACK_MODELS;
let chosen = '';        // valeur du réglage « modèle » ('' = défaut du CLI)
let actual = '';        // modèle réellement utilisé, tel que rapporté par le CLI
let chosenEffort = '';
let appliedEffort = ''; // effort réellement appliqué, tel que rapporté par le CLI
const EFFORT_LABELS = { low: 'faible', medium: 'moyen', high: 'élevé', xhigh: 'maximal', max: 'max' };
let defaultEntry = null; // entrée « default » de la liste du CLI, qui dit ce que recouvre le défaut

/** Réponse à initialize : `models` du CLI. */
export function setModels(list) {
  if (!Array.isArray(list) || !list.length) return;
  defaultEntry = list.find(m => m.value === 'default') || null;
  models = list.filter(m => m.value && m.value !== 'default');
  render();
}

/** `model` de system/init, puis de chaque réponse assistant. Les modèles internes (« <synthetic> ») sont ignorés. */
export function setActualModel(name) {
  if (!name || name.startsWith('<') || name === actual) return;
  actual = name;
  render();
}

/** Modèle et effort réellement appliqués par le CLI (get_settings) : complètent les entrées « par défaut ». */
export function setApplied(m) {
  if (m.effort) appliedEffort = m.effort;
  if (m.model) actual = m.model;
  render();
}

/** Réglage d'effort reçu de l'extension ('' = défaut). */
export function setChosenEffort(value) {
  chosenEffort = value || '';
  renderEffortDefault();
}

/** Réglage reçu de l'extension (démarrage, changement de conversation). */
export function setChosenModel(value) {
  chosen = value || '';
  render();
}

function render() {
  // Le défaut du CLI ; le modèle réellement utilisé ne le remplace que si aucun modèle n'est choisi explicitement
  const shown = displayName(!chosen && actual ? actual : defaultEntry?.resolvedModel);
  modelEl.textContent = '';
  const add = (value, text, extra = {}) => {
    const o = document.createElement('option');
    o.value = value; o.textContent = text;
    if (extra.title) o.title = extra.title;
    if (extra.disabled) o.disabled = true;
    modelEl.appendChild(o);
  };
  add('', shown ? `Par défaut (${shown})` : 'Par défaut');
  for (const m of models) add(m.value, m.displayName || m.value, { title: m.description, disabled: m.disabled });
  // Réglage sauvegardé que la liste ne connaît pas (version précise, liste de repli) : on le garde sélectionnable
  if (chosen && !models.some(m => m.value === chosen)) add(chosen, chosen);
  modelEl.value = chosen;
  updateEfforts();
  renderEffortDefault();
}

/** Nom affiché dans la liste (« Opus 5.5 ») pour un identifiant de modèle (« claude-opus-5-5 »), même format que les autres entrées. */
function displayName(id) {
  if (!id) return '';
  const m = models.find(x => x.resolvedModel === id || x.value === id);
  return m?.displayName || id;
}

/** « Par défaut (moyen) » : l'effort réel, tant que l'utilisateur n'en a pas choisi un. */
function renderEffortDefault() {
  const shown = !chosenEffort && appliedEffort ? ` (${EFFORT_LABELS[appliedEffort] || appliedEffort})` : '';
  effortEl.options[0].textContent = 'Par défaut' + shown;
}

/** Grise les niveaux d'effort que le modèle choisi ne gère pas (liste `supportedEffortLevels` du CLI). */
export function updateEfforts() {
  const entry = chosen ? models.find(m => m.value === chosen) : defaultEntry;
  const supported = entry?.supportedEffortLevels;
  for (const o of effortEl.options) o.disabled = !!(o.value && Array.isArray(supported) && !supported.includes(o.value));
}
