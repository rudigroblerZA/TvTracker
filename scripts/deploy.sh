#!/usr/bin/env bash
# Deploys the app to this machine with Docker Compose (README, Deploying).
#
#   scripts/deploy.sh [directory]        # default: ~/tvtracker
#
# It publishes the AppHost to a compose file, builds each service's image, and starts everything. The directory keeps
# what must survive between deploys: .env (settings and generated secrets), the feature-flag overrides, and the
# compose file. Run it again to deploy a newer checkout; values already in .env are kept.
#
# The first run needs four things, as environment variables (later runs read them from .env):
#   PUBLIC_HOST     the site's host name, e.g. tv.example.com (its DNS must point at this machine)
#   API_HOST        the mobile gateway's host name, e.g. api.tv.example.com (same)
#   OPERATOR_NAME   who runs the site, shown in the privacy policy and terms
#   CONTACT_EMAIL   where people can write to
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
    CADDY_BINDMOUNT_0) value=$apphost/Deploy/Caddyfile ;;
    FLAGD_BINDMOUNT_0) value=$home/flags ;;
    KEYCLOAK_BINDMOUNT_0) value=$home/keycloak ;;
    BACKUP_BINDMOUNT_0) value=$apphost/Deploy/backup.sh ;;
    BACKUP_BINDMOUNT_1) value=$home/backups ;;
    # Backups: how long they're kept, and the hour (UTC) the daily one is made.
    BACKUP_KEEP_DAYS) value=$(current "$key"); value=${value:-14} ;;
    BACKUP_HOUR_UTC) value=$(current "$key"); value=${value:-3} ;;
    PUBLIC_HOST | API_HOST | OPERATOR_NAME | CONTACT_EMAIL)
      value=$(current "$key")
      [[ -n $value ]] || missing+=("$key") ;;
    STRIPE_* | TMDB_* | TVDB_* | EMAIL_* | GOOGLE_* | OPERATOR_ADDRESS | COMPANY_NUMBER | GOVERNING_LAW)
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
# Keys from an earlier deploy that this publish no longer asks for are kept, in case a later one does again.
grep -v -f <(sed 's/=.*/=/; s/^/^/' "$new_env") "$env_file" >> "$new_env" || true
cp "$new_env" "$env_file"

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
grep -o '^[A-Z0-9_]*_IMAGE=.*' "$env_file" | while IFS== read -r key image; do
  resource=$(echo "${key%_IMAGE}" | tr 'A-Z_' 'a-z-')
  if docker image inspect "$image" > /dev/null 2>&1; then
    echo "  $resource: already built"
    continue
  fi
  # The project the AppHost runs as this resource: AddProject<Projects.Catalog_API>("catalog-api").
  project=$(grep -o "AddProject<Projects\.[A-Za-z_]*>(\"$resource\")" "$apphost/AppHost.cs" | sed 's/.*Projects\.\([A-Za-z_]*\)>.*/\1/' | tr _ .)
  csproj=$(find "$repo/src" -name "$project.csproj" | head -1)
  [[ -n $csproj ]] || { echo "No project for $resource." >&2; exit 1; }
  echo "  $resource"
  dotnet publish "$csproj" -c Release -t:PublishContainer -p:ContainerRepository="${image%%:*}" -p:ContainerImageTag="$tag" \
    --nologo -v quiet > "$work/build-$resource.log" 2>&1 || { tail -30 "$work/build-$resource.log" >&2; exit 1; }
done

echo "Starting..."
docker compose --project-name tvtracker --project-directory "$home" up -d --remove-orphans

echo
echo "Deployed to $home. The site: https://$(current PUBLIC_HOST)"
echo "The admin signs in as 'admin' with APP_ADMIN_PASSWORD from $env_file."
echo "Backups: daily, in $home/backups. Copy that folder, .env and flags/ off this machine."
echo "Logs, traces and metrics: ssh -L 18888:localhost:18888 <this machine>, then http://localhost:18888"
