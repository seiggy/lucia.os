#!/bin/sh
set -eu
umask 077

if [ "${1:-}" = "--publish" ]; then
  exec 9>/certificates/.publish.lock
  flock -x 9
  step certificate verify /certificates/renewing.crt --roots /trust/lucia-root-ca.crt
  mv /certificates/renewing.crt /certificates/identity.crt
  cd /certificates
  sha256sum identity.crt identity.key > ready.sha256.new
  mv ready.sha256.new ready.sha256
  touch /gateway/tls.yml
  exit 0
fi

while [ ! -s /trust/lucia-root-ca.crt ] || ! (cd /certificates && sha256sum -c ready.sha256 >/dev/null 2>&1); do
  sleep 5
done

exec step ca renew /certificates/identity.crt /certificates/identity.key \
  --ca-url https://identity-ca:9000 \
  --root /trust/lucia-root-ca.crt \
  --out /certificates/renewing.crt \
  --daemon --force \
  --exec "/bin/sh /config/renew-certificate.sh --publish"
