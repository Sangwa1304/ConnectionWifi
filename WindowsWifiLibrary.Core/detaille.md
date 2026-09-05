Voici l'explication exhaustive et approfondie de l'architecture, du fonctionnement interne, des mécanismes réseau, de la concurrence et des structures de données de ce projet de transfert de fichiers Wi-Fi P2P.

---

# 1. Architecture Globale & Principes de Fonctionnement

Le projet `WindowsWifiLibrary` implémente une solution de transfert de fichiers **Peer-to-Peer (P2P)** sur réseau local (Wi-Fi) basée sur le protocole **HTTP/1.1**.

### Concepts architecturaux clés :

1. **Modèle Symétrique (Chaque appareil est à la fois Serveur et Client) :**
* **Serveur HTTP embarqué :** Chaque machine exécute une instance de `HttpListener` pour recevoir des requêtes de transfert, des confirmations et des demandes de téléchargement de fichiers.


* **Client HTTP (`HttpClient`) :** Chaque machine utilise `HttpClient` pour émettre des demandes vers les machines distantes ou télécharger des morceaux de données.




2. **Négociation Distribuée Asynchrone :**
* Lorsqu'une machine A veut envoyer un fichier à une machine B, elle envoie d'abord une requête HTTP `POST /api/transfer/request` contenant le profil complet de la session (taille, nombre de fichiers, arborescence).


* La machine A met sa tâche en attente à l'aide d'un `TaskCompletionSource<TransferDecision>`.


* La machine B affiche une notification à l'utilisateur. Dès que l'utilisateur accepte ou refuse, B répond par un `POST /api/transfer/accept` à la machine A, ce qui débloque la tâche en attente sur la machine A.




3. **Transfert de Fichiers en Streaming & Support des Reprises :**
* Les fichiers sont servis via un endpoint HTTP `GET /api/transfer/download` qui prend en charge les en-têtes HTTP `Range: bytes=start-end` (HTTP 206 Partial Content).


* Le téléchargement s'effectue par blocs de mémoire d'une taille optimale (80 Ko / 81 920 octets) afin d'éviter la surconsommation de RAM lors du transfert de gros fichiers.





---

# 2. Analyse Détaillée des Modèles et Structures de Données (`Core.Models`)

### 2.1. Énumérations d'État (`TransferSessionState` & `TransferNegotiationStatus`)

* **`TransferNegotiationStatus`** : Représente l'état de la négociation initiale entre les deux appareils :


* `Pending` (0) : Demande envoyée, en attente de la réponse du destinataire.


* `Accepted` (1) : Le destinataire a accepté le transfert.


* `Refused` (2) : Le destinataire a décliné le transfert.


* `Cancelled` (3) : Le transfert a été annulé.


* `Completed` (4) : Négociation et transfert terminés avec succès.


* `Error` (5) : Une erreur est survenue lors de la négociation.




* **`TransferSessionState`** : Représente le cycle de vie global de la session :


* `Created` $\rightarrow$ `Negotiating` $\rightarrow$ `ReadyToSend` $\rightarrow$ `Sending` / `Receiving` $\rightarrow$ `Completed` (ou `Cancelled` / `Failed`).





### 2.2. Représentation Hiérarchique des Fichiers (`TransferItemMetadata`)

Fichier : `TransferItemMetadata.cs`


Cette classe utilise le **design pattern Composite** pour représenter une arborescence complète de fichiers et de répertoires :

* `RelativePath` : Le chemin relatif de l'élément à partir du dossier racine du transfert (ex: `Dossier/Projet/photo.jpg`). C'est ce chemin qui est transmis sur le réseau.


* `FullLocalPath` : Le chemin absolu sur le disque local de l'émetteur (ex: `C:\Users\Sangwa\Documents\Dossier\Projet\photo.jpg`).


* `IsDirectory` : Booleen indiquant s'il s'agit d'un dossier (`true`) ou d'un fichier (`false`).


* `Size` : Taille en octets.


* `Children` : Liste de type `List<TransferItemMetadata>` contenant les sous-fichiers et sous-dossiers si `IsDirectory == true`.



### 2.3. Métadonnées Globales de Session (`TransferSessionMetadata`)

Fichier : `TransferSessionMetadata.cs`


C'est le contrat de données échangé via JSON entre les appareils pour décrire la transaction dans son ensemble :

* `SessionId` : Identifiant unique généré sous forme de GUID (`Guid.NewGuid().ToString()`).


* `SenderName` : Nom de la machine émettrice (`Environment.MachineName`).


* `SenderIp` & `SenderPort` : Adresse IP et Port TCP sur lesquels le serveur de l'émetteur écoute.


* `TotalSize` & `TotalFiles` : Cumul de la taille totale en octets et du nombre total de fichiers.


