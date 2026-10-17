# Manuel d’installation et d’exploitation — 2gether

## 1. Contenu et architecture

2gether est une application web composée de deux projets :

- `PocketTasks.csproj` est le client Blazor WebAssembly ciblant `net10.0`. Il contient les pages, l’interface, les styles et le code navigateur.
- `Server/TwoGether.Server.csproj` est l’API ASP.NET Core ciblant `net10.0`. Elle gère les comptes Identity, SQLite, les profils, les photos, les messages, le courrier et la vérification des passkeys.

Les échanges navigateur/serveur se font sur la même origine et utilisent des cookies d’authentification HttpOnly. L’API sert également les fichiers du client.

| Fichier/dossier | Fonction |
| --- | --- |
| `Pages/Home.razor` | Inscription, connexion, profils et fonctions principales |
| `Pages/ConfirmEmail.razor` | Confirmation d’adresse |
| `Pages/PasswordRecovery.razor` | Demande de code et changement du mot de passe |
| `Pages/Passkeys.razor` | Enregistrement, consultation et suppression des passkeys |
| `Server/Program.cs` | Démarrage, Identity, persistance, origine de confiance WebAuthn, limitations et fichiers statiques |
| `Server/Migrations/` | Historique versionné du schéma SQLite |
| `Server/DatabaseMigrationRunner.cs` | Détection prudente des bases héritées et application des migrations |
| `Server/AuthenticationApi.cs` | Routes Identity, récupération et vérification cryptographique WebAuthn |
| `Server/TwoGetherApi.cs` | API des fonctionnalités sociales |
| `Server/BackendModels.cs` | Modèles de données, y compris les clés publiques des passkeys |
| `Server/VerificationEmailSender.cs` | Envoi SMTP en production ou écriture `.eml` en développement |
| `wwwroot/js/passkeys.js` | Adaptation des options WebAuthn aux API standard du navigateur |
| `Server/Tests/` | Tests d’intégration de sécurité |

## 2. Prérequis

- Windows, Linux ou macOS.
- SDK .NET 10.x et un runtime ASP.NET Core correspondant. Vérifier avec `dotnet --info`.
- Accès au dépôt Git et à NuGet pendant la restauration initiale.
- Un navigateur récent pour Blazor ; un navigateur et un appareil compatibles WebAuthn pour utiliser les passkeys.
- Pour une installation publique : nom de domaine, HTTPS, serveur SMTP autorisé à envoyer depuis l’adresse configurée et stockage persistant.

Le projet cible .NET 10 LTS. Maintenir le SDK, le runtime et les packages Microsoft sur une version de maintenance prise en charge; contrôler régulièrement la politique officielle de support .NET.

## 3. Préparer le dépôt

Depuis le répertoire racine :

```powershell
dotnet tool restore
dotnet restore .\Server\TwoGether.Server.csproj
dotnet build .\Server\TwoGether.Server.csproj -c Release
```

La référence au projet client construit aussi le WebAssembly et ses ressources statiques. L’API doit être démarrée depuis son projet (ou depuis le répertoire publié), afin que son répertoire de contenu et ses ressources soient correctement déterminés.

Les migrations EF Core sont versionnées dans `Server/Migrations`. Pour ajouter une migration après un changement de modèle, depuis la racine :

```powershell
dotnet tool run dotnet-ef migrations add AjouterUnChamp --project .\Server\TwoGether.Server.csproj --output-dir Migrations
```

Au démarrage, l’application applique automatiquement les migrations en attente. N’exécuter qu’une instance pendant une migration SQLite et sauvegarder la base avant chaque déploiement.

## 4. Lancer en développement

Depuis la racine du dépôt, dans PowerShell :

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:Email__Delivery = "PickupDirectory"
$env:Email__PublicOrigin = "http://localhost:5067"
$env:Email__FromAddress = "verification@2gether.invalid"
$env:Email__PickupDirectory = "App_Data/Mail"
$env:TWOGETHER_DB_PATH = Join-Path $env:TEMP "2gether-dev.db"
dotnet run --project .\Server\TwoGether.Server.csproj --no-launch-profile --urls http://localhost:5067
```

Ouvrir <http://localhost:5067> dans le navigateur. Garder cette fenêtre PowerShell ouverte pendant l’utilisation.

Les variables définies dans cette fenêtre ne concernent que ce processus. Si le port change, mettre à jour `Email__PublicOrigin` avec l’origine exacte (schéma, hôte et port) utilisée dans le navigateur. WebAuthn compare l’origine et le nom de domaine configurés avec ceux de la cérémonie ; une valeur incorrecte empêche l’enregistrement et la connexion par passkey.

En configuration locale :

- Les courriels sont écrits dans `Server/App_Data/Mail` quand `Email__PickupDirectory` est relatif. Ils ne sont pas envoyés sur Internet. Ouvrir un fichier `.eml` avec un lecteur de courrier pour suivre un lien de confirmation ou copier un code de récupération.
- La base est créée à l’emplacement `TWOGETHER_DB_PATH`. Sans cette variable, l’application utilise `Server/App_Data/2gether.db`.
- `Server/App_Data/` est exclu du dépôt et des fichiers statiques. Protéger quand même ses permissions : les `.eml` contiennent des jetons utilisables et la base contient des données personnelles.
- `http://localhost` est accepté pour le développement. Les passkeys ne fonctionnent pas sur une origine HTTP publique.

