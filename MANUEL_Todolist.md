# MANUEL_Todolist

## But

Ce document regroupe les décisions et démarches qui nécessitent l’exploitant de 2gether. Il indique quoi réunir, où le configurer et comment vérifier chaque étape. Le déploiement et la bêta publique ne sont pas prêts tant que les éléments bloquants ne sont pas validés.

**Ne transmets jamais les mots de passe SMTP, clés Stripe, secrets webhook, clés Data Protection ou sauvegardes dans une conversation ou dans Git.** Saisis les secrets directement dans le coffre de l’hébergeur.

## 1. Choisir l’hébergement et le domaine

- [ ] Choisir un hébergeur supportant .NET 10 avec stockage persistant, puis réserver/configurer le domaine.
- [ ] Préparer une seule instance Staging derrière HTTPS et, si nécessaire, un proxy inverse.
- [ ] Prévoir un chemin persistant pour SQLite et un autre pour les clés Data Protection.
- [ ] Garder l’adresse IP du proxy de confiance; elle est nécessaire si les en-têtes transférés doivent être utilisés.

**Configuration Staging :** `ASPNETCORE_ENVIRONMENT=Staging`, `ASPNETCORE_URLS`, `Email__PublicOrigin`, `TWOGETHER_DB_PATH`, `DataProtection__KeysPath` et, derrière un proxy, `ForwardedHeaders__KnownProxies__0`.

**Pourquoi une seule instance :** les défis WebAuthn/passkey sont conservés en mémoire. N’ajoute pas de seconde instance ou de répartition de charge avant d’avoir mis en place et testé un stockage partagé atomique pour ces défis.

**Validation :** domaine HTTPS accessible, origine publique identique à l’origine WebAuthn, `/api/health` répond `200`, chemins de données encore disponibles après redémarrage.

## 2. Configurer les courriels

- [ ] Choisir un fournisseur SMTP et autoriser le domaine expéditeur.
- [ ] Publier ses enregistrements DNS SPF, DKIM et DMARC.
- [ ] Réunir le nom d’hôte, le port, le mode TLS, l’identifiant, le mot de passe SMTP et l’adresse d’expédition.
- [ ] Tester la confirmation d’adresse et la récupération de compte avec des boîtes de test.

**Variables :** `Email__Delivery=Smtp`, `Email__FromAddress`, `Email__Host`, `Email__Port` (`587` avec STARTTLS ou `465` avec TLS implicite), `Email__ImplicitTls`, `Email__Username` et `Email__Password`. Le mot de passe va dans le coffre de secrets.

## 3. Désigner les responsables

- [ ] Créer et confirmer un compte administrateur et un compte modérateur.
- [ ] Nommer les personnes qui surveilleront le service et traiteront les signalements; convenir d’un canal d’incident et d’une cadence de revue.

**Variables :** `Administration__AdministratorEmails__0` et `Moderation__ModeratorEmails__0`; ajouter les entrées `__1`, `__2`, etc. si nécessaire. Les adresses doivent correspondre à des comptes confirmés. Les rôles sont distincts.

## 4. Fixer et vérifier les sauvegardes

- [ ] Décider qui sauvegarde la base et les clés Data Protection, à quelle fréquence et pendant combien de temps.
- [ ] Choisir les objectifs de reprise et les permissions d’accès aux sauvegardes.
- [ ] Chiffrer les sauvegardes et tester une restauration dans un environnement isolé.

**Chemins à protéger :** `TWOGETHER_DB_PATH` et l’ensemble du répertoire `DataProtection__KeysPath`. Une restauration n’est validée qu’après contrôle de `/api/health`, de l’authentification, des profils, des photos et des messages.

## 5. Valider la durée de conservation des messages

- [ ] Vérifier que la politique de suppression correspond aux informations de confidentialité destinées aux membres et à la politique de sauvegarde.
- [ ] Après déploiement, ouvrir Administration → Conservation et choisir une durée adaptée entre 1 et 150 jours.

Le réglage initial est **30 jours (1 mois conventionnel)**; un mois vaut 30 jours dans ce curseur. Les messages expirés ne sont plus affichés puis sont supprimés de la base au plus tard au prochain nettoyage (15 minutes). Une réduction du délai masque immédiatement les messages concernés; une suppression effectuée n’est pas récupérable en rallongeant ensuite le délai. Les copies dans les sauvegardes ne sont pas effacées par le curseur et suivent leur propre durée de conservation. Les photos et les signalements ne sont pas concernés.

## 6. Préparer Stripe en mode test

