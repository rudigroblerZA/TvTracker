#!/usr/bin/env bash
# Deploys the app to this machine with Docker Compose (README, Deploying).
#
#   scripts/deploy.sh [directory]        # default: ~/tvtracker
#
# It publishes the AppHost to a compose file, builds each service's image, and starts everything. The directory keeps
# what must survive between deploys: .env (settings and generated secrets), the feature-flag overrides, and the
# compose file. Run it again to deploy a newer checkout; values already in .env are kept.
#
# The first run needs three things, as environment variables (later runs read them from .env):
#   PUBLIC_HOST     the site's host name, e.g. tv.example.com (its DNS must point at this machine). The mobile
#                   gateway is under it, at https://PUBLIC_HOST/api
#   OPERATOR_NAME   who runs the site, shown in the privacy policy and terms
#   CONTACT_EMAIL   where people can write to
# Optional: API_HOST, a host name of its own for the gateway as well, e.g. api.tv.example.com (its DNS likewise).
# Optional: PROXY_PORT, when a reverse proxy of your own (Caddy, nginx, a load balancer) already has ports 80 and 443
#           and holds the certificate: this deployment then listens on that port of this machine only, in plain
#           HTTP, for your proxy to send PUBLIC_HOST to (README, Deploying: behind your own reverse proxy).
#
# Needs: Docker with the compose plugin, the .NET SDK (global.json), the Aspire CLI (dotnet tool install -g Aspire.Cli),
# jq and openssl.
set -euo pipefail

repo=$(cd "$(dirname "$0")/.." && pwd)
home=${1:-$HOME/tvtracker}
apphost=$repo/src/TvTracker.AppHost

for tool in docker dotnet aspire jq openssl; do
  command -v "$tool" > /dev/null || { echo "Missing $tool." >&2; exit 1; }
done
docker compose version > /dev/null || { echo "Docker's compose plugin is missing." >&2; exit 1; }

mkdir -p "$home/flags" "$home/keycloak" "$home/backups"
env_file=$home/.env
touch "$env_file" && chmod 600 "$env_file"

# The value a key has: in .env already, else in this shell's environment, else nothing.
current() {
  local value
  value=$(grep -m1 "^$1=" "$env_file" | cut -d= -f2- || true)
  echo "${value:-${!1:-}}"
}

echo "Publishing the compose file..."
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
(cd "$apphost" && aspire publish -o "$work" --non-interactive --nologo > "$work/publish.log" 2>&1) || { cat "$work/publish.log" >&2; exit 1; }
cp "$work/docker-compose.yaml" "$home/docker-compose.yaml"

# The tag: this checkout's commit, so a deploy of the same commit reuses its images.
tag=$(git -C "$repo" rev-parse --short HEAD 2> /dev/null || date +%Y%m%d%H%M%S)
# With changes not committed, the commit doesn't say what's being built: build afresh.
[[ -z $(git -C "$repo" status --porcelain 2> /dev/null) ]] || tag=$tag-$(date +%H%M%S)

# Every key the compose file wants is in the published .env; each gets a value here.
new_env=$work/env
: > "$new_env"
missing=()
for key in $(grep -o '^[A-Z0-9_]*=' "$work/.env" | tr -d =); do
  case $key in
    *_IMAGE)
      # WEBAPP_IMAGE -> the resource "webapp" -> its project, built below.
      value=tvtracker/$(echo "${key%_IMAGE}" | tr 'A-Z_' 'a-z-'):$tag ;;
    *_PORT)
      # The port .NET's container images listen on.
      value=8080 ;;
    # Written below, from the checked-in one.
    CADDY_BINDMOUNT_0) value=$home/Caddyfile ;;
    FLAGD_BINDMOUNT_0) value=$home/flags ;;
    KEYCLOAK_BINDMOUNT_0) value=$home/keycloak ;;
    BACKUP_BINDMOUNT_0) value=$apphost/Deploy/backup.sh ;;
    BACKUP_BINDMOUNT_1 | OFFSITE_BINDMOUNT_1) value=$home/backups ;;
    OFFSITE_BINDMOUNT_0) value=$apphost/Deploy/offsite.sh ;;
    OFFSITE_BINDMOUNT_2) value=$home/flags ;;
    OFFSITE_BINDMOUNT_3) value=$env_file ;;
    # A mount this script doesn't know is a mistake here, not a setting to leave empty.
    *_BINDMOUNT_*) echo "The compose file has a mount this script doesn't know: $key." >&2; exit 1 ;;
    # Backups: how long they're kept, and the hour (UTC) the daily one is made.
    BACKUP_KEEP_DAYS) value=$(current "$key"); value=${value:-14} ;;
    BACKUP_HOUR_UTC) value=$(current "$key"); value=${value:-3} ;;
    PUBLIC_HOST | OPERATOR_NAME | CONTACT_EMAIL)
      value=$(current "$key")
      [[ -n $value ]] || missing+=("$key") ;;
    API_HOST | STRIPE_* | TMDB_* | TVDB_* | JUSTWATCH_* | GIPHY_* | EMAIL_* | GOOGLE_* | SMTP_* | OFFSITE_* | OPERATOR_ADDRESS | COMPANY_NUMBER | GOVERNING_LAW)
      # Optional: empty until there's an account. Without Stripe's, turn the `payments` flag off (README, Deploying).
      value=$(current "$key") ;;
    *)
      # Every other parameter is a secret this deployment makes up once and keeps.
      value=$(current "$key")
      [[ -n $value ]] || value=$(openssl rand -hex 24) ;;
  esac
  echo "$key=$value" >> "$new_env"
