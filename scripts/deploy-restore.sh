#!/usr/bin/env bash
# Puts a deployment's databases back from one of its backups (README, Deploying: Backups).
#
#   scripts/deploy-restore.sh [directory] <name|latest>     # directory defaults to ~/tvtracker
#   scripts/deploy-restore.sh [directory] --list
#   scripts/deploy-restore.sh [directory] --offsite <name|latest>   # one from the copies off the machine
#   scripts/deploy-restore.sh [directory] --offsite-list
#
# Everything but Postgres is stopped first (services hold connections, and would write over a database mid-restore),
# each database in the backup replaces the one on the server, and the stack is started again. Databases the backup
# doesn't have are left alone. What people did after the backup was taken is lost: that's what restoring means.
set -euo pipefail

home=$HOME/tvtracker
# A first argument that isn't an option is the directory, when something follows it or it is one.
if [[ ${1:-} != --* && ( $# -ge 2 || ( $# -eq 1 && -d $1 ) ) ]]; then
  home=$1
  shift
fi
usage="usage: scripts/deploy-restore.sh [directory] <name|latest|--list|--offsite <name|latest>|--offsite-list>"
what=${1:?$usage}
[[ -f $home/docker-compose.yaml ]] || { echo "No deployment in $home." >&2; exit 1; }
compose() { docker compose --project-name tvtracker --project-directory "$home" "$@"; }
tool() { compose run --rm --no-deps -T --entrypoint bash backup /backup.sh "$@"; }

# The same, in the container that copies backups off the machine.
offsite() { compose run --rm --no-deps -T --entrypoint sh offsite /offsite.sh "$@"; }

case $what in
  --list) tool list; exit ;;
  --offsite-list) offsite list; exit ;;
  --offsite)
    # Brought back into the backups folder first; from there it's a backup like any other.
    echo "Fetching ${2:?$usage} from the copies off the machine..."
    what=$(offsite fetch "$2")
    echo "Fetched $what." ;;
esac

echo "Stopping the services..."
# Postgres stays up; so does nothing that talks to it.
mapfile -t others < <(compose config --services | grep -v '^postgres$')
compose stop "${others[@]}" > /dev/null 2>&1
compose up -d postgres > /dev/null 2>&1

tool restore "$what"

echo "Starting again..."
compose up -d > /dev/null 2>&1
echo "Done. Services take a minute or two to settle."
