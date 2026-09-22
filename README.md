# Caisse réseau

Socle de l'application de caisse multi-postes destinée à remplacer le classeur Excel/VBA.

## Architecture

- `src/Caisse.Server` : API ASP.NET Core 8, règles métier, authentification et transactions.
- `src/Caisse.Client` : client Windows WPF minimal consommant l'API locale.
- `database/init.sql` : schéma PostgreSQL initial.
- `docker-compose.yml` : PostgreSQL local de développement.

Le serveur est la source de vérité : les clients ne modifient jamais directement la base. Une validation de vente écrit le ticket, ses lignes, le paiement, le mouvement de stock et le stock dans une transaction unique.

## Démarrage du serveur

Prérequis : .NET 8 SDK et Docker Desktop.

```powershell
docker compose up -d db
dotnet restore
dotnet run --project src/Caisse.Server
```

Configuration par défaut : `http://localhost:5080`, base PostgreSQL `caisse`.

Le compte administrateur initial est créé uniquement si `Seed:AdminPassword` est fourni. Définir ce secret avant le premier démarrage :

```powershell
$env:Seed__AdminPassword = "ChangerCeMotDePasse"
dotnet run --project src/Caisse.Server
```

Ne jamais utiliser ce mécanisme de seed en production après l'installation initiale.