Arrêter le serveur avec `Ctrl+C`.

## 5. Vérifier l’installation

Dans une autre fenêtre PowerShell, depuis la racine :

```powershell
curl.exe --fail --show-error http://localhost:5067/api/health
dotnet test .\Server\Tests\TwoGether.SecurityTests.csproj -c Release
```

Les tests créent leur propre base et leur propre dossier de courriels temporaires, démarrent un serveur local et nettoient ces données. Ils couvrent notamment l’absence de divulgation d’un compte lors d’une demande de récupération, le changement effectif du mot de passe, les codes à usage unique, l’exigence de vérification utilisateur des passkeys et l’usage unique des défis.

Les tests ne remplacent pas une vérification sur appareil réel. Avant ouverture aux utilisateurs, tester la création, la connexion et la suppression d’une passkey avec les navigateurs et systèmes d’exploitation ciblés.

## 6. Publier l’application

Construire une publication :

```powershell
dotnet publish .\Server\TwoGether.Server.csproj -c Release -o .\publish
```

Le projet copie le client compilé dans `publish/wwwroot`. Déployer ensemble le contenu de ce répertoire, le serveur, sa configuration, le stockage persistant configuré et les clés Data Protection. Pour lancer manuellement la publication sous PowerShell :

```powershell
Push-Location .\publish
dotnet .\TwoGether.Server.dll
Pop-Location
```

Configurer les variables d’environnement sur la plateforme d’hébergement, pas dans le dépôt. En production, le serveur refuse une origine HTTP non locale ou une configuration SMTP incomplète.

### Variables de configuration de production

| Variable | Configuration attendue |
| --- | --- |
| `ASPNETCORE_ENVIRONMENT` | `Production` |
| `ASPNETCORE_URLS` | Adresse d’écoute interne du serveur, par exemple `http://0.0.0.0:8080` derrière un proxy TLS |
| `Email__Delivery` | `Smtp` ; le dépôt de fichiers de courriels est réservé au développement |
| `Email__PublicOrigin` | Origine publique exacte en HTTPS, par exemple `https://rencontres.example.fr`, sans chemin, requête ni fragment |
| `Email__FromAddress` | Adresse d’expédition validée par le fournisseur |
| `Email__Host` | Nom d’hôte SMTP |
| `Email__Port` | `587` pour STARTTLS ou `465` pour TLS implicite |
| `Email__ImplicitTls` | `false` sur le port 587 ; `true` sur le port 465 |
| `Email__Username` | Identifiant SMTP si requis |
| `Email__Password` | Secret SMTP, à stocker dans le coffre de secrets de l’hébergeur |
| `TWOGETHER_DB_PATH` | Chemin absolu vers un fichier SQLite persistant et sauvegardé |
| `DataProtection__KeysPath` | Chemin absolu vers un répertoire persistant et protégé; obligatoire en Production |
| `ForwardedHeaders__KnownProxies__0` | Adresse IP du proxy inverse de confiance, si l’application est derrière un proxy; ajouter un index par proxy connu |
| `Moderation__ModeratorEmails__0` | Adresse exacte d’un compte modérateur confirmé; ajouter `__1`, `__2`, etc. pour d’autres modérateurs |
| `Administration__AdministratorEmails__0` | Adresse exacte d’un compte administrateur confirmé; ajouter `__1`, `__2`, etc. pour d’autres administrateurs |
| `Stripe__SecretKey` | Clé API secrète Stripe `sk_live_…`, à enregistrer uniquement dans le coffre de secrets |
| `Stripe__PremiumPriceId` | Identifiant `price_…` d’un prix actif, récurrent chaque mois et libellé en EUR |
| `Stripe__WebhookSecret` | Secret de signature `whsec_…` de l’endpoint webhook correspondant à cette application |

