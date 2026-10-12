// Types des messages échangés avec l'extension (ChatController).
// À garder synchronisé avec Chat/Protocol.cs.

// Interface → extension
export const ToHost = Object.freeze({
  ready: 'ready',
  send: 'send',
  searchFiles: 'searchFiles',
  interrupt: 'interrupt',
  permission: 'permission',
  showDiff: 'showDiff',
  setMode: 'setMode',
  setModel: 'setModel',
  setEffort: 'setEffort',
  rewind: 'rewind',
  setAttachSelection: 'setAttachSelection',
  setAutoFormat: 'setAutoFormat',
  setAutoAnalyze: 'setAutoAnalyze',
  analyzeException: 'analyzeException',
  newChat: 'newChat',
  listSessions: 'listSessions',
  resume: 'resume',
  openFile: 'openFile',
});

// Extension → interface
export const FromHost = Object.freeze({
  cli: 'cli',
  status: 'status',
  error: 'error',
  permissionResolved: 'permissionResolved',
  cleared: 'cleared',
  history: 'history',
  insert: 'insert',
  selection: 'selection',
  focus: 'focus',
  notice: 'notice',
  debugEvent: 'debugEvent',
  context: 'context',
  usage: 'usage', // consommation du forfait, /usage (limits : plafonds ou null, cost : coût de la session)
  rewindPreview: 'rewindPreview',
  rewindDone: 'rewindDone',
  settings: 'settings',
  applied: 'applied', // modèle et effort réellement appliqués par le CLI
  settingApplied: 'settingApplied', // réponse du CLI à un réglage de la barre d'outils (later : appliqué au démarrage)
  sessions: 'sessions',
  files: 'files',
  theme: 'theme',
  banner: 'banner', // bandeau d'annonce en haut du panneau (text null = masquer)
});
