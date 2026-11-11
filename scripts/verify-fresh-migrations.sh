#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
solution_root="$repository_root/backend/main/DotnetBackEnd/solution"
temporary_root="$(mktemp -d -t photodrive-migrations.XXXXXX)"
trap 'rm -rf -- "$temporary_root"' EXIT

export ASPNETCORE_ENVIRONMENT=Development
# A disposable local Turso file; never the Development cloud database from infrastructure.Development.json.
export Database__Url="$temporary_root/photodrive.db"
export Database__ReplicaPath="$temporary_root/turso/photodrive.replica.db"
export Security__DataProtectionKeysDirectory="$temporary_root/keys"
export MediaStorage__LocalDisk__StagingDirectory="$temporary_root/staging"
export MediaStorage__LocalDisk__ThumbnailDirectory="$temporary_root/thumbnails"
export MediaStorage__LocalDisk__OriginalCacheDirectory="$temporary_root/cache/blobs/sha256"
export MediaStorage__LocalDisk__BackupDirectory="$temporary_root/backups"
export MediaStorage__Backups__ScheduleEnabled=false
export AccessControl__IpAllowlist__Enabled=false
export ReverseProxy__Enabled=false

dotnet run --project "$solution_root/src/Hosts/CSSApps.Api/CSSApps.Api.csproj" --no-build -- database migrate --confirm MIGRATE
test -s "$temporary_root/photodrive.db"
echo "Fresh Media catalog and Users migrations succeeded on a local Turso database."