Le nom de domaine WebAuthn (`RPID`) et les origines autorisées proviennent de `Email__PublicOrigin`. Configurer cette valeur sur la même origine que celle affichée au navigateur ; ne pas accepter une origine issue d’un en-tête fourni librement par le client. Avec un proxy inverse, n’exposer que le proxy en HTTPS et limiter/valider les en-têtes transférés conformément à la configuration de l’hébergeur.

Les adresses transmises (`X-Forwarded-For` et `X-Forwarded-Proto`) ne sont prises en compte que si elles proviennent d’une adresse configurée dans `ForwardedHeaders__KnownProxies__*`. Un proxy non déclaré n’est pas approuvé et ses en-têtes ne modifient ni l’adresse client ni le schéma détectés.

### Configuration Stripe Premium

La facturation est obligatoire en Production : le serveur refuse de démarrer si les trois variables Stripe ci-dessus ne sont pas renseignées ensemble. En Production, seule une clé `sk_live_…` est acceptée ; en développement, utiliser exclusivement la clé `sk_test_…`, un prix et un webhook de test. Sans ces trois variables en développement, les paiements restent désactivés.

Pour une préproduction accessible sur Internet, utiliser `ASPNETCORE_ENVIRONMENT=Staging`, une origine HTTPS, SMTP réel et des identifiants Stripe de test. Les clés `sk_test_…` sont refusées en `Production`; ne pas contourner ce garde-fou pour effectuer un essai. Définir aussi explicitement les chemins persistants de base et de clés Data Protection en Staging.

Dans le tableau de bord Stripe :

1. Créer le produit éditorial Premium et son prix récurrent mensuel en EUR, puis renseigner son identifiant `price_…`. L’application affiche le montant Stripe configuré et active la collecte de taxe automatique au Checkout ; configurer les informations fiscales, l’immatriculation et le comportement fiscal du prix dans Stripe avant toute vente.
2. Activer et configurer le portail client Stripe pour permettre aux abonnés de consulter et résilier leur abonnement.
3. Créer un endpoint webhook HTTPS à `https://<domaine-public>/api/billing/webhook` et sélectionner les événements `checkout.session.completed`, `checkout.session.expired`, `customer.subscription.created`, `customer.subscription.updated` et `customer.subscription.deleted`. Copier son secret de signature `whsec_…` dans `Stripe__WebhookSecret`.
4. En développement, utiliser l’URL locale publique de Stripe CLI comme endpoint de transfert et configurer le secret `whsec_…` fourni par cette session. Ce secret change avec la session CLI.

Ne jamais réutiliser en Production un prix, une clé ou un secret de webhook de test, ni placer ces valeurs dans le dépôt ou les journaux. Avant ouverture au public, réaliser un parcours de souscription, renouvellement, résiliation et suppression de compte dans le mode test, puis vérifier l’arrivée des webhooks et la configuration fiscale du compte Stripe.

Toutes les mutations de l’API utilisent un jeton antiforgery transmis dans l’en-tête `X-CSRF-TOKEN`. Le client Blazor récupère ce jeton avant chaque requête qui modifie des données. Ne pas désactiver ce contrôle sur les nouveaux endpoints.

L’accès à l’espace de modération (`/moderation`) est réservé aux comptes confirmés dont l’adresse correspond à une valeur `Moderation__ModeratorEmails__*`. L’administration (`/administration`) utilise une liste distincte `Administration__AdministratorEmails__*`; un modérateur n’obtient pas automatiquement des droits d’administration. Configurer les deux listes dans l’environnement protégé de l’hébergeur; ne jamais les exposer dans le dépôt ni les rendre contrôlables par les utilisateurs. Le serveur refuse de démarrer en Production sans au moins une adresse pour chaque liste.

Le centre d’administration regroupe le tableau de bord, les liens vers la modération, les réglages Premium et le journal des changements. L’ouverture de nouvelles souscriptions est fermée par défaut; l’administrateur ne peut l’activer qu’après vérification d’une offre Stripe valide. La fermeture des nouvelles souscriptions ne coupe pas l’accès des abonnements déjà actifs et ne bloque pas leur résiliation. Les fonctions essentielles de rencontre restent gratuites. Aucun secret Stripe ou SMTP n’est éditable dans l’interface : ils restent dans le coffre de configuration. Les publicités ne sont pas encore intégrées et ne peuvent pas être activées dans cette version.

Les changements administratifs de monétisation sont enregistrés dans un journal protégé et consultable par les administrateurs. Ce journal conserve l’identifiant du compte administrateur et le détail du changement; définir sa durée de conservation et les personnes qui peuvent le consulter. Définir également une procédure de traitement et de conservation des signalements et une revue périodique des listes d’accès administrateur/modérateur.

