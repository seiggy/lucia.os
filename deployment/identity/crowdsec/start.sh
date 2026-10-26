#!/bin/bash
# Lucia's CrowdSec engine, beside the identity gateway. Lucia's settings (config.yaml.local, profiles, log sources) come
# read-only from /lucia/config and the gateway's bouncer key from its secret file. A background loop keeps the engine in step
# with Lucia: the lucia-safe-harbor allowlist follows the gateway's list, and an enroll key placed in /lucia/enroll enrolls
# the engine in the CrowdSec Console once. Then the image's own start script registers with the Central API, installs the hub
# collections and runs CrowdSec. Nothing here can stop the engine from starting.
set -euo pipefail

install -d -m 0755 /etc/crowdsec
install -m 0644 /lucia/config/config.yaml.local /etc/crowdsec/config.yaml.local

key=$(tr -d '\r\n' < /run/secrets/crowdsec-bouncer-key)
# The bouncer's name carries a digest of its key, so a replaced key registers afresh instead of meeting a stale registration.
export "BOUNCER_KEY_lucia_gateway_$(printf '%s' "$key" | sha256sum | cut -c1-12)=$key"
unset key

harbor_seen=""
harbor_synced=0
harbor() {
  local file=/lucia/security/safe-harbor.txt list=lucia-safe-harbor wanted current add remove seen now
  [ -r "$file" ] || return 0
  # Lucia rewrites the file whenever the list changes; between changes, the allowlist is rechecked every ten minutes.
  seen=$(sha256sum "$file")
  now=$(date +%s)
  if [ "$seen" = "$harbor_seen" ] && [ $((now - harbor_synced)) -lt 600 ]; then return 0; fi
  wanted=$(grep -E '^[0-9A-Fa-f:.]+(/[0-9]{1,3})?$' "$file" | sort -u || true)
  if ! cscli allowlists inspect "$list" -o json >/dev/null 2>&1; then
    cscli allowlists create "$list" -d "Lucia safe harbor: never banned, never checked by AppSec. Managed by Lucia." >/dev/null || return 1
  fi
  current=$(cscli allowlists inspect "$list" -o json | yq -r '.items[]?.value' | sort -u) || return 1
  add=$(comm -23 <(printf '%s\n' "$wanted") <(printf '%s\n' "$current") | grep . || true)
  remove=$(comm -13 <(printf '%s\n' "$wanted") <(printf '%s\n' "$current") | grep . || true)
  if [ -n "$add" ]; then
    # shellcheck disable=SC2086
    cscli allowlists add "$list" $add -d "Lucia safe harbor" >/dev/null || return 1
    echo "lucia: the safe harbor now allows" $add
  fi
  if [ -n "$remove" ]; then
    # shellcheck disable=SC2086
    cscli allowlists remove "$list" $remove >/dev/null || return 1
    echo "lucia: the safe harbor no longer allows" $remove
  fi
  harbor_seen=$seen
  harbor_synced=$now
}

enroll_key=""
enroll_tried=0
enroll() {
  local file=/lucia/enroll/enroll-key key marker now
  [ -s "$file" ] || return 0
  key=$(tr -d '[:space:]' < "$file")
  marker=/var/lib/crowdsec/data/.lucia-enrolled-$(printf '%s' "$key" | sha256sum | cut -c1-16)
  [ -e "$marker" ] && return 0
  now=$(date +%s)
  # A new key is tried at once; a key that failed is retried every ten minutes.
  if [ "$key" = "$enroll_key" ] && [ $((now - enroll_tried)) -lt 600 ]; then return 0; fi
  enroll_key=$key
  enroll_tried=$now
  if cscli console enroll --name "${LUCIA_ENGINE_NAME:-lucia}" --tags lucia "$key" >/dev/null 2>&1; then
    touch "$marker"
    echo "lucia: enrollment sent to the CrowdSec Console; accept this engine there"
  else
    echo "lucia: CrowdSec Console enrollment failed; retrying in ten minutes" >&2
  fi
}

(
  set +e
  until cscli lapi status >/dev/null 2>&1; do sleep 5; done
  while :; do
    harbor || echo "lucia: the safe-harbor allowlist could not be updated; retrying" >&2
    enroll
    sleep 20
  done
) &

exec /bin/bash /docker_start.sh "$@"
