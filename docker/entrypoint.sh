#!/bin/sh
set -eu

fail() {
  printf 'PhotoDrive container error: %s\n' "$1" >&2
  exit 1
}

database_directory=${PHOTODRIVE_DATABASE_DIRECTORY:-/data/databases}
cache_directory=${PHOTODRIVE_CACHE_DIRECTORY:-/data/cache}

for directory in \
  "$database_directory" \
  "$cache_directory"; do
  [ -d "$directory" ] || fail "required bind mount is missing: $directory"
  [ -r "$directory" ] || fail "required bind mount is not readable: $directory"
  [ -w "$directory" ] || fail "required bind mount is not writable by UID $(id -u): $directory"
done

for file in \
  /app/appsettings.json \
  /app/authentication.json \
  /app/deployment.json \
  /app/infrastructure.json \
  /app/logging.json \
  /app/usersSettings.json \
  /home/photodrive/.aws/credentials \
  /home/photodrive/.aws/config; do
  [ -r "$file" ] || fail "required read-only file is missing or unreadable: $file"
done

umask 077
exec dotnet CSSApps.Api.dll "$@"
