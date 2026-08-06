#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
solution_root="$repository_root/backend/main/DotnetBackEnd/solution"
temporary_root="$(mktemp -d -t photodrive-migrations.XXXXXX)"
trap 'rm -rf -- "$temporary_root"' EXIT

export ASPNETCORE_ENVIRONMENT=Development
export ConnectionStrings__MediaCatalog="Data Source=$temporary_root/catalog.db;Foreign Keys=True;Pooling=False"
export ConnectionStrings__UsersModule="Data Source=$temporary_root/users.db;Foreign Keys=True;Pooling=False"
export MediaStorage__LocalDisk__StagingDirectory="$temporary_root/staging"
export MediaStorage__LocalDisk__ThumbnailDirectory="$temporary_root/thumbnails"
export MediaStorage__LocalDisk__OriginalCacheDirectory="$temporary_root/cache/blobs/sha256"
export MediaStorage__LocalDisk__BackupDirectory="$temporary_root/backups"
export MediaStorage__Backups__ScheduleEnabled=false
export AccessControl__IpAllowlist__Enabled=false
export ReverseProxy__Enabled=false

dotnet run --project "$solution_root/src/Hosts/CSSApps.Api/CSSApps.Api.csproj" --no-build -- database migrate --confirm MIGRATE
test -s "$temporary_root/catalog.db"
test -s "$temporary_root/users.db"
echo "Fresh Media catalog and Users SQLite migrations succeeded."
