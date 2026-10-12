# ClaudeVS

Extension Visual Studio 2022 (17.14+) et 2026 (usage personnel) qui intègre Claude Code à VS :
- un **panneau de chat** qui pilote le CLI `claude` ;
- un **pont IDE** compatible avec le protocole de l'extension VS Code, pour le CLI lancé dans un terminal.

Voir aussi [ETUDE_FAISABILITE.md](ETUDE_FAISABILITE.md).

## Table des matières

- [Contenu](#contenu)
- [1. Compiler](#1-compiler)
- [2. Panneau de chat](#2-panneau-de-chat)
- [3. Claude dans un terminal](#3-claude-dans-un-terminal)
- [4. Architecture](#4-architecture)
- [5. Déboguer](#5-déboguer)
- [6. Limites connues](#6-limites-connues)

## Contenu

| Fonction | Où |
|---|---|
| Panneau de chat : streaming, Markdown, outils, permissions, questions de Claude, plans, historique | **Affichage › Autres fenêtres › Claude**, **Outils › Claude**, ou **Ctrl+Maj+Alt+C** |
| Choix du modèle, du niveau d'effort et du mode de permission, à chaud | barre du panneau |
| Jauge de remplissage du contexte et bouton de compactage (⇲) | bas du panneau |
| Retour arrière des fichiers à l'état d'avant un message (↶ sur la bulle) | panneau |
| Saisie : commandes `/`, autocomplétion `@fichier`, images collées (Ctrl+V), glissées ou jointes (🖼) | panneau |
| Activité des sous-agents (outils appelés, état) sous l'outil Agent | panneau |
| Modifications proposées affichées dans le **diff natif de VS** (Accepter / Refuser, retouche possible) | automatique |
| Sélection de l'éditeur jointe au message (puce 📎) | automatique |
| Outils VS pour le modèle : `getDiagnostics`, `getOpenEditors`, `getCurrentSelection`, `openFile`… | serveur MCP `visualstudio` |
| Compilation et débogage pilotés par le modèle : `build`, points d'arrêt, `debugStart`/`Stop`/`Continue`/`Step`/`Break`, état et pile, variables locales, évaluation, fenêtre Sortie | serveur MCP `visualstudio` |
| Tests pilotés par le modèle : `runTests` (xUnit, NUnit, MSTest), avec filtre et détail des échecs | serveur MCP `visualstudio` |
| Navigation sémantique Roslyn (C#/VB) : `findSymbol`, `symbolInfo`, `findReferences`, `documentSymbols`, `semanticDiagnostics`, et renommage de toute la solution `renameSymbol` | serveur MCP `visualstudio` |
| CLI dans une console externe, connecté à VS | **Outils › Lancer Claude Code (terminal)** |
| @-mention du fichier et des lignes sélectionnés | **Clic droit › Envoyer la sélection à Claude**, ou **Ctrl+Alt+M** |
| Journal de l'extension | **Sortie › Claude**, et `%TEMP%\ClaudeVS\claudevs.log` |

[⬆ Retour à la table des matières](#table-des-matières)

## 1. Compiler

Prérequis :
- VS 2026, ou VS 2022 à partir de 17.14. Le workload « Développement d'extensions » n'est pas indispensable : les outils VSSDK viennent de NuGet. Le projet est compilé contre les versions de Roslyn (4.14) et de WebView2 (1.0.2903) de VS 2022 ; chaque VS redirige vers sa propre version à l'exécution, et aucune de ces DLL n'est embarquée dans le VSIX.
- Claude Code installé sur le poste (`irm https://claude.ai/install.ps1 | iex`, ou `npm install -g @anthropic-ai/claude-code`), puis VS redémarré (le `PATH` est lu au démarrage). Le CLI est cherché dans cet ordre : la variable d'environnement `CLAUDEVS_CLAUDE_PATH`, le `PATH` (`claude.exe`, ou le `claude.cmd` d'npm, lancé via `node`), `~/.local/bin`, `~/claude` (et `~/claude/bin`), le dossier npm global, puis le binaire embarqué par l'extension VS Code. La fenêtre **Sortie › Claude** indique lequel a été retenu.

```bash
"/c/Program Files/Microsoft Visual Studio/18/Professional/MSBuild/Current/Bin/MSBuild.exe" src/ClaudeVS/ClaudeVS.csproj -restore -p:Configuration=Debug
```

La compilation produit `src/ClaudeVS/bin/Debug/net472/ClaudeVS.vsix` et déploie l'extension dans l'**instance expérimentale**. Le déploiement échoue si cette instance est ouverte : il faut la fermer avant.

Pour lancer l'instance expérimentale avec le panneau déjà ouvert :

```bash
"/c/Program Files/Microsoft Visual Studio/18/Professional/Common7/IDE/devenv.exe" /rootsuffix Exp /Command Claude.OpenChat
```

Pour l'installer dans le VS principal, double-cliquer sur le `.vsix` (l'installeur propose VS 2022 et VS 2026).

### Instance expérimentale de VS 2022

Les outils de build VSSDK 18 ne déploient que dans VS 2026. Pour VS 2022, installer le VSIX à la main dans l'instance expérimentale, puis fusionner la configuration (sinon le package n'est pas chargé) :

```bash
"/c/Program Files/Microsoft Visual Studio/2022/Professional/Common7/IDE/VSIXInstaller.exe" //quiet //rootSuffix:Exp "F:\repositories\perso\ClaudeVS\src\ClaudeVS\bin\Debug\net472\ClaudeVS.vsix"
```

```bash
"/c/Program Files/Microsoft Visual Studio/2022/Professional/Common7/IDE/devenv.exe" //rootsuffix Exp //updateConfiguration
```

(Les doubles barres sont pour Git Bash, qui transformerait `/quiet` en chemin ; depuis PowerShell, une seule barre suffit.) Attention au commutateur `/log` de devenv : il prend un nom de fichier en argument et écrase ce fichier.

[⬆ Retour à la table des matières](#table-des-matières)

## 2. Panneau de chat

- **Envoyer** : Entrée. **Saut de ligne** : Maj+Entrée. **Interrompre** : Échap ou ■ Arrêter.
- **`@`** propose les fichiers du dossier de travail. Les dossiers `bin`, `obj`, `.git`, `.vs` et `node_modules` sont exclus. Le CLI joint le contenu du fichier mentionné.
- **Images** : coller (Ctrl+V), glisser-déposer, ou bouton 🖼. Au-delà de 1568 px, elles sont réduites avant l'envoi.
- **Sous-agents** : leurs appels d'outils s'affichent sous l'outil Agent pendant qu'ils travaillent. La liste se replie à la fin.
- **Raccourcis** : Ctrl+Maj+Alt+C ouvre le panneau et place le curseur dans la saisie ; Ctrl+Alt+M y insère une @-mention du fichier et des lignes sélectionnés (si le panneau est fermé et qu'un CLI de terminal est connecté, la mention part vers ce CLI). Modifiables dans Outils › Options › Clavier, commandes `Claude.OpenChat` et `Claude.SendSelection`.
- **Modèle**, **niveau d'effort** (faible à maximal) et **mode de permission** (Demander, Accepter les modifications, Plan, Tout autoriser) se choisissent en haut. Les changements s'appliquent à chaud ; l'effort choisi est un réglage de session, pas une modification de tes paramètres Claude.
- **Contexte** : la jauge en bas à droite indique le remplissage de la fenêtre de contexte (détail par catégorie au survol). Le bouton ⇲ lance `/compact`, qui résume la conversation pour libérer de la place.
- **Compilation et débogage** : le modèle dispose d'outils `mcp__visualstudio__*` qui pilotent VS. Sans confirmation : `build` (renvoie les erreurs), `breakpointAdd` / `breakpointRemove` / `breakpointList`, `debugState` (mode, position, pile, raison de l'arrêt, exception), `debugLocals`, `getOutput` (volets Déboguer ou Build). Avec confirmation dans le panneau : `debugStart` (F5, avec choix du projet de démarrage), `debugStop`, `debugContinue`, `debugStep` (over / into / out), `debugBreak` et `debugEvaluate` (une expression peut avoir des effets de bord). `debugStart` et `debugContinue` attendent que le programme s'arrête ou se termine (délai réglable, 30 s par défaut). Exemple de demande : « pose un point d'arrêt ligne 42 de Facture.cs, lance le débogage et dis-moi la valeur de `total` ».
- **Priorité aux outils de l'IDE** : le serveur `visualstudio` est déclaré `alwaysLoad`, pour que ses outils soient visibles du modèle dès le départ (sinon le CLI les charge en différé et le modèle les ignore), et le panneau ajoute au prompt système une consigne de préférer ces outils aux grep, lectures et modifications fichier par fichier. C'est une préférence, pas une règle : demander explicitement une autre méthode (« sans les outils Visual Studio », « à la main, fichier par fichier ») est respecté. Le CLI lancé dans un terminal n'a pas cette consigne.
- **Navigation et refactorisation Roslyn** (C# et VB), pour économiser la lecture de fichiers : `findSymbol` (recherche de types et membres par nom dans toute la solution), `symbolInfo` (signature, type conteneur, bases et interfaces, documentation, emplacement de la définition), `findReferences` (toutes les utilisations, sémantiques et non textuelles, avec la ligne de code), `documentSymbols` (plan d'un fichier), `semanticDiagnostics` (erreurs et avertissements avec leurs plages, sans compiler). Un symbole se désigne par son nom (`Facture`, `Facture.Total`) ou par `file` + `line`. Tout cela sans confirmation. `renameSymbol` renomme dans toute la solution comme F2 dans VS (références, surcharges, commentaires, fichier si demandé), avec aperçu possible ; il demande confirmation, et s'annule par Ctrl+Z dans VS. Ces modifications passent par l'IDE, pas par les outils du CLI : le retour arrière ↶ ne les couvre pas.
- **Exceptions pendant le débogage** : quand VS s'arrête sur une exception en dehors d'un appel d'outil (par exemple après un F5 de ta part), le panneau affiche une carte ⚡ avec l'exception et la position. Si la puce **⚡ Exceptions** est active (par défaut) et que Claude est disponible, l'analyse lui est demandée aussitôt ; sinon un bouton « Analyser avec Claude » le fait à la demande. L'outil `debugWait` permet aussi au modèle d'attendre un arrêt plus longtemps que le délai de `debugStart`.
- **Formatage** : `formatDocument` applique « Mettre en forme le document » (Ctrl+K Ctrl+D) avec tes réglages et enregistre. La puce **✨ Format auto** (désactivée par défaut) le fait automatiquement sur chaque fichier modifié par Claude.
- **Signets** : `bookmarkList` (fichier, ligne, texte de la ligne) et `bookmarkToggle`. VS n'a pas d'API publique pour les signets : la liste est obtenue en parcourant `Edit.NextBookmark`, ce qui ouvre brièvement les fichiers concernés avant de les refermer.
- **Diagrammes** : les blocs ```` ```mermaid ```` des réponses sont rendus en graphique (diagrammes de classes, séquences, flux…), dans le thème du panneau.
- **Tests** : `runTests` lance `dotnet test` en arrière-plan avec un rapport TRX et renvoie les compteurs (réussis, échoués, ignorés) et, pour chaque échec, le message et la pile. Paramètres : projet ou solution (par défaut la solution ouverte), filtre `dotnet test` (`FullyQualifiedName~Facture`), `noBuild`. Sans confirmation, comme la compilation. Les résultats n'apparaissent pas dans l'Explorateur de tests de VS, dont l'API n'est pas publique.
- **Retour arrière** : au survol d'un de tes messages, ↶ propose de remettre les fichiers dans l'état où ils étaient juste avant ce message. Un aperçu liste les fichiers concernés avant confirmation. Seuls les fichiers sont restaurés, la conversation est conservée : il faut ensuite dire à Claude ce qu'on attend. Les points de sauvegarde sont pris par le CLI à chaque modification faite depuis le panneau.
- **Permissions** : une carte apparaît sous l'outil concerné, avec trois choix :
  - **Autoriser** ;
  - **Toujours autoriser (session)** ;
  - **Refuser**, avec une consigne facultative. La consigne interrompt le tour et part comme un message de l'utilisateur.
- **Modifications de fichiers** (Edit / MultiEdit / Write) : le diff s'ouvre aussi dans VS.
  - Accepter ou Refuser dans la barre du diff répond à la permission.
  - Une retouche faite dans la partie droite est transmise telle quelle.
  - Fermer l'onglet du diff ne décide rien.
- **Contexte** : la puce 📎 indique le fichier et les lignes joints. Cliquer dessus active ou désactive l'envoi de la sélection.
- **🕘** reprend une conversation précédente du dossier (avec son historique). **＋** en démarre une nouvelle.
- Le CLI tourne dans le dossier de la solution ouverte, ou dans le profil utilisateur si aucune solution n'est ouverte. Il utilise ta configuration habituelle : `CLAUDE.md`, skills, MCP, hooks, abonnement.

### Plusieurs instances de Visual Studio

Chaque instance de VS charge sa propre copie de l'extension : son serveur (port et jeton distincts), son fichier de verrou, son panneau et son propre processus `claude` branché sur son port. Les conversations ne peuvent pas se mélanger.

- Le CLI lancé par **Outils › Lancer Claude Code (terminal)** vise l'instance qui l'a lancé. Un `claude` lancé à la main choisit l'instance dont le dossier de solution contient son répertoire courant ; si deux instances ont la même solution ouverte, `/ide` propose de choisir.
- L'historique (🕘) est celui du CLI, rangé par dossier de solution : deux instances ouvertes sur la même solution voient la même liste. Éviter de reprendre la même conversation dans deux panneaux à la fois.
- Les fichiers de verrou d'instances fermées brutalement sont supprimés au démarrage suivant (le CLI les ignore de toute façon, il vérifie le PID).

[⬆ Retour à la table des matières](#table-des-matières)

## 3. Claude dans un terminal

1. Ouvrir une solution, puis lancer **Outils › Lancer Claude Code (terminal)**. Autre possibilité : lancer `claude` dans le dossier de la solution, puis taper `/ide`.
2. Les modifications s'ouvrent dans le diff de VS, avec Accepter / Refuser. Les diagnostics et la sélection sont transmis au CLI.

[⬆ Retour à la table des matières](#table-des-matières)

## 4. Architecture

```
VS 2026 (package VSSDK in-process)
 ├─ IdeServer       WebSocket 127.0.0.1 + jeton, MCP JSON-RPC
 │    ├─ /          client « IDE » (CLI du terminal, découvert via ~/.claude/ide/<port>.lock)
 │    └─ /tools     serveur MCP « visualstudio » pour le CLI du panneau (outils limités)
 ├─ ChatToolWindow  WebView2 → Chat/web (HTML/JS, marked + DOMPurify)
 └─ ChatController  ⇄ ClaudeProcess : claude --input-format/--output-format stream-json
                       --permission-prompt-tool stdio --mcp-config <visualstudio>
```

[⬆ Retour à la table des matières](#table-des-matières)

## 5. Déboguer

- La variable `CLAUDEVS_WEBVIEW_DEBUG_PORT=9333`, définie avant de lancer VS, expose le panneau au protocole DevTools. On peut alors l'inspecter depuis `edge://inspect`, ou le piloter par script.
- Les fichiers de `Chat/web` peuvent être copiés directement dans le dossier de l'extension déployée, puis le panneau rechargé : pas besoin de redémarrer VS pour une modification de l'interface.

[⬆ Retour à la table des matières](#table-des-matières)

## 6. Limites connues

- Le protocole IDE et le protocole stream-json ne sont pas documentés. Tests faits contre le CLI 2.1.294 / 2.1.295.
- `getDiagnostics` lit l'Error List : seule la position de début est connue.
- Seules les images peuvent être jointes. Les autres fichiers passent par `@fichier`.
- Si VS tourne **en administrateur sous un autre compte** que celui qui a installé Claude Code, le CLI n'est pas dans son `PATH` ni dans son `~/.local/bin` : définir `CLAUDEVS_CLAUDE_PATH` au niveau système, ou installer Claude Code depuis ce compte. Le dossier de données WebView2 est séparé par instance de VS et par niveau d'élévation.
- Si l'extension VSColorOutput est installée et qu'un `.csproj` est ouvert seul (sans `.sln`), VS peut afficher un bandeau « ArgumentException » venant de VSColorOutput lors de la première lecture de la fenêtre Sortie. Sans conséquence ; n'arrive pas avec une solution.
- Les outils Roslyn ne couvrent que C# et VB, et seulement les projets chargés dans VS. VS 2022 17.14 ne charge pas les projets .NET 10.
- Les outils de débogage reposent sur l'automatisation de VS (DTE) : testés sur du C# ; l'inspection des variables est moins complète en natif. Un programme qui attend une saisie console reste bloqué jusqu'à ce que tu la fournisses.
- Le retour arrière ne couvre que les modifications faites par Claude depuis le panneau (pas celles du CLI en terminal, ni les tiennes), et pas la conversation elle-même.
- L'index des fichiers pour `@` est rafraîchi au plus toutes les 30 s et ne lit pas le `.gitignore`.
- Les requêtes de contrôle autres que les permissions (elicitation MCP, callbacks de hooks SDK) reçoivent une réponse d'erreur.

[⬆ Retour à la table des matières](#table-des-matières)
