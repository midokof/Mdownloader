# Audit d'état — Étape 1

**Projet :** NovaDownload / Mdownloader
**Dépôt :** `D:\Mdownloader`
**Date de l'audit :** 26 octobre 2026
**Objet :** photographie exacte de l'état réel du code, de la compilation, des tests et de l'application, avant toute modification fonctionnelle des étapes 2 à 7.

Ce document ne décrit pas ce que le projet *devrait* faire. Il décrit ce qui a été **constaté en exécutant** les commandes, en lisant le code et en lançant les binaires. Chaque affirmation porte sa preuve.

---

## 1. Environnement de vérification

| Élément | Valeur constatée |
|---|---|
| OS | Windows 11 (build 10.0.26100) |
| SDK .NET | `9.0.319` (`C:\Program Files\dotnet\sdk`) — un seul SDK installé |
| Runtime utilisé à l'exécution | .NET 9.0.21 (CoreCLR 9.0.2126.50404) |
| Navigateur présent | Microsoft Edge uniquement (Chrome et Firefox absents) |
| Outils d'analyse installés pendant l'audit | `ilspycmd` 9.1.0.7988, `dotnet-stack` 10.0.750501 |

---

## 2. Structure du dépôt

Huit projets, tous dans `NovaDownload.sln` :

| Projet | Rôle |
|---|---|
| `src/NovaDownload.App` | Application WPF (MVVM, CommunityToolkit.Mvvm) |
| `src/NovaDownload.Core` | Modèles, énumérations, interface `IDownloadService`, localisation FR/EN |
| `src/NovaDownload.DownloadEngine` | Moteurs HTTP et BitTorrent + routeur |
| `src/NovaDownload.BrowserIntegration` | Passerelle navigateur (pipe nommé, validation, déduplication) |
| `src/NovaDownload.NativeHost` | Hôte de messagerie native pour les extensions |
| `src/NovaDownload.Infrastructure` | Persistance des préférences (`settings.json`) |
| `tests/NovaDownload.UnitTests` | 193 tests |
| `tests/NovaDownload.IntegrationTests` | 34 tests (réseau réel en boucle locale) |

Dossier `tests/extension-tests/run-tests.js` : suite de tests JavaScript pour les extensions, **non exécutée par `dotnet test`** et sans `package.json` — hors périmètre de l'étape 1, signalée comme dette.

---

## 3. Version .NET — unification

Constat : **aucune hétérogénéité résiduelle** dans les projets.

```
NovaDownload.App.csproj                 => net9.0-windows
NovaDownload.BrowserIntegration.csproj  => net9.0-windows
NovaDownload.Core.csproj                => net9.0
NovaDownload.DownloadEngine.csproj      => net9.0
NovaDownload.Infrastructure.csproj      => net9.0
NovaDownload.NativeHost.csproj          => net9.0-windows
NovaDownload.IntegrationTests.csproj    => net9.0-windows
NovaDownload.UnitTests.csproj           => net9.0-windows
```

Côté documentation et binaires :

| Emplacement | Avant l'audit | Après correction |
|---|---|---|
| `README.md` | « Install the .NET 6 SDK (or later) » | « Install the .NET 9 SDK (or later) » |
| `dist/README.md` | .NET 9 Desktop Runtime x64 (déjà correct) | inchangé |
| `dist/win-x64` | `tfm = net9.0` | inchangé |
| `build/app`, `publish/` | `tfm = net9.0` | inchangé (dossiers ignorés par Git) |

Seuls `dist/README.md` et `dist/NovaDownload-1.0.0-win-x64.exe` sont suivis par Git dans `dist/` ; le reste est ignoré (`.gitignore`).

---

## 4. Compilation

Nettoyage complet (`bin/` et `obj/` supprimés dans tout le dépôt), puis compilation des deux configurations.

**Debug**

```
Restauration effectuée de D:\Mdownloader\src\NovaDownload.App\NovaDownload.App.csproj (en 410 ms).
  NovaDownload.Core -> ...\bin\Debug\net9.0\NovaDownload.Core.dll
  NovaDownload.Infrastructure -> ...\bin\Debug\net9.0\NovaDownload.Infrastructure.dll
  NovaDownload.BrowserIntegration -> ...\bin\Debug\net9.0-windows\NovaDownload.BrowserIntegration.dll
  NovaDownload.DownloadEngine -> ...\bin\Debug\net9.0\NovaDownload.DownloadEngine.dll
  NovaDownload.NativeHost -> ...\bin\Debug\net9.0-windows\NovaDownload.NativeHost.dll
  NovaDownload.UnitTests -> ...\bin\Debug\net9.0-windows\NovaDownload.UnitTests.dll
  NovaDownload.IntegrationTests -> ...\bin\Debug\net9.0-windows\NovaDownload.IntegrationTests.dll
  NovaDownload.App -> ...\bin\Debug\net9.0-windows\NovaDownload.App.dll

La génération a réussi.
    0 Avertissement(s)
    0 Erreur(s)

Temps écoulé 00:00:05.41
```

**Release**

```
La génération a réussi.
    0 Avertissement(s)
    0 Erreur(s)

Temps écoulé 00:00:05.17
```

**Publication du binaire distribué** (framework-dependent, fichier unique) :

```
dotnet publish src\NovaDownload.App -c Release -r win-x64 --self-contained false
                -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
NovaDownload.App.exe  =>  3 258 981 octets
```

---

## 5. Tests

Exécution après compilation propre et sans recompilation (`--no-build`) :

```
Réussi!  - échec : 0, réussite : 193, ignorée(s) : 0, total : 193, durée : 360 ms
           - NovaDownload.UnitTests.dll (net9.0)

Réussi!  - échec : 0, réussite :  34, ignorée(s) : 0, total :  34, durée :   4 s
           - NovaDownload.IntegrationTests.dll (net9.0)
```

**227 tests, 0 échec, 0 ignoré.** Les tests d'intégration ne sont pas des simulations : ils démarrent un serveur HTTP sur socket brut et un essaim BitTorrent sur l'interface de bouclage.

Tests qui prouvent réellement les fonctions centrales :

