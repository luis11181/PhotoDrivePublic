#!/bin/sh
set -eu

fail() {
  printf 'PhotoDrive configuration error: %s\n' "$1" >&2
  exit 1
}

require_value() {
  eval "value=\${$1:-}"
  [ -n "$value" ] || fail "$1 is required in .env"
  case "$value" in
    replace-*|*example.com) fail "$1 still contains an example value" ;;
  esac
}

require_value PHOTODRIVE_ORIGIN
require_value PHOTODRIVE_OWNER_EMAIL
require_value PHOTODRIVE_JWT_SECRET
require_value AWS_ACCESS_KEY_ID
require_value AWS_SECRET_ACCESS_KEY
require_value PHOTODRIVE_ORIGINALS_BUCKET
require_value PHOTODRIVE_BACKUPS_BUCKET

[ "$PHOTODRIVE_ORIGINALS_BUCKET" != "$PHOTODRIVE_BACKUPS_BUCKET" ] || \
  fail "the originals and backups buckets must be different"

origin=${PHOTODRIVE_ORIGIN%/}
case "$origin" in
  https://*) require_https=true ;;
  http://*) require_https=false ;;
  *) fail "PHOTODRIVE_ORIGIN must start with http:// or https://" ;;
esac

authority=${origin#*://}
authority=${authority%%/*}
host=${authority%%:*}
[ -n "$host" ] || fail "PHOTODRIVE_ORIGIN does not contain a hostname"

if [ ! -f /data/databases/photodrive.users.db ] && \
   [ -z "${PHOTODRIVE_OWNER_PASSWORD:-}" ] && \
   [ -z "${GOOGLE_CLIENT_ID:-}" ]; then
  fail "PHOTODRIVE_OWNER_PASSWORD is required for the first startup unless Google OAuth is configured"
fi

umask 077
install -d /home/photodrive/.aws /data/databases /data/cache /data/backups /data/keys
{
  printf '[photodrive]\n'
  printf 'aws_access_key_id=%s\n' "$AWS_ACCESS_KEY_ID"
  printf 'aws_secret_access_key=%s\n' "$AWS_SECRET_ACCESS_KEY"
  if [ -n "${AWS_SESSION_TOKEN:-}" ]; then
    printf 'aws_session_token=%s\n' "$AWS_SESSION_TOKEN"
  fi
} > /home/photodrive/.aws/credentials

export AWS_SHARED_CREDENTIALS_FILE=/home/photodrive/.aws/credentials
export AWS_REGION="${AWS_REGION:-us-east-1}"
export Urls=http://0.0.0.0:8080
export AllowedHosts="$host"
export Security__RequireHttps="$require_https"
export Frontend__AllowedOrigins__0="$origin"
export JwtSettings__Issuer="$origin"
export JwtSettings__Audience="$origin"
export JwtSettings__AccessTokenSecret="$PHOTODRIVE_JWT_SECRET"
export FamilyAuthentication__PasswordLoginEnabled="${PHOTODRIVE_PASSWORD_LOGIN:-true}"
export FamilyAuthentication__BootstrapOwner__Email="$PHOTODRIVE_OWNER_EMAIL"
export FamilyAuthentication__BootstrapOwner__InitialPassword="${PHOTODRIVE_OWNER_PASSWORD:-}"
export OAuth__Google__ClientId="${GOOGLE_CLIENT_ID:-}"
export OAuth__Google__ClientSecret="${GOOGLE_CLIENT_SECRET:-}"
export MediaStorage__S3__BucketName="$PHOTODRIVE_ORIGINALS_BUCKET"
export MediaStorage__S3__ProfileName=photodrive
export MediaStorage__S3__Region="$AWS_REGION"
export MediaStorage__S3Backups__BucketName="$PHOTODRIVE_BACKUPS_BUCKET"
export MediaStorage__S3Backups__ProfileName=photodrive
export MediaStorage__S3Backups__Region="$AWS_REGION"
export MediaStorage__Backups__TimeZoneId="${PHOTODRIVE_TIME_ZONE:-UTC}"
export MediaStorage__Backups__IntervalHours="${PHOTODRIVE_BACKUP_INTERVAL_HOURS:-12}"
export MediaStorage__Backups__RemoteRetentionDays="${PHOTODRIVE_BACKUP_RETENTION_DAYS:-90}"
export MediaStorage__LocalDisk__BackupRetainedSnapshotCount="${PHOTODRIVE_LOCAL_BACKUP_COUNT:-14}"
export MediaStorage__LocalDisk__OriginalCacheMaxBytes="${PHOTODRIVE_CACHE_MAX_BYTES:-10737418240}"
export MediaStorage__LocalDisk__OriginalCacheAutomaticMaxFileBytes="${PHOTODRIVE_CACHE_FILE_MAX_BYTES:-536870912}"
export MediaStorage__BackupStrategies__RecoveryManifestsEnabled="${PHOTODRIVE_RECOVERY_MANIFESTS:-true}"
export MediaStorage__BackupStrategies__PortableJsonExportsEnabled="${PHOTODRIVE_PORTABLE_EXPORTS:-false}"
export Logging__LogLevel__Default="${PHOTODRIVE_LOG_LEVEL:-Information}"

if [ -n "${PHOTODRIVE_ALLOWED_NETWORKS:-}" ]; then
  export AccessControl__IpAllowlist__Enabled=true
  export AccessControl__IpAllowlist__AllowedNetworks="$PHOTODRIVE_ALLOWED_NETWORKS"
else
  export AccessControl__IpAllowlist__Enabled=false
fi

if [ "${PHOTODRIVE_BEHIND_PROXY:-false}" = true ]; then
  export ReverseProxy__Enabled=true
  export ReverseProxy__KnownNetworks__0="${PHOTODRIVE_TRUSTED_PROXY_NETWORK:-172.16.0.0/12}"
else
  export ReverseProxy__Enabled=false
fi

exec dotnet CSSApps.Api.dll "$@"
