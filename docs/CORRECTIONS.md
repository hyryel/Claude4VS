# Compte rendu des corrections

Date : 12 octobre 2026
Branche : `revue/corrections`
Référence : [REVUE-CODE.md](REVUE-CODE.md)

## 1. Synthèse

Tous les constats de la revue ont été traités : 14 majeurs, 58 mineurs et les 4 suggestions de petite taille retenues (SUG-09, 10, 12, 13). Les suggestions structurelles (SUG-01 à 08, SUG-11) sont reportées à une passe dédiée, comme convenu.

| État | Nombre |
|---|---|
| Corrigé | 71 |
| Corrigé différemment (justification ci-dessous) | 5 |
| Non corrigé | 0 |

La relecture indépendante du lot a remonté 4 régressions majeures et 7 points mineurs. Tous sont corrigés sauf un, sans correction nécessaire (section 4).

La solution compile en Release sans erreur ni avertissement. La syntaxe de `app.js` est vérifiée avec `node --check`. **Aucun test fonctionnel n'a été fait dans Visual Studio** : le projet n'a pas de tests automatisés. La section 6 liste les vérifications manuelles à faire.

## 2. Organisation

| Équipe | Périmètre | Commit |
|---|---|---|
| Agent A · Chat C# | `ChatController`, `ClaudeProcess`, `ChatToolWindow`, `FileIndex`, nouveau `JobObject.cs` | `61bb488` |
| Agent B · Front web | `app.js`, `index.html`, `style.css` | `4d2a679` |
| Agent C · Outils IDE | `IdeServer`, `IdeTools`, `RoslynTools`, `DebugTools`, `EditorTools`, `TestTools`, `DiffManager`, `SelectionTracker`, `DebugMonitor`, `ClaudeLauncher`, `ClaudeVSPackage`, `.csproj`, nouveaux `CommandLine.cs` et `PathResolver.cs` | `e748266` |
| Agent D · Relecteur | Diff complet, en lecture seule | (constats intégrés dans les commits ci-dessus) |
| Intégration | `ClaudeProcess` raccordé à `CommandLine`, double affichage des erreurs de contrôle, corrections de la relecture | inclus dans les trois commits |
| MAJ-14, traité ensuite | `ChatController` | `f5e2ec3`, puis `c73b955` |

Les deux contrats de protocole fixés à l'avance sont implémentés et vérifiés des deux côtés :

- **Permissions expirées.** Le C# envoie `permissionResolved` avec `behavior: "cancelled"`. Le JS désactive la carte et affiche « Demande expirée ».
- **Historique par lots.** Le C# envoie plusieurs messages `history` de 50 éléments. Le dernier porte `last: true`, et le séparateur « conversation reprise » n'est affiché qu'une fois.

## 3. Constats de la revue

### Majeurs

| Id | État | Correction |
|---|---|---|
| MAJ-01 | Corrigé | `ClaudeProcess` met les messages dans une file. Un thread dédié les écrit sur stdin : le thread UI ne bloque plus jamais sur le pipe. |
| MAJ-02 | Corrigé | Un échec d'écriture lève l'événement `WriteFailed`. Le contrôleur affiche l'erreur et la fin de stderr, puis arrête le processus. Le statut « busy » n'est posté qu'après une mise en file réussie. |
| MAJ-03 | Corrigé | Helper `RunOnUi` : bascule sur le thread UI, try/catch, journal avec pile et erreur affichée dans le chat. Il est appliqué à toutes les lambdas de fond. |
| MAJ-04 | Corrigé | Le message est relayé à l'interface avant l'ouverture du diff. La lecture du fichier est protégée : en cas d'échec, une notice s'affiche et la carte reste utilisable. |
| MAJ-05 | Corrigé | Les références DOM sont résolues une seule fois. DOMPurify interdit `id`, `name`, `style`, les éléments de formulaire, les `data-*` et la balise `<style>`. `class` n'est gardé que sur `<code class="language-…">`. La CSP reçoit `form-action 'none'`. |
| MAJ-06 | Corrigé | CSP `img-src 'self' data:`. Une image distante dans une réponse s'affiche désormais cassée, sans être chargée. |
| MAJ-07 | Corrigé | Les bulles envoyées sont marquées « en attente d'uuid ». Le rejeu du CLI attribue l'uuid à la plus ancienne bulle en attente, dans l'ordre d'envoi. Les marqueurs sont retirés quand le processus s'arrête. |
| MAJ-08 | Corrigé | Le rendu différé est annulé par `cancelAnimationFrame` dans `finalize`. |
| MAJ-09 | Corrigé différemment | Le drapeau `interrupted` est réinitialisé sur « idle », sur « ready » hors tour, et à l'effacement. Il n'est pas réinitialisé sur « ready » en fin de tour, car le C# poste ce statut avant le `result` qui affiche « interrompu ». L'erreur propre à l'interruption n'est plus affichée en rouge. |
| MAJ-10 | Corrigé | Le jeton est vérifié à chaque message, pas seulement à l'ouverture. Les connexions authentifiées sont mémorisées sous verrou et retirées à la fermeture. |
| MAJ-11 | Corrigé | Pendant le build de lancement, l'attente du débogueur continue au lieu de répondre « Not debugging ». `debugWait` accepte aussi un build encore en cours. |
| MAJ-12 | Corrigé | Logger TRX sans nom fixe. Tous les fichiers TRX sont agrégés : compteurs sommés, échecs concaténés. |
| MAJ-13 | Corrigé | Nouveau `PathResolver` unique, utilisé par les cinq groupes d'outils. Un chemin relatif sans solution ouverte donne désormais une erreur claire. |
| MAJ-14 | Corrigé | À l'ouverture d'une solution ou d'un dossier, le CLI est relancé dans le nouveau dossier. Si Claude répond, la bascule attend la fin du tour. Une simple fermeture de solution ne déplace pas le CLI. |