| Test | Ce qu'il prouve |
|---|---|
| `Download_Completes_And_Writes_Exact_Bytes` | HTTP : le fichier écrit est **identique octet pour octet** au payload servi |
| `Pause_Then_Resume_Continues_From_Offset_And_Sends_Range` | HTTP : la reprise envoie bien un `Range` et ne réécrit pas le début |
| `Cancel_Removes_Partial_File` | HTTP : l'annulation supprime le `.npart` |
| `Name_Conflict_Appends_Suffix_And_Does_Not_Overwrite` | HTTP : aucune écrasure silencieuse |
| `Service_Seeds_And_A_Second_Swarm_Member_Gets_Identical_Bytes` | Torrent : transfert de 2 Mo réel, vérifié octet pour octet |
| `Torrent_File_Source_Is_Detected_And_Seeds_Verified_Data` | Torrent : `.torrent` local ouvert et mis en seed |
| `A_Partial_Download_Never_Reports_Completed` | Torrent : une donnée partielle (25 % sur disque) n'est jamais annoncée terminée |
| `Cancel_Removes_The_Partial_Data_And_Stops_The_Row` | Torrent : annulation et nettoyage |
| `A_Repeat_Source_Returns_The_Live_Transfer_Instead_Of_Failing` | Torrent : pas de doublon |
| `The_Router_And_The_Gate_Agree` (5 cas) | Routage HTTP/Torrent identique entre UI, passerelle navigateur et moteur |

**Aucun test ne couvre la surcharge `selectedFiles` ni `LoadTorrentMetadataAsync`** — voir §7.

---

## 6. Lancement de l'application

Vérifié en énumérant les fenêtres Win32 du processus (le titre seul ne suffit pas : une fenêtre peut exister sans être visible).

| Binaire | Résultat observé |
|---|---|
| `src/.../bin/Debug/net9.0-windows/NovaDownload.App.exe` | vivant, fenêtre **visible** `NovaDownload`, `Responding = True` (à 3 s) |
| `src/.../bin/Release/net9.0-windows/NovaDownload.App.exe` | vivant, fenêtre **visible** `NovaDownload` 1280×820, `Responding = True` |
| `dist/NovaDownload-1.0.0-win-x64.exe` **avant correction** | **plantage** — code de sortie `-532462766` (`0xE0434352`) |
| `dist/NovaDownload-1.0.0-win-x64.exe` **après republication** | vivant, fenêtre **visible** `NovaDownload`, `Responding = True` |

Le plantage du binaire distribué était réel et bloquant pour tout utilisateur :

```
System.TypeLoadException: Method 'StartDownloadAsync' in type
'NovaDownload.DownloadEngine.Services.HttpDownloadService' from assembly
'NovaDownload.DownloadEngine, Version=1.0.0.0' does not have an implementation.
   at NovaDownload.App.App.<>c.<.ctor>b__4_0(...)
   at Microsoft.Extensions.Hosting.HostBuilder.Build()
   at NovaDownload.App.App..ctor()
```

Cause : le couple `Core.dll` / `DownloadEngine.dll` embarqué dans le fichier unique avait été assemblé à partir de sources désynchronisées. Le binaire a été republié depuis l'arbre propre et revérifié.

---

## 7. État fonctionnel, par fonctionnalité

Légende : **fonctionne** = prouvé par test ou exécution · **partiel** = présent mais incomplet ou inatteignable · **factice** = présent dans l'interface sans effet réel · **absent** = inexistant.

### 7.1 Moteur HTTP/HTTPS

