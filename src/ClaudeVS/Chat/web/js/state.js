// État partagé du panneau : un seul objet mutable, plus les tables du fil en cours.

export const state = {
  cwd: '',
  commands: [],             // commandes / renvoyées par initialize
  busy: false,
  interrupted: false,       // le prochain « result » vient d'une interruption volontaire
  currentAssistant: null,   // conteneur du tour en cours
  currentMessageId: null,   // id du message API en cours de streaming
  stick: true,              // le fil suit le bas tant que l'utilisateur n'est pas remonté
  darkTheme: true,
  keepClasses: false,       // vrai le temps de nettoyer un SVG mermaid, dont le style repose sur ses classes
  rewindCard: null,
  images: [],               // { mediaType, data (base64), url (aperçu) }
  // suggestions « / » et « @ »
  popupItems: [],
  popupIndex: 0,
  fileSeq: 0,
  fileQuery: null,
  fileTimer: null,
  pendingFileSearch: null,
};

export const messages = new Map();  // id message API → { el, blocks: Map(index → bloc) }
export const streamed = new Set();  // messages dont le texte est arrivé en streaming
export const tools = new Map();     // tool_use_id → élément
export const perms = new Map();     // request_id → carte de permission
export const subTools = new Map();  // tool_use_id d'un outil de sous-agent → ligne