### Mineurs · gestion des erreurs et états

| Id | État | Correction |
|---|---|---|
| MIN-01 | Corrigé | Try/catch dans le timer de sélection ; `Uri.TryCreate`. |
| MIN-02 | Corrigé | Try/catch autour des deux dispatchs JS, erreur discrète affichée. `renderMd` se replie sur du texte brut échappé. |
| MIN-03 | Corrigé | `ClearPendingPermissions` est appelé à l'arrêt, au `result` et sur `control_cancel_request`. Il ferme le diff et notifie l'interface. |
| MIN-04 | Corrigé | Notice si le CLI s'arrête avec le code 0 en plein tour. |
| MIN-05 | Corrigé différemment | Sans processus, le retour arrière affiche « impossible » avec une explication dans sa propre carte, plutôt qu'une erreur générique. |
| MIN-06 | Corrigé | La fin de stderr est attendue, deux secondes au plus, avant de signaler la sortie du processus. |
| MIN-07 | Corrigé | La fin du compactage rétablit le statut réel. |
| MIN-08 | Corrigé | Un refus du CLI s'affiche en erreur. Limite : le sélecteur n'est pas remis à l'ancienne valeur, car la réponse ne dit pas quel réglage a été refusé. |
| MIN-09 | Corrigé différemment | Revenir à « effort par défaut » pendant une conversation affiche une notice : la valeur s'appliquera à la prochaine conversation. Le comportement du CLI avec une valeur nulle n'est pas documenté. |
| MIN-10 | Corrigé | Le statut de `doc.Save()` est lu. Une annulation ou un échec est signalé. |
| MIN-11 | Corrigé | Indicateur `timedOut` et erreurs filtrées par projet. Limite : la liste d'erreurs de VS ne distingue pas build et IntelliSense. |
| MIN-12 | Corrigé | Annulation à la déconnexion du client. Timeout de 120 s, porté à 10 min pour le renommage et les diagnostics de toute la solution. |
| MIN-13 | Corrigé | Un renommage qui introduit des erreurs de compilation est refusé. La vérification est limitée aux renommages de 25 fichiers au plus ; au-delà, une note invite à compiler. |
| MIN-14 | Corrigé | Arbre de processus tué sur timeout, nettoyage en `finally`, message clair si `dotnet` est absent. |
| MIN-15 | Corrigé | `skipped = total - passed - failed`. |
| MIN-16 | Corrigé | Exceptions attrapées au bon endroit ; le processus doit s'appeler `devenv`. |
| MIN-17 | Corrigé | Notice si une image collée est illisible. |
| MIN-18 | Corrigé | Les `catch` vides journalisent. Seule exception, le parse ligne à ligne des sessions, qui reste silencieux volontairement. |

### Mineurs · cycle de vie et ressources

