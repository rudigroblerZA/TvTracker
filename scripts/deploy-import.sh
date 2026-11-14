#!/usr/bin/env bash
# Runs the import tool (tvtracker-import) against a deployment (README, Deploying: Filling the catalog).
#
#   scripts/deploy-import.sh [directory] <tvtracker-import arguments>     # directory defaults to ~/tvtracker
#
#   scripts/deploy-import.sh top 500                  # the provider's 500 most popular shows
#   scripts/deploy-import.sh titles - < shows.txt     # one title a line, read from standard input
#   scripts/deploy-import.sh status                   # follow the running import, or show the last one
#   scripts/deploy-import.sh wikidata run             # the Wikidata jobs, now
#
# The sync worker has no address outside the stack, so the tool runs as a container on the stack's network, signed
# in as the internal client with the secret from the deployment's .env. Ctrl+C stops following, not the import.
set -euo pipefail

home=$HOME/tvtracker
if [[ $# -ge 1 && -f $1/docker-compose.yaml ]]; then
  home=$1
  shift
fi
env_file=$home/.env
[[ -f $home/docker-compose.yaml && -f $env_file ]] || { echo "No deployment in $home." >&2; exit 1; }
setting() { grep -m1 "^$1=" "$env_file" | cut -d= -f2-; }

image=$(setting IMPORT_CLI_IMAGE)
[[ -n $image ]] || { echo "No IMPORT_CLI_IMAGE in $env_file: run scripts/deploy.sh once more to build it." >&2; exit 1; }

# A terminal when there is one, so progress redraws and Ctrl+C reaches the tool; plain input otherwise (titles -).
tty=()
[[ -t 0 && -t 1 ]] && tty=(-t)

exec docker run --rm -i "${tty[@]}" --network tvtracker_default \
  -e TVTRACKER_SYNC_URL="http://catalog-sync:$(setting CATALOG_SYNC_PORT)" \
  -e TVTRACKER_RESEARCH_URL="http://research-api:$(setting RESEARCH_API_PORT)" \
  -e TVTRACKER_KEYCLOAK_URL=http://keycloak:8080 \
  -e TVTRACKER_INTERNAL_SECRET="$(setting INTERNAL_CLIENT_SECRET)" \
  "$image" "$@"
