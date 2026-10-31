# Module 1 — Socle du transfert TCP

## Périmètre

Ce module concerne exclusivement le socle TCP du projet `ServiceTcpChared`.

Fichiers concernés :

- `ServiceTcpChared/StreamingData.cs`
- `ServiceTcpChared/TcpClientStreaming.cs`
- `ServiceTcpChared/TcpServerStreaming.cs`

Les projets `WindowsWifiLibrary.Core` et `WindowsWifiWpf` n'ont pas été modifiés.

## Mises à jour apportées

- Suivi explicite des tâches d'émission et de réception TCP.
- Annulation centralisée des opérations via des `CancellationTokenSource` liés au jeton fourni.
- Arrêt propre des flux réseau, clients TCP et listeners TCP.
- Prévention des doubles démarrages d'émission, de réception et de connexion.
- Gestion des lectures TCP partielles avec une lecture exacte jusqu'à réception du contenu attendu.
- Uniformisation du framing TCP avec un préfixe de longueur pour les métadonnées et les réponses booléennes.
- Validation de la taille des trames reçues et envoyées.
- Gestion des connexions fermées par le pair et notification de la fermeture via `ConnectionClosed`.
- Correction du calcul de progression pour les fichiers vides et bornage de la progression entre 0 et 100 %.
- Utilisation d'une taille de buffer TCP constante de 2 Mo.
- Suppression des tâches d'arrière-plan lancées sans suivi direct dans les connexions client et serveur.
- Libération des anciennes ressources avant l'établissement d'une nouvelle connexion.

## Validation

- Compilation du projet `ServiceTcpChared` réussie.
- Compilation de la solution réussie.
- Aucun code de test, prototype ou expérimental ajouté.
- Aucun changement effectué dans `WindowsWifiLibrary.Core` ou `WindowsWifiWpf`.

## Limites volontairement conservées

La validation des sessions, l'intégrité cryptographique des fichiers, les limites métier des métadonnées et les contrôles de sécurité avancés appartiennent aux modules suivants. Ils ne sont pas inclus dans le module 1.