| Id | État | Correction |
|---|---|---|
| MIN-19 | Corrigé | Le processus mort est libéré dans tous les chemins. |
| MIN-20 | Corrigé | Désabonnement du thème. Les envois après `Dispose` sont ignorés. |
| MIN-21 | Corrigé | Rechargement de la page si le moteur de rendu meurt, recréation du contrôle si le navigateur meurt, notice. Après rechargement, les permissions en attente sont réaffichées. |
| MIN-22 | Corrigé | File d'attente ignorée après un échec d'initialisation, et bornée à 500 messages. |
| MIN-23 | Corrigé | L'arrêt du processus se fait en arrière-plan, sans bloquer le thread UI. |
| MIN-24 | Corrigé | Job Object : si devenv meurt, le CLI et ses sous-processus meurent avec lui. |
| MIN-25 | Corrigé | Lecture des sessions hors thread UI, résultats d'outils tronqués à 20 000 caractères, envoi par lots. |
| MIN-26 | Corrigé | Le fichier de configuration MCP, qui contient le jeton, est supprimé à l'arrêt. |
| MIN-27 | Corrigé | Purge des dossiers de diff de plus de deux jours, rotation du journal à 5 Mo. Limite : l'écriture du journal reste synchrone. |
| MIN-28 | Corrigé | Objet `Dte` mis en cache. |
| MIN-29 | Corrigé | Projet de démarrage restauré si le lancement échoue. L'effet de bord est documenté dans la description de l'outil. |
| MIN-30 | Corrigé | Restauration en `finally` dans `ListBookmarks`. |

### Mineurs · robustesse

| Id | État | Correction |
|---|---|---|
| MIN-31 à 34 | Corrigé | Tests de nullité sur la vue WPF, la vue texte et la fenêtre de diff ; try/catch au lancement du terminal. |
| MIN-35 | Corrigé | `switch` explicite : les retouches faites dans le diff ne sont plus perdues sur un fichier vide. |
| MIN-36 | Corrigé | Le formatage automatique ne s'applique qu'aux résultats d'un outil Edit, Write ou MultiEdit identifié. |
| MIN-37 | Corrigé | Index de fichiers : une construction périmée ne publie pas ses résultats. |
| MIN-38 | Corrigé | Nouveau `CommandLine` partagé par `ClaudeProcess`, `TestTools` et `ClaudeLauncher`. |
| MIN-39 | Corrigé | `CSS.escape` sur l'uuid. |
| MIN-40 | Corrigé | Timer de recherche `@` annulé à la fermeture de la popup. |
| MIN-41 | Corrigé différemment | Le SVG mermaid passe par DOMPurify, avec `foreignObject` autorisé. Sans cela, les libellés des diagrammes disparaissent. |

### Mineurs · exactitude des outils

| Id | État | Correction |
|---|---|---|
| MIN-42 | Corrigé | `makeFrontmost: false` ne vole plus le focus. `preview: true` ouvre un onglet provisoire : le paramètre est implémenté plutôt que retiré. |
| MIN-43 | Corrigé | `count` après filtrage, indicateur `truncated`. |
| MIN-44 | Corrigé | La description précise que les analyseurs ne sont pas exécutés. |
| MIN-45 | Corrigé | `IsVisible` décide si le panneau est ouvert. |
| MIN-46 | Corrigé différemment | Le chat n'est plus réaffiché aux points d'arrêt ni aux pas, seulement sur une exception. Limite : au lancement du débogage, une fermeture volontaire du panneau n'est pas mémorisée. |

### Mineurs · lisibilité et code mort

| Id | État | Correction |
|---|---|---|
| MIN-47, 48 | Corrigé | Condition et regex simplifiées dans `RoslynTools`. |
| MIN-49 | Corrigé | Branche `contextUsage` supprimée. |
| MIN-50 | Corrigé | `DebugMonitor` partage l'instance `DebugTools` ; le compteur n'est plus statique. |
| MIN-51 | Corrigé | `ReadPane` sépare la lecture du formatage MCP. |
| MIN-52 | Corrigé | `RefreshWorkspace` réutilise `Tools`. |
| MIN-53, 54 | Corrigé | Nettoyage JS (masquage de nom, déclarations, propriétés expando, duplications) et CSS (règles fusionnées). |
| MIN-55 | Corrigé | Palette sémantique dédiée au thème clair. |
| MIN-56 | Corrigé | Rôles, `tabindex`, Entrée et Espace, `aria-label`, `aria-expanded`, `aria-pressed`, listbox, focus visible. |
| MIN-57 | Corrigé | Questions indexées par position, « Autre » et options exclusifs, « Répondre » désactivé tant qu'une question est sans réponse. |
| MIN-58 | Corrigé | Titre du package corrigé, `using` inutilisé supprimé. |

### Suggestions retenues