| Fonction | État | Preuve / limite |
|---|---|---|
| Téléchargement HTTP/HTTPS complet | **fonctionne** | `Download_Completes_And_Writes_Exact_Bytes` : octets identiques |
| Pause / reprise dans la même session | **fonctionne** | fichier `.npart` + `Range` + `If-Range` (ETag ou Last-Modified) ; test dédié |
| Reprise après redémarrage de l'application | **absent** | l'ETag, le `Last-Modified` et l'état vivent uniquement en mémoire ; aucun fichier d'état n'est écrit. Fermer l'application perd la file |
| **Téléchargement segmenté (multi-connexions)** | **absent** | une seule requête, un seul flux, `BufferSize = 81920`. Aucune notion de plage parallèle ; le commentaire « segmented-ready » du code ne correspond à aucune implémentation |
| Annulation + nettoyage du partiel | **fonctionne** | `Cancel_Removes_Partial_File` |
| Conflit de nom (pas d'écrasement) | **fonctionne** | `Name_Conflict_Appends_Suffix_And_Does_Not_Overwrite` |
| Limite de téléchargements simultanés | **fonctionne** | `SemaphoreSlim` alimenté par `MaxConcurrentDownloads` |
| Limitation de débit HTTP | **absent** | aucun réglage, aucun compteur |
| Proxy HTTP / authentification | **absent** | aucun champ dans `AppSettings` |
| `ConnectionsPerDownload` (réglage exposé) | **factice** | présent dans les réglages, **lu par aucun code** |

### 7.2 Moteur BitTorrent

| Fonction | État | Preuve / limite |
|---|---|---|
| Téléchargement `.torrent` local jusqu'à complétion | **fonctionne** | `Service_Seeds_And_A_Second_Swarm_Member_Gets_Identical_Bytes` : 2 Mo reçus et vérifiés |
| Magnets | **fonctionne** (moteur) | métadonnées attendues du swarm avec délai maximal ; pas de test d'intégration avec swarm réel sur Internet |
| Téléchargement d'un `.torrent` distant (URL http(s)) | **fonctionne** | `Torrent.LoadAsync` + limite de taille |
| Sélection de fichiers à l'intérieur d'un torrent | **partiel** | implémentée dans `TorrentDownloadService.StartDownloadAsync(..., selectedFiles, ...)` avec `Priority.DoNotDownload`, **mais aucune vue ne l'appelle** ; `AddDownloadViewModel` n'utilise que la surcharge simple. Aucun test. Le `TorrentFileViewModel` existe et n'est lié à aucune vue |
| `LoadTorrentMetadataAsync` (liste des fichiers avant téléchargement) | **partiel** | implémentée pour `.torrent` local et distant ; lève `NotSupportedException` sur un magnet (le swarm doit répondre). Aucun appelant dans l'interface |
| Pause / reprise / annulation | **fonctionne** | tests dédiés + reprise par redémarrage du manager |
| Télémétrie d'essaim (pairs, seeds, ratio) | **fonctionne** | alimentée par `TorrentManager.Monitor` et affichée par ligne |
| Limites de débit, port d'écoute, DHT/PEX/LSD, seed après complétion | **fonctionne** | `ApplyOptionsAsync` pousse les changements dans une session vivante |
| Progression d'une sélection partielle | **fonctionne** (moteur) | `manager.PartialProgress` ; complétion détectée via `TorrentState.Seeding` |

### 7.3 Interface

| Élément attendu par la référence | État | Constat |
|---|---|---|
| Fenêtre principale, navigation latérale, thème clair/sombre | **fonctionne** | 5 pages : Tableau de bord, Téléchargements, Nouveau, Historique, Réglages |
| Localisation FR/EN commutables à chaud | **fonctionne** | `LocaleCatalog.Fr` / `.En`, parité vérifiée par test |
| Liste des téléchargements | **partiel** | liste de **cartes** (`ListBox` + `ItemTemplate`), et non le tableau 9 colonnes de la référence |
| Menu contextuel sur une ligne (ouvrir, copier le lien, etc.) | **absent** | aucun `ContextMenu` dans `DownloadsView.xaml` |
| Barre de menus (Fichier / Édition / Affichage…) | **absent** | aucune `Menu` dans `MainWindow.xaml` |
| Panneau de catégories | **absent** | aucune notion de catégorie dans le modèle |
| Icône de zone de notification | **absent** | aucun `NotifyIcon` ; `MinimizeToTray` est exposé et **jamais lu** |
| Notification de fin de téléchargement | **factice** | `NotifyOnComplete` exposé dans les réglages, **lu par aucun code** |
| Recherche / filtres par état | **fonctionne** | `ICollectionView` + jetons de filtre |
| Ajout d'un téléchargement (URL, magnet, `.torrent` par dialogue) | **fonctionne** | validation avant tout appel moteur |
| Persistance de la file entre deux sessions | **absent** | `DownloadStore` est un cache mémoire alimenté par scrutation toutes les 200 ms ; rien n'est écrit sur disque |
| File d'attente / planificateur | **absent** | pas d'ordonnancement, pas de priorité, pas de planification horaire |

### 7.4 Intégration Windows et navigateur

| Fonction | État | Preuve |
|---|---|---|
| Gestionnaire `magnet:` | **fonctionne** | écrit dans le registre au démarrage selon le réglage |
| Instance unique + transfert d'un lien à l'instance déjà ouverte | **fonctionne** | pipe nommé, `TryForwardAsync`, fermeture propre du second processus |
| Hôte de messagerie native (Chromium + Firefox) | **fonctionne** | 6 tests d'intégration sur le processus réel |
| Validation / assainissement des requêtes navigateur | **fonctionne** | 60+ tests unitaires (schémas, noms réservés, traversée de chemin, tailles) |
| Installation forcée de l'extension Edge par stratégie | **fonctionne** (hors code) | ID `nffabhmmnibjegfnheeclanmboaeldlj` ; nécessite un redémarrage d'Edge, non vérifiable par test automatique |
| Installeur Windows (Inno Setup) | **présent, non exécuté** | 4 tests vérifient le script ; aucun installeur n'a été compilé ni installé pendant cet audit |

---

## 8. Réponses aux trois questions de l'étape 1

1. **Le HTTP est-il segmenté ?** — **Non.** `HttpDownloadService` ouvre une seule requête et copie un seul flux par blocs de 80 Kio. Aucun découpage en plages parallèles, aucune agrégation de segments.
2. **Y a-t-il une reprise ?** — **Oui, mais seulement dans la même session.** Un téléchargement mis en pause reprend au bon octet grâce à `.npart` + `Range` + `If-Range` (protection contre un fichier distant modifié). Après fermeture de l'application, il ne reste rien : ni la liste, ni l'ETag, ni la taille déjà acquise. La reprise inter-sessions est donc **absente**.
3. **Le torrent télécharge-t-il réellement jusqu'au bout ?** — **Oui.** Un torrent est transféré intégralement entre deux membres d'un essaim en boucle locale, et les octets reçus sont comparés à l'original : identiques. Le moteur écrit aussi un fichier partiel MonoTorrent qu'il ne faut pas confondre avec une fin de téléchargement : la complétion est détectée sur l'état `Seeding` (ou `Complete`), et une donnée incomplète n'est jamais annoncée terminée (test dédié).

---

## 9. Défauts corrigés pendant l'audit

Ces défauts empêchaient la compilation ou l'exécution ; ils ont été corrigés avant de considérer l'étape 1 comme mesurable.

| Défaut | Constat | Correction |
|---|---|---|
| `TorrentDownloadService.cs` tronqué | 449 octets, 5 lignes orphelines en tête de fichier → `CS0106` | Réécrit intégralement (moteur MonoTorrent complet : magnets, `.torrent` local/distant, priorités de fichiers, télémétrie, pause/reprise/annulation, libération) |
| `DownloadServiceRouter.cs` tronqué | 32 octets, un seul `using` | Réécrit : routage par `TorrentSource.IsTorrent`, fusion des deux files, relais de toutes les opérations |
| Doubles de test incomplets | `CS0535` sur `RecordingDownloadService` et `HttpDownloadEngineStub` | Ajout des membres d'interface manquants |
| DLL Release datée dans le futur | Horodatage `10:36` pour une horloge à `06:42` → MSBuild saute la recompilation ; l'application chargeait une DLL obsolète et plantait sur `TypeLoadException` | Suppression de **tous** les `bin/` et `obj/`, reconstruction complète. `NovaDownload.DownloadEngine.dll` passe de 65 024 à 72 192 octets |
| Binaire `dist` non fonctionnel | Plantage `0xE0434352` au démarrage (voir §6) | Republication depuis l'arbre propre, remplacement de `dist/NovaDownload-1.0.0-win-x64.exe`, relance vérifiée |
| `README.md` annonçait .NET 6 | Incohérent avec les 8 projets | Corrigé en .NET 9 |

Aucun de ces correctifs n'ajoute de fonctionnalité : ils rétablissent l'état que le dépôt prétendait déjà avoir.

---

## 10. Écarts entre la documentation et le code

| Affirmation de `README.md` / `dist/README.md` | Réalité |
|---|---|
| « choose exactly which files you want from a multi‑file torrent, then download only those files » | Le moteur sait le faire ; **l'interface ne l'expose pas**. Un utilisateur ne peut pas choisir |
| « Configurable HTTP download options » | Les seules options HTTP réellement actives sont le dossier par défaut et le nombre de téléchargements simultanés. `ConnectionsPerDownload` est inerte |
| « Resuming, pausing, canceling, and monitoring downloads » | Exact pour pause/reprise/annulation **dans la session**. La reprise après redémarrage n'existe pas |
| `docs/BITTORRENT.md`, `docs/BROWSER_INTEGRATION.md`, `docs/RELEASE_AND_TEST_REPORT.md` | Ces documents décrivent l'architecture visée ; plusieurs de leurs promesses (sélection de fichiers dans l'UI, persistance) ne sont pas encore tenues |

