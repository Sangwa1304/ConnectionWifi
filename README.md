# ConnectionWifi

ConnectionWifi est une solution .NET orientée Windows pour transférer des fichiers entre appareils proches via Wi‑Fi Direct, avec une interface utilisateur WPF et des couches de logique réseau et de modélisation séparées. La solution est organisée autour du fichier `ConnectionWifi.slnx` et regroupe plusieurs projets pour la couche métier, le transfert, les modèles communs et l'application cliente.

## Vue d'ensemble

L'objectif du projet est de permettre :

- la découverte d'appareils compatibles Wi‑Fi Direct sur le même réseau local ;
- l'envoi de fichiers ou de groupes de fichiers vers un autre appareil ;
- la vérification de la session de transfert ;
- la négociation de l'acceptation du transfert ;
- l'affichage de l'état de progression dans une interface graphique.

Le projet est structuré selon une architecture modulaire :

- `WindowsWifiLibrary` : intégration native Wi‑Fi Direct côté Windows ;
- `WindowsWifiLibrary.Core` : cœur technique et fonctionnalités de base ;
- `LibraryModels` : modèles partagés et contrats de transfert ;
- `TcpApiTransfert` : abstraction de transfert TCP / réseau pour les sessions ;
- `WindowsWifiWpf` : application Windows desktop avec interface utilisateur ;
- `WindowsWifiLibrary.Core.Tests` : tests unitaires du noyau fonctionnel.

## Solution et projets

La solution `ConnectionWifi.slnx` contient ces projets :

- `LibraryModels/LibraryMI.csproj`
- `TcpApiTransfert/TcpApiTransfert.csproj`
- `WindowsWifiLibrary.Core/WindowsWifiLibrary.Core.csproj`
- `WindowsWifiLibrary.Core.Tests/WindowsWifiLibrary.Core.Tests.csproj`
- `WindowsWifiLibrary/WindowsWifiLibrary.csproj`
- `WindowsWifiWpf/WindowsWifiWpf.csproj`

## Projets clés

### 1) LibraryModels

Projet de modèles partagés et interfaces de transfert.

Il contient principalement :

- `FileI` : représentation d'un fichier à envoyer ;
- `TransferSessionMetadata` : métadonnées complètes d'une session de transfert ;
- `TransferItemMetadata` : élément de fichier ou de dossier dans le transfert ;
- `TransferDecision` : acceptation / refuser / annuler ;
- `TransferNegotiationStatus` : états de négociation ;
- `TransferWorkflowState` : cycle de vie visuel de l'interface ;
- `FileTransferProgress` : suivi de progression ;
- `PeerDevice` : description d'un appareil pair ;
- `TransferValidationResult` : validation avant envoi.

Ce projet sert de contrat entre la couche d'interface, le service de transfert et les vues WPF.

### 2) WindowsWifiLibrary

C'est la couche d'intégration Windows Wi‑Fi Direct.

Le composant central est :

- `WindowsWifiLibrary.Services.WifiDirectService`

Il s'appuie sur les API Windows :

- `WiFiDirectAdvertisementPublisher`
- `WiFiDirectConnectionListener`
- `DeviceWatcher`
- `WiFiDirectDevice`

Il offre la logique pour :

- initialiser le support Wi‑Fi Direct ;
- publier l'appareil pour être découvert ;
- rechercher les pairs disponibles ;
- recevoir des demandes d'invitation ;
- accepter ou refuser une invitation ;
- établir une connexion et déclencher des événements de statut.

Les événements exposés incluent :

- `StatusChanged`
- `PeerDiscovered`
- `PeerUpdated`
- `PeerRemoved`
- `InvitationReceived`
- `ConnectionEstablished`
- `ConnectionClosed`
- `Error`

### 3) TcpApiTransfert

Ce projet contient la logique de transfert réseau basée sur des flux TCP / API de service, et implémente l'interface `IFileTransferService`.

Il est conçu pour prendre en charge :

- l'écoute locale d'une connexion entrante ;
- la préparation des métadonnées d'un transfert ;
- la validation des fichiers ;
- la négociation de session avec le pair distant ;
- le téléchargement ou la réception des données ;
- l'annulation de la session en cours.

Le code actuel montre déjà la structure essentielle d'un service d'échange de fichiers : écoute de socket, gestion des événements de connexion, et points d'extension pour la négociation de session et les flux de données.

### 4) WindowsWifiLibrary.Core

C'est la couche métier de base du noyau applicatif. Elle regroupe les éléments d'architecture technique et les abstractions liées au transfert, incluant les mécanismes de préparation et de validation d'une session.

Cette couche fournit la base pour :

