# API and database migrator of Nomori Marketplace. Build context: the root of this repository.
#   docker build -f deploy/api.Dockerfile --target api      -t nomori-api .
#   docker build -f deploy/api.Dockerfile --target migrator -t nomori-migrator .
# See docs/deployment-guide.md.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/Nomori.Marketplace.Api -c Release -o /out/api -p:UseAppHost=false \
 && dotnet publish src/Nomori.Marketplace.DbMigrator -c Release -o /out/migrator -p:UseAppHost=false

# One-shot job: creates the database when missing and applies the migrations. Safe to run again.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS migrator
WORKDIR /app
COPY --from=build /out/migrator .
ENTRYPOINT ["dotnet", "Nomori.Marketplace.DbMigrator.dll"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS api
WORKDIR /app
COPY --from=build /out/api .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Nomori.Marketplace.Api.dll"]
