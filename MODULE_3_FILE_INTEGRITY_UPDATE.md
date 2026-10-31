# Module 3 — Métadonnées, intégrité et fichiers

## Périmètre

Ce module concerne exclusivement la validation des métadonnées, l'intégrité des fichiers et la gestion des fichiers temporaires dans `ServiceTcpChared`.

Fichiers fonctionnels concernés :

- `ServiceTcpChared/InfoMeta.cs`
- `ServiceTcpChared/StreamingData.cs`

Aucun commit n'est créé pour ce module sans autorisation explicite.

Les projets `WindowsWifiLibrary.Core` et `WindowsWifiWpf` n'ont pas été modifiés.

## Mises à jour apportées

- Ajout d'une empreinte SHA-256 calculée sur le fichier source.
- Transmission de l'empreinte dans les métadonnées TCP.
- Validation du format de l'empreinte reçue avant traitement.
- Vérification de la taille réelle du fichier reçu par rapport à la taille annoncée.
- Vérification de l'empreinte SHA-256 avant de considérer le fichier comme reçu avec succès.
- Déplacement vers la destination uniquement après validation complète de l'intégrité.
- Détection des flux interrompus avant réception de la taille attendue.
- Suppression des fichiers temporaires en cas d'annulation, d'interruption ou d'erreur d'intégrité.
- Remplacement des noms temporaires génériques par des noms liés à la session et à l'empreinte du fichier.
- Validation des chemins temporaires et de destination dans le dossier de téléchargement.
- Conservation d'une stratégie de renommage déterministe pour les fichiers existants.
- Remplacement du renommage récursif par une recherche itérative bornée à 100 000 possibilités.
- Correction de l'utilisation du jeton d'annulation lors de l'écriture du fichier reçu.

## Règles de contribution

- Aucun code de test, prototype ou expérimental n'a été ajouté.
- Toute modification doit rester professionnelle, sécurisée et destinée à la production.
- Le transfert reste exclusivement basé sur le protocole TCP existant de `ServiceTcpChared`.
- Aucune architecture HTTP, ASP.NET, Kestrel ou HTTPS n'a été introduite.

## Validation

- Compilation du projet `ServiceTcpChared` réussie.
- Compilation de la solution réussie.
- Vérification des erreurs sur les fichiers modifiés réussie.
- Aucun commit créé pour ce module.
