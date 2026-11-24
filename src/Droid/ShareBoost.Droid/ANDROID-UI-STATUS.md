# Android — alignement sur l'architecture Windows

## Contrat Wi-Fi Direct partagé

Le contrat `IWifiDirectService` et les modèles `WifiDirectPeer`, `WifiDirectStatus`, `WifiDirectConnectionEventArgs`, les événements de pairs et les statuts ont été déplacés dans `src/WifiDirect.Contracts` (cible `net10.0`). Windows et Android référencent maintenant le même assembly.

L'interface ne transporte aucun type WinRT. Windows conserve `WiFiDirectConnectionRequest` dans son dictionnaire privé de demandes en attente et ne publie au ViewModel que le token et les métadonnées du pair. L'implémentation Android `AndroidWifiDirectService` implémente directement `IWifiDirectService`.

Les deux ViewModels consomment le même contrat de connexion : `WifiDirectConnectionEventArgs(ServerMode, RemoteIpAddress, Id)`. Le rôle `ServerMode` conserve la convention de Windows (initiateur = true, répondant = false), afin que les ports TCP et l'ordre logique des deux canaux restent identiques.

## Transport de fichiers réutilisé

Android utilise directement `TcpServerStreaming`, `TcpClientStreaming`, `InfoMeta` et le framing existant de `ServiceTcpChared`. Aucun protocole parallèle n'a été introduit.

- Les ports et rôles restent ceux du transport Windows.
- Les deux canaux doivent être ouverts avant d'activer l'envoi.
- Les demandes de réception entrantes déclenchent une confirmation dans l'interface Android.
- Les fichiers reçus sont validés par la vérification SHA-256 existante avant leur déplacement dans le dossier privé `FileSystem.AppDataDirectory/Received`.
- La galerie reçue fournit aperçu image, ouverture avec l'application Android associée et partage via la feuille de partage native.

Lorsque le téléphone Android est propriétaire du groupe, `WifiP2pInfo` ne fournit pas l'adresse IP du client. Le transport Android démarre donc son serveur TCP sur le port correspondant au rôle et récupère l'adresse distante réelle à partir du socket TCP accepté par le client Windows. Il ne devine pas l'adresse DHCP.

## Différences imposées par Android

Android ne permet pas à une application ordinaire de remplacer la boîte de dialogue système d'acceptation d'une invitation Wi-Fi Direct entrante par un dialogue applicatif. L'OS gère cette négociation ; ShareBoost ne l'accepte jamais silencieusement. Le consentement applicatif séparé est demandé avant la réception des fichiers.

L'API Android P2P ne reproduit pas exactement le couple Windows AdvertisementPublisher + DeviceWatcher. La publication Android crée un groupe propriétaire ; la découverte et la publication sont donc exposées comme actions distinctes dans l'interface plutôt que de prétendre qu'elles sont simultanées.

## Vérification avant livraison

Aucun build Android/Windows ni test sur appareils physiques n'a été exécuté dans l'environnement d'édition. Avant release, exécuter les tests de compilation et d'interopérabilité suivants :

- Windows initiateur / Android répondant, Android initiateur / Windows répondant ;
- Android propriétaire du groupe et Windows propriétaire du groupe ;
- acceptation et refus de l'invitation système ;
- acceptation et refus de la demande de transfert ;
- transferts multiples, fichiers volumineux, doublons, interruption réseau et annulation ;
- vérification SHA-256, ouverture et partage des fichiers reçus ;
- Android 10 à 15+ (permissions, restrictions d'arrière-plan et variations OEM).

Le bouton d'envoi n'est activable qu'après établissement des deux canaux TCP.