- préparer les métadonnées d'un transfert ;
- vérifier les fichiers avant envoi ;
- décider du comportement de la session ;
- gérer les états de progression logicielle.

### 5) WindowsWifiWpf

C'est l'application cliente Windows desktop.

Le point d'entrée principal est la fenêtre `MainWindow`, qui :

- initialise le `MainViewModel` ;
- gère les opérations de drag-and-drop ;
- expose les événements UI liés aux fichiers et aux transferts ;
- connecte l'interface avec les services de Wi‑Fi Direct et de transfert.

Le `MainViewModel` gère une grande partie de la logique d'application :

- sélection de fichiers de l'utilisateur ;
- liste des appareils découverts ;
- dossiers de destination ;
- états de progression ;
- historique des transferts ;
- acceptation / refus d'un transfert entrant ;
- scan, connexion et gestion des sessions.

La vue WPF met aussi en place un système d'état visuel pour représenter les différents modes de la procédure de transfert :

- `Idle`
- `DiscoveringPeers`
- `PeerSelected`
- `PairingRequested`
- `TransferNegotiation`
- `Sending`
- `Receiving`
- `Completed`
- `Cancelled`
- `Error`

### 6) WindowsWifiLibrary.Core.Tests

Ce projet est dédié aux tests du noyau fonctionnel. Il vise à sécuriser les comportements de la couche d'abstraction logicielle, notamment la validation des fichiers, les états de session et la fiabilité des composants partagés.

## Architecture fonctionnelle

Le projet suit un modèle centré sur la session de transfert :

1. L'utilisateur sélectionne des fichiers dans l'interface WPF.
2. Le `MainViewModel` prépare la liste des fichiers à envoyer.
3. La couche Wi‑Fi Direct découvre les pairs disponibles.
4. L'utilisateur choisit un appareil et lance une connexion.
5. Une session de transfert est créée avec métadonnées (nom, taille, fichiers, port, identifiant unique).
6. La négociation de l'acceptation se fait entre les deux appareils.
7. Le transfert est exécuté et son état est suivi en temps réel.
8. Le résultat est rapporté dans l'historique et dans l'interface utilisateur.

## Technologies et dépendances

- .NET 10
- WPF (Windows Presentation Foundation)
- Wi‑Fi Direct API de Windows
- Bibliothèques de modèles partagés (`LibraryMI`)
- Flux réseau de type TCP / socket / HTTP-like services
- Microsoft.AspNetCore.App (référencé par le projet WPF)

## Prérequis

Pour compiler et exécuter la solution sur un poste Windows :

- Windows 10 ou 11 ;
- .NET SDK 10 preview ou compatible ;
- Visual Studio 2022 avec charge de travail .NET desktop / WPF ;
- appareil compatible Wi‑Fi Direct ;
- permissions système nécessaires à la découverte et à la connexion Wi‑Fi Direct.

## Build

À partir de la racine du dépôt :

```powershell
dotnet restore

dotnet build ConnectionWifi.slnx
```

## Exécution

L'application WPF peut être démarrée avec :

```powershell
dotnet run --project WindowsWifiWpf/WindowsWifiWpf.csproj
```

## Cas d'usage

La solution est conçue pour les scénarios suivants :

- transfert rapide de fichiers entre deux ordinateurs Windows sur le même réseau local ;
- envoi sans passer par Internet ;
- partage de documents, photos ou ensembles de fichiers volumineux ;
- usage dans des environnements où Wi‑Fi Direct est préféré à un réseau classique ou à un court-circuit de serveur centralisé.

## Points d'attention

Le projet est une base de solution technique de transfert local robuste et modulée. Certaines parties de la logique de transfert sont encore structurées comme des points d'extension ou des implémentations en cours, notamment dans `TcpApiTransfert`, où plusieurs méthodes restent des stubs à compléter selon le besoin applicatif final.

En pratique, les éléments les plus avancés sont :

- l'orchestration WPF ;
- les modèles partagés ;
- la couche de découverte Wi‑Fi Direct ;
- la préparation de l'architecture de session et de négociation de transfert.

## Conclusion

ConnectionWifi est une solution de transfert local entre appareils basée sur le standard Wi‑Fi Direct, organisée en couches et pensée pour être extensible. Elle sépare clairement :

- la couche UI (`WindowsWifiWpf`),
- les services réseau (`TcpApiTransfert`),
- le cœur fonctionnel (`WindowsWifiLibrary.Core`),
- les modèles partagés (`LibraryModels`),
- et la prise en charge native Windows (`WindowsWifiLibrary`).

Cette conception permet de rendre le système plus maintenable, plus testable et plus facilement adaptable à de nouvelles fonctionnalités de transfert, de supervision ou d'intégration réseau.
