// Icônes SVG monochromes (fill / stroke = currentColor) : elles suivent le thème de VS, contrairement aux emojis en
// couleur et aux caractères rares qui tombent dans des polices de secours. Grille 12 × 12 ; les rectangles restent
// calés sur les pixels (crispEdges, hérité de svg.glyph), les obliques et les courbes sont lissées (geometricPrecision).
const NS = 'http://www.w3.org/2000/svg';

const rect = (x, y, width, height) => ['rect', { x, y, width, height }];
const fill = d => ['path', { d, 'shape-rendering': 'geometricPrecision' }];
const line = (d, w = 1) => ['path', {
  d, fill: 'none', stroke: 'currentColor', 'stroke-width': w,
  'stroke-linecap': 'round', 'stroke-linejoin': 'round', 'shape-rendering': 'geometricPrecision',
}];

// nom → [taille par défaut en px, formes]
const ICONS = {
  plus: [12, [rect(5, 1, 2, 10), rect(1, 5, 10, 2)]],
  close: [10, [line('M2 2l8 8M10 2l-8 8', 1.6)]],
  history: [13, [
    ['circle', { cx: 6, cy: 6, r: 5, fill: 'none', stroke: 'currentColor', 'stroke-width': 1, 'shape-rendering': 'geometricPrecision' }],
    rect(5, 3, 1, 4), rect(5, 6, 3, 1),
  ]],
  compact: [11, [line('M3 1l3 3 3-3M3 11l3-3 3 3', 1.3), rect(1, 5, 10, 2)]],
  send: [14, [fill('M1.5 1.5L11 6 1.5 10.5 3.5 6z')]],
  image: [14, [
    ['rect', { x: 0.5, y: 1.5, width: 11, height: 9, fill: 'none', stroke: 'currentColor', 'stroke-width': 1 }],
    fill('M1.5 10L5 5.5l2.5 3L9 7l2.5 3z'),
    ['circle', { cx: 8.5, cy: 4, r: 1.2, 'shape-rendering': 'geometricPrecision' }],
  ]],
  sparkle: [11, [fill('M6 .5Q6.6 5.4 11.5 6 6.6 6.6 6 11.5 5.4 6.6 .5 6 5.4 5.4 6 .5z')]],
  bolt: [11, [fill('M7.5 .5L2 7h3.5l-1 4.5L10 5H6.5z')]],
  clip: [11, [line('M8.5 4v4.5a2.5 2.5 0 0 1-5 0V3a1.75 1.75 0 0 1 3.5 0v5a1 1 0 0 1-2 0V4')]],
  rewind: [12, [fill('M1 4.5L4.5 1.5v6z'), line('M4 4.5h3.5a3 3 0 0 1 0 6H3', 1.2)]],
};

// inline : l'icône précède un texte sur la même ligne (puces, titres de cartes) au lieu d'occuper seule un bouton
export function icon(name, size, inline = false) {
  const [def, shapes] = ICONS[name];
  const svg = document.createElementNS(NS, 'svg');
  svg.setAttribute('class', inline ? 'glyph inline' : 'glyph');
  svg.setAttribute('viewBox', '0 0 12 12');
  svg.setAttribute('width', size ?? def);
  svg.setAttribute('height', size ?? def);
  svg.setAttribute('aria-hidden', 'true');
  for (const [tag, attrs] of shapes) {
    const s = svg.appendChild(document.createElementNS(NS, tag));
    for (const [k, v] of Object.entries(attrs)) s.setAttribute(k, v);
  }
  return svg;
}

// Icône suivie d'un texte dans un élément : <div class="title"><svg/>texte</div>
export function withIcon(node, name, text) {
  node.replaceChildren(icon(name, null, true), text);
  return node;
}

// Éléments statiques d'index.html marqués data-icon (taille facultative : data-size). Un bouton sans texte reçoit
// l'icône seule ; ailleurs (puce avec libellé, texte courant) elle se place en ligne, devant le contenu.
export function fillIcons(root = document) {
  for (const node of root.querySelectorAll('[data-icon]')) {
    const inline = node.tagName !== 'BUTTON' || node.textContent.trim() !== '';
    node.prepend(icon(node.dataset.icon, node.dataset.size ? +node.dataset.size : null, inline));
  }
}