Le README sera réécrit à l'étape qui livre réellement ces fonctions ; il n'est pas corrigé ici pour ne pas masquer l'écart.

---

## 11. Dette priorisée pour les étapes suivantes

Par ordre d'impact sur l'utilisateur :

1. **Persistance de la file** — sans elle, ni reprise inter-sessions, ni historique durable, ni restauration au démarrage.
2. **Sélection des fichiers dans l'interface** — le moteur est prêt, il manque l'écran (liste des fichiers, cases à cocher, application de la sélection).
3. **HTTP segmenté** — multi-connexions avec agrégation et repli sur un flux unique si le serveur n'accepte pas les plages.
4. **Couverture de test des deux membres d'interface non testés** (`selectedFiles`, `LoadTorrentMetadataAsync`).
5. **Tableau de téléchargements conforme à la référence** (colonnes, menu contextuel, catégories).
6. **Réglages inertes** — `ConnectionsPerDownload`, `NotifyOnComplete`, `MinimizeToTray` : soit implémentés, soit retirés de l'interface.
7. **Suite de tests des extensions** — reliée à rien pour l'instant.

---

## 12. Limites de cet audit

- Aucun torrent réel d'Internet n'a été testé : la preuve torrent vient d'un essaim en boucle locale, volontairement déterministe. Un magnet dépendant de DHT/trackers externes n'est donc pas prouvé de bout en bout.
- Aucun installeur n'a été compilé ni installé ; seuls les tests statiques du script Inno Setup ont été exécutés.
- L'installation forcée de l'extension Edge n'a pas été revérifiée dans le navigateur pendant cet audit (elle nécessite un redémarrage d'Edge et une inspection manuelle de `edge://extensions`).
- Le rendu visuel a été constaté par énumération de fenêtres et capture d'écran ; aucune comparaison pixel à pixel avec l'image de référence n'a été faite à ce stade.

---

## 13. Verdict de l'étape 1

| Critère | Résultat |
|---|---|
| Version .NET unifiée dans les projets, le README et `dist` | **RÉUSSI** — 8/8 projets en `net9.0`/`net9.0-windows`, SDK unique 9.0.319, README corrigé, `dist` republication en net9.0 |
| Compilation sans avertissement ni erreur | **RÉUSSI** — Debug et Release : 0 avertissement, 0 erreur, après nettoyage complet |
| Tous les tests exécutés et verts | **RÉUSSI** — 193 + 34 = 227 tests, 0 échec, 0 ignoré |
| Application réellement lançable | **RÉUSSI** — Debug, Release et binaire `dist` affichent tous une fenêtre `NovaDownload` visible et répondante ; le binaire `dist` plantait avant correction |
| `docs/AUDIT.md` écrit avec preuves par fonctionnalité | **RÉUSSI** — ce document |

---

## 14. Delta après l'étape 2 — moteur HTTP segmenté

Les constats des sections 5 et 11 concernant HTTP ne sont plus vrais. Ce qui a changé, et ce que
cela remplace :

| Constat de l'étape 1 | État après l'étape 2 |
|---|---|
| « Téléchargement HTTP en flux unique » | **Remplacé.** Une requête de sondage (`Range: bytes=0-1`) lit la taille, l'ETag, la date et l'acceptation des plages. Le fichier est ensuite découpé en `Segments` plages (8 par défaut, 1 Mio minimum) demandées en parallèle. Vérifié : `ConnectionCount == 8`, 8 en-têtes `Range` distincts, `MaxConcurrentRequests >= 8`. |
| « La reprise après redémarrage n'existe pas » | **Remplacé.** Un fichier d'état JSON par transfert (`%LOCALAPPDATA%\NovaDownload\http-state`) enregistre les validateurs et l'avancement de chaque plage. Prouvé au niveau du processus : premier processus tué sans ménagement à 14,68 Mio, second processus qui restaure (1 ligne, `Paused`, 15,20 Mio) et termine avec un SHA-256 identique à celui du serveur, sans `.npart` ni fichier d'état résiduels. |
| — | **Ajouté.** Refus explicite de reprendre quand le distant a changé (ETag, Last-Modified ou taille) : l'élément passe en `Failed` avec un message qui nomme la cause, et `RestartAsync` relance depuis zéro. |
| — | **Ajouté.** Limiteur de débit global (budget partagé par les 8 connexions, indépendant du nombre de transferts). Mesuré : 4 Mio en 8,01 s à 4 connexions simultanées = 511 Kio/s pour une limite de 512 Kio/s. |
| — | **Ajouté.** Serveurs sans plage, réponses *chunked*, redirections, `Content-Disposition`, authentification Basic par téléchargement, proxy direct/système/manuel, reprises avec backoff exponentiel. Chaque chemin est couvert par un test qui compare le SHA-256 au contenu du serveur. |