### Persistance et mises à jour

- Le serveur applique les migrations EF Core au démarrage. Une nouvelle base est créée par la migration initiale; les migrations suivantes ne doivent pas supprimer de données sans procédure explicite.
- En Production, `DataProtection__KeysPath` est obligatoire. Le répertoire doit être durable entre redémarrages, accessible en écriture uniquement au compte de service et protégé par le chiffrement du stockage de l’hébergeur. Sans ce répertoire, le serveur refuse de démarrer en Production.
- Les bases créées par une version antérieure à EF Core n’ont pas d’historique de migration. Arrêter le serveur et faire une copie de sauvegarde avant toute opération. Après vérification que le schéma existant correspond bien à `InitialSchema` (y compris `PasskeyCredentials`), exécuter depuis la racine :

  ```powershell
  if ([string]::IsNullOrWhiteSpace($env:TWOGETHER_DB_PATH)) { throw "Définis TWOGETHER_DB_PATH vers la base à reprendre." }
  if (-not (Test-Path -LiteralPath $env:TWOGETHER_DB_PATH)) { throw "La base à reprendre est introuvable." }
  Get-Content .\Server\Scripts\BaselineLegacySqlite.sql | sqlite3.exe "$env:TWOGETHER_DB_PATH"
  if ($LASTEXITCODE -ne 0) { throw "La reprise de la base a échoué; ne redémarre pas le serveur." }
  ```

  Le script ne crée l’entrée de référence que si un compte existe déjà et qu’aucune migration n’est enregistrée. Redémarrer ensuite le serveur : il vérifie la présence de cette migration avant de continuer. Si le schéma diffère, ne pas marquer la migration comme appliquée; préparer une migration de reprise spécifique à cette base.
- Stocker durablement et protéger la base, les sauvegardes et les clés ASP.NET Core Data Protection. Sans clés Data Protection persistantes, les cookies et jetons peuvent devenir invalides après redémarrage ou changement d’instance.
- Limiter strictement les permissions de lecture/écriture de la base, des clés et des courriels de test. Les clés publiques WebAuthn ne sont pas des clés secrètes, mais restent des données de compte à protéger.
- Configurer SPF, DKIM et DMARC pour le domaine d’expédition. Tester la délivrabilité, les liens HTTPS et le fonctionnement de la récupération avant la mise en service.
- Prévoir surveillance, quotas disque, rotation/sauvegarde de la base et procédure de restauration. Le contrôle `/api/health` vérifie maintenant la connectivité SQLite et répond `503` si la base est inaccessible.

### Réaliser et vérifier une sauvegarde

L’application conserve les profils, messages, abonnements et photos dans SQLite. Les clés Data Protection se trouvent dans un répertoire distinct; sauvegarder les deux ensembles, chiffrer la sauvegarde et limiter son accès aux opérateurs habilités.

Pour une sauvegarde simple et cohérente sur le déploiement mono-instance actuel :

1. Arrêter proprement le serveur (ou le service hébergeant l’application) et attendre la fin du processus afin que SQLite ferme sa base et termine ses écritures WAL.
2. Copier le fichier pointé par `TWOGETHER_DB_PATH` ainsi que tout le répertoire `DataProtection__KeysPath` vers le stockage de sauvegarde chiffré. Inclure le fichier de base complet, qui contient aussi les photos.
3. Vérifier que les deux copies existent, noter la date et protéger les permissions. Ne jamais copier une sauvegarde ou une base de production dans le dépôt Git.
4. Redémarrer le serveur et vérifier `/api/health`, la connexion, les profils et les photos.

Pour tester une restauration, utiliser un environnement isolé : arrêter son serveur, conserver une copie de la base de test à remplacer, restaurer ensemble une sauvegarde de base et son jeu de clés associé dans les chemins configurés, redémarrer, puis vérifier `/api/health`, l’authentification, les photos et l’accès aux messages. Ne pas expérimenter la restauration sur la seule copie de production. Si le fournisseur impose une sauvegarde à chaud, utiliser un mécanisme SQLite cohérent avec WAL plutôt qu’une simple copie du fichier pendant que le serveur écrit.

### Porte de préproduction et bêta limitée

Ne pas ouvrir le service au public tant que les points bloquants suivants ne sont pas validés et consignés par l’exploitant :

