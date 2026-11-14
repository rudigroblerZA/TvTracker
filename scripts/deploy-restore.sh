#!/usr/bin/env bash
# Puts a deployment's databases back from one of its backups (README, Deploying: Backups).
#
#   scripts/deploy-restore.sh [directory] <name|latest>     # directory defaults to ~/tvtracker
#   scripts/deploy-restore.sh [directory] --list
#
# Everything but Postgres is stopped first (services hold connections, and would write over a database mid-restore),
# each database in the backup replaces the one on the server, and the stack is started again. Databases the backup
# doesn't have are left alone. What people did after the backup was taken is lost: that's what restoring means.
set -euo pipefail

home=$HOME/tvtracker
if [[ $# -ge 2 || ( $# -eq 1 && -d $1 ) ]]; then
  home=$1
  shift
fi
what=${1:?usage: scripts/deploy-restore.sh [directory] <name|latest|--list>}
[[ -f $home/docker-compose.yaml ]] || { echo "No deployment in $home." >&2; exit 1; }
compose() { docker compose --project-name tvtracker --project-directory "$home" "$@"; }
tool() { compose run --rm --no-deps -T --entrypoint bash backup /backup.sh "$@"; }

if [[ $what == --list ]]; then
  tool list
  exit
fi

echo "Stopping the services..."
# Postgres stays up; so does nothing that talks to it.
mapfile -t others < <(compose config --services | grep -v '^postgres$')
compose stop "${others[@]}" > /dev/null 2>&1
compose up -d postgres > /dev/null 2>&1

tool restore "$what"

echo "Starting again..."
compose up -d > /dev/null 2>&1
echo "Done. Services take a minute or two to settle."