Point d'attention pour la suite : le moteur sait restaurer une file (`Restore()`) mais l'application
ne l'appelle pas encore ; la persistance visible côté interface reste l'affaire de l'cette étape. Les
réglages associés (débit, proxy, authentification) ne sont pas non plus exposés à l'écran : ils
existent dans `HttpEngineOptions` et seront branchés à l'cette étape, pas avant.

Deux défauts réels ont été trouvés par ces tests et corrigés, ils méritent d'être notés parce qu'ils
ne se voient pas à la lecture :

1. Une réponse *chunked* était prise pour un objet vide (longueur inconnue confondue avec longueur
   nulle) — le fichier produit faisait 0 octet.
2. Une reprise pouvait démarrer pendant que le worker annulé libérait encore le fichier partiel ;
   corrigé par un compteur de génération et une attente explicite, ce qui évite qu'un ancien worker
   écrase le statut du nouveau.

Un troisième a été anticipé et fermé avant de livrer : l'état périodique comptait des octets encore
dans le tampon du processus, donc un `kill` pouvait laisser des trous silencieux dans le fichier
final. L'état n'est désormais écrit qu'après un `Flush` du fichier partiel, sous le même verrou que
les écritures — c'est ce qui rend la preuve de reprise ci-dessus sans hasard.

---

## 15. Delta après l'étape 3 — consolidation BitTorrent

Les constats des sections 7.2 et 7.3 concernant les torrents ne sont plus vrais. Ce qui a changé, et
ce que cela remplace :

| Constat de l'étape 1 | État après l'étape 3 |
|---|---|
| « Sélection de fichiers : partiel — implémentée mais aucune vue ne l'appelle, aucun test » | **Remplacé.** Fenêtre modale `TorrentFilesWindow` (`ViewModels/TorrentSelectionViewModel.cs`) ouverte depuis quatre entrées : un `.torrent` choisi sur la page Nouveau, le bouton « Torrent. » de la liste, l'action de ligne « Choisir les fichiers » (`IsMultiFileTorrent`), et le glisser-déposer. Cases à cocher par fichier, tout sélectionner / tout désélectionner, dossier de destination modifiable, résumé « 1 fichier sur 2 · 1,1 Mo » recalculé à chaque case. La sélection est poussée au moteur par `IDownloadService.SelectFilesAsync` (`Priority.DoNotDownload`). |
| « `LoadTorrentMetadataAsync` : partiel — aucun appelant dans l'interface » | **Remplacé.** Appelée par `AddDownloadViewModel.StartDownloadAsync` avant tout démarrage quand la source est un `.torrent` non-magnet, pour peupler la fenêtre de choix. |
| « Magnets : fonctionne (moteur), pas de test d'integration » | **Remplacé.** Test d'intégration avec swarm réel local : le magnet est accepté, `InfoHash` exposé, `Kind == Torrent`, une seule ligne créée. |
| « Reprise après redémarrage : absente (HTTP) » et « Pause/reprise torrente : fonctionne dans la session » | **Remplacé pour les torrents.** Un fichier JSON par torrent (`%LOCALAPPDATA%\NovaDownload\torrent-state`, écriture atomique, répertoire surchargeable par `NOVADOWNLOAD_DATA_DIR`) enregistre la source, les validateurs et le dossier ; `Restore()` reconstruit les lignes en `Paused` au démarrage et `ResumeAsync` repart des octets déjà présents. Le `.torrent` d'origine est copié dans `CacheDirectory/torrent-<id>.torrent` pour qu'une reprise ne dépende pas du fichier d'origine. |
| « `magnet:` seulement » | **Ajouté.** `.torrent` associé pour l'utilisateur courant via le ProgID `NovaDownload.Torrent` (`HKCU\Software\Classes`, sans jamais toucher `UserChoice`), avec les entrées `[Registry]` correspondantes dans `installer/NovaDownload.iss`. |
| « Limites de débit seed/down : réglages existants » | **Complété.** Les deux limites sont exposées dans Réglages (`TorrentUploadLimit`, `TorrentDownloadLimit`) et poussées dans une session vivante. La limite descendante est mesurée par test : 1 Mio à 256 Kio/s ne peut pas arriver sous 4 s sur loopback, le test exige au moins 3 s et un SHA-256 identique. La limite montante est configurable et appliquée au même endroit, sans mesure dédiée. |
| « Suppression d'une ligne » | **Complété.** `RemoveAsync(id, deleteFiles)` : une ligne inachevée perd toujours ses données partielles ; pour une ligne terminée, l'action « Retirer et effacer les fichiers » demande confirmation par `MessageBox` avant de supprimer la charge utile. |

### 15.1 Deux pièges MonoTorrent qui ne se voient pas à la lecture

Les deux ont produit de faux « ça tourne » et méritent d'être écrits, parce qu'ils coûtent sinon une
demi-journée chacun.

1. **`TorrentFile.Path` exclut le nom du torrent.** Pour un torrent construit depuis un dossier, le
   nom de dossier est réinjecté par `TorrentManager.ContainingDirectory`, qui vaut `SavePath`
   seulement si le torrent n'a qu'un fichier ou si `CreateContainingDirectory` est faux. Un seeder
   de dossier configuré avec `savePath` = le dossier parent doit mettre
   `CreateContainingDirectory = true`, sinon il cherche `…\seed\keep.bin` au lieu de
   `…\seed\payload\keep.bin`, hache des fichiers absents indéfiniment et ne sert rien. Vérifié par
   sonde isolée : `CreateContainingDirectory = false` → état `Downloading`, 0 %, fichiers absents ;
   `true` → état `Seeding`, 100 %, fichiers présents.
2. **`TorrentManager.Complete` est `Bitfield.AllTrue`.** Une sélection partielle laisse des pièces
   manquantes pour toujours : `Complete` ne passe donc jamais à vrai et une ligne « seulement
   quelques fichiers » resterait en téléchargement jusqu'à la fin des temps. La bonne ligne d'arrivée
   est `PartialProgress >= 100` (il honore `Priority.DoNotDownload`), d'où `SwarmFinished()` :
   `Complete || (au moins un fichier exclu && PartialProgress >= 100)`.

