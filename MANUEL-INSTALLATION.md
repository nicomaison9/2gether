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
| `Stripe__SecretKey` | Clé API secrète Stripe `sk_live_…`, à enregistrer uniquement dans le coffre de secrets |
| `Stripe__PremiumPriceId` | Identifiant `price_…` d’un prix actif, récurrent chaque mois et libellé en EUR |
| `Stripe__WebhookSecret` | Secret de signature `whsec_…` de l’endpoint webhook correspondant à cette application |

Le nom de domaine WebAuthn (`RPID`) et les origines autorisées proviennent de `Email__PublicOrigin`. Configurer cette valeur sur la même origine que celle affichée au navigateur ; ne pas accepter une origine issue d’un en-tête fourni librement par le client. Avec un proxy inverse, n’exposer que le proxy en HTTPS et limiter/valider les en-têtes transférés conformément à la configuration de l’hébergeur.

Les adresses transmises (`X-Forwarded-For` et `X-Forwarded-Proto`) ne sont prises en compte que si elles proviennent d’une adresse configurée dans `ForwardedHeaders__KnownProxies__*`. Un proxy non déclaré n’est pas approuvé et ses en-têtes ne modifient ni l’adresse client ni le schéma détectés.

### Configuration Stripe Premium

La facturation est obligatoire en Production : le serveur refuse de démarrer si les trois variables Stripe ci-dessus ne sont pas renseignées ensemble. En Production, seule une clé `sk_live_…` est acceptée ; en développement, utiliser exclusivement la clé `sk_test_…`, un prix et un webhook de test. Sans ces trois variables en développement, les paiements restent désactivés.

Dans le tableau de bord Stripe :

1. Créer le produit éditorial Premium et son prix récurrent mensuel en EUR, puis renseigner son identifiant `price_…`. L’application affiche le montant Stripe configuré et active la collecte de taxe automatique au Checkout ; configurer les informations fiscales, l’immatriculation et le comportement fiscal du prix dans Stripe avant toute vente.
2. Activer et configurer le portail client Stripe pour permettre aux abonnés de consulter et résilier leur abonnement.
3. Créer un endpoint webhook HTTPS à `https://<domaine-public>/api/billing/webhook` et sélectionner les événements `checkout.session.completed`, `checkout.session.expired`, `customer.subscription.created`, `customer.subscription.updated` et `customer.subscription.deleted`. Copier son secret de signature `whsec_…` dans `Stripe__WebhookSecret`.
4. En développement, utiliser l’URL locale publique de Stripe CLI comme endpoint de transfert et configurer le secret `whsec_…` fourni par cette session. Ce secret change avec la session CLI.

Ne jamais réutiliser en Production un prix, une clé ou un secret de webhook de test, ni placer ces valeurs dans le dépôt ou les journaux. Avant ouverture au public, réaliser un parcours de souscription, renouvellement, résiliation et suppression de compte dans le mode test, puis vérifier l’arrivée des webhooks et la configuration fiscale du compte Stripe.

Toutes les mutations de l’API utilisent un jeton antiforgery transmis dans l’en-tête `X-CSRF-TOKEN`. Le client Blazor récupère ce jeton avant chaque requête qui modifie des données. Ne pas désactiver ce contrôle sur les nouveaux endpoints.

L’accès à l’espace de modération (`/moderation`) est réservé aux comptes confirmés dont l’adresse correspond à une valeur `Moderation__ModeratorEmails__*`. Configurer ces adresses dans l’environnement protégé de l’hébergeur; ne jamais exposer cette liste dans le dépôt ou la laisser contrôlable par les utilisateurs. Le serveur refuse de démarrer en Production si aucune adresse de modérateur n’est configurée. Définir également une procédure de traitement et de conservation des signalements.

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
- Prévoir surveillance, quotas disque, rotation/sauvegarde de la base et procédure de restauration.

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
