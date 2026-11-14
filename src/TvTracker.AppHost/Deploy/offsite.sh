#!/bin/sh
# Copies a deployment's backups off the machine, encrypted, to S3-compatible storage (AWS S3, Backblaze B2,
# Cloudflare R2, Hetzner, MinIO...). It runs in the "offsite" container of the compose stack (AppHost.cs, Deployment),
# which has rclone and sees the backups folder, the flag overrides and .env.
#
#   offsite.sh                  each new backup within a minute, and everything once an hour, for as long as the
#                               container runs (what the stack starts)
#   offsite.sh once             one copy, now
#   offsite.sh list             the backups in the storage, oldest first
#   offsite.sh fetch <name>     bring one back into the backups folder ("latest" for the newest); prints its name.
#                               scripts/deploy-restore.sh --offsite does this and then restores it
#   offsite.sh env              print the .env that was copied there (for setting up a replacement machine)
#
# What goes: every finished backup (backups/<timestamp>), flags/overrides.json and .env. Everything is encrypted
# with OFFSITE_PASSPHRASE before it leaves, so the storage provider can read none of it; file names stay readable.
# KEEP THE PASSPHRASE SOMEWHERE ELSE TOO: it's in .env on this machine, and without it the copies can't be read.
#
# Copies are only ever added. Ones older than OFFSITE_KEEP_DAYS are deleted from the storage, but only after a
# backup from the last two days has arrived there, so a machine that stops making backups never empties it.
set -eu

root=/backups
keep_days=${OFFSITE_KEEP_DAYS:-30}

# The storage and the encryption over it, as rclone remotes made from the settings. Says what's missing.
setup() {
  [ -n "${OFFSITE_BUCKET:-}" ] || { echo "Off-machine backups are off: OFFSITE_BUCKET isn't set." >&2; return 1; }
  for setting in OFFSITE_ACCESS_KEY OFFSITE_SECRET_KEY OFFSITE_PASSPHRASE; do
    eval "value=\${$setting:-}"
    [ -n "$value" ] || { echo "Off-machine backups are off: $setting isn't set." >&2; return 1; }
  done
  # No config file: the remotes are these variables.
  : > /tmp/rclone.conf
  export RCLONE_CONFIG=/tmp/rclone.conf
  export RCLONE_CONFIG_STORE_TYPE=s3
  export RCLONE_CONFIG_STORE_ACCESS_KEY_ID=$OFFSITE_ACCESS_KEY
  export RCLONE_CONFIG_STORE_SECRET_ACCESS_KEY=$OFFSITE_SECRET_KEY
  export RCLONE_CONFIG_STORE_REGION=${OFFSITE_REGION:-}
  if [ -n "${OFFSITE_ENDPOINT:-}" ]; then
    export RCLONE_CONFIG_STORE_PROVIDER=Other RCLONE_CONFIG_STORE_ENDPOINT=$OFFSITE_ENDPOINT
  else
    export RCLONE_CONFIG_STORE_PROVIDER=AWS
  fi
  export RCLONE_CONFIG_VAULT_TYPE=crypt
  export RCLONE_CONFIG_VAULT_REMOTE=store:$OFFSITE_BUCKET
  export RCLONE_CONFIG_VAULT_FILENAME_ENCRYPTION=off
  export RCLONE_CONFIG_VAULT_DIRECTORY_NAME_ENCRYPTION=false
  RCLONE_CONFIG_VAULT_PASSWORD=$(rclone obscure "$OFFSITE_PASSPHRASE")
  export RCLONE_CONFIG_VAULT_PASSWORD
}

newest_local() { ls "$root" 2> /dev/null | grep '^20' | sort | tail -1; }

push() {
  newest=$(newest_local)
  [ -n "$newest" ] || return 0
  # Finished backups only (one still being written is under another name), and none the storage would delete again.
  rclone copy "$root" vault:backups --include '/20*/**' --max-age "${keep_days}d" --transfers 2
  [ ! -f /flags/overrides.json ] || rclone copyto /flags/overrides.json vault:config/overrides.json
  [ ! -f /deploy.env ] || rclone copyto /deploy.env vault:config/env
  # Read back through the encryption: what's there is what was sent, and this passphrase opens it.
  rclone cryptcheck "$root/$newest" "vault:backups/$newest" --one-way > /dev/null 2>&1 \
    || { echo "The copy of $newest doesn't match what was sent." >&2; return 1; }
  if [ "$newest" != "$(cat /tmp/copied 2> /dev/null || true)" ]; then
    echo "$(date -u +%FT%TZ) copied $newest to $OFFSITE_BUCKET"
    echo "$newest" > /tmp/copied
  fi
  # Old copies go, but only while new ones are arriving.
  if [ -n "$(find "$root/$newest" -maxdepth 0 -mtime -2)" ]; then
    rclone delete vault:backups --min-age "${keep_days}d"
    rclone rmdirs vault:backups --leave-root
  fi
}

list() { rclone lsf vault:backups --dirs-only | tr -d / | sort; }

fetch() {
  name=${1:?usage: offsite.sh fetch <name|latest>}
  [ "$name" != latest ] || name=$(list | tail -1)
  [ -n "$name" ] && [ -n "$(rclone lsf "vault:backups/$name" 2> /dev/null | head -1)" ] \
    || { echo "No backup named '$name' in $OFFSITE_BUCKET." >&2; exit 1; }
  if [ ! -d "$root/$name" ]; then
    # Under another name until it's whole, as a new backup is.
    rm -rf "$root/.partial-fetch"
    rclone copy "vault:backups/$name" "$root/.partial-fetch" --retries 1 > /tmp/fetch.log 2>&1 || {
      rm -rf "$root/.partial-fetch"
      if grep -q "bad password" /tmp/fetch.log; then
        echo "OFFSITE_PASSPHRASE isn't the one these backups were encrypted with." >&2
      else
        tail -5 /tmp/fetch.log >&2
      fi
      exit 1
    }
    mv "$root/.partial-fetch" "$root/$name"
  fi
  echo "$name"
}

case ${1:-schedule} in
  once) setup && push ;;
  list) setup && list ;;
  fetch) setup && fetch "${2:-}" ;;
  env) setup && rclone cat vault:config/env ;;
  schedule)
    # Not set up: say so once and wait, rather than be restarted over and over.
    setup || { while true; do sleep 86400; done; }
    echo "Copying backups to $OFFSITE_BUCKET as they're made, keeping $keep_days days there."
    minutes=60
    while true; do
      # A new backup goes within a minute; otherwise once an hour, for the flag overrides, .env and a copy that failed.
      if [ "$minutes" -ge 60 ] || [ "$(newest_local)" != "$(cat /tmp/copied 2> /dev/null || true)" ]; then
        minutes=0
        push || echo "$(date -u +%FT%TZ) copying backups off the machine failed; trying again in an hour" >&2
      fi
      sleep 60
      minutes=$((minutes + 1))
    done ;;
  *) echo "usage: offsite.sh [once|list|fetch <name|latest>|env]" >&2; exit 1 ;;
esac
