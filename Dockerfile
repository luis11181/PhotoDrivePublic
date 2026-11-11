# syntax=docker/dockerfile:1.7

FROM node:24.22.2-trixie-slim AS frontend-build
WORKDIR /source/frontend
COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci
COPY frontend/ ./
ENV NEXT_PUBLIC_API_BASE_URL="" \
    NEXT_PUBLIC_API_SAME_ORIGIN=true
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0-resolute AS backend-build
WORKDIR /source
COPY backend/main/DotnetBackEnd/solution/ ./
RUN dotnet restore src/Hosts/CSSApps.Api/CSSApps.Api.csproj
RUN dotnet publish src/Hosts/CSSApps.Api/CSSApps.Api.csproj \
    --configuration Release \
    --no-restore \
    --output /publish

# Ubuntu 26.04 LTS: glibc for the Turso native library, apt for ffmpeg/ImageMagick 7, and current HEIC/AVIF decoders.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-resolute AS runtime
LABEL io.photodrive.operator-cli="1"
ARG PHOTODRIVE_UID=1000
ARG PHOTODRIVE_GID=1000

ENV DEBIAN_FRONTEND=noninteractive
RUN existing_group="$(getent group "${PHOTODRIVE_GID}" | cut -d: -f1 || true)" \
    && if [ -n "${existing_group}" ] && [ "${existing_group}" != photodrive ]; then groupmod --new-name photodrive "${existing_group}"; elif [ -z "${existing_group}" ]; then groupadd --gid "${PHOTODRIVE_GID}" photodrive; fi \
    && existing_user="$(getent passwd "${PHOTODRIVE_UID}" | cut -d: -f1 || true)" \
    && if [ -n "${existing_user}" ] && [ "${existing_user}" != photodrive ]; then usermod --login photodrive --home /home/photodrive --move-home "${existing_user}"; elif [ -z "${existing_user}" ]; then useradd --uid "${PHOTODRIVE_UID}" --gid photodrive --create-home --home-dir /home/photodrive photodrive; fi \
    && usermod --gid photodrive --groups photodrive photodrive \
    && apt-get update \
    && apt-get install --yes --no-install-recommends ca-certificates curl ffmpeg file imagemagick tzdata \
    && rm -rf /var/lib/apt/lists/* \
    && install -d -o photodrive -g photodrive /app /home/photodrive/.aws

WORKDIR /app
COPY --from=backend-build --chown=photodrive:photodrive /publish/ ./
COPY --from=frontend-build --chown=photodrive:photodrive /source/frontend/out/ ./wwwroot/
COPY --chown=photodrive:photodrive docker/entrypoint.sh /usr/local/bin/photodrive-entrypoint
RUN sed -i 's/\r$//' /usr/local/bin/photodrive-entrypoint \
    && chmod 0755 /usr/local/bin/photodrive-entrypoint

USER photodrive
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS=8080 \
    HOME=/home/photodrive \
    DOTNET_EnableDiagnostics=0
EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
  CMD curl --fail --silent http://127.0.0.1:8080/health >/dev/null || exit 1

ENTRYPOINT ["/usr/local/bin/photodrive-entrypoint"]
