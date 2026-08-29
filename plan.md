# Plan d'amélioration production pour ConnectionWifi

## Problème et objectif
Le projet est déjà structuré autour d'un service Wi‑Fi Direct côté librairie et d'une interface WPF, mais la logique actuelle reste encore un prototype : le flux de transfert est incomplet, les états de connexion sont implicites, l'expérience utilisateur est trop orientée "sélection de fichier puis bouton", et le moteur de transfert n'est pas encore assez robuste pour un usage production. 

Le but est de faire évoluer la couche WindowsWifiLibrary.Core en un vrai moteur de transfert fiable et cohérent, et la couche WindowsWifiWpf en une application de production orientée workflow de transfert local entre pairs, en s'appuyant sur la logique décrite dans FeaturesToAdded.txt sans conserver les éléments premium comme contrainte fonctionnelle.

## Périmètre retenu
- Côté Core : métadonnées de session, négociation d'échange, endpoints HTTP, gestion des fichiers/dossiers, validation, progression, annulation, résilience, sécurité minimale.
- Côté WPF : machine d'états, interaction utilisateur, glisser-déposer, scan de peers, appairage, acceptation de réception, affichage de progression, états visuels / temps réel.
- Hors périmètre : script de tests, démonstrateur expérimental, logique premium, mise en place de fonctionnalités d'abonnement historique ou d'icône système avancée si elle n'est pas nécessaire à la production.

## Approche proposée
1. Séparer clairement les responsabilités : découverte / appairage / négociation / transfert / réception / UI.
2. Stabiliser le contrat HTTP entre pairs pour que le transfert ne dépende pas d'un mode ponctuel ou d'un flux monofichier implicite.
3. Refaire la structure d'interaction WPF autour d'une vraie machine d'états utilisateur : veille, découverte, sélection, appairage, acceptation, envoi/réception, fin/suite.
4. Ajouter les garde-fous de production : gestion d'erreurs, limites, validation des chemins, nettoyage, tracking de progression, gestion de l'annulation, dossiers de réception, affichage explicite.

## Plan détaillé

