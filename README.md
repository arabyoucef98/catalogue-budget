# Caisse réseau

Client Windows de caisse et serveur API partagé avec catalogue PostgreSQL. Le serveur valide les prix et le stock, puis enregistre le ticket, ses lignes, les mouvements de stock et l’audit dans une transaction. Le classeur Excel d’origine reste en lecture seule : seul le fichier XLSM volontairement choisi par l’administrateur est importé, après prévisualisation.

## Premier démarrage (Windows)

Installez Docker Desktop en mode conteneurs Linux sur le poste serveur. Le .NET 8 SDK est nécessaire uniquement pour reconstruire et publier le client WPF. Dans un terminal PowerShell à la racine du projet :

```powershell
Copy-Item .env.example .env
notepad .env
```

Remplacez les trois valeurs secrètes de `.env` par des secrets aléatoires privés, définissez `ADMIN_PASSWORD` sur le mot de passe initial de l’administrateur, puis démarrez l’application :

```powershell
docker compose up --build -d
docker compose ps
Invoke-RestMethod http://localhost:5080/health
```

`docker compose up` attend que PostgreSQL passe son healthcheck avant de démarrer l’API. La base et l’API communiquent sur le réseau privé Compose (`Host=db`). PostgreSQL n’est publié que sur la boucle locale du serveur; par défaut, l’API l’est également sur le port `5080`. Pour une caisse sur un autre poste du réseau local, définissez `API_BIND_ADDRESS=0.0.0.0` dans `.env`, autorisez le port dans le pare-feu Windows et remplacez dans `src/Caisse.Client/appsettings.json` `localhost` par le nom ou l’adresse LAN du serveur. Si un autre port est choisi avec `API_PORT` dans `.env`, adaptez aussi cette URL.

Le compte `admin` est créé à partir de `ADMIN_PASSWORD` au premier démarrage de l’API et ne remplace jamais un compte existant. Après connexion en tant qu’administrateur, l’onglet **Import catalogue** du client permet de sélectionner le XLSM, de corriger ou refuser les fichiers invalides, de consulter l’aperçu avant d’appliquer et de confirmer explicitement le remplacement des quantités de stock. Pour des achats et ventes réels, mettez le serveur, les postes, le pare-feu et le réseau sur un réseau privé maîtrisé; l’API doit être publiée derrière HTTPS avant toute utilisation au-delà d’un réseau local de confiance. Ne partagez pas le fichier `.env`.

## Client WPF et packaging Windows

Construisez l’application cliente sur Windows :

```powershell
dotnet build src/Caisse.Client/Caisse.Client.csproj -c Release
dotnet publish src/Caisse.Client/Caisse.Client.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish\windows-x64
```

Distribuez le dossier `publish\windows-x64` (notamment `appsettings.json`), pas seulement l’EXE. Le client est configuré dans `src/Caisse.Client/appsettings.json`; l’URL de l’API doit être remplacée avant l’installation sur les postes clients.

## Import du classeur XLSM

Connectez-vous avec un compte administrateur, puis utilisez l’API `POST /api/admin/products/import/preview` et `POST /api/admin/products/import/apply` en envoyant le fichier dans le champ multipart `file`; `apply` exige également le hash SHA-256 `previewHash` renvoyé par la prévisualisation. Exemple en PowerShell après connexion à l’API et stockage du jeton dans `$token` :

```powershell
$headers = @{ Authorization = "Bearer $token" }
$preview = Invoke-RestMethod http://localhost:5080/api/admin/products/import/preview -Method Post -Headers $headers -Form @{ file = Get-Item '.\gestionnaire de caisse youcef.xlsm' }
$preview
Invoke-RestMethod http://localhost:5080/api/admin/products/import/apply -Method Post -Headers $headers -Form @{ file = Get-Item '.\gestionnaire de caisse youcef.xlsm'; previewHash = $preview.sha256 }
```

L’aperçu est sans écriture. Seuls les fichiers `.xlsm` de 20 Mo maximum avec les colonnes `Code article`, `Désignation`, `Catégorie`, `Prix achat`, `Prix vente TTC` et `Stock actuel` dans l’onglet `Articles` sont acceptés. Les macros ne sont jamais exécutées. L’application refuse les lignes invalides et codes répétés. L’application de l’aperçu n’est acceptée que pour le fichier dont le hash correspond exactement à celui prévisualisé. Les insertions et mises à jour, l’audit et les ajustements de stock sont atomiques. Les articles absents du classeur ne sont pas supprimés; les quantités importées remplacent le stock existant et leur différence est inscrite dans les mouvements.

## Contrôles

```powershell
dotnet restore Caisse.sln
dotnet test Caisse.sln -c Release
dotnet build Caisse.sln -c Release
```

Les commandes de vente et le catalogue exigent un bearer token obtenu avec `POST /api/auth/login`; les imports et l’audit sont réservés aux administrateurs. Toute tentative de vente avec une même clé d’idempotence et une requête identique renvoie le ticket existant; réutiliser cette clé pour une requête différente est refusé.
