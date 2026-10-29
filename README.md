# Open Story Studio

[![CI](https://github.com/FrqSalah/open-story-studio/actions/workflows/ci.yml/badge.svg)](https://github.com/FrqSalah/open-story-studio/actions/workflows/ci.yml)
[![Deploy web](https://github.com/FrqSalah/open-story-studio/actions/workflows/deploy-web.yml/badge.svg)](https://github.com/FrqSalah/open-story-studio/actions/workflows/deploy-web.yml)
[![Licence: MPL-2.0](https://img.shields.io/badge/licence-MPL--2.0-blue.svg)](LICENSE)

Créez, simulez et transférez vos propres packs d'histoires vers les conteuses compatibles
(firmwares **v1**, **v2** et **v3**), sur **Windows**, **Android** et dans le **navigateur**.

Réécriture en .NET 10 du projet [STUdio](https://github.com/marian-m12l/studio), dont il reprend
les travaux de rétro-ingénierie et le format d'archive (compatibilité totale avec les packs STUdio et Moiki).

> ⚠️ **Utilisation à vos risques.** Une écriture interrompue peut rendre l'appareil inutilisable jusqu'à sa
> réinitialisation. L'application sauvegarde l'index de l'appareil avant chaque modification et vérifie chaque
> fichier écrit, mais aucune garantie n'est donnée. Ce projet n'est affilié à aucun fabricant et ne distribue,
> ni ne déchiffre, aucun contenu protégé : il sert uniquement à l'interopérabilité avec vos propres créations.

---

## Fonctionnalités

| | Web (PWA) | Windows | Android |
|---|:---:|:---:|:---:|
| Bibliothèque de packs (import/export `.zip` STUdio) | ✅ IndexedDB | ✅ | ✅ |
| Éditeur de graphe (glisser-déposer, annuler/rétablir, modèles) | ✅ | ✅ | ✅ |
| Validation en direct (liens cassés, étapes inaccessibles, impasses…) | ✅ | ✅ | ✅ |
| Simulateur de la conteuse (molette, OK, accueil, pause, son) | ✅ | ✅ | ✅ |
| Enregistrement au micro | ✅ | ✅ | — |
| Conversion automatique des images (n'importe quel format → BMP 16 couleurs 320×240) | — | ✅ | ✅ |
| Conversion automatique du son (→ MP3 mono 44,1 kHz / WAV v1) | — | ✅ avec ffmpeg | MP3 conformes uniquement |
| Transfert **v2** (stockage de masse) | — | ✅ | ✅ (USB OTG, stockage choisi via Android) |
| Transfert **v3** (AES, clé lue sur l'appareil) | — | 🧪 expérimental | 🧪 expérimental |
| Transfert **v1** (USB bas niveau) | — | 🧪 expérimental (libusb) | 🧪 expérimental |
| Sauvegarde / restauration de l'index de l'appareil | — | ✅ | ✅ |
| CLI (`studio`) | — | ✅ (+ Linux/macOS) | — |

🧪 = implémenté d'après le code de STUdio et couvert par des tests sur des images d'appareil simulées, mais **pas encore
validé sur du vrai matériel**. L'écriture est désactivée par défaut ; activez-la dans la page *Appareil*
(ou avec `--experimental` en CLI).

## Architecture

```
src/
├─ Studio.Core      Modèle de pack, validation, moteur de navigation (simulateur)      — sans dépendance
├─ Studio.Formats   Formats : archive STUdio (zip), FS v2/v3, binaire v1, .md/.pi,     — sans dépendance
│                   chiffrement XXTEA/AES, bibliothèque (IPackLibrary)
├─ Studio.Media     Conversion images (SkiaSharp, quantification 16 couleurs, RLE4) et audio (ffmpeg)
├─ Studio.Device    Accès appareil : stockage de masse (IDeviceStorage), USB v1 (SCSI vendeur sur
│                   Bulk-Only Transport), transferts transactionnels, sauvegardes, DeviceManager
├─ Studio.Cli       Outil en ligne de commande `studio`
├─ Studio.UI        Interface Blazor partagée (bibliothèque, éditeur, simulateur, appareil)
├─ Studio.Web       Hôte Blazor WebAssembly (PWA) → Netlify
└─ Studio.App       Hôte .NET MAUI Blazor Hybrid → Windows et Android
tests/
└─ Studio.Tests     xUnit v3 : formats, chiffrement, conversion, appareils simulés, bibliothèque
```

Principes :

- **Le cœur ne dépend d'aucune interface** : la même logique sert la CLI, l'application et le site.
- **La bibliothèque garde les fichiers d'origine** ; la conversion vers le format de l'appareil se fait au moment du
  transfert, selon sa génération (`DeviceAssetPreparer`).
- **Transferts sûrs** : sauvegarde de l'index → écriture du dossier du pack (ignoré par le firmware tant qu'il n'est pas
  indexé) → relecture et comparaison → mise à jour de l'index en dernier. En cas d'échec, le dossier est supprimé et
  l'index d'origine conservé.
- **Testable sans matériel** : `InMemoryDeviceStorage` et un faux bus USB permettent de tester toute la pile.

### Formats pris en charge

| Génération | Accès | Format | Chiffrement |
|---|---|---|---|
| v1 (firmware 1.x) | USB, secteurs SD bruts (commandes SCSI `0xF6`) | binaire, index au secteur 100 000 | — |
| v2 (firmware 2.x) | stockage de masse : `.md`, `.pi`, `.content/XXXXXXXX/` | `ni` `li` `ri` `si` `rf/` `sf/` `nm` `bt` | XXTEA (clé commune) sur les 512 premiers octets, `bt` lié à l'appareil |
| v3 (firmware 3.x, `.md` v6/v7) | stockage de masse | identique à v2 | AES-CBC avec la clé lue dans `.md` |

## Démarrer

### Prérequis

- [SDK .NET 10](https://dotnet.microsoft.com/download/dotnet/10.0) (voir `global.json`)
- Pour l'application : `dotnet workload install maui-windows maui-android`
- Optionnel : [ffmpeg](https://ffmpeg.org/) dans le `PATH` (ou variable `STUDIO_FFMPEG`) pour convertir n'importe quel son

### Compiler et tester

```bash
dotnet build tests/Studio.Tests -c Release
```

```bash
dotnet test --project tests/Studio.Tests
```

### Lancer la version web

```bash
dotnet run --project src/Studio.Web
```

### Lancer l'application

```bash
dotnet build src/Studio.App -t:Run -f net10.0-windows10.0.19041.0
```

```bash
dotnet build src/Studio.App -t:Run -f net10.0-android
```

> Sous Windows, clonez le dépôt dans un chemin court (ex. `C:\dev\open-story-studio`) : le Windows App SDK échoue
> au-delà de 260 caractères de chemin (`APPX0002`).

## Utilisation de la CLI

```bash
dotnet run --project src/Studio.Cli -- --help
```

```text
studio device info                    Informations de l'appareil (détecté automatiquement)
studio device list                    Packs installés, dans l'ordre
studio device install pack.zip        Convertit puis installe un pack
studio device remove <id>             Supprime un pack (index sauvegardé avant)
studio device reorder <id> <id> …     Change l'ordre
studio device export <id> out.zip     Récupère un pack de l'appareil (v2/v3)
studio device backup                  Sauvegarde l'index
studio pack new "Titre" out.zip -n 3  Crée un pack « menu de 3 histoires »
studio pack validate pack.zip         Vérifie la structure
studio pack convert pack.zip -o dir   Écrit le format appareil en clair (inspection)
studio library list | import          Bibliothèque partagée avec l'application Windows
```

Options globales : `--device <dossier>` (racine de l'appareil si la détection échoue), `--experimental`.

## Connecter l'appareil

- **v2 / v3 sur PC** : l'appareil apparaît comme un lecteur contenant un fichier caché `.md` ; il est détecté
  automatiquement.
- **Android** : branchez l'appareil avec un adaptateur **USB OTG**, puis *Appareil → Choisir le dossier…* et
  sélectionnez le stockage USB. L'autorisation est mémorisée.
- **v1** : l'accès se fait en USB bas niveau via libusb. Sous Windows, installez le pilote WinUSB pour l'appareil
  (par exemple avec [Zadig](https://zadig.akeo.ie/)) et placez `libusb-1.0.dll` à côté de l'exécutable. Sous Linux,
  ajoutez une règle udev donnant l'accès au périphérique `0c45:6820` / `0c45:6840`.

Les sauvegardes d'index sont rangées dans `%LOCALAPPDATA%\OpenStoryStudio\backups\<n° de série>\`.

## Intégration et déploiement continus

| Workflow | Déclencheur | Rôle |
|---|---|---|
| [`ci.yml`](.github/workflows/ci.yml) | push, PR | Compilation Release (avertissements = erreurs), tests + couverture, APK Android et application Windows en artefacts |
| [`deploy-web.yml`](.github/workflows/deploy-web.yml) | push sur `main` | Publie la PWA et la déploie sur **Netlify** |
| [`release.yml`](.github/workflows/release.yml) | tag `v*` | Release GitHub : APK, application Windows (zip), CLI Windows/Linux/macOS |

### Configurer Netlify

La version web est statique : **aucun backend n'est nécessaire**.

1. Sur Netlify : *Add new site → Deploy manually* (ou `npx netlify-cli sites:create`), puis notez le **Site ID**
   (*Site configuration → Site details*).
2. Créez un jeton personnel : *User settings → Applications → Personal access tokens*.
3. Dans GitHub : *Settings → Secrets and variables → Actions*, ajoutez `NETLIFY_AUTH_TOKEN` et `NETLIFY_SITE_ID`.
4. Relancez le workflow *Deploy web (Netlify)*. Sans ces secrets, le site est construit (artefact `web`) mais pas
   déployé.

`_redirects` (routage de la SPA) et `_headers` (en-têtes de sécurité) sont fournis dans `src/Studio.Web/wwwroot`.

### Signer l'APK Android (optionnel)

Sans configuration, l'APK est signé avec la clé de debug (installable, mais pas publiable sur le Play Store).
Pour une clé de production, ajoutez les secrets `ANDROID_KEYSTORE_BASE64` (keystore encodé en base64),
`ANDROID_KEYSTORE_PASSWORD` et `ANDROID_KEY_ALIAS`.

## Limites connues et feuille de route

- Validation sur du vrai matériel : v2 sur PC à confirmer avec votre appareil ; v1, v3 et Android SAF/USB marqués
  expérimentaux en attendant des retours.
- Vecteurs de compatibilité octet par octet avec STUdio pour le chiffrement : à ajouter (les tests actuels vérifient
  les allers-retours et la structure des fichiers).
- Conversion audio sur Android sans ffmpeg : seuls les MP3 déjà conformes (mono 44,1 kHz) sont acceptés.
- Micro sur Android (permission WebView) et export des packs v1 : à venir.

## Crédits et licence

- Formats et protocoles : [STUdio](https://github.com/marian-m12l/studio) de Marian Muller et ses contributeurs (MPL-2.0).
  Les fichiers portés depuis STUdio le mentionnent en en-tête de documentation.
- Code : [Mozilla Public License 2.0](LICENSE).
