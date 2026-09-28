# syntax=docker/dockerfile:1

# ---------- build ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/DocuMind.Core/DocuMind.Core.csproj src/DocuMind.Core/
COPY src/DocuMind.Api/DocuMind.Api.csproj src/DocuMind.Api/
RUN dotnet restore src/DocuMind.Api/DocuMind.Api.csproj

COPY src/ src/
RUN dotnet publish src/DocuMind.Api/DocuMind.Api.csproj -c Release --no-restore -o /app/publish /p:UseAppHost=false

# The chiseled image has no shell, so prepare the writable data folder here.
RUN mkdir -p /staging/data

# ---------- runtime ----------
# Chiseled: no shell or package manager, runs as the non-root "app" user.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS runtime
WORKDIR /app

ENV ASPNETCORE_HTTP_PORTS=8080 \
    ConnectionStrings__Documents="Data Source=/home/app/data/documind.db"

# Supply ANTHROPIC_API_KEY at runtime; secrets are never baked into the image.

COPY --from=build /app/publish .
# Owned by the non-root app user; a named volume mounted here inherits this ownership.
COPY --from=build --chown=$APP_UID:$APP_UID /staging/data /home/app/data

# Mount a volume here to keep the document index across restarts.
VOLUME ["/home/app/data"]
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "DocuMind.Api.dll"]
