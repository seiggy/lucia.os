#!/bin/sh
set -eu
umask 077
export TMPDIR=/bao/data

ready() {
  [ -s /certificates/ready.sha256 ] &&
    (cd /certificates && sha256sum -c ready.sha256 >/dev/null 2>&1)
}

# Provisioning publishes the leaf only after the independent CA has started.
while ! ready; do sleep 2; done
marker=$(cat /certificates/ready.sha256)
bao server -config=/bao/config/server.hcl &
server=$!
watcher=
stop() {
  [ -z "$watcher" ] || kill "$watcher" 2>/dev/null || true
  kill -TERM "$server" 2>/dev/null || true
}
trap stop TERM INT
(
  while kill -0 "$server" 2>/dev/null; do
    sleep 5
    if ready; then
      next=$(cat /certificates/ready.sha256)
      if [ "$next" != "$marker" ]; then
        kill -HUP "$server"
        marker=$next
      fi
    fi
  done
) &
watcher=$!
result=0
wait "$server" || result=$?
stop
wait "$server" 2>/dev/null || true
wait "$watcher" 2>/dev/null || true
exit "$result"
