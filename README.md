# passportcustom

Mod **BepInEx** pour **PEAK** qui ajoute une pagination au menu Passport (personnalisation), un système de report/ban de joueurs, et une console de développement restreinte.

## # Fonctionnalités

### Pagination du Passport
- Ajoute des boutons de navigation (gauche/droite) au menu de personnalisation (Passport)
- Patch Harmony sur `PassportManager` (`Awake`, `OpenTab`, `SetButtons`) pour intercepter et gérer l'affichage par pages
- Expose une **API publique** (`PassportPaginationAPI`) permettant à d'autres mods d'enregistrer des items supplémentaires dans un onglet du Passport, sans toucher au code natif
- Détection robuste du bouton "template" à cloner (plusieurs stratégies de fallback si la structure du jeu change)

### Report Card (report/ban de joueurs)
- Système de report en jeu, entièrement en réseau via Photon `RaiseEvent`/`IOnEventCallback` (pas de `PhotonView` requis)
- Touche `F1` pour ouvrir le menu de report
- Seuil configurable (`REPORTS_THRESHOLD`) : au-delà, un panneau de décision s'ouvre côté host (bannir ou ignorer)
- Anti-triche : vérification que l'expéditeur Photon correspond bien au reporter déclaré, anti-spam (un report par joueur)
- UI entièrement construite en code (aucun prefab requis)

### DevConsole
- Console de développement accessible uniquement au joueur dont le pseudo est `bynex` (insensible à la casse)
- Touche `Suppr` : envoie une demande d'accès à l'host, qui doit l'accepter ou la refuser
- Commandes disponibles : `help`, `players`, `kick [actor]`, `ping`, `room`, `time`, `clear`, `say [message]`, `master`
- Fonctionne à la fois en local (si `bynex` est l'host) et à distance (commandes relayées au host via Photon)

## # Prérequis

- [BepInEx](https://github.com/BepInEx/BepInEx) installé sur PEAK
- .NET Standard 2.1 SDK pour compiler
- HarmonyLib (fourni avec BepInEx)
- Photon Realtime / PUN (déjà présent dans les DLL du jeu)

## # Compilation

```bash
dotnet build passportcustom.csproj
```

Ou directement via les scripts fournis :
```bash
build.bat         # utilise dotnet du PATH
build_fixed.bat   # utilise le chemin complet C:\Program Files\dotnet\dotnet.exe
```

Le `.csproj` référence `Assembly-CSharp.dll`, `PhotonRealtime.dll` et `PhotonUnityNetworking.dll` via un chemin relatif vers l'installation Steam de PEAK — à adapter si le jeu est installé ailleurs.

## # Installation

1. Compiler le mod (ou récupérer `passportcustom.dll`)
2. Copier `passportcustom.dll` dans le dossier :
   ```
   PEAK/BepInEx/plugins/passportcustom/
   ```
3. Lancer le jeu — le log `[passportcustom] Initialisation du bridge de pagination...` doit apparaître dans la console BepInEx

## # État actuel / TODO

- [ ] Vérifier la compatibilité de `FindTemplateButton()` à chaque mise à jour de PEAK (le chemin natif peut changer)
- [ ] `ListPings()` n'affiche que le ping local — Photon n'expose pas directement le ping des autres clients
- [ ] Étendre les commandes DevConsole si besoin (téléportation, spawn, etc.)
- [ ] Ajouter une persistance des bans (actuellement uniquement en mémoire pour la session)

## # Licence

Projet personnel / hobby — usage libre pour la communauté PEAK.