| Id | État | Correction |
|---|---|---|
| SUG-09 | Corrigé | Outils de développement WebView2 seulement en Debug, ou si la variable de débogage est définie. |
| SUG-10 | Corrigé | Notice si le CLI n'a pas répondu à l'initialisation au bout de 30 s. |
| SUG-12 | Corrigé | Mode, effort et modèle validés par liste blanche ou expression régulière avant de les passer au CLI. |
| SUG-13 | Corrigé | Accueil restauré, `rel()` accepte `/` et `\`, hauteur de la saisie recalculée, classe `busy` à la place de `:has()`. |

## 4. Relecture indépendante du lot

| Constat | Gravité | Correction |
|---|---|---|
| Uuid attribué à la dernière bulle en attente : inversion avec deux messages en file | Majeur | Attribution à la plus ancienne bulle en attente ; marqueurs retirés quand le processus s'arrête. |
| Tour interrompu affiché aussi en erreur rouge | Majeur | Le `result` d'une interruption n'est plus affiché en erreur. |
| Vérification des conflits de renommage trop coûteuse sous le timeout de 120 s | Majeur | Vérification limitée à 25 fichiers ; timeout porté à 10 min pour le renommage et les diagnostics globaux. |
| `debugStart` sur build long : note fausse, projet de démarrage restauré trop tôt, `debugWait` refusé | Majeur | Pas de restauration ni d'annotation tant que le build tourne ; `debugWait` accepte le build en cours. |
| Reprise de session : statut « travaille » résiduel, message envoyé pendant le chargement | Mineur | Statut « idle » dès l'effacement ; notice si un processus a démarré pendant le chargement. |
| Permissions orphelines après rechargement WebView2 ; rechargement sur simple lenteur | Mineur | Permissions réaffichées au `ready` ; plus de rechargement sur simple lenteur du rendu. |
| Course `Authenticate` / `OnClose`, sources d'annulation jamais libérées | Mineur | Test refait sous verrou ; `Dispose` après annulation. |
| Balise `<style>` encore autorisée | Mineur | Ajoutée aux balises interdites. |
| `aria-live` sur le fil en streaming | Mineur | Retiré : le texte en streaming aurait été relu en boucle. |
| Arrêt du processus devenu asynchrone | Mineur | Pas de correction : aucun effet concret identifié. |
| Erreur affichée si le CLI refuse une interruption tardive | Mineur | L'interruption passe par une requête de contrôle dont la réponse est consommée en silence. |

## 5. Nouveaux fichiers

| Fichier | Rôle |
|---|---|
| `src/ClaudeVS/CommandLine.cs` | Échappement des arguments selon les règles de `CommandLineToArgvW`. |
| `src/ClaudeVS/PathResolver.cs` | Résolution unique des chemins relatifs : dossier de la solution, sinon du premier projet. |
| `src/ClaudeVS/Chat/JobObject.cs` | Job Object Windows qui tue le CLI et ses sous-processus si devenv meurt. |

## 6. Vérifications manuelles à faire

Dans l'instance expérimentale de Visual Studio :

### Chat et processus

1. Coller une grosse image et l'envoyer : l'interface reste réactive.
2. Tuer `claude.exe` pendant un tour : erreur affichée, statut au repos, cartes de permission marquées « expirée ».
3. Demande de modification sur un fichier verrouillé : la carte s'affiche, avec une notice « diff indisponible ».
4. Retoucher le diff d'une modification sur un fichier vide, puis accepter : les retouches sont appliquées.
5. Reprendre une grosse session : pas de gel, un seul séparateur « conversation reprise ».
6. Tuer le processus `msedgewebview2.exe` du panneau : le panneau revient, les permissions en attente sont réaffichées.
7. Tuer devenv brutalement : `claude.exe` disparaît aussi.
8. Ouvrir une autre solution, avec et sans tour en cours : le CLI bascule dans le nouveau dossier.

### Front web

9. Diagramme mermaid en fin de réponse : affiché avec ses libellés, en thème sombre et en thème clair.
10. Échap pendant un tour : « interrompu » s'affiche, sans erreur rouge.
11. Deux messages envoyés pendant un tour : chacun reçoit son propre bouton de retour arrière.
12. Question à choix multiples : « Répondre » est grisé tant qu'il manque une réponse.
13. Navigation au clavier sur les en-têtes d'outils et la liste des sessions.

### Outils IDE

14. `debugStart` sur un projet à compiler longtemps : l'attente continue pendant le build.
15. `runTests` sur une solution avec deux projets de tests : les compteurs sont additionnés.
16. `renameSymbol` vers un nom déjà pris : refusé, avec la liste des erreurs.
17. `openFile` avec `makeFrontmost: false` : le focus reste dans l'éditeur courant.