- **Stripe en mode test :** configurer un prix mensuel EUR, le portail client, la taxe automatique et les cinq événements webhook indiqués plus haut. Tester souscription, arrivée du webhook avant/après le retour navigateur, notification dupliquée, expiration du Checkout, renouvellement, résiliation et suppression de compte. Vérifier que l’accès Premium ne s’ouvre qu’après un webhook valide et qu’une annulation ne prolonge pas indûment l’accès. Ne passer en mode live qu’après validation fiscale et légale par l’exploitant.
- **Parcours utilisateurs :** faire tester par un petit groupe contrôlé la création et confirmation de compte, récupération du mot de passe, passkeys sur de vrais appareils, modification du profil, géolocalisation facultative, photos publiques/privées, demandes d’accès, blocage, signalement et modération. Vérifier qu’un refus d’accès à la localisation ou à la biométrie laisse une autre voie d’usage.
- **Exploitation :** exécuter la suite Release, vérifier le contrôle de santé, tester l’envoi SMTP réel, confirmer la persistance des clés et répéter une restauration sur un environnement isolé. Définir un opérateur joignable, un canal de remontée des incidents et une procédure de suspension/réactivation des comptes.
- **Informations et conformité :** compléter avant inscription publique l’identité et les coordonnées de l’exploitant, l’hébergement, les finalités et bases légales, les durées de conservation, les destinataires/sous-traitants (dont courrier, cartographie et Stripe), l’exercice des droits, la procédure de signalement et les conditions Premium (prix TTC applicable, renouvellement, résiliation, rétractation/remboursement). Faire examiner spécifiquement les données de localisation et les champs liés aux affinités/orientations par un professionnel compétent; ne pas publier de texte juridique non vérifié.
- **Limite d’architecture :** garder une seule instance et une base locale SQLite tant que les défis WebAuthn sont stockés en mémoire. Avant de multiplier les instances, introduire et tester un stockage partagé atomique pour les défis, une persistance adaptée et des limites de débit distribuées.

Après cette vérification, ouvrir la bêta à un groupe restreint, traiter les retours et incidents avant d’élargir l’accès. Aucune clé Stripe réelle, valeur légale, sauvegarde de production ou configuration d’hébergeur n’est incluse dans le dépôt; ces étapes requièrent les comptes et choix de l’exploitant.

## 7. Passkeys, modération et limites

- L’appareil vérifie localement l’empreinte, le visage ou le code ; le serveur reçoit une preuve WebAuthn et stocke la clé publique, jamais les données biométriques.
- L’API demande un authentificateur résident et `userVerification=required`. Le code de l’appareil peut donc être utilisé en remplacement d’un capteur biométrique.
- Les membres peuvent bloquer et signaler des profils. Un blocage masque les profils et conversations dans les deux sens et empêche les nouveaux messages et demandes d’accès aux photos. Les modérateurs configurés peuvent classer un signalement, suspendre le compte concerné ou le rétablir.
- Les options de défi expirent après cinq minutes et chaque défi est consommé une seule fois. Elles sont conservées en mémoire dans `PasskeyChallengeStore` : les cérémonies doivent rester sur la même instance serveur. Pour plusieurs instances ou une répartition de charge, remplacer ce composant par un stockage partagé et atomique (par exemple Redis ou SQLite avec consommation transactionnelle) ; l’affinité de session seule n’est pas une stratégie durable de haute disponibilité.
- La récupération par e-mail s’appuie sur les jetons Identity à durée de vie limitée. Le code de récupération est à usage unique et sa durée est d’une heure. La limitation par adresse IP ne suffit pas, à elle seule, contre une attaque distribuée.
- Les messages et signalements ne sont pas chiffrés de bout en bout. Vérifier les exigences réglementaires, définir une durée de conservation des données de modération et tester les procédures de sauvegarde/suppression avant l’ouverture publique.

## 8. Diagnostic

| Symptôme | Vérifications |
| --- | --- |
| Le serveur échoue avec une erreur de configuration d’e-mail | En développement, définir `ASPNETCORE_ENVIRONMENT=Development`, `Email__Delivery=PickupDirectory`, une origine loopback exacte et une adresse d’expédition valide. |
| Le lien de confirmation ou le code de récupération n’arrive pas | En local, chercher le `.eml` dans `Server/App_Data/Mail` ou le dossier configuré. En production, vérifier la configuration et les journaux du fournisseur SMTP. |
| Une passkey est indisponible | Vérifier HTTPS/localhost, la prise en charge WebAuthn du navigateur, la présence d’une passkey enregistrée pour le bon site et la concordance exacte de l’origine configurée. |
| Les profils et l’API ne se chargent pas | Vérifier `http://localhost:5067/api/health`, les journaux du serveur, les ressources WebAssembly publiées et la disponibilité du fichier SQLite. |
| Les connexions sont refusées après un déploiement | Vérifier la confirmation d’e-mail, l’état du compte et la persistance des clés Data Protection. |
