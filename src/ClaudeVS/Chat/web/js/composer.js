// Zone de saisie : envoi, images jointes, barre d'outils, suggestions « / » et « @ ».
import { state } from './state.js';
import {
  el, append, scroll, short,
  inputEl, selectionEl, slashEl, sessionsEl, attachmentsEl, fileInputEl, activityEl,
  modeEl, modelEl, effortEl, cwdEl, autoFormatEl, autoAnalyzeEl,
  attachEl, sendEl, compactEl, newChatEl, historyEl, closeSessionsEl,
} from './dom.js';
import { post } from './host.js';
import { setChosenModel, setChosenEffort } from './toolbar.js';
import { ToHost } from './protocol.js';
import { icon, withIcon } from './icons.js';
import { addOwnMessage, interrupt, clear } from './conversation.js';

// ---------------------------------------------------------------- saisie

export function resizeInput() {
  inputEl.style.height = 'auto';
  inputEl.style.height = Math.min(inputEl.scrollHeight + 2, window.innerHeight * 0.4) + 'px';
}

export function send() {
  const text = inputEl.value.trim();
  if (!text && !state.images.length) return;
  const ctx = !selectionEl.hidden && !selectionEl.classList.contains('off') ? selectionEl.textContent : null;
  addOwnMessage(text, ctx, state.images);
  state.stick = true; scroll();
  post({ type: ToHost.send, text, images: state.images.map(i => ({ mediaType: i.mediaType, data: i.data })) });
  inputEl.value = '';
  state.images = [];
  renderAttachments();
  hidePopup();
  resizeInput();
}

// Texte inséré par l'extension (mention @fichier depuis l'éditeur) à la position du curseur
export function onInsert(m) {
  const pos = inputEl.selectionStart ?? inputEl.value.length;
  inputEl.value = inputEl.value.slice(0, pos) + m.text + inputEl.value.slice(pos);
  inputEl.focus();
  inputEl.selectionStart = inputEl.selectionEnd = pos + m.text.length;
  resizeInput();
}

export function onSelection(m) {
  selectionEl.hidden = !m.label;
  withIcon(selectionEl, 'clip', m.label || '');
}

export function onSettings(m) {
  setChosenModel(m.model);
  effortEl.value = m.effort || '';
  setChosenEffort(m.effort);
  modeEl.value = m.mode;
  selectionEl.classList.toggle('off', !m.attachSelection);
  autoFormatEl.classList.toggle('off', !m.autoFormat);
  autoAnalyzeEl.classList.toggle('off', !m.autoAnalyze);
  state.cwd = m.cwd || '';
  cwdEl.textContent = state.cwd;
}

// ---------------------------------------------------------------- images jointes

const MAX_SIDE = 1568; // au-delà, l'API redimensionne de toute façon : on allège l'envoi

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
      state.images.push({ mediaType, data: url.slice(url.indexOf(',') + 1), url });
      renderAttachments();
    };
    img.src = reader.result;
  };
  reader.readAsDataURL(file);
}

function renderAttachments() {
  attachmentsEl.innerHTML = '';
  attachmentsEl.hidden = !state.images.length;
  state.images.forEach((img, i) => {
    const thumb = attachmentsEl.appendChild(el('div', 'thumb'));
    const im = thumb.appendChild(el('img'));
    im.src = img.url; im.alt = 'Image jointe ' + (i + 1);
    const x = thumb.appendChild(el('button', 'remove'));
    x.appendChild(icon('close', 8));
    x.title = 'Retirer';
    x.setAttribute('aria-label', 'Retirer l\'image ' + (i + 1));
    x.onclick = () => { state.images.splice(i, 1); renderAttachments(); };
  });
}

export function setMode(mode) {
  modeEl.value = mode;
  post({ type: ToHost.setMode, mode });
}

// ---------------------------------------------------------------- suggestions « / » et « @ »
// Une suggestion = { label, detail, apply() } ; la liste s'affiche au-dessus de la zone de saisie.