done
if (( ${#missing[@]} )); then
  echo "Set these and run again: ${missing[*]}" >&2
  exit 1
fi
# The import tool's image, for scripts/deploy-import.sh; not part of the compose file.
echo "IMPORT_CLI_IMAGE=tvtracker/import-cli:$tag" >> "$new_env"
# This script's own setting, likewise: the port a reverse proxy of your own sends the site to (empty for none).
proxy_port=$(current PROXY_PORT)
[[ -z $proxy_port || $proxy_port =~ ^[0-9]+$ ]] || { echo "PROXY_PORT is a port number, like 8088." >&2; exit 1; }
echo "PROXY_PORT=$proxy_port" >> "$new_env"
# Keys from an earlier deploy that this publish no longer asks for are kept, in case a later one does again.
grep -v -f <(sed 's/=.*/=/; s/^/^/' "$new_env") "$env_file" >> "$new_env" || true
cp "$new_env" "$env_file"

# The front door's routes: the site, with the gateway under /api, and the gateway's own host name when it has one.
# Written in place, so the file the running Caddy has open is the one that changes; it's told to re-read it below.
cat "$apphost/Deploy/Caddyfile" > "$home/Caddyfile"
[[ -z $(current API_HOST) ]] || cat "$apphost/Deploy/Caddyfile.apihost" >> "$home/Caddyfile"
if [[ -n $proxy_port ]]; then
  # Behind a reverse proxy of your own: it has ports 80 and 443 and the certificate, so this Caddy asks for none
  # (an http:// address), believes the proxy about who the visitor is, and is reachable from this machine only.
  routes=$(sed -E 's#^\{\$(PUBLIC_HOST|API_HOST)\} \{#http://{$\1} {#' "$home/Caddyfile")
  printf '{\n\tservers {\n\t\ttrusted_proxies static private_ranges\n\t}\n}\n\n%s\n' "$routes" > "$home/Caddyfile"
  printf 'services:\n  caddy:\n    ports: !override\n      - "127.0.0.1:%s:80"\n' "$proxy_port" > "$home/docker-compose.override.yaml"
else
  rm -f "$home/docker-compose.override.yaml"
fi

# Feature flags: the checked-in defaults (replaced on every deploy), and the overrides the flags page writes (kept).
cp "$apphost/Flags/flagd.json" "$home/flags/flagd.json"
[[ -f $home/flags/overrides.json ]] || echo '{ "flags": {} }' > "$home/flags/overrides.json"
# Identity.API writes the overrides as its image's unprivileged user.
chmod -R a+rwX "$home/flags"

# Keycloak's realm, from the development one: without its demo and admin users (Identity.API creates the admin, with
# the password in .env) and without its client secrets (Identity.API sets them, from .env), and requiring HTTPS.
jq '.users |= map(select(.serviceAccountClientId)) | (.clients[] |= del(.secret)) | .sslRequired = "external" | .bruteForceProtected = true' \
  "$apphost/Realms/tvtracker-realm.json" > "$home/keycloak/tvtracker-realm.json"
chmod -R a+rX "$home/keycloak"

echo "Building images ($tag)..."
# Builds a project as the image named, unless this commit's is there already.
build_image() {
  local name=$1 csproj=$2 image=$3
  if docker image inspect "$image" > /dev/null 2>&1; then
    echo "  $name: already built"
    return
  fi
  echo "  $name"
  dotnet publish "$csproj" -c Release -t:PublishContainer -p:ContainerRepository="${image%%:*}" -p:ContainerImageTag="${image##*:}" \
    --nologo -v quiet > "$work/build-$name.log" 2>&1 || { tail -30 "$work/build-$name.log" >&2; exit 1; }
}

# One image per service in the compose file: each *_IMAGE key the publish asked for is an AppHost project.
grep -o '^[A-Z0-9_]*_IMAGE=' "$work/.env" | tr -d = | while read -r key; do
  resource=$(echo "${key%_IMAGE}" | tr 'A-Z_' 'a-z-')
  # The project the AppHost runs as this resource: AddProject<Projects.Catalog_API>("catalog-api").
  project=$(grep -o "AddProject<Projects\.[A-Za-z_]*>(\"$resource\")" "$apphost/AppHost.cs" | sed 's/.*Projects\.\([A-Za-z_]*\)>.*/\1/' | tr _ .)
  csproj=$(find "$repo/src" -name "$project.csproj" | head -1)
  [[ -n $csproj ]] || { echo "No project for $resource." >&2; exit 1; }
  build_image "$resource" "$csproj" "$(current "$key")"
done
# And the import tool's, which isn't a service: scripts/deploy-import.sh runs it on demand.
build_image import-cli "$repo/src/Tools/Catalog.ImportCli/Catalog.ImportCli.csproj" "$(current IMPORT_CLI_IMAGE)"

echo "Starting..."
docker compose --project-name tvtracker --project-directory "$home" up -d --remove-orphans
# A Caddy that was already running keeps the routes it started with until told otherwise.
docker compose --project-name tvtracker --project-directory "$home" exec -T caddy caddy reload --config /etc/caddy/Caddyfile \
  > /dev/null 2>&1 || true

# The database has to accept the password every service was just given. It takes POSTGRES_PASSWORD only when its
# volume is first made, so a .env made afresh beside an older volume (a deleted .env, a deploy into another folder)
# leaves Keycloak and every service refused, which is far from obvious in their logs.
for _ in $(seq 30); do
  docker compose --project-name tvtracker --project-directory "$home" exec -T postgres pg_isready -q > /dev/null 2>&1 && break
  sleep 2
done
if ! tried=$(docker compose --project-name tvtracker --project-directory "$home" exec -T postgres \
    sh -c 'PGPASSWORD="$POSTGRES_PASSWORD" psql -h 127.0.0.1 -U postgres -d postgres -Atc "select 1"' 2>&1) \
    && [[ $tried == *"password authentication failed"* ]]; then
  cat >&2 <<EOF

The database doesn't accept the password in $env_file: its volume was made with another one, by an earlier deployment
whose .env this isn't. Keycloak and every service will fail with "password authentication failed" until that's put right.
  - If you still have that deployment's .env, put its POSTGRES_PASSWORD line in $env_file and run this again; or
  - scripts/deploy-db-password.sh $home     sets the database's password to this .env's, keeping the data; or
  - docker compose -p tvtracker --project-directory $home down -v     starts empty, deleting everything it holds.
EOF
  exit 1
fi

# Nobody can sign in until Identity.API has set Keycloak up (its realm's secrets, then the admin account), which
# takes a minute or so after it starts: wait for it, so "Deployed" means it works. A deploy that left Identity.API
# running finds it long done; its log's line saying so may be gone by then, so one up for ten minutes isn't waited on.
identity=$(docker compose --project-name tvtracker --project-directory "$home" ps -q identity-api 2> /dev/null | head -1 || true)
started=$(docker inspect -f '{{.State.StartedAt}}' "$identity" 2> /dev/null || true)
if [[ -n $started ]] && (( $(date +%s) - $(date -d "$started" +%s) < 600 )); then
  echo "Waiting for sign-in to be ready..."
  ready=
  for _ in $(seq 60); do
    # Not grep -q: it stops reading at the first match, which ends docker's side of the pipe with an error, and
    # under pipefail that reads as "not found" once the log is longer than a pipe holds.
    # Since this start only: a restarted container keeps the log of its earlier runs.
    if docker logs --since "$started" "$identity" 2>&1 | grep "is ready in Keycloak" > /dev/null; then
      ready=yes
      break
    fi
    sleep 5
  done
  [[ -n $ready ]] || echo "Sign-in isn't ready after five minutes: docker compose -p tvtracker logs identity-api keycloak" >&2
fi

echo
echo "Deployed to $home. The site: https://$(current PUBLIC_HOST)"
[[ -z $proxy_port ]] || echo "Behind your reverse proxy: have it send $(current PUBLIC_HOST) to http://127.0.0.1:$proxy_port, keeping the Host header."
gateway=https://$(current PUBLIC_HOST)/api
[[ -z $(current API_HOST) ]] || gateway="$gateway and https://$(current API_HOST)"
echo "The mobile gateway: $gateway"
echo "The admin signs in as 'admin' with this deployment's own password: grep APP_ADMIN_PASSWORD $env_file"
echo "The development logins (demo / demo, admin / admin) don't exist here; anyone else registers from the sign-in page."
echo "To fill the catalog in bulk: scripts/deploy-import.sh top 500 (README, Deploying)."
if [[ -n $(current OFFSITE_BUCKET) ]]; then
  echo "Backups: daily, in $home/backups, and copied encrypted to $(current OFFSITE_BUCKET). Keep OFFSITE_PASSPHRASE somewhere else too."
else
  echo "Backups: daily, in $home/backups ONLY. Set OFFSITE_* in $env_file to copy them off this machine (README, Deploying)."
fi
echo "Logs, traces and metrics: ssh -L 18888:localhost:18888 <this machine>, then http://localhost:18888"
