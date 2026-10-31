#!/usr/bin/env bash
# Backs up the development data: every Postgres database (pg_dump, custom format) and Keycloak's tvtracker realm with
# its users (ids and password hashes included, so a restore keeps every service's data linked to its user).
#
#   scripts/backup.sh [dir]        default: backups/<UTC timestamp>
#   scripts/backup.sh --databases-only | --keycloak-only [dir]
#
# Postgres is dumped while it runs. Keycloak keeps its data in an embedded H2 database that the server holds open, so
# its container is stopped for the export (a few seconds) and started again; sign-in is unavailable meanwhile.
# See scripts/restore.sh to put a backup back.
set -euo pipefail
cd "$(dirname "$0")/.."
source scripts/lib.sh

what=all
case "${1:-}" in
  --databases-only) what=databases; shift ;;
  --keycloak-only) what=keycloak; shift ;;
esac
out="${1:-backups/$(date -u +%Y%m%dT%H%M%SZ)}"
mkdir -p "$out"

if [[ $what != keycloak ]]; then
  pg=$(postgres_container)
  require_running "$pg"
  pass=$(docker exec "$pg" printenv POSTGRES_PASSWORD)
  mkdir -p "$out/postgres"
  for db in $(docker exec -e PGPASSWORD="$pass" "$pg" psql -U postgres -h localhost -Atc \
      "select datname from pg_database where not datistemplate and datname <> 'postgres' order by 1"); do
    docker exec -e PGPASSWORD="$pass" "$pg" pg_dump -U postgres -h localhost -Fc -d "$db" > "$out/postgres/$db.dump"
    echo "postgres: $db ($(du -h "$out/postgres/$db.dump" | cut -f1))"
  done
fi

if [[ $what != databases ]]; then
  kc=$(keycloak_container)
  image=$(docker inspect -f '{{.Config.Image}}' "$kc")
  mkdir -p "$out/keycloak"
  chmod 777 "$out/keycloak"   # Keycloak's image runs as uid 1000.
  was_running=$(docker inspect -f '{{.State.Running}}' "$kc")
  [[ $was_running == true ]] && docker stop "$kc" > /dev/null
  if docker run --rm -v "$KEYCLOAK_VOLUME:/opt/keycloak/data" -v "$(realpath "$out/keycloak"):/export" "$image" \
      export --dir /export --realm "$KEYCLOAK_REALM" --users realm_file > "$out/keycloak/export.log" 2>&1; then
    echo "keycloak: realm $KEYCLOAK_REALM with $(python3 -c "import json,sys;print(len(json.load(open(sys.argv[1])).get('users',[])))" \
      "$out/keycloak/$KEYCLOAK_REALM-realm.json") users"
  else
    echo "keycloak: export failed; see $out/keycloak/export.log" >&2
    status=1
  fi
  [[ $was_running == true ]] && docker start "$kc" > /dev/null
fi

echo "Backed up to $out"
exit "${status:-0}"
