# Passation — 12 octobre 2026

Note de reprise pour une nouvelle session. Le détail des revues est dans [REVUE-CODE.md](REVUE-CODE.md) et [CORRECTIONS.md](CORRECTIONS.md).

## 1. État du dépôt

- **`main`** : contient la revue et toutes ses corrections (jusqu'au commit `ffedbf3`).
- **`refonte/structure`** : refonte structurelle (4 vagues) + corrections issues des tests manuels. Compile sans erreur ni avertissement, 113 tests au vert. Fusionnée dans `main` le 12 octobre 2026.

## 2. Refonte structurelle (vagues 1 à 4)

| Vague | Commit | Contenu |
|---|---|---|
| 1 | `e20d56f` | ChatController découpé (7 fichiers), interface `IToolGroup`, injection du journal |
| 2 | `264c1ed` | Projet `ClaudeVS.Tests` (113 tests, fichiers sources liés), extraction de `TrxReport` |
| 3 | `6bb1411` | `app.js` → 11 modules ES (`Chat/web/js/`), constantes de protocole C#/JS, rendu streaming limité à 100 ms |
| 4 | `a0c4585` | Annotations de nullité dans les 13 fichiers nouveaux ou extraits |

Coût des sous-agents : ≈ 611 000 jetons. Reporté : SUG-08 (remplacement de Fleck).

## 3. Tests manuels de l'interface (instance expérimentale)

| Test | Statut | Corrections faites pendant le test |
|---|---|---|
| 1. Streaming, markdown, mermaid | ✔ | `<style>` du SVG mermaid de nouveau autorisé, limité aux règles `#id` du diagramme |
| 2. Interruption | ✔ | Résultat d'erreur d'un tour interrompu non affiché en rouge |
| 3. Permission, diff, retour arrière | ✔ | Confirmation verte en fin de fil + défilement jusqu'à elle |
| 4. Reprise d'une conversation | ✔ | — |
| 5. Carte de question, clavier | ✔ | — |
| 6. Messages envoyés pendant une réponse | ✔ | Messages fusionnés par le CLI : tous reçoivent l'uuid du tour |
| 7. Changement de solution (MAJ-14) | ✔ | Relance auto du CLI, dossier affiché à jour, plus de fausse alerte 30 s, bandeau orange avec décompte 5 s |
| 8. Mort de `claude.exe` | ✔ | Message d'erreur explicite |
| 9. Fermeture brutale de VS | ✔ | — (Job Object OK) |
| 10. `renameSymbol` en conflit | ✔ | Résultat d'outil MCP en chaîne : plus d'exception ; erreur d'outil = carte repliée + résumé ; chevron |
| 11. `runTests`, `openFile` | ✔ | `makeFrontmost=false` n'active plus l'onglet |
| 12. Plantage de WebView2 | ✔ | Annonce dans le bandeau orange |

Autres ajustements : contraste des textes secondaires (gris 70 %), icône « + » en SVG, carte de permission résorbée en étiquette dans l'outil, consigne système « ne pas recopier les résultats d'outils ».

### Tests 13 à 16

13 (retouche d'un diff : correction, l'acceptation depuis la carte du chat conserve les retouches), 14 (fichier verrouillé), 15 (grosse image), 16 (`debugStart` pendant une compilation) : tous passés. `refonte/structure` est fusionnée dans `main` (non poussée). Alertes « diff indisponible » et « Claude s'est arrêté » désormais en bandeau orange.

## 4. Mode opératoire des tests

- Lancer l'instance expérimentale avec le port DevTools (PowerShell) :
  ```
  $env:CLAUDEVS_WEBVIEW_DEBUG_PORT = '9333'
  Start-Process "C:\Program Files\Microsoft Visual Studio\18\Professional\Common7\IDE\devenv.exe" -ArgumentList '/rootsuffix Exp "F:\repositories\perso\Claude4VS\src\ClaudeVS\ClaudeVS.slnx"'
  ```
- Client DevTools : `%LOCALAPPDATA%\Temp\claudevs-cdp\cdp.ps1` (`-Expression`, `-Console`, `-Screenshot`, `-Method Page.crash`).
- **Fichiers web** : copie à chaud dans `%LOCALAPPDATA%\Microsoft\VisualStudio\18.0_4940ae17Exp\Extensions\nicolas.pernot\ClaudeVS (POC)\0.1.0\Chat\web\`, puis `location.reload()`.
- **C#** : fermer l'instance expérimentale (la DLL est verrouillée), compiler (le build déploie), relancer.

## 5. Évolutions demandées, à faire après les tests

Détail dans la mémoire du projet (`MEMORY.md`) :

1. Barre d'outils : libellés au-dessus des listes, liste des modèles fournie par le CLI (avec Fable et versions), modèle réel entre parenthèses.
2. Icônes SVG pour les 10 boutons et les emojis du contenu (option 2).
3. Bouton « copier » sur les blocs de code ; suppression de l'erreur `favicon.ico`.
4. Résumé d'une ligne (≤ 80 caractères, via `input.description`) sur les cartes de permission, commande longue rétractable.
5. Carte d'agent compacte (titre animé, « Règles » et actions rétractables) et badge des sous-agents en cours.
6. Historique ↑/↓ des demandes, en mémoire seulement.
7. Option « prévenir le modèle après un retour arrière » (≈ 30 jetons par retour arrière) : non décidée.

## 6. Conseils d'économie de jetons

- Repartir d'une conversation neuve à partir de cette note.
- Modèle Sonnet pour les tests ; pas de capture d'écran sauf nécessité ; croire l'état annoncé par l'utilisateur.
- Regrouper les tests et les corrections C# pour limiter les redémarrages de l'instance.
