# Android — interface de partage et MVVM

## État livré dans cette branche

L'interface MAUI Android a été remplacée par un écran de partage organisé autour de `MainViewModel` :

- initialisation Wi-Fi Direct avec demande de permissions Android ;
- accès au panneau système de connectivité Android (l'application ne tente pas de basculer silencieusement le Wi-Fi) ;
- publication du téléphone en tant que propriétaire potentiel du groupe ;
- découverte des pairs, actualisation et suppression des entrées ;
- demande de connexion à un pair et attente de la confirmation effective de formation du groupe ;
- déconnexion ;
- sélection multiple via le sélecteur de documents Android (Storage Access Framework, donc navigation dans les fournisseurs de fichiers autorisés par l'utilisateur) ;
- copie contrôlée des éléments sélectionnés dans le cache privé de l'application pour stabiliser les sources de fichiers pendant une session ;
- file de sélection avec nom, type, taille, miniature locale pour les images et retrait individuel ou global ;
- enregistrement DI de la page, du ViewModel et du service Wi-Fi Direct.

## Consentement et actions système

L'application n'active pas le Wi-Fi par contournement, ne rejoint pas un groupe sans action utilisateur et n'accepte pas automatiquement les invitations entrantes. Les demandes de négociation entrantes restent gérées par l'interface système Android. Pour une demande sortante, l'interface indique explicitement à l'utilisateur qu'une confirmation système peut être requise.

Le sélecteur de fichiers est le sélecteur natif Android : l'accès aux fichiers est accordé par l'utilisateur au moment de la sélection, sans permission générale de lecture de tout le stockage.

## Transfert : blocage volontaire avant activation

Le bouton « Envoyer les fichiers » est volontairement désactivé. Le transport TCP Windows existant utilise deux canaux et des rôles de ports définis par son propre protocole. L'API Wi-Fi Direct Android ne fournit pas toujours l'adresse IP du client lorsque le téléphone est propriétaire du groupe. Activer un envoi Android sans résoudre le rôle réseau, l'identification de l'endpoint et le handshake commun produirait un comportement non fiable et non interopérable.

La prochaine étape doit établir un contrat de session commun Windows/Android : négociation du rôle, découverte authentifiée de l'endpoint TCP, consentement de réception applicatif, framing versionné, contrôle d'intégrité, reprise/annulation et stockage de destination via SAF. L'envoi ne doit être activé qu'après ces tests d'interopérabilité.

## Vérification

Aucun build Android ni test sur appareil physique n'a été exécuté dans l'environnement d'édition. Avant une livraison commerciale, vérifier au minimum Android 10, 12, 13, 14 et 15+ ; autorisations refusées/révoquées ; Wi-Fi coupé ; refus de demande ; formation du groupe dans les deux rôles ; sélection de gros fichiers depuis différents fournisseurs SAF ; reprise après mise en arrière-plan et rotation/process death.
