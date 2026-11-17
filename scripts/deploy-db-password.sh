#!/usr/bin/env bash
# Makes a deployment's Postgres accept the password in its .env again (README, Deploying: If the database refuses
# the password).
#
#   scripts/deploy-db-password.sh [directory]        # default: ~/tvtracker
#
# Postgres takes its password from POSTGRES_PASSWORD only when its data volume is first made. If .env is lost or made
# afresh while the volume stays (a deleted .env, a deploy into another folder), every service is then given a
# password the database doesn't know, and Keycloak and the rest fail with
#   FATAL: password authentication failed for user "postgres"
# This sets the database's password to .env's, keeping all the data: Postgres is stopped, started once by itself in
# single-user mode (which asks for no password, and which only someone with the volume can do) to change it, and
# everything is started again. If you still have the old .env, putting it back does the same with less.
set -euo pipefail

home=${1:-$HOME/tvtracker}
[[ -f $home/docker-compose.yaml && -f $home/.env ]] || { echo "No deployment in $home." >&2; exit 1; }
compose() { docker compose --project-name tvtracker --project-directory "$home" "$@"; }
grep -q '^POSTGRES_PASSWORD=.' "$home/.env" || { echo "No POSTGRES_PASSWORD in $home/.env." >&2; exit 1; }

echo "Stopping the services..."
compose stop > /dev/null 2>&1

echo "Setting the database's password to the one in .env..."
# The image's own server, as its own user, on the deployment's volume. The password reaches it through the
# container's environment (compose gives it POSTGRES_PASSWORD from .env), never the command line; a quote in it is
# doubled, as SQL wants.
# Quiet unless it fails: the server's own shutdown notes aren't news.
log=$(mktemp)
trap 'rm -f "$log"' EXIT
compose run --rm --no-deps -T --entrypoint bash postgres -s > "$log" 2>&1 <<'INNER' || { cat "$log" >&2; echo "The password wasn't changed." >&2; compose up -d > /dev/null 2>&1; exit 1; }
set -euo pipefail
data=${PGDATA:-/var/lib/postgresql/data}
[[ -s $data/PG_VERSION ]] || { echo "There is no database in the volume yet; nothing to change." >&2; exit 1; }
password=${POSTGRES_PASSWORD//\'/\'\'}
printf "ALTER USER \"%s\" WITH PASSWORD '%s';\n" "${POSTGRES_USER:-postgres}" "$password" \
  | gosu postgres postgres --single -D "$data" postgres > /dev/null
INNER

echo "Starting again..."
compose up -d > /dev/null 2>&1
echo "Done. Services take a minute or two to settle; scripts/deploy.sh checks the password each time it runs."
