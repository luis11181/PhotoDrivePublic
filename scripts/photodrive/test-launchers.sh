#!/usr/bin/env bash
# Isolated Linux checks; mock Docker, no host credentials, Turso, or live database access.
set -euo pipefail
root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)
temporary=$(mktemp -d -t photodrive-launcher-check.XXXXXX)
trap 'rm -rf -- "$temporary"' EXIT
cp -- "$root/photodrive" "$temporary/photodrive"
mkdir -p "$temporary/bin" "$temporary/aws" "$temporary/home"
touch "$temporary/aws/config" "$temporary/aws/credentials"
export HOME=$temporary/home TEST_DOCKER_CALLS=$temporary/docker-calls
export PATH=$temporary/bin:$PATH
cat > "$temporary/bin/docker" <<'MOCK'
#!/usr/bin/env bash
printf '%s | secrets=%s\n' "$*" "${PHOTODRIVE_SECRETS_FILE:-}" >> "$TEST_DOCKER_CALLS"
if [[ $1 == info ]]; then echo linux; fi
MOCK
chmod +x "$temporary/bin/docker"
run() { bash "$temporary/photodrive" "$@"; }
fail() { printf '%s\n' "$*" >&2; exit 1; }
expect_failure() { local pattern=$1; shift; if run "$@" >"$temporary/output" 2>&1; then fail 'Expected failure'; fi; grep -q -- "$pattern" "$temporary/output" || { cat "$temporary/output"; fail 'Wrong failure'; }; }
run help >/dev/null
run configure --environment development --data-directory "$temporary/data with spaces" --import-directory "$temporary/imports" --aws-directory "$temporary/aws" --port 6046 >/dev/null
run config --environment development | grep -q "DataDirectory=$temporary/data with spaces"
expect_failure 'Missing .*secrets.Development.json' start --environment development
if grep -Eq ' run | up ' "$TEST_DOCKER_CALLS"; then fail 'Nothing may start without a secrets file'; fi
secrets=$temporary/backend/main/DotnetBackEnd/solution/src/Hosts/CSSApps.Api/secrets.Development.json
mkdir -p "$(dirname "$secrets")" && printf '{ "Database": { "AuthToken": "test-token" } }\n' > "$secrets"
mkdir -p "$temporary/data with spaces/Databases/MediaExplorer-test" "$temporary/data with spaces/Cache/MediaExplorer-test" "$temporary/imports"
# REMOVE-WITH-SQLITE-IMPORT: part of the one-time v1 (SQLite) to v2 (Turso) import. Development was imported on 2026-11-09 and Production on 2026-11-10; delete it once the v1 files are no longer kept (search the repo for this marker).
expect_failure 'import-sqlite needs' import-sqlite --environment development
if grep -q ' stop ' "$TEST_DOCKER_CALLS"; then fail 'A failed import-sqlite check must not stop the running app'; fi
for name in media-explorer.catalog.db media-explorer.users.db; do : > "$temporary/data with spaces/Databases/MediaExplorer-test/$name"; done
run import-sqlite --environment development
grep -E ' stop | database import-sqlite ' "$TEST_DOCKER_CALLS" | head -1 | grep -q ' stop ' || fail 'import-sqlite must stop the running app before importing'
grep -q 'database import-sqlite --catalog /data/databases/media-explorer.catalog.db' "$TEST_DOCKER_CALLS" || fail 'import-sqlite must run the import'
run migrate --environment development
grep -q "photodrive-development.*database migrate --confirm MIGRATE | secrets=$secrets" "$TEST_DOCKER_CALLS" || fail 'Migrate must use the Development project and its secrets file'
printf 'Linux launcher checks passed\n'
