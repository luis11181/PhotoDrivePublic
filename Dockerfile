# syntax=docker/dockerfile:1.7

FROM node:24-bookworm-slim AS frontend-build
WORKDIR /source/frontend
COPY frontend/package.json frontend/package-lock.json ./
RUN npm install --global npm@11.6.2 \
    && npm ci
COPY frontend/ ./
ENV NEXT_PUBLIC_API_BASE_URL=""
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS backend-build
WORKDIR /source
COPY backend/main/DotnetBackEnd/solution/ ./
RUN dotnet restore src/Hosts/CSSApps.Api/CSSApps.Api.csproj
RUN dotnet publish src/Hosts/CSSApps.Api/CSSApps.Api.csproj \
    --configuration Release \
    --no-restore \
    --output /publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
RUN apt-get update \
    && apt-get install --yes --no-install-recommends ca-certificates curl ffmpeg file imagemagick \
    && rm -rf /var/lib/apt/lists/*

RUN groupadd --system photodrive \
    && useradd --system --gid photodrive --create-home --home-dir /home/photodrive photodrive \
    && install -d -o photodrive -g photodrive /app /data /home/photodrive/.aws

WORKDIR /app
COPY --from=backend-build --chown=photodrive:photodrive /publish/ ./
COPY --from=frontend-build --chown=photodrive:photodrive /source/frontend/out/ ./wwwroot/
COPY --chown=photodrive:photodrive docker/entrypoint.sh /usr/local/bin/photodrive-entrypoint

USER photodrive
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS=8080 \
    HOME=/home/photodrive \
    DOTNET_EnableDiagnostics=0
EXPOSE 8080
VOLUME ["/data"]

HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
  CMD curl --fail --silent http://127.0.0.1:8080/health >/dev/null || exit 1

ENTRYPOINT ["/usr/local/bin/photodrive-entrypoint"]
