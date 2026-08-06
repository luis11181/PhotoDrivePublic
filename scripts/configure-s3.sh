#!/bin/sh
set -eu

region=${AWS_REGION:-us-east-1}
retention_days=${PHOTODRIVE_BACKUP_RETENTION_DAYS:-90}

for name in AWS_ACCESS_KEY_ID AWS_SECRET_ACCESS_KEY PHOTODRIVE_ORIGINALS_BUCKET PHOTODRIVE_BACKUPS_BUCKET; do
  eval "value=\${$name:-}"
  [ -n "$value" ] || { printf '%s is required in .env\n' "$name" >&2; exit 1; }
done

create_bucket() {
  bucket=$1
  if aws s3api head-bucket --bucket "$bucket" --region "$region" >/dev/null 2>&1; then
    return
  fi

  if [ "$region" = us-east-1 ]; then
    aws s3api create-bucket --bucket "$bucket" --region "$region"
  else
    aws s3api create-bucket \
      --bucket "$bucket" \
      --region "$region" \
      --create-bucket-configuration "LocationConstraint=$region"
  fi
}

harden_bucket() {
  bucket=$1
  aws s3api put-public-access-block \
    --bucket "$bucket" \
    --region "$region" \
    --public-access-block-configuration 'BlockPublicAcls=true,IgnorePublicAcls=true,BlockPublicPolicy=true,RestrictPublicBuckets=true'
  aws s3api put-bucket-ownership-controls \
    --bucket "$bucket" \
    --region "$region" \
    --ownership-controls 'Rules=[{ObjectOwnership=BucketOwnerEnforced}]'
  aws s3api put-bucket-encryption \
    --bucket "$bucket" \
    --region "$region" \
    --server-side-encryption-configuration '{"Rules":[{"ApplyServerSideEncryptionByDefault":{"SSEAlgorithm":"AES256"}}]}'
  aws s3api put-bucket-versioning \
    --bucket "$bucket" \
    --region "$region" \
    --versioning-configuration Status=Enabled
}

create_bucket "$PHOTODRIVE_ORIGINALS_BUCKET"
harden_bucket "$PHOTODRIVE_ORIGINALS_BUCKET"

create_bucket "$PHOTODRIVE_BACKUPS_BUCKET"
harden_bucket "$PHOTODRIVE_BACKUPS_BUCKET"

lifecycle=$(printf '{"Rules":[{"ID":"MediaExplorerCatalogBackupRetention","Status":"Enabled","Filter":{"Prefix":""},"Expiration":{"Days":%s},"NoncurrentVersionExpiration":{"NoncurrentDays":30},"AbortIncompleteMultipartUpload":{"DaysAfterInitiation":7}}]}' "$retention_days")
aws s3api put-bucket-lifecycle-configuration \
  --bucket "$PHOTODRIVE_BACKUPS_BUCKET" \
  --region "$region" \
  --lifecycle-configuration "$lifecycle"

printf 'Configured private, encrypted, versioned PhotoDrive buckets in %s.\n' "$region"
