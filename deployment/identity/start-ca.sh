#!/bin/sh
set -eu
umask 077
export STEPPATH=/home/step

if [ ! -f "$STEPPATH/config/ca.json" ]; then
  if [ -e "$STEPPATH/certs/root_ca.crt" ] || [ -e "$STEPPATH/secrets/root_ca_key" ]; then
    echo "Partial CA initialization found. Restore or repair it; refusing to replace trust keys." >&2
    exit 1
  fi
  step ca init \
    --deployment-type standalone \
    --name "$LUCIA_CA_NAME" \
    --dns "localhost,identity-ca,$LUCIA_PUBLIC_HOST" \
    --address ":9000" \
    --provisioner "lucia-installer" \
    --password-file /run/secrets/ca-password
fi

exec step-ca "$STEPPATH/config/ca.json" --password-file /run/secrets/ca-password
