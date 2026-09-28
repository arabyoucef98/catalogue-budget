FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /source
COPY global.json ./
COPY src/Caisse.Server/Caisse.Server.csproj src/Caisse.Server/
RUN dotnet restore src/Caisse.Server/Caisse.Server.csproj
COPY src/Caisse.Server/ src/Caisse.Server/
RUN dotnet publish src/Caisse.Server/Caisse.Server.csproj -c Release -o /publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /publish/ ./
USER $APP_UID
EXPOSE 5080
ENTRYPOINT ["dotnet", "Caisse.Server.dll"]
