# 2gether

Application de rencontres de quartier réalisée avec Blazor WebAssembly et ASP.NET Core Identity. L’application regroupe l’inscription, la découverte de profils, la carte, les photos, les messages, la récupération du compte par e-mail et la connexion par passkey.

## Documentation

- [Manuel d’utilisation](MANUEL-UTILISATEUR.md)
- [Manuel d’installation](MANUEL-INSTALLATION.md)
- [Notes de sécurité et contrôles avant déploiement](Server/SECURITY.md)

## Démarrage rapide (Windows PowerShell)

Installer le SDK .NET 9, puis depuis la racine du dépôt :

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:Email__Delivery = "PickupDirectory"
$env:Email__PublicOrigin = "http://localhost:5067"
$env:Email__FromAddress = "verification@2gether.invalid"
dotnet run --project .\Server\TwoGether.Server.csproj --no-launch-profile --urls http://localhost:5067
```

Ouvrir ensuite <http://localhost:5067>. En développement, les courriels sont écrits dans `Server/App_Data/Mail` et ne sont pas envoyés sur Internet. Pour l’installation complète, les tests et la configuration SMTP, consulter le [manuel d’installation](MANUEL-INSTALLATION.md).

## Organisation du dépôt

| Emplacement | Rôle |
| --- | --- |
| `Pages/` | Parcours Blazor : inscription et application, confirmation d’e-mail, récupération du mot de passe et gestion des passkeys |
| `Server/Program.cs` | Configuration du serveur, d’Identity, de SQLite, du courrier, de WebAuthn et des limites de débit |
| `Server/AuthenticationApi.cs` | Routes Identity et vérification serveur des cérémonies WebAuthn |
| `Server/TwoGetherApi.cs` | API des profils, photos, localisations et messages |
| `Server/BackendModels.cs` | Modèles Identity, profils et passkeys |
| `Server/VerificationEmailSender.cs` | Envoi SMTP ou écriture locale des courriels |
| `wwwroot/js/passkeys.js` | Adaptation WebAuthn du navigateur ; aucune donnée biométrique n’est lue par l’application |
| `Server/Tests/` | Tests d’intégration des limites de sécurité, de la récupération et des passkeys |

## Limite de déploiement à connaître

Les défis WebAuthn à usage unique sont stockés en mémoire et expirent après cinq minutes. Cette version est destinée à une instance serveur. Pour plusieurs instances, remplacer ce stockage par un stockage distribué atomique partagé avant d’activer la répartition de charge.
