#!/usr/bin/env bash
# Puts back a backup made by scripts/backup.sh: every database in it, and Keycloak's realm with its users.
#
#   scripts/restore.sh <dir>
#   scripts/restore.sh --databases-only | --keycloak-only <dir>
#
# Stop the AppHost first: services hold connections and caches, and would write over a database mid-restore.
#
# Databases: each dump replaces its database's objects (pg_restore --clean, in one transaction); a database missing
# from the server is created. Databases not in the backup are left alone.
#
# Keycloak: its current H2 database is moved aside inside the volume (to h2-replaced-<timestamp>), never deleted, so
# a restore can be undone by moving it back. The backup's realm is then imported into a fresh database, users with
# the ids and password hashes they had.
set -euo pipefail
cd "$(dirname "$0")/.."
source scripts/lib.sh

what=all
case "${1:-}" in
  --databases-only) what=databases; shift ;;
  --keycloak-only) what=keycloak; shift ;;
esac
dir="${1:?usage: scripts/restore.sh [--databases-only|--keycloak-only] <backup dir>}"
[[ -d $dir ]] || { echo "No backup at $dir" >&2; exit 1; }

if apphost_running; then
  echo "The AppHost is running: stop it first (services would write over the restore)." >&2
  exit 1
fi

if [[ $what != keycloak ]]; then
  pg=$(postgres_container)
  require_running "$pg"
  pass=$(docker exec "$pg" printenv POSTGRES_PASSWORD)
  for dump in "$dir"/postgres/*.dump; do
    [[ -e $dump ]] || { echo "No database dumps in $dir/postgres" >&2; break; }
    db=$(basename "$dump" .dump)
    if [[ -z $(docker exec -e PGPASSWORD="$pass" "$pg" psql -U postgres -h localhost -Atc "select 1 from pg_database where datname = '$db'") ]]; then
      docker exec -e PGPASSWORD="$pass" "$pg" createdb -U postgres -h localhost "$db"
    fi
    docker exec -i -e PGPASSWORD="$pass" "$pg" pg_restore -U postgres -h localhost -d "$db" --clean --if-exists --no-owner \
      --single-transaction < "$dump"
    echo "postgres: $db restored"
  done
fi

if [[ $what != databases ]]; then
  realm="$dir/keycloak/$KEYCLOAK_REALM-realm.json"
  [[ -f $realm ]] || { echo "No Keycloak export at $realm" >&2; exit 1; }
  kc=$(keycloak_container)
  image=$(docker inspect -f '{{.Config.Image}}' "$kc")
  docker stop "$kc" > /dev/null 2>&1 || true
  aside="h2-replaced-$(date -u +%Y%m%dT%H%M%SZ)"
  docker run --rm --entrypoint sh -v "$KEYCLOAK_VOLUME:/opt/keycloak/data" "$image" \
    -c "if [ -d /opt/keycloak/data/h2 ]; then mv /opt/keycloak/data/h2 /opt/keycloak/data/$aside; fi"
  echo "keycloak: previous database kept in the volume as $aside"
  docker run --rm -v "$KEYCLOAK_VOLUME:/opt/keycloak/data" -v "$(realpath "$dir/keycloak"):/export:ro" "$image" \
    import --dir /export --override true > /dev/null
  echo "keycloak: realm $KEYCLOAK_REALM imported"
  docker start "$kc" > /dev/null
fi

echo "Restored from $dir. Start the AppHost again."