* `Items` : Liste hiérarchique des éléments racines à transférer (`List<TransferItemMetadata>`).



### 2.4. Suivi de Progression (`FileTransferProgress` & `ProgressStreamContent`)

* **`FileTransferProgress`** : Encapsule les métriques de transfert :


* `BytesTransferred` : Nombre d'octets actuellement transférés.


* `TotalBytes` : Taille totale attendue.


* `Percentage` : Propriété calculée automatiquement : `TotalBytes == 0 ? 0 : (double)BytesTransferred / TotalBytes * 100`.


* `CurrentFileName` : Nom du fichier en cours d'acheminement.




* **`ProgressStreamContent`** :


* Hérite de `HttpContent`. Elle surcharge la méthode `SerializeToStreamAsync( destination, context )`.


* Lors de l'envoi HTTP, elle lit le flux source par blocs de `bufferSize` (80 Ko par défaut). À chaque écriture sur le flux réseau `destination`, elle invoque `_progress?.Report(...)` pour avertir l'interface utilisateur en temps réel de la progression sans bloquer le thread d'E/S.


* Intègre un vérification systématique de `CancellationToken.ThrowIfCancellationRequested()` dans la boucle de lecture.





---

# 3. Le Service Principal de Transfert (`FileTransferService.cs`)

Le service `FileTransferService` constitue le cœur opérationnel de la bibliothèque. Il implémente `IFileTransferService` et `IDisposable`.

### 3.1. Dictionnaires d'État Interne

* `_pairingRequests` (`Dictionary<string, TaskCompletionSource<TransferDecision>>`) : Maintient en mémoire les demandes d'appairage sortantes en attente de réponse. La clé est le `SessionId`.


* `_servedFiles` (`Dictionary<string, string>`) : Fait la liaison directe entre un `RelativePath` (ex: `Docs/Rapport.pdf`) et le `FullLocalPath` réel sur le disque de l'émetteur (ex: `D:\Docs\Rapport.pdf`).


* `_activeSessions` (`Dictionary<string, TransferSessionMetadata>`) : Stocke les sessions actives sur la machine.



### 3.2. Le Serveur HTTP Embarqué (`StartListeningAsync`)

```csharp
public async Task StartListeningAsync(string localIp, int port, CancellationToken cancellationToken)

```

1. Démarre une instance de `HttpListener` configurée sur le préfixe `http://{localIp}:{port}/`.


2. Boucle indéfiniment tant que le jeton d'annulation `_listenerCts` n'est pas activé.


3. À chaque requête HTTP entrante (`GetContextAsync()`), le traitement est délégué à un composant d'arrière-plan via `Task.Run(() => HandleIncomingRequestAsync(context))`. Cela garantit que le serveur peut traiter plusieurs requêtes simultanément sans bloquer l'écouteur principal.



### 3.3. Routage et Traitement des Requêtes HTTP (`HandleIncomingRequestAsync`)

La méthode analyse la méthode HTTP (`GET`, `POST`) et le chemin d'accès (`AbsolutePath`) :

1. **`POST /api/transfer/request`** :


* Désérialise le corps de la requête JSON en un objet `TransferSessionMetadata`.


* Enregistre la session dans `_activeSessions`.


* Déclenche l'événement C# `IncomingTransferRequested?.Invoke(this, metadata)` pour notifier l'UI ou l'application qu'un autre appareil souhaite envoyer des fichiers.


* Renvoie le code de statut HTTP `202 Accepted`.




2. **`POST /api/transfer/accept`** :


* Reçoit la décision de l'utilisateur distant (`TransferDecision`).


* Extrait le paramètre de requête `sessionId`.


* Recherche dans le dictionnaire `_pairingRequests` la promesse `TaskCompletionSource` associée.


* Exécute `tcs.TrySetResult(decision)`, ce qui débloque instantanément la méthode `RequestPairingAsync` qui était en attente du côté de l'émetteur.




3. **`GET /api/transfer/session/{sessionId}`** :


* Permet à un destinataire d'interroger à tout moment l'état et les métadonnées d'une session à partir de son identifiant.


* Sérialise l'objet `TransferSessionMetadata` en JSON et le renvoie avec un statut HTTP `200 OK`.




4. **`GET /api/transfer/download?path={relativePath}`** :


* Extrait le chemin relatif demandé de la query string.


* Résout le chemin local correspondant dans le dictionnaire `_servedFiles`.


* Vérifie que le fichier existe puis passe le relais à `ServeFileChunkAsync`.





### 3.4. Transmission Streaming avec Support HTTP Range (`ServeFileChunkAsync`)

Fichier : `FileTransferService.cs`


Ce composant assure la lecture et l'envoi physique du fichier sur le réseau :