- [ ] Créer dans Stripe le produit éditorial et un prix récurrent mensuel en EUR.
- [ ] Activer le portail client et configurer les informations fiscales nécessaires.
- [ ] Créer l’endpoint HTTPS `https://<domaine>/api/billing/webhook` avec les événements :
  - `checkout.session.completed`
  - `checkout.session.expired`
  - `customer.subscription.created`
  - `customer.subscription.updated`
  - `customer.subscription.deleted`
- [ ] Tester Checkout, le retour avant/après webhook, les webhooks répétés, l’expiration, le renouvellement, l’annulation et la suppression de compte.

**Variables Staging, dans le coffre :** `Stripe__SecretKey` avec une clé `sk_test_…`, `Stripe__PremiumPriceId` avec l’identifiant `price_…`, `Stripe__WebhookSecret` avec `whsec_…`. Les trois doivent correspondre au même environnement. Les clés de test ne doivent jamais être réutilisées en Production.

Les nouvelles ventes Premium sont fermées par défaut. Ne les ouvre pas tant que les tests, les informations tarifaires et les vérifications fiscales/juridiques ne sont pas terminés.

## 7. Obtenir les validations légales et vie privée

- [ ] Réunir l’identité et les coordonnées de l’exploitant, le pays d’exploitation et la liste des sous-traitants (hébergement, courriel, cartographie, Stripe).
- [ ] Définir les finalités, bases légales, durées de conservation, destinataires et procédures d’exercice des droits.
- [ ] Faire examiner les données de localisation et les informations d’affinité/orientation par un professionnel compétent.
- [ ] Faire vérifier les conditions d’utilisation et, avant des ventes, le prix TTC, le renouvellement, la résiliation, la rétractation et les remboursements.

Ne publie pas de texte juridique générique sans l’adapter et le faire vérifier pour l’activité et le pays concernés.

## 8. Organiser la bêta gratuite

- [ ] Choisir une zone géographique et recruter des participants informés qu’ils utilisent une bêta.
- [ ] Confirmer qu’un opérateur et un modérateur pourront traiter les incidents et signalements au rythme prévu.
- [ ] Annoncer le canal de support et recueillir les retours par un questionnaire facultatif non relié au compte.
- [ ] Fixer avant l’ouverture les objectifs, seuils go/no-go et conditions de pause.

Pendant ce pilote, garder Premium fermé et la publicité désactivée. La publicité n’est pas intégrée à l’application : il faudra choisir un fournisseur, les emplacements, les limites de fréquence et les règles de consentement, puis faire valider leur conformité avant toute intégration.

## 9. Décider de l’ouverture publique et des ventes réelles

- [ ] Après le pilote, décider si le service est prêt à être élargi et si Premium doit être ouvert.
- [ ] Refaire les tests de santé, de sauvegarde/restauration, de courriel et de parcours utilisateur sur l’hébergement retenu.
- [ ] Seulement après les validations fiscales et juridiques, configurer en Production les clés `sk_live_…`, le prix et le webhook live dans le coffre de l’hébergeur.
- [ ] Ouvrir les ventes depuis le compte administrateur uniquement après vérification d’une offre Stripe valide.

La Production exige aussi au moins un administrateur et un modérateur configurés, un SMTP fonctionnel, un domaine HTTPS et des clés Data Protection persistantes. L’ouverture des ventes Premium ne change pas l’accès aux fonctions essentielles gratuites.

## 10. Mettre en service Jenkins

- [ ] Installer ou choisir un contrôleur Jenkins maintenu, le protéger par HTTPS et limiter l’accès à ses comptes d’administration.
- [ ] Fournir un agent Linux étiqueté `dotnet-10-linux-playwright`, avec Git, le SDK .NET 10.x, PowerShell 7 (`pwsh`), les dépendances système de Chromium et l’accès réseau au dépôt et à NuGet.
- [ ] Créer une pipeline Multibranch pour le dépôt; enregistrer dans Jenkins les identifiants de lecture si le dépôt est privé.
- [ ] Configurer la découverte des branches et demandes de fusion, puis le webhook du fournisseur Git pour déclencher les builds.
- [ ] Pousser une branche de test et vérifier qu’un build échoue en cas de test rouge, que le parcours navigateur Chromium s’exécute, qu’un build vert archive l’artefact de validation et qu’aucun déploiement n’est déclenché.

Le dépôt fournit déjà le `Jenkinsfile`; les étapes ci-dessus nécessitent l’accès à l’infrastructure Jenkins et à la configuration du fournisseur Git. La pipeline lance la suite de non-régression et produit un artefact, mais ne remplace pas la revue de code, les essais manuels ou l’approbation du déploiement.