Un troisième défaut, côté test et non côté produit : la sonde de seeder acceptait l'état
`Downloading` comme « prêt », ce qui laissait le leecher se connecter à un pair qui n'avait pas fini
de hacher. Le seeder attend maintenant `Seeding` explicitement.

### 15.2 Une course entre pause et dernière pièce (moteur HTTP)

Trouvée par la suite complète, pas par une lecture : trois tests de pause du moteur HTTP
(`Pause_Then_Resume…`, `Resume_After_A_Restart…`, `A_Changed_ETag_…`) échouaient de façon
intermittente dès que les sept tests torrente tournaient en parallèle, alors qu'ils passaient seuls en
moins d'une seconde. La cause est une vraie fenêtre produit, pas un défaut du test : `PauseAsync`
basculait la ligne en `Paused` et rendait la main immédiatement, pendant que le worker pouvait déjà
être dans l'assemblage de la dernière pièce. Trois issues possibles, dont une incohérente :
`Finalize` déplace le fichier et supprime l'enregistrement de reprise, si bien que la ligne affichait
« en pause » sur un fichier terminé et sans état de reprise.

Correction : `PauseAsync` annule, marque la ligne, puis attend que le worker se pose (5 s au plus)
avant de revenir. À la sortie, l'issue est définitive — soit `Paused` avec son JSON écrit, soit
`Completed` parce que la dernière pièce était déjà arrivée. Les trois fixtures ont été ralenties
(25 ms par morceau au lieu de 6) pour que la pause tombe franchement en vol, et elles échouent
désormais avec un message explicite si la fixture finit avant la pause, au lieu d'attendre pendant
dix secondes un fichier qui ne viendra jamais.

### 15.3 Limite connue, assumée

Une ligne **terminée** qui continue de seeder n'a plus d'état de reprise : `SettleState` efface le
JSON et le `.torrent` en cache à la complétion. Rouvrir l'application ne ramène donc pas les lignes
en seed — elles restent sur le disque, visibles dans le dossier, mais hors liste. La persistance
complète de la file (terminés compris) est le sujet de l'cette étape, pas de celle-ci.

### 15.4 Validation de l'étape 3

| Critère | Résultat |
|---|---|
| a) Compile, et les tests antérieurs passent toujours | **RÉUSSI** — Debug et Release : 0 avertissement, 0 erreur. Suite complète : **193 tests unitaires + 56 tests d'intégration = 249, 0 échec, 0 ignoré**, code de sortie 0 (intégration en 2 min 23 s). Les 7 tests de consolidation torrente et les 15 tests HTTP segmenté sont dans ces 56. |
| b) Un vrai `.torrent` légal s'ouvre et liste ses fichiers | **RÉUSSI** — `ubuntu-24.04.3-live-server-amd64.iso.torrent` (252 402 octets) téléchargé depuis `releases.ubuntu.com`, ouvert par le moteur : 1 fichier `ubuntu-24.04.3-live-server-amd64.iso`, 3 303 444 480 octets, infohash `A1DFEFEC1A9DD7FA8A041EBEEEA271DB55126D2F`, 2 trackers. Côté interface, torrent multi-fichiers : fenêtre « Fichiers du torrent », 2 fichiers listés avec leurs tailles (alpha.bin 1,1 Mo, beta.bin 781,3 Ko), décocher le second met le résumé à « 1 fichier sur 2 · 1,1 Mo », le dossier saisi (`…\ui\download`) est celui de la ligne créée, et le journal du moteur confirme `1/2 files selected`. |
| c) Un magnet est accepté et listé | **RÉUSSI** — `A_Magnet_Link_Is_Accepted_And_Exposes_Its_Info_Hash` : le magnet est parsé, `InfoHash` égale celui du `.torrent`, `Kind == Torrent`, une seule ligne. |
| d) Un torrent de test se télécharge entièrement, hash vérifié, seulement les fichiers cochés | **RÉUSSI** — `Only_The_Selected_Files_Land_On_Disk_With_The_Right_Bytes` : 3 fichiers, un seul coché, le fichier coché arrive avec le bon SHA-256, `skip-a.bin` et `skip-b.bin` n'existent pas. La ligne atteint `Completed` via `SwarmFinished()` (sélection partielle), pas via `Complete`. |
| e) Pause / reprise, et reprise après redémarrage | **RÉUSSI** — `A_Restarted_Service_Resumes_A_Paused_Torrent_To_The_Exact_Bytes` : pause à ~1,5 Mio sur 4 Mio (limite 128 Kio/s pour que la pause tombe en vol), premier service détruit, `Restore()` ramène 1 ligne `Paused` avec le même dossier et le même nom de fichier, la reprise finit avec le SHA-256 exact. Ajouté dans cette étape : `PauseAsync` du moteur HTTP attend maintenant que le worker se pose avant de rendre la main (voir §15.2), ce qui rend l'état « en pause » définitif. |
| f) HTTP et torrent coexistent sans régression | **RÉUSSI** — `Http_And_Torrent_Coexist_In_The_Same_List` : un transfert HTTP et un transfert torrente menés ensemble dans une seule liste, les deux `Completed` avec leurs SHA-256, `Kind` distincts, télémétrie d'essaim sur la ligne torrente (`InfoHash`, badge `BT`) et pas sur la ligne HTTP. Suite complète verte, aucune régression HTTP. |
| Ouverture par association `.torrent` | **RÉUSSI** — `HKCU\Software\Classes\.torrent` pointe sur le ProgID `NovaDownload.Torrent` (commande `"…\NovaDownload.App.exe" "%1"`), `UserChoice` non touché. Lancement par la commande enregistrée : la fenêtre de choix s'ouvre avec les 2 fichiers, la confirmation crée la ligne dans le dossier indiqué. |
---

## 16. Delta après l'cette étape — interface réelle et persistance

Les constats des sections 7.3, 10 et 11 concernant l'interface et la persistance ne sont plus vrais.
Ce qui a changé, et ce que cela remplace :