* **Analyse de l'en-tête `Range` :** Si le client envoie l'en-tête `Range: bytes=start-end`, le serveur calcule la plage exacte demandée, définit le code de statut HTTP à `206 Partial Content` et ajoute l'en-tête `Content-Range`. Sinon, il renvoie un statut `200 OK`.


* **E/S Asynchrones non-bloquantes :** Le fichier est ouvert en mode asynchrone (`useAsync: true`) avec accès en lecture partagée (`FileShare.Read`).


* **Boucle de transfert :** Les données sont lues dans un tampon de 80 Ko (`new byte[81920]`) puis écrites sur le flux de sortie de la réponse HTTP (`response.OutputStream`) jusqu'à épuisement du fichier ou de la plage demandée.



### 3.5. Préparation des Métadonnées & Validation (`PrepareMetadataAsync`)

Lorsqu'un utilisateur sélectionne des fichiers/dossiers à envoyer :

1. **Validation (`ValidateTransferAsync`)** :


* Vérifie l'existence physique de chaque chemin via `File.Exists` ou `Directory.Exists`.


* Normalise les chemins (convertit les chemins relatifs en chemins absolus canoniques via `Path.GetFullPath`).


* Élimine les doublons grâce à un `HashSet<string>(StringComparer.OrdinalIgnoreCase)`.




2. **Construction Récursive (`BuildDirectoryMetadata`)** :


* Si un chemin est un fichier, il crée un `TransferItemMetadata`, calcule sa taille, met à jour `TotalSize` et enregistre le fichier dans `_servedFiles`.


* Si un chemin est un répertoire, la méthode `BuildDirectoryMetadata` parcourt récursivement tous les sous-fichiers (`dir.GetFiles()`) et sous-dossiers (`dir.GetDirectories()`). Elle reconstruit l'arborescence complète sous forme d'arbre composite et attribue un chemin relatif normalisé avec des séparateurs slash `/` à chaque élément.





### 3.6. Envoi de la Demande d'Appairage (`RequestPairingAsync`)

```csharp
public async Task<bool> RequestPairingAsync(PeerDevice targetDevice, TransferSessionMetadata metadata, CancellationToken cancellationToken)

```

1. Crée un `TaskCompletionSource<TransferDecision>`.


2. Stocke ce TCS dans `_pairingRequests[metadata.SessionId]`.


3. Envoie une requête HTTP `POST` au destinataire à l'adresse `http://{targetDevice.IpAddress}:{targetDevice.Port}/api/transfer/request` contenant le JSON des métadonnées.


4. Utilise `cancellationToken.Register()` pour permettre l'annulation de l'attente.


5. **Attente Asynchrone :** Exécute `await tcs.Task`. Le thread d'exécution est libéré. Il restera suspendu jusqu'à ce que le destinataire réponde sur le point de terminaison `/api/transfer/accept` du serveur local de l'émetteur.


6. Lorsque la réponse arrive, le TCS est complété, et `RequestPairingAsync` reprend son exécution, renvoyant `true` si la décision a été acceptée ou `false` si elle a été refusée ou si une erreur est survenue.



---

# 4. Le Service de Réception Dédié (`TransferReceiverService.cs`)

Fichier : `TransferReceiverService.cs`


Ce service est utilisé par l'appareil récepteur pour piloter le téléchargement des données exposées par l'émetteur.

### 4.1. Récupération des Métadonnées & Acceptation

* `GetSessionAsync(...)` : Interroge l'URL distante `/api/transfer/session/{sessionId}` pour obtenir la description à jour du transfert.


* `AcceptTransferAsync(...)` : Transmet la décision d'acceptation et le dossier de destination choisi à l'émetteur.



### 4.2. Téléchargement Récursif & Reconstitution sur Disque

```csharp
public async Task DownloadSessionAsync(string senderBaseUrl, TransferSessionMetadata session, string destinationFolder, IProgress<FileTransferProgress> progress, CancellationToken cancellationToken)

```

1. Réinitialise le compteur global d'octets transférés de manière atomique : `Interlocked.Exchange(ref _totalTransferred, 0)`.


2. Invoque la méthode récursive `DownloadItemsAsync` :


* **Gestion des Dossiers :** Si un élément est un répertoire (`item.IsDirectory`), le service boucle sur ses enfants (`item.Children`) et relance le téléchargement de manière récursive.


