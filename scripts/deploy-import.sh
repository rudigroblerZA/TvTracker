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
# A key that isn't there is empty, not an error: the checks below say what's missing.
setting() { grep -m1 "^$1=" "$env_file" | cut -d= -f2- || true; }

image=$(setting IMPORT_CLI_IMAGE)
[[ -n $image ]] || { echo "No IMPORT_CLI_IMAGE in $env_file: run scripts/deploy.sh once more to build it." >&2; exit 1; }

# A terminal when there is one, so progress redraws and Ctrl+C reaches the tool; plain input otherwise (titles -).
tty=()
[[ -t 0 && -t 1 ]] && tty=(-t)

# The stack's network, asked of the running sync worker rather than assumed: its name is the compose project's plus
# whatever the publisher called the network.
worker=$(docker compose --project-name tvtracker --project-directory "$home" ps -q catalog-sync 2> /dev/null | head -1)
[[ -n $worker ]] || { echo "The sync worker isn't running in $home: start the stack with scripts/deploy.sh." >&2; exit 1; }
network=$(docker inspect -f '{{range $name, $_ := .NetworkSettings.Networks}}{{$name}}{{"\n"}}{{end}}' "$worker" | head -1)
[[ -n $network ]] || { echo "Couldn't find the stack's network." >&2; exit 1; }

# The secret goes to docker through this script's environment, named but not spelled out, so it isn't in the
# process list for the length of an import.
export TVTRACKER_INTERNAL_SECRET
TVTRACKER_INTERNAL_SECRET=$(setting INTERNAL_CLIENT_SECRET)

exec docker run --rm -i "${tty[@]}" --network "$network" \
  -e TVTRACKER_SYNC_URL="http://catalog-sync:$(setting CATALOG_SYNC_PORT)" \
  -e TVTRACKER_RESEARCH_URL="http://research-api:$(setting RESEARCH_API_PORT)" \
  -e TVTRACKER_KEYCLOAK_URL=http://keycloak:8080 \
  -e TVTRACKER_INTERNAL_SECRET \
  "$image" "$@"
