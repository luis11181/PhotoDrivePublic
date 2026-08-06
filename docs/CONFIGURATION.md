# Configuration

PhotoDrive reads safe application defaults from the image and user settings
from the root `.env` file. Start from `.env.example`; never commit `.env`.

## Required settings

| Variable | Purpose |
| --- | --- |
| `PHOTODRIVE_ORIGIN` | Exact browser origin without a trailing slash |
| `PHOTODRIVE_PORT` | Host port published by Docker |
| `PHOTODRIVE_OWNER_EMAIL` | Email for the one-time first Owner bootstrap |
| `PHOTODRIVE_OWNER_PASSWORD` | Temporary strong password for the first Owner |
| `PHOTODRIVE_JWT_SECRET` | Random signing secret of at least 32 UTF-8 bytes |
| `AWS_ACCESS_KEY_ID` | AWS identity used only by the server and setup tool |
| `AWS_SECRET_ACCESS_KEY` | Matching AWS secret |
| `AWS_REGION` | Region containing both buckets |
| `PHOTODRIVE_ORIGINALS_BUCKET` | Unique private bucket for immutable originals |
| `PHOTODRIVE_BACKUPS_BUCKET` | Different private bucket for verified backups |

Generate the JWT secret with `openssl rand -base64 48`. The owner password must
be at least 12 characters and include uppercase, lowercase, a digit, and a
symbol. It is a bootstrap credential, not a permanent master password.

## Google OAuth

Google login is optional. Create a Google Web OAuth client, put its client ID
and secret in `GOOGLE_CLIENT_ID` and `GOOGLE_CLIENT_SECRET`, and register the
exact `PHOTODRIVE_ORIGIN` as an authorized JavaScript origin. HTTPS is required
for non-localhost Google browser origins.

Google login does not create accounts. An Owner must first create the family
account and allow Google as its login method.

## Network exposure

Plain HTTP is accepted only for loopback and private-network origins. Public
hostnames must use HTTPS. PhotoDrive does not terminate TLS itself; place a
trusted HTTPS reverse proxy or private access layer in front of port 8080.

When using a reverse proxy:

```dotenv
PHOTODRIVE_ORIGIN=https://photos.example.net
PHOTODRIVE_BEHIND_PROXY=true
PHOTODRIVE_TRUSTED_PROXY_NETWORK=172.20.0.0/24
```

Trust only the address or network used by the direct proxy hop. Never configure
`0.0.0.0/0` or `::/0` as a trusted proxy network.

To restrict all application traffic to known client networks, set a
comma-separated allowlist:

```dotenv
PHOTODRIVE_ALLOWED_NETWORKS=192.168.1.0/24,100.64.0.0/10
```

Health checks originating inside the container remain available.

## Storage and backups

| Variable | Default | Meaning |
| --- | ---: | --- |
| `PHOTODRIVE_TIME_ZONE` | `UTC` | IANA zone used by the backup schedule |
| `PHOTODRIVE_CACHE_MAX_BYTES` | `10737418240` | Maximum local original cache size |
| `PHOTODRIVE_CACHE_FILE_MAX_BYTES` | `536870912` | Largest original automatically cached after a full read |
| `PHOTODRIVE_BACKUP_INTERVAL_HOURS` | `12` | Hours between verified backups |
| `PHOTODRIVE_BACKUP_RETENTION_DAYS` | `90` | Remote backup-set retention |
| `PHOTODRIVE_LOCAL_BACKUP_COUNT` | `14` | Local verified snapshots retained |
| `PHOTODRIVE_RECOVERY_MANIFESTS` | `true` | Store immutable per-original recovery hints |
| `PHOTODRIVE_PORTABLE_EXPORTS` | `false` | Add checksummed JSONL exports to backups |

The local cache is replaceable. The databases and data-protection keys in the
same Docker volume are not. Preserve the whole volume during ordinary upgrades.

## Validation

Run this after every configuration change:

```bash
./photodrive validate
```

The command reports invalid origins, paths, secrets, buckets, authentication
settings, proxy trust, and network rules without printing secret values.
