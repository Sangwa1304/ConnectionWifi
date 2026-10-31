# Module 4 — Cycle de vie Wi‑Fi Direct

## Périmètre

Ce module concerne la gestion du cycle de vie du service Wi‑Fi Direct dans `WindowsWifiLibrary`.

Fichier fonctionnel concerné :

- `WindowsWifiLibrary/Services/WindowsWifiDirectService.cs`

Le contrat public `WindowsWifiLibrary/Interfaces/IWifiDirectService.cs` reste compatible avec l’implémentation existante. Les projets `WindowsWifiLibrary.Core` et `WindowsWifiWpf` n’ont pas été modifiés.

## Mises à jour apportées

- Protection des opérations publiques contre l’utilisation d’une instance déjà libérée.
- Arrêt de l’ancien publicateur avant le démarrage d’un nouveau publicateur.
- Arrêt de l’ancien `DeviceWatcher` avant le remplacement par un nouveau watcher.
- Désabonnement explicite des événements du watcher avant son arrêt et sa libération.
- Synchronisation de l’accès aux appareils connectés et aux ressources natives avec le verrou de service.
- Désabonnement des événements de statut avant la libération des appareils Wi‑Fi Direct.
- Nettoyage coordonné des appareils connectés et des invitations en attente lors de l’arrêt.
- Réinitialisation des collections internes après l’arrêt du service.
- Prévention des doubles libérations grâce à un `Dispose` idempotent.
- Conservation d’un état explicite pour le service, le publicateur et la découverte des pairs.
- Maintien de la propagation des événements de découverte, de connexion, de déconnexion et d’erreur.

## Validation

- Compilation de la solution `ConnectionWifi.slnx` réussie.
- Aucun code de test, prototype ou expérimental ajouté.
- Aucun changement effectué dans `WindowsWifiLibrary.Core` ou `WindowsWifiWpf`.
- La note de mise à jour est ajoutée à la racine de la solution.

## Limites volontairement conservées

La coordination applicative, l’orchestration des transferts et le parcours utilisateur appartiennent aux modules suivants. Ce module se limite au cycle de vie et à la libération fiable des ressources Wi‑Fi Direct.