function showPopup(items) {
  state.popupItems = items;
  if (!items.length) return hidePopup();
  state.popupIndex = Math.min(state.popupIndex, items.length - 1);
  slashEl.innerHTML = '';
  items.forEach((it, i) => {
    const row = slashEl.appendChild(el('div', 'item' + (i === state.popupIndex ? ' active' : ''), it.label));
    row.setAttribute('role', 'option');
    row.setAttribute('aria-selected', i === state.popupIndex);
    if (it.detail) row.appendChild(el('small', null, it.detail));
    row.onmousedown = e => { e.preventDefault(); pick(i); };
  });
  slashEl.hidden = false;
  slashEl.querySelector('.active')?.scrollIntoView({ block: 'nearest' });
}
function hidePopup() {
  slashEl.hidden = true; state.popupItems = []; state.popupIndex = 0;
  // une recherche « @ » en vol ne doit pas rouvrir la liste
  clearTimeout(state.fileTimer); state.fileQuery = null; state.pendingFileSearch = null;
}
function pick(i) { const it = state.popupItems[i]; hidePopup(); it.apply(); inputEl.focus(); }

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
  if (slash && state.commands.length) {
    const q = slash[1].toLowerCase();
    return showPopup(state.commands.filter(c => c.name.toLowerCase().includes(q)).slice(0, 30).map(c => ({
      label: '/' + c.name,
      detail: c.description ? short(c.description, 90) : '',
      apply: () => replaceToken(0, '/' + c.name + ' '),
    })));
  }

  const at = /(^|\s)@([^\s@]*)$/.exec(before);
  if (at) {
    const query = at[2], start = before.length - query.length - 1;
    if (query === state.fileQuery) return;
    state.fileQuery = query;
    clearTimeout(state.fileTimer);
    state.fileTimer = setTimeout(() => {
      const seq = ++state.fileSeq;
      state.pendingFileSearch = { seq, start };
      post({ type: ToHost.searchFiles, query, seq });
    }, 120);
    return;
  }
  hidePopup();
}

export function onFiles(m) {
  const pending = state.pendingFileSearch;
  if (!pending || m.seq !== pending.seq) return; // réponse périmée
  const { start } = pending;
  showPopup((m.files || []).map(f => {
    const slash = f.lastIndexOf('/');
    return {
      label: f.slice(slash + 1),
      detail: slash > 0 ? f.slice(0, slash) : '',
      apply: () => replaceToken(start, '@' + f + ' '),
    };
  }));
}

// ---------------------------------------------------------------- gestionnaires (appelé une fois par main.js)

export function wireComposer() {
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
  attachEl.onclick = () => fileInputEl.click();
  fileInputEl.onchange = e => { [...e.target.files].forEach(addImageFile); e.target.value = ''; };

  sendEl.onclick = send;
  // Toute la pastille « Claude travaille… | ■ Arrêter » est cliquable, pas seulement le bouton.
  activityEl.onclick = interrupt;
  modeEl.onchange = e => post({ type: ToHost.setMode, mode: e.target.value });
  modelEl.onchange = e => { setChosenModel(e.target.value); post({ type: ToHost.setModel, model: e.target.value }); };
  effortEl.onchange = e => post({ type: ToHost.setEffort, effort: e.target.value });
  compactEl.onclick = () => {
    if (state.busy) return;
    append(el('div', 'divider', '— compactage demandé —'));
    post({ type: ToHost.send, text: '/compact' });
  };

  newChatEl.onclick = () => { clear(); post({ type: ToHost.newChat }); };
  historyEl.onclick = () => post({ type: ToHost.listSessions });
  closeSessionsEl.onclick = () => { sessionsEl.hidden = true; };
  selectionEl.onclick = () => post({ type: ToHost.setAttachSelection, value: selectionEl.classList.contains('off') });
  autoFormatEl.onclick = () => post({ type: ToHost.setAutoFormat, value: autoFormatEl.classList.contains('off') });
  autoAnalyzeEl.onclick = () => post({ type: ToHost.setAutoAnalyze, value: autoAnalyzeEl.classList.contains('off') });

  inputEl.addEventListener('input', () => { updateSuggestions(); resizeInput(); });
  inputEl.addEventListener('click', updateSuggestions);
  inputEl.addEventListener('blur', () => setTimeout(hidePopup, 150));
  inputEl.addEventListener('keydown', e => {
    const items = state.popupItems;
    if (!slashEl.hidden && items.length) {
      if (e.key === 'ArrowDown') { state.popupIndex = (state.popupIndex + 1) % items.length; showPopup(items); e.preventDefault(); return; }
      if (e.key === 'ArrowUp') { state.popupIndex = (state.popupIndex - 1 + items.length) % items.length; showPopup(items); e.preventDefault(); return; }
      if (e.key === 'Tab' || (e.key === 'Enter' && !e.shiftKey)) { pick(state.popupIndex); e.preventDefault(); return; }
      if (e.key === 'Escape') { hidePopup(); e.preventDefault(); return; }
    }
    if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); send(); }
    else if (e.key === 'Escape') interrupt();
  });
}
