#!/usr/bin/env bash
# Creates or hardens private, versioned, encrypted S3 buckets for PhotoDrive database backups.
# Usage: scripts/configure-s3-backup-buckets.sh <bucket>:<retention-days> [<bucket>:<retention-days> ...]
# Example: scripts/configure-s3-backup-buckets.sh my-photodrive-backups:180 my-photodrive-dev-backups:90
set -euo pipefail

profile="${AWS_PROFILE:-photodrive}"
region="${AWS_REGION:-us-east-1}"
buckets=("$@")

if ((${#buckets[@]} == 0)); then
  echo "Usage: $0 <bucket>:<retention-days> [...]" >&2
  exit 2
fi
if ! command -v aws >/dev/null 2>&1; then
  echo "aws-cli is required. Install it from https://aws.amazon.com/cli/" >&2
  exit 1
fi

for entry in "${buckets[@]}"; do
  bucket="${entry%%:*}"
  configured_retention_days="${entry##*:}"
  retention_days="${MEDIA_EXPLORER_BACKUP_RETENTION_DAYS:-${configured_retention_days}}"
  namespace_args=()
  # Names ending in -an use the S3 account-regional namespace (<prefix>-<account>-<region>-an).
  [[ ${bucket} == *-an ]] && namespace_args=(--bucket-namespace account-regional)
  if ! aws s3api head-bucket --bucket "${bucket}" --profile "${profile}" --region "${region}" >/dev/null 2>&1; then
    if [[ "${region}" == "us-east-1" ]]; then
      aws s3api create-bucket --bucket "${bucket}" ${namespace_args[@]+"${namespace_args[@]}"} --profile "${profile}" --region "${region}"
    else
      aws s3api create-bucket --bucket "${bucket}" ${namespace_args[@]+"${namespace_args[@]}"} --create-bucket-configuration "LocationConstraint=${region}" --profile "${profile}" --region "${region}"
    fi
  fi

  aws s3api put-public-access-block \
    --bucket "${bucket}" \
    --public-access-block-configuration "BlockPublicAcls=true,IgnorePublicAcls=true,BlockPublicPolicy=true,RestrictPublicBuckets=true" \
    --profile "${profile}" \
    --region "${region}"

  aws s3api put-bucket-ownership-controls \
    --bucket "${bucket}" \
    --ownership-controls 'Rules=[{ObjectOwnership=BucketOwnerEnforced}]' \
    --profile "${profile}" \
    --region "${region}"

  aws s3api put-bucket-encryption \
    --bucket "${bucket}" \
    --server-side-encryption-configuration '{"Rules":[{"ApplyServerSideEncryptionByDefault":{"SSEAlgorithm":"AES256"}}]}' \
    --profile "${profile}" \
    --region "${region}"

  aws s3api put-bucket-versioning \
    --bucket "${bucket}" \
    --versioning-configuration Status=Enabled \
    --profile "${profile}" \
    --region "${region}"

  lifecycle_configuration="{\"Rules\":[{\"ID\":\"MediaExplorerCatalogBackupRetention\",\"Status\":\"Enabled\",\"Filter\":{\"Prefix\":\"\"},\"Expiration\":{\"Days\":${retention_days}},\"NoncurrentVersionExpiration\":{\"NoncurrentDays\":30},\"AbortIncompleteMultipartUpload\":{\"DaysAfterInitiation\":7}}]}"
  aws s3api put-bucket-lifecycle-configuration \
    --bucket "${bucket}" \
    --lifecycle-configuration "${lifecycle_configuration}" \
    --profile "${profile}" \
    --region "${region}"

  echo "Configured ${bucket}: private, AES-256 encrypted, versioned, ${retention_days}-day backup retention."
done
