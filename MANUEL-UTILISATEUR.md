# Manuel d’utilisation — 2gether

## 1. Présentation

2gether est une application de rencontres de quartier. Après la création et la confirmation d’un compte, elle permet de renseigner un profil, de choisir des affinités, de découvrir des profils, d’utiliser une carte, d’échanger des messages et de gérer des photos publiques ou privées.

Les profils affichés avant connexion sont des exemples de démonstration. La version de démonstration ne constitue pas une communauté réelle.

## 2. Créer un compte

1. Ouvrir l’adresse fournie par l’administrateur.
2. Choisir **Créer un compte**.
3. Saisir une adresse e-mail, un mot de passe et un prénom ou surnom.
4. Le mot de passe doit contenir au moins 10 caractères et un chiffre.
5. Ouvrir le courriel de confirmation et suivre le lien dans l’heure. Tant que l’adresse n’est pas confirmée, la connexion et les fonctions réservées aux membres restent indisponibles.
6. Revenir à l’application, choisir **J’ai confirmé, me connecter**, puis terminer les étapes du profil.

Si un courriel tarde à arriver, vérifier les courriers indésirables et utiliser **Renvoyer le lien**. En environnement local, le serveur n’envoie pas de vrai courriel : le message `.eml` est déposé dans `Server/App_Data/Mail` (ou dans le dossier défini par l’administrateur).

## 3. Se connecter avec un mot de passe

1. Choisir **J’ai déjà un compte**.
2. Saisir l’adresse confirmée et le mot de passe.
3. En cas d’échec, vérifier l’adresse, le mot de passe et la confirmation de l’adresse. Après plusieurs tentatives, attendre avant de recommencer.

## 4. Mot de passe oublié

1. Sur l’écran de connexion, ouvrir **Mot de passe oublié ?**.
2. Saisir l’adresse e-mail associée au compte et demander un code.
3. Si le compte est confirmé, relever le code dans le courriel de récupération. Une réponse identique est affichée même si aucun compte ne correspond à l’adresse.
4. Entrer le code, saisir le nouveau mot de passe deux fois, puis le valider. Il doit contenir au moins 10 caractères et un chiffre.
5. Le code est à usage unique et expire au bout d’une heure. En cas de code invalide, expiré ou déjà utilisé, demander un nouveau code.
6. Se reconnecter avec le nouveau mot de passe.

En environnement local, rechercher le message intitulé **Récupération de ton compte 2gether** dans le dossier de courriels de test. Ne pas partager le code : toute personne qui le possède peut renouveler le mot de passe pendant sa période de validité.

## 5. Se connecter avec une passkey

Une passkey (clé d’accès) permet de se connecter sans saisir le mot de passe. Pour en créer une :

1. Se connecter avec le mot de passe et confirmer l’adresse e-mail.
2. Ouvrir **Mon profil**, puis **Gérer mes passkeys**.
3. Choisir **Ajouter une passkey sur cet appareil** et suivre l’invite du navigateur ou du système : empreinte, reconnaissance faciale, code de l’appareil ou clé de sécurité compatible.
4. Pour la connexion suivante, choisir **J’ai déjà un compte**, puis **Se connecter avec une passkey**.
5. Sélectionner le compte/la passkey dans la fenêtre système et confirmer l’authentification locale.

La connexion par passkey associe la possession de la clé privée de l’appareil à une vérification locale obligatoire. C’est la voie sans mot de passe prévue pour fournir ces deux éléments d’authentification. Une empreinte ou un visage peut être utilisé lorsque l’appareil le propose ; son code de déverrouillage peut également être accepté par le système.

**La biométrie ne quitte pas l’appareil.** Le navigateur et l’OS effectuent la vérification. 2gether ne reçoit ni l’image du visage, ni l’empreinte, ni le code ; le serveur conserve seulement la clé publique, l’identifiant de la passkey et son compteur cryptographique. La passkey est propre à l’origine du site et résiste mieux à l’hameçonnage qu’un mot de passe.

Les passkeys nécessitent un navigateur compatible et une origine sécurisée : HTTPS en production, ou `localhost` en développement. Si cette option n’est pas disponible, utiliser le mot de passe ou un navigateur/appareil compatible.

### Gérer les passkeys

Dans **Mon profil > Gérer mes passkeys**, la liste montre les clés enregistrées, leur date d’ajout et leur dernière utilisation. Une clé peut être supprimée de ce compte depuis cette page. Ajouter d’abord une autre méthode de connexion ou une seconde passkey, surtout avant de retirer la seule clé d’un appareil.

## 6. Compléter et utiliser le profil

- **Affinités :** sélectionner des activités et des raisons d’affinité pour affiner les suggestions.
- **Profil :** renseigner les informations demandées et enregistrer les modifications. L’âge doit être compris entre 18 et 120 ans.
- **Localisation :** l’accès au GPS est facultatif et soumis à l’autorisation du navigateur. L’utilisateur peut supprimer la position enregistrée.
- **Découvrir :** parcourir les profils visibles et réduire ou élargir le rayon de recherche.
- **Carte :** afficher la répartition approximative des profils. Une connexion Internet est nécessaire pour les tuiles de carte.
- **Photos :** gérer l’avatar et les bibliothèques publiques et privées selon les limites indiquées dans l’application. Les demandes d’accès aux photos privées sont soumises à l’accord du propriétaire.
- **Messages :** démarrer une conversation depuis un profil et retrouver les échanges dans l’onglet correspondant.
- **Sécurité :** depuis le profil détaillé d’un membre, le signaler ou le bloquer. Un blocage masque les profils et conversations dans les deux sens et empêche les nouveaux messages. Gérer ou annuler un blocage depuis **Membres bloqués**.
- **Aperçu visiteur :** vérifier la présentation du profil.

## 7. Supprimer son compte

1. Depuis ton profil, choisir **Supprimer mon compte**.
2. Lire les données concernées, saisir le mot de passe actuel et confirmer l’action.
3. La suppression déconnecte l’appareil et efface du serveur le profil, les photos, les passkeys, les demandes d’accès aux photos ainsi que les messages envoyés et reçus.

La suppression est définitive. Les signalements auxquels tu as participé et tes blocages sont également effacés. Les sauvegardes du serveur peuvent conserver temporairement une copie jusqu’à leur expiration selon la politique de sauvegarde de l’exploitant. En cas de problème de connexion ou d’oubli du mot de passe, utilise d’abord la récupération du compte.

## 8. Vie privée et dépannage

- La localisation GPS est facultative. Les coordonnées exactes sont stockées par le serveur pour les comptes ; les autres membres ne voient qu’une approximation.
- Les photos téléversées sont redimensionnées et leurs métadonnées sont supprimées par le serveur.
- Les courriels locaux contiennent des liens ou des codes temporaires : ne pas publier les fichiers `.eml` et restreindre les permissions du dossier.
- Si le serveur ou la boîte SMTP est indisponible, la demande est signalée comme non aboutie. Réessayer ultérieurement ; ne pas multiplier les demandes de code.
- Pour se déconnecter, utiliser le bouton **Se déconnecter** dans le profil.
- Pour toute question sur les sauvegardes et leur durée de conservation, contacter l’administrateur.
