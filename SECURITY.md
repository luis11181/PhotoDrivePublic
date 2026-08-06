# Security policy

Please do not open a public issue for a suspected vulnerability. Report it
privately through GitHub Security Advisories for this repository and include the
affected version, reproduction steps, impact, and any proposed mitigation.

Do not include real credentials, cookies, media, filenames, bucket names,
database contents, or private hostnames in a report.

## Deployment responsibility

PhotoDrive handles application authentication and authorization, but operators
remain responsible for the Docker host, `.env`, AWS identity, private buckets,
TLS or VPN boundary, updates, and tested restores. Keep `.env` readable only by
the operator and use a narrowly scoped AWS identity. Protect the Docker data
volume with host-level disk encryption and encrypted, access-controlled volume
backups; application data-protection keys are intentionally kept with that
volume so sessions survive container replacement.
