# ClaudeVS

Extension Visual Studio (2022 à partir de 17.14, et 2026) qui intègre Claude Code à Visual Studio :

- un **panneau de chat** qui pilote le CLI `claude` ;
- un **pont IDE** compatible avec le protocole de l'extension VS Code, pour le CLI lancé dans un terminal.

## Prérequis

- Visual Studio 2026, ou Visual Studio 2022 17.14 ou plus récent.
- Claude Code installé : `irm https://claude.ai/install.ps1 | iex` ou `npm install -g @anthropic-ai/claude-code`. Redémarrer VS ensuite, le `PATH` n'est lu qu'au démarrage.
- Le CLI est cherché dans cet ordre : `CLAUDEVS_CLAUDE_PATH`, le `PATH`, `~/.local/bin`, `~/claude`, le dossier npm global, puis le binaire de l'extension VS Code. **Sortie › Claude** indique celui retenu.

## Compiler et installer

Build de développement, déployé dans l'instance expérimentale (la fermer avant : le déploiement échoue si elle est ouverte) :

```powershell
& "C:\Program Files\Microsoft Visual Studio\18\Professional\MSBuild\Current\Bin\MSBuild.exe" src\ClaudeVS\ClaudeVS.csproj -restore -p:Configuration=Debug
```

Lancer l'instance expérimentale avec le panneau ouvert :

```powershell
& "C:\Program Files\Microsoft Visual Studio\18\Professional\Common7\IDE\devenv.exe" /rootsuffix Exp /Command Claude.OpenChat
```

Paquet pour le VS principal : `.\pack.ps1` produit `dist\ClaudeVS-<version>.vsix`, à double-cliquer. L'installeur propose VS 2022 et VS 2026.

Sur VS 2022, les outils VSSDK 18 ne déploient que dans VS 2026. Il faut alors installer le VSIX de `src\ClaudeVS\bin\Debug\net472\` à la main dans l'instance expérimentale, puis fusionner la configuration :

```powershell
& "C:\Program Files\Microsoft Visual Studio\2022\Professional\Common7\IDE\VSIXInstaller.exe" /quiet /rootSuffix:Exp "src\ClaudeVS\bin\Debug\net472\ClaudeVS.vsix"
& "C:\Program Files\Microsoft Visual Studio\2022\Professional\Common7\IDE\devenv.exe" /rootsuffix Exp /updateConfiguration
```

## Utilisation

**Ouvrir le panneau** : Ctrl+Maj+Alt+C, ou **Affichage › Autres fenêtres › Claude**, ou **Outils › Claude**.

- **Envoyer** : Entrée. **Saut de ligne** : Maj+Entrée. **Interrompre** : Échap.
- **Réglages** en haut du panneau : modèle, niveau d'effort, mode de permission. Ils s'appliquent à chaud et ne modifient pas ta configuration Claude.
- **`@`** joint un fichier du dossier de travail. Images : Ctrl+V, glisser-déposer ou bouton 🖼.
- **Sélection de l'éditeur** : **Clic droit › Envoyer la sélection à Claude**, ou Ctrl+Alt+M. La puce 📎 indique ce qui sera joint.
- **Permissions** : Autoriser, Toujours autoriser (session), ou Refuser avec une consigne. Pour les modifications de fichiers, le diff natif de VS s'ouvre avec Accepter / Refuser. Une retouche faite à droite est transmise telle quelle.
- **↶** sur un message remet les fichiers modifiés par Claude dans l'état d'avant ce message. La conversation n'est pas touchée.
- **Contexte** : la jauge en bas du panneau indique le remplissage. **⇲** lance `/compact`.
- **🕘** reprend une conversation précédente, **＋** en démarre une nouvelle.
- **Outils VS pour le modèle** (serveur MCP `visualstudio`) : compilation, tests `runTests`, points d'arrêt, débogage (démarrer, pas à pas, variables, évaluation), navigation et renommage Roslyn en C# et VB, diagnostics, mise en forme, signets, fenêtre Sortie. Demander « sans les outils Visual Studio » est respecté.

### Claude dans un terminal

**Outils › Lancer Claude Code (terminal)**, ou lancer `claude` dans le dossier de la solution puis taper `/ide`. Les modifications s'ouvrent alors dans le diff de VS, et les diagnostics et la sélection sont transmis au CLI.

### Plusieurs instances de VS

Chaque instance charge sa propre copie de l'extension, avec son port, son jeton et son panneau : les conversations ne se mélangent pas. Évite en revanche de reprendre la même conversation dans deux panneaux à la fois.

## Architecture

```
VS (VSSDK, in-process)
 ├─ IdeServer       WebSocket 127.0.0.1 + jeton, JSON-RPC MCP
 │    ├─ /         client « IDE » du CLI terminal (découvert via ~/.claude/ide/<port>.lock)
 │    └─ /tools    serveur MCP « visualstudio » du CLI du panneau
 ├─ ChatToolWindow  WebView2 → Chat/web (HTML, JS, marked, DOMPurify)
 └─ ChatController  ⇄ ClaudeProcess : claude --input-format/--output-format stream-json
```

## Déboguer

- Journal de l'extension : **Sortie › Claude**, ou `%TEMP%\ClaudeVS\claudevs.log`.
- `CLAUDEVS_WEBVIEW_DEBUG_PORT=9333`, définie avant de lancer VS, expose le panneau au protocole DevTools (`edge://inspect`).
- Les fichiers de `src\ClaudeVS\Chat\web` peuvent être copiés dans le dossier de l'extension déployée, puis le panneau rechargé : inutile de redémarrer VS pour une modification d'interface.

## Limites connues

- Le protocole IDE et le protocole stream-json de Claude Code ne sont pas documentés : l'extension a été testée avec les CLI 2.1.294 et 2.1.295 et peut casser avec d'autres versions.
- Seules les images peuvent être jointes au message ; les autres fichiers passent par `@fichier`.
- Le retour arrière ne couvre que les modifications faites par Claude depuis le panneau, pas celles du terminal ni les tiennes.
- Les outils Roslyn ne couvrent que C# et VB, pour les projets chargés dans VS. Les outils de débogage sont testés surtout sur du C#.
- Sous VS lancé en administrateur avec un autre compte, le CLI n'est pas forcément dans le `PATH` : définir `CLAUDEVS_CLAUDE_PATH` au niveau système.
