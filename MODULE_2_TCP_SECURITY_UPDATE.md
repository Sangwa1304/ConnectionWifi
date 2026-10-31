# Module 2 — Protocole et sécurité TCP

## Périmètre

Ce module concerne le protocole TCP et la validation des métadonnées dans `ServiceTcpChared`.

Fichiers fonctionnels concernés :

- `ServiceTcpChared/InfoMeta.cs`
- `ServiceTcpChared/StreamingData.cs`

Le fichier `.github/copilot-instructions.md` est inclus dans le commit afin de conserver les règles de contribution et de sécurité du projet.

Les projets `WindowsWifiLibrary.Core` et `WindowsWifiWpf` n'ont pas été modifiés.

## Mises à jour apportées

- Ajout d'un identifiant de session aléatoire par instance de transfert TCP.
- Propagation de l'identifiant de session dans les métadonnées échangées.
- Vérification de la cohérence de la session pour les listes et demandes de fichiers.
- Validation stricte des noms de fichiers reçus :
  - interdiction des chemins absolus ;
  - interdiction des chemins relatifs et du traversal ;
  - rejet des caractères de fichier invalides ;
  - rejet des caractères de contrôle ;
  - longueur limitée à 255 caractères.
- Limitation à 10 000 fichiers par transfert.
- Limitation à 1 To par fichier et pour le lot traité par le socle actuel.
- Rejet des métadonnées vides, mal formées ou incohérentes.
- Validation du format GUID de l'identifiant de session.
- Ajout d'une limite de taille pour les trames TCP.
- Ajout d'un délai d'expiration de 30 secondes par trame TCP.
- Association des demandes de fichiers à la session TCP active.
- Correction de l'utilisation du jeton d'annulation dans le chemin d'émission.
- Conservation de la confirmation explicite du transfert entrant.

## Règles de contribution

- Aucun code de test, prototype ou expérimental n'a été ajouté.
- Toute modification doit rester professionnelle, sécurisée et destinée à la production.
- Les architectures HTTP, ASP.NET, Kestrel et HTTPS ne font pas partie de ce module.

## Validation

- Compilation du projet `ServiceTcpChared` réussie.
- Compilation de la solution réussie.
- Vérification `git diff --check` effectuée.
- Aucun fichier de `WindowsWifiLibrary.Core` ou `WindowsWifiWpf` modifié.
