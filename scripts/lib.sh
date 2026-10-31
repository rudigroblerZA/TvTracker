# Shared by backup.sh and restore.sh: where the AppHost's Postgres and Keycloak live.

KEYCLOAK_VOLUME=tvtracker-keycloak-data
KEYCLOAK_REALM=tvtracker

# The AppHost names containers <resource>-<hash>; both are persistent, so they outlive an AppHost run.
postgres_container() { container_named postgres; }
keycloak_container() { container_named keycloak; }

container_named() {
  local name
  name=$(docker ps -a --filter "name=^$1-" --format '{{.Names}}' | head -1)
  if [[ -z $name ]]; then
    echo "No $1 container: start the AppHost once first." >&2
    exit 1
  fi
  echo "$name"
}

require_running() {
  if [[ $(docker inspect -f '{{.State.Running}}' "$1") != true ]]; then
    docker start "$1" > /dev/null
    for _ in $(seq 1 30); do
      docker exec "$1" pg_isready -q 2> /dev/null && return
      sleep 1
    done
    echo "$1 didn't become ready." >&2
    exit 1
  fi
}

# The AppHost's own process (its built binary), not a shell or editor whose command line mentions it.
apphost_running() { pgrep -f '/bin/[^ ]+/TvTracker\.AppHost( |$)' > /dev/null; }
