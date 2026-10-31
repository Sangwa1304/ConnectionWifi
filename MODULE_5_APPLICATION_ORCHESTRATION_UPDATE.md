# Module 5 — Orchestration applicative

## Périmètre

Ce module concerne la coordination des sessions Wi‑Fi Direct et TCP dans l’application WinUI.

Fichier fonctionnel concerné :

- `ShareBoost.UI/ViewModels/MainViewModel.cs`

Les projets `WindowsWifiLibrary.Core` et `WindowsWifiWpf` n’ont pas été modifiés.

## Mises à jour apportées

- Remplacement de l’attente active de trois secondes pour les demandes entrantes par une décision associée à un `TaskCompletionSource` annulable.
- Refus automatique d’une nouvelle demande entrante lorsqu’une autre décision est déjà en attente.
- Propagation de l’annulation et nettoyage de la demande entrante après acceptation, refus, annulation ou arrêt de l’application.
- Suivi des tâches de démarrage TCP côté client et serveur au lieu de créer des tâches totalement non référencées.
- Vérification explicite du résultat de la connexion Wi‑Fi Direct avant de démarrer un envoi.
- Protection contre les transferts concurrents avec un `SemaphoreSlim` d’orchestration.
- Protection contre les démarrages simultanés de sessions réseau après plusieurs événements de connexion.
- Désactivation des commandes de connexion et d’envoi pendant un transfert actif ou après libération du ViewModel.
- Mise à jour des commandes d’acceptation, de refus et d’annulation après chaque changement de session.
- Propagation des erreurs de connexion et de démarrage de transfert vers le message utilisateur au lieu de les ignorer silencieusement.
- Propagation des notifications de progression vers le `DispatcherQueue` de l’interface.
- Gestion idempotente de `Dispose`, annulation de la décision entrante et libération des sémaphores d’orchestration.
- Réinitialisation de l’état du pair et information de l’utilisateur lors de la fermeture de la connexion Wi‑Fi Direct.

## Validation

- Compilation de la solution `ConnectionWifi.slnx` réussie.
- Vérification du périmètre : aucun changement dans `WindowsWifiLibrary.Core` ou `WindowsWifiWpf`.
- Aucun code de test, prototype ou expérimental ajouté.
- La note de mise à jour est ajoutée à la racine de la solution.

## Limites volontairement conservées

Le protocole TCP, l’intégrité des fichiers et le cycle de vie natif Wi‑Fi Direct restent dans leurs modules dédiés. Le Module 5 se limite à l’orchestration applicative, à l’annulation et à la coordination entre les services et l’interface WinUI.