### 1) Côté Core – contract de transfert et modèle de session
Fichiers concernés : WindowsWifiLibrary.Core/Models/*.cs, Interfaces/IFileTransferService.cs, Services/FileTransferService.cs

- Formaliser le contrat d'échange de métadonnées avant l'envoi :
  - session id unique
  - nom de l'expéditeur, IP, port
  - total de fichiers, taille totale
  - liste d'éléments (fichiers + dossiers + sous-dossiers)
  - hash de fichier ou identifiant de contenu pour validation
  - statut de négociation : en attente, accepté, refusé, terminé
- Remplacer la logique de préparation actuelle fondée sur des fichiers “plats” par un arbre de métadonnées normalisé et récursif.
- Introduire les objets de résultats pour la réception et l'envoi :
  - fichier accepté / refusé / annulé
  - réponse de confirmation côté destinataire
  - erreur détaillée d'un élément
- Ajouter des règles de validation :
  - path absolu vs relatif
  - nom de fichiers et dossiers sûrs
  - doublons dans le même transfert
  - limitation de taille / nombre d'éléments si nécessaire

### 2) Côté Core – pipeline de transfert robuste
- Rendre le flux de réception compatible avec plusieurs cas :
  - un fichier unique
  - un dossier complet
  - mix d'éléments dans un même transfert
  - plusieurs fichiers avec progression globale cohérente
- Implémenter un mécanisme de streaming HTTP fiable basé sur des chunks et sur les headers Range/Content-Range.
- Attribuer un endpoint dédié pour la négociation des métadonnées, puis un endpoint de téléchargement distinct.
- Garantir la création du dossier cible et l'écriture sécurisée dans le dossier de réception.
- Gérer la progression avec une fréquence raisonnable pour éviter la surcharge UI, et exposer le statut courant par fichier et global.
- Prévoir une API d'annulation de la session sans laisser des flux ouverts.

### 3) Côté Core – fiabilité, sécurité et intégration de session
- Ajouter les contrôles de fiabilité suivants :
  - checksum de fichier (hash) pour validation post-lecture
  - reprise ou rejet si le fichier existe déjà et la cible est conflictuelle
  - retry sur erreurs réseau transitoires
  - nettoyage système des sessions inactives après timeout
- Ajouter des protections minimales de production :
  - validation du port, des adresses IP et des URLs
  - normalisation des chemins
  - gestion explicite des exceptions HttpListener / HttpClient
  - éviction des entrées de cache _servedFiles après la session
- Introduire une notion de “session active / session terminée / session annulée” pour éviter les transferts fantômes.

### 4) Côté WPF – remplacer le modèle actuel par une machine d’états
Fichiers concernés : WindowsWifiWpf/ViewModels/MainViewModel.cs, MainWindow.xaml, MainWindow.xaml.cs, App.xaml.cs

- Concevoir un état global unique pour l'application :
  - Idle / Ready
  - DiscoveringPeers
  - PeerSelected
  - PairingRequested
  - TransferNegotiation
  - Sending / Receiving
  - Completed / Cancelled / Error
- Les actions de l'utilisateur doivent déclencher des transitions explicites plutôt que des effets latéraux dispersés.
- Ajouter le concept de “source de sélection” : explorateur système, glisser-déposer, ou scan direct.
- Ajouter un “état de préparation” avec validation de ce qui est prêt à envoyer : noms, taille, type, destination, peer valide.

### 5) Côté WPF – interaction de sélection et d'appairage
- Remplacer la logique “ListBox + bouton unique” en une conception plus naturelle :
  - zone de dépôt principale avec surface de glisser-déposer
  - bouton “Parcourir” / “Ajouter” et bouton “Scanner” par action claire
  - ligne ou panel par élément sélectionné, avec actions Supprimer, Réduire, Ouvrir emplacement
  - action directe par appareil : “Envoyer à X” au lieu d’un bouton de sélection distant
- Structurer la liste des appareils détectés avec des données utiles :
  - nom
  - statut (disponible / appairé / connecté / indisponible)
  - distance ou stabilité si pertinente
  - bouton d'action contextualisé
- Mettre en place la logique de demande d'appairage :
  - si aucun peer n'est disponible, lancer le scan
  - si peer trouvé, demander confirmation locale
  - vérifier la demande côté destinataire et afficher un message clair
  - garder le statut “pairing” visible tant que l'association n'est pas vraie

### 6) Côté WPF – gestion de la négociation et du transfert
- Ajouter un écran / panneau de confirmation de réception avec :
  - nom de l'expéditeur
  - nombre d'éléments et volume total
  - liste détaillée des fichiers / dossiers
  - bouton Accepter / Refuser / Ouvrir emplacement de réception
- Ajouter un panneau de progression qui affiche :
  - transfert global en %
  - fichier courant
  - compteur de fichiers terminés
  - bouton Annuler
- Mettre en place une logique de transfert avec session active et historique récent.
- Prévoir le cas où le destinataire refuse, annule, ou ne répond pas :
  - fermeture propre de la session
  - message d'état explicite
  - retrait de la demande en cours

### 7) Côté WPF – production / UX de fin de cycle
- Préparer un flux de fin de session propre :
  - attendre la fin du transfer, garder le résultat 5 secondes ou selon configuration
  - afficher “Terminé” et permettre un nouveau transfert sans relancer toute l'application
  - conserver la connexion si l'utilisateur le demande, sinon la fermer proprement
- Ajouter l'UX de réception par défaut :
  - dossier de téléchargement standard
  - confirmation d'enregistrement ou ouverture du dossier de destination
  - historique des derniers transferts pour reprise rapide
- Rendre le design plus lisible et robuste :
  - états visuels par couleur / icône
  - taille de texte et contrastes
  - messages d'erreur non techniques pour l'utilisateur final
  - mise à jour via binding fiable

## Points de décision importants
- Le projet doit rester cohérent avec la structure actuelle (services + ViewModel + binding) sans “bricolage” de code UI dans le code-behind.
- La logique premium (background service, bubble, etc.) ne doit pas bloquer la production : elle peut être mise en place en option avec un système de fonctionnalités activables, mais pas comme prérequis du flux de base.
- Le cœur de la robustesse est à placer dans WindowsWifiLibrary.Core, et la WPF ne doit surtout pas contenir la logique métier de transfert.
- Le transport doit être état-driven et explicite, pas uniquement déclenché par des événements UI ou des méthodes async isolées.

## Livrables attendus de la mise en œuvre
- Un moteur de transfert réseau de fichiers normalisé et testable côté Core.
- Un workflow de prise en charge complet de l’appairage et du transfert entre pairs.
- Une interface WPF lisible, cohérente, plus proche d'une application de production que d'un démonstrateur.
- Un système de progression et de confirmation de réception compréhensible par l'utilisateur final.
