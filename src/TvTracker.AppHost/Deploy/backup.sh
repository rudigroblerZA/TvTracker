#!/usr/bin/env bash
# Backs up a deployment's Postgres server: every database on it (the nine the services own and Keycloak's, so
# accounts and their passwords too), one pg_dump file each, into /backups/<UTC timestamp>. It runs in the "backup"
# container of the compose stack (AppHost.cs, Deployment), which has Postgres's own tools and reaches the server on
# the stack's private network.
#
#   backup.sh                 once a day at BACKUP_HOUR_UTC, for as long as the container runs (what the stack starts)
#   backup.sh once            one backup, now
#   backup.sh list            the backups there are, oldest first
#   backup.sh restore <name>  put one back ("latest" for the newest); see scripts/deploy-restore.sh, which stops the
#                             services first
#
# Backups older than BACKUP_KEEP_DAYS are deleted after each new one; the newest is always kept.
set -euo pipefail

: "${PGHOST:?}" "${PGUSER:?}" "${PGPASSWORD:?}"
keep_days=${BACKUP_KEEP_DAYS:-14}
hour=${BACKUP_HOUR_UTC:-3}
root=/backups

databases() {
  psql -d postgres -Atc "select datname from pg_database where not datistemplate and datname <> 'postgres' order by 1"
}

# The server may still be starting when this container does.
wait_for_server() {
  local tries=0
  until pg_isready -q; do
    (( ++tries < 60 )) || { echo "Postgres isn't answering at $PGHOST." >&2; return 1; }
    sleep 2
  done
}

backup() {
  local name partial db dbs
  wait_for_server
  # Asked for first and on its own, so a failure to list them stops the backup rather than making an empty one.
  dbs=$(databases)
  [[ -n $dbs ]] || { echo "No databases to back up." >&2; return 1; }
  name=$(date -u +%Y%m%dT%H%M%SZ)
  # Written under another name and renamed when whole, so a backup that was cut short never looks like one.
  partial=$root/.partial-$name
  rm -rf "$root"/.partial-*
  mkdir -p "$partial"
  for db in $dbs; do
    pg_dump -Fc -d "$db" -f "$partial/$db.dump"
    # A dump that can't be read back is no backup: listing its contents reads the whole archive's index.
    pg_restore --list "$partial/$db.dump" > /dev/null
  done
  mv "$partial" "$root/$name"
  echo "$(date -u +%FT%TZ) backed up $(ls "$root/$name" | wc -l) databases to $name ($(du -sh "$root/$name" | cut -f1))"

  # Old ones go; the one just made stays whatever its age says.
  find "$root" -mindepth 1 -maxdepth 1 -type d -name '20*' -mtime "+$keep_days" ! -name "$name" -exec rm -rf {} +
}

list() {
  find "$root" -mindepth 1 -maxdepth 1 -type d -name '20*' -printf '%f\n' | sort
}

restore() {
  local name=${1:?usage: backup.sh restore <name|latest>} dump db
  [[ $name != latest ]] || name=$(list | tail -1)
  [[ -n $name && -d $root/$name ]] || { echo "No backup named '$name' in $root." >&2; exit 1; }
  compgen -G "$root/$name/*.dump" > /dev/null || { echo "The backup $name has no databases in it." >&2; exit 1; }
  wait_for_server
  for dump in "$root/$name"/*.dump; do
    db=$(basename "$dump" .dump)
    # Replaced whole: a database half its old self and half the backup's is worse than either.
    psql -d postgres -qc "drop database if exists \"$db\" with (force)" -c "create database \"$db\""
    pg_restore -d "$db" --no-owner --exit-on-error "$dump"
    echo "restored $db"
  done
  echo "Restored from $name."
}

case ${1:-schedule} in
  once) backup ;;
  list) list ;;
  restore) restore "${2:-}" ;;
  schedule)
    echo "Backing up daily at $hour:00 UTC, keeping $keep_days days."
    while true; do
      # Straight away when there's no backup from the last day (a first start, or a machine that was off at the hour).
      if [[ -z $(find "$root" -mindepth 1 -maxdepth 1 -type d -name '20*' -mmin -1440 | head -1) ]]; then
        backup || echo "$(date -u +%FT%TZ) backup failed; trying again in an hour" >&2
        [[ -n $(find "$root" -mindepth 1 -maxdepth 1 -type d -name '20*' -mmin -1440 | head -1) ]] || { sleep 3600; continue; }
      fi
      now=$(date -u +%s)
      next=$(date -u -d "today $hour:00" +%s)
      (( next > now )) || next=$(date -u -d "tomorrow $hour:00" +%s)
      sleep $(( next - now ))
      backup || echo "$(date -u +%FT%TZ) backup failed; trying again in an hour" >&2
    done ;;
  *) echo "usage: backup.sh [once|list|restore <name|latest>]" >&2; exit 1 ;;
esac