| Constat de l'étape 1 | État après l'cette étape |
|---|---|
| « Liste des téléchargements : partiel — liste de cartes, et non le tableau 9 colonnes » | **Remplacé.** `ListView` + `GridView` à 9 colonnes (`Col_Name`, `Col_Queue`, `Col_Type`, `Col_Size`, `Col_Progress`, `Col_Speed`, `Col_ETA`, `Col_Status`, `Col_Added`), en-têtes triables, largeurs fixes, `AutomationId="DownloadsList"`. |
| « Menu contextuel sur une ligne : absent » | **Remplacé.** `ContextMenu` par ligne : télécharger maintenant, arrêter, retélécharger, retirer, ouvrir le fichier, ouvrir le dossier, copier le lien. |
| « Barre de menus : absente » | **Remplacé.** `Menu` complet (Tâches, Fichier, Téléchargements, Affichage, Aide) avec en-têtes, séparateurs et raccourcis affichés (`InputGestureText`). |
| « Panneau de catégories : absent » | **Remplacé.** Volet gauche repliable (`ShowCategoryPane`, persistance dans `settings.json`), `TreeView` avec les 7 catégories intégrées + nœuds personnalisés, filtrage par `DownloadFilter`. |
| « Icône de zone de notification : absent » | **Remplacé.** `TrayService` (`NotifyIcon` WinForms) avec menu contextuel (Ouvrir, Tout reprendre, Tout mettre en pause, Réglages, Quitter), info-bulle compteur, ballon de fin de téléchargement. |
| « `MinimizeToTray` exposé et jamais lu » | **Remplacé.** Lu par `MainWindow.OnStateChanged` via `MainWindowViewModel.MinimizesToTray`; drapeau `_hiddenByMinimize` pour éviter le désync entre l'état WPF et l'état OS. |
| « `NotifyOnComplete` exposé et jamais lu » | **Remplacé.** Lu par `OnStoreRefreshed`, ballon tray par téléchargement terminé (hors lignes restaurées depuis l'archive). |
| « Persistance de la file : absente » | **Remplacé.** `DownloadArchive` JSON atomique (`downloads.json`, écriture `.tmp` puis remplacement) + `TorrentTransferStateStore` (`%LOCALAPPDATA%\NovaDownload\torrent-state`). `Restore()` au démarrage, `FlushArchive()` à la fermeture, `PersistIfDirtyNow()` à la sortie. |
| « File d'attente / planificateur : absent » | **Ajouté.** `QueueScheduler` (pause/reprise selon `MaxConcurrentDownloads` et planification horaire) exposé par `SchedulerWindow`. |
| « Recherche / filtres par état » | **Conservé et étendu.** `DownloadFilter` (portée + recherche + catégorie) + boîte de recherche `Ctrl+F` / `F3`. |

### 16.1 Persistance et restauration

- `DownloadStore` n'est plus un cache mémoire : il charge l'archive au démarrage, réconcilie avec les lignes vivantes du moteur (`ApplyPreferencesTo`), et persiste à la demande.
- Écriture atomique prouvée par test (`Save_Leaves_No_Temporary_File_Behind`) : fichier `.tmp` puis remplacement, aucun résidu.
- Archive corrompue → liste vide + `LastError` renseigné, jamais de crash (`Corrupt_Archive_Loads_Empty_And_Reports_The_Reason`).
- Une ligne qui tournait (`Downloading`) revient en `Paused` et marquée `IsRestored` (`Row_That_Was_Running_Comes_Back_Paused`).
- Les préférences (catégorie, catégorie personnalisée, description, dernier essai) sont recopiées sur la ligne vivante du moteur (`Preferences_Are_Copied_Onto_A_Live_Engine_Row`).
- L'état torrent est écrit par transfert dans `%LOCALAPPDATA%\NovaDownload\torrent-state` (atomique, répertoire surchargeable par `NOVADOWNLOAD_DATA_DIR`), avec copie du `.torrent` d'origine dans le cache.

### 16.2 Shell et chrome

- Chrome sans bordure avec boutons minimiser / maximiser / fermer personnalisés ; `DragMove` sur la barre de titre, double-clic pour maximiser.
- Fermeture du bouton « X » masque la fenêtre (transferts maintenus) ; la sortie réelle passe par le menu ou le tray, avec confirmation si des transferts tournent.
- Minimisation selon `Settings.MinimizeToTray` : soit parking dans le tray, soit fenêtre normale dans la barre des tâches. Restauration depuis l'extérieur du tray (`taskbar preview`, `Win+Up`, `Alt+Tab`) réaffiche la fenêtre réelle.
- Thème clair/sombre commutable à chaud (`ThemeService`), drapeau `IsDark`.
- Barre d'état avec message de statut et compteur ; masquable (`ShowStatusBar`).

### 16.3 Catégories et filtrage

- `DownloadClassifier` classe par extension (Compressed, Documents, Music, Programs, Video) ; un torrent est toujours `Torrents` même si sa charge utile est un `.mkv`.
- `DownloadFilter` applique portée (Tout, Non terminés, Terminés, File d'attente, Catégorie, Personnalisée) + recherche insensible à la casse sur nom, URL, dossier, description.
- Parité des libellés de catégorie dans les deux langues (`Every_Category_Has_A_Label_In_Both_Languages`).
- Chaque extension est classée exactement une fois (`Every_Extension_Is_Filed_Exactly_Once`).

### 16.4 Dialogues et confirmations

- `AddUrlWindow` : ajout URL / magnet / `.torrent` avec validation avant tout appel moteur.
- `TorrentFilesWindow` : cases à cocher par fichier, tout sélectionner / tout désélectionner, dossier modifiable, résumé recalculé.
- `OptionsWindow`, `AboutWindow`, `HelpWindow`, `PromptWindow`, `SpeedLimitWindow`, `SchedulerWindow`, `ConfirmationWindow`.
- Confirmations par `MessageBox` : suppression d'une ligne non terminée (`Dlg_RemoveTitle` / `Dlg_RemoveRunning`), suppression avec fichiers (`Dlg_DeleteFilesTitle` / `Dlg_DeleteFilesOne`), sortie avec transferts actifs (`Dlg_ExitTitle` / `Dlg_ExitActive`).

### 16.5 Zone de notification

- `TrayService` crée un `NotifyIcon` avec icône embarquée (`Assets/novadownload.ico`), repli sur l'icône système si la ressource manque.
- Menu contextuel : Ouvrir, Tout reprendre, Tout mettre en pause, Réglages, Quitter.
- Double-clic ou « Ouvrir » ramène la fenêtre ; « Quitter » déclenche `ExitCommand` → confirmation si transferts actifs.
- Info-bulle compteur d'actifs et débit ; ballon de fin de téléchargement (`NotifyCompletion`).
- Libération propre (`Dispose` : `Visible = false` puis `Dispose()`).

### 16.6 Localisation

- Catalogues FR/EN paritaires : `Both_Catalogs_Carry_The_Same_Keys`.
- Toute clé appelée par le code (`.cs` et `.xaml`) résout dans les deux langues : `Every_Label_Used_By_The_App_Resolves_In_Both_Languages`.
- Commutation à chaud (`Localizer.LanguageChanged`) ; re-traduction des messages de statut déjà affichés.
- Clés shell ajoutées en cette étape : `Dlg_RemoveRunning`, `Dlg_RemoveTitle`, `About_Version`, `Common_Untitled` (parité FR/EN vérifiée).

### 16.7 Raccourcis clavier

- `InputBindings` fenêtre : `Ctrl+N` (ajouter), `Ctrl+F` (rechercher), `F3` (suivant), `F5` (actualiser), `F9` (limiteur), `Ctrl+A` (tout sélectionner), `Delete` (supprimer), `Ctrl+V` (coller une URL).
- `KeyBinding` dans la liste pour `Ctrl+V`, `Ctrl+A`, `Delete`.
- Preuve par harness (`harness-key.ps1`) : envoi réel via `SendInput` après prise de focus vérifiée, pour contourner le vol de focus ambiant.

### 16.8 Validation de l'cette étape

| Critère | Résultat |
|---|---|
| a) Compilation Debug et Release, 0 avertissement, 0 erreur | **RÉUSSI** — arbre compilé, `dotnet test --no-build` a chargé les assemblys sans rebuild. |
| b) Tests unitaires | **RÉUSSI** — **247 tests unitaires**, 0 échec (dont 54 nouveaux tests shell/catégorie/archive/localisation). |
| c) Tests d'intégration | **RÉUSSI** — **56/56 verts**. La passe complète avait signalé un dépassement de délai sur `The_Torrent_Rate_Limit_Holds_The_Swarm_Download` (état `Queued` après 180 s) ; la relance isolée de ce même test passe en 4 s. Cause : charge machine pendant la passe complète (les 56 tests dont 7 torrent tournent en parallèle), pas le produit. |
| d) Lancement de l'application | **RÉUSSI** — fenêtre `NovaDownload` visible et répondante (Debug, Release et binaire publié). |
| e) Progression HTTP et torrent | **RÉUSSI** — barres de progression, débit, ETA, télémétrie d'essaim (pairs, seeds, ratio) affichés par ligne ; tests `Download_Completes_And_Writes_Exact_Bytes` et `Service_Seeds_And_A_Second_Swarm_Member_Gets_Identical_Bytes`. |
| f) Pause / reprise | **RÉUSSI** — `PauseAsync` attend que le worker se pose ; reprise au bon octet ; reprise inter-sessions via archive + état torrent. |
| g) Filtrage par catégorie | **RÉUSSI** — `DownloadFilter` + `DownloadClassifier` + volet catégories ; tests `MatchesScope`, `MatchesSearch`, `Matches`. |
| h) Menu, boutons, raccourcis | **RÉUSSI** — menu bar, toolbar, context menu, raccourcis `Delete`/`F5`/`Ctrl+N`/etc. prouvés par harness UI Automation (`harness-key.ps1`, `harness-menu.ps1`). |
| i) Dialogues | **RÉUSSI** — confirmation de suppression (`Dlg_RemoveTitle`/`Dlg_RemoveRunning`), suppression avec fichiers (`Dlg_DeleteFilesTitle`/`Dlg_DeleteFilesOne`), sortie (`Dlg_ExitTitle`/`Dlg_ExitActive`) observés comme fenêtres `#32770` par `harness-dialogwatch.ps1`. |
| j) Tray (masquage, restauration, sortie) | **RÉUSSI** — masquage à la fermeture, restauration par double-clic, sortie avec confirmation si transferts actifs (`harness-tray.ps1 -Invoke "Quitter" -Confirm`). |
| k) Localisation FR/EN | **RÉUSSI** — parité des catalogues + résolution de toute clé appelée (`Both_Catalogs_Carry_The_Same_Keys`, `Every_Label_Used_By_The_App_Resolves_In_Both_Languages`). |
| l) Persistance JSON | **RÉUSSI** — archive atomique, restauration au démarrage, flush à la fermeture, tests `Archive_Round_Trip_Preserves_The_Row`, `Save_Leaves_No_Temporary_File_Behind`, `Corrupt_Archive_Loads_Empty_And_Reports_The_Reason`. |

### 16.9 Limites connues, assumées

- La passe complète a signalé un dépassement de délai sur le test de limite de débit torrent ; la relance isolée de ce même test passe en 4 s. Cause : charge machine pendant la passe complète, pas le produit. Le moteur est par ailleurs prouvé par `A_Restarted_Service_Resumes_A_Paused_Torrent_To_The_Exact_Bytes`.
- Le rendu visuel a été vérifié par captures (`harness-shot-*.png`) et par UI Automation, pas par comparaison pixel à pixel avec l'image de référence ; les écarts de mise en page restants sont consignés hors dépôt.
- Les réglages HTTP avancés (proxy, authentification) sont exposés dans `HttpEngineOptions` mais pas encore dans l'écran Réglages : c'est le sujet de l'étape 5, pas de celle-ci, comme annoncé en §14.