* **Reconstitution des Chemins :** Les séparateurs de chemins (`/` ou `\`) sont convertis au format spécifique du système d'exploitation hôte (`Path.DirectorySeparatorChar`). Le dossier parent cible est automatiquement créé s'il n'existe pas (`Directory.CreateDirectory`).





### 4.3. Téléchargement Binaire et Progression Thread-Safe (`DownloadFileAsync`)

1. Construit l'URL de téléchargement : `{senderBaseUrl}/api/transfer/download?sessionId={sessionId}&path={relativePath}`.


2. Effectue une requête HTTP `GET` avec l'option `HttpCompletionOption.ResponseHeadersRead`. Cela permet de démarrer la lecture du flux immédiatement après la réception des en-têtes HTTP, sans charger tout le fichier en mémoire RAM.


3. Ouvre le flux de destination local via `File.Create(destinationPath)`.


4. **Boucle de Streaming :**
* Lit le flux entrant par blocs de 80 Ko (`new byte[81920]`).


* Écrit les octets dans le fichier local.


* Met à jour le total d'octets transférés au niveau de la session en utilisant l'opération thread-safe `Interlocked.Add(ref _totalTransferred, bytesRead)`.


* Notifie le rapporteur de progression `progress.Report(...)` avec l'instance réutilisée `FileTransferProgress`.




5. **Contrôle d'Intégrité :** Vérifie que le nombre d'octets téléchargés correspond exactement à la taille attendue (`expectedLength`). Si ce n'est pas le cas, une exception `InvalidOperationException` est levée.



---

# 5. Synthèse des Endpoints de l'API REST Inter-Appareils

| Méthode | Route / Endpoint | Émetteur / Destinataire | Rôle et Fonctionnement |
| --- | --- | --- | --- |
| `POST` | `/api/transfer/request` | Client $\rightarrow$ Serveur Destinataire | Transmet le JSON `TransferSessionMetadata`. Déclenche l'événement local `IncomingTransferRequested`.

 |
| `POST` | `/api/transfer/accept` | Destinataire $\rightarrow$ Serveur Émetteur | Transmet la décision (`TransferDecision`). Libère le `TaskCompletionSource` en attente chez l'émetteur.

 |
| `GET` | `/api/transfer/session/{id}` | Client $\rightarrow$ Serveur | Renvoie les métadonnées de la session sous forme de JSON.

 |
| `GET` | `/api/transfer/download` | Client $\rightarrow$ Serveur Émetteur | Transfère le contenu binaire d'un fichier via query string `?path=...`. Supporte l'en-tête HTTP `Range`.

 |

---

# 6. Fiabilité, Concurrence et Sécurité Mémoire

1. **Aucune Saturation de la Mémoire RAM :**
Grâce à l'utilisation systématique de tampons de taille fixe (80 Ko / 81 920 octets) et du streaming asynchrone (`Stream.ReadAsync` / `Stream.WriteAsync`), l'empreinte mémoire reste minime et constante, quelle que soit la taille des fichiers transférés (plusieurs gigaoctets).


2. **Accès Mutex et Thread-Safety :**
Lors du téléchargement multi-fichiers, la mise à jour des octets globaux transférés s'appuie sur la classe `Interlocked` (`Interlocked.Add` et `Interlocked.Read`), évitant tout risque de *race condition* (concurrence critique) lors des accès simultanés.


3. **Interruption et Annulation Réactive :**
Chaque méthode asynchrone accepte un `CancellationToken`. Les boucles de lecture réseau et de fichier testent continuellement ce jeton, assurant une libération immédiate des ressources réseau et système dès qu'un transfert est annulé.


4. **Gestion Rapprochée des Erreurs Réseau :**
Les interruptions brutales de connexion ou les blocages liés au pare-feu Windows sont capturés spécifiquement via l'interception des exceptions `HttpRequestException` et `HttpListenerException`, garantissant un passage propre de la session à l'état `TransferSessionState.Failed` sans faire planter l'application.



---

### Résumé des fichiers du projet

* `DeviceOperatingMode.cs` : Modes de fonctionnement UI/Service.


* `FileTransferEndpoint.cs` : DTO décrivant un point de terminaison URI.


* `FileTransferProgress.cs` : Structure de rapport de progression.


* `PeerDevice.cs` : Représentation d'un nœud du réseau.


* `ProgressStreamContent.cs` : Surcharge de `HttpContent` pour le rapport de progression en streaming.


* `TransferDecision.cs` : DTO de réponse à une proposition de transfert.


* `TransferItemMetadata.cs` : Structure d'un fichier ou d'un dossier (Pattern Composite).


* `TransferNegotiationStatus.cs` : Énumération d'état de négociation.


* `TransferSessionMetadata.cs` : Métadonnées complètes d'une session de transfert.


* `TransferSessionState.cs` : Énumération du cycle de vie de la session.


* `IFileTransferService.cs` : Interface du service de transfert.


* `FileTransferService.cs` : Implémentation du serveur HTTP, de la négociation et du service d'envoi.


* `TransferReceiverService.cs` : Client dédié au téléchargement et à la reconstitution d'arborescence.


* `TransferValidationResult.cs` : Structure de résultat de validation de chemins.