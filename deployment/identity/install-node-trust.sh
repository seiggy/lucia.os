#!/bin/sh
# Run explicitly in a Debian-family node image or on an approved managed node.
set -eu

if [ "$#" -ne 2 ]; then
  echo "Usage: sh install-node-trust.sh PUBLIC_CA_FILE EXPECTED_SHA256_FINGERPRINT" >&2
  exit 2
fi
certificate=$1
for command in openssl update-ca-certificates; do
  if ! command -v "$command" >/dev/null; then
    echo "Required command is missing: $command. Install openssl and ca-certificates first." >&2
    exit 1
  fi
done
expected=$(printf '%s' "$2" | tr -d ':' | tr '[:upper:]' '[:lower:]')
if [ "${#expected}" -ne 64 ] || printf '%s' "$expected" | grep -q '[^0-9a-f]'; then
  echo "Expected fingerprint must be the certificate's SHA-256 fingerprint, not its PEM file hash." >&2
  exit 2
fi
fingerprint() {
  openssl x509 -in "$1" -noout -fingerprint -sha256 | cut -d= -f2 | tr -d ':' | tr '[:upper:]' '[:lower:]'
}
if [ "$(fingerprint "$certificate")" != "$expected" ]; then
  echo "Public CA fingerprint mismatch; trust was not changed." >&2
  exit 1
fi
openssl x509 -in "$certificate" -noout -ext basicConstraints | grep -q 'CA:TRUE'
openssl verify -CAfile "$certificate" "$certificate"
if [ "$(id -u)" -ne 0 ]; then
  echo "Run as root only after approving installation of this CA." >&2
  exit 1
fi
destination=/usr/local/share/ca-certificates/lucia-root-ca.crt
if [ -e "$destination" ] && [ "$(fingerprint "$destination")" != "$expected" ]; then
  echo "A different Lucia CA is already installed; use an explicit trust migration." >&2
  exit 1
fi
install -m 644 "$certificate" "$destination"
update-ca-certificates
