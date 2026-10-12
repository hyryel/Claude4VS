// Interface du panneau Claude : rend le flux stream-json du CLI relayé par l'extension (ChatController).
// Point d'entrée : aucun autre module n'appelle de fonction importée pendant son évaluation ; tout est branché ici,
// une fois tous les modules évalués, et « ready » part en dernier.
import { state } from './state.js';
import { ToHost } from './protocol.js';
import { messagesEl, inputEl, nearBottom } from './dom.js';
import { setupMarkdown } from './markdown.js';
import { post, listen } from './host.js';
import { wireComposer } from './composer.js';
import { fillIcons } from './icons.js';

fillIcons();
setupMarkdown();
messagesEl.addEventListener('scroll', () => { state.stick = nearBottom(); });
listen();
wireComposer();

post({ type: ToHost.ready });
inputEl.focus();
